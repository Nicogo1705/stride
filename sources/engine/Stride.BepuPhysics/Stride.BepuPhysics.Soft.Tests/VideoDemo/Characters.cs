// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using BepuPhysics.CollisionDetection;
using Stride.BepuPhysics.Definitions;
using Stride.BepuPhysics.Definitions.Contacts;
using Stride.BepuPhysics.Components;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Harness.Demo;
using Stride.Rendering.ProceduralModels;

namespace Stride.BepuPhysics.Soft.Tests.VideoDemo;

/// <summary> Humans made in code, painted in the color of their mode </summary>
internal static class Humans
{
    public static readonly Color Skin = new(236, 196, 160);

    /// <summary> A character with its hitboxes in <paramref name="mode"/>, standing at <paramref name="position"/> and facing <paramref name="yaw"/> </summary>
    public static (Entity Entity, SkeletonBodyComponent Body, ProceduralCharacter.Animator Animator) Add(DemoGame game, SkeletonBodyMode mode, Vector3 position, float yaw, ProceduralCharacter.Animator? animator = null, string name = "human", Quaternion? rotation = null)
    {
        var device = game.GraphicsDevice;
        var body = ProceduralCharacter.CreateHitboxes();
        body.Mode = mode;
        animator ??= new ProceduralCharacter.Animator();
        var model = ProceduralCharacter.CreateModel(device, Look.Paint(device, Modes.Of(mode), 0.5f), Look.Paint(device, Skin, 0.4f));
        var entity = ProceduralCharacter.Create(model, animator, body);
        entity.Name = name;
        entity.Transform.Position = position;
        entity.Transform.Rotation = rotation ?? Quaternion.RotationY(yaw);
        entity.Add(new ModeTint(body, new[] { entity.Get<ModelComponent>()! }));
        game.AddToScene(entity);
        return (entity, body, animator);
    }
}

/// <summary> Raised by any dynamic body or soft body touching the collidable it is set on, the static floor excluded </summary>
internal sealed class HitDetector : IContactHandler
{
    private volatile bool _hit;

    public bool NoContactResponse => false;

    /// <summary> Whether something hit it since the last call </summary>
    public bool Take()
    {
        bool hit = _hit;
        _hit = false;
        return hit;
    }

    public void OnStartedTouching<TManifold>(Contacts<TManifold> contacts) where TManifold : unmanaged, IContactManifold<TManifold>
    {
        if (contacts.Other is BodyComponent { Kinematic: false } or SoftBodyComponent)
            _hit = true;
    }
}

/// <summary>
/// Walks an Animated character along a circle, its kinematic hitboxes following the animation; the first dynamic body or soft body
/// that touches it turns it into a ragdoll, which lies where it falls until <see cref="Restore"/>.
/// </summary>
internal sealed class CircleWalk : ScriptComponent, ISimulationUpdate
{
    private readonly Entity _human;
    private readonly SkeletonBodyComponent _body;
    private readonly HitDetector _hits = new();
    private readonly Vector3 _center;
    private readonly float _radius, _speed;
    private float _time;

    public CircleWalk(Entity human, SkeletonBodyComponent body, Vector3 center, float radius, float speed)
    {
        (_human, _body, _center, _radius, _speed) = (human, body, center, radius, speed);
        body.ContactEventHandler = _hits;
        Place();
    }

    public SkeletonBodyComponent Body => _body;

    public int Knockdowns { get; private set; }

    /// <summary> Counter clockwise seen from above, facing along the circle </summary>
    private void Place()
    {
        float angle = _speed * _time / _radius;
        _human.Transform.Position = _center + new Vector3(_radius * MathF.Sin(angle), 0f, _radius * MathF.Cos(angle));
        _human.Transform.Rotation = Quaternion.RotationY(angle + MathUtil.PiOverTwo);
    }

