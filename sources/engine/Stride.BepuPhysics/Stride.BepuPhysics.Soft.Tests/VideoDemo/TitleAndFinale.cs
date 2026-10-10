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
using Stride.Graphics.GeometricPrimitives;
using Stride.Rendering.Materials;
using Stride.Rendering.ProceduralModels;

namespace Stride.BepuPhysics.Soft.Tests.VideoDemo;

/// <summary> The opening shot: a small diorama behind a card listing every pull request in this build </summary>
internal sealed class TitleScene : VideoScene
{
    private static readonly (string Section, string[] Lines)[] Card =
    {
        ("Physics (Bepu)", new[]
        {
            "Volumetric soft bodies and cloth  ·  #3528",
            "Boned soft bodies: FollowSkeleton, AnchorNode  ·  upcoming",
            "Bone hitboxes (SkeletonBodyComponent)  ·  upcoming",
            "Passive, active and Standing ragdolls  ·  upcoming",
            "VoxelCollider  ·  #3471",
            "Debug wireframes no longer show through  ·  #3565",
            "SetTargetPose reaches the orientation in one step  ·  #3563",
        }),
        ("Rendering", new[]
        {
            "Procedural primitive subdivisions  ·  #3564",
            "Opacity by thickness (OpacityThickness)  ·  #3530",
            "\"Shadow map view\" flag for materials  ·  #3387",
        }),
        ("Fixes included in this build", new[]
        {
            "Direct3D12: MSAA resolves, barriers, UAVs  ·  #3557, #3560",
            "Vulkan: per-subresource layouts, labels  ·  #3559, #3562",
            "Debug names after creation  ·  #3558   |   typed UAVs  ·  #3469",
            "Direct3D11: compute UAV read elsewhere   |   GPU tests  ·  #3561",
            "Build: dev package cleanup   |   Game Studio: recent projects",
        }),
    };

    private SkeletonBodyComponent? _standing;

    public override string Title => "Stride: my open PRs";
    public override string Pr => "one build with all of them";
    public override string Description => "Esc: scene menu  |  H: hide the overlay to film";
    // Looking left of the diorama, so it stands in the right half of the image, clear of the card
    public override Vector3 Eye => new(-2.9f, 2.5f, 5.9f);
    public override Vector3 Target => new(-2.6f, 1.15f, -0.4f);

