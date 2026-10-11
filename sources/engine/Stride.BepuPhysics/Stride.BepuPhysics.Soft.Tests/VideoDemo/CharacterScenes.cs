// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using Stride.BepuPhysics.Definitions;
using Stride.BepuPhysics.Definitions.Colliders;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Input;
using Stride.Rendering.ProceduralModels;

namespace Stride.BepuPhysics.Soft.Tests.VideoDemo;

/// <summary> An animated human walking a circle with kinematic hitboxes on its bones; anything that touches it turns it into a ragdoll, U puts it back </summary>
internal sealed class AnimatedHumanScene : VideoScene
{
    private const float PathRadius = 2.4f;
    private static readonly Vector3 PathCenter = new(0f, 0f, -0.6f);

    private CircleWalk? _walk;

    public override string Title => "Animated human with bone hitboxes";
    public override string Pr => "SkeletonBodyComponent, ragdoll (upcoming PRs, on #3528)";
    public override string Description => "kinematic hitboxes follow the walk; hit him (F: ball, gravity gun, a thrown prop) and he goes Ragdoll; U stands him back up";
    public override Vector3 Eye => new(0.6f, 2.5f, 5.8f);
    public override Vector3 Target => new(0f, 0.9f, -0.6f);
    public override string SceneKeys => "U: put the human back (Animated)  |  F / middle click: throw a ball  |  left click a bone: grab (Ragdoll)";
    public override IReadOnlyList<SkeletonBodyMode> LegendModes => new[] { SkeletonBodyMode.Animated, SkeletonBodyMode.Ragdoll };
    public override int DefaultDebugView => VideoDemoGame.RigidView;

    public SkeletonBodyMode Mode => _walk?.Body.Mode ?? SkeletonBodyMode.Animated;

    protected override void BuildScene(VideoDemoGame game)
    {
        var (human, body, _) = Humans.Add(game, SkeletonBodyMode.Animated, PathCenter, 0f);
        _walk = new CircleWalk(human, body, PathCenter, PathRadius, 1.35f);
        human.Add(_walk);

        // A table of props to throw on the right, a stack of crates to push on the left, a ring on the floor marking the walk
        var device = game.GraphicsDevice;
        Look.StaticBox(game, new Vector3(1.0f, 0.75f, 2.2f), new Vector3(3.9f, 0.375f, 0.6f), Look.Paint(device, Look.Wood, 0.45f), "table");
        for (int i = 0; i < 8; i++)
            game.AddToScene(Props.Rigid(game, i, new Vector3(3.75f + (i % 2) * 0.35f, 1.0f, -0.25f + i / 2 * 0.45f), Quaternion.RotationY(i * 0.7f), 0.8f));
        for (int row = 0; row < 3; row++)
        {
            for (int i = 0; i < 3 - row; i++)
                game.AddToScene(Props.Crate(game, new Vector3(0.45f), 4f, new Vector3(-4.0f + i * 0.47f + row * 0.235f, 0.225f + row * 0.45f, -1.0f)));
        }
        var ring = new Entity("walk ring") { new ModelComponent(Look.Model(game, new TorusProceduralModel { Radius = PathRadius, Thickness = 0.03f, Tessellation = 96 }, Look.Ivory, 0.3f)) { IsShadowCaster = false } };
        ring.Transform.Position = PathCenter + new Vector3(0f, 0.005f, 0f);
        game.AddToScene(ring);
    }

    public override void UpdateInput(VideoDemoGame game)
    {
        if (game.Input.IsKeyPressed(Keys.U))
            Restore();
    }

    public void Restore() => _walk?.Restore();

    public override IEnumerable<(Vector3 World, string Text)> Labels()
    {
        if (_walk is { Body.BodyCount: > 0 })
            yield return (_walk.Body.GetBodyPosition(0) + new Vector3(0f, 1.25f, 0f), _walk.Body.Mode.ToString());
    }

    public override IEnumerable<string> Status()
    {
        yield return $"mode: {Mode}  |  knocked down {_walk?.Knockdowns ?? 0} time(s)  |  {_walk?.Body.BodyCount ?? 0} hitboxes  |  U: back to the animation";
    }
}

/// <summary> Standing NPCs wandering a square under cannon fire, and one character in each mode on a pedestal at the back </summary>
internal sealed class StandingNpcScene : VideoScene
{
    private static readonly Vector3 Square = new(0f, 0f, 1.3f);
    private readonly List<Walker> _walkers = new();
    private readonly List<(SkeletonBodyComponent Body, Vector3 Pedestal)> _lineup = new();
    private Cannon? _cannon;

