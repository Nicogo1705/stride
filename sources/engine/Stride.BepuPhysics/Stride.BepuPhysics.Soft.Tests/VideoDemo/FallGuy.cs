// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using Stride.BepuPhysics.Definitions;
using Stride.BepuPhysics.Definitions.Colliders;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Graphics.GeometricPrimitives;
using Stride.Harness.Demo;
using Stride.Rendering;
using Stride.Rendering.ProceduralModels;

namespace Stride.BepuPhysics.Soft.Tests.VideoDemo;

/// <summary>
/// A jelly-bean character made of a single soft body: body, arms and legs are one volumetric mesh carrying all the mass and every contact.
/// Inside, a Standing skeleton of ghost bones (no shape, a hundredth of the mass) holds it up; the flesh and the bones pull on each other.
/// </summary>
internal static class FallGuy
{
    /// <summary> The skeleton, its animation and its bone frames are the bean's </summary>
    public static (Entity Entity, SkeletonBodyComponent Body, VolumetricSoftBodyComponent Flesh, Bean.Animator Animator) Add(DemoGame game, Color color, Vector3 position, float yaw, float time = 0f)
    {
        var device = game.GraphicsDevice;
        var model = Look.Model(game, new FallGuyProceduralModel(), color, 0.8f);
        model.Skeleton = new Skeleton { Nodes = Bean.Nodes() };

        var body = new SkeletonBodyComponent { Mode = SkeletonBodyMode.Standing, Ghost = true, SelfCollision = false, StandingStrength = 2.5f };
        ((BoneCollider)body.Collider).Bones.AddRange(Bean.Hitboxes());
        var flesh = new VolumetricSoftBodyComponent
        {
            Resolution = 10, Mass = 20f, Stiffness = 22f, ShearStiffness = 18f, VolumeStiffness = 16f, Damping = 0.6f,
            FollowSkeleton = true, SkeletonStiffness = 12f, SkeletonDamping = 0.5f,
        };
        var animator = new Bean.Animator { Time = time };
        var modelComponent = new ModelComponent(model);
        var entity = new Entity("fall guy") { modelComponent, flesh, body, animator };
        entity.Transform.Position = position;
        entity.Transform.Rotation = Quaternion.RotationY(yaw);

        // Eyes on the head bone, which the flesh follows
        var white = Look.Paint(device, new Color(250, 250, 250), 0.9f);
        var black = Look.Paint(device, new Color(25, 25, 32), 0.95f);
        foreach (var x in new[] { -0.1f, 0.1f })
        {
            Part("eye", new SphereProceduralModel { Radius = 0.07f, Tessellation = 20 }, new Vector3(x, 0.02f, 0.27f), white);
            Part("pupil", new SphereProceduralModel { Radius = 0.038f, Tessellation = 16 }, new Vector3(x * 1.05f, 0.02f, 0.325f), black);
        }
        game.AddToScene(entity);
        return (entity, body, flesh, animator);

        void Part(string name, PrimitiveProceduralModelBase shape, Vector3 offset, Material material)
        {
            var part = new Entity(name) { new ModelComponent(Look.Model(game, shape, material)), new ModelNodeLinkComponent { Target = modelComponent, NodeName = "Head" } };
            part.Transform.Position = offset;
            entity.AddChild(part);
        }
    }
}

/// <summary>
/// A closed jelly-bean body with stubby arms and legs, one surface around the bean's skeleton: a sphere whose vertices are pushed out
/// to the outermost crossing of a smooth union of capsules along their direction from the middle of the body.
/// </summary>
internal sealed class FallGuyProceduralModel : PrimitiveProceduralModelBase
{
    private static readonly Vector3 Center = new(0f, 0.75f, 0f);
    private const float Blend = 0.07f, Reach = 1.2f, Step = 0.004f;

