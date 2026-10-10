// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Stride.BepuPhysics.Definitions.Colliders;
using Stride.BepuPhysics.Components;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics.GeometricPrimitives;
using Stride.Harness.Demo;
using Stride.Rendering;
using Stride.Rendering.ProceduralModels;

namespace Stride.BepuPhysics.Soft.Tests.VideoDemo;

/// <summary> Soft bodies of every kind dropped into a tray, and the subdivided primitives they need to bend on screen </summary>
internal sealed class JellyScene : VideoScene
{
    private Spawner? _spawner;
    private readonly List<(Entity Entity, string Text, Vector3 Offset)> _labelled = new();

    public override string Title => "Corps mous volumiques + subdivisions des primitives";
    public override string Pr => "PR #3528, #3564";
    public override string Description => "les gelées s'écrasent et rebondissent ; à droite, LengthRings et Subdivisions permettent au maillage de se plier";
    public override Vector3 Eye => new(0.9f, 3.0f, 7.8f);
    public override Vector3 Target => new(0.4f, 0.8f, 0.2f);

    protected override void BuildScene(VideoDemoGame game)
    {
        _labelled.Clear();
        // The tray, ivory, with low walls
        var tray = Look.Paint(game.GraphicsDevice, Look.Ivory, 0.5f);
        var center = new Vector3(-2.6f, 0f, 0f);
        Look.StaticBox(game, new Vector3(4.4f, 0.2f, 4.4f), center + new Vector3(0f, 0.1f, 0f), tray, "plateau");
        foreach (var (offset, size) in new[] { (new Vector3(0f, 0.45f, 2.1f), new Vector3(4.4f, 0.5f, 0.2f)), (new Vector3(0f, 0.45f, -2.1f), new Vector3(4.4f, 0.5f, 0.2f)), (new Vector3(2.1f, 0.45f, 0f), new Vector3(0.2f, 0.5f, 4.0f)), (new Vector3(-2.1f, 0.45f, 0f), new Vector3(0.2f, 0.5f, 4.0f)) })
            Look.StaticBox(game, size, center + offset, tray, "bord");

        _spawner = new Spawner(game, i => Jelly(game, i), i => center + new Vector3(MathF.Sin(i * 2.3f) * 1.1f, 4.2f, MathF.Cos(i * 1.7f) * 1.1f), 1.1f, 11, center, 5f);
        game.AddToScene(new Entity("distributeur de gelées") { _spawner });

        // Two soft capsules held at one end over a post: only the one with rings along its length can bend
        var posts = Look.Paint(game.GraphicsDevice, Look.Navy, 0.35f);
        foreach (var (rings, z, color) in new[] { (0, -2.0f, Look.Blue), (16, -0.8f, Look.Violet) })
        {
            Look.StaticBox(game, new Vector3(0.5f, 1.4f, 0.5f), new Vector3(0.4f, 0.7f, z), posts, "poteau");
            var body = new VolumetricSoftBodyComponent { Resolution = 12, Stiffness = 28f, ShearStiffness = 28f, VolumeStiffness = 20f, Damping = 0.2f, Mass = 1.5f };
            // The capsule runs along its local Y: turned to lie along +X, its bottom end inside the post is pinned
            body.Pins.Add(new SoftBodyPin { Region = new BoundingBox(new Vector3(-1f, -1.2f, -1f), new Vector3(1f, -0.65f, 1f)) });
            var capsule = new Entity("capsule molle")
            {
                new ModelComponent(Look.Model(game, new CapsuleProceduralModel { Length = 1.8f, Radius = 0.2f, Tessellation = 20, LengthRings = rings }, color, 0.6f)),
                body,
            };
            capsule.Transform.Position = new Vector3(1.3f, 1.65f, z);
            capsule.Transform.Rotation = Quaternion.RotationZ(-MathUtil.PiOverTwo);
            game.AddToScene(capsule);
            _labelled.Add((capsule, $"LengthRings = {rings}", Vector3.Zero));
        }

        // Two soft cubes pressed by a piston: the cube without subdivisions cannot bulge
        var piston = new BodyComponent { Kinematic = true, Collider = new CompoundCollider { Colliders = { new BoxCollider { Size = new Vector3(2.6f, 0.2f, 1.0f) } } } };
        var pistonEntity = new Entity("piston") { new ModelComponent(Look.Model(game, new CubeProceduralModel { Size = new Vector3(2.6f, 0.2f, 1.0f) }, Look.Stone, 0.6f)), piston };
        pistonEntity.Transform.Position = new Vector3(2.9f, 1.75f, 1.3f);
        game.AddToScene(pistonEntity);
        foreach (var (subdivisions, x, color) in new[] { (0, 2.3f, Look.Sun), (6, 3.5f, Look.Coral) })
        {
            var cube = new Entity("cube mou")
            {
                new ModelComponent(Look.Model(game, new CubeProceduralModel { Size = new Vector3(0.8f), Subdivisions = subdivisions }, color, 0.7f)),
                new VolumetricSoftBodyComponent { Resolution = 6, Stiffness = 9f, Damping = 0.3f },
            };
            cube.Transform.Position = new Vector3(x, 0.45f, 1.3f);
            game.AddToScene(cube);
            _labelled.Add((cube, $"Subdivisions = {subdivisions}", new Vector3(0f, 0.85f, 0f)));
        }
        game.AddToScene(new Entity("piston et pichenettes")
        {
            new StepDriver((time, _) =>
            {
                // Down to squash the cubes to half their height, then back up, every 3 s
                float phase = time % 3f;
                float press = phase < 1.2f ? MathF.Sin(phase / 1.2f * MathF.PI) : 0f;
                piston.SetTargetPose(new Vector3(2.9f, 1.75f - press * 1.15f, 1.3f));
                // A flick under the tip of each capsule every 4 s keeps them swinging
                if (time % 4f < 1f / 60f)
                {
                    foreach (var soft in _labelled.Select(l => l.Entity.Get<VolumetricSoftBodyComponent>()).Where(b => b is { ParticleCount: > 0 } && b.Pins.Count > 0))
                    {
                        for (int p = 0; p < soft!.ParticleCount; p++)
                        {
                            if (soft.GetParticlePosition(p).X > 1.9f)
                                soft.SetParticleVelocity(p, new Vector3(0f, 3.2f, 0f));
                        }
                    }
                }
            }),
        });
    }

