// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using Stride.BepuPhysics.Definitions.Colliders;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Rendering.ProceduralModels;

namespace Stride.BepuPhysics.Soft.Tests.VideoDemo;

/// <summary>
/// A striped soft rope held at both ends by rigid handles, one <see cref="SoftBodyPin"/> per end anchored to its handle:
/// a kinematic disk turned by a motor twists it, a dynamic block pulled with the gravity gun stretches it.
/// </summary>
/// <remarks> Each end is a body anchor: the rope pulls on its handles as they pull on it, and its pinned particles never touch them </remarks>
internal sealed class SoftPiece
{
    private const float Radius = 0.17f, PinLength = 0.2f;

    public Entity Piece { get; }

    public BodyComponent Twister { get; }

    public BodyComponent Handle { get; }

    /// <summary> Turns of the twister on each side of its rest angle </summary>
    public float Turns { get; set; } = 2f;

    /// <summary> Seconds per back and forth of the twister </summary>
    public float Period { get; set; } = 9f;

    private readonly Vector3 _twisterAt;
    private readonly Vector3 _axis;
    private readonly Quaternion _twisterRest;

    public float Angle { get; private set; }

    public SoftPiece(VideoDemoGame game, Vector3 left, Vector3 right, Color color, Color stripe)
    {
        var device = game.GraphicsDevice;
        var post = Look.Paint(device, Look.Navy, 0.6f);
        _axis = Vector3.Normalize(right - left);
        _twisterAt = left;

        // The twister: a disk facing the rope, kinematic, on a post
        Look.StaticBox(game, new Vector3(0.16f, left.Y - 0.25f, 0.16f), new Vector3(left.X - _axis.X * 0.1f, (left.Y - 0.25f) / 2f, left.Z - _axis.Z * 0.1f), post, "twister post");
        _twisterRest = Quaternion.BetweenDirections(Vector3.UnitY, _axis);
        Twister = new BodyComponent { Kinematic = true, Collider = new CompoundCollider { Colliders = { new CylinderCollider { Radius = 0.25f, Length = 0.12f } } } };
        var twister = new Entity("twister") { new ModelComponent(Look.Model(game, new CylinderProceduralModel { Radius = 0.25f, Height = 0.12f, Tessellation = 40 }, Look.Wood, 0.6f)), Twister };
        twister.Transform.Position = left;
        twister.Transform.Rotation = _twisterRest;
        // A knob off the axis, so the turns show
        var knob = new Entity("twister knob") { new ModelComponent(Look.Model(game, new SphereProceduralModel { Radius = 0.05f, Tessellation = 16 }, Look.Navy, 0.6f)) };
        knob.Transform.Position = new Vector3(0.17f, -0.08f, 0f);
        twister.AddChild(knob);
        game.AddToScene(twister);

        // The handle: a dynamic block resting on a stand
        Look.StaticBox(game, new Vector3(0.36f, right.Y - 0.18f, 0.36f), new Vector3(right.X, (right.Y - 0.18f) / 2f, right.Z), post, "handle stand");
        Handle = new BodyComponent { Collider = new CompoundCollider { Colliders = { new BoxCollider { Size = new Vector3(0.36f), Mass = 3f } } } };
        var handle = new Entity("handle") { new ModelComponent(Look.Model(game, new CubeProceduralModel { Size = new Vector3(0.36f) }, Look.Wood, 0.6f)), Handle };
        handle.Transform.Position = right;
        handle.Transform.Rotation = _twisterRest;
        game.AddToScene(handle);

        // The rope runs along its local Y from the twister to the center of the block; each end is pinned to its handle
        float length = Vector3.Distance(left, right);
        float half = length / 2f;
        var body = new VolumetricSoftBodyComponent
        {
            Resolution = 14, Stiffness = 12f, ShearStiffness = 12f, VolumeStiffness = 10f, Damping = 0.3f, Mass = 1.5f,
            Pins =
            {
                new SoftBodyPin { Region = new BoundingBox(new Vector3(-1f, -half - 1f, -1f), new Vector3(1f, -half + PinLength, 1f)), Anchor = twister },
                new SoftBodyPin { Region = new BoundingBox(new Vector3(-1f, half - PinLength, -1f), new Vector3(1f, half + 1f, 1f)), Anchor = handle },
            },
        };
        Piece = new Entity("soft rope")
        {
            new ModelComponent(Look.Model(game, new CapsuleProceduralModel { Radius = Radius, Length = length - 2f * Radius, Tessellation = 28, LengthRings = 24 }, Look.Striped(device, color, stripe, 6))),
            body,
        };
        Piece.Transform.Position = (left + right) / 2f;
        Piece.Transform.Rotation = _twisterRest;
        game.AddToScene(Piece);
    }

    /// <summary> Turns the twister back and forth, <see cref="Turns"/> each way; call before each physics step </summary>
    public void Drive(float time)
    {
        Angle = Turns * MathUtil.TwoPi * MathF.Sin(time * MathUtil.TwoPi / Period);
        // A spin around the disk's own axis (its local Y), then its rest orientation
        Twister.SetTargetPose(_twisterAt, Quaternion.RotationMatrix(Matrix.RotationY(Angle) * Matrix.RotationQuaternion(_twisterRest)));
    }
}
