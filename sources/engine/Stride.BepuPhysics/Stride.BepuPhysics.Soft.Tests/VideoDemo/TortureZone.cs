// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Globalization;
using Stride.BepuPhysics.Definitions.Colliders;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics.GeometricPrimitives;
using Stride.Rendering.ProceduralModels;

namespace Stride.BepuPhysics.Soft.Tests.VideoDemo;

/// <summary>
/// What soft bodies take, slowly and with force: a hydraulic press, rollers swallowing jellies, a sausage twisted without end by two motors,
/// a jelly quartered by four ropes, a curtain in front of a fan, a soft ball under a pile driver, a block stretched on a rack. All on timers
/// </summary>
internal sealed class TortureZone : Zone
{
    public const float TurnSpeed = 0.2f;
    private const float PullMax = 1.8f, PullSpeed = 0.25f, PullHold = 3f, PullRest = 2f;
    private const float RackPeriod = 20f, RackClosed = 1.0f, RackOpen = 4.5f;
    private const int MaxRolled = 3;
    private const float FanReach = 3f, FanRadius = 1.1f;

    private readonly Queue<Entity> _rolled = new();
    private Vector3 _press, _rollers, _quarter, _fan, _sausage, _driver, _rack;
    private float _turns, _pull, _rackSeparation = RackClosed;
    private int _dropped;

    public override string Name => "Torture lab";
    public override string Description => "a press, rollers, a sausage twisted by two motors, a jelly quartered by four ropes, a fan, a pile driver, a stretch rack";
    public override Vector3 Eye => Origin + new Vector3(0.2f, 5.2f, 11.5f);
    public override Vector3 Target => Origin + new Vector3(0f, 0.9f, 0f);

    /// <summary> Turns of each sausage motor so far, one way only </summary>
    public float Turns => _turns;

    /// <summary> Distance between the two handles of the stretch rack </summary>
    public float RackSeparation => _rackSeparation;

