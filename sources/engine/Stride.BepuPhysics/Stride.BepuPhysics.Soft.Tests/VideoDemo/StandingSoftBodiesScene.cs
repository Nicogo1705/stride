// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using Stride.BepuPhysics.Definitions;
using Stride.BepuPhysics.Definitions.Colliders;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Rendering.Materials;
using Stride.Rendering.ProceduralModels;

namespace Stride.BepuPhysics.Soft.Tests.VideoDemo;

/// <summary>
/// Round soft beans standing on a physical skeleton, waddling around an arena while a spinning bar sweeps their legs;
/// see-through jelly bumpers on the rim, and a worm with a physical spine crawling outside
/// </summary>
internal sealed class StandingSoftBodiesScene : VideoScene
{
    public static readonly Color[] FleshColors = { new(255, 120, 170), new(160, 120, 255), new(150, 220, 80), new(90, 190, 255), new(255, 160, 110) };
    private static readonly Vector3 Arena = new(0f, 0.15f, 0f), Worm = new(5.8f, 0f, -4.4f);
    private readonly List<Walker> _beans = new();
    private Entity? _worm;

    public override string Title => "Standing soft bodies";
    public override string Pr => "FollowSkeleton flesh on a Standing ragdoll (upcoming PRs, on #3528)";
    public override string Description => "soft beans on a Standing skeleton waddle, get swept off their feet by the bar and stand back up; the worm's spine is Active";
    public override Vector3 Eye => new(0.6f, 4.0f, 8.8f);
    public override Vector3 Target => new(0f, 0.6f, 0f);
    public override string SceneKeys => "left click a bean: grab it by the nearest bone, F: throw  |  F with empty hands: a ball  |  V: particles";
    public override IReadOnlyList<SkeletonBodyMode> LegendModes => new[] { SkeletonBodyMode.Standing, SkeletonBodyMode.Active };
    public override string LegendCaption => "Mode color: beans' shorts and limbs, the worm's flesh";


    protected override void BuildScene(VideoDemoGame game)
    {
        _beans.Clear();
        var device = game.GraphicsDevice;

        BuildArena(game, Arena, 5f, 4.6f);

        // See-through jelly bumpers on the rim, their base pinned in place
        var bumperColors = new[] { new Color(255, 200, 60), new Color(80, 220, 200), new Color(255, 110, 120) };
        for (int i = 0; i < bumperColors.Length; i++)
        {
            float angle = 0.6f + i * MathUtil.TwoPi / 3f;
            var bumper = new Entity("jelly bumper")
            {
                new ModelComponent(Look.Model(game, new CubeProceduralModel { Size = new Vector3(0.8f), Subdivisions = 8 }, Look.Volume(device, bumperColors[i], 0.85f, 0.35f, MaterialVolumeMedium.Scattering))),
                new VolumetricSoftBodyComponent { Resolution = 5, Stiffness = 10f, ShearStiffness = 10f, Damping = 0.25f, Pins = { new SoftBodyPin { Region = new BoundingBox(new Vector3(-1f, -1f, -1f), new Vector3(1f, -0.3f, 1f)) } } },
            };
            bumper.Transform.Position = Arena + new Vector3(4.1f * MathF.Sin(angle), 0.4f, 4.1f * MathF.Cos(angle));
            bumper.Transform.Rotation = Quaternion.RotationY(angle);
            game.AddToScene(bumper);
        }

        // Five beans, Standing, wandering the arena
        for (int i = 0; i < FleshColors.Length; i++)
        {
            float angle = i * MathUtil.TwoPi / FleshColors.Length + 0.3f;
            var at = Arena + new Vector3(3.0f * MathF.Sin(angle), 0f, 3.0f * MathF.Cos(angle));
            var (entity, body, animator) = Bean.Add(game, FleshColors[i], at, angle + MathUtil.Pi * 0.6f, SkeletonBodyMode.Standing, i * 0.31f);
            var walker = new Walker(entity, body, animator.StanceSpeed, Bean.TrunkBody, angle + MathUtil.Pi * 0.6f, Arena, 3.6f);
            entity.Add(walker);
            _beans.Add(walker);
        }

        // A worm with a physical spine crawling behind the arena, Active: Standing would take its own flesh for the floor
        _worm = PhysicalWorm.Add(game, Modes.Of(SkeletonBodyMode.Active), new Vector3(6.2f, 0f, -5.8f), MathF.PI, PhysicalWorm.Circle(Worm, 1.4f, 0.6f, MathUtil.PiOverTwo + 0.3f));
    }

    /// <summary> A round arena whose top is at <paramref name="top"/>, a pillar in the middle with a bar sweeping at hip height </summary>
    public static void BuildArena(VideoDemoGame game, Vector3 top, float radius, float sweeperLength)
    {
        var arena = new Entity("arena")
        {
            new StaticComponent { Collider = new CompoundCollider { Colliders = { new CylinderCollider { Radius = radius, Length = top.Y } } } },
            new ModelComponent(Look.Model(game, new CylinderProceduralModel { Radius = radius, Height = top.Y, Tessellation = 96 }, new Color(205, 190, 240), 0.55f)),
        };
        arena.Transform.Position = top - new Vector3(0f, top.Y / 2f, 0f);
        game.AddToScene(arena);
        Props.Pillar(game, top, 0.3f, 1.0f, Look.Ivory, 0.6f);
        var size = new Vector3(sweeperLength, 0.2f, 0.2f);
        var sweeper = new BodyComponent { Kinematic = true, Collider = new CompoundCollider { Colliders = { new BoxCollider { Size = size } } } };
        var entity = new Entity("sweeper") { new ModelComponent(Look.Model(game, new CubeProceduralModel { Size = size }, Look.Navy, 0.8f)), sweeper };
        entity.Transform.Position = top + new Vector3(0f, 0.32f, 0f);
        float angle = 0f;
        entity.Add(new StepDriver((time, step) =>
        {
            // Faster, then slower, so the beans cannot learn the rhythm
            angle += step * (1.0f + 0.5f * MathF.Sin(time * 0.35f));
            sweeper.SetTargetPose(top + new Vector3(0f, 0.32f, 0f), Quaternion.RotationY(angle));
        }));
        game.AddToScene(entity);
    }

    public override IEnumerable<(Vector3 World, string Text)> Labels()
    {
        if (_worm is not null)
            yield return (_worm.Transform.Position + new Vector3(0f, 0.8f, 0f), "worm: Active spine + FollowSkeleton flesh");
    }

    public override IEnumerable<string> Status()
    {
        var standing = _beans.Count(b => b.Body.IsGrounded && Vector3.Transform(Vector3.UnitY, b.Body.BodyCount > Bean.TrunkBody ? b.Body.GetBodyOrientation(Bean.TrunkBody) : Quaternion.Identity).Y > 0.8f);
        yield return $"{_beans.Count} soft beans, {standing} on their feet  |  {Bean.Hitboxes().Count} bones each, flesh Resolution 6";
    }
}
