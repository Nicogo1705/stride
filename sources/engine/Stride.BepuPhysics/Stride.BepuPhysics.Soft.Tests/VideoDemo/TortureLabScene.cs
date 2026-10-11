// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using Stride.BepuPhysics.Definitions.Colliders;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics.GeometricPrimitives;
using Stride.Input;
using Stride.Rendering;
using Stride.Rendering.Materials;
using Stride.Rendering.ProceduralModels;

namespace Stride.BepuPhysics.Soft.Tests.VideoDemo;

/// <summary>
/// What soft bodies take: a hydraulic press, two rollers swallowing jellies, a sausage twisted five turns by two motors,
/// a jelly quartered by four ropes, a curtain in front of a fan, a soft ball under a pile driver
/// </summary>
internal sealed class TortureLabScene : VideoScene
{
    public const float MaximumTurns = 5f;
    private static readonly Vector3 Press = new(-4.6f, 0f, -1.2f), Rollers = new(-1.6f, 0f, -1.2f), Quarter = new(1.6f, 0f, -1.2f), Fan = new(4.8f, 0f, -1.4f), Sausage = new(-2.6f, 1.1f, 2.0f), Driver = new(3.4f, 0f, 2.2f);
    private static readonly Color[] JellyColors = { new(255, 90, 140), new(130, 230, 100), new(255, 185, 40), new(150, 110, 255), new(70, 200, 240) };

    private bool _twisting, _pulling, _fanOn = true;
    private float _turns, _pull;
    private int _dropped;
    private ClothComponent? _curtain;

    public override string Title => "Torture lab";
    public override string Pr => "soft bodies under stress: body anchors, PR #3528";
    public override string Description => "a press, rollers, a sausage twisted by two motors, a jelly quartered by four ropes, a curtain in a gale, a pile driver";
    public override Vector3 Eye => new(0.2f, 4.6f, 9.4f);
    public override Vector3 Target => new(0f, 0.9f, 0f);
    public override string SceneKeys => "J: drop a jelly into the rollers  |  T: twist the sausage to 5 turns / untwist  |  Y: quartering ropes pull / release  |  W: fan on / off";

    public float Turns => _turns;

    public float Pull => _pull;

    public void SetTwisting(bool on) => _twisting = on;

    public void SetPulling(bool on) => _pulling = on;

