﻿using NUnit.Framework;
using Unity.Entities;
using Unity.Burst;
using Unity.Physics;
using Unity.Mathematics;
using Unity.Profiling;
using Unity.Collections;
using Unity.Entities.Content;
using Unity.NetCode;
using Unity.Physics.Systems;
using UnityEngine;


[UpdateInGroup(typeof(PhysicsSystemGroup))]
[UpdateBefore(typeof(PhysicsInitializeGroup))]
[BurstCompile]
partial struct PlayerMovementSystem : ISystem
{
    const float k_DefaultTau = 0.4f;
    const float k_DefaultDamping = 0.9f;
    const float k_DefaultSkinWidth = 0f;
    const float k_DefaultContactTolerance = 0.1f;
    const float k_DefaultMaxSlope = 60f;
    const float k_DefaultMaxMovementSpeed = 10f;
    const int k_DefaultMaxIterations = 10;
    const float k_DefaultMass = 1f;

    private ProfilerMarker m_MarkerGroundCheck;
    private ProfilerMarker m_MarkerStep;
    
    private BufferLookup<TerrainBlocks> _bufferLookup;
    private NativeArray<Entity> terrainAreasEntities;
    private NativeArray<TerrainArea> terrainAreas;
    private int blocksPerChunkSide;

    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<TerrainSpawner>();
        state.RequireForUpdate<PhysicsWorldSingleton>();
        state.RequireForUpdate<NetworkTime>();
        state.RequireForUpdate<Player>();

        m_MarkerGroundCheck = new ProfilerMarker("GroundCheck");
        m_MarkerStep = new ProfilerMarker("Step");
        _bufferLookup = state.GetBufferLookup<TerrainBlocks>(true);
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        state.CompleteDependency();

        var physicsWorldSingleton = SystemAPI.GetSingleton<PhysicsWorldSingleton>();
        if (!HasPhysicsWorldBeenInitialized(physicsWorldSingleton))
        {
            return;
        }
        var networkTime = SystemAPI.GetSingleton<NetworkTime>();
        
        _bufferLookup.Update(ref state);
        var terrainAreasQuery = SystemAPI.QueryBuilder().WithAll<TerrainArea>().Build();
        terrainAreasEntities = terrainAreasQuery.ToEntityArray(state.WorldUpdateAllocator);
        terrainAreas = terrainAreasQuery.ToComponentDataArray<TerrainArea>(state.WorldUpdateAllocator);
        blocksPerChunkSide = SystemAPI.GetSingleton<TerrainSpawner>().blocksPerChunkSide;
        