    private static Entity Jelly(DemoGame game, int index)
    {
        var color = Look.Accents[index % Look.Accents.Length];
        PrimitiveProceduralModelBase shape = (index % 4) switch
        {
            0 => new CubeProceduralModel { Size = new Vector3(0.7f), Subdivisions = 6 },
            1 => new SphereProceduralModel { Radius = 0.42f, Tessellation = 28 },
            2 => new TorusProceduralModel { Radius = 0.38f, Thickness = 0.17f, Tessellation = 32 },
            _ => new CapsuleProceduralModel { Radius = 0.25f, Length = 0.5f, Tessellation = 20, LengthRings = 8 },
        };
        return new Entity("gelée")
        {
            new ModelComponent(Look.Model(game, shape, color, 0.75f)),
            new VolumetricSoftBodyComponent { Resolution = 5, Stiffness = 8f, ShearStiffness = 8f, Damping = 0.3f },
        };
    }

    public override IEnumerable<(Vector3 World, string Text)> Labels()
    {
        foreach (var (entity, text, offset) in _labelled)
            yield return (text.StartsWith("Length", StringComparison.Ordinal) ? (text.EndsWith(" 0", StringComparison.Ordinal) ? new Vector3(1.6f, 2.35f, entity.Transform.Position.Z) : new Vector3(2.4f, 0.9f, entity.Transform.Position.Z)) : new Vector3(entity.Transform.Position.X, 1.1f, 1.3f), text);
    }

    public override IEnumerable<string> Status()
    {
        yield return $"{_spawner?.Items.Count ?? 0} gelées en circulation (recyclées en continu)  |  capsules : LengthRings 0 / 16  |  cubes : Subdivisions 0 / 6";
    }
}

/// <summary> Flags in gusts of wind, a curtain taking balls, and a tent lifted by a kinematic sphere </summary>
internal sealed class ClothScene : VideoScene
{
    public override string Title => "Tissus";
    public override string Pr => "PR #3528";
    public override string Description => "drapeaux en rafales, rideau qui encaisse des balles, tente soulevée par une sphère cinématique";
    public override Vector3 Eye => new(0.5f, 3.2f, 8.5f);
    public override Vector3 Target => new(0f, 1.6f, -0.5f);

    private readonly List<ClothComponent> _flags = new();