    protected override void BuildScene(VideoDemoGame game)
    {
        (_twisting, _pulling, _fanOn, _turns, _pull, _dropped) = (false, false, true, 0f, 0f, 0);
        var device = game.GraphicsDevice;
        var steel = Look.Paint(device, new Color(150, 156, 170), 0.85f, 0.6f);
        var navy = Look.Paint(device, Look.Navy, 0.5f);

        // The press: a block under a kinematic plate coming down every 4 s, a jelly cube on the block
        Look.StaticBox(game, new Vector3(1.4f, 0.4f, 1.4f), Press + new Vector3(0f, 0.2f, 0f), navy, "press base");
        foreach (var x in new[] { -0.75f, 0.75f })
            Look.StaticBox(game, new Vector3(0.12f, 2.8f, 0.12f), Press + new Vector3(x, 1.4f, 0f), steel, "press column");
        var plateSize = new Vector3(1.3f, 0.15f, 1.2f);
        var plate = Kinematic(game, "press plate", new CubeProceduralModel { Size = plateSize }, new BoxCollider { Size = plateSize }, steel, Press + new Vector3(0f, 2.2f, 0f));
        AddJelly(game, new CubeProceduralModel { Size = new Vector3(0.7f), Subdivisions = 8 }, Press + new Vector3(0f, 0.78f, 0f), 0, 7);

        // The rollers: two steel cylinders along Z turning inward over a narrow gap
        var rollers = new BodyComponent[2];
        for (int i = 0; i < 2; i++)
        {
            Look.StaticBox(game, new Vector3(0.1f, 1.25f, 0.1f), Rollers + new Vector3(i == 0 ? -0.4f : 0.4f, 0.62f, -0.55f), navy, "roller stand");
            Look.StaticBox(game, new Vector3(0.1f, 1.25f, 0.1f), Rollers + new Vector3(i == 0 ? -0.4f : 0.4f, 0.62f, 0.55f), navy, "roller stand");
            rollers[i] = Kinematic(game, "roller", new CylinderProceduralModel { Radius = 0.36f, Height = 0.9f, Tessellation = 48 }, new CylinderCollider { Radius = 0.36f, Length = 0.9f }, steel,
                Rollers + new Vector3(i == 0 ? -0.4f : 0.4f, 1.25f, 0f), Quaternion.RotationX(MathUtil.PiOverTwo)).Get<BodyComponent>()!;
        }
        DropJelly(game);

        // The quartering: a jelly held in the air by four ropes to four kinematic winches pulling outward with Y
        var quartered = AddJelly(game, new CubeProceduralModel { Size = new Vector3(0.6f), Subdivisions = 8 }, Quarter + new Vector3(0f, 1.3f, 0f), 3, 5, gravity: true);
        var winches = new (BodyComponent Body, Vector3 Home, Vector3 Out)[4];
        for (int i = 0; i < 4; i++)
        {
            var corner = new Vector3(i % 2 == 0 ? -1f : 1f, 0f, i < 2 ? -1f : 1f);
            var rest = Quarter + corner * 1.15f + new Vector3(0f, 1.3f, 0f);
            var postFoot = Quarter + corner * 1.3f;
            Look.StaticBox(game, new Vector3(0.12f, 1.25f, 0.12f), postFoot + new Vector3(0f, 0.62f, 0f), navy, "winch post");
            var winch = Kinematic(game, "winch", new CubeProceduralModel { Size = new Vector3(0.18f) }, new BoxCollider { Size = new Vector3(0.18f) }, steel, rest);
            winches[i] = (winch.Get<BodyComponent>()!, rest, corner);
            var cornerOfJelly = Quarter + new Vector3(0f, 1.3f, 0f) + corner * 0.28f;
            Ropes.Add(game, cornerOfJelly, rest, 0.045f, Look.Ivory, quartered, winch, name: "quartering rope");
        }

        // The fan blows on a curtain hanging from a bar
        Props.Pillar(game, Fan + new Vector3(-0.9f, 0f, 0f), 0.05f, 2.6f, Look.Navy);
        Props.Pillar(game, Fan + new Vector3(0.9f, 0f, 0f), 0.05f, 2.6f, Look.Navy);
        _curtain = new ClothComponent { Pins = { new SoftBodyPin { Region = new BoundingBox(new Vector3(-1f, 0.68f, -1f), new Vector3(1f, 1f, 1f)) } }, AirDrag = 0.4f, Turbulence = 0.9f };
        var curtain = new Entity("curtain") { new ModelComponent(Look.Model(game, new PlaneProceduralModel { Size = new Vector2(1.7f, 1.4f), Tessellation = new Int2(24, 20), Normal = NormalDirection.UpZ, GenerateBackFace = true }, Look.Coral, 0.3f)), _curtain };
        curtain.Transform.Position = Fan + new Vector3(0f, 1.9f, 0f);
        game.AddToScene(curtain);
        var fanBody = new Entity("fan") { new ModelComponent(Look.Model(game, new CylinderProceduralModel { Radius = 0.45f, Height = 0.2f, Tessellation = 40 }, steel)) };
        fanBody.Transform.Position = Fan + new Vector3(0f, 1.5f, -1.6f);
        fanBody.Transform.Rotation = Quaternion.RotationX(MathUtil.PiOverTwo);
        game.AddToScene(fanBody);

        // The sausage twisted by two motors turning opposite ways
        var (left, right) = (Sausage - new Vector3(1.2f, 0f, 0f), Sausage + new Vector3(1.2f, 0f, 0f));
        var twisters = new BodyComponent[2];
        for (int i = 0; i < 2; i++)
        {
            var at = i == 0 ? left : right;
            Look.StaticBox(game, new Vector3(0.14f, at.Y - 0.25f, 0.14f), new Vector3(at.X + (i == 0 ? -0.1f : 0.1f), (at.Y - 0.25f) / 2f, at.Z), navy, "motor post");
            twisters[i] = Kinematic(game, "motor", new CylinderProceduralModel { Radius = 0.22f, Height = 0.1f, Tessellation = 40 }, new CylinderCollider { Radius = 0.22f, Length = 0.1f }, Look.Paint(device, Look.Wood, 0.6f), at, Quaternion.RotationZ(MathUtil.PiOverTwo)).Get<BodyComponent>()!;
            var knob = new Entity("motor knob") { new ModelComponent(Look.Model(game, new SphereProceduralModel { Radius = 0.045f, Tessellation = 16 }, Look.Navy, 0.6f)) };
            knob.Transform.Position = new Vector3(0.15f, i == 0 ? -0.07f : 0.07f, 0f);
            twisters[i].Entity.AddChild(knob);
        }
        float sausageLength = right.X - left.X;
        var sausage = new VolumetricSoftBodyComponent
        {
            Resolution = 16, Stiffness = 14f, ShearStiffness = 10f, VolumeStiffness = 12f, Damping = 0.35f, Mass = 1.2f,
            Pins =
            {
                new SoftBodyPin { Region = new BoundingBox(new Vector3(-1f, -sausageLength / 2f - 1f, -1f), new Vector3(1f, -sausageLength / 2f + 0.14f, 1f)), Anchor = new BodyAnchor { Body = twisters[0] } },
                new SoftBodyPin { Region = new BoundingBox(new Vector3(-1f, sausageLength / 2f - 0.14f, -1f), new Vector3(1f, sausageLength / 2f + 1f, 1f)), Anchor = new BodyAnchor { Body = twisters[1] } },
            },
        };
        var sausageEntity = new Entity("sausage")
        {
            new ModelComponent(Look.Model(game, new CapsuleProceduralModel { Radius = 0.16f, Length = sausageLength - 0.32f, Tessellation = 28, LengthRings = 24 }, Look.Striped(device, Look.Pink, Look.Ivory, 7))),
            sausage,
        };
        sausageEntity.Transform.Position = Sausage;
        sausageEntity.Transform.Rotation = Quaternion.RotationZ(-MathUtil.PiOverTwo);
        game.AddToScene(sausageEntity);

        // The pile driver: a heavy kinematic block falling on a soft ball every 3 s
        Look.StaticBox(game, new Vector3(1.0f, 0.1f, 1.0f), Driver + new Vector3(0f, 0.05f, 0f), navy, "anvil");
        var hammerSize = new Vector3(0.8f, 0.5f, 0.8f);
        var hammer = Kinematic(game, "pile driver", new CubeProceduralModel { Size = hammerSize }, new BoxCollider { Size = hammerSize }, steel, Driver + new Vector3(0f, 2.6f, 0f));
        var ball = new Entity("soft ball")
        {
            new ModelComponent(Look.Model(game, new SphereProceduralModel { Radius = 0.32f, Tessellation = 32 }, Look.Sun, 0.85f)),
            new VolumetricSoftBodyComponent { Resolution = 6, Stiffness = 16f, ShearStiffness = 14f, VolumeStiffness = 14f, Damping = 0.2f, Mass = 1f },
        };
        ball.Transform.Position = Driver + new Vector3(0f, 0.45f, 0f);
        game.AddToScene(ball);

        float rollerAngle = 0f;
        game.AddToScene(new Entity("lab machines")
        {
            new StepDriver((time, step) =>
            {
                // Press: down in 1.2 s to 0.25 m above the block, held 0.6 s, back up in 1.4 s
                float phase = time % 4f;
                float down = phase < 1.2f ? phase / 1.2f : phase < 1.8f ? 1f : phase < 3.2f ? 1f - (phase - 1.8f) / 1.4f : 0f;
                plate.Get<BodyComponent>()!.SetTargetPose(Press + new Vector3(0f, MathUtil.Lerp(2.2f, 0.55f, MathUtil.SmoothStep(down)), 0f));

                // Rollers turning inward, the left one clockwise seen from +Z
                rollerAngle += step * 2.4f;
                rollers[0].SetTargetPose(Rollers + new Vector3(-0.4f, 1.25f, 0f), Quaternion.RotationZ(-rollerAngle) * Quaternion.RotationX(MathUtil.PiOverTwo));
                rollers[1].SetTargetPose(Rollers + new Vector3(0.4f, 1.25f, 0f), Quaternion.RotationZ(rollerAngle) * Quaternion.RotationX(MathUtil.PiOverTwo));

                // Quartering: the winches go out by up to 0.8 m at 0.25 m/s, and come back
                _pull = Math.Clamp(_pull + (_pulling ? 0.25f : -0.25f) * step, 0f, 0.8f);
                foreach (var (body, home, corner) in winches)
                    body.SetTargetPose(home + corner * _pull);

                // Sausage: half a turn per second each way up to five turns, untwisting at the same pace
                _turns = Math.Clamp(_turns + (_twisting ? 0.5f : -0.5f) * step, 0f, MaximumTurns);
                float angle = _turns * MathUtil.TwoPi;
                twisters[0].SetTargetPose(left, Quaternion.RotationX(angle) * Quaternion.RotationZ(MathUtil.PiOverTwo));
                twisters[1].SetTargetPose(right, Quaternion.RotationX(-angle) * Quaternion.RotationZ(MathUtil.PiOverTwo));

                // Fan: a gale with gusts toward +Z
                if (_curtain is not null)
                    _curtain.Wind = _fanOn ? new Vector3(0f, 2f, 18f + 8f * MathF.Sin(time * 2.3f)) : Vector3.Zero;

                // Pile driver: falls in 0.25 s every 3 s, rests 0.4 s, rises in 2 s
                float drive = time % 3f;
                float height = drive < 0.25f ? MathUtil.Lerp(2.6f, 0.62f, drive / 0.25f * (drive / 0.25f)) : drive < 0.65f ? 0.62f : MathUtil.Lerp(0.62f, 2.6f, Math.Min(1f, (drive - 0.65f) / 2f));
                hammer.Get<BodyComponent>()!.SetTargetPose(Driver + new Vector3(0f, height, 0f));
            }),
        });
    }