    public override void Build(VideoDemoGame game, WorldTerrain terrain)
    {
        _rolled.Clear();
        (_turns, _pull, _rackSeparation, _dropped) = (0f, 0f, RackClosed, 0);
        var o = Origin;
        (_press, _rollers, _quarter, _fan) = (o + new Vector3(-5f, 0f, -2.5f), o + new Vector3(-1.7f, 0f, -2.5f), o + new Vector3(1.7f, 0f, -2.5f), o + new Vector3(5f, 0f, -2.5f));
        (_sausage, _rack, _driver) = (o + new Vector3(-4f, 1.1f, 2.5f), o + new Vector3(0f, 0f, 2.5f), o + new Vector3(4f, 0f, 2.5f));
        var device = game.GraphicsDevice;
        var steel = Look.Paint(device, new Color(150, 156, 170), 0.85f, 0.6f);
        var navy = Look.Paint(device, Look.Navy, 0.5f);
        var press = _press;
        var rollersAt = _rollers;
        var sausageAt = _sausage;
        var driverAt = _driver;
        var rackAt = _rack;

        // The press: a block under a kinematic plate, a jelly cube on the block
        Look.StaticBox(game, new Vector3(1.4f, 0.4f, 1.4f), press + new Vector3(0f, 0.2f, 0f), navy, "press base");
        foreach (var x in new[] { -0.75f, 0.75f })
            Look.StaticBox(game, new Vector3(0.12f, 2.8f, 0.12f), press + new Vector3(x, 1.4f, 0f), steel, "press column");
        var plateSize = new Vector3(1.3f, 0.15f, 1.2f);
        var plate = Kit.Kinematic(game, "press plate", new CubeProceduralModel { Size = plateSize }, new BoxCollider { Size = plateSize }, steel, press + new Vector3(0f, 2.2f, 0f)).Get<BodyComponent>()!;
        Kit.AddJelly(game, new CubeProceduralModel { Size = new Vector3(0.7f), Subdivisions = 8 }, press + new Vector3(0f, 0.78f, 0f), 0, 5);

        // The rollers: two steel cylinders along Z turning inward over a narrow gap
        var rollers = new BodyComponent[2];
        for (int i = 0; i < 2; i++)
        {
            float x = i == 0 ? -0.4f : 0.4f;
            Look.StaticBox(game, new Vector3(0.1f, 1.25f, 0.1f), rollersAt + new Vector3(x, 0.62f, -0.55f), navy, "roller stand");
            Look.StaticBox(game, new Vector3(0.1f, 1.25f, 0.1f), rollersAt + new Vector3(x, 0.62f, 0.55f), navy, "roller stand");
            // Striped along their length, so the turning shows
            rollers[i] = Kit.Kinematic(game, "roller", new CylinderProceduralModel { Radius = 0.36f, Height = 0.9f, Tessellation = 48 }, new CylinderCollider { Radius = 0.36f, Length = 0.9f }, Look.Striped(device, new Color(150, 156, 170), Look.Coral, 8, 0.8f),
                rollersAt + new Vector3(x, 1.25f, 0f), Quaternion.RotationX(MathUtil.PiOverTwo)).Get<BodyComponent>()!;
        }
        DropJelly(game);

        // The quartering: a jelly held in the air by four ropes to four kinematic winches pulling outward
        var quartered = Kit.AddJelly(game, new CubeProceduralModel { Size = new Vector3(0.6f), Subdivisions = 8 }, _quarter + new Vector3(0f, 1.3f, 0f), 3, 5);
        var winches = new (BodyComponent Body, Vector3 Home, Vector3 Out)[4];
        for (int i = 0; i < 4; i++)
        {
            var corner = new Vector3(i % 2 == 0 ? -1f : 1f, 0f, i < 2 ? -1f : 1f);
            var rest = _quarter + corner * 1.15f + new Vector3(0f, 1.3f, 0f);
            Look.StaticBox(game, new Vector3(0.12f, 1.25f, 0.12f), _quarter + corner * 1.3f + new Vector3(0f, 0.62f, 0f), navy, "winch post");
            var winch = Kit.Kinematic(game, "winch", new CubeProceduralModel { Size = new Vector3(0.18f) }, new BoxCollider { Size = new Vector3(0.18f) }, steel, rest);
            winches[i] = (winch.Get<BodyComponent>()!, rest, corner);
            Ropes.Add(game, _quarter + new Vector3(0f, 1.3f, 0f) + corner * 0.28f, rest, 0.045f, Look.Ivory, quartered, winch, name: "quartering rope");
        }

        // The fan blows steadily on a curtain hanging from a bar
        Props.Pillar(game, _fan + new Vector3(-0.9f, 0f, 0f), 0.05f, 2.6f, Look.Navy);
        Props.Pillar(game, _fan + new Vector3(0.9f, 0f, 0f), 0.05f, 2.6f, Look.Navy);
        var curtain = new ClothComponent { Pins = { new SoftBodyPin { Region = new BoundingBox(new Vector3(-1f, 0.68f, -1f), new Vector3(1f, 1f, 1f)) } }, AirDrag = 0.4f, Turbulence = 0.9f };
        var curtainEntity = new Entity("curtain") { new ModelComponent(Look.Model(game, new PlaneProceduralModel { Size = new Vector2(1.7f, 1.4f), Tessellation = new Int2(16, 14), Normal = NormalDirection.UpZ, GenerateBackFace = true }, Look.Coral, 0.3f)), curtain };
        curtainEntity.Transform.Position = _fan + new Vector3(0f, 1.9f, 0f);
        game.AddToScene(curtainEntity);
        // The fan is a kinematic body the gravity gun can carry; it blows along its axis (local Y) inside a cone drawn see-through
        var fanBody = Kit.Kinematic(game, "fan", new CylinderProceduralModel { Radius = 0.45f, Height = 0.2f, Tessellation = 40 }, new CylinderCollider { Radius = 0.45f, Length = 0.2f }, steel,
            _fan + new Vector3(0f, 1.5f, -1.6f), Quaternion.RotationX(MathUtil.PiOverTwo));
        var blowZone = new Entity("fan blow zone") { new ModelComponent(Look.Model(game, new ConeProceduralModel { Radius = FanRadius, Height = FanReach, Tessellation = 32 }, Look.Ghost(device, Look.Teal, 0.12f))) };
        blowZone.Transform.Position = new Vector3(0f, 0.1f + FanReach / 2f, 0f);
        blowZone.Transform.Rotation = Quaternion.RotationX(MathF.PI);
        fanBody.AddChild(blowZone);
        var curtainCenter = _fan + new Vector3(0f, 1.5f, 0f);

        // The sausage twisted by two motors turning opposite ways; stiffer than a jelly so it fights back
        var (left, right) = (sausageAt - new Vector3(1.2f, 0f, 0f), sausageAt + new Vector3(1.2f, 0f, 0f));
        var twisters = new BodyComponent[2];
        for (int i = 0; i < 2; i++)
        {
            var at = i == 0 ? left : right;
            Look.StaticBox(game, new Vector3(0.14f, at.Y - 0.25f - o.Y, 0.14f), new Vector3(at.X + (i == 0 ? -0.1f : 0.1f), o.Y + (at.Y - 0.25f - o.Y) / 2f, at.Z), navy, "motor post");
            twisters[i] = Kit.Kinematic(game, "motor", new CylinderProceduralModel { Radius = 0.22f, Height = 0.1f, Tessellation = 40 }, new CylinderCollider { Radius = 0.22f, Length = 0.1f }, Look.Paint(device, Look.Wood, 0.6f), at, Quaternion.RotationZ(MathUtil.PiOverTwo)).Get<BodyComponent>()!;
            var knob = new Entity("motor knob") { new ModelComponent(Look.Model(game, new SphereProceduralModel { Radius = 0.045f, Tessellation = 16 }, Look.Navy, 0.6f)) };
            knob.Transform.Position = new Vector3(0.15f, i == 0 ? -0.07f : 0.07f, 0f);
            twisters[i].Entity.AddChild(knob);
        }
        float sausageLength = right.X - left.X;
        var sausage = new VolumetricSoftBodyComponent
        {
            Resolution = 16, Stiffness = 20f, ShearStiffness = 14f, VolumeStiffness = 16f, Damping = 0.35f, Mass = 1.2f,
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
        sausageEntity.Transform.Position = sausageAt;
        sausageEntity.Transform.Rotation = Quaternion.RotationZ(-MathUtil.PiOverTwo);
        game.AddToScene(sausageEntity);

        // The stretch rack: a soft block held at both ends by kinematic handles that slowly move apart, its volume kept by VolumeStiffness
        var rackY = 1.2f;
        float rackHalf = RackOpen / 2f + 0.3f;
        foreach (var x in new[] { -rackHalf, rackHalf })
            Look.StaticBox(game, new Vector3(0.16f, rackY - 0.3f, 0.16f), rackAt + new Vector3(x, (rackY - 0.3f) / 2f, 0f), navy, "rack post");
        Look.StaticBox(game, new Vector3(2f * rackHalf + 0.16f, 0.12f, 0.2f), rackAt + new Vector3(0f, rackY - 0.36f, -0.45f), navy, "rack rail");
        var handleSize = new Vector3(0.2f, 0.6f, 0.6f);
        var handles = new BodyComponent[2];
        for (int i = 0; i < 2; i++)
            handles[i] = Kit.Kinematic(game, "rack handle", new CubeProceduralModel { Size = handleSize }, new BoxCollider { Size = handleSize }, steel, rackAt + new Vector3((i == 0 ? -1f : 1f) * RackClosed / 2f, rackY, 0f)).Get<BodyComponent>()!;
        var block = new VolumetricSoftBodyComponent
        {
            Resolution = 8, Stiffness = 12f, ShearStiffness = 10f, VolumeStiffness = 40f, Damping = 0.35f, Mass = 1.5f,
            Pins =
            {
                new SoftBodyPin { Region = new BoundingBox(new Vector3(-1f, -1f, -1f), new Vector3(-0.48f, 1f, 1f)), Anchor = new BodyAnchor { Body = handles[0] } },
                new SoftBodyPin { Region = new BoundingBox(new Vector3(0.48f, -1f, -1f), new Vector3(1f, 1f, 1f)), Anchor = new BodyAnchor { Body = handles[1] } },
            },
        };
        var blockEntity = new Entity("stretched block")
        {
            new ModelComponent(Look.Model(game, new CubeProceduralModel { Size = new Vector3(1.2f, 0.5f, 0.5f), Subdivisions = 10 }, Look.Striped(device, Look.Violet, Look.Ivory, 6))),
            block,
        };
        blockEntity.Transform.Position = rackAt + new Vector3(0f, rackY, 0f);
        game.AddToScene(blockEntity);

        // The pile driver: a heavy kinematic block falling on a soft ball
        Look.StaticBox(game, new Vector3(1.0f, 0.1f, 1.0f), driverAt + new Vector3(0f, 0.05f, 0f), navy, "anvil");
        var hammerSize = new Vector3(0.8f, 0.5f, 0.8f);
        var hammer = Kit.Kinematic(game, "pile driver", new CubeProceduralModel { Size = hammerSize }, new BoxCollider { Size = hammerSize }, steel, driverAt + new Vector3(0f, 2.6f, 0f)).Get<BodyComponent>()!;
        Kit.AddSoftBall(game, driverAt + new Vector3(0f, 0.45f, 0f), 0.32f, Look.Sun, 6, 1f);

        float rollerAngle = 0f;
        game.AddToScene(new Entity("lab machines")
        {
            new StepDriver((time, step) =>
            {
                // Press, every 8 s: down in 3 s to 0.25 m above the block, held 1.5 s, up in 3 s
                float phase = time % 8f;
                float down = phase < 3f ? phase / 3f : phase < 4.5f ? 1f : phase < 7.5f ? 1f - (phase - 4.5f) / 3f : 0f;
                plate.SetTargetPose(press + new Vector3(0f, MathUtil.Lerp(2.2f, 0.55f, MathUtil.SmoothStep(down)), 0f));

                // Rollers spinning on their own axis (world Z), tops moving toward the gap: a calender
                rollerAngle += step * 2.5f;
                rollers[0].SetTargetPose(rollersAt + new Vector3(-0.4f, 1.25f, 0f), Quaternion.RotationX(MathUtil.PiOverTwo) * Quaternion.RotationZ(-rollerAngle));
                rollers[1].SetTargetPose(rollersAt + new Vector3(0.4f, 1.25f, 0f), Quaternion.RotationX(MathUtil.PiOverTwo) * Quaternion.RotationZ(rollerAngle));

                // Quartering: out to 1.8 m, held, released, rest
                _pull = Ramp(time, PullMax / PullSpeed, PullHold, PullRest) * PullMax;
                foreach (var (body, home, corner) in winches)
                    body.SetTargetPose(home + corner * _pull);

                // Sausage: the motors spin on the sausage's axis (world X), never stop nor reverse, the twist only builds up
                _turns = time * TurnSpeed;
                float angle = _turns * MathUtil.TwoPi;
                twisters[0].SetTargetPose(left, Quaternion.RotationZ(MathUtil.PiOverTwo) * Quaternion.RotationX(angle));
                twisters[1].SetTargetPose(right, Quaternion.RotationZ(MathUtil.PiOverTwo) * Quaternion.RotationX(-angle));

                // Stretch rack: the handles go from 1 m to 4.5 m apart and back
                _rackSeparation = MathUtil.Lerp(RackClosed, RackOpen, 0.5f - 0.5f * MathF.Cos(time * MathUtil.TwoPi / RackPeriod));
                handles[0].SetTargetPose(rackAt + new Vector3(-_rackSeparation / 2f, rackY, 0f));
                handles[1].SetTargetPose(rackAt + new Vector3(_rackSeparation / 2f, rackY, 0f));

                // Fan: a gale with gusts along its axis, only while the curtain is inside the cone, weaker far from the fan
                curtain.Wind = FanWind(fanBody.Transform, curtainCenter, 18f + 8f * MathF.Sin(time * 2.3f));

                // Pile driver, every 6 s: falls in 0.35 s, rests 0.8 s, rises in 3 s
                float drive = time % 6f;
                float fall = drive / 0.35f;
                float height = drive < 0.35f ? MathUtil.Lerp(2.6f, 0.62f, fall * fall) : drive < 1.15f ? 0.62f : MathUtil.Lerp(0.62f, 2.6f, Math.Min(1f, (drive - 1.15f) / 3f));
                hammer.SetTargetPose(driverAt + new Vector3(0f, height, 0f));
            }),
        });
    }

    /// <summary> The wind a fan blowing along its local Y gives at <paramref name="point"/>: full on the axis near the fan, fading to the cone's edge and its far end </summary>
    private static Vector3 FanWind(TransformComponent fan, Vector3 point, float speed)
    {
        var axis = Vector3.Transform(Vector3.UnitY, fan.Rotation);
        var toPoint = point - fan.Position;
        float along = Vector3.Dot(toPoint, axis);
        if (along <= 0f || along >= FanReach)
            return Vector3.Zero;
        float radius = 0.2f + FanRadius * along / FanReach;
        float off = (toPoint - along * axis).Length() / radius;
        if (off >= 1f)
            return Vector3.Zero;
        return axis * speed * (1f - 0.6f * along / FanReach) * (1f - off * off);
    }

    /// <summary> 0 to 1 in <paramref name="rise"/> s, held, back to 0 in <paramref name="rise"/> s, rested, over and over </summary>
    private static float Ramp(float time, float rise, float hold, float rest)
    {
        float t = time % (2f * rise + hold + rest);
        return t < rise ? t / rise : t < rise + hold ? 1f : t < 2f * rise + hold ? 1f - (t - rise - hold) / rise : 0f;
    }

    private void DropJelly(VideoDemoGame game)
    {
        _rolled.Enqueue(Kit.AddJelly(game, new SphereProceduralModel { Radius = 0.3f, Tessellation = 28 }, _rollers + new Vector3(0.02f * (_dropped % 3 - 1), 2.3f, 0f), 1 + _dropped, 5));
        _dropped++;
        if (_rolled.Count > MaxRolled)
            Remove(game, _rolled.Dequeue());
    }

    protected override void Tick(VideoDemoGame game, float time, float seconds)
    {
        if (Every(time, seconds, 6f))
            DropJelly(game);
    }

    public override IEnumerable<SceneLabel> Labels()
    {
        yield return new(_press + new Vector3(0f, 3.2f, 0f), "hydraulic press");
        yield return new(_rollers + new Vector3(0f, 2.9f, 0f), "rollers, a jelly every 6 s");
        yield return new(_quarter + new Vector3(0f, 2.4f, 0f), "4 ropes, body anchors");
        yield return new(_fan + new Vector3(0f, 3.4f, 0f), "fan: grab it, it blows inside the cone");
        yield return new(_sausage + new Vector3(0f, 0.7f, 0f), "two motors, opposite ways");
        yield return new(_rack + new Vector3(0f, 2.1f, 0f), "stretch rack: volume kept (VolumeStiffness)");
        yield return new(_driver + new Vector3(0f, 3.0f, 0f), "pile driver");
    }

    public override IEnumerable<string> Status()
    {
        yield return string.Format(CultureInfo.InvariantCulture, "sausage: {0:0.0} turns each way ({1} per second)  |  quartering ropes: {2:0.00} m out  |  rack: {3:0.00} m apart  |  {4} jellies dropped",
            _turns, TurnSpeed, _pull, _rackSeparation, _dropped);
    }
}
