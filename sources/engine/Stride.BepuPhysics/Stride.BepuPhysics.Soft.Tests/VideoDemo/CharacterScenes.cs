// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Stride.BepuPhysics.Definitions;
using Stride.BepuPhysics.Definitions.Colliders;
using Stride.BepuPhysics.Components;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Harness.Demo;
using Stride.Input;
using Stride.Rendering.ProceduralModels;

namespace Stride.BepuPhysics.Soft.Tests.VideoDemo;

/// <summary> Characters made in code with hitboxes on their bones: one kicks balls, two are hit by balls and go Ragdoll or Active </summary>
internal sealed class HitboxScene : VideoScene
{
    private readonly List<(Entity Character, SkeletonBodyComponent Body, string Text)> _characters = new();

    public override string Title => "Hitboxes d'os et ragdoll (passif, actif)";
    public override string Pr => "PR à venir (sur #3528)";
    public override string Description => "au centre, les hitboxes animées frappent la balle ; à gauche Ragdoll, à droite Active (les servos tiennent la pose) ; V : voir les hitboxes";
    public override Vector3 Eye => new(0.4f, 2.3f, 6.4f);
    public override Vector3 Target => new(0f, 0.85f, -0.6f);
    public override string SceneKeys => "V : hitboxes en wireframe  |  M : mode de tous (Animated / Ragdoll / Active / Standing)  |  clic sur un os : le perso passe en Ragdoll";

    protected override void BuildScene(VideoDemoGame game)
    {
        _characters.Clear();

        // The kicker, turned so the ball leaves toward the back of the scene
        var kickerBody = ProceduralCharacter.CreateHitboxes();
        var animator = new ProceduralCharacter.Animator { Kick = true };
        var kicker = ProceduralCharacter.Create(ProceduralCharacter.CreateModel(game.GraphicsDevice, Look.Paint(game.GraphicsDevice, Look.Blue, 0.5f)), animator, kickerBody);
        kicker.Name = "frappeur";
        kicker.Transform.Position = new Vector3(0f, 0f, -1.2f);
        kicker.Transform.Rotation = Quaternion.RotationY(MathUtil.Pi * 0.82f);
        game.AddToScene(kicker);
        game.AddToScene(new Entity { new KickBall(game, kicker, animator) });
        _characters.Add((kicker, kickerBody, "Animated : hitboxes cinématiques"));

        foreach (var (mode, x, color) in new[] { (SkeletonBodyMode.Ragdoll, -2.7f, Look.Coral), (SkeletonBodyMode.Active, 2.7f, Look.Teal) })
        {
            var body = ProceduralCharacter.CreateHitboxes();
            var character = ProceduralCharacter.Create(ProceduralCharacter.CreateModel(game.GraphicsDevice, Look.Paint(game.GraphicsDevice, color, 0.5f)), new ProceduralCharacter.Animator { Time = x }, body);
            character.Name = mode.ToString();
            character.Transform.Position = new Vector3(x, 0f, -0.4f);
            game.AddToScene(character);
            game.AddToScene(new Entity { new HitReplay(game, character, body, mode, x > 0 ? 3f : 0f) });
            _characters.Add((character, body, mode == SkeletonBodyMode.Ragdoll ? "Ragdoll, puis retour à l'animation" : "Active : servos vers la pose animée"));
        }
    }

    public override void UpdateInput(VideoDemoGame game)
    {
        if (game.Input.IsKeyPressed(Keys.M) && _characters.Count > 0)
        {
            var next = (SkeletonBodyMode)(((int)_characters[1].Body.Mode + 1) % 4);
            foreach (var (_, body, _) in _characters)
                body.Mode = next;
        }
    }

    public override IEnumerable<(Vector3 World, string Text)> Labels()
    {
        foreach (var (character, body, text) in _characters)
        {
            var at = character.Transform.Position;
            yield return (new Vector3(at.X, 2.25f, at.Z), text);
        }
    }

    public override IEnumerable<string> Status()
    {
        if (_characters.Count == 3)
            yield return string.Format(CultureInfo.InvariantCulture, "modes : {0} / {1} / {2}  |  {3} corps par perso", _characters[0].Body.Mode, _characters[1].Body.Mode, _characters[2].Body.Mode, _characters[0].Body.BodyCount);
    }

