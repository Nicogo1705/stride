// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using Stride.BepuPhysics.Definitions.Colliders;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Rendering.ProceduralModels;

namespace Stride.BepuPhysics.Soft.Tests.VideoDemo;

/// <summary>
/// The voxel ground itself: an overhang over a plateau taking a rain of objects, an arch, a kinematic arm batting what rolls off,
/// and craters dug on a timer, the collider and the mesh updated together
/// </summary>
internal sealed class PlaygroundZone : Zone
{
    private const int MaxRain = 30, MaxCraters = 16;
    private const float RainPeriod = 0.6f, CraterPeriod = 6f;

    // Where the craters go, in turn: over the mound, then on the open pad
    private static readonly Vector2[] CraterSpots =
    {
        new(6f, 9f), new(4.6f, 10.2f), new(7.4f, 7.8f), new(-4f, -6f), new(5.2f, 7.4f), new(-6.5f, -2f), new(7.2f, 10.4f), new(-1.5f, 8.5f),
        new(-3f, -9f), new(4.2f, 8.6f), new(-7.5f, -5.5f), new(6.6f, 11f), new(-2.5f, 11f), new(8.2f, 9.2f), new(-5f, -10f), new(1f, 10.5f),
    };

    private readonly Queue<Entity> _rain = new();
    private WorldTerrain? _terrain;
    private int _dropped, _craters;

    public override string Name => "Voxel playground";
    public override string Description => "VoxelCollider surface nets: an overhang taking a rain of objects, an arch, craters dug every 6 s";
    public override Vector3 Eye => Origin + new Vector3(-2f, 6f, 16f);
    public override Vector3 Target => Origin + new Vector3(3f, 2f, -1f);

    /// <summary> Where to look at the overhang from below </summary>
    public Vector3 OverhangEye => Origin + new Vector3(4.2f, 1.6f, 4.2f);

    public Vector3 OverhangTarget => Origin + new Vector3(6.5f, 3.6f, -3f);

    public override void Build(VideoDemoGame game, WorldTerrain terrain)
    {
        _rain.Clear();
        (_dropped, _craters) = (0, 0);
        _terrain = terrain;
        var o = Origin;

        // A kinematic arm in front of the overhang, batting what rolls down
        var axle = o + new Vector3(6.5f, 0f, 2.5f);
        Look.StaticBox(game, new Vector3(0.25f, 0.6f, 0.25f), axle + new Vector3(0f, 0.3f, 0f), Look.Paint(game.GraphicsDevice, Look.Navy, 0.4f), "arm axle");
        var size = new Vector3(2.4f, 0.18f, 0.35f);
        var arm = Kit.Kinematic(game, "kinematic arm", new CubeProceduralModel { Size = size }, new BoxCollider { Size = size }, Look.Paint(game.GraphicsDevice, Look.Wood, 0.6f), axle + new Vector3(0f, 0.75f, 0f)).Get<BodyComponent>()!;
        game.AddToScene(new Entity("arm driver") { new StepDriver((time, _) => arm.SetTargetPose(axle + new Vector3(0f, 0.75f, 0f), Quaternion.RotationY(time * 1.6f))) });

        // A few objects already resting under the arch
        for (int i = 0; i < 3; i++)
            game.AddToScene(Props.Crate(game, new Vector3(0.5f), 2f, o + new Vector3(-8.6f + i * 0.6f, 0.3f + i * 0.55f, 6f), Look.Accents[i]));
    }

    protected override void Tick(VideoDemoGame game, float time, float seconds)
    {
        if (Every(time, seconds, RainPeriod))
            Drop(game);
        if (_terrain is not null && _craters < MaxCraters && Every(time, seconds, CraterPeriod, CraterPeriod * 0.5f))
        {
            var spot = CraterSpots[_craters % CraterSpots.Length];
            float x = Origin.X + spot.X, z = Origin.Z + spot.Y;
            _terrain.Carve(new Vector3(x, _terrain.SurfaceY(x, z) - 0.3f, z), 1.4f);
            _craters++;
        }
    }

    private void Drop(VideoDemoGame game)
    {
        int i = _dropped++;
        var at = Origin + new Vector3(6.5f + 2.5f * MathF.Sin(i * 2.4f), 9f, -3f + 2.5f * MathF.Cos(i * 1.7f));
        var entity = i % 3 == 0
            ? Props.Rigid(game, i, at, Quaternion.RotationYawPitchRoll(i, i * 0.5f, 0f))
            : Props.Ball(game, 0.2f, 1f, Look.Accents[i % Look.Accents.Length], at);
        game.AddToScene(entity);
        _rain.Enqueue(entity);
        if (_rain.Count > MaxRain)
            Remove(game, _rain.Dequeue());
    }

    public override IEnumerable<SceneLabel> Labels()
    {
        var o = Origin;
        yield return new(o + new Vector3(6.5f, 6f, -3f), "overhang: VoxelCollider, surface nets");
        yield return new(o + new Vector3(6f, 3.2f, 9f), $"craters: {_craters} / {MaxCraters}");
        yield return new(o + new Vector3(-8f, 4.4f, 6f), "arch");
        yield return new(o + new Vector3(6.5f, 1.6f, 2.5f), "kinematic arm");
    }

    public override IEnumerable<string> Status()
    {
        yield return $"{_rain.Count} objects in the rain ({_dropped} dropped, oldest removed past {MaxRain})  |  craters: {_craters} / {MaxCraters}, one every {CraterPeriod:0} s";
    }
}
