// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Stride.BepuPhysics.Definitions.Colliders;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Graphics.GeometricPrimitives;
using Stride.Harness.Demo;
using Stride.Extensions;
using Stride.Input;
using Stride.Rendering;
using Stride.Rendering.Materials;
using Stride.Rendering.ProceduralModels;

namespace Stride.BepuPhysics.Soft.Tests.VideoDemo;

/// <summary> Hills collided as a voxel density field, never meshed for the physics; a rain of objects rolls down, X digs craters live </summary>
internal sealed class VoxelScene : VideoScene
{
    private VoxelTerrain? _terrain;
    private Spawner? _rain;

    public override string Title => "VoxelCollider : terrain voxel sans maillage";
    public override string Pr => "PR #3471";
    public override string Description => "les objets roulent sur un champ de densité lu directement par Bepu ; X creuse un cratère au réticule, la collision suit sans reconstruction";
    public override Vector3 Eye => new(0f, 9.5f, 15.5f);
    public override Vector3 Target => new(0f, 1.6f, 0f);
    public override string SceneKeys => "X : creuser un cratère au réticule  |  V : les triangles que voit Bepu (surface nets)";

    protected override void BuildScene(VideoDemoGame game)
    {
        _terrain = new VoxelTerrain(game, 64, 20, 0.4f, Vector3.Zero, Hills);
        float half = _terrain.Size / 2f;
        _rain = new Spawner(game, i => Props.Rigid(game, i, Vector3.Zero, Quaternion.Identity, 1.1f),
            i => new Vector3(MathF.Sin(i * 2.399f) * half * 0.75f, 9f, MathF.Cos(i * 2.399f) * half * 0.75f), 0.25f, 45, Vector3.Zero, half * 1.15f);
        game.AddToScene(new Entity("pluie") { _rain });
    }

    // Rolling hills around a bowl, high enough to send the rain rolling
    public static float Hills(float x, float z)
    {
        float r = new Vector2(x, z).Length();
        return 2.6f + 0.12f * r + 1.1f * MathF.Sin(x * 0.42f + 0.6f) * MathF.Cos(z * 0.37f) + 0.5f * MathF.Sin(x * 0.9f + z * 0.7f) - 1.2f * MathF.Exp(-r * r / 10f);
    }

    public override void UpdateInput(VideoDemoGame game)
    {
        if (_terrain is null || !game.Input.IsKeyPressed(Keys.X))
            return;
        CarveAtCrosshair(game, _terrain);
    }

    public static void CarveAtCrosshair(VideoDemoGame game, VoxelTerrain terrain)
    {
        var camera = game.SceneSystem.SceneInstance.RootScene.Entities.First(e => e.Get<CameraComponent>() is not null).Transform.WorldMatrix;
        var forward = Vector3.Normalize(Vector3.TransformNormal(-Vector3.UnitZ, camera));
        var simulation = terrain.Entity.Get<StaticComponent>()!.Simulation!;
        if (simulation.RayCast(camera.TranslationVector, forward, 200f, out var hit) && hit.Collidable.Entity == terrain.Entity)
            terrain.Carve(hit.Point, 1.6f, 1.1f);
    }

    public override IEnumerable<string> Status()
    {
        yield return $"terrain 64 x 20 x 64 échantillons (cellules de 0,4 m), surface nets  |  {_rain?.Items.Count ?? 0} objets  |  cratères : {_terrain?.Craters ?? 0}";
    }
}

/// <summary> Glass, jelly and water whose opacity depends on the thickness crossed; O switches OpacityThickness off to compare </summary>
internal sealed class OpacityScene : VideoScene
{
    private readonly List<(ModelComponent Model, Material On, Material Off)> _swappable = new();
    private bool _on = true;
    private Spawner? _jellies;

    public override string Title => "Opacité par épaisseur (OpacityThickness)";
    public override string Pr => "PR #3530";
    public override string Description => "plus on traverse de matière, plus c'est opaque : bords clairs, cœur dense ; les gelées changent de teinte en s'écrasant (O : comparer sans)";
    public override Vector3 Eye => new(0.6f, 3.2f, 7.4f);
    public override Vector3 Target => new(0f, 0.9f, 0f);
    public override string SceneKeys => "O : OpacityThickness on / off";

