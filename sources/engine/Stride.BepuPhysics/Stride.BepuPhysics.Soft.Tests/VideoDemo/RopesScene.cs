// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using Stride.BepuPhysics.Definitions;
using Stride.BepuPhysics.Definitions.Colliders;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics.GeometricPrimitives;
using Stride.Input;
using Stride.Rendering.ProceduralModels;

namespace Stride.BepuPhysics.Soft.Tests.VideoDemo;

/// <summary>
/// Soft ropes tied to bodies both ways: a crane and its load, a ragdoll hanging by the ankle, a cable car, a swing,
/// and an all-soft Fall Guy bouncing on a trampoline
/// </summary>
internal sealed class RopesScene : VideoScene
{
    private static readonly Vector3 CraneFoot = new(-5.4f, 0f, -1.6f), Hanging = new(-2.4f, 0f, -1.6f), Cable = new(1.2f, 0f, -1.8f), Swing = new(5.0f, 0f, -1.4f), Trampoline = new(3.6f, 0f, 2.4f);

    private SwingingCrane? _crane;
    private SkeletonBodyComponent? _hanging;
    private BodyComponent? _cabin, _seat;
    private (Entity Entity, SkeletonBodyComponent Body, VolumetricSoftBodyComponent Flesh)? _jumper;

    public override string Title => "Ropes and cables";
    public override string Pr => "soft body pins as body anchors, AnchorNode on bones, PR #3528 and upcoming";
    public override string Description => "every rope pulls on what it is tied to: a crane, a ragdoll hung by the ankle, a cable car, a swing, a trampoline";
    public override Vector3 Eye => new(0.0f, 4.4f, 10.4f);
    public override Vector3 Target => new(0f, 1.2f, 0f);
    public override string SceneKeys => "I: push the swing  |  M: push the cable car  |  J: lift the Fall Guy above the trampoline  |  left click: grab the load, the ragdoll, the cabin, a rope";
    public override IReadOnlyList<SkeletonBodyMode> LegendModes => new[] { SkeletonBodyMode.Ragdoll, SkeletonBodyMode.Standing };
    public override string LegendCaption => "Mode color: the hanging ragdoll, the Fall Guy";

    public SwingingCrane? Crane => _crane;

