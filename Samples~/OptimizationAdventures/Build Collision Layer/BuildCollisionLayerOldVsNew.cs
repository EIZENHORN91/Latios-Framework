﻿using System.Diagnostics;
using Latios;
using Latios.Psyshock;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;

namespace OptimizationAdventures
{
    internal static class BuildCollisionLayerOldVsNew
    {
        public struct LayerChunkTypeGroup
        {
            [ReadOnly] public ComponentTypeHandle<Collider>     collider;
            [ReadOnly] public ComponentTypeHandle<Translation>  translation;
            [ReadOnly] public ComponentTypeHandle<Rotation>     rotation;
            [ReadOnly] public ComponentTypeHandle<PhysicsScale> scale;
            [ReadOnly] public ComponentTypeHandle<Parent>       parent;
            [ReadOnly] public ComponentTypeHandle<LocalToWorld> localToWorld;
            [ReadOnly] public EntityTypeHandle                  entity;
        }

        public static LayerChunkTypeGroup BuildLayerChunkTypeGroup(ComponentSystemBase system)
        {
            LayerChunkTypeGroup result = new LayerChunkTypeGroup
            {
                collider     = system.GetComponentTypeHandle<Collider>(true),
                translation  = system.GetComponentTypeHandle<Translation>(true),
                rotation     = system.GetComponentTypeHandle<Rotation>(true),
                scale        = system.GetComponentTypeHandle<PhysicsScale>(true),
                parent       = system.GetComponentTypeHandle<Parent>(true),
                localToWorld = system.GetComponentTypeHandle<LocalToWorld>(true),
                entity       = system.GetEntityTypeHandle()
            };
            return result;
        }

        public struct ColliderAoSData
        {
            public Collider       collider;
            public RigidTransform rigidTransform;
            public Aabb           aabb;
            public Entity         entity;
        }

        #region Jobs
        //Parallel
        //Calculate RigidTransform, AABB, and target bucket. Write the targetBucket as the layerIndex
        [BurstCompile]
        public struct Part1FromQueryJob : IJobChunk
        {
            [ReadOnly] public TestCollisionLayer                                               layer;
            [NoAlias, NativeDisableParallelForRestriction] public NativeArray<int>             layerIndices;
            [NoAlias, NativeDisableParallelForRestriction] public NativeArray<ColliderAoSData> colliderAoS;
            [NoAlias, NativeDisableParallelForRestriction] public NativeArray<float>           xmins;
            [ReadOnly] public LayerChunkTypeGroup                                              typeGroup;
            [ReadOnly, DeallocateOnJobCompletion] public NativeArray<int>                      firstEntityInChunkIndices;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                var firstEntityIndex = firstEntityInChunkIndices[unfilteredChunkIndex];

                bool ltw = chunk.Has(typeGroup.localToWorld);
                bool p   = chunk.Has(typeGroup.parent);
                bool t   = chunk.Has(typeGroup.translation);
                bool r   = chunk.Has(typeGroup.rotation);
                bool s   = chunk.Has(typeGroup.scale);

                int mask  = math.select(0, 0x10, ltw);
                mask     += math.select(0, 0x8, p);
                mask     += math.select(0, 0x4, t);
                mask     += math.select(0, 0x2, r);
                mask     += math.select(0, 0x1, s);