    protected override void BuildScene(VideoDemoGame game)
    {
        _flags.Clear();
        var navy = Look.Paint(game.GraphicsDevice, Look.Navy, 0.4f);

        // Three flags, each pinned along the pole side
        var flagColors = new[] { Look.Coral, Look.Sun, Look.Teal };
        for (int i = 0; i < 3; i++)
        {
            var foot = new Vector3(-4.2f + i * 1.9f, 0f, -2.4f);
            Props.Pillar(game, foot, 0.05f, 3.4f, Look.Navy);
            var flag = new ClothComponent { Pins = { new SoftBodyPin { Region = new BoundingBox(new Vector3(-0.82f, -1f, -1f), new Vector3(-0.78f, 1f, 1f)) } }, Wind = new Vector3(7f, 0f, 1f) };
            var entity = new Entity("drapeau")
            {
                new ModelComponent(Look.Model(game, new PlaneProceduralModel { Size = new Vector2(1.6f, 1f), Tessellation = new Int2(24, 16), Normal = NormalDirection.UpZ, GenerateBackFace = true }, flagColors[i], 0.3f)),
                flag,
            };
            entity.Transform.Position = foot + new Vector3(0.85f, 2.85f, 0f);
            game.AddToScene(entity);
            _flags.Add(flag);
        }

        // A curtain hanging between two posts, a ball thrown into it every 2.5 s
        foreach (var x in new[] { 2.0f, 4.4f })
            Look.StaticBox(game, new Vector3(0.12f, 3f, 0.12f), new Vector3(x, 1.5f, -0.6f), navy, "poteau");
        var curtain = new ClothComponent
        {
            Pins =
            {
                new SoftBodyPin { Region = new BoundingBox(new Vector3(-1.25f, 1.1f, -1f), new Vector3(-1.1f, 1.25f, 1f)) },
                new SoftBodyPin { Region = new BoundingBox(new Vector3(1.1f, 1.1f, -1f), new Vector3(1.25f, 1.25f, 1f)) },
                new SoftBodyPin { Region = new BoundingBox(new Vector3(-0.05f, 1.1f, -1f), new Vector3(0.05f, 1.25f, 1f)) },
            },
        };
        var curtainEntity = new Entity("rideau") { new ModelComponent(Look.Model(game, new PlaneProceduralModel { Size = new Vector2(2.4f), Tessellation = new Int2(28), Normal = NormalDirection.UpZ, GenerateBackFace = true }, Look.Violet, 0.3f)), curtain };
        curtainEntity.Transform.Position = new Vector3(3.2f, 1.75f, -0.6f);
        game.AddToScene(curtainEntity);
        var ball = Props.Ball(game, 0.2f, 3f, Look.Sun, new Vector3(3.2f, 1f, 4f));
        game.AddToScene(ball);

        // A tent pinned at its four corners, lifted from below by a kinematic sphere
        foreach (var (x, z) in new[] { (-4.4f, 0.4f), (-1.6f, 0.4f), (-4.4f, 3.2f), (-1.6f, 3.2f) })
            Look.StaticBox(game, new Vector3(0.12f, 1.1f, 0.12f), new Vector3(x, 0.55f, z), navy, "piquet");
        var tent = new ClothComponent
        {
            Pins =
            {
                new SoftBodyPin { Region = new BoundingBox(new Vector3(-1.5f, -1f, -1.5f), new Vector3(-1.35f, 1f, -1.35f)) },
                new SoftBodyPin { Region = new BoundingBox(new Vector3(1.35f, -1f, -1.5f), new Vector3(1.5f, 1f, -1.35f)) },
                new SoftBodyPin { Region = new BoundingBox(new Vector3(-1.5f, -1f, 1.35f), new Vector3(-1.35f, 1f, 1.5f)) },
                new SoftBodyPin { Region = new BoundingBox(new Vector3(1.35f, -1f, 1.35f), new Vector3(1.5f, 1f, 1.5f)) },
            },
        };
        var tentEntity = new Entity("tente") { new ModelComponent(Look.Model(game, new PlaneProceduralModel { Size = new Vector2(2.9f), Tessellation = new Int2(32), Normal = NormalDirection.UpY, GenerateBackFace = true }, Look.Mint, 0.3f)), tent };
        tentEntity.Transform.Position = new Vector3(-3.0f, 1.12f, 1.8f);
        game.AddToScene(tentEntity);
        var lifter = new BodyComponent { Kinematic = true, Collider = new CompoundCollider { Colliders = { new SphereCollider { Radius = 0.45f } } } };
        var lifterEntity = new Entity("sphère cinématique") { new ModelComponent(Look.Model(game, new SphereProceduralModel { Radius = 0.45f, Tessellation = 32 }, Look.Pink, 0.8f)), lifter };
        lifterEntity.Transform.Position = new Vector3(-3.0f, 0.45f, 1.8f);
        game.AddToScene(lifterEntity);

        var ballBody = ball.Get<BodyComponent>()!;
        game.AddToScene(new Entity("vent, balle et sphère")
        {
            new StepDriver((time, step) =>
            {
                // Gusts: the same wind for all flags, with a phase each, so they wave out of step
                for (int i = 0; i < _flags.Count; i++)
                {
                    float gust = 0.55f + 0.45f * MathF.Sin(time * 0.9f + i * 1.3f) * MathF.Sin(time * 0.37f + i);
                    _flags[i].Wind = new Vector3(9f * gust + 2f, 0.5f, 2.5f * MathF.Sin(time * 0.6f + i));
                }
                // The sphere rises under the tent and goes back down, every 5 s; it moves sideways a little so the peak travels
                float phase = time % 5f;
                float lift = phase < 3f ? MathF.Sin(phase / 3f * MathF.PI) : 0f;
                lifter.SetTargetPose(new Vector3(-3.0f + 0.6f * MathF.Sin(time * 0.4f), 0.45f + lift * 1.3f, 1.8f + 0.5f * MathF.Cos(time * 0.3f)));
                // A ball into the curtain every 2.5 s, from a point that moves along the curtain
                if (time % 2.5f < step)
                {
                    float x = 3.2f + 0.8f * MathF.Sin(time * 0.7f);
                    ballBody.Teleport(new Vector3(x, 1.2f, 3.5f), Quaternion.Identity);
                    ballBody.AngularVelocity = Vector3.Zero;
                    ballBody.LinearVelocity = new Vector3(0f, 3.2f, -10f);
                    ballBody.Awake = true;
                }
            }),
        });
    }