    private static readonly (Vector3 A, Vector3 B, float Radius)[] Capsules =
    [
        (new(0f, 0.55f, 0f), new(0f, 1.08f, 0f), 0.32f), // body
        (new(0.13f, 0.32f, 0f), new(0.13f, 0.1f, 0.02f), 0.1f), // legs
        (new(-0.13f, 0.32f, 0f), new(-0.13f, 0.1f, 0.02f), 0.1f),
        (new(0.3f, 0.84f, 0f), new(0.44f, 0.6f, 0.02f), 0.075f), // arms
        (new(-0.3f, 0.84f, 0f), new(-0.44f, 0.6f, 0.02f), 0.075f),
    ];

    /// <summary> Rings from the top to the bottom, and segments around </summary>
    public int Rings { get; set; } = 56;

    public int Segments { get; set; } = 64;

    protected override GeometricMeshData<VertexPositionNormalTexture> CreatePrimitiveMeshData()
    {
        var vertices = new VertexPositionNormalTexture[(Rings + 1) * (Segments + 1)];
        for (int ring = 0; ring <= Rings; ring++)
        {
            float polar = MathF.PI * ring / Rings;
            for (int segment = 0; segment <= Segments; segment++)
            {
                float azimuth = MathF.Tau * segment / Segments;
                var direction = new Vector3(MathF.Sin(polar) * MathF.Sin(azimuth), MathF.Cos(polar), MathF.Sin(polar) * MathF.Cos(azimuth));
                var position = Center + direction * Outermost(direction);
                vertices[ring * (Segments + 1) + segment] = new VertexPositionNormalTexture(position, Normal(position), new Vector2((float)segment / Segments, (float)ring / Rings));
            }
        }

        var indices = new List<int>(Rings * Segments * 6);
        for (int ring = 0; ring < Rings; ring++)
        {
            for (int segment = 0; segment < Segments; segment++)
            {
                int a = ring * (Segments + 1) + segment, b = a + 1, c = a + Segments + 1, d = c + 1;
                indices.AddRange([a, b, c, b, d, c]);
            }
        }
        return new GeometricMeshData<VertexPositionNormalTexture>(vertices, indices.ToArray(), isLeftHanded: false) { Name = "FallGuy" };
    }

    /// <summary> The distance from <see cref="Center"/> to the outermost surface along <paramref name="direction"/> </summary>
    private static float Outermost(Vector3 direction)
    {
        for (float t = Reach; t > 0f; t -= Step)
        {
            if (Distance(Center + direction * t) >= 0f)
                continue;
            float outside = t + Step, inside = t;
            for (int i = 0; i < 16; i++)
            {
                float middle = (outside + inside) / 2f;
                if (Distance(Center + direction * middle) < 0f)
                    inside = middle;
                else
                    outside = middle;
            }
            return (outside + inside) / 2f;
        }
        return 0f;
    }

    private static Vector3 Normal(Vector3 p)
    {
        const float e = 1e-3f;
        var gradient = new Vector3(
            Distance(p + new Vector3(e, 0f, 0f)) - Distance(p - new Vector3(e, 0f, 0f)),
            Distance(p + new Vector3(0f, e, 0f)) - Distance(p - new Vector3(0f, e, 0f)),
            Distance(p + new Vector3(0f, 0f, e)) - Distance(p - new Vector3(0f, 0f, e)));
        return gradient.LengthSquared() > 1e-12f ? Vector3.Normalize(gradient) : Vector3.UnitY;
    }

    /// <summary> Signed distance to the smooth union of the capsules, negative inside </summary>
    private static float Distance(Vector3 p)
    {
        float distance = float.MaxValue;
        foreach (var (a, b, radius) in Capsules)
        {
            var ab = b - a;
            float t = Math.Clamp(Vector3.Dot(p - a, ab) / ab.LengthSquared(), 0f, 1f);
            float capsule = Vector3.Distance(p, a + ab * t) - radius;
            // Polynomial smooth minimum: the parts melt into each other over Blend
            if (distance == float.MaxValue)
            {
                distance = capsule;
                continue;
            }
            float h = Math.Clamp(0.5f + 0.5f * (distance - capsule) / Blend, 0f, 1f);
            distance = MathUtil.Lerp(distance, capsule, h) - Blend * h * (1f - h);
        }
        return distance;
    }
}
