﻿using Opencraft.Terrain.Authoring;
using Unity.Collections;
using Unity.Entities;
using Unity.Entities.Graphics;
using Unity.Mathematics;
using Unity.Rendering;
using UnityEngine;

namespace Opencraft.Rendering
{
    // Adds components to TerrainArea entities on client side for meshing and rendering purposes
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.Presentation)]
    [UpdateBefore(typeof(TerrainMeshingSystem))]
    public partial class TerrainRenderInitSystem : SystemBase
    {
        private EntityQuery _materialQuery;

        protected override void OnCreate()
        {
            RequireForUpdate<TerrainArea>();
            RequireForUpdate<MaterialBank>();
            _materialQuery = GetEntityQuery(ComponentType.ReadOnly<MaterialBank>());
        }

        protected override void OnUpdate()
        {
            // On each new terrain area, add a new Mesh managed object
            EntityCommandBuffer ecb = new EntityCommandBuffer(Allocator.Temp);
            float3 boundsExtents = new float3(0.5 * Env.AREA_SIZE);
            MaterialBank materialBank = _materialQuery.GetSingleton<MaterialBank>();
            foreach (var (terrainArea, terrainNeighbors, entity) in SystemAPI.Query<RefRO<TerrainArea>,RefRO<TerrainNeighbors>>().WithNone<MaterialMeshInfo>()
                         .WithEntityAccess())
            {
                int3 loc = terrainArea.ValueRO.location;
                Mesh mesh = new Mesh { name = $"mesh_{loc.x},{loc.y},{loc.z}" };

                RenderMeshArray renderMeshArray = new RenderMeshArray(new[] { materialBank.TerrainMaterial }, new[] { mesh });
                var materialMeshInfo = MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0);

                ecb.AddSharedComponentManaged(entity, renderMeshArray);
                ecb.AddComponent(entity, materialMeshInfo);
                ecb.AddComponent(entity,
                    new RenderBounds { Value = new AABB() { Center = boundsExtents, Extents = boundsExtents } });
                ecb.AddSharedComponentManaged(entity, RenderFilterSettings.Default);
                ecb.AddComponent(entity, new ComponentTypeSet(new[]
                {
                    // Entities without these components will not match queries and will never be rendered.
                    ComponentType.ReadWrite<WorldRenderBounds>(),
                    ComponentType.ChunkComponent<ChunkWorldRenderBounds>(),
                    ComponentType.ChunkComponent<EntitiesGraphicsChunkInfo>(),
                    ComponentType.ReadWrite<WorldToLocal_Tag>(),
                    ComponentType.ReadWrite<PerInstanceCullingTag>(),

                }));

                // Remesh neighbors of the new area to eliminate shared faces
                if (EntityManager.HasComponent<Remesh>(terrainNeighbors.ValueRO.neighborXN))
                    ecb.SetComponentEnabled<Remesh>(terrainNeighbors.ValueRO.neighborXN, true);
                if (EntityManager.HasComponent<Remesh>(terrainNeighbors.ValueRO.neighborXP))
                    ecb.SetComponentEnabled<Remesh>(terrainNeighbors.ValueRO.neighborXP, true);
                if (EntityManager.HasComponent<Remesh>(terrainNeighbors.ValueRO.neighborYN))
                    ecb.SetComponentEnabled<Remesh>(terrainNeighbors.ValueRO.neighborYN, true);
                if (EntityManager.HasComponent<Remesh>(terrainNeighbors.ValueRO.neighborYP))
                    ecb.SetComponentEnabled<Remesh>(terrainNeighbors.ValueRO.neighborYP, true);
                if (EntityManager.HasComponent<Remesh>(terrainNeighbors.ValueRO.neighborZN))
                    ecb.SetComponentEnabled<Remesh>(terrainNeighbors.ValueRO.neighborZN, true);
                if (EntityManager.HasComponent<Remesh>(terrainNeighbors.ValueRO.neighborZP))
                    ecb.SetComponentEnabled<Remesh>(terrainNeighbors.ValueRO.neighborZP, true);
                ecb.SetComponentEnabled<NewSpawn>(entity, false);
            }

            //TODO ECB playback can be deferred to avoid immediate sync point
            ecb.Playback(EntityManager);
            ecb.Dispose();
        }
    }
}