    /// <summary> A new ball in front of the kicking foot at the start of each kick of the animator </summary>
    private sealed class KickBall(DemoGame game, Entity kicker, ProceduralCharacter.Animator animator) : SyncScript
    {
        private int _cycle = int.MinValue;
        private Entity? _ball;

        public override void Update()
        {
            int cycle = (int)MathF.Floor(animator.Time / animator.KickPeriod);
            if (cycle == _cycle)
                return;
            _cycle = cycle;
            if (_ball is not null)
                Entity.Scene?.Entities.Remove(_ball);
            var local = new Vector3(-0.11f, 0.12f, 0.36f);
            _ball = Props.Ball(game, 0.12f, 0.45f, Look.Sun, kicker.Transform.Position + Vector3.Transform(local, kicker.Transform.Rotation));
            game.AddToScene(_ball);
        }
    }

    /// <summary> Throws a heavy ball at the walking character every 6 s and switches its mode as the ball arrives; back to Animated before the next throw </summary>
    private sealed class HitReplay(DemoGame game, Entity character, SkeletonBodyComponent body, SkeletonBodyMode mode, float delay) : SyncScript
    {
        private static readonly Vector3 BallStart = new(-2.6f, 1.6f, 2.6f), BallVelocity = new(6.36f, 1.65f, -6.36f);
        private float _time = 6f - delay;
        private BodyComponent? _ball;
        private bool _launch;

        public override void Update()
        {
            _time += (float)Game.UpdateTime.Elapsed.TotalSeconds;
            var origin = character.Transform.Position;
            if (_time > 6f)
            {
                _time = 0f;
                body.Mode = SkeletonBodyMode.Animated;
                if (_ball is null)
                {
                    var entity = Props.Ball(game, 0.18f, 15f, Look.Navy, origin + BallStart);
                    _ball = entity.Get<BodyComponent>();
                    game.AddToScene(entity);
                }
                else
                {
                    _ball.Teleport(origin + BallStart, Quaternion.Identity);
                }
                _launch = true;
                return;
            }

            if (_launch && _ball is { Simulation: not null })
            {
                _ball.AngularVelocity = Vector3.Zero;
                _ball.LinearVelocity = BallVelocity;
                _ball.Awake = true;
                _launch = false;
            }

            var chest = character.Get<ModelComponent>()!.Skeleton.NodeTransformations[ProceduralCharacter.Chest].WorldMatrix.TranslationVector;
            if (body.Mode != mode && body.Mode == SkeletonBodyMode.Animated && _ball is { Simulation: not null } ball && Vector3.Distance(ball.Position, chest) < 0.6f && _time < 3f)
                body.Mode = mode;
        }
    }
}

/// <summary>
/// Standing ragdolls: they walk around, take cannon balls, fall and get back up where they land, stand on crates;
/// the one furthest away is dropped from 6 m from time to time and lands on its feet.
/// </summary>
internal sealed class StandingScene : VideoScene
{
    private readonly List<Walker> _walkers = new();
    private Cannon? _cannon;

    public override string Title => "Ragdoll Standing";
    public override string Pr => "PR à venir (sur #3528)";
    public override string Description => "ils marchent, encaissent les boulets, tombent et se relèvent là où ils atterrissent ; lâchés de 6 m, ils retombent sur leurs pieds";
    public override Vector3 Eye => new(0.5f, 3.6f, 8.6f);
    public override Vector3 Target => new(0f, 0.9f, 0f);
    public override string SceneKeys => "clic sur un os : attraper, lancer (F) ; relâché, il se relève  |  B : boulet maintenant  |  L : lâcher le plus proche de 6 m";