                switch (mask)
                {
                    case 0x0: ProcessNoTransform(in chunk, useEnabledMask, chunkEnabledMask, firstEntityIndex); break;
                    case 0x1: ProcessScale(in chunk, useEnabledMask, chunkEnabledMask, firstEntityIndex); break;
                    case 0x2: ProcessRotation(in chunk, useEnabledMask, chunkEnabledMask, firstEntityIndex); break;
                    case 0x3: ProcessRotationScale(in chunk, useEnabledMask, chunkEnabledMask, firstEntityIndex); break;
                    case 0x4: ProcessTranslation(in chunk, useEnabledMask, chunkEnabledMask, firstEntityIndex); break;
                    case 0x5: ProcessTranslationScale(in chunk, useEnabledMask, chunkEnabledMask, firstEntityIndex); break;
                    case 0x6: ProcessTranslationRotation(in chunk, useEnabledMask, chunkEnabledMask, firstEntityIndex); break;
                    case 0x7: ProcessTranslationRotationScale(in chunk, useEnabledMask, chunkEnabledMask, firstEntityIndex); break;

                    case 0x8: ErrorCase(); break;
                    case 0x9: ErrorCase(); break;
                    case 0xa: ErrorCase(); break;
                    case 0xb: ErrorCase(); break;
                    case 0xc: ErrorCase(); break;
                    case 0xd: ErrorCase(); break;
                    case 0xe: ErrorCase(); break;
                    case 0xf: ErrorCase(); break;

                    case 0x10: ProcessLocalToWorld(in chunk, useEnabledMask, chunkEnabledMask, firstEntityIndex); break;
                    case 0x11: ProcessScale(in chunk, useEnabledMask, chunkEnabledMask, firstEntityIndex); break;
                    case 0x12: ProcessRotation(in chunk, useEnabledMask, chunkEnabledMask, firstEntityIndex); break;
                    case 0x13: ProcessRotationScale(in chunk, useEnabledMask, chunkEnabledMask, firstEntityIndex); break;
                    case 0x14: ProcessTranslation(in chunk, useEnabledMask, chunkEnabledMask, firstEntityIndex); break;
                    case 0x15: ProcessTranslationScale(in chunk, useEnabledMask, chunkEnabledMask, firstEntityIndex); break;
                    case 0x16: ProcessTranslationRotation(in chunk, useEnabledMask, chunkEnabledMask, firstEntityIndex); break;
                    case 0x17: ProcessTranslationRotationScale(in chunk, useEnabledMask, chunkEnabledMask, firstEntityIndex); break;

                    case 0x18: ProcessParent(in chunk, useEnabledMask, chunkEnabledMask, firstEntityIndex); break;
                    case 0x19: ProcessParentScale(in chunk, useEnabledMask, chunkEnabledMask, firstEntityIndex); break;
                    case 0x1a: ProcessParent(in chunk, useEnabledMask, chunkEnabledMask, firstEntityIndex); break;
                    case 0x1b: ProcessParentScale(in chunk, useEnabledMask, chunkEnabledMask, firstEntityIndex); break;
                    case 0x1c: ProcessParent(in chunk, useEnabledMask, chunkEnabledMask, firstEntityIndex); break;
                    case 0x1d: ProcessParentScale(in chunk, useEnabledMask, chunkEnabledMask, firstEntityIndex); break;
                    case 0x1e: ProcessParent(in chunk, useEnabledMask, chunkEnabledMask, firstEntityIndex); break;
                    case 0x1f: ProcessParentScale(in chunk, useEnabledMask, chunkEnabledMask, firstEntityIndex); break;

                    default: ErrorCase(); break;
                }
            }

            [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
            private void ErrorCase()
            {
                throw new System.InvalidOperationException("BuildCollisionLayer.Part1FromQueryJob received an invalid EntityQuery");
            }

            private void ProcessNoTransform(in ArchetypeChunk chunk, bool useEnabledMask, in v128 chunkEnabledMask, int firstEntityIndex)
            {
                var chunkEntities  = chunk.GetNativeArray(typeGroup.entity);
                var chunkColliders = chunk.GetNativeArray(typeGroup.collider);
                var enumerator     = new ChunkEntityWithIndexEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count, firstEntityIndex);
                while (enumerator.NextEntityIndex(out var i, out var entityInQueryIndex))
                {
                    ProcessEntity(entityInQueryIndex, chunkEntities[i], chunkColliders[i], RigidTransform.identity);
                }
            }

