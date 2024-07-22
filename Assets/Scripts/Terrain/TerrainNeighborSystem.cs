﻿using Opencraft.Rendering;
using Opencraft.Terrain.Authoring;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.NetCode;

namespace Opencraft.Terrain
{
    [BurstCompile]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation  | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
#if UNITY_CLIENT
    [UpdateBefore(typeof(TerrainRenderInitSystem))]
#endif
    // Calculates terrain neighbors and sets up links between them for easy access by terrain modification and meshing systems
    public partial class TerrainNeighborSystem : SystemBase
    {
        private EntityQuery _terrainChunkQuery;

        private EntityQuery _newSpawnQuery;
        private ComponentLookup<TerrainNeighbors> _terrainNeighborsLookup;

        protected override void OnCreate()
        {
            RequireForUpdate<TerrainArea>();
            RequireForUpdate<NewSpawn>();
            _terrainChunkQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<TerrainArea>()
                .Build(EntityManager);
            _newSpawnQuery = SystemAPI.QueryBuilder().WithAll<NewSpawn>().Build();
            _terrainNeighborsLookup = GetComponentLookup<TerrainNeighbors>(false);
        }


        [BurstCompile]
        protected override void OnUpdate()
        {
            if (_newSpawnQuery.IsEmpty)
                return;
            CompleteDependency();
            _terrainNeighborsLookup.Update(ref CheckedStateRef);
            NativeArray<Entity> terrainAreaEntities = _terrainChunkQuery.ToEntityArray(Allocator.TempJob);
            NativeArray<TerrainArea> terrainAreas =
                _terrainChunkQuery.ToComponentDataArray<TerrainArea>(Allocator.TempJob);

            new SetAreaNeighborsJob()
            {
                terrainAreas = terrainAreas,
                terrainAreaEntities = terrainAreaEntities,
                terrainNeighborsLookup = _terrainNeighborsLookup
            }.ScheduleParallel();
        }
    }


    // When a new terrain area has been spawned, set it's neighbors, and update it's neighbors neighbors.
    [BurstCompile]
    public partial struct SetAreaNeighborsJob : IJobEntity
    {
        // thread safe as long as no terrain areas have the same location!
        [NativeDisableParallelForRestriction] 
        [DeallocateOnJobCompletion] 
        public NativeArray<Entity> terrainAreaEntities;
        [NativeDisableParallelForRestriction]
        [DeallocateOnJobCompletion] 
        public NativeArray<TerrainArea> terrainAreas;
        [NativeDisableParallelForRestriction] public ComponentLookup<TerrainNeighbors> terrainNeighborsLookup;

        public void Execute(Entity entity, ref TerrainArea terrainArea, EnabledRefRW<NewSpawn> newSpawn)
        {
            var terrainNeighbors = terrainNeighborsLookup.GetRefRW(entity);
            for (int i = 0; i < terrainAreaEntities.Length; i++)
            {
                var otherTerrainEntity = terrainAreaEntities[i];
                var otherTerrainArea = terrainAreas[i];
                var otherTerrainNeighbors = terrainNeighborsLookup.GetRefRW(otherTerrainEntity);
                int3 otherLoc = otherTerrainArea.location;
                if (otherLoc.Equals(terrainArea.location + new int3(1, 0, 0)))
                {
                    terrainNeighbors.ValueRW.neighborXP = otherTerrainEntity;
                    otherTerrainNeighbors.ValueRW.neighborXN = entity;
                }

                if (otherLoc.Equals(terrainArea.location + new int3(-1, 0, 0)))
                {
                    terrainNeighbors.ValueRW.neighborXN = otherTerrainEntity;
                    otherTerrainNeighbors.ValueRW.neighborXP = entity;
                }

                if (otherLoc.Equals(terrainArea.location + new int3(0, 1, 0)))
                {
                    terrainNeighbors.ValueRW.neighborYP = otherTerrainEntity;
                    otherTerrainNeighbors.ValueRW.neighborYN = entity;
                }

                if (otherLoc.Equals(terrainArea.location + new int3(0, -1, 0)))
                {
                    terrainNeighbors.ValueRW.neighborYN = otherTerrainEntity;
                    otherTerrainNeighbors.ValueRW.neighborYP = entity;
                }

                if (otherLoc.Equals(terrainArea.location + new int3(0, 0, 1)))
                {
                    terrainNeighbors.ValueRW.neighborZP = otherTerrainEntity;
                    otherTerrainNeighbors.ValueRW.neighborZN = entity;
                }

                if (otherLoc.Equals(terrainArea.location + new int3(0, 0, -1)))
                {
                    terrainNeighbors.ValueRW.neighborZN = otherTerrainEntity;
                    otherTerrainNeighbors.ValueRW.neighborZP = entity;
                }
            }

            newSpawn.ValueRW = false;
        }
    }
}