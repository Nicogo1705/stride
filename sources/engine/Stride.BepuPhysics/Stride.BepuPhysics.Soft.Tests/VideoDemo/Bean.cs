// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using Stride.BepuPhysics.Definitions;
using Stride.BepuPhysics.Definitions.Colliders;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Harness.Demo;
using Stride.Rendering;
using Stride.Rendering.ProceduralModels;

namespace Stride.BepuPhysics.Soft.Tests.VideoDemo;

/// <summary>
/// A round, wobbly character: a soft bean of flesh following a physical skeleton (FollowSkeleton over a <see cref="SkeletonBodyComponent"/>),
/// with stubby rigid arms, legs and shorts linked to its bones, which wear the color of its mode.
/// </summary>
/// <remarks>
/// The flesh starts above the hips: Standing looks for the ground with a ray down from the hips that only skips the skeleton itself,
/// and would take flesh under the hips for the floor.
/// </remarks>
internal static class Bean
{
    public const int Hips = 1, Spine = 2, Head = 3, ArmL = 4, ArmR = 5, LegL = 6, FootL = 7, LegR = 8, FootR = 9;

    /// <summary> The index of the spine among the bodies, the one whose tilt says whether it stands </summary>
    public const int TrunkBody = 1;

    private const float FleshRadius = 0.32f, FleshLength = 0.36f, FleshBottom = 0.46f;

    public static ModelNodeDefinition[] Nodes() =>
    [
        Node("Root", -1, Vector3.Zero),
        Node("Hips", 0, new Vector3(0f, 0.32f, 0f)),
        Node("Spine", Hips, new Vector3(0f, 0.43f, 0f)),
        Node("Head", Spine, new Vector3(0f, 0.35f, 0f)),
        Node("Arm.L", Spine, new Vector3(0.34f, 0.04f, 0f)),
        Node("Arm.R", Spine, new Vector3(-0.34f, 0.04f, 0f)),
        Node("Leg.L", Hips, new Vector3(0.12f, -0.04f, 0f)),
        Node("Foot.L", LegL, new Vector3(0f, -0.2f, 0f)),
        Node("Leg.R", Hips, new Vector3(-0.12f, -0.04f, 0f)),
        Node("Foot.R", LegR, new Vector3(0f, -0.2f, 0f)),
    ];

    /// <summary> Bodies in this order: hips, spine, head, then the limbs </summary>
    public static List<BoneShape> Hitboxes()
    {
        var shapes = new List<BoneShape>
        {
            new() { NodeName = "Hips", Shape = new BoxCollider { Size = new Vector3(0.44f, 0.2f, 0.34f), PositionLocal = new Vector3(0f, 0.04f, 0f), Mass = 6f } },
            new() { NodeName = "Spine", Shape = new CapsuleCollider { Radius = 0.22f, Length = 0.2f, Mass = 7f } },
            new() { NodeName = "Head", Shape = new SphereCollider { Radius = 0.22f, PositionLocal = new Vector3(0f, 0.04f, 0f), Mass = 4f } },
        };
        foreach (var side in new[] { "L", "R" })
        {
            shapes.Add(new() { NodeName = $"Arm.{side}", Shape = new CapsuleCollider { Radius = 0.075f, Length = 0.16f, PositionLocal = new Vector3(0f, -0.12f, 0f), Mass = 1f } });
            shapes.Add(new() { NodeName = $"Leg.{side}", Shape = new CapsuleCollider { Radius = 0.08f, Length = 0.1f, PositionLocal = new Vector3(0f, -0.1f, 0f), Mass = 2f } });
            shapes.Add(new() { NodeName = $"Foot.{side}", Shape = new BoxCollider { Size = new Vector3(0.15f, 0.08f, 0.24f), PositionLocal = new Vector3(0f, -0.04f, 0.04f), Mass = 1f } });
        }
        return shapes;
    }