    public override IEnumerable<(Vector3 World, string Text)> Labels()
    {
        yield return (new Vector3(-2.3f, 3.75f, -2.4f), "ClothComponent.Wind (rafales)");
        yield return (new Vector3(3.2f, 3.3f, -0.6f), "épinglé en 3 points");
        yield return (new Vector3(-3.0f, 0.25f, 3.6f), "4 coins épinglés + sphère cinématique");
    }

    public override IEnumerable<string> Status()
    {
        if (_flags.Count > 0)
            yield return string.Format(CultureInfo.InvariantCulture, "vent du premier drapeau : {0:0.0} m/s", _flags[0].Wind.Length());
    }
}

/// <summary> Molluscs crawling on a skeleton the soft body follows (FollowSkeleton), one carrying a sail pinned to its head bone (AnchorNode) </summary>
internal sealed class MolluscScene : VideoScene
{
    private MolluscDriver? _driver;
    private readonly List<Entity> _molluscs = new();

    public override string Title => "Corps mous à os : FollowSkeleton, AnchorNode";
    public override string Pr => "PR à venir (sur #3528)";
    public override string Description => "la chair molle suit le squelette animé, se cogne et pousse les caisses ; le voile jaune est épinglé à l'os de la tête";
    public override Vector3 Eye => new(1.0f, 4.0f, 6.8f);
    public override Vector3 Target => new(0.3f, 0.3f, 0f);