    public override string Title => "Standing NPCs, and every SkeletonBody mode";
    public override string Pr => "ragdoll Active / Standing (upcoming PR, on #3528)";
    public override string Description => "Standing NPCs walk, take cannonballs, fall and get up where they land; at the back, one character per mode";
    public override Vector3 Eye => new(0.8f, 3.8f, 8.6f);
    public override Vector3 Target => new(0f, 0.8f, -0.8f);
    public override string SceneKeys => "B: cannonball now  |  L: lift the nearest NPC 6 m up and drop it  |  left click a bone: grab, F: throw";
    public override IReadOnlyList<SkeletonBodyMode> LegendModes => Modes.All;

    protected override void BuildScene(VideoDemoGame game)
    {
        _walkers.Clear();
        _lineup.Clear();
        var device = game.GraphicsDevice;

        // One character per mode on a pedestal, facing the camera
        var stone = Look.Paint(device, Look.Ivory, 0.55f);
        for (int i = 0; i < Modes.All.Length; i++)
        {
            var mode = Modes.All[i];
            var pedestal = new Vector3(-3.6f + i * 2.4f, 0f, -3.8f);
            Look.StaticBox(game, new Vector3(1.5f, 0.3f, 1.5f), pedestal + new Vector3(0f, 0.15f, 0f), stone, "pedestal");
            var animator = new ProceduralCharacter.Animator { Idle = mode != SkeletonBodyMode.Animated, Time = i * 0.7f };
            var (_, body, _) = Humans.Add(game, mode, pedestal + new Vector3(0f, 0.3f, 0f), 0f, animator, mode + " exemplar");
            _lineup.Add((body, pedestal));
        }

        // A static crate and a 100 kg dynamic one in the square
        foreach (var dynamic in new[] { false, true })
        {
            var size = new Vector3(1.1f, 0.5f, 1.1f);
            var collider = new CompoundCollider { Colliders = { new BoxCollider { Size = size, Mass = 100f } } };
            CollidableComponent crate = dynamic ? new BodyComponent { Collider = collider } : new StaticComponent { Collider = collider };
            var entity = new Entity(dynamic ? "dynamic crate (100 kg)" : "static crate") { new ModelComponent(Look.Model(game, new CubeProceduralModel { Size = size }, dynamic ? Look.Wood : Look.Stone, 0.35f)), crate };
            entity.Transform.Position = Square + new Vector3(dynamic ? 1.9f : -1.9f, 0.25f, -0.4f);
            game.AddToScene(entity);
        }

        var spots = new[] { new Vector3(-1.9f, 0.5f, -0.4f), new Vector3(1.9f, 0.5f, -0.4f), new Vector3(-3.0f, 0f, 1.8f), new Vector3(2.8f, 0f, 2.0f), new Vector3(0f, 0f, -1.3f), new Vector3(0.2f, 0f, 2.4f) };
        for (int i = 0; i < spots.Length; i++)
        {
            var animator = new ProceduralCharacter.Animator { Time = i * 0.41f };
            var (character, body, _) = Humans.Add(game, SkeletonBodyMode.Standing, Square + spots[i], i * 1.3f, animator, $"npc {i + 1}");
            var walker = new Walker(character, body, animator.StanceSpeed, 2, i * 1.3f, Square, 2.6f);
            character.Add(walker);
            _walkers.Add(walker);
        }

        _cannon = Cannon.Build(game, _walkers, new Vector3(-6.2f, 0.75f, 3.4f), 4f);
    }

    public override void UpdateInput(VideoDemoGame game)
    {
        if (game.Input.IsKeyPressed(Keys.B))
            _cannon?.FireNow();
        if (game.Input.IsKeyPressed(Keys.L) && _walkers.Count > 0)
            _walkers.OrderBy(w => Vector3.Distance(w.Hips, game.CameraPosition)).First().DropFrom(6f);
    }

    public override IEnumerable<(Vector3 World, string Text)> Labels()
    {
        foreach (var (body, pedestal) in _lineup)
            yield return (pedestal + new Vector3(0f, 2.55f, 0f), body.Mode.ToString());
        yield return (Square + new Vector3(1.9f, 0f, 0.35f), "100 kg crate");
    }

    public override IEnumerable<string> Status()
    {
        var grounded = _walkers.Count(w => w.Body.IsGrounded);
        yield return $"{_walkers.Count} Standing NPCs, {grounded} on their feet  |  cannonball every 4 s (B: now)  |  lineup: {string.Join(", ", _lineup.Select(l => l.Body.Mode))}";
    }
}