    protected override void BuildScene(VideoDemoGame game)
    {
        _swappable.Clear();
        _on = true;
        var device = game.GraphicsDevice;
        var amber = new Color(255, 150, 30);
        var ivory = Look.Paint(device, Look.Ivory, 0.5f);

        // A prism of amber glass, 5 cm thick on the left and 1.2 m on the right, with opaque objects behind it
        Look.StaticBox(game, new Vector3(2.6f, 0.2f, 1.6f), new Vector3(-2.4f, 0.1f, -0.6f), ivory, "socle");
        AddVolume(game, Wedge(1.1f, 1.3f, 0.05f, 1.2f), amber, 0.6f, 0.4f, MaterialVolumeMedium.Absorbing, new Vector3(-2.4f, 0.2f, -0.1f));
        foreach (var (x, color) in new[] { (-3.2f, Look.Teal), (-2.4f, Look.Ivory), (-1.6f, Look.Blue) })
        {
            var marker = new Entity("repère") { new ModelComponent(Look.Model(game, new SphereProceduralModel { Radius = 0.22f, Tessellation = 28 }, color, 0.7f)) };
            marker.Transform.Position = new Vector3(x, 0.42f, -1.15f);
            game.AddToScene(marker);
        }

        // A block of blue glass around a coral sphere: the middle of the sphere shows through the thickest glass
        Look.StaticBox(game, new Vector3(1.6f, 0.2f, 1.6f), new Vector3(0.4f, 0.1f, -0.9f), ivory, "socle");
        var core = new Entity("cœur") { new ModelComponent(Look.Model(game, new SphereProceduralModel { Radius = 0.35f, Tessellation = 32 }, Look.Coral, 0.7f)) };
        core.Transform.Position = new Vector3(0.4f, 0.9f, -0.9f);
        game.AddToScene(core);
        AddVolume(game, GeometricPrimitive.Cube.New(new Vector3(1.3f)), new Color(80, 150, 255), 0.55f, 0.5f, MaterialVolumeMedium.Absorbing, new Vector3(0.4f, 0.85f, -0.9f));

        // A pool of scattering water over a sloped floor: shallow and clear on one side, deep and milky on the other
        Look.StaticBox(game, new Vector3(2.8f, 0.1f, 2.0f), new Vector3(2.9f, 0.1f, -0.6f), Look.Paint(device, Look.Stone, 0.3f), "fond");
        var slope = new Entity("pente") { new ModelComponent(Look.Model(game, new CubeProceduralModel { Size = new Vector3(2.6f, 0.08f, 1.8f) }, Look.Mint, 0.4f)) };
        slope.Transform.Position = new Vector3(2.9f, 0.55f, -0.6f);
        slope.Transform.Rotation = Quaternion.RotationZ(0.3f);
        game.AddToScene(slope);
        AddVolume(game, GeometricPrimitive.Cube.New(new Vector3(2.6f, 0.9f, 1.8f)), new Color(60, 140, 200), 0.65f, 0.6f, MaterialVolumeMedium.Scattering, new Vector3(2.9f, 0.6f, -0.6f));

        // Soft jellies with the same materials, in front: they get clearer as they flatten
        var tray = new Vector3(0f, 0f, 1.6f);
        Look.StaticBox(game, new Vector3(5.2f, 0.12f, 1.4f), tray + new Vector3(0f, 0.06f, 0f), ivory, "plateau");
        var jellyColors = new[] { new Color(255, 90, 130), new Color(120, 230, 110), new Color(255, 190, 40), new Color(150, 110, 255) };
        _jellies = new Spawner(game, i =>
        {
            var color = jellyColors[i % jellyColors.Length];
            var medium = i % 2 == 0 ? MaterialVolumeMedium.Scattering : MaterialVolumeMedium.Absorbing;
            PrimitiveProceduralModelBase shape = i % 3 == 1 ? new SphereProceduralModel { Radius = 0.38f, Tessellation = 28 } : new CubeProceduralModel { Size = new Vector3(0.62f), Subdivisions = 6 };
            var on = Look.Volume(device, color, 0.6f, 0.35f, medium);
            var off = Look.Volume(device, color, 0.6f, 0f, medium);
            var model = new ModelComponent(Look.Model(game, shape, on));
            _swappable.Add((model, on, off));
            if (!_on)
                model.Materials[0] = off;
            return new Entity("gelée translucide") { model, new VolumetricSoftBodyComponent { Resolution = 5, Stiffness = 7f, ShearStiffness = 7f, Damping = 0.3f } };
        }, i => tray + new Vector3(-2f + (i % 6) * 0.8f, 3.2f, MathF.Sin(i) * 0.25f), 1.4f, 6, tray, 4f);
        game.AddToScene(new Entity("gelées") { _jellies });
    }