    /// <summary> A kinematic body with a model, driven by SetTargetPose </summary>
    private static Entity Kinematic(VideoDemoGame game, string name, PrimitiveProceduralModelBase shape, ColliderBase collider, Material material, Vector3 position, Quaternion? rotation = null)
    {
        var entity = new Entity(name)
        {
            new ModelComponent(Look.Model(game, shape, material)),
            new BodyComponent { Kinematic = true, Collider = new CompoundCollider { Colliders = { collider } } },
        };
        entity.Transform.Position = position;
        entity.Transform.Rotation = rotation ?? Quaternion.Identity;
        game.AddToScene(entity);
        return entity;
    }

    private static Entity AddJelly(VideoDemoGame game, PrimitiveProceduralModelBase shape, Vector3 position, int color, int resolution, bool gravity = true)
    {
        var medium = color % 2 == 0 ? MaterialVolumeMedium.Scattering : MaterialVolumeMedium.Absorbing;
        var jelly = new Entity("jelly")
        {
            new ModelComponent(Look.Model(game, shape, Look.Volume(game.GraphicsDevice, JellyColors[color % JellyColors.Length], 0.85f, 0.3f, medium))),
            new VolumetricSoftBodyComponent { Resolution = resolution, Stiffness = 10f, ShearStiffness = 9f, VolumeStiffness = 12f, Damping = 0.3f, Mass = 1.5f, Gravity = gravity },
        };
        jelly.Transform.Position = position;
        game.AddToScene(jelly);
        return jelly;
    }