    protected override void BuildScene(VideoDemoGame game)
    {
        var device = game.GraphicsDevice;
        var ivory = Look.Paint(device, Look.Ivory, 0.5f);

        // Three pedestals: a jelly under a slow piston, a Standing ragdoll, a flag
        Look.StaticBox(game, new Vector3(1.2f, 0.6f, 1.2f), new Vector3(-0.6f, 0.3f, 0f), ivory, "socle");
        Look.StaticBox(game, new Vector3(1.2f, 0.4f, 1.2f), new Vector3(1.0f, 0.2f, -1.2f), ivory, "socle");
        Look.StaticBox(game, new Vector3(1.2f, 0.3f, 1.2f), new Vector3(2.4f, 0.15f, 0.4f), ivory, "socle");

        var jelly = new Entity("gelée")
        {
            new ModelComponent(Look.Model(game, new CubeProceduralModel { Size = new Vector3(0.8f), Subdivisions = 8 }, Look.Coral, 0.8f)),
            new VolumetricSoftBodyComponent { Resolution = 6, Stiffness = 7f, ShearStiffness = 7f, Damping = 0.25f },
        };
        jelly.Transform.Position = new Vector3(-0.6f, 1.05f, 0f);
        game.AddToScene(jelly);
        var press = new BodyComponent { Kinematic = true, Collider = new CompoundCollider { Colliders = { new SphereCollider { Radius = 0.3f } } } };
        var pressEntity = new Entity("presse") { new ModelComponent(Look.Model(game, new SphereProceduralModel { Radius = 0.3f, Tessellation = 32 }, Look.Blue, 0.7f)), press };
        pressEntity.Transform.Position = new Vector3(-0.6f, 2.4f, 0f);
        game.AddToScene(pressEntity);

        _standing = ProceduralCharacter.CreateHitboxes();
        _standing.Mode = SkeletonBodyMode.Standing;
        var character = ProceduralCharacter.Create(ProceduralCharacter.CreateModel(device, Look.Paint(device, Look.Teal, 0.5f)), new ProceduralCharacter.Animator { Paused = true }, _standing);
        character.Transform.Position = new Vector3(1.0f, 0.4f, -1.2f);
        character.Transform.Rotation = Quaternion.RotationY(0.6f);
        game.AddToScene(character);

        Props.Pillar(game, new Vector3(2.0f, 0.3f, 0.4f), 0.05f, 2.6f, Look.Navy);
        var flag = new ClothComponent { Pins = { new SoftBodyPin { Region = new BoundingBox(new Vector3(-0.72f, -1f, -1f), new Vector3(-0.68f, 1f, 1f)) } } };
        var flagEntity = new Entity("drapeau") { new ModelComponent(Look.Model(game, new PlaneProceduralModel { Size = new Vector2(1.4f, 0.9f), Tessellation = new Int2(22, 14), Normal = NormalDirection.UpZ, GenerateBackFace = true }, Look.Sun, 0.3f)), flag };
        flagEntity.Transform.Position = new Vector3(2.75f, 2.45f, 0.4f);
        game.AddToScene(flagEntity);

        game.AddToScene(new Entity("animation")
        {
            new StepDriver((time, _) =>
            {
                float phase = time % 3.5f;
                float down = phase < 1.4f ? MathF.Sin(phase / 1.4f * MathF.PI) : 0f;
                press.SetTargetPose(new Vector3(-0.6f, 2.0f - down * 0.55f, 0f));
                flag.Wind = new Vector3(6f + 3f * MathF.Sin(time * 0.8f), 0.4f, 2f * MathF.Sin(time * 0.5f));
            }),
        });
    }

    public override void DrawOverlay(VideoDemoGame game)
    {
        // A card on the left: title, then one line per pull request, grouped
        float x = 40f, y = 70f;
        float width = 0f;
        int lineCount = Card.Sum(section => section.Lines.Length + 1);
        foreach (var (section, lines) in Card)
        {
            width = MathF.Max(width, game.Measure(section, 19, true).X);
            foreach (var line in lines)
                width = MathF.Max(width, game.Measure(line, 16).X + 18f);
        }
        float height = 58f + lineCount * 22f + Card.Length * 8f;
        game.Panel(new RectangleF(x - 20f, y - 20f, width + 40f, height + 20f), new Color(18, 20, 32, 190));
        game.Write("Stride: my open PRs", new Vector2(x, y - 6f), new Color(255, 193, 69), 28, true);
        y += 42f;
        foreach (var (section, lines) in Card)
        {
            game.Write(section, new Vector2(x, y), new Color(46, 196, 182), 19, true);
            y += 24f;
            foreach (var line in lines)
            {
                game.Write("•  " + line, new Vector2(x + 6f, y), Color.White, 16);
                y += 21f;
            }
            y += 8f;
        }
    }
}

/// <summary> Everything at once in a playground: Standing ragdolls, a mollusc with a sail, flags, see-through jellies, a voxel hill, a kinematic arm </summary>
internal sealed class FinaleScene : VideoScene
{
    private readonly List<Walker> _walkers = new();
    private VoxelTerrain? _hill;
    private Cannon? _cannon;

    public override string Title => "Grand Finale";
    public override string Pr => "every PR at once";
    public override string Description => "wandering Standing ragdolls, sail mollusc, flags, translucent jellies, voxel hill, kinematic arm; grab anything with the crosshair";
    public override Vector3 Eye => new(1.5f, 6.5f, 13.5f);
    public override Vector3 Target => new(0f, 1.0f, -0.5f);
    public override void OnSimulationReady(BepuSimulation simulation) => PhysicalWorm.Filter(simulation);

    public override string SceneKeys => "X: crater in the hill  |  B: cannonball  |  L: drop a character from 6 m  |  M: character mode";