    protected override void BuildScene(VideoDemoGame game)
    {
        _molluscs.Clear();
        _driver = new MolluscDriver();

        // A teal one around a pillar, pushing crates on its way
        var path = Mollusc.Circle(new Vector3(-1.6f, 0f, 0f), 1.9f, 0.75f, 0f);
        _molluscs.Add(Mollusc.Add(game, Look.Teal, _driver, path));
        Props.Pillar(game, new Vector3(-1.6f, 0f, 0f), 0.35f, 1.1f, Look.Ivory);
        var crates = new List<Entity>();
        for (int i = 0; i < 4; i++)
        {
            var angle = 1.0f + i * 1.4f;
            var crate = new Entity("caisse")
            {
                new BodyComponent { Collider = new CompoundCollider { Colliders = { new BoxCollider { Size = new Vector3(0.36f), Mass = 0.5f } } } },
                new ModelComponent(Look.Model(game, new CubeProceduralModel { Size = new Vector3(0.36f) }, Look.Wood, 0.35f)),
            };
            crate.Transform.Position = new Vector3(-1.6f + 1.9f * MathF.Cos(angle), 0.18f, -1.9f * MathF.Sin(angle));
            game.AddToScene(crate);
            crates.Add(crate);
        }

        // A coral one with a sail pinned to its head bone
        var sailPath = Mollusc.Circle(new Vector3(2.7f, 0f, -0.6f), 1.3f, 0.6f, 2f);
        var coral = Mollusc.Add(game, Look.Coral, _driver, sailPath);
        _molluscs.Add(coral);
        var (position, yaw) = sailPath(0f);
        var head = position + Mollusc.Forward(yaw) * (Mollusc.Length / 2f + Mollusc.Radius * 0.6f);
        var edge = new SoftBodyPin { Region = new BoundingBox(new Vector3(0.39f, -1f, -1f), new Vector3(0.41f, 1f, 1f)), Anchor = coral, AnchorNode = Mollusc.Head };
        var sail = new Entity("voile")
        {
            new ModelComponent(Look.Model(game, new PlaneProceduralModel { Size = new Vector2(0.8f, 0.55f), Tessellation = new Int2(16, 10), Normal = NormalDirection.UpZ, GenerateBackFace = true }, Look.Sun, 0.3f)),
            new ClothComponent { Pins = { edge } },
        };
        sail.Transform.Position = head - Mollusc.Forward(yaw) * 0.4f + new Vector3(0f, Mollusc.Radius + 0.25f, 0f);
        sail.Transform.Rotation = Quaternion.RotationY(yaw);
        game.AddToScene(sail);

        // A violet one crawling back and forth into a wall
        var wallPath = Mollusc.BackAndForth(new Vector3(0.6f, 0f, 2.75f), 2.2f, 0.7f);
        _molluscs.Add(Mollusc.Add(game, Look.Violet, _driver, wallPath));
        Look.StaticBox(game, new Vector3(0.35f, 1f, 1.6f), new Vector3(2.65f, 0.5f, 2.75f), Look.Paint(game.GraphicsDevice, Look.Ivory, 0.4f), "mur");

        game.AddToScene(new Entity("molluscs") { _driver });
        // Crates pushed away from the path come back in front of the teal one
        game.AddToScene(new Entity("caisses") { new CrateKeeper(crates, path, _driver) });
    }

    public override IEnumerable<(Vector3 World, string Text)> Labels()
    {
        if (_molluscs.Count == 3)
        {
            yield return (_molluscs[0].Transform.Position + new Vector3(0f, 0.75f, 0f), "FollowSkeleton");
            yield return (_molluscs[1].Transform.Position + new Vector3(0f, 1.05f, 0f), "voile : AnchorNode = os de la tête");
            yield return (new Vector3(2.65f, 1.3f, 2.75f), "écrasé contre le mur");
        }
    }

    public override IEnumerable<string> Status()
    {
        yield return $"{_driver?.Count ?? 0} mollusques, squelette de {Mollusc.Bones} os, capsule à LengthRings = 16 (#3564)";
    }

    /// <summary> Puts crates that left the teal mollusc's circle back ahead of it, once they rest </summary>
    private sealed class CrateKeeper(List<Entity> crates, Func<float, (Vector3 Position, float Yaw)> path, MolluscDriver driver) : SyncScript
    {
        public override void Update()
        {
            foreach (var crate in crates)
            {
                var body = crate.Get<BodyComponent>()!;
                if (body.Simulation is null)
                    continue;
                var flat = new Vector2(body.Position.X + 1.6f, body.Position.Z);
                if (MathF.Abs(flat.Length() - 1.9f) > 0.9f && body.LinearVelocity.Length() < 0.05f)
                {
                    var (ahead, _) = path(driver.Time + 2.5f);
                    Props.Respawn(crate, new Vector3(ahead.X, 0.6f, ahead.Z), Quaternion.Identity);
                }
            }
        }
    }
}

/// <summary> A mollusc made in code: a capsule over a chain of bones, crawling by a wave running along its body </summary>
internal static class Mollusc
{
    public const int Bones = 8;
    public const float Radius = 0.22f;
    public const float Length = 1.8f;
    public const string Head = "Bone7";

