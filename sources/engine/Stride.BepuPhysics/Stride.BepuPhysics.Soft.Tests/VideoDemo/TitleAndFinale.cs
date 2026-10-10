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
        ("Physique (Bepu)", new[]
        {
            "Corps mous volumiques et tissus  ·  #3528",
            "Corps mous à os : FollowSkeleton, AnchorNode  ·  à venir",
            "Hitboxes d'os (SkeletonBodyComponent)  ·  à venir",
            "Ragdoll passif, actif et Standing  ·  à venir",
            "VoxelCollider  ·  #3471",
            "Wireframes de debug qui ne transparaissent plus  ·  #3565",
            "SetTargetPose atteint l'orientation en un pas  ·  #3563",
        }),
        ("Rendu", new[]
        {
            "Subdivisions des primitives procédurales  ·  #3564",
            "Opacité par épaisseur (OpacityThickness)  ·  #3530",
            "Indicateur « vue de shadow map » pour les matériaux  ·  #3387",
        }),
        ("Correctifs inclus dans ce build", new[]
        {
            "Direct3D12 : résolutions MSAA, barrières, UAV  ·  #3557, #3560",
            "Vulkan : layouts par sous-ressource, labels  ·  #3559, #3562",
            "Noms de debug après création  ·  #3558   |   UAV typés  ·  #3469",
            "Direct3D11 : UAV compute lue ailleurs   |   tests GPU  ·  #3561",
            "Build : nettoyage des dev packages   |   Game Studio : projets récents",
        }),
    };

    private SkeletonBodyComponent? _standing;

    public override string Title => "Stride : mes PR en cours";
    public override string Pr => "un build qui les réunit toutes";
    public override string Description => "Échap : menu des scènes  |  H : masquer l'overlay pour filmer";
    public override Vector3 Eye => new(-2.6f, 2.4f, 5.4f);
    public override Vector3 Target => new(-1.2f, 1.1f, -0.2f);

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
        var pressEntity = new Entity("presse") { new ModelComponent(Look.Model(game, new SphereProceduralModel { Radius = 0.3f, Tessellation = 32 }, Look.Navy, 0.7f)), press };
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
        float x = 40f, y = 150f;
        float width = 0f;
        int lineCount = Card.Sum(section => section.Lines.Length + 1);
        foreach (var (section, lines) in Card)
        {
            width = MathF.Max(width, game.Measure(section, 22, true).X);
            foreach (var line in lines)
                width = MathF.Max(width, game.Measure(line, 19).X + 18f);
        }
        float height = 70f + lineCount * 27f + Card.Length * 10f;
        game.Panel(new RectangleF(x - 20f, y - 20f, width + 40f, height + 20f), new Color(18, 20, 32, 190));
        game.Write("Stride : mes PR en cours", new Vector2(x, y - 6f), new Color(255, 193, 69), 34, true);
        y += 52f;
        foreach (var (section, lines) in Card)
        {
            game.Write(section, new Vector2(x, y), new Color(46, 196, 182), 22, true);
            y += 29f;
            foreach (var line in lines)
            {
                game.Write("•  " + line, new Vector2(x + 6f, y), Color.White, 19);
                y += 26f;
            }
            y += 10f;
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
    public override string Pr => "toutes les PR à la fois";
    public override string Description => "ragdolls Standing qui se baladent, mollusque à voile, drapeaux, gelées translucides, colline voxel, bras cinématique ; attrapez tout au réticule";
    public override Vector3 Eye => new(1.5f, 6.5f, 13.5f);
    public override Vector3 Target => new(0f, 1.0f, -0.5f);
    public override string SceneKeys => "X : cratère dans la colline  |  B : boulet  |  L : lâcher un perso de 6 m  |  M : mode des persos";

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
            var character = ProceduralCharacter.Create(ProceduralCharacter.CreateModel(device, Look.Paint(device, colors[i], 0.5f)), new ProceduralCharacter.Animator { Time = i * 0.5f }, body);
            character.Transform.Position = new Vector3(-1.5f + i * 1.6f, 0f, 0.8f + (i % 2) * 0.8f);
            character.Transform.Rotation = Quaternion.RotationY(i * 2.1f);
            game.AddToScene(character);
            var walker = new Walker(character, body, i * 2.1f, walks: true, new Vector3(0f, 0f, 1f), 2.6f);
            character.Add(walker);
            _walkers.Add(walker);
        }
        _cannon = new Cannon(game, _walkers, new Vector3(-6.5f, 0.6f, -4.5f), 6f);
        game.AddToScene(new Entity("canon") { new ModelComponent(Look.Model(game, new CylinderProceduralModel { Radius = 0.22f, Height = 0.9f, Tessellation = 24 }, Look.Navy, 0.6f)), _cannon });
        game.AddToScene(new Entity("ascenseur") { new Lifter(_walkers, 12f) });

        // A mollusc with its sail, crawling around the left
        var driver = new MolluscDriver();
        var path = Mollusc.Circle(new Vector3(-4.6f, 0f, -0.6f), 1.7f, 0.65f, 0f);
        var mollusc = Mollusc.Add(game, Look.Mint, driver, path);
        var (position, yaw) = path(0f);
        var head = position + Mollusc.Forward(yaw) * (Mollusc.Length / 2f + Mollusc.Radius * 0.6f);
        var sail = new Entity("voile")
        {
            new ModelComponent(Look.Model(game, new PlaneProceduralModel { Size = new Vector2(0.8f, 0.55f), Tessellation = new Int2(16, 10), Normal = NormalDirection.UpZ, GenerateBackFace = true }, Look.Pink, 0.3f)),
            new ClothComponent { Pins = { new SoftBodyPin { Region = new BoundingBox(new Vector3(0.39f, -1f, -1f), new Vector3(0.41f, 1f, 1f)), Anchor = mollusc, AnchorNode = Mollusc.Head } } },
        };
        sail.Transform.Position = head - Mollusc.Forward(yaw) * 0.4f + new Vector3(0f, Mollusc.Radius + 0.25f, 0f);
        sail.Transform.Rotation = Quaternion.RotationY(yaw);
        game.AddToScene(sail);
        game.AddToScene(new Entity("mollusque") { driver });

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
        Look.StaticBox(game, new Vector3(2.2f, 0.3f, 1.2f), new Vector3(-3.6f, 0.15f, 3.4f), ivory, "socle");
        var jellyColors = new[] { new Color(255, 90, 130), new Color(120, 230, 110), new Color(150, 110, 255) };
        for (int i = 0; i < 3; i++)
        {
            var medium = i == 1 ? MaterialVolumeMedium.Absorbing : MaterialVolumeMedium.Scattering;
            var jelly = new Entity("gelée translucide")
            {
                new ModelComponent(Look.Model(game, new CubeProceduralModel { Size = new Vector3(0.55f), Subdivisions = 6 }, Look.Volume(device, jellyColors[i], 0.6f, 0.35f, medium))),
                new VolumetricSoftBodyComponent { Resolution = 5, Stiffness = 7f, ShearStiffness = 7f, Damping = 0.3f },
            };
            jelly.Transform.Position = new Vector3(-4.3f + i * 0.7f, 0.65f + i * 0.9f, 3.4f);
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
        yield return $"{_walkers.Count} ragdolls ({(_walkers.Count > 0 ? _walkers[0].Body.Mode : SkeletonBodyMode.Standing)})  |  colline voxel : {_hill?.Craters ?? 0} cratères";
    }
}