    protected override void BuildScene(VideoDemoGame game)
    {
        var device = game.GraphicsDevice;
        var navy = Look.Paint(device, Look.Navy, 0.5f);

        _crane = new SwingingCrane(game, CraneFoot, Look.Sun, Look.Navy);

        _hanging = AddHangingRagdoll(game, Hanging);

        // A cable car: a cable between two pylons, the cabin hanging from its middle
        float span = 3.0f;
        foreach (var x in new[] { -span / 2f, span / 2f })
            Look.StaticBox(game, new Vector3(0.2f, 3.2f, 0.2f), Cable + new Vector3(x, 1.6f, 0f), navy, "pylon");
        var left = Look.StaticBox(game, new Vector3(0.3f, 0.12f, 0.3f), Cable + new Vector3(-span / 2f, 3.26f, 0f), navy, "pylon head");
        var right = Look.StaticBox(game, new Vector3(0.3f, 0.12f, 0.3f), Cable + new Vector3(span / 2f, 3.26f, 0f), navy, "pylon head");
        var cabinSize = new Vector3(0.6f, 0.5f, 0.5f);
        _cabin = new BodyComponent { Collider = new CompoundCollider { Colliders = { new BoxCollider { Size = cabinSize, Mass = 5f }, new BoxCollider { Size = new Vector3(0.06f, 0.3f, 0.06f), PositionLocal = new Vector3(0f, 0.38f, 0f), Mass = 0.2f } } } };
        var cabin = new Entity("cable car") { new ModelComponent(Look.Model(game, new CubeProceduralModel { Size = cabinSize }, Look.Coral, 0.6f)), _cabin };
        var hanger = new Entity("hanger") { new ModelComponent(Look.Model(game, new CubeProceduralModel { Size = new Vector3(0.06f, 0.3f, 0.06f) }, Look.Navy, 0.5f)) };
        hanger.Transform.Position = new Vector3(0f, 0.38f, 0f);
        cabin.AddChild(hanger);
        cabin.Transform.Position = Cable + new Vector3(0f, 3.26f - 0.53f, 0f);
        game.AddToScene(cabin);
        // Two ropes, each from a pylon head to the hanger: the cabin hangs where they meet
        Ropes.Add(game, Cable + new Vector3(-span / 2f, 3.26f, 0f), Cable + new Vector3(0f, 3.24f, 0f), 0.035f, Look.Ivory, left, cabin, mass: 1.5f, name: "cable");
        Ropes.Add(game, Cable + new Vector3(span / 2f, 3.26f, 0f), Cable + new Vector3(0f, 3.24f, 0f), 0.035f, Look.Ivory, right, cabin, mass: 1.5f, name: "cable");

        // A swing: a seat on two ropes from a bar
        foreach (var z in new[] { -0.6f, 0.6f })
            Look.StaticBox(game, new Vector3(0.12f, 2.8f, 0.12f), Swing + new Vector3(0f, 1.4f, z), navy, "swing post");
        var bar = Look.StaticBox(game, new Vector3(0.12f, 0.12f, 1.32f), Swing + new Vector3(0f, 2.86f, 0f), navy, "swing bar");
        var seatSize = new Vector3(0.36f, 0.06f, 0.7f);
        _seat = new BodyComponent { Collider = new CompoundCollider { Colliders = { new BoxCollider { Size = seatSize, Mass = 2f } } } };
        var seat = new Entity("swing seat") { new ModelComponent(Look.Model(game, new CubeProceduralModel { Size = seatSize }, Look.Wood, 0.5f)), _seat };
        seat.Transform.Position = Swing + new Vector3(0f, 0.6f, 0f);
        game.AddToScene(seat);
        foreach (var z in new[] { -0.3f, 0.3f })
            Ropes.Add(game, Swing + new Vector3(0f, 2.8f, z), Swing + new Vector3(0f, 0.62f, z), 0.03f, Look.Ivory, bar, seat, mass: 0.8f, name: "swing rope");

        // A trampoline: a cloth pinned on its four edges between four posts, an all-soft Fall Guy above it
        var size = 2.2f;
        foreach (var (x, z) in new[] { (-1f, -1f), (1f, -1f), (-1f, 1f), (1f, 1f) })
            Look.StaticBox(game, new Vector3(0.12f, 0.9f, 0.12f), Trampoline + new Vector3(x * (size / 2f + 0.08f), 0.45f, z * (size / 2f + 0.08f)), navy, "trampoline post");
        var edge = size / 2f - 0.06f;
        var trampoline = new ClothComponent
        {
            StretchStiffness = 60f, BendStiffness = 2f, Compression = 0.2f, Mass = 2f, AirDrag = 0f,
            Pins =
            {
                new SoftBodyPin { Region = new BoundingBox(new Vector3(-2f, -1f, -2f), new Vector3(-edge, 1f, 2f)) },
                new SoftBodyPin { Region = new BoundingBox(new Vector3(edge, -1f, -2f), new Vector3(2f, 1f, 2f)) },
                new SoftBodyPin { Region = new BoundingBox(new Vector3(-2f, -1f, -2f), new Vector3(2f, 1f, -edge)) },
                new SoftBodyPin { Region = new BoundingBox(new Vector3(-2f, -1f, edge), new Vector3(2f, 1f, 2f)) },
            },
        };
        var cloth = new Entity("trampoline") { new ModelComponent(Look.Model(game, new PlaneProceduralModel { Size = new Vector2(size), Tessellation = new Int2(20), Normal = NormalDirection.UpY, GenerateBackFace = true }, Look.Teal, 0.4f)), trampoline };
        cloth.Transform.Position = Trampoline + new Vector3(0f, 0.9f, 0f);
        game.AddToScene(cloth);
        var (jumper, jumperBody, flesh, _) = FallGuy.Add(game, Modes.Of(SkeletonBodyMode.Standing), Trampoline + new Vector3(0f, 2.6f, 0f), 0.3f);
        _jumper = (jumper, jumperBody, flesh);

    }