    protected override void BuildScene(VideoDemoGame game)
    {
        _walkers.Clear();
        var colors = new[] { Look.Coral, Look.Teal, Look.Sun, Look.Violet, Look.Mint };

        // Two crates: a static one and a 100 kg dynamic one, a character dropped on each
        foreach (var dynamic in new[] { false, true })
        {
            float x = dynamic ? 1.6f : -1.6f;
            var collider = new CompoundCollider { Colliders = { new BoxCollider { Size = new Vector3(1.1f, 0.5f, 1.1f), Mass = 100f } } };
            CollidableComponent crate = dynamic ? new BodyComponent { Collider = collider } : new StaticComponent { Collider = collider };
            var crateEntity = new Entity(dynamic ? "caisse dynamique (100 kg)" : "caisse statique") { new ModelComponent(Look.Model(game, new CubeProceduralModel { Size = new Vector3(1.1f, 0.5f, 1.1f) }, dynamic ? Look.Wood : Look.Ivory, 0.35f)), crate };
            crateEntity.Transform.Position = new Vector3(x, 0.25f, 0f);
            game.AddToScene(crateEntity);
        }

        var spots = new[] { new Vector3(-1.6f, 0.9f, 0f), new Vector3(1.6f, 0.9f, 0f), new Vector3(-2.8f, 0f, 2.2f), new Vector3(2.6f, 0f, 2.4f), new Vector3(0f, 0f, -2.6f) };
        for (int i = 0; i < spots.Length; i++)
        {
            var body = ProceduralCharacter.CreateHitboxes();
            body.Mode = SkeletonBodyMode.Standing;
            var animator = new ProceduralCharacter.Animator { Time = i * 0.41f };
            var character = ProceduralCharacter.Create(ProceduralCharacter.CreateModel(game.GraphicsDevice, Look.Paint(game.GraphicsDevice, colors[i], 0.5f)), animator, body);
            character.Name = $"perso {i + 1}";
            character.Transform.Position = spots[i];
            character.Transform.Rotation = Quaternion.RotationY(i * 1.3f);
            game.AddToScene(character);
            // The two on the crates stay there, the others walk
            var walker = new Walker(character, body, animator, i * 1.3f, walks: i >= 2, Vector3.Zero, 4.2f);
            character.Add(walker);
            _walkers.Add(walker);
        }

        _cannon = Cannon.Build(game, _walkers, new Vector3(-5.5f, 0.75f, -4.5f), 4f);
        game.AddToScene(new Entity("ascenseur") { new Lifter(_walkers, 9f) });
    }

    public override void UpdateInput(VideoDemoGame game)
    {
        if (game.Input.IsKeyPressed(Keys.B))
            _cannon?.FireNow();
        if (game.Input.IsKeyPressed(Keys.L) && _walkers.Count > 0)
        {
            var eye = game.Scenes.Count > 0 ? Eye : Vector3.Zero;
            _walkers.OrderBy(w => Vector3.Distance(w.Hips, eye)).First().DropFrom(6f);
        }
    }

    public override IEnumerable<(Vector3 World, string Text)> Labels()
    {
        yield return (new Vector3(-1.6f, 0f, 0.75f), "caisse statique");
        yield return (new Vector3(1.6f, 0f, 0.75f), "caisse dynamique 100 kg");
    }

    public override IEnumerable<string> Status()
    {
        var grounded = _walkers.Count(w => w.Body.IsGrounded);
        yield return $"{_walkers.Count} persos en Standing, {grounded} au sol  |  boulet toutes les 4 s (B : maintenant)  |  lâcher de 6 m toutes les 9 s (L)";
    }
}

/// <summary>
/// Walks a Standing character around. The animation walks in place: the foot on the ground slides back under the hips at a speed
/// that changes through the stride, so the hips and upper body get exactly that speed forward, while the legs are left to their servos
/// and the friction of the floor; the stance foot then stays planted. The entity turns toward a heading the root servo follows, and stays under the hips.
/// Off when it is grabbed, lying or in the air.
/// </summary>
internal sealed class Walker(Entity character, SkeletonBodyComponent body, ProceduralCharacter.Animator animator, float heading, bool walks, Vector3 center, float radius) : ScriptComponent, ISimulationUpdate
{
    private float _heading = heading;
    private float _time;
    private bool[]? _upper;

    public SkeletonBodyComponent Body => body;

    public Entity Character => character;

    public Vector3 Hips => body.BodyCount > 0 ? body.GetBodyPosition(0) : character.Transform.Position;

    /// <summary> Seconds since it last fell from a drop </summary>
    public float SinceDrop { get; private set; } = 100f;

