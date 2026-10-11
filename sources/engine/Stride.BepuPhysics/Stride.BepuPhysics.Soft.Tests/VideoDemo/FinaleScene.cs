// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using Stride.BepuPhysics.Definitions;
using Stride.BepuPhysics.Definitions.Colliders;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics.GeometricPrimitives;
using Stride.Input;
using Stride.Rendering.Materials;
using Stride.Rendering.ProceduralModels;

namespace Stride.BepuPhysics.Soft.Tests.VideoDemo;

/// <summary>
/// Everything at once in a playground: soft beans on an arena with a sweeper, Standing humans under cannon fire, an animated guard
/// who goes ragdoll when hit, a worm on a physical spine, flags, see-through jellies, a voxel hill taking a rain of objects, a kinematic arm
/// </summary>
internal sealed class FinaleScene : VideoScene
{
    private static readonly Vector3 ArenaTop = new(0.6f, 0.15f, -0.8f), Square = new(-4.4f, 0f, 1.4f);
    private readonly List<Walker> _humans = new();
    private readonly List<Walker> _beans = new();
    private VoxelTerrain? _hill;
    private Cannon? _cannon;
    private CircleWalk? _guard;

    public override string Title => "Grand Finale";
    public override string Pr => "every PR at once";
    public override string Description => "soft beans and a sweeper, Standing humans under cannon fire, an animated guard, a worm, flags, jellies, a voxel hill; grab anything";
    public override Vector3 Eye => new(0.8f, 6.0f, 12.6f);
    public override Vector3 Target => new(0.2f, 0.9f, -1.2f);
    public override string SceneKeys => "X: crater in the hill  |  B: cannonball  |  U: put the guard back  |  L: lift the nearest human 6 m up";
    public override IReadOnlyList<SkeletonBodyMode> LegendModes => Modes.All;
    public override string LegendCaption => "Mode color: humans' clothes, beans' limbs, the worm";