    /// <summary> A bean of <paramref name="flesh"/> color in <paramref name="mode"/>, its feet at <paramref name="position"/> </summary>
    public static (Entity Entity, SkeletonBodyComponent Body, Animator Animator) Add(DemoGame game, Color flesh, Vector3 position, float yaw, SkeletonBodyMode mode = SkeletonBodyMode.Standing, float time = 0f)
    {
        // Filtered before the first step: the bones start inside the flesh
        foreach (var simulation in game.Services.GetService<BepuConfiguration>()?.BepuSimulations ?? new List<BepuSimulation>())
            PhysicalWorm.Filter(simulation);

        var device = game.GraphicsDevice;
        var shape = new CapsuleProceduralModel { Radius = FleshRadius, Length = FleshLength, Tessellation = 28, LengthRings = 10, LocalOffset = new Vector3(0f, FleshBottom + FleshRadius + FleshLength / 2f, 0f) };
        var model = Look.Model(game, shape, flesh, 0.8f);
        model.Skeleton = new Skeleton { Nodes = Nodes() };

        var body = new SkeletonBodyComponent { Mode = mode, SelfCollision = false, CollisionLayer = PhysicalWorm.BoneLayer };
        ((BoneCollider)body.Collider).Bones.AddRange(Hitboxes());
        var soft = new VolumetricSoftBodyComponent { Resolution = 6, FollowSkeleton = true, SkeletonStiffness = 5f, SkeletonDamping = 0.3f, Mass = 3f, CollisionLayer = PhysicalWorm.FleshLayer };
        var animator = new Animator { Time = time };
        var modelComponent = new ModelComponent(model);
        var entity = new Entity("bean") { modelComponent, soft, body, animator };
        entity.Transform.Position = position;
        entity.Transform.Rotation = Quaternion.RotationY(yaw);

        // Rigid parts in the mode color, eyes on the head
        var parts = new List<ModelComponent>();
        void Part(string name, string node, PrimitiveProceduralModelBase partShape, Vector3 offset, Material? material = null)
        {
            var partModel = new ModelComponent(material is null ? Look.Model(game, partShape, Modes.Of(mode), 0.5f) : Look.Model(game, partShape, material));
            var part = new Entity(name) { partModel, new ModelNodeLinkComponent { Target = modelComponent, NodeName = node } };
            part.Transform.Position = offset;
            entity.AddChild(part);
            if (material is null)
                parts.Add(partModel);
        }
        Part("shorts", "Hips", new CylinderProceduralModel { Radius = 0.25f, Height = 0.2f, Tessellation = 32, Scale = new Vector3(1f, 1f, 0.8f) }, new Vector3(0f, 0.06f, 0f));
        foreach (var side in new[] { "L", "R" })
        {
            Part("arm", $"Arm.{side}", new CapsuleProceduralModel { Radius = 0.07f, Length = 0.16f, Tessellation = 16 }, new Vector3(0f, -0.12f, 0f));
            Part("leg", $"Leg.{side}", new CapsuleProceduralModel { Radius = 0.075f, Length = 0.1f, Tessellation = 16 }, new Vector3(0f, -0.1f, 0f));
            Part("shoe", $"Foot.{side}", new CubeProceduralModel { Size = new Vector3(0.14f, 0.08f, 0.22f) }, new Vector3(0f, -0.04f, 0.04f));
        }
        var white = Look.Paint(device, new Color(250, 250, 250), 0.9f);
        var black = Look.Paint(device, new Color(25, 25, 32), 0.95f);
        foreach (var x in new[] { -0.1f, 0.1f })
        {
            Part("eye", "Head", new SphereProceduralModel { Radius = 0.075f, Tessellation = 20 }, new Vector3(x, 0.06f, FleshRadius - 0.05f), white);
            Part("pupil", "Head", new SphereProceduralModel { Radius = 0.04f, Tessellation = 16 }, new Vector3(x * 1.05f, 0.06f, FleshRadius + 0.005f), black);
        }
        entity.Add(new ModeTint(body, parts));
        game.AddToScene(entity);
        return (entity, body, animator);
    }

    private static ModelNodeDefinition Node(string name, int parent, Vector3 position) => new()
    {
        Name = name,
        ParentIndex = parent,
        Transform = { Position = position, Rotation = Quaternion.Identity, Scale = Vector3.One },
        Flags = ModelNodeFlags.Default,
    };

    /// <summary> A waddle in place: short steps, the body rocking from side to side, arms flapping </summary>
    public sealed class Animator : SyncScript
    {
        private const float LegLength = 0.32f, Swing = 0.5f;

        public float Time { get; set; }

        /// <summary> Steps per second of each leg </summary>
        public float Speed { get; set; } = 1.3f;

        public override void Update()
        {
            Time += (float)Game.UpdateTime.Elapsed.TotalSeconds;
            Apply(Entity.Get<ModelComponent>()!.Skeleton.NodeTransformations, Time);
        }

        /// <summary> The forward speed of the hips over the planted foot </summary>
        public float StanceSpeed()
        {
            float omega = Speed * MathF.Tau;
            float phase = Time * omega;
            return LegLength * MathF.Cos(Swing * MathF.Sin(phase)) * Swing * omega * MathF.Abs(MathF.Cos(phase));
        }

        public void Apply(ModelNodeTransformation[] nodes, float time)
        {
            float phase = time * Speed * MathF.Tau;
            float swing = MathF.Sin(phase);
            nodes[Hips].Transform.Position = new Vector3(0f, 0.32f + 0.02f * MathF.Abs(MathF.Cos(phase)), 0f);
            nodes[Hips].Transform.Rotation = Quaternion.RotationZ(0.06f * swing);
            nodes[Spine].Transform.Rotation = Quaternion.RotationZ(-0.05f * swing);
            nodes[LegL].Transform.Rotation = Quaternion.RotationX(Swing * swing);
            nodes[LegR].Transform.Rotation = Quaternion.RotationX(-Swing * swing);
            nodes[ArmL].Transform.Rotation = Quaternion.RotationZ(0.5f + 0.35f * MathF.Sin(phase * 2f)) * Quaternion.RotationX(-0.3f * swing);
            nodes[ArmR].Transform.Rotation = Quaternion.RotationZ(-0.5f - 0.35f * MathF.Sin(phase * 2f + 1f)) * Quaternion.RotationX(0.3f * swing);
        }
    }
}
