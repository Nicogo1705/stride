// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Stride.BepuPhysics;
using Stride.BepuPhysics.Debug;
using Stride.BepuPhysics.Definitions;
using Stride.BepuPhysics.Definitions.Colliders;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Graphics.GeometricPrimitives;
using Stride.Graphics.Regression;
using Stride.Input;
using Stride.Rendering.ProceduralModels;
using Xunit;

namespace Stride.Harness.Demo;

/// <summary>
/// One capture per case of Bepu's collider wireframe drawn over the visual models.
/// Opt-in: STRIDE_BEPU_WIREFRAME_CASES=1. STRIDE_WIREFRAME_CASES_SET picks the cases: « occlusion » (curved and flat shapes,
/// walls without collider, the rigid pile), « match » (colliders smaller, equal or larger than their model, near, far and
/// grazing), « backfaces » (dashed back faces), « cost » (frame time on the frozen rigid pile, debug off then on). STRIDE_WIREFRAME_DEBUG_TESS sets the tessellation of the curved debug meshes,
/// STRIDE_WIREFRAME_CASES_FOLDER and STRIDE_WIREFRAME_CASES_TAG name the files.
/// </summary>
public class WireframeCases : GameTestBase
{
    private static readonly Color Blue = new(70, 110, 190);
    private static readonly Color Green = new(60, 150, 110);
    private static readonly Color Slate = new(110, 120, 140);
    private static readonly Color Purple = new(130, 100, 180);
    private static readonly Color Teal = new(40, 130, 140);