    public void DropFrom(float height)
    {
        var hips = Hips;
        var at = new Vector3(Math.Clamp(hips.X, -3f, 3f), height, Math.Clamp(hips.Z, -3f, 3f));
        character.Transform.Position = at;
        character.Transform.Rotation = Quaternion.RotationY(_heading);
        body.Mode = SkeletonBodyMode.Standing;
        body.Rebuild();
        SinceDrop = 0f;
    }

    public void SimulationUpdate(BepuSimulation simulation, float simTimeStep)
    {
        _time += simTimeStep;
        SinceDrop += simTimeStep;
        if (body.BodyCount == 0)
            return;
        var hips = body.GetBodyPosition(0);

        // Far away or fallen off: back from the sky in the middle
        if (hips.Y < -3f || new Vector2(hips.X - center.X, hips.Z - center.Z).Length() > radius + 4f)
        {
            DropFrom(6f);
            return;
        }

        // Upright and on the ground: walk; anything else (held, knocked down, falling) lets physics do its work
        var up = Vector3.Transform(Vector3.UnitY, body.GetBodyOrientation(2));
        bool standing = body.Mode == SkeletonBodyMode.Standing && body.IsGrounded && up.Y > 0.8f;
        if (!walks || !standing)
            return;

        if (_upper is null || _upper.Length != body.BodyCount)
        {
            _upper = new bool[body.BodyCount];
            for (int i = 0; i < body.BodyCount; i++)
            {
                var node = body.GetBoneShape(i).NodeName;
                _upper[i] = !(node.StartsWith("Thigh", StringComparison.Ordinal) || node.StartsWith("Shin", StringComparison.Ordinal) || node.StartsWith("Foot", StringComparison.Ordinal));
            }
        }

        // Wander, and turn back toward the middle near the edge of the area
        var toCenter = new Vector2(center.X - hips.X, center.Z - hips.Z);
        _heading += simTimeStep * 0.3f * MathF.Sin(_time * 0.23f + heading * 3f);
        if (toCenter.Length() > radius)
        {
            float wanted = MathF.Atan2(toCenter.X, toCenter.Y);
            float delta = MathUtil.Mod(wanted - _heading + MathF.PI, MathUtil.TwoPi) - MathF.PI;
            _heading += Math.Clamp(delta, -1f * simTimeStep, 1f * simTimeStep);
        }
        character.Transform.Rotation = Quaternion.RotationY(_heading);
        character.Transform.Position = new Vector3(hips.X, character.Transform.Position.Y, hips.Z);

        // The hips move at the speed the planted foot slides back in the animation
        var direction = new Vector3(MathF.Sin(_heading), 0f, MathF.Cos(_heading));
        var wantedVelocity = direction * animator.StanceSpeed();
        var current = body.GetBodyLinearVelocity(0);
        var push = (wantedVelocity - new Vector3(current.X, 0f, current.Z)) * 0.6f;
        for (int i = 0; i < body.BodyCount; i++)
        {
            if (!_upper[i])
                continue;
            var reference = VideoDemoGame.BoneBody(body, i);
            reference.Velocity.Linear += new System.Numerics.Vector3(push.X, 0f, push.Z);
            reference.Awake = true;
        }
    }

    public void AfterSimulationUpdate(BepuSimulation simulation, float simTimeStep) { }
}

/// <summary> Fires a heavy ball at the chest of one of the characters every few seconds, along an arc; the cannon turns toward it </summary>
internal sealed class Cannon(DemoGame game, IReadOnlyList<Walker> targets, Vector3 muzzle, float period) : SyncScript
{
    private float _time = period - 1.5f;
    private int _shot;
    private BodyComponent? _ball;
    private Vector3? _pending;

    public void FireNow() => _time = period;

