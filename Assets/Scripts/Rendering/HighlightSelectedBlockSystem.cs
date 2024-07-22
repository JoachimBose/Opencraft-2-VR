﻿using Opencraft.Player;
using Opencraft.Player.Authoring;
using Opencraft.Rendering.Authoring;
using Opencraft.Terrain.Authoring;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;



namespace Opencraft.Rendering
{
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(PlayerSelectedBlockSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.Presentation)]
    [BurstCompile]
    
    public partial struct HighlightSelectedBlockSystem : ISystem
    {
        
        private EntityQuery playerQuery;
        private EntityQuery blockOutlineQuery;
        private ComponentLookup<TerrainArea> _terrainAreaLookup;
        private static readonly float3 defaultOutLinePosition = new float3(0.0f, -100.0f, 0.0f);
        private static readonly float3 outlinePosOffset = new float3(0.5);

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<EndSimulationEntityCommandBufferSystem.Singleton>();
            state.RequireForUpdate<PlayerComponent>();
            state.RequireForUpdate<BlockOutline>();
            playerQuery= new EntityQueryBuilder(Allocator.Temp)
                .WithAll<PlayerComponent, SelectedBlock, GhostOwnerIsLocal>()
                .Build(ref state);
            blockOutlineQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<BlockOutline>()
                .WithAllRW<LocalTransform>()
                .Build(ref state);
            _terrainAreaLookup = state.GetComponentLookup<TerrainArea>(isReadOnly:true);
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            _terrainAreaLookup.Update(ref state);
            NativeArray<SelectedBlock> players = playerQuery.ToComponentDataArray<SelectedBlock>(Allocator.Temp);
            
            var singleton = SystemAPI.GetSingleton<EndSimulationEntityCommandBufferSystem.Singleton>();
            EntityCommandBuffer ecb = singleton.CreateCommandBuffer(state.WorldUnmanaged);
            
            int i = 0;
            int numPlayers = players.Length;
            foreach (var (outlinePos, entity) in SystemAPI.Query<RefRW<LocalTransform>>().WithAll<BlockOutline, Simulate>().WithEntityAccess())
            {
                if (i >= numPlayers)
                {
                    // There are too many highlight entities, remove the additional ones
                    ecb.DestroyEntity(entity);
                    continue;
                }
                
                Entity area = players[i].terrainArea;
                if (area != Entity.Null)
                {
                    TerrainArea terrainArea = _terrainAreaLookup[area];
                    float3 globalPos = terrainArea.location * Env.AREA_SIZE + players[i].blockLoc + outlinePosOffset;
                    outlinePos.ValueRW.Position = globalPos;
                }
                else
                {
                    outlinePos.ValueRW.Position = defaultOutLinePosition;
                }

                i++;
            }

        }
    }
    
    
    
}