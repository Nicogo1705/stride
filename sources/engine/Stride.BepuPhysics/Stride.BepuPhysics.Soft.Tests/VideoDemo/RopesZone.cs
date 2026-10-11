// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Collections.Generic;
using System.Globalization;
using Stride.BepuPhysics.Definitions.Colliders;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics.GeometricPrimitives;
using Stride.Rendering.ProceduralModels;

namespace Stride.BepuPhysics.Soft.Tests.VideoDemo;

/// <summary>
/// Every rope pulls on what it is tied to: a crane whose load swings the jib, a cable car pushed along its cable, a swing on short stiff ropes,
/// soft balls thrown up from a trampoline. Pushes come on timers
/// </summary>
internal sealed class RopesZone : Zone
{
    private const float SwingHeight = 2.2f;
    private readonly List<VolumetricSoftBodyComponent> _balls = new();
    private Vector3 _craneFoot, _cable, _swing, _trampoline;
    private SwingingCrane? _crane;
    private BodyComponent? _cabin, _seat;
    private int _pushes;

    public override string Name => "Ropes and cables";
    public override string Description => "pins on a BodyAnchor or a WorldAnchor: a crane, a cable car, a swing, a trampoline";
    public override Vector3 Eye => Origin + new Vector3(0f, 4.4f, 10.4f);
    public override Vector3 Target => Origin + new Vector3(0f, 1.2f, 0f);

    public override void Build(VideoDemoGame game, WorldTerrain terrain)
    {
        _balls.Clear();
        _pushes = 0;
        var o = Origin;
        (_craneFoot, _cable, _swing, _trampoline) = (o + new Vector3(-5f, 0f, -1.5f), o + new Vector3(-1f, 0f, -1.8f), o + new Vector3(3.5f, 0f, -1.4f), o + new Vector3(3f, 0f, 3f));
        var navy = Look.Paint(game.GraphicsDevice, Look.Navy, 0.5f);

        _crane = new SwingingCrane(game, _craneFoot, Look.Sun, Look.Navy);

        // A cable car: two ropes from the pylon heads meet at the hanger of the cabin
        float span = 3.0f;
        foreach (var x in new[] { -span / 2f, span / 2f })
            Look.StaticBox(game, new Vector3(0.2f, 3.2f, 0.2f), _cable + new Vector3(x, 1.6f, 0f), navy, "pylon");
        var left = Look.StaticBox(game, new Vector3(0.3f, 0.12f, 0.3f), _cable + new Vector3(-span / 2f, 3.26f, 0f), navy, "pylon head");
        var right = Look.StaticBox(game, new Vector3(0.3f, 0.12f, 0.3f), _cable + new Vector3(span / 2f, 3.26f, 0f), navy, "pylon head");
        var cabinSize = new Vector3(0.6f, 0.5f, 0.5f);
        _cabin = new BodyComponent { Collider = new CompoundCollider { Colliders = { new BoxCollider { Size = cabinSize, Mass = 5f }, new BoxCollider { Size = new Vector3(0.06f, 0.3f, 0.06f), PositionLocal = new Vector3(0f, 0.38f, 0f), Mass = 0.2f } } } };
        var cabin = new Entity("cable car") { new ModelComponent(Look.Model(game, new CubeProceduralModel { Size = cabinSize }, Look.Coral, 0.6f)), _cabin };
        var hanger = new Entity("hanger") { new ModelComponent(Look.Model(game, new CubeProceduralModel { Size = new Vector3(0.06f, 0.3f, 0.06f) }, Look.Navy, 0.5f)) };
        hanger.Transform.Position = new Vector3(0f, 0.38f, 0f);
        cabin.AddChild(hanger);
        cabin.Transform.Position = _cable + new Vector3(0f, 3.26f - 0.53f, 0f);
        game.AddToScene(cabin);
        Ropes.Add(game, _cable + new Vector3(-span / 2f, 3.26f, 0f), _cable + new Vector3(0f, 3.24f, 0f), 0.035f, Look.Ivory, left, cabin, mass: 1.5f, name: "cable");
        Ropes.Add(game, _cable + new Vector3(span / 2f, 3.26f, 0f), _cable + new Vector3(0f, 3.24f, 0f), 0.035f, Look.Ivory, right, cabin, mass: 1.5f, name: "cable");

        // A swing: a seat on two short, stiff ropes from a low bar
        foreach (var z in new[] { -0.6f, 0.6f })
            Look.StaticBox(game, new Vector3(0.12f, SwingHeight, 0.12f), _swing + new Vector3(0f, SwingHeight / 2f, z), navy, "swing post");
        var bar = Look.StaticBox(game, new Vector3(0.12f, 0.12f, 1.32f), _swing + new Vector3(0f, SwingHeight + 0.06f, 0f), navy, "swing bar");
        var seatSize = new Vector3(0.36f, 0.06f, 0.7f);
        _seat = new BodyComponent { Collider = new CompoundCollider { Colliders = { new BoxCollider { Size = seatSize, Mass = 2f } } } };
        var seat = new Entity("swing seat") { new ModelComponent(Look.Model(game, new CubeProceduralModel { Size = seatSize }, Look.Wood, 0.5f)), _seat };
        seat.Transform.Position = _swing + new Vector3(0f, 0.6f, 0f);
        game.AddToScene(seat);
        foreach (var z in new[] { -0.3f, 0.3f })
            Ropes.Add(game, _swing + new Vector3(0f, SwingHeight, z), _swing + new Vector3(0f, 0.62f, z), 0.03f, Look.Ivory, bar, seat, mass: 0.8f, stiffness: 60f, name: "swing rope");

        // A trampoline: a cloth pinned on its four edges between four posts, two soft balls on it
        var size = 2.2f;
        foreach (var (x, z) in new[] { (-1f, -1f), (1f, -1f), (-1f, 1f), (1f, 1f) })
            Look.StaticBox(game, new Vector3(0.12f, 0.9f, 0.12f), _trampoline + new Vector3(x * (size / 2f + 0.08f), 0.45f, z * (size / 2f + 0.08f)), navy, "trampoline post");
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
        cloth.Transform.Position = _trampoline + new Vector3(0f, 0.9f, 0f);
        game.AddToScene(cloth);
        _balls.Add(Kit.AddSoftBall(game, _trampoline + new Vector3(-0.35f, 1.8f, 0.1f), 0.3f, Look.Coral, 6, 1f).Get<VolumetricSoftBodyComponent>()!);
        _balls.Add(Kit.AddSoftBall(game, _trampoline + new Vector3(0.4f, 2.6f, -0.2f), 0.24f, Look.Sun, 6, 0.7f).Get<VolumetricSoftBodyComponent>()!);
    }