    private void AddVolume(VideoDemoGame game, GeometricMeshData<VertexPositionNormalTexture> data, Color color, float alpha, float thickness, MaterialVolumeMedium medium, Vector3 position)
    {
        var device = game.GraphicsDevice;
        var on = Look.Volume(device, color, alpha, thickness, medium);
        var off = Look.Volume(device, color, alpha, 0f, medium);
        var primitive = new GeometricPrimitive(device, data);
        var bounds = BoundingBox.FromPoints(data.Vertices.Select(v => v.Position).ToArray());
        var model = new Model { on };
        model.Add(new Mesh { Draw = primitive.ToMeshDraw(), MaterialIndex = 0, BoundingBox = bounds });
        var component = new ModelComponent(model) { IsShadowCaster = false };
        var entity = new Entity("volume") { component };
        entity.Transform.Position = position;
        game.AddToScene(entity);
        _swappable.Add((component, on, off));
    }

    // Front face at z = 0, from depth thin at x = -h to thick at x = +h
    private static GeometricMeshData<VertexPositionNormalTexture> Wedge(float h, float height, float thin, float thick)
    {
        Vector3 f0 = new(-h, 0, 0), f1 = new(h, 0, 0), f2 = new(h, height, 0), f3 = new(-h, height, 0);
        Vector3 b0 = new(-h, 0, -thin), b1 = new(h, 0, -thick), b2 = new(h, height, -thick), b3 = new(-h, height, -thin);
        var inside = new Vector3(0, height / 2, -(thin + thick) / 4);
        Vector3[][] faces = [[f0, f1, f2, f3], [b0, b1, b2, b3], [f0, f3, b3, b0], [f1, f2, b2, b1], [f3, f2, b2, b3], [f0, f1, b1, b0]];
        var vertices = new List<VertexPositionNormalTexture>();
        var indices = new List<int>();
        foreach (var face in faces)
        {
            var centre = (face[0] + face[1] + face[2] + face[3]) / 4;
            var normal = Vector3.Normalize(Vector3.Cross(face[1] - face[0], face[2] - face[0]));
            if (Vector3.Dot(normal, centre - inside) < 0)
                normal = -normal;
            var clockwise = Vector3.Dot(Vector3.Cross(face[1] - face[0], face[2] - face[0]), normal) < 0;
            int start = vertices.Count;
            foreach (var v in face)
                vertices.Add(new VertexPositionNormalTexture(v, normal, Vector2.Zero));
            indices.AddRange(clockwise ? [start, start + 1, start + 2, start, start + 2, start + 3] : [start, start + 2, start + 1, start, start + 3, start + 2]);
        }
        return new GeometricMeshData<VertexPositionNormalTexture>(vertices.ToArray(), indices.ToArray(), false);
    }

    public override void UpdateInput(VideoDemoGame game)
    {
        if (!game.Input.IsKeyPressed(Keys.O))
            return;
        _on = !_on;
        foreach (var (model, on, off) in _swappable)
            model.Materials[0] = _on ? on : off;
    }

    public override IEnumerable<(Vector3 World, string Text)> Labels()
    {
        yield return (new Vector3(-2.4f, 1.85f, -0.6f), "verre absorbant : 5 cm → 1,2 m");
        yield return (new Vector3(0.4f, 1.85f, -0.9f), "bloc de verre autour d'une sphère");
        yield return (new Vector3(2.9f, 1.4f, -0.6f), "eau diffusante, fond en pente");
    }

    public override void DrawOverlay(VideoDemoGame game)
    {
        var text = _on ? "OpacityThickness on" : "OpacityThickness off (opacité constante)";
        var size = game.Measure(text, 30, true);
        var corner = new Vector2((game.Screen.X - size.X) / 2f, game.Screen.Y - 120f);
        game.Panel(new RectangleF(corner.X - 18, corner.Y - 10, size.X + 36, size.Y + 20), _on ? new Color(30, 110, 60, 200) : new Color(140, 40, 40, 200));
        game.Write(text, corner, Color.White, 30, true);
    }