    protected override void BuildScene(VideoDemoGame game)
    {
        _walkers.Clear();
        var device = game.GraphicsDevice;
        var ivory = Look.Paint(device, Look.Ivory, 0.5f);

        // A voxel hill at the back right, balls rolling down it
        _hill = new VoxelTerrain(game, 36, 14, 0.35f, new Vector3(5.4f, 0f, -3.6f), (x, z) =>
        {
            float r = new Vector2(x, z).Length();
            return 3.6f * MathF.Exp(-r * r / 12f) + 0.25f * MathF.Sin(x * 1.3f) * MathF.Cos(z * 1.1f) + 0.2f;
        });
        var rain = new Spawner(game, i => Props.Ball(game, 0.2f, 1f, Look.Accents[i % Look.Accents.Length], Vector3.Zero), i => new Vector3(5.4f + MathF.Sin(i * 2.4f) * 0.8f, 6.5f, -3.6f + MathF.Cos(i * 2.4f) * 0.8f), 0.6f, 14, new Vector3(2f, 0f, -1f), 11f);
        game.AddToScene(new Entity("pluie") { rain });

        // Standing ragdolls walking in the middle
        var colors = new[] { Look.Coral, Look.Teal, Look.Violet };
        for (int i = 0; i < 3; i++)
        {
            var body = ProceduralCharacter.CreateHitboxes();
            body.Mode = SkeletonBodyMode.Standing;
            var animator = new ProceduralCharacter.Animator { Time = i * 0.5f };
            var character = ProceduralCharacter.Create(ProceduralCharacter.CreateModel(device, Look.Paint(device, colors[i], 0.5f)), animator, body);
            character.Transform.Position = new Vector3(-1.5f + i * 1.6f, 0f, 0.8f + (i % 2) * 0.8f);
            character.Transform.Rotation = Quaternion.RotationY(i * 2.1f);
            game.AddToScene(character);
            var walker = new Walker(character, body, animator, i * 2.1f, walks: true, new Vector3(0f, 0f, 1f), 2.6f);
            character.Add(walker);
            _walkers.Add(walker);
        }
        _cannon = Cannon.Build(game, _walkers, new Vector3(-6.0f, 0.75f, -3.6f), 6f);
        game.AddToScene(new Entity("ascenseur") { new Lifter(_walkers, 12f) });

        // A mollusc with a physical spine and its sail, crawling around the left
        float yaw = MathUtil.PiOverTwo;
        var mollusc = PhysicalWorm.Add(game, Look.Mint, new Vector3(-3.0f, 0f, -0.6f), yaw, PhysicalWorm.Circle(new Vector3(-4.6f, 0f, -0.6f), 1.7f, 0.6f, 0.4f));
        var head = mollusc.Transform.Position + Mollusc.Forward(yaw) * (Mollusc.Length / 2f + Mollusc.Radius * 0.6f);
        var sail = new Entity("voile")
        {
            new ModelComponent(Look.Model(game, new PlaneProceduralModel { Size = new Vector2(0.8f, 0.55f), Tessellation = new Int2(16, 10), Normal = NormalDirection.UpZ, GenerateBackFace = true }, Look.Pink, 0.3f)),
            new ClothComponent { CollisionLayer = PhysicalWorm.SailLayer, Pins = { new SoftBodyPin { Region = new BoundingBox(new Vector3(0.39f, -1f, -1f), new Vector3(0.41f, 1f, 1f)), Anchor = mollusc, AnchorNode = Mollusc.Head } } },
        };
        sail.Transform.Position = head - Mollusc.Forward(yaw) * 0.4f + new Vector3(0f, Mollusc.Radius + 0.25f, 0f);
        sail.Transform.Rotation = Quaternion.RotationY(yaw);
        game.AddToScene(sail);

        // Two flags at the back
        var flags = new List<ClothComponent>();
        foreach (var (x, color) in new[] { (-2.2f, Look.Coral), (0.4f, Look.Sun) })
        {
            Props.Pillar(game, new Vector3(x, 0f, -4.4f), 0.05f, 3.4f, Look.Navy);
            var flag = new ClothComponent { Pins = { new SoftBodyPin { Region = new BoundingBox(new Vector3(-0.82f, -1f, -1f), new Vector3(-0.78f, 1f, 1f)) } } };
            var entity = new Entity("drapeau") { new ModelComponent(Look.Model(game, new PlaneProceduralModel { Size = new Vector2(1.6f, 1f), Tessellation = new Int2(22, 14), Normal = NormalDirection.UpZ, GenerateBackFace = true }, color, 0.3f)), flag };
            entity.Transform.Position = new Vector3(x + 0.85f, 2.85f, -4.4f);
            game.AddToScene(entity);
            flags.Add(flag);
        }

        // See-through jellies on a pedestal at the front left
        Look.StaticBox(game, new Vector3(3.2f, 0.3f, 1.5f), new Vector3(-1.6f, 0.15f, 4.6f), ivory, "socle");
        var jellyColors = new[] { new Color(255, 90, 130), new Color(120, 230, 110), new Color(150, 110, 255) };
        for (int i = 0; i < 3; i++)
        {
            var medium = i == 1 ? MaterialVolumeMedium.Absorbing : MaterialVolumeMedium.Scattering;
            var jelly = new Entity("gelée translucide")
            {
                new ModelComponent(Look.Model(game, new CubeProceduralModel { Size = new Vector3(0.85f), Subdivisions = 8 }, Look.Volume(device, jellyColors[i], 0.6f, 0.45f, medium))),
                new VolumetricSoftBodyComponent { Resolution = 5, Stiffness = 7f, ShearStiffness = 7f, Damping = 0.3f },
            };
            jelly.Transform.Position = new Vector3(-2.6f + i * 1.0f, 0.8f + i * 1.1f, 4.6f);
            jelly.Transform.Rotation = Quaternion.RotationYawPitchRoll(i, i * 0.5f, 0f);
            game.AddToScene(jelly);
        }

        // A kinematic arm at the front right, batting the balls that roll off the hill
        Look.StaticBox(game, new Vector3(0.25f, 0.6f, 0.25f), new Vector3(3.6f, 0.3f, 2.4f), Look.Paint(device, Look.Navy, 0.4f), "axe");
        var arm = new BodyComponent { Kinematic = true, Collider = new CompoundCollider { Colliders = { new BoxCollider { Size = new Vector3(2.2f, 0.18f, 0.35f) } } } };
        var armEntity = new Entity("bras") { new ModelComponent(Look.Model(game, new CubeProceduralModel { Size = new Vector3(2.2f, 0.18f, 0.35f) }, Look.Blue, 0.6f)), arm };
        armEntity.Transform.Position = new Vector3(3.6f, 0.75f, 2.4f);
        game.AddToScene(armEntity);

        game.AddToScene(new Entity("vent et bras")
        {
            new StepDriver((time, _) =>
            {
                for (int i = 0; i < flags.Count; i++)
                    flags[i].Wind = new Vector3(7f + 3f * MathF.Sin(time * 0.8f + i), 0.4f, 2f * MathF.Sin(time * 0.5f + i));
                arm.SetTargetPose(new Vector3(3.6f, 0.75f, 2.4f), Quaternion.RotationY(time * 1.6f));
            }),
        });
    }

    public override void UpdateInput(VideoDemoGame game)
    {
        if (_hill is not null && game.Input.IsKeyPressed(Keys.X))
            VoxelScene.CarveAtCrosshair(game, _hill);
        if (game.Input.IsKeyPressed(Keys.B))
            _cannon?.FireNow();
        if (game.Input.IsKeyPressed(Keys.L) && _walkers.Count > 0)
            _walkers[0].DropFrom(6f);
        if (game.Input.IsKeyPressed(Keys.M) && _walkers.Count > 0)
        {
            var next = _walkers[0].Body.Mode == SkeletonBodyMode.Standing ? SkeletonBodyMode.Ragdoll : SkeletonBodyMode.Standing;
            foreach (var walker in _walkers)
                walker.Body.Mode = next;
        }
    }

    public override IEnumerable<string> Status()
    {
        yield return $"{_walkers.Count} ragdolls ({(_walkers.Count > 0 ? _walkers[0].Body.Mode : SkeletonBodyMode.Standing)})  |  voxel hill: {_hill?.Craters ?? 0} craters";
    }
}