    /// <summary> A capsule along Y over <see cref="Bones"/> nodes from its tail to its head, with rings along its length so it can bend </summary>
    public static Model Model(DemoGame game, Color color)
    {
        var model = Look.Model(game, new CapsuleProceduralModel { Length = Length, Radius = Radius, Tessellation = 20, LengthRings = 16 }, color, 0.75f);
        var nodes = new ModelNodeDefinition[Bones + 1];
        nodes[0] = new ModelNodeDefinition { Name = "Root", ParentIndex = -1, Transform = { Scale = Vector3.One, Rotation = Quaternion.Identity }, Flags = ModelNodeFlags.Default };
        float half = Length / 2f + Radius * 0.6f, step = 2f * half / (Bones - 1);
        for (int i = 1; i <= Bones; i++)
        {
            var position = i == 1 ? new Vector3(0f, -half, 0f) : new Vector3(0f, step, 0f);
            nodes[i] = new ModelNodeDefinition { Name = "Bone" + (i - 1), ParentIndex = i - 1, Transform = { Position = position, Scale = Vector3.One, Rotation = Quaternion.Identity }, Flags = ModelNodeFlags.Default };
        }
        model.Skeleton = new Skeleton { Nodes = nodes };
        return model;
    }

    public static VolumetricSoftBodyComponent Body() => new() { Resolution = 14, FollowSkeleton = true, SkeletonStiffness = 3f, SkeletonDamping = 0.35f, Mass = 4f };

    /// <summary> The model's Y axis turned to the horizontal heading <paramref name="yaw"/> (zero is +X) </summary>
    public static Quaternion Orientation(float yaw) => Quaternion.RotationMatrix(Matrix.RotationZ(-MathUtil.PiOverTwo) * Matrix.RotationY(yaw));

    public static Vector3 Forward(float yaw) => new(MathF.Cos(yaw), 0f, -MathF.Sin(yaw));

    /// <summary> Crawls around a circle at <paramref name="speed"/> meters per second, counter clockwise seen from above </summary>
    public static Func<float, (Vector3 Position, float Yaw)> Circle(Vector3 center, float radius, float speed, float start) => time =>
    {
        var angle = start + speed * time / radius;
        return (center + new Vector3(radius * MathF.Cos(angle), Radius + 0.02f, -radius * MathF.Sin(angle)), angle + MathUtil.PiOverTwo);
    };

    /// <summary> Crawls along +X from <paramref name="start"/> until its head is pressed into a wall, then backs off, forever </summary>
    public static Func<float, (Vector3 Position, float Yaw)> BackAndForth(Vector3 start, float distance, float speed) => time =>
    {
        float period = 2f * distance / speed + 2f;
        float t = time % period;
        float x = t < distance / speed ? t * speed : t < distance / speed + 1.2f ? distance : t < 2f * distance / speed + 1.2f ? distance - (t - distance / speed - 1.2f) * speed : 0f;
        return (start + new Vector3(x - Length / 2f - Radius, Radius + 0.02f, 0f), 0f);
    };

    public static Entity Add(DemoGame game, Color color, MolluscDriver driver, Func<float, (Vector3 Position, float Yaw)> path)
    {
        var entity = new Entity("mollusque") { new ModelComponent(Model(game, color)), Body() };
        var (position, yaw) = path(0f);
        entity.Transform.Position = position;
        entity.Transform.Rotation = Orientation(yaw);
        game.AddToScene(entity);
        driver.Add(entity, path);
        return entity;
    }
}

/// <summary> Moves each mollusc along its path and plays its crawl, before each physics step </summary>
internal sealed class MolluscDriver : ScriptComponent, ISimulationUpdate
{
    private readonly List<(Entity Entity, Func<float, (Vector3 Position, float Yaw)> Path)> _molluscs = new();

    public int Count => _molluscs.Count;

    public float Time { get; private set; }

    public void Add(Entity entity, Func<float, (Vector3 Position, float Yaw)> path) => _molluscs.Add((entity, path));

    public void SimulationUpdate(BepuSimulation simulation, float simTimeStep)
    {
        Time += simTimeStep;
        foreach (var (entity, path) in _molluscs)
        {
            var (position, yaw) = path(Time);
            entity.Transform.Position = position;
            entity.Transform.Rotation = Mollusc.Orientation(yaw);
            Animate(entity.Get<ModelComponent>()!.Skeleton, Time);
        }
    }

    public void AfterSimulationUpdate(BepuSimulation simulation, float simTimeStep) { }

    /// <summary> A wave of the heading running from the head to the tail </summary>
    public static void Animate(SkeletonUpdater skeleton, float time)
    {
        var nodes = skeleton.NodeTransformations;
        float parentHeading = 0f;
        for (int bone = 1; bone < nodes.Length; bone++)
        {
            float heading = 0.5f * MathF.Sin(MathUtil.TwoPi * 0.7f * time - bone * 0.9f);
            nodes[bone].Transform.Rotation = Quaternion.RotationX(heading - parentHeading);
            parentHeading = heading;
        }
    }
}