            private void ProcessScale(in ArchetypeChunk chunk, bool useEnabledMask, in v128 chunkEnabledMask, int firstEntityIndex)
            {
                var chunkEntities  = chunk.GetNativeArray(typeGroup.entity);
                var chunkColliders = chunk.GetNativeArray(typeGroup.collider);
                var chunkScales    = chunk.GetNativeArray(typeGroup.scale);
                var enumerator     = new ChunkEntityWithIndexEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count, firstEntityIndex);
                while (enumerator.NextEntityIndex(out var i, out var entityInQueryIndex))
                {
                    var collider = Physics.ScaleCollider(chunkColliders[i], chunkScales[i]);
                    ProcessEntity(entityInQueryIndex, chunkEntities[i], in collider, RigidTransform.identity);
                }
            }

            private void ProcessRotation(in ArchetypeChunk chunk, bool useEnabledMask, in v128 chunkEnabledMask, int firstEntityIndex)
            {
                var chunkEntities  = chunk.GetNativeArray(typeGroup.entity);
                var chunkColliders = chunk.GetNativeArray(typeGroup.collider);
                var chunkRotations = chunk.GetNativeArray(typeGroup.rotation);
                var enumerator     = new ChunkEntityWithIndexEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count, firstEntityIndex);
                while (enumerator.NextEntityIndex(out var i, out var entityInQueryIndex))
                {
                    var rigidTransform = new RigidTransform(chunkRotations[i].Value, float3.zero);
                    ProcessEntity(entityInQueryIndex, chunkEntities[i], chunkColliders[i], in rigidTransform);
                }
            }

            private void ProcessRotationScale(in ArchetypeChunk chunk, bool useEnabledMask, in v128 chunkEnabledMask, int firstEntityIndex)
            {
                var chunkEntities  = chunk.GetNativeArray(typeGroup.entity);
                var chunkColliders = chunk.GetNativeArray(typeGroup.collider);
                var chunkRotations = chunk.GetNativeArray(typeGroup.rotation);
                var chunkScales    = chunk.GetNativeArray(typeGroup.scale);
                var enumerator     = new ChunkEntityWithIndexEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count, firstEntityIndex);
                while (enumerator.NextEntityIndex(out var i, out var entityInQueryIndex))
                {
                    var collider       = Physics.ScaleCollider(chunkColliders[i], chunkScales[i]);
                    var rigidTransform = new RigidTransform(chunkRotations[i].Value, float3.zero);
                    ProcessEntity(entityInQueryIndex, chunkEntities[i], in collider, in rigidTransform);
                }
            }

            private void ProcessTranslation(in ArchetypeChunk chunk, bool useEnabledMask, in v128 chunkEnabledMask, int firstEntityIndex)
            {
                var chunkEntities     = chunk.GetNativeArray(typeGroup.entity);
                var chunkColliders    = chunk.GetNativeArray(typeGroup.collider);
                var chunkTranslations = chunk.GetNativeArray(typeGroup.translation);
                var enumerator        = new ChunkEntityWithIndexEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count, firstEntityIndex);
                while (enumerator.NextEntityIndex(out var i, out var entityInQueryIndex))
                {
                    var rigidTransform = new RigidTransform(quaternion.identity, chunkTranslations[i].Value);
                    ProcessEntity(entityInQueryIndex, chunkEntities[i], chunkColliders[i], in rigidTransform);
                }
            }

            private void ProcessTranslationScale(in ArchetypeChunk chunk, bool useEnabledMask, in v128 chunkEnabledMask, int firstEntityIndex)
            {
                var chunkEntities     = chunk.GetNativeArray(typeGroup.entity);
                var chunkColliders    = chunk.GetNativeArray(typeGroup.collider);
                var chunkTranslations = chunk.GetNativeArray(typeGroup.translation);
                var chunkScales       = chunk.GetNativeArray(typeGroup.scale);
                var enumerator        = new ChunkEntityWithIndexEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count, firstEntityIndex);
                while (enumerator.NextEntityIndex(out var i, out var entityInQueryIndex))
                {
                    var collider       = Physics.ScaleCollider(chunkColliders[i], chunkScales[i]);
                    var rigidTransform = new RigidTransform(quaternion.identity, chunkTranslations[i].Value);
                    ProcessEntity(entityInQueryIndex, chunkEntities[i], in collider, in rigidTransform);
                }
            }

            private void ProcessTranslationRotation(in ArchetypeChunk chunk, bool useEnabledMask, in v128 chunkEnabledMask, int firstEntityIndex)
            {
                var chunkEntities     = chunk.GetNativeArray(typeGroup.entity);
                var chunkColliders    = chunk.GetNativeArray(typeGroup.collider);
                var chunkTranslations = chunk.GetNativeArray(typeGroup.translation);
                var chunkRotations    = chunk.GetNativeArray(typeGroup.rotation);
                var enumerator        = new ChunkEntityWithIndexEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count, firstEntityIndex);
                while (enumerator.NextEntityIndex(out var i, out var entityInQueryIndex))
                {
                    var rigidTransform = new RigidTransform(chunkRotations[i].Value, chunkTranslations[i].Value);
                    ProcessEntity(entityInQueryIndex, chunkEntities[i], chunkColliders[i], in rigidTransform);
                }
            }

            private void ProcessTranslationRotationScale(in ArchetypeChunk chunk, bool useEnabledMask, in v128 chunkEnabledMask, int firstEntityIndex)
            {
                var chunkEntities     = chunk.GetNativeArray(typeGroup.entity);
                var chunkColliders    = chunk.GetNativeArray(typeGroup.collider);
                var chunkTranslations = chunk.GetNativeArray(typeGroup.translation);
                var chunkRotations    = chunk.GetNativeArray(typeGroup.rotation);
                var chunkScales       = chunk.GetNativeArray(typeGroup.scale);
                var enumerator        = new ChunkEntityWithIndexEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count, firstEntityIndex);
                while (enumerator.NextEntityIndex(out var i, out var entityInQueryIndex))
                {
                    var collider       = Physics.ScaleCollider(chunkColliders[i], chunkScales[i]);
                    var rigidTransform = new RigidTransform(chunkRotations[i].Value, chunkTranslations[i].Value);
                    ProcessEntity(entityInQueryIndex, chunkEntities[i], in collider, in rigidTransform);
                }
            }

            private void ProcessLocalToWorld(in ArchetypeChunk chunk, bool useEnabledMask, in v128 chunkEnabledMask, int firstEntityIndex)
            {
                var chunkEntities      = chunk.GetNativeArray(typeGroup.entity);
                var chunkColliders     = chunk.GetNativeArray(typeGroup.collider);
                var chunkLocalToWorlds = chunk.GetNativeArray(typeGroup.localToWorld);
                var enumerator         = new ChunkEntityWithIndexEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count, firstEntityIndex);
                while (enumerator.NextEntityIndex(out var i, out var entityInQueryIndex))
                {
                    var localToWorld   = chunkLocalToWorlds[i];
                    var rotation       = quaternion.LookRotationSafe(localToWorld.Forward, localToWorld.Up);
                    var position       = localToWorld.Position;
                    var rigidTransform = new RigidTransform(rotation, position);
                    ProcessEntity(entityInQueryIndex, chunkEntities[i], chunkColliders[i], in rigidTransform);
                }
            }

            private void ProcessParent(in ArchetypeChunk chunk, bool useEnabledMask, in v128 chunkEnabledMask, int firstEntityIndex)
            {
                var chunkEntities      = chunk.GetNativeArray(typeGroup.entity);
                var chunkColliders     = chunk.GetNativeArray(typeGroup.collider);
                var chunkLocalToWorlds = chunk.GetNativeArray(typeGroup.localToWorld);
                var enumerator         = new ChunkEntityWithIndexEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count, firstEntityIndex);
                while (enumerator.NextEntityIndex(out var i, out var entityInQueryIndex))
                {
                    var localToWorld   = chunkLocalToWorlds[i];
                    var rotation       = quaternion.LookRotationSafe(localToWorld.Forward, localToWorld.Up);
                    var position       = localToWorld.Position;
                    var rigidTransform = new RigidTransform(rotation, position);
                    ProcessEntity(entityInQueryIndex, chunkEntities[i], chunkColliders[i], in rigidTransform);
                }
            }

            private void ProcessParentScale(in ArchetypeChunk chunk, bool useEnabledMask, in v128 chunkEnabledMask, int firstEntityIndex)
            {
                var chunkEntities      = chunk.GetNativeArray(typeGroup.entity);
                var chunkColliders     = chunk.GetNativeArray(typeGroup.collider);
                var chunkLocalToWorlds = chunk.GetNativeArray(typeGroup.localToWorld);
                var chunkScales        = chunk.GetNativeArray(typeGroup.scale);
                var enumerator         = new ChunkEntityWithIndexEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count, firstEntityIndex);
                while (enumerator.NextEntityIndex(out var i, out var entityInQueryIndex))
                {
                    var localToWorld   = chunkLocalToWorlds[i];
                    var rotation       = quaternion.LookRotationSafe(localToWorld.Forward, localToWorld.Up);
                    var position       = localToWorld.Position;
                    var rigidTransform = new RigidTransform(rotation, position);
                    var collider       = Physics.ScaleCollider(chunkColliders[i], chunkScales[i]);
                    ProcessEntity(entityInQueryIndex, chunkEntities[i], in collider, in rigidTransform);
                }
            }

            private void ProcessEntity(int index, Entity entity, in Collider collider, in RigidTransform rigidTransform)
            {
                Aabb aabb = Physics.AabbFrom(in collider, in rigidTransform);

                colliderAoS[index] = new ColliderAoSData
                {
                    collider       = collider,
                    rigidTransform = rigidTransform,
                    aabb           = aabb,
                    entity         = entity
                };
                xmins[index] = aabb.min.x;

                int3 minBucket = math.int3(math.floor((aabb.min - layer.worldMin) / layer.worldAxisStride));
                int3 maxBucket = math.int3(math.floor((aabb.max - layer.worldMin) / layer.worldAxisStride));
                minBucket      = math.clamp(minBucket, 0, layer.worldSubdivisionsPerAxis - 1);
                maxBucket      = math.clamp(maxBucket, 0, layer.worldSubdivisionsPerAxis - 1);

                if (math.any(math.isnan(aabb.min) | math.isnan(aabb.max)))
                {
                    layerIndices[index] = layer.bucketStartsAndCounts.Length - 1;
                }
                else if (math.all(minBucket == maxBucket))
                {
                    layerIndices[index] = (minBucket.x * layer.worldSubdivisionsPerAxis.y + minBucket.y) * layer.worldSubdivisionsPerAxis.z + minBucket.z;
                }
                else
                {
                    layerIndices[index] = layer.bucketStartsAndCounts.Length - 2;
                }
            }
        }

        //Single
        //Count total in each bucket and assign global array position to layerIndex
        [BurstCompile]
        public struct Part2Job : IJob
        {
            public TestCollisionLayer layer;
            public NativeArray<int>   layerIndices;

            public void Execute()
            {
                NativeArray<int> countsPerBucket = new NativeArray<int>(layer.bucketStartsAndCounts.Length, Allocator.Temp, NativeArrayOptions.ClearMemory);
                for (int i = 0; i < layerIndices.Length; i++)
                {
                    countsPerBucket[layerIndices[i]] = countsPerBucket[layerIndices[i]] + 1;
                }

                int totalProcessed = 0;
                for (int i = 0; i < countsPerBucket.Length; i++)
                {
                    layer.bucketStartsAndCounts[i]  = new int2(totalProcessed, countsPerBucket[i]);
                    totalProcessed                 += countsPerBucket[i];
                    countsPerBucket[i]              = 0;
                }

                for (int i = 0; i < layerIndices.Length; i++)
                {
                    int bucketIndex              = layerIndices[i];
                    layerIndices[i]              = layer.bucketStartsAndCounts[bucketIndex].x + countsPerBucket[bucketIndex];
                    countsPerBucket[bucketIndex] = countsPerBucket[bucketIndex] + 1;
                }
            }
        }

        //Parallel
        //Reverse array of dst indices to array of src indices
        //Todo: Might be faster as an IJob due to potential false sharing
        [BurstCompile]
        public struct Part3Job : IJobParallelFor
        {
            [ReadOnly, DeallocateOnJobCompletion] public NativeArray<int> layerIndices;
            [NativeDisableParallelForRestriction] public NativeArray<int> unsortedSrcIndices;

            public void Execute(int index)
            {
                unsortedSrcIndices[layerIndices[index]] = index;
            }
        }

        //Parallel
        //Sort buckets
        [BurstCompile]
        public struct Part4Job : IJobParallelFor
        {
            [NativeDisableParallelForRestriction] public NativeArray<int>   unsortedSrcIndices;
            [ReadOnly, DeallocateOnJobCompletion] public NativeArray<float> xmins;
            [ReadOnly] public NativeArray<int2>                             bucketStartAndCounts;

            public void Execute(int index)
            {
                var startAndCount = bucketStartAndCounts[index];

                var intSlice = unsortedSrcIndices.Slice(startAndCount.x, startAndCount.y);
                RadixSortBucket(intSlice, xmins);
            }
        }

        //Parallel
        //Sort buckets
        [BurstCompile]
        public struct Part4JobUnity : IJobParallelFor
        {
            [NativeDisableParallelForRestriction] public NativeArray<int>   unsortedSrcIndices;
            [ReadOnly, DeallocateOnJobCompletion] public NativeArray<float> xmins;
            [ReadOnly] public NativeArray<int2>                             bucketStartAndCounts;

            public void Execute(int index)
            {
                var startAndCount = bucketStartAndCounts[index];

                var intSlice = unsortedSrcIndices.Slice(startAndCount.x, startAndCount.y);
                UnitySortBucket(intSlice, xmins);
            }
        }

        //Parallel
        //Copy AoS data to SoA layer
        [BurstCompile]
        public struct Part5Job : IJobParallelFor
        {
            [NativeDisableParallelForRestriction]
            public TestCollisionLayer layer;

            [ReadOnly, DeallocateOnJobCompletion] public NativeArray<ColliderAoSData> colliderAoS;
            [ReadOnly] public NativeArray<int>                                        remapSrcIndices;

            public void Execute(int index)
            {
                var aos             = colliderAoS[remapSrcIndices[index]];
                layer.bodies[index] = new ColliderBody
                {
                    collider  = aos.collider,
                    transform = aos.rigidTransform,
                    entity    = aos.entity
                };
                layer.xmins[index]     = aos.aabb.min.x;
                layer.xmaxs[index]     = aos.aabb.max.x;
                layer.yzminmaxs[index] = new float4(aos.aabb.min.yz, aos.aabb.max.yz);
            }
        }

        #endregion

        #region UnitySortBucket
        private struct Ranker : System.IComparable<Ranker>
        {
            public float key;
            public int   index;

            public int CompareTo(Ranker other)
            {
                return key.CompareTo(other.key);
            }
        }

        private static void UnitySortBucket(NativeSlice<int> unsortedSrcIndices, NativeArray<float> xmins)
        {
            var count = unsortedSrcIndices.Length;
            if (count <= 1)
                return;

            var ranks = new NativeArray<Ranker>(count, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < count; i++)
            {
                ranks[i] = new Ranker
                {
                    index = unsortedSrcIndices[i],
                    key   = xmins[unsortedSrcIndices[i]]
                };
            }

            ranks.Sort();

            for (int i = 0; i < count; i++)
            {
                unsortedSrcIndices[i] = ranks[i].index;
            }
        }
        #endregion

        #region RadixSortBucket
        private struct Indexer
        {
            public UintAsBytes key;
            public int         index;
        }

        private struct UintAsBytes
        {
            public byte byte1;
            public byte byte2;
            public byte byte3;
            public byte byte4;
        }

        private static UintAsBytes Keys(float val)
        {
            uint key  = math.asuint(val);
            uint mask = (key & 0x80000000) > 0 ? 0xffffffff : 0x80000000;
            key       = mask ^ key;

            UintAsBytes result;
            result.byte1 = (byte)(key & 0x000000FF);
            key          = key >> 8;
            result.byte2 = (byte)(key & 0x000000FF);
            key          = key >> 8;
            result.byte3 = (byte)(key & 0x000000FF);
            key          = key >> 8;
            result.byte4 = (byte)(key & 0x000000FF);
            return result;
        }

        private static void CalculatePrefixSum(NativeArray<int> counts, NativeArray<int> sums)
        {
            sums[0] = 0;
            for (int i = 0; i < counts.Length - 1; i++)
            {
                sums[i + 1] = sums[i] + counts[i];
            }
        }

        public static void RadixSortBucket(NativeSlice<int> unsortedSrcIndices, NativeArray<float> xmins)
        {
            var count = unsortedSrcIndices.Length;
            if (count <= 0)
                return;

            NativeArray<int> counts1    = new NativeArray<int>(256, Allocator.Temp, NativeArrayOptions.ClearMemory);
            NativeArray<int> counts2    = new NativeArray<int>(256, Allocator.Temp, NativeArrayOptions.ClearMemory);
            NativeArray<int> counts3    = new NativeArray<int>(256, Allocator.Temp, NativeArrayOptions.ClearMemory);
            NativeArray<int> counts4    = new NativeArray<int>(256, Allocator.Temp, NativeArrayOptions.ClearMemory);
            NativeArray<int> prefixSum1 = new NativeArray<int>(256, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            NativeArray<int> prefixSum2 = new NativeArray<int>(256, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            NativeArray<int> prefixSum3 = new NativeArray<int>(256, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            NativeArray<int> prefixSum4 = new NativeArray<int>(256, Allocator.Temp, NativeArrayOptions.UninitializedMemory);

            NativeArray<Indexer> frontArray = new NativeArray<Indexer>(count, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            NativeArray<Indexer> backArray  = new NativeArray<Indexer>(count, Allocator.Temp, NativeArrayOptions.UninitializedMemory);

            //Counts
            for (int i = 0; i < count; i++)
            {
                var keys            = Keys(xmins[unsortedSrcIndices[i]]);
                counts1[keys.byte1] = counts1[keys.byte1] + 1;
                counts2[keys.byte2] = counts2[keys.byte2] + 1;
                counts3[keys.byte3] = counts3[keys.byte3] + 1;
                counts4[keys.byte4] = counts4[keys.byte4] + 1;
                frontArray[i]       = new Indexer { key = keys, index = unsortedSrcIndices[i] };
            }

            //Sums
            CalculatePrefixSum(counts1, prefixSum1);
            CalculatePrefixSum(counts2, prefixSum2);
            CalculatePrefixSum(counts3, prefixSum3);
            CalculatePrefixSum(counts4, prefixSum4);

            for (int i = 0; i < count; i++)
            {
                byte key        = frontArray[i].key.byte1;
                int  dest       = prefixSum1[key];
                backArray[dest] = frontArray[i];
                prefixSum1[key] = prefixSum1[key] + 1;
            }

            for (int i = 0; i < count; i++)
            {
                byte key         = backArray[i].key.byte2;
                int  dest        = prefixSum2[key];
                frontArray[dest] = backArray[i];
                prefixSum2[key]  = prefixSum2[key] + 1;
            }

            for (int i = 0; i < count; i++)
            {
                byte key        = frontArray[i].key.byte3;
                int  dest       = prefixSum3[key];
                backArray[dest] = frontArray[i];
                prefixSum3[key] = prefixSum3[key] + 1;
            }

            for (int i = 0; i < count; i++)
            {
                byte key                 = backArray[i].key.byte4;
                int  dest                = prefixSum4[key];
                int  src                 = backArray[i].index;
                unsortedSrcIndices[dest] = src;
                prefixSum4[key]          = prefixSum4[key] + 1;
            }
        }

        #endregion
    }
}