    /// <summary> Back to the start of the walk, animated, its hitboxes rebuilt there </summary>
    public void Restore()
    {
        _time = 0f;
        Place();
        _body.Mode = SkeletonBodyMode.Animated;
        _body.Rebuild();
        _hits.Take();
    }

    public void SimulationUpdate(BepuSimulation simulation, float simTimeStep)
    {
        if (_hits.Take() && _body.Mode == SkeletonBodyMode.Animated)
        {
            _body.Mode = SkeletonBodyMode.Ragdoll;
            Knockdowns++;
        }
        // Only while animated: the entity does not move with a ragdoll
        if (_body.Mode != SkeletonBodyMode.Animated)
            return;
        _time += simTimeStep;
        Place();
    }

    public void AfterSimulationUpdate(BepuSimulation simulation, float simTimeStep) { }
}

/// <summary>
/// Walks a Standing character around. The animation walks in place: the foot on the ground slides back under the hips at a speed
/// that changes through the stride, so the upper bodies get exactly that speed forward, while the legs are left to their servos
/// and the friction of the floor. The entity turns toward a heading the root servo follows, and stays under the hips.
/// Off when it is grabbed, lying or in the air; a character thrown away stays where it lands and walks from there.
/// </summary>
/// <remarks> A character whose bones are ghosts carries its mass in <paramref name="flesh"/>: the push goes to the flesh above the legs instead. </remarks>
internal sealed class Walker(Entity character, SkeletonBodyComponent body, Func<float> stanceSpeed, int trunk, float heading, Vector3 center, float radius, SoftBodyComponent? flesh = null) : ScriptComponent, ISimulationUpdate
{
    private float _heading = heading;
    private float _time;
    private bool[]? _upper;

    public SkeletonBodyComponent Body => body;

    public Entity Character => character;

    public Vector3 Hips => body.BodyCount > 0 ? body.GetBodyPosition(0) : character.Transform.Position;

    /// <summary> Lifts the character to <paramref name="height"/> above where it is, upright, for the L key </summary>
    public void DropFrom(float height)
    {
        var hips = Hips;
        character.Transform.Position = new Vector3(hips.X, height, hips.Z);
        character.Transform.Rotation = Quaternion.RotationY(_heading);
        body.Mode = SkeletonBodyMode.Standing;
        body.Rebuild();
    }