    /// <summary> A ragdoll hanging head down from a gallows at <paramref name="foot"/> by a rope tied to its left foot bone (AnchorNode) </summary>
    public static SkeletonBodyComponent AddHangingRagdoll(VideoDemoGame game, Vector3 foot)
    {
        var navy = Look.Paint(game.GraphicsDevice, Look.Navy, 0.5f);
        foreach (var x in new[] { -0.9f, 0.9f })
            Look.StaticBox(game, new Vector3(0.14f, 3.6f, 0.14f), foot + new Vector3(x, 1.8f, 0f), navy, "gallows post");
        var beam = Look.StaticBox(game, new Vector3(1.94f, 0.16f, 0.16f), foot + new Vector3(0f, 3.66f, 0f), navy, "gallows beam");
        // Head down: the feet, 0.06 m above the origin of the entity and 0.11 m to its side, are now below it and on the other side
        var (human, body, _) = Humans.Add(game, SkeletonBodyMode.Ragdoll, foot + new Vector3(0.11f, 2.6f, 0f), 0f, new ProceduralCharacter.Animator { Paused = true }, "hanging ragdoll", Quaternion.RotationZ(MathUtil.Pi));
        Ropes.Add(game, foot + new Vector3(0f, 3.58f, 0f), foot + new Vector3(0f, 2.54f, 0f), 0.045f, Look.Ivory, beam, human, toNode: "Foot.L", mass: 12f, stiffness: 40f, name: "ankle rope");
        return body;
    }

    public override void UpdateInput(VideoDemoGame game)
    {
        if (game.Input.IsKeyPressed(Keys.I) && _seat is not null)
        {
            _seat.LinearVelocity += new Vector3(3f, 0f, 0f);
            _seat.Awake = true;
        }
        if (game.Input.IsKeyPressed(Keys.M) && _cabin is not null)
        {
            _cabin.LinearVelocity += new Vector3(0f, 0f, 2.5f);
            _cabin.Awake = true;
        }
        if (game.Input.IsKeyPressed(Keys.J) && _jumper is { } jumper)
        {
            // Up above the middle of the trampoline, upright, at rest; the bones are rebuilt there
            jumper.Entity.Transform.Position = Trampoline + new Vector3(0f, 2.6f, 0f);
            jumper.Entity.Transform.Rotation = Quaternion.RotationY(0.3f);
            jumper.Flesh.Teleport(Trampoline + new Vector3(0f, 2.6f, 0f), Quaternion.RotationY(0.3f));
            jumper.Body.Rebuild();
        }
    }

    public override IEnumerable<(Vector3 World, string Text)> Labels()
    {
        yield return (CraneFoot + new Vector3(0.8f, 3.1f, 0f), "crane: load and jib pull on each other");
        yield return (Hanging + new Vector3(0f, 4.1f, 0f), "rope on a bone (AnchorNode Foot.L)");
        yield return (Cable + new Vector3(0f, 3.9f, 0f), "cable car (M)");
        yield return (Swing + new Vector3(0f, 3.3f, 0f), "swing (I)");
        yield return (Trampoline + new Vector3(0f, 3.4f, 0f), "trampoline (J: up again)");
    }

    public override IEnumerable<string> Status()
    {
        yield return string.Format(System.Globalization.CultureInfo.InvariantCulture, "crane jib at {0:0} deg  |  hanging ragdoll: hips at {1:0.00} m  |  Fall Guy {2}",
            _crane?.JibAngle ?? 0f, _hanging is { BodyCount: > 0 } h ? h.GetBodyPosition(0).Y : 0f,
            _jumper is { } j && j.Body.IsGrounded ? $"standing on {j.Body.Ground?.Entity.Name}" : "in the air");
    }
}