    public override IEnumerable<string> Status()
    {
        yield return $"OpacityThickness : {(_on ? "on" : "off")} (O)  |  {_jellies?.Items.Count ?? 0} gelées molles translucides";
    }
}

/// <summary>
/// Kinematic arms driven by SetTargetPose every step, with a ghost of the target: they now land on it in one step.
/// Next to them, the same arm driven with the former velocity (half the angle per step) lags behind its ghost.
/// The shapes on the right are drawn with Bepu's wireframes, which no longer show through their own model.
/// </summary>
internal sealed class TargetPoseScene : VideoScene
{
    private readonly List<(Entity Body, Entity Ghost, string Text)> _arms = new();
    private float _lagFixed, _lagFormer;

    public override string Title => "SetTargetPose en un pas + wireframes de debug";
    public override string Pr => "PR #3563, #3565";
    public override string Description => "fantôme jaune = orientation cible : le tourniquet de gauche la rejoint à chaque pas, l'ancien calcul (à droite) traîne derrière ; les wireframes ne transparaissent plus";
    public override Vector3 Eye => new(0.6f, 3.6f, 7.6f);
    public override Vector3 Target => new(0.4f, 0.9f, 0f);
    public override bool WireframeByDefault => true;
    public override string SceneKeys => "V : Auto (wireframes on) / colliders / colliders + faces arrière / off";