    public void SimulationUpdate(BepuSimulation simulation, float simTimeStep)
    {
        _time += simTimeStep;
        if (body.BodyCount == 0)
            return;
        var hips = body.GetBodyPosition(0);

        // Upright and on the ground: walk; anything else (held, knocked down, falling) lets physics do its work
        var up = Vector3.Transform(Vector3.UnitY, body.GetBodyOrientation(trunk));
        bool standing = body.Mode == SkeletonBodyMode.Standing && body.IsGrounded && up.Y > 0.8f;
        if (!standing)
            return;

        if (_upper is null || _upper.Length != body.BodyCount)
        {
            _upper = new bool[body.BodyCount];
            for (int i = 0; i < body.BodyCount; i++)
            {
                var node = body.GetBoneShape(i).NodeName;
                _upper[i] = !(node.StartsWith("Thigh", StringComparison.Ordinal) || node.StartsWith("Shin", StringComparison.Ordinal) || node.StartsWith("Foot", StringComparison.Ordinal) || node.StartsWith("Leg", StringComparison.Ordinal));
            }
        }

        // Wander, and turn back toward the middle beyond the radius
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
        var wantedVelocity = direction * stanceSpeed();
        var current = body.GetBodyLinearVelocity(0);
        var push = (wantedVelocity - new Vector3(current.X, 0f, current.Z)) * 0.6f;
        if (flesh is { ParticleCount: > 0 })
        {
            for (int i = 0; i < flesh.ParticleCount; i++)
            {
                if (flesh.GetParticlePosition(i).Y > hips.Y - 0.15f)
                    flesh.SetParticleVelocity(i, flesh.GetParticleVelocity(i) + push);
            }
            return;
        }
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

/// <summary>
/// Fires a heavy ball at the chest of one of the targets every few seconds, along an arc; the barrel turns toward it.
/// Each shot is a new ball; past <see cref="MaxBalls"/>, the oldest one is removed.
/// </summary>
internal sealed class Cannon(DemoGame game, IReadOnlyList<Walker> targets, Vector3 muzzle, float period) : SyncScript
{
    private const int MaxBalls = 6;
    private readonly Queue<Entity> _balls = new();
    private float _time = period - 1.5f;
    private int _shot;
    private (BodyComponent Body, Vector3 Velocity)? _pending;

    public int Shots => _shot;

    public void FireNow() => _time = period;

    /// <summary> A wooden carriage on two wheels, and the barrel on top (this script) turning toward its target </summary>
    public static Cannon Build(DemoGame game, IReadOnlyList<Walker> targets, Vector3 muzzle, float period)
    {
        var device = game.GraphicsDevice;
        var wood = Look.Paint(device, Look.Wood, 0.35f);
        var dark = Look.Paint(device, new Color(110, 72, 44), 0.3f);
        var carriage = new Entity("cannon carriage") { new ModelComponent(Look.Model(game, new CubeProceduralModel { Size = new Vector3(0.75f, 0.28f, 0.5f) }, wood)) };
        carriage.Transform.Position = muzzle - new Vector3(0f, 0.42f, 0f);
        foreach (var side in new[] { -1f, 1f })
        {
            var wheel = new Entity("cannon wheel") { new ModelComponent(Look.Model(game, new CylinderProceduralModel { Radius = 0.3f, Height = 0.08f, Tessellation = 32 }, dark)) };
            wheel.Transform.Position = new Vector3(0f, -0.08f, side * 0.3f);
            wheel.Transform.Rotation = Quaternion.RotationX(MathUtil.PiOverTwo);
            carriage.AddChild(wheel);
        }
        game.AddToScene(carriage);

        var metal = Look.Paint(device, new Color(96, 104, 120), 0.85f);
        var cannon = new Cannon(game, targets, muzzle, period);
        var barrel = new Entity("cannon barrel") { new ModelComponent(Look.Model(game, new CylinderProceduralModel { Radius = 0.15f, Height = 1.0f, Tessellation = 32 }, metal)), cannon };
        var ring = new Entity("cannon muzzle") { new ModelComponent(Look.Model(game, new TorusProceduralModel { Radius = 0.16f, Thickness = 0.045f, Tessellation = 32 }, metal)) };
        ring.Transform.Position = new Vector3(0f, 0.48f, 0f);
        barrel.AddChild(ring);
        var breech = new Entity("cannon breech") { new ModelComponent(Look.Model(game, new SphereProceduralModel { Radius = 0.2f, Tessellation = 24 }, metal)) };
        breech.Transform.Position = new Vector3(0f, -0.5f, 0f);
        barrel.AddChild(breech);
        game.AddToScene(barrel);
        return cannon;
    }

    public override void Update()
    {
        // A new ball gets its velocity once it is in the simulation
        if (_pending is { } pending && pending.Body.Simulation is not null)
        {
            pending.Body.LinearVelocity = pending.Velocity;
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
        float flight = MathF.Max(0.3f, flat.Length() / 9f);
        var velocity = flat / flight + new Vector3(0f, (chest.Y - muzzle.Y) / flight + 0.5f * 9.81f * flight, 0f);
        var entity = Props.Ball(game, 0.2f, 12f, Look.Navy, muzzle);
        game.AddToScene(entity);
        _pending = (entity.Get<BodyComponent>()!, velocity);
        _balls.Enqueue(entity);
        if (_balls.Count > MaxBalls)
            game.SceneSystem.SceneInstance.RootScene.Entities.Remove(_balls.Dequeue());
    }
}