    protected override void Tick(VideoDemoGame game, float time, float seconds)
    {
        if (Every(time, seconds, 7f, 2f) && _cabin is not null)
        {
            // Along the cable, one way then the other
            _cabin.LinearVelocity += new Vector3(0f, 0f, _pushes++ % 2 == 0 ? 2.5f : -2.5f);
            _cabin.Awake = true;
        }
        if (Every(time, seconds, 6f, 1f) && _seat is not null)
        {
            _seat.LinearVelocity += new Vector3(2.5f, 0f, 0f);
            _seat.Awake = true;
        }
        if (Every(time, seconds, 7f, 4f))
        {
            foreach (var ball in _balls)
            {
                // A ball that left the trampoline comes back above it first
                var at = ball.GetParticlePosition(0);
                if (new Vector2(at.X - _trampoline.X, at.Z - _trampoline.Z).Length() > 1.3f || at.Y < _trampoline.Y + 0.5f)
                    ball.Teleport(_trampoline + new Vector3(0f, 2.2f, 0f), Quaternion.Identity);
                else
                    ball.LinearVelocity += new Vector3(0f, 4.5f, 0f);
            }
        }
    }

    public override IEnumerable<SceneLabel> Labels()
    {
        yield return new(_craneFoot + new Vector3(0.8f, 3.1f, 0f), "crane: load and jib pull on each other");
        yield return new(_cable + new Vector3(0f, 3.9f, 0f), "cable car, pushed every 7 s");
        yield return new(_swing + new Vector3(0f, 2.8f, 0f), "swing, short stiff ropes");
        yield return new(_trampoline + new Vector3(0f, 2.9f, 0f), "trampoline: cloth pinned on four edges");
    }

    public override IEnumerable<string> Status()
    {
        yield return string.Format(CultureInfo.InvariantCulture, "crane jib at {0:0} deg  |  cable car pushed {1} times  |  swing seat at {2:0.00} m",
            _crane?.JibAngle ?? 0f, _pushes, _seat?.Position.Y - Origin.Y ?? 0f);
    }
}