    /// <summary> A wooden carriage on two wheels, and the barrel on top (this script) turning toward its target </summary>
    public static Cannon Build(DemoGame game, IReadOnlyList<Walker> targets, Vector3 muzzle, float period)
    {
        var device = game.GraphicsDevice;
        var wood = Look.Paint(device, Look.Wood, 0.35f);
        var dark = Look.Paint(device, new Color(110, 72, 44), 0.3f);
        var carriage = new Entity("affût") { new ModelComponent(Look.Model(game, new CubeProceduralModel { Size = new Vector3(0.75f, 0.28f, 0.5f) }, wood)) };
        carriage.Transform.Position = muzzle - new Vector3(0f, 0.42f, 0f);
        foreach (var side in new[] { -1f, 1f })
        {
            var wheel = new Entity("roue") { new ModelComponent(Look.Model(game, new CylinderProceduralModel { Radius = 0.3f, Height = 0.08f, Tessellation = 32 }, dark)) };
            wheel.Transform.Position = new Vector3(0f, -0.08f, side * 0.3f);
            wheel.Transform.Rotation = Quaternion.RotationX(MathUtil.PiOverTwo);
            carriage.AddChild(wheel);
        }
        game.AddToScene(carriage);

        var metal = Look.Paint(device, new Color(96, 104, 120), 0.85f);
        var cannon = new Cannon(game, targets, muzzle, period);
        var barrel = new Entity("canon") { new ModelComponent(Look.Model(game, new CylinderProceduralModel { Radius = 0.15f, Height = 1.0f, Tessellation = 32 }, metal)), cannon };
        var ring = new Entity("bouche") { new ModelComponent(Look.Model(game, new TorusProceduralModel { Radius = 0.16f, Thickness = 0.045f, Tessellation = 32 }, metal)) };
        ring.Transform.Position = new Vector3(0f, 0.48f, 0f);
        barrel.AddChild(ring);
        var breech = new Entity("culasse") { new ModelComponent(Look.Model(game, new SphereProceduralModel { Radius = 0.2f, Tessellation = 24 }, metal)) };
        breech.Transform.Position = new Vector3(0f, -0.5f, 0f);
        barrel.AddChild(breech);
        game.AddToScene(barrel);
        return cannon;
    }

    public override void Update()
    {
        // The ball made on the first shot gets its velocity once it is in the simulation
        if (_pending is { } launch && _ball is { Simulation: not null })
        {
            _ball.LinearVelocity = launch;
            _pending = null;
        }

        Entity.Transform.Position = muzzle;
        _time += (float)Game.UpdateTime.Elapsed.TotalSeconds;
        var target = targets.Count > 0 ? targets[_shot % targets.Count] : null;
        if (target is not null)
        {
            var aim = target.Hips + new Vector3(0f, 0.35f, 0f) - muzzle;
            Entity.Transform.Rotation = Quaternion.BetweenDirections(Vector3.UnitY, Vector3.Normalize(aim + new Vector3(0f, aim.Length() * 0.25f, 0f)));
        }
        if (_time < period || target is null)
            return;
        _time = 0f;
        _shot++;

        var chest = target.Hips + new Vector3(0f, 0.35f, 0f);
        var flat = new Vector3(chest.X - muzzle.X, 0f, chest.Z - muzzle.Z);
        float flight = flat.Length() / 9f;
        var velocity = flat / flight + new Vector3(0f, (chest.Y - muzzle.Y) / flight + 0.5f * 9.81f * flight, 0f);
        if (_ball is null)
        {
            var entity = Props.Ball(game, 0.2f, 12f, Look.Navy, muzzle);
            _ball = entity.Get<BodyComponent>();
            game.AddToScene(entity);
            _pending = velocity;
            return;
        }
        _ball.Teleport(muzzle, Quaternion.Identity);
        _ball.AngularVelocity = Vector3.Zero;
        _ball.LinearVelocity = velocity;
        _ball.Awake = true;
    }
}

/// <summary> Every few seconds, drops the walker furthest from the middle from 6 m: free fall, then it lands on its feet </summary>
internal sealed class Lifter(IReadOnlyList<Walker> walkers, float period) : SyncScript
{
    private float _time;

    public override void Update()
    {
        _time += (float)Game.UpdateTime.Elapsed.TotalSeconds;
        if (_time < period || walkers.Count == 0)
            return;
        _time = 0f;
        walkers.Where(w => w.SinceDrop > period * 2f).OrderByDescending(w => w.Hips.Length()).FirstOrDefault()?.DropFrom(6f);
    }
}
