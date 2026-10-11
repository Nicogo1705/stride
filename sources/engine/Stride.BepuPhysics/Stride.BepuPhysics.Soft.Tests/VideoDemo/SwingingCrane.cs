// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System;
using Stride.BepuPhysics.Constraints;
using Stride.BepuPhysics.Definitions.Colliders;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Rendering.ProceduralModels;

namespace Stride.BepuPhysics.Soft.Tests.VideoDemo;

/// <summary>
/// A jib turning freely on a mast, a soft rope hanging from its tip and a heavy ball at the end of the rope:
/// both ends of the rope are body anchors, so the swinging load turns the jib, and turning the jib swings the load.
/// </summary>
internal sealed class SwingingCrane
{
    private const float MastHeight = 2.4f, JibLength = 1.7f, RopeLength = 1.3f, RopeRadius = 0.06f, PinLength = 0.08f, BallRadius = 0.17f;

    public BodyComponent Jib { get; }

    public BodyComponent Load { get; }

    public VolumetricSoftBodyComponent Rope { get; }

    public SwingingCrane(VideoDemoGame game, Vector3 foot, Color jibColor, Color loadColor)
    {
        Props.Pillar(game, foot, 0.09f, MastHeight, Look.Navy, 0.6f);

        // The hinge holds the jib to a kinematic pivot hidden in the top of the mast
        var top = foot + new Vector3(0f, MastHeight, 0f);
        var pivot = new BodyComponent { Kinematic = true, Collider = new CompoundCollider { Colliders = { new SphereCollider { Radius = 0.03f } } } };
        var pivotEntity = new Entity("crane pivot") { pivot };
        pivotEntity.Transform.Position = top - new Vector3(0f, 0.05f, 0f);
        game.AddToScene(pivotEntity);

        var jibSize = new Vector3(JibLength, 0.12f, 0.12f);
        Jib = new BodyComponent { Collider = new CompoundCollider { Colliders = { new BoxCollider { Size = jibSize, Mass = 3f } } } };
        var jib = new Entity("crane jib") { new ModelComponent(Look.Model(game, new CubeProceduralModel { Size = jibSize }, jibColor, 0.6f)), Jib };
        var jibCenter = top + new Vector3(JibLength / 2f - 0.15f, 0.1f, 0f);
        jib.Transform.Position = jibCenter;
        jib.Add(new HingeConstraintComponent
        {
            A = pivot, B = Jib,
            LocalOffsetA = new Vector3(0f, 0.15f, 0f), LocalOffsetB = new Vector3(-(JibLength / 2f - 0.15f), 0f, 0f),
            LocalHingeAxisA = Vector3.UnitY, LocalHingeAxisB = Vector3.UnitY,
        });
        game.AddToScene(jib);

        // The load hangs under the rope, the pinned particles of the bottom end inside it
        var tip = jibCenter + new Vector3(JibLength / 2f - 0.08f, 0f, 0f);
        var ropeCenter = tip - new Vector3(0f, RopeLength / 2f, 0f);
        Load = new BodyComponent { Collider = new CompoundCollider { Colliders = { new SphereCollider { Radius = BallRadius, Mass = 6f } } } };
        var load = new Entity("crane load") { new ModelComponent(Look.Model(game, new SphereProceduralModel { Radius = BallRadius, Tessellation = 32 }, loadColor, 0.75f)), Load };
        load.Transform.Position = tip - new Vector3(0f, RopeLength + BallRadius - PinLength * 1.5f, 0f);
        game.AddToScene(load);

        float half = RopeLength / 2f;
        Rope = new VolumetricSoftBodyComponent
        {
            Resolution = 14, Stiffness = 30f, ShearStiffness = 20f, VolumeStiffness = 10f, Damping = 0.4f, Mass = 2.5f,
            Pins =
            {
                new SoftBodyPin { Region = new BoundingBox(new Vector3(-1f, half - PinLength, -1f), new Vector3(1f, half + 1f, 1f)), Anchor = jib },
                new SoftBodyPin { Region = new BoundingBox(new Vector3(-1f, -half - 1f, -1f), new Vector3(1f, -half + PinLength, 1f)), Anchor = load },
            },
        };
        var rope = new Entity("crane rope")
        {
            new ModelComponent(Look.Model(game, new CapsuleProceduralModel { Radius = RopeRadius, Length = RopeLength - 2f * RopeRadius, Tessellation = 12, LengthRings = 20 }, Look.Ivory, 0.4f)),
            Rope,
        };
        rope.Transform.Position = ropeCenter;
        game.AddToScene(rope);
    }

    /// <summary> The angle of the jib around the mast, in degrees </summary>
    public float JibAngle
    {
        get
        {
            var axis = Vector3.Transform(Vector3.UnitX, Jib.Orientation);
            return MathUtil.RadiansToDegrees(MathF.Atan2(-axis.Z, axis.X));
        }
    }
}
