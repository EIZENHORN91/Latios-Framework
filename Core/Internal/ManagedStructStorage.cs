﻿using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine.Assertions;

namespace Latios
{
#if UNITY_EDITOR
    internal static class InjectGenericComponentTypes
    {
        [UnityEditor.InitializeOnLoadMethod]
        public static void DoInjection()
        {
            TypeManager.Initialize();
            BootstrapTools.PopulateTypeManagerWithGenerics(typeof(ManagedComponentCleanupTag<>),    typeof(IManagedStructComponent));
            BootstrapTools.PopulateTypeManagerWithGenerics(typeof(CollectionComponentCleanupTag<>), typeof(ICollectionComponent));
        }
    }
#endif

    internal class ManagedStructComponentStorage : IDisposable
    {
        struct Key : IEquatable<Key>
        {
            public long   typeHash;
            public Entity entity;

            public bool Equals(Key other)
            {
                return typeHash.Equals(other.typeHash) && entity.Equals(other.entity);
            }

            public override unsafe int GetHashCode()
            {
                fixed (void* ptr = &this)
                {
                    return ((Hash128*)ptr)->GetHashCode();
                }
            }
        }

        struct RegisteredType
        {
            public ComponentType associatedType;
            public int           typedStorageIndex;
        }

        NativeHashMap<Key, int2>            m_twoLevelLookup;
        NativeHashMap<long, RegisteredType> m_registeredTypeLookup;
        List<TypedManagedStructStorageBase> m_typedStorages;

        public ManagedStructComponentStorage()
        {
            m_twoLevelLookup       = new NativeHashMap<Key, int2>(128, Allocator.Persistent);
            m_registeredTypeLookup = new NativeHashMap<long, RegisteredType>(32, Allocator.Persistent);
            m_typedStorages        = new List<TypedManagedStructStorageBase>(32);
        }

        public void Dispose()
        {
            m_twoLevelLookup.Dispose();
            m_registeredTypeLookup.Dispose();
        }

        public ComponentType GetAssociatedType<T>() where T : struct, IManagedStructComponent
        {
            var typeHash = BurstRuntime.GetHashCode64<T>();
            if (!m_registeredTypeLookup.TryGetValue(typeHash, out var element))
            {
                element = new RegisteredType
                {
                    associatedType    = new T().AssociatedComponentType,
                    typedStorageIndex = m_typedStorages.Count
                };

                m_typedStorages.Add(new TypedManagedStructStorage<T>() {
                    typeIndex = element.typedStorageIndex
                });
                m_registeredTypeLookup.Add(typeHash, element);
            }
            return element.associatedType;
        }

        public bool AddComponent<T>(Entity entity, T value) where T : struct, IManagedStructComponent
        {
            var tmss = GetTypedManagedStructStorage<T>(entity, out int index);
            if (index >= 0)
            {
                tmss.storage[index] = value;
                return false;
            }

            if (!tmss.freeStack.TryPop(out index))
            {
                index = tmss.storage.Count;
                tmss.storage.Add(value);
            }
            else
            {
                tmss.storage[index] = value;
            }

            m_twoLevelLookup.Add(new Key { entity = entity, typeHash = BurstRuntime.GetHashCode64<T>() }, new int2(tmss.typeIndex, index));
            return true;
        }

        public T GetComponent<T>(Entity entity) where T : struct, IManagedStructComponent
        {
            var tmss = GetTypedManagedStructStorage<T>(entity, out int index);
            if (index < 0)
            {
                throw new InvalidOperationException($"Entity {entity} does not have a component of type: {typeof(T).Name}");
            }

            return tmss.storage[index];
        }

        public T GetOrAddDefaultComponent<T>(Entity entity) where T : struct, IManagedStructComponent
        {
            var tmss = GetTypedManagedStructStorage<T>(entity, out int index);
            if (index < 0)
            {
                if (!tmss.freeStack.TryPop(out index))
                {
                    index = tmss.storage.Count;
                    tmss.storage.Add(default);
                }
                else
                {
                    tmss.storage[index] = default;
                }

                m_twoLevelLookup.Add(new Key { entity = entity, typeHash = BurstRuntime.GetHashCode64<T>() }, new int2(tmss.typeIndex, index));
            }
            return tmss.storage[index];
        }

        public bool HasComponent<T>(Entity entity) where T : struct, IManagedStructComponent
        {
            GetTypedManagedStructStorage<T>(entity, out int index);
            return index >= 0;
        }

        public bool RemoveComponent<T>(Entity entity) where T : struct, IManagedStructComponent
        {
            var tmss = GetTypedManagedStructStorage<T>(entity, out int index);

            if (index < 0)
                return false;
            tmss.freeStack.Push(index);
            var key = new Key { typeHash = BurstRuntime.GetHashCode64<T>(), entity = entity };
            m_twoLevelLookup.Remove(key);
            return true;
        }

        public void SetComponent<T>(Entity entity, T value) where T : struct, IManagedStructComponent
        {
            var tmss = GetTypedManagedStructStorage<T>(entity, out int index);
            if (index < 0)
            {
                throw new InvalidOperationException($"Entity {entity} does not have a component of type: {typeof(T).Name}");
            }
            tmss.storage[index] = value;
        }

        public void CopyComponent(Entity src, Entity dst, Type type)
        {
            if (!m_registeredTypeLookup.TryGetValue(BurstRuntime.GetHashCode64(type), out var registeredType))
            {
                throw new InvalidOperationException($"Source Entity {src} does not have a component of type: {type.Name}");
            }
            m_typedStorages[registeredType.typedStorageIndex].CopyComponent(this, src, dst);
        }

        private TypedManagedStructStorage<T> GetTypedManagedStructStorage<T>(Entity entity, out int indexInTypedStorage) where T : struct, IManagedStructComponent
        {
            var key = new Key { typeHash = BurstRuntime.GetHashCode64<T>(), entity = entity };
            if (m_twoLevelLookup.TryGetValue(key, out var indices))
            {
                indexInTypedStorage = indices.y;
                return m_typedStorages[indices.x] as TypedManagedStructStorage<T>;
            }

            if (!m_registeredTypeLookup.TryGetValue(key.typeHash, out var element))
            {
                element = new RegisteredType
                {
                    associatedType    = new T().AssociatedComponentType,
                    typedStorageIndex = m_typedStorages.Count
                };

                m_typedStorages.Add(new TypedManagedStructStorage<T>() {
                    typeIndex = element.typedStorageIndex
                });
                m_registeredTypeLookup.Add(key.typeHash, element);
            }

            indexInTypedStorage = -1;
            return m_typedStorages[element.typedStorageIndex] as TypedManagedStructStorage<T>;
        }

        private abstract class TypedManagedStructStorageBase
        {
            public abstract void CopyComponent(ManagedStructComponentStorage mscs, Entity src, Entity dst);
        }

        private class TypedManagedStructStorage<T> : TypedManagedStructStorageBase where T : struct, IManagedStructComponent
        {
            public List<T>    storage   = new List<T>();
            public Stack<int> freeStack = new Stack<int>();
            public int        typeIndex;

            public override void CopyComponent(ManagedStructComponentStorage mscs, Entity src, Entity dst)
            {
                var srcValue = mscs.GetComponent<T>(src);
                mscs.AddComponent(dst, srcValue);
            }
        }
    }
}