    protected override void BuildScene(VideoDemoGame game)
    {
        _humans.Clear();
        _beans.Clear();
        var device = game.GraphicsDevice;

        // The voxel hill at the back right, objects raining on it once
        _hill = new VoxelTerrain(game, 36, 14, 0.35f, new Vector3(8.6f, 0f, -6.4f), (x, z) =>
        {
            float r = new Vector2(x, z).Length();
            return 3.6f * MathF.Exp(-r * r / 12f) + 0.25f * MathF.Sin(x * 1.3f) * MathF.Cos(z * 1.1f) + 0.2f;
        });
        game.AddToScene(new Entity("object rain") { new Dropper(game, i => i % 3 == 0 ? Props.Rigid(game, i, Vector3.Zero, Quaternion.RotationYawPitchRoll(i, i * 0.5f, 0f)) : Props.Ball(game, 0.2f, 1f, Look.Accents[i % Look.Accents.Length], Vector3.Zero),
            i => new Vector3(8.6f + MathF.Sin(i * 2.4f) * 1.0f, 7f, -6.4f + MathF.Cos(i * 2.4f) * 1.0f), 0.45f, 14) });

        // The arena in the middle, three beans on it
        StandingSoftBodiesScene.BuildArena(game, ArenaTop, 3.4f, 3.0f);
        for (int i = 0; i < 3; i++)
        {
            float angle = i * MathUtil.TwoPi / 3f + 0.5f;
            var (entity, body, animator) = Bean.Add(game, StandingSoftBodiesScene.FleshColors[i], ArenaTop + new Vector3(2.1f * MathF.Sin(angle), 0f, 2.1f * MathF.Cos(angle)), angle + 2f, SkeletonBodyMode.Standing, i * 0.4f);
            var walker = new Walker(entity, body, animator.StanceSpeed, Bean.TrunkBody, angle + 2f, ArenaTop, 2.4f);
            entity.Add(walker);
            _beans.Add(walker);
        }

        // Standing humans on the left, under cannon fire
        for (int i = 0; i < 3; i++)
        {
            var animator = new ProceduralCharacter.Animator { Time = i * 0.5f };
            var (character, body, _) = Humans.Add(game, SkeletonBodyMode.Standing, Square + new Vector3(-1.2f + i * 1.2f, 0f, (i % 2) * 0.9f - 0.4f), i * 2.1f, animator, $"standing human {i + 1}");
            var walker = new Walker(character, body, animator.StanceSpeed, 2, i * 2.1f, Square, 2.0f);
            character.Add(walker);
            _humans.Add(walker);
        }
        _cannon = Cannon.Build(game, _humans, new Vector3(-8.4f, 0.75f, -2.4f), 5f);

        // An animated guard walking a circle at the front; hit him and he goes Ragdoll
        var (guard, guardBody, _) = Humans.Add(game, SkeletonBodyMode.Animated, Vector3.Zero, 0f, name: "guard");
        _guard = new CircleWalk(guard, guardBody, new Vector3(-1.4f, 0f, 4.4f), 1.3f, 1.35f);
        guard.Add(_guard);

        // A worm with a physical spine crawling at the back left
        PhysicalWorm.Add(game, Modes.Of(SkeletonBodyMode.Active), new Vector3(-3.6f, 0f, -4.0f), MathUtil.PiOverTwo, PhysicalWorm.Circle(new Vector3(-5.2f, 0f, -4.0f), 1.6f, 0.6f, 0.4f));

        // Two flags at the back
        var flags = new List<ClothComponent>();
        foreach (var (x, color) in new[] { (-2.6f, Look.Violet), (0.2f, Look.Pink) })
        {
            Props.Pillar(game, new Vector3(x, 0f, -6.2f), 0.05f, 3.6f, Look.Navy, 0.7f);
            var flag = new ClothComponent { Pins = { new SoftBodyPin { Region = new BoundingBox(new Vector3(-0.82f, -1f, -1f), new Vector3(-0.78f, 1f, 1f)) } } };
            var entity = new Entity("flag") { new ModelComponent(Look.Model(game, new PlaneProceduralModel { Size = new Vector2(1.6f, 1f), Tessellation = new Int2(22, 14), Normal = NormalDirection.UpZ, GenerateBackFace = true }, color, 0.3f)), flag };
            entity.Transform.Position = new Vector3(x + 0.85f, 3.05f, -6.2f);
            game.AddToScene(entity);
            flags.Add(flag);
        }

        // See-through jellies on a pedestal at the front right
        Look.StaticBox(game, new Vector3(3.0f, 0.3f, 1.4f), new Vector3(4.6f, 0.15f, 4.2f), Look.Paint(device, Look.Ivory, 0.55f), "pedestal");
        var jellyColors = new[] { new Color(255, 90, 140), new Color(130, 230, 100), new Color(70, 200, 240) };
        for (int i = 0; i < jellyColors.Length; i++)
        {
            var medium = i == 1 ? MaterialVolumeMedium.Absorbing : MaterialVolumeMedium.Scattering;
            var jelly = new Entity("jelly")
            {
                new ModelComponent(Look.Model(game, new CubeProceduralModel { Size = new Vector3(0.75f), Subdivisions = 8 }, Look.Volume(device, jellyColors[i], 0.85f, 0.35f, medium))),
                new VolumetricSoftBodyComponent { Resolution = 5, Stiffness = 7f, ShearStiffness = 7f, Damping = 0.3f },
            };
            jelly.Transform.Position = new Vector3(3.7f + i * 0.9f, 0.8f + i * 0.9f, 4.2f);
            jelly.Transform.Rotation = Quaternion.RotationYawPitchRoll(i, i * 0.5f, 0f);
            game.AddToScene(jelly);
        }

        // A striped rope between a twister and a block, in front of the jellies
        var rope = new SoftPiece(game, new Vector3(2.6f, 1.0f, 5.6f), new Vector3(5.0f, 1.0f, 5.6f), Look.Violet, Look.Sun);

        // A kinematic arm at the right, batting what rolls off the hill
        Look.StaticBox(game, new Vector3(0.25f, 0.6f, 0.25f), new Vector3(5.6f, 0.3f, -1.2f), Look.Paint(device, Look.Navy, 0.4f), "arm axle");
        var arm = new BodyComponent { Kinematic = true, Collider = new CompoundCollider { Colliders = { new BoxCollider { Size = new Vector3(2.4f, 0.18f, 0.35f) } } } };
        var armEntity = new Entity("kinematic arm") { new ModelComponent(Look.Model(game, new CubeProceduralModel { Size = new Vector3(2.4f, 0.18f, 0.35f) }, Look.Wood, 0.6f)), arm };
        armEntity.Transform.Position = new Vector3(5.6f, 0.75f, -1.2f);
        game.AddToScene(armEntity);

        game.AddToScene(new Entity("wind and arm")
        {
            new StepDriver((time, _) =>
            {
                for (int i = 0; i < flags.Count; i++)
                    flags[i].Wind = new Vector3(7f + 3f * MathF.Sin(time * 0.8f + i), 0.4f, 2f * MathF.Sin(time * 0.5f + i));
                rope.Drive(time);
                arm.SetTargetPose(new Vector3(5.6f, 0.75f, -1.2f), Quaternion.RotationY(time * 1.6f));
            }),
        });
    }

    public override void UpdateInput(VideoDemoGame game)
    {
        if (_hill is not null && game.Input.IsKeyPressed(Keys.X))
            CarveAtCrosshair(game, _hill);
        if (game.Input.IsKeyPressed(Keys.B))
            _cannon?.FireNow();
        if (game.Input.IsKeyPressed(Keys.U))
            _guard?.Restore();
        if (game.Input.IsKeyPressed(Keys.L) && _humans.Count > 0)
            _humans.OrderBy(w => Vector3.Distance(w.Hips, game.CameraPosition)).First().DropFrom(6f);
    }

    /// <summary> Digs a crater where the crosshair meets the terrain </summary>
    public static void CarveAtCrosshair(VideoDemoGame game, VoxelTerrain terrain)
    {
        var camera = game.SceneSystem.SceneInstance.RootScene.Entities.First(e => e.Get<CameraComponent>() is not null).Transform.WorldMatrix;
        var forward = Vector3.Normalize(Vector3.TransformNormal(-Vector3.UnitZ, camera));
        var simulation = terrain.Entity.Get<StaticComponent>()!.Simulation!;
        if (simulation.RayCast(camera.TranslationVector, forward, 200f, out var hit) && hit.Collidable.Entity == terrain.Entity)
            terrain.Carve(hit.Point, 1.6f, 1.1f);
    }

    public override IEnumerable<(Vector3 World, string Text)> Labels()
    {
        yield return (new Vector3(6.4f, 2.4f, -4.6f), "VoxelCollider hill (X: crater)");
    }

    public override IEnumerable<string> Status()
    {
        yield return $"{_humans.Count} Standing humans, {_beans.Count} Standing beans, guard {(_guard?.Body.Mode ?? SkeletonBodyMode.Animated)}  |  cannonballs fired: {_cannon?.Shots ?? 0}  |  craters: {_hill?.Craters ?? 0}";
    }
}