    protected override void BuildScene(VideoDemoGame game)
    {
        _arms.Clear();
        var device = game.GraphicsDevice;
        var navy = Look.Paint(device, Look.Navy, 0.4f);

        // Two swinging arms over a low table, balls falling on them
        var arms = new List<(BodyComponent Body, Entity Ghost, bool Fixed, Vector3 Pivot)>();
        foreach (var (x, color, isFixed) in new[] { (-3.4f, Look.Teal, true), (-0.9f, Look.Coral, false) })
        {
            var pivot = new Vector3(x, 0.9f, 0f);
            Look.StaticBox(game, new Vector3(0.25f, 0.8f, 0.25f), pivot - new Vector3(0f, 0.5f, 0f), navy, "axe");
            var size = new Vector3(2f, 0.16f, 0.3f);
            var body = new BodyComponent { Kinematic = true, Collider = new CompoundCollider { Colliders = { new BoxCollider { Size = size } } } };
            var arm = new Entity(isFixed ? "bras SetTargetPose" : "bras ancien calcul") { new ModelComponent(Look.Model(game, new CubeProceduralModel { Size = size }, color, 0.6f)), body };
            arm.Transform.Position = pivot;
            game.AddToScene(arm);
            var ghost = new Entity("fantôme") { new ModelComponent(Look.Model(game, new CubeProceduralModel { Size = size * new Vector3(1.01f, 1.6f, 1.15f) }, Look.Ghost(device, Look.Sun, 0.35f))) { IsShadowCaster = false } };
            ghost.Transform.Position = pivot;
            game.AddToScene(ghost);
            arms.Add((body, ghost, isFixed, pivot));
            _arms.Add((arm, ghost, isFixed ? "SetTargetPose (#3563)" : "ancien calcul : moitié de l'angle par pas"));
        }
        game.AddToScene(new Entity("bras")
        {
            new StepDriver((time, step) =>
            {
                // A swing of ±100 degrees around Z, faster and slower
                // A turnstile whose speed goes from 2 to 9 rad/s and back
                float angle = 5.5f * time - 3.5f * MathF.Cos(time * 0.6f) / 0.6f;
                var target = Quaternion.RotationY(angle);
                foreach (var (body, ghost, isFixed, pivot) in arms)
                {
                    // How far the last step landed from the target it was given
                    float lag = 2f * MathF.Acos(MathF.Min(1f, MathF.Abs(Quaternion.Dot(body.Orientation, ghost.Transform.Rotation)))) * 180f / MathF.PI;
                    if (isFixed)
                        _lagFixed = _lagFixed * 0.9f + lag * 0.1f;
                    else
                        _lagFormer = _lagFormer * 0.9f + lag * 0.1f;

                    ghost.Transform.Rotation = target;
                    if (isFixed)
                    {
                        body.SetTargetPose(pivot, target);
                    }
                    else
                    {
                        // The velocity SetTargetPose used before #3563: the quaternion's xyz per step, half the angle
                        var delta = Quaternion.Invert(body.Orientation) * target;
                        body.SetTargetPose(pivot);
                        body.AngularVelocity = new Vector3(delta.X, delta.Y, delta.Z) / step;
                        body.Awake = true;
                    }
                }
            }),
        });
        var spawner = new Spawner(game, i => Props.Ball(game, 0.16f, 0.6f, Look.Accents[i % Look.Accents.Length], Vector3.Zero), i => new Vector3((i % 2 == 0 ? -3.4f : -0.9f) + MathF.Sin(i * 1.3f) * 0.6f, 4f, MathF.Cos(i * 0.9f) * 0.15f), 0.45f, 16, new Vector3(-2.1f, 0f, 0f), 7f);
        game.AddToScene(new Entity("balles") { spawner });

        // Shapes on a shelf whose collider is the same as their model, smaller, or larger
        Look.StaticBox(game, new Vector3(3.6f, 0.3f, 1.6f), new Vector3(2.9f, 0.15f, -0.2f), Look.Paint(device, Look.Ivory, 0.5f), "étagère");
        AddShape(game, new SphereProceduralModel { Radius = 0.35f, Tessellation = 32 }, new SphereCollider { Radius = 0.35f }, Look.Blue, new Vector3(1.6f, 0.65f, -0.5f));
        AddShape(game, new CapsuleProceduralModel { Radius = 0.22f, Length = 0.5f, Tessellation = 20 }, new CapsuleCollider { Radius = 0.22f, Length = 0.5f }, Look.Mint, new Vector3(2.5f, 0.77f, -0.5f));
        AddShape(game, new CylinderProceduralModel { Radius = 0.3f, Height = 0.6f, Tessellation = 32 }, new CylinderCollider { Radius = 0.3f, Length = 0.6f }, Look.Sun, new Vector3(3.4f, 0.6f, -0.5f));
        AddShape(game, new SphereProceduralModel { Radius = 0.35f, Tessellation = 32 }, new SphereCollider { Radius = 0.26f }, Look.Violet, new Vector3(4.3f, 0.65f, -0.5f), dynamic: false);
        AddShape(game, new CubeProceduralModel { Size = new Vector3(0.5f) }, new BoxCollider { Size = new Vector3(0.66f) }, Look.Coral, new Vector3(2.0f, 0.63f, 0.3f), dynamic: false);
        AddShape(game, new CubeProceduralModel { Size = new Vector3(0.55f) }, new BoxCollider { Size = new Vector3(0.55f) }, Look.Pink, new Vector3(3.6f, 0.575f, 0.3f));
    }

    private static void AddShape(DemoGame game, PrimitiveProceduralModelBase shape, ColliderBase collider, Color color, Vector3 position, bool dynamic = true)
    {
        var compound = new CompoundCollider { Colliders = { collider } };
        CollidableComponent collidable = dynamic ? new BodyComponent { Collider = compound } : new StaticComponent { Collider = compound };
        var entity = new Entity("forme") { new ModelComponent(Look.Model(game, shape, color, 0.6f)), collidable };
        entity.Transform.Position = position;
        game.AddToScene(entity);
    }

    public override IEnumerable<(Vector3 World, string Text)> Labels()
    {
        foreach (var (arm, _, text) in _arms)
            yield return (arm.Transform.Position + new Vector3(0f, 1.3f, 0f), text);
        yield return (new Vector3(2.5f, 1.75f, -0.5f), "collider = modèle : lignes visibles");
        yield return (new Vector3(4.4f, 1.3f, -0.5f), "collider plus petit (jaune)");
        yield return (new Vector3(2.0f, 0.2f, 0.9f), "collider plus grand (vermillon)");
    }

    public override IEnumerable<string> Status()
    {
        yield return string.Format(CultureInfo.InvariantCulture, "écart à la cible : SetTargetPose {0:0.0}°  |  ancien calcul {1:0.0}°", _lagFixed, _lagFormer);
    }
}
