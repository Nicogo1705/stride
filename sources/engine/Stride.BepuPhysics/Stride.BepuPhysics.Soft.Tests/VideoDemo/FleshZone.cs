// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using Stride.BepuPhysics.Definitions;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Rendering.Materials;
using Stride.Rendering.ProceduralModels;

namespace Stride.BepuPhysics.Soft.Tests.VideoDemo;

/// <summary>
/// Soft flesh on skeletons that are not characters: two molluscs crawling around a rock, one under a sail pinned to its head bone,
/// a worm on a physical Standing spine, and an arena where a sweeper bats soft balls into pinned jelly bumpers
/// </summary>
internal sealed class FleshZone : Zone
{
    private const int MaxBalls = 6;
    private readonly Queue<Entity> _balls = new();
    private Vector3 _arena, _rock, _worm;
    private MolluscDriver? _molluscs;
    private int _thrown;

    public override string Name => "Flesh on bones";
    public override string Description => "molluscs and a worm: one soft body around a skeleton, an arena with a sweeper and jelly bumpers";
    public override Vector3 Eye => Origin + new Vector3(0f, 6.5f, 13f);
    public override Vector3 Target => Origin + new Vector3(0f, 0.6f, -0.5f);
    public override IReadOnlyList<SkeletonBodyMode> LegendModes => new[] { SkeletonBodyMode.Standing };
    public override string LegendCaption => "Mode color: the worm's spine";

    public override void Build(VideoDemoGame game, WorldTerrain terrain)
    {
        _balls.Clear();
        _thrown = 0;
        var o = Origin;
        (_arena, _rock, _worm) = (o + new Vector3(0f, 0.3f, 0f), o + new Vector3(-8f, 0f, -1f), o + new Vector3(8f, 0f, 0f));
        var device = game.GraphicsDevice;

        // The arena, with see-through bumpers on the rim, their base pinned in place
        Kit.BuildArena(game, _arena, 5f, 4.6f);
        var bumperColors = new[] { new Color(255, 200, 60), new Color(80, 220, 200), new Color(255, 110, 120) };
        for (int i = 0; i < bumperColors.Length; i++)
        {
            float angle = 0.6f + i * MathUtil.TwoPi / 3f;
            var bumper = new Entity("jelly bumper")
            {
                new ModelComponent(Look.Model(game, new CubeProceduralModel { Size = new Vector3(0.8f), Subdivisions = 8 }, Look.Volume(device, bumperColors[i], 0.85f, 0.35f, MaterialVolumeMedium.Scattering))),
                new VolumetricSoftBodyComponent { Resolution = 5, Stiffness = 10f, ShearStiffness = 10f, Damping = 0.25f, Pins = { new SoftBodyPin { Region = new BoundingBox(new Vector3(-1f, -1f, -1f), new Vector3(1f, -0.3f, 1f)) } } },
            };
            bumper.Transform.Position = _arena + new Vector3(4.3f * MathF.Sin(angle), 0.4f, 4.3f * MathF.Cos(angle));
            bumper.Transform.Rotation = Quaternion.RotationY(angle);
            game.AddToScene(bumper);
        }
        for (int i = 0; i < 3; i++)
            ThrowBall(game);

        // Two molluscs crawling around a rock, the violet one under a sail pinned to its head bone
        Props.Pillar(game, _rock, 0.35f, 0.6f, Look.Stone, 0.3f);
        _molluscs = new MolluscDriver();
        game.AddToScene(new Entity("mollusc driver") { _molluscs });
        Mollusc.Add(game, Look.Coral, _molluscs, Mollusc.Circle(_rock, 1.05f, 0.5f, MathF.PI));
        var path = Mollusc.Circle(_rock, 1.05f, 0.5f, 0f);
        var sailed = Mollusc.Add(game, Look.Violet, _molluscs, path);
        Mollusc.AddSail(game, sailed, path(0f).Yaw, Look.Sun);

        // A worm on a physical spine, Standing, pulled along a circle by its head
        PhysicalWorm.Add(game, Modes.Of(SkeletonBodyMode.Standing), _worm + new Vector3(1.4f, 0f, 0f), MathUtil.PiOverTwo, PhysicalWorm.Circle(_worm, 1.4f, 0.5f, 0f));
    }

    /// <summary> A soft ball dropped on the arena, away from the pillar; the oldest goes past <see cref="MaxBalls"/> </summary>
    private void ThrowBall(VideoDemoGame game)
    {
        float angle = _thrown * 2.4f;
        var at = _arena + new Vector3(2.6f * MathF.Sin(angle), 2.2f, 2.6f * MathF.Cos(angle));
        _balls.Enqueue(Kit.AddSoftBall(game, at, 0.32f, Look.Accents[_thrown % Look.Accents.Length], 6, 1f));
        _thrown++;
        if (_balls.Count > MaxBalls)
            Remove(game, _balls.Dequeue());
    }

    protected override void Tick(VideoDemoGame game, float time, float seconds)
    {
        if (Every(time, seconds, 8f))
            ThrowBall(game);
    }

    public override IEnumerable<SceneLabel> Labels()
    {
        yield return new(_arena + new Vector3(0f, 2.2f, -4.6f), "sweeper, jelly bumpers pinned at their base");
        yield return new(_rock + new Vector3(0f, 2.1f, 0f), "molluscs: soft body following a skeleton, sail pinned to a bone");
        yield return new(_worm + new Vector3(0f, 1.6f, 0f), "worm: Standing spine inside soft flesh");
    }

    public override IEnumerable<string> Status()
    {
        yield return $"{_molluscs?.Count ?? 0} molluscs, 1 worm  |  {_balls.Count} soft balls on the arena ({_thrown} thrown, one every 8 s)";
    }
}
