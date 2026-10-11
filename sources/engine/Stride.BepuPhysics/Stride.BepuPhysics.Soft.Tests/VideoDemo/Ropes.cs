// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Rendering.ProceduralModels;

namespace Stride.BepuPhysics.Soft.Tests.VideoDemo;

/// <summary> Soft ropes: a thin volumetric capsule between two points, each end pinned to an anchor (a body anchor when the anchor carries a body) </summary>
internal static class Ropes
{
    /// <summary> Pinned length at each end, along the rope </summary>
    public const float PinLength = 0.08f;

    /// <summary> A rope from <paramref name="from"/> to <paramref name="to"/>; a null anchor holds its end where it is </summary>
    /// <remarks> The solver holds a load up to a few dozen times heavier than a particle: <paramref name="mass"/> grows with what the rope carries </remarks>
    public static (Entity Entity, VolumetricSoftBodyComponent Body) Add(VideoDemoGame game, Vector3 from, Vector3 to, float radius, Color color,
        Entity? fromAnchor, Entity? toAnchor, string? fromNode = null, string? toNode = null, float mass = 0.5f, float stiffness = 30f, string name = "rope")
    {
        float length = Vector3.Distance(from, to);
        float half = length / 2f;
        // About two cells across the rope, at most 40 along it: few particles, each heavy enough for what hangs on the rope
        int resolution = Math.Clamp((int)MathF.Ceiling(length / radius), 6, 40);
        var body = new VolumetricSoftBodyComponent
        {
            Resolution = resolution, Stiffness = stiffness, ShearStiffness = stiffness * 0.6f, VolumeStiffness = 8f, Damping = 0.4f, Mass = mass,
            Pins =
            {
                new SoftBodyPin { Region = new BoundingBox(new Vector3(-1f, -half - 1f, -1f), new Vector3(1f, -half + PinLength, 1f)), Anchor = Anchor(fromAnchor, fromNode) },
                new SoftBodyPin { Region = new BoundingBox(new Vector3(-1f, half - PinLength, -1f), new Vector3(1f, half + 1f, 1f)), Anchor = Anchor(toAnchor, toNode) },
            },
        };
        var entity = new Entity(name)
        {
            new ModelComponent(Look.Model(game, new CapsuleProceduralModel { Radius = radius, Length = MathF.Max(0.01f, length - 2f * radius), Tessellation = 12, LengthRings = Math.Max(8, resolution) }, color, 0.4f)),
            body,
        };
        entity.Transform.Position = (from + to) / 2f;
        entity.Transform.Rotation = Quaternion.BetweenDirections(Vector3.UnitY, Vector3.Normalize(to - from));
        game.AddToScene(entity);
        return (entity, body);
    }

    /// <summary> Joined to the collidable of <paramref name="entity"/>, the bones of a skeleton for a <paramref name="node"/>; null holds the end in place </summary>
    public static SoftBodyAnchor? Anchor(Entity? entity, string? node = null)
    {
        if (entity is null)
            return null;
        var collidable = (node is null ? null : entity.Get<SkeletonBodyComponent>()) ?? entity.Get<CollidableComponent>();
        return collidable is null ? new WorldAnchor { Entity = entity, NodeName = node } : new BodyAnchor { Body = collidable, NodeName = node };
    }
}