        foreach (var player in SystemAPI.Query<PlayerAspect>().WithAll<Simulate>())
        {
            if (!player.AutoCommandTarget.Enabled)
            {
                player.Velocity.Linear = float3.zero;
                return;
            }

            var playerConfig = SystemAPI.GetComponent<PlayerConfig>(player.Player.PlayerConfig);
            var playerCollider = SystemAPI.GetComponent<PhysicsCollider>(player.Player.PlayerConfig);
            
            // Character step input
            PlayerUtilities.PlayerStepInput stepInput = new PlayerUtilities.PlayerStepInput
            {
                PhysicsWorldSingleton = physicsWorldSingleton,
                DeltaTime = SystemAPI.Time.DeltaTime,
                Up = new float3(0, 1, 0),
                Gravity = new float3(0, -playerConfig.Gravity, 0),
                MaxIterations = k_DefaultMaxIterations,
                Tau = k_DefaultTau,
                Damping = k_DefaultDamping,
                SkinWidth = k_DefaultSkinWidth,
                ContactTolerance = k_DefaultContactTolerance,
                MaxSlope = math.radians(k_DefaultMaxSlope),
                RigidBodyIndex = physicsWorldSingleton.PhysicsWorld.GetRigidBodyIndex(player.Self),
                CurrentVelocity = player.Player.Velocity,
                MaxMovementSpeed = k_DefaultMaxMovementSpeed
            };

            //Using local position here is fine, because the character controller does not have any parent.
            //Using the Position is wrong because it is not up to date. (the LocalTransform is synchronized but
            //the world transform isn't).
            RigidTransform ccTransform = new RigidTransform()
            {
                pos = player.Transform.ValueRO.Position,
                rot = quaternion.identity
            };

            m_MarkerGroundCheck.Begin();
            // Check the terrain areas underneath the player
            PlayerUtilities.PlayerSupportState supportState = CheckPlayerSupported(ccTransform);
            /*PlayerUtilities.CheckSupport(
                in physicsWorldSingleton,
                ref playerCollider,
                stepInput,
                ccTransform,
                out PlayerUtilities.PlayerSupportState supportState,
                out _,
                out _);*/
            m_MarkerGroundCheck.End();

            float2 input = player.Input.Movement;
            float3 wantedMove = new float3(input.x, 0, input.y) * playerConfig.Speed * SystemAPI.Time.DeltaTime;

            // Wanted movement is relative to camera
            wantedMove = math.rotate(quaternion.RotateY(player.Input.Yaw), wantedMove);

            float3 wantedVelocity = wantedMove / SystemAPI.Time.DeltaTime;
            wantedVelocity.y = player.Player.Velocity.y;

            if (supportState == PlayerUtilities.PlayerSupportState.Supported)
            {
                player.Player.JumpStart = NetworkTick.Invalid;
                player.Player.OnGround = 1;
                player.Player.Velocity = wantedVelocity;
                // Allow jump and stop falling when grounded
                if (player.Input.Jump.IsSet)
                {
                    player.Player.Velocity.y = playerConfig.JumpSpeed;
                    player.Player.JumpStart = networkTime.ServerTick;
                }
                else
                    player.Player.Velocity.y = 0;
            }
            else
            {
                player.Player.OnGround = 0;
                // Free fall
                player.Player.Velocity.y -= playerConfig.Gravity * SystemAPI.Time.DeltaTime;
            }

            m_MarkerStep.Begin();
            // Ok because affect bodies is false so no impulses are written
            // todo
            MovePlayerCheckCollisions(stepInput, ref ccTransform, ref player.Player.Velocity);
            //NativeStream.Writer deferredImpulseWriter = default;
            //PlayerUtilities.CollideAndIntegrate(stepInput, k_DefaultMass, false, ref playerCollider, ref ccTransform, ref player.Player.Velocity, ref deferredImpulseWriter);
            m_MarkerStep.End();

            // Set the physics velocity and let physics move the kinematic object based on that
            player.Velocity.Linear = (ccTransform.pos - player.Transform.ValueRO.Position) / SystemAPI.Time.DeltaTime;
        }
    }

    /// <summary>
    /// As we run before <see cref="PhysicsInitializeGroup"/> it is possible to execute before any physics bodies
    /// has been initialized.
    ///
    /// There may be a better way to do this.
    /// </summary>
    static bool HasPhysicsWorldBeenInitialized(PhysicsWorldSingleton physicsWorldSingleton)
    {
        return physicsWorldSingleton.PhysicsWorld.Bodies is { IsCreated: true, Length: > 0 };
    }

    private PlayerUtilities.PlayerSupportState CheckPlayerSupported(RigidTransform transform)
    {
        float offset = 0.0f;
        // Need to check each corner under player for support
        NativeHashSet<int3> set = new NativeHashSet<int3>(4, Allocator.Temp);
        set.Add(new int3((int)math.floor(transform.pos.x +offset), (int)math.floor(transform.pos.y),
            (int)math.floor(transform.pos.z+offset)));
        set.Add(new int3((int)math.ceil(transform.pos.x -offset), (int)math.floor(transform.pos.y),
            (int)math.floor(transform.pos.z+offset)));
        set.Add(new int3((int)math.floor(transform.pos.x +offset), (int)math.floor(transform.pos.y),
            (int)math.ceil(transform.pos.z-offset)));
        set.Add(new int3((int)math.ceil(transform.pos.x -offset), (int)math.floor(transform.pos.y),
            (int)math.ceil(transform.pos.z-offset)));
        
        foreach (var pos in set)
        {
            //Debug.DrawLine(transform.pos, new float3(pos), Color.cyan);
            //Debug.Log($"Checking pos {pos}");
            if (IsBlockAtPosition(pos))
            {
                Debug.DrawLine(transform.pos, new float3(pos), Color.cyan);
                return PlayerUtilities.PlayerSupportState.Supported;
            }
        }
        return PlayerUtilities.PlayerSupportState.Unsupported;
    }

    private bool IsBlockAtPosition(int3 pos)
    {
        if (GetTerrainAreaByPosition(pos, out Entity containingArea, out int3 containingAreaLocation))
        {
            var terrainBuffer = _bufferLookup[containingArea];
            int localx = pos.x - containingAreaLocation.x-1; 
            int localy = pos.y - containingAreaLocation.y-1; 
            int localz = pos.z - containingAreaLocation.z-1; 
            int index = localx + localy * blocksPerChunkSide + localz  * blocksPerChunkSide * blocksPerChunkSide;
            int4 block = terrainBuffer[index].Value; 
            if (block.w != -1)
            {
                int d = 1;
                //terrainArea.ValueRO.location
                float3 baseLocation = new float3(
                    containingAreaLocation.x + block.x,
                    containingAreaLocation.y + block.y,
                    containingAreaLocation.z + block.z);
                Debug.DrawLine(baseLocation, baseLocation + new float3(d,0,0), Color.green);
                Debug.DrawLine(baseLocation, baseLocation + new float3(0,d,0), Color.green);
                Debug.DrawLine(baseLocation, baseLocation + new float3(0,0,d), Color.green);
                Debug.DrawLine(baseLocation + new float3(d,d,0), baseLocation + new float3(d,0,0), Color.green);
                Debug.DrawLine(baseLocation + new float3(d,d,0), baseLocation + new float3(0,d,0), Color.green);
                Debug.DrawLine(baseLocation + new float3(d,d,0), baseLocation + new float3(d,d,d), Color.green);
                Debug.DrawLine(baseLocation + new float3(0,d,d), baseLocation + new float3(0,d,0), Color.green);
                Debug.DrawLine(baseLocation + new float3(0,d,d), baseLocation + new float3(0,0,d), Color.green);
                Debug.DrawLine(baseLocation + new float3(0,d,d), baseLocation + new float3(d,d,d), Color.green);
                Debug.DrawLine(baseLocation + new float3(d,0,d), baseLocation + new float3(d,0,0), Color.green);
                Debug.DrawLine(baseLocation + new float3(d,0,d), baseLocation + new float3(d,d,d), Color.green);
                Debug.DrawLine(baseLocation + new float3(d,0,d), baseLocation + new float3(0,0,d), Color.green);
                return true;
            }
        }
        return false;
    }

    private bool GetTerrainAreaByPosition(int3 pos, out Entity containingArea, out int3 containingAreaLocation)
    {
        for (int i = 0; i < terrainAreas.Length; i++ )
        {
            var terrainArea = terrainAreas[i];
            int3 loc = terrainArea.location;
            if (pos.x > loc.x && pos.x <= loc.x + blocksPerChunkSide &&
                pos.y > loc.y && pos.y <= loc.y + blocksPerChunkSide &&
                pos.z > loc.z && pos.z <= loc.z + blocksPerChunkSide)
            {
                containingArea = terrainAreasEntities[i];
                containingAreaLocation = loc;
                return true;
            }
        }
        containingArea = new Entity();
        containingAreaLocation = new int3(-1);
        return false;
    }

    private void MovePlayerCheckCollisions(PlayerUtilities.PlayerStepInput stepInput, ref RigidTransform transform,
        ref float3 linearVelocity)
    {
        float deltaTime = stepInput.DeltaTime;
        float3 newPosition = transform.pos + deltaTime * linearVelocity;
        float3 newVelocity = linearVelocity;
        float3 norm = math.normalize(linearVelocity);
        
        /* Need to check four corners of the mesh for obstacles
        NativeHashSet<int3> set = new NativeHashSet<int3>(2, Allocator.Temp);
        
        set.Add((int3)(transform.pos + norm + new float3(0.5f,0.0f,0.0f)));
        
        int3 pos = (int3)(transform.pos + new float3(0.1f,0.1f,0.0f) + norm);
        Debug.DrawLine(transform.pos, (float3) pos, Color.magenta);
        if (IsBlockAtPosition(pos))
        {
            //Debug.DrawLine(transform.pos, (float3) pos, Color.magenta);
            //transform.pos = newPosition;
            //linearVelocity = new float3(0);
        }*/
        transform.pos = newPosition;
        linearVelocity = newVelocity; 

    }
}