    [SkippableFact]
    public static void Capture()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("STRIDE_BEPU_WIREFRAME_CASES") == "1", "Wireframe cases, set STRIDE_BEPU_WIREFRAME_CASES=1 to run them");

        var set = Environment.GetEnvironmentVariable("STRIDE_WIREFRAME_CASES_SET") ?? "occlusion";
        if (set == "cost")
        {
            MeasureCost();
            return;
        }
        if (int.TryParse(Environment.GetEnvironmentVariable("STRIDE_WIREFRAME_DEBUG_TESS"), out var tessellation))
            SetDebugTessellation(tessellation);

        var cases = set switch
        {
            "match" => MatchCases().ToList(),
            "backfaces" => BackFaceCases().ToList(),
            _ => OcclusionCases().ToList(),
        };
        var pile = RigidScenes.All().First();
        var scenes = set == "occlusion" ? cases.Cast<DemoScene>().Append(pile) : cases.Cast<DemoScene>();
        var game = new BepuDemoGame(scenes, DemoSettings.Label, DemoSettings.CaptureFolder);
        var folder = Environment.GetEnvironmentVariable("STRIDE_WIREFRAME_CASES_FOLDER") ?? DemoSettings.TourFolder;
        var tag = Environment.GetEnvironmentVariable("STRIDE_WIREFRAME_CASES_TAG") ?? "capture";
        DemoSettings.RunTour(game, async () =>
        {
            game.OverlayVisible = false;
            // V only works once a scene is loaded (the menu is open before)
            game.LoadScene(cases[0]);
            await game.Frames(2);
            await game.Press(Keys.V);
            SetBackFaces(game, set == "backfaces");
            for (int i = 0; i < cases.Count; i++)
            {
                game.LoadScene(cases[i]);
                game.SetCamera(cases[i].Eye, cases[i].Target);
                await Settle(game);
                await game.CaptureTo(Path.Combine(folder, $"{set}-{cases[i].File}-{tag}"));
            }

            if (set == "occlusion")
            {
                game.LoadScene(pile);
                await game.Steps(200);
                game.SetCamera(new Vector3(10f, 9f, 13f), new Vector3(0f, 2f, 0f));
                await Settle(game);
                await game.CaptureTo(Path.Combine(folder, $"{set}-tas-rigide-{tag}"));
            }
        }, g => RunGameTest(g));
    }

    /// <summary> The auto exposure adapts over time, not frames: at a thousand frames per second, a few frames leave it halfway </summary>
    private static async System.Threading.Tasks.Task Settle(DemoGame game)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.Elapsed.TotalSeconds < 1.5)
            await game.Frames(1);
    }

    private static void MeasureCost()
    {
        var pile = RigidScenes.All().First();
        var sphere = OcclusionCases().First();
        var game = new CostGame(new DemoScene[] { pile, sphere });
        var folder = Environment.GetEnvironmentVariable("STRIDE_WIREFRAME_CASES_FOLDER") ?? DemoSettings.TourFolder;
        var tag = Environment.GetEnvironmentVariable("STRIDE_WIREFRAME_CASES_TAG") ?? "capture";
        var frames = int.TryParse(Environment.GetEnvironmentVariable("STRIDE_WIREFRAME_COST_FRAMES"), out var f) ? f : 400;
        DemoSettings.RunTour(game, async () =>
        {
            game.OverlayVisible = false;
            var lines = new List<string> { $"{frames} frames per case after 120 of warm-up, {game.GraphicsDevice.Adapter.Description}, back buffer {game.GraphicsDevice.Presenter.BackBuffer.Width}x{game.GraphicsDevice.Presenter.BackBuffer.Height}" };
            async System.Threading.Tasks.Task Measure(string label)
            {
                await game.Frames(120);
                game.Start();
                await game.Frames(frames);
                lines.Add($"{label}: {game.Stop()}");
            }

            game.LoadScene(pile);
            await game.Steps(200);
            await game.Press(Keys.Space);
            game.SetCamera(new Vector3(10f, 9f, 13f), new Vector3(0f, 2f, 0f));
            await Measure("pile 1500, debug off");
            await game.Press(Keys.V);
            await Measure("pile 1500, debug on ");
            await game.Press(Keys.V);

            game.LoadScene(sphere);
            game.SetCamera(sphere.Eye, sphere.Target);
            await Measure("one sphere, debug off");
            await game.Press(Keys.V);
            await Measure("one sphere, debug on ");
            File.WriteAllLines(Path.Combine(folder, $"cout-{tag}.txt"), lines);
        }, g => RunGameTest(g));
    }

    /// <summary> Records, per frame, the time between frames, the CPU time of Draw, and the GPU time of Draw from timestamp queries </summary>
    private sealed class CostGame(IEnumerable<DemoScene> scenes) : BepuDemoGame(scenes, DemoSettings.Label, DemoSettings.CaptureFolder)
    {
        private const int Ring = 8;
        private readonly QueryPool?[] _pools = new QueryPool?[Ring];
        private readonly bool[] _pending = new bool[Ring];
        private readonly bool[] _recorded = new bool[Ring];
        private readonly long[] _data = new long[2];
        private readonly List<double> _period = new(), _cpu = new(), _gpu = new();
        private bool _recording;
        private long _last;
        private int _frame;

        public void Start()
        {
            _period.Clear(); _cpu.Clear(); _gpu.Clear();
            _recording = true;
        }

        public string Stop()
        {
            _recording = false;
            static string Stats(List<double> values)
            {
                if (values.Count == 0)
                    return "n/a";
                var sorted = values.OrderBy(v => v).ToArray();
                return $"median {sorted[sorted.Length / 2]:0.000} p95 {sorted[(int)(sorted.Length * 0.95)]:0.000} (n={sorted.Length})";
            }
            return $"frame {Stats(_period)} | cpu draw {Stats(_cpu)} | gpu draw {Stats(_gpu)}";
        }

        protected override void Draw(Stride.Games.GameTime gameTime)
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_recording && _last != 0)
                _period.Add((now - _last) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            _last = now;

            var slot = _frame++ % Ring;
            var pool = _pools[slot] ??= QueryPool.New(GraphicsDevice, QueryType.Timestamp, 2);
            if (_pending[slot] && pool.TryGetData(_data) && _recorded[slot] && GraphicsDevice.TimestampFrequency > 0)
                _gpu.Add((_data[1] - _data[0]) * 1000.0 / GraphicsDevice.TimestampFrequency);

            GraphicsContext.CommandList.WriteTimestamp(pool, 0);
            base.Draw(gameTime);
            GraphicsContext.CommandList.WriteTimestamp(pool, 1);
            _pending[slot] = true;
            _recorded[slot] = _recording;
            if (_recording)
                _cpu.Add((System.Diagnostics.Stopwatch.GetTimestamp() - now) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
        }
    }

    private static IEnumerable<Case> OcclusionCases()
    {
        yield return new("sphere-r1-pres", new(0f, 0.3f, 2.6f), Vector3.Zero, game => Sphere(game, 1f, 1f));
        yield return new("sphere-r1-loin", new(0f, 0.5f, 18f), Vector3.Zero, game => Sphere(game, 1f, 1f));
        yield return new("sphere-r01-pres", new(0f, 0.03f, 0.28f), Vector3.Zero, game => Sphere(game, 0.1f, 0.1f));
        yield return new("sphere-r10", new(0f, 4f, 26f), Vector3.Zero, game => Sphere(game, 10f, 10f));
        yield return new("sphere-rasante", new(1f, 0f, 2.2f), new(1f, 0f, 0f), game => Sphere(game, 1f, 1f));
        yield return new("capsule", new(0f, 0.3f, 2.4f), Vector3.Zero, game =>
            Add(game, new CapsuleCollider { Radius = 0.4f, Length = 1.2f }, new CapsuleProceduralModel { Radius = 0.4f, Length = 1.2f, Tessellation = 16 }, Green, Vector3.Zero));
        yield return new("cylindre", new(0.5f, 0.9f, 2.2f), Vector3.Zero, game =>
            Add(game, new CylinderCollider { Radius = 0.5f, Length = 1.2f }, new CylinderProceduralModel { Radius = 0.5f, Height = 1.2f }, Teal, Vector3.Zero));
        yield return new("boites-coplanaires", new(1.7f, 2.0f, 2.6f), new(0f, 1f, 0f), game =>
        {
            Add(game, new BoxCollider { Size = Vector3.One }, new CubeProceduralModel { Size = Vector3.One }, Slate, new Vector3(0f, 0.5f, 0f));
            Add(game, new BoxCollider { Size = Vector3.One }, new CubeProceduralModel { Size = Vector3.One }, Blue, new Vector3(0f, 1.5f, 0f));
        });
        yield return new("boite-rasante", new(0.3f, 0.53f, 3f), new(0f, 0.5f, -3f), game =>
            Add(game, new BoxCollider { Size = new Vector3(2f, 1f, 4f) }, new CubeProceduralModel { Size = new Vector3(2f, 1f, 4f) }, Slate, Vector3.Zero));
        yield return new("convex-hull", new(1.0f, 0.6f, 1.6f), Vector3.Zero, game =>
        {
            var gem = new GemProceduralModel();
            var collider = new ConvexHullCollider { Hull = new DecomposedHulls([new DecomposedHulls.DecomposedMesh([new DecomposedHulls.Hull(gem.Points, gem.Indices.Select(i => (uint)i).ToArray())])]) };
            Add(game, collider, gem, Purple, Vector3.Zero);
        });
        yield return new("mesh", new(0f, 1.0f, 1.4f), Vector3.Zero, game =>
        {
            var torus = game.Shape(new TorusProceduralModel { Radius = 0.5f, Thickness = 0.2f, Tessellation = 24 }, Teal);
            AddModel(game, new MeshCollider { Model = torus, Closed = true }, torus, Vector3.Zero);
        });
        yield return new("compound", new(1.4f, 1.1f, 2.0f), new(0f, 0.4f, 0f), Compound);
        yield return new("mur-devant", new(0f, 0f, 3.2f), Vector3.Zero, game =>
        {
            Sphere(game, 0.5f, 0.5f);
            Wall(game, new Vector3(3f, 3f, 0.2f), new Vector3(0f, 0f, 1f));
        });
        yield return new("mur-moitie", new(0f, 0f, 3.2f), Vector3.Zero, game =>
        {
            Add(game, new BoxCollider { Size = Vector3.One }, new CubeProceduralModel { Size = Vector3.One }, Slate, Vector3.Zero);
            Wall(game, new Vector3(3f, 3f, 0.2f), new Vector3(1.5f, 0f, 1f));
        });
    }

    private static IEnumerable<Case> MatchCases()
    {
        // The collider matches its model: no line should be colored, near, far, and with the outline at the center
        yield return new("egal-pres", new(0f, 0.2f, 1.5f), Vector3.Zero, game => Sphere(game, 0.5f, 0.5f));
        yield return new("egal-loin", new(0f, 0.3f, 12f), Vector3.Zero, game => Sphere(game, 0.5f, 0.5f));
        yield return new("egal-rasante", new(0.5f, 0f, 1.4f), new(0.5f, 0f, 0f), game => Sphere(game, 0.5f, 0.5f));
        yield return new("capsule-egale", new(0f, 0.2f, 1.8f), Vector3.Zero, game =>
            Add(game, new CapsuleCollider { Radius = 0.3f, Length = 0.8f }, new CapsuleProceduralModel { Radius = 0.3f, Length = 0.8f, Tessellation = 16 }, Green, Vector3.Zero));
        yield return new("capsule-egale-rasante", new(0.3f, 0f, 1.4f), new(0.3f, 0f, 0f), game =>
            Add(game, new CapsuleCollider { Radius = 0.3f, Length = 0.8f }, new CapsuleProceduralModel { Radius = 0.3f, Length = 0.8f, Tessellation = 16 }, Green, Vector3.Zero));
        // The visual model is coarser than the debug mesh: the exact collider stands out of it by the model's own error
        yield return new("modele-grossier", new(0f, 0.2f, 1.5f), Vector3.Zero, game =>
            Add(game, new SphereCollider { Radius = 0.5f }, new SphereProceduralModel { Radius = 0.5f, Tessellation = 8 }, Blue, Vector3.Zero));
        // Smaller and larger colliders: inside and outside colors
        yield return new("interieur-04-dans-05", new(0f, 0.2f, 1.5f), Vector3.Zero, game => Sphere(game, 0.4f, 0.5f));
        yield return new("exterieur-06-sur-05", new(0f, 0.2f, 1.7f), Vector3.Zero, game => Sphere(game, 0.6f, 0.5f));
    }

    private static IEnumerable<Case> BackFaceCases()
    {
        yield return new("sphere", new(0f, 0.3f, 2.6f), Vector3.Zero, game => Sphere(game, 1f, 1f));
        yield return new("compound", new(1.4f, 1.1f, 2.0f), new(0f, 0.4f, 0f), Compound);
        yield return new("mur-moitie", new(0f, 0f, 3.2f), Vector3.Zero, game =>
        {
            Add(game, new BoxCollider { Size = Vector3.One }, new CubeProceduralModel { Size = Vector3.One }, Slate, Vector3.Zero);
            Wall(game, new Vector3(3f, 3f, 0.2f), new Vector3(1.5f, 0f, 1f));
        });
        yield return new("interieur-04-dans-05", new(0f, 0.2f, 1.5f), Vector3.Zero, game => Sphere(game, 0.4f, 0.5f));
    }

    private static void Compound(DemoGame game)
    {
        var compound = new CompoundCollider
        {
            Colliders = { new BoxCollider { Size = new Vector3(0.8f) }, new SphereCollider { Radius = 0.4f, PositionLocal = new Vector3(0f, 0.8f, 0f) } },
        };
        var entity = new Entity { new ModelComponent(game.Shape(new CubeProceduralModel { Size = new Vector3(0.8f) }, Slate)), new StaticComponent { Collider = compound } };
        var top = new Entity { new ModelComponent(game.Shape(new SphereProceduralModel { Radius = 0.4f }, Blue)) };
        top.Transform.Position = new Vector3(0f, 0.8f, 0f);
        entity.AddChild(top);
        game.AddToScene(entity);
    }

    private static void Sphere(DemoGame game, float colliderRadius, float modelRadius)
        => Add(game, new SphereCollider { Radius = colliderRadius }, new SphereProceduralModel { Radius = modelRadius }, Blue, Vector3.Zero);

    private static void Add(DemoGame game, ColliderBase collider, PrimitiveProceduralModelBase model, Color color, Vector3 position)
        => AddModel(game, new CompoundCollider { Colliders = { collider } }, game.Shape(model, color), position);

    private static void AddModel(DemoGame game, ICollider collider, Stride.Rendering.Model model, Vector3 position)
    {
        var entity = new Entity { new ModelComponent(model), new StaticComponent { Collider = collider } };
        entity.Transform.Position = position;
        game.AddToScene(entity);
    }

    /// <summary> A visual wall with no collider: it must hide the wireframes behind it </summary>
    private static void Wall(DemoGame game, Vector3 size, Vector3 position)
    {
        var wall = new Entity { new ModelComponent(game.Shape(new CubeProceduralModel { Size = size }, Color.LightGray)) };
        wall.Transform.Position = position;
        game.AddToScene(wall);
    }

    // Through reflection: the same harness builds against a Stride.BepuPhysics without these members
    private static void SetDebugTessellation(int tessellation)
    {
        var property = typeof(BodyComponent).Assembly.GetType("Stride.BepuPhysics.Systems.ShapeCacheSystem")?
            .GetProperty("CurvedTessellation", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
        if (property is null)
            throw new InvalidOperationException("This build has no adjustable debug mesh tessellation");
        property.SetValue(null, tessellation);
    }

    private static void SetBackFaces(DemoGame game, bool show)
    {
        var property = typeof(DebugRenderComponent).GetProperty("ShowBackFaces");
        if (property is null)
            return;
        foreach (var entity in game.SceneSystem.SceneInstance.RootScene.Entities)
        {
            if (entity.Get<DebugRenderComponent>() is { } debug)
                property.SetValue(debug, show);
        }
    }

    private sealed class Case(string file, Vector3 eye, Vector3 target, Action<DemoGame> build) : BepuDemoScene
    {
        public string File => file;
        public override string Group => "Wireframe";
        public override string Name => file;
        public override string Description => "wireframe du collider par-dessus le modèle";
        public override Vector3 Eye => eye;
        public override Vector3 Target => target;
        public override void Build(DemoGame game) => build(game);
    }

    /// <summary> A flat-shaded hexagonal bipyramid, the same points and triangles as its convex hull </summary>
    private sealed class GemProceduralModel : PrimitiveProceduralModelBase
    {
        public Vector3[] Points { get; }
        public int[] Indices { get; }

        public GemProceduralModel()
        {
            var points = new List<Vector3> { new(0f, 0.6f, 0f), new(0f, -0.6f, 0f) };
            for (int i = 0; i < 6; i++)
            {
                var angle = MathF.PI * 2f * i / 6f;
                points.Add(new Vector3(MathF.Cos(angle) * 0.6f, 0f, MathF.Sin(angle) * 0.6f));
            }
            Points = points.ToArray();

            var indices = new List<int>();
            for (int i = 0; i < 6; i++)
            {
                int a = 2 + i, b = 2 + (i + 1) % 6;
                indices.AddRange(new[] { 0, b, a, 1, a, b });
            }
            Indices = indices.ToArray();
        }

        protected override GeometricMeshData<VertexPositionNormalTexture> CreatePrimitiveMeshData()
        {
            // One vertex per triangle corner so each face is flat
            var vertices = new VertexPositionNormalTexture[Indices.Length];
            var indices = new int[Indices.Length];
            for (int t = 0; t < Indices.Length; t += 3)
            {
                var a = Points[Indices[t]];
                var b = Points[Indices[t + 1]];
                var c = Points[Indices[t + 2]];
                var normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
                vertices[t] = new VertexPositionNormalTexture(a, normal, Vector2.Zero);
                vertices[t + 1] = new VertexPositionNormalTexture(b, normal, Vector2.Zero);
                vertices[t + 2] = new VertexPositionNormalTexture(c, normal, Vector2.Zero);
                indices[t] = t;
                indices[t + 1] = t + 1;
                indices[t + 2] = t + 2;
            }
            return new GeometricMeshData<VertexPositionNormalTexture>(vertices, indices, isLeftHanded: false) { Name = "Gem" };
        }
    }
}