    private void DropJelly(VideoDemoGame game)
    {
        AddJelly(game, new SphereProceduralModel { Radius = 0.3f, Tessellation = 28 }, Rollers + new Vector3(0.02f * (_dropped % 3 - 1), 2.3f, 0f), 1 + _dropped, 6);
        _dropped++;
    }

    public override void UpdateInput(VideoDemoGame game)
    {
        if (game.Input.IsKeyPressed(Keys.J))
            DropJelly(game);
        if (game.Input.IsKeyPressed(Keys.T))
            _twisting = !_twisting;
        if (game.Input.IsKeyPressed(Keys.Y))
            _pulling = !_pulling;
        if (game.Input.IsKeyPressed(Keys.W))
            _fanOn = !_fanOn;
    }

    public override IEnumerable<(Vector3 World, string Text)> Labels()
    {
        yield return (Press + new Vector3(0f, 3.2f, 0f), "hydraulic press");
        yield return (Rollers + new Vector3(0f, 2.9f, 0f), "rollers (J: another jelly)");
        yield return (Quarter + new Vector3(0f, 2.4f, 0f), "4 ropes, body anchors (Y)");
        yield return (Fan + new Vector3(0f, 3.4f, 0f), "fan (W)");
        yield return (Sausage + new Vector3(0f, 0.7f, 0f), "two motors, opposite ways (T)");
        yield return (Driver + new Vector3(0f, 3.0f, 0f), "pile driver");
    }

    public override IEnumerable<string> Status()
    {
        yield return string.Format(System.Globalization.CultureInfo.InvariantCulture, "sausage: {0:0.0} / {1} turns each way ({2})  |  quartering ropes: {3:0.00} m out ({4})  |  fan {5}  |  {6} {7} dropped",
            _turns, MaximumTurns, _twisting ? "twisting" : "untwisting", _pull, _pulling ? "pulling" : "releasing", _fanOn ? "on" : "off", _dropped, _dropped == 1 ? "jelly" : "jellies");
    }
}
