// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System.Collections.Generic;
using Stride.BepuPhysics;
using Stride.BepuPhysics.Definitions.Colliders;
using Stride.BepuPhysics.Definitions.Colliders.Voxels;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Input;

namespace VoxelTerrain;

/// <summary>
/// Digs into the field with the left mouse button, fills it back with F.
/// </summary>
/// <remarks>
/// This is the point of the grid layer, made visible: a terrain edit is a write into the field.
/// Nothing is re-meshed, no acceleration structure is rebuilt, no collider is re-created and the
/// collidable's bounds do not move. The renderer traces the samples and the narrow phase reads the
/// same samples, so both see the hole on the next frame. The aim is a physics ray against the voxel
/// collider, which walks the grid on the CPU exactly as the shader walks it on the GPU.
/// </remarks>
public sealed class Digger : SyncScript
{
    /// <summary>Radius of the ball added or removed, in world units.</summary>
    public float Radius { get; set; } = 1.6f;

    /// <summary>How far the aim reaches.</summary>
    public float Reach { get; set; } = 80f;

    /// <summary>Seconds between two edits while a button is held.</summary>
    public float Interval { get; set; } = 0.06f;

    /// <summary>What the aim lands on, for the HUD.</summary>
    public string Status { get; private set; } = "nothing";

    private readonly List<HitInfo> hits = [];
    private float cooldown;

    public override void Update()
    {
        cooldown -= (float)Game.UpdateTime.Elapsed.TotalSeconds;

        var simulation = Entity.GetSimulation();
        if (simulation is null)
            return;

        Entity.Transform.UpdateWorldMatrix();
        var origin = Entity.Transform.WorldMatrix.TranslationVector;
        var forward = -Vector3.Normalize(new Vector3(
            Entity.Transform.WorldMatrix.M31,
            Entity.Transform.WorldMatrix.M32,
            Entity.Transform.WorldMatrix.M33));

        // Every hit along the ray, not just the first: a ball resting on the terrain is hit before
        // it is, and a tool that digs only where nothing sits is a tool that works half the time.
        hits.Clear();
        simulation.RayCastPenetrating(origin, forward, Reach, hits);

        HitInfo? terrain = null;
        foreach (var candidate in hits)
        {
            if (candidate.Collidable.Collider is not VoxelCollider)
                continue;
            if (terrain is null || candidate.Distance < terrain.Value.Distance)
                terrain = candidate;
        }

        Status = terrain is { } aimed
            ? $"terrain at {aimed.Distance:0.0} m"
            : hits.Count > 0 ? "something else in the way" : "nothing";

        var dig = Input.IsMouseButtonDown(MouseButton.Left);
        var fill = Input.IsKeyDown(Keys.F);
        if ((!dig && !fill) || cooldown > 0f || terrain is not { } hit)
            return;

        cooldown = Interval;
        var centre = hit.Point + (fill ? hit.Normal * (Radius * 0.6f) : Vector3.Zero);
        VoxelTerrainScene.Edit(centre, Radius, fill);
    }
}
