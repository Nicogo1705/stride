// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics.Regression;
using Stride.Harness.Demo;
using Stride.Input;
using Xunit;

namespace Stride.BepuPhysics.Soft.Tests.VideoDemo;

/// <summary>
/// The video showcase: soft bodies, an animated human, Standing NPCs, Standing soft bodies, a torture lab, ropes and cables, and a finale with all of them.
/// Opt-in: STRIDE_BEPU_DEMO=1 (interactive), STRIDE_BEPU_DEMO_SMOKE=1 (smoke test), STRIDE_BEPU_DEMO_TOUR=1 (scripted tour).
/// </summary>
public class VideoShowcaseDemo : GameTestBase
{
    internal static List<DemoScene> Scenes() => new()
    {
        new SoftBodiesScene(),
        new AnimatedHumanScene(),
        new StandingNpcScene(),
        new StandingSoftBodiesScene(),
        new TortureLabScene(),
        new RopesScene(),
        new FinaleScene(),
        new DeformationStressScene(),
    };

    [SkippableFact]
    public static void Interactive()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("STRIDE_BEPU_DEMO") == "1", "Interactive demo, set STRIDE_BEPU_DEMO=1 to open it");
        ForceInteractiveMode = true;
        RunGameTest(new VideoDemoGame(Scenes(), DemoSettings.Label, DemoSettings.CaptureFolder));
    }

    [SkippableFact]
    public static void SmokeTest()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("STRIDE_BEPU_DEMO_SMOKE") == "1", "Demo smoke test, set STRIDE_BEPU_DEMO_SMOKE=1 to run it");
        DemoSettings.RunSmoke(new VideoDemoGame(Scenes(), DemoSettings.Label, DemoSettings.CaptureFolder), game => RunGameTest(game));
    }

    /// <summary>
    /// Every scene: a shot at load, a shot after its interaction, then 1800 physics steps watched for anything flying away and a last shot;
    /// frames per second without a limit over those steps, then at 144; report in report.txt
    /// </summary>
    [SkippableFact]
    public static void Tour()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("STRIDE_BEPU_DEMO_TOUR") == "1", "Demo tour, set STRIDE_BEPU_DEMO_TOUR=1 to run it");
        var game = new VideoDemoGame(Scenes(), DemoSettings.Label, DemoSettings.CaptureFolder);
        var folder = DemoSettings.TourFolder;
        var report = new List<string>();
        int stabilitySteps = int.TryParse(Environment.GetEnvironmentVariable("VIDEO_TOUR_STEPS"), out var steps) ? steps : 1800;
        var only = Environment.GetEnvironmentVariable("VIDEO_TOUR_ONLY") is { Length: > 0 } list ? list.Split(',').Select(int.Parse).ToHashSet() : null;
        var picked = game.Scenes.Select((scene, i) => (Scene: scene, Index: i + 1)).Where(s => only is null || only.Contains(s.Index)).ToList();
        DemoSettings.RunTour(game, async () =>
        {
            if (Environment.GetEnvironmentVariable("VIDEO_TOUR_PROFILE") == "1")
            {
                // The cost of each kind of physical object: the step time once all of them are removed
                foreach (var (scene, index) in picked)
                {
                    foreach (var threads in (Environment.GetEnvironmentVariable("VIDEO_TOUR_THREADS") ?? "1,2,4,8,16,-1").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse))
                    {
                        game.LoadScene(scene);
                        await game.Steps(60);
                        var simulation = game.SceneSystem.SceneInstance.RootScene.Entities.SelectMany(e => e.Components).OfType<CollidableComponent>().Select(c => c.Simulation).First(s => s is not null)!;
                        simulation.ThreadCount = threads;
                        await game.Steps(240);
                        report.Add($"{index:00} threads {threads}: {game.StepTiming()}");
                    }
                    if (Environment.GetEnvironmentVariable("VIDEO_TOUR_THREADS_ONLY") == "1")
                        continue;
                    game.LoadScene(scene);
                    await game.Steps(240);
                    report.Add($"{index:00} all: {game.StepTiming()}");
                    report.Add($"{index:00}   solver: {SolverStats(game)}");
                    var names = game.SceneSystem.SceneInstance.RootScene.Entities.Where(e => e.Components.OfType<CollidableComponent>().Any() && e.Get<StaticComponent>() is null).Select(e => e.Name).Distinct().ToList();
                    foreach (var name in names)
                    {
                        game.LoadScene(scene);
                        await game.Steps(60);
                        foreach (var entity in game.SceneSystem.SceneInstance.RootScene.Entities.Where(e => e.Name == name).ToList())
                            game.SceneSystem.SceneInstance.RootScene.Entities.Remove(entity);
                        await game.Steps(180);
                        report.Add($"{index:00}   without {name}: {game.StepTiming()}");
                    }
                }
                return;
            }

            // Shaders compile in the background on first use: warm every scene up before the shots
            foreach (var (scene, _) in picked)
            {
                game.LoadScene(scene);
                await game.Steps(150);
            }
            foreach (var (scene, index) in picked)
            {
                string Shot(string name) => Path.Combine(folder, $"{index:00}-{name}");
                game.LoadScene(scene);
                await game.Steps(120);
                await game.CaptureTo(Shot("a-start"));
                if (Environment.GetEnvironmentVariable("VIDEO_TOUR_VIEWS") == "1")
                {
                    // Every debug view from the scene's camera, with its frame rate
                    for (int view = 0; view < VideoDemoGame.Views.Length; view++)
                    {
                        game.SetDebugView(view);
                        await game.Frames(10);
                        game.ClearFrameTimes();
                        await game.Steps(120);
                        report.Add($"{index:00} view {VideoDemoGame.Views[view]}: {game.FpsText()}");
                        await game.CaptureTo(Shot($"a-view{view}"));
                    }
                    game.SetDebugView(scene is VideoScene videoScene ? videoScene.DefaultDebugView : VideoDemoGame.NoDebugView);
                }
                switch (scene)
                {
                    case SoftBodiesScene:
                        await game.Press(Keys.O);
                        await game.Frames(2);
                        await game.CaptureTo(Shot("b-opacity-off"));
                        await game.Press(Keys.O);
                        game.SetCamera(new Vector3(1.6f, 2.0f, 3.4f), new Vector3(0f, 1.0f, 0f));
                        await game.Steps(90);
                        await game.CaptureTo(Shot("b-jellies-close"));
                        if (((SoftBodiesScene)scene).Crane is { } crane)
                        {
                            // The load thrown sideways: the jib follows it around
                            game.SetCamera(new Vector3(4.2f, 2.6f, 4.4f), new Vector3(5.6f, 1.6f, 0.4f));
                            crane.Load.LinearVelocity = new Vector3(0f, 0f, -4f);
                            crane.Load.Awake = true;
                            await game.Steps(50);
                            await game.CaptureTo(Shot("b-crane"));
                            report.Add(string.Format(CultureInfo.InvariantCulture, "{0:00} crane: jib at {1:0} deg after the push", index, crane.JibAngle));
                        }
                        if (((SoftBodiesScene)scene).Rope is { } rope)
                        {
                            // Four turns each way, and the block pulled away and up as the gravity gun does, held there
                            rope.Turns = 4f;
                            game.SetCamera(new Vector3(3.0f, 2.5f, 6.6f), new Vector3(3.0f, 1.3f, 2.0f));
                            var pull = rope.Handle.Position + new Vector3(1.3f, 0.8f, 0.4f);
                            for (int i = 0; i < 240; i++)
                            {
                                rope.Handle.LinearVelocity = (pull - rope.Handle.Position) * 12f;
                                rope.Handle.Awake = true;
                                await game.Steps(1);
                                if (i is 90 or 239)
                                    await game.CaptureTo(Shot(i == 90 ? "b-rope-stretched" : "b-rope-stretched-later"));
                            }
                            report.Add(string.Format(CultureInfo.InvariantCulture, "{0:00} rope: block pulled {1:0.00} m from its stand, twister at {2:0.0} turns", index, Vector3.Distance(rope.Handle.Position, pull - new Vector3(1.3f, 0.8f, 0.4f)), rope.Angle / MathUtil.TwoPi));
                        }
                        game.SetCamera(scene.Eye, scene.Target);
                        break;
                    case AnimatedHumanScene human:
                        // A ball thrown at the walking human, then U
                        await game.Steps(60);
                        var hips = HumanHips(game) ?? scene.Target;
                        game.SetCamera(hips + new Vector3(0.5f, 0.9f, 4.5f), hips + new Vector3(0f, 0.2f, 0f));
                        await game.Press(Keys.F);
                        await game.Steps(40);
                        report.Add($"{index:00} after the ball: mode {human.Mode}");
                        await game.CaptureTo(Shot("b-hit"));
                        await game.Steps(80);
                        await game.CaptureTo(Shot("b-ragdoll"));
                        await game.Press(Keys.U);
                        await game.Steps(60);
                        report.Add($"{index:00} after U: mode {human.Mode}");
                        await game.CaptureTo(Shot("b-restored"));
                        game.SetCamera(scene.Eye, scene.Target);
                        break;
                    case StandingNpcScene:
                        await game.Press(Keys.B);
                        await game.Steps(70);
                        await game.CaptureTo(Shot("b-cannon"));
                        break;
                    case StandingSoftBodiesScene standingSoft:
                        game.SetCamera(new Vector3(1.8f, 2.2f, 5.6f), new Vector3(0f, 0.6f, 0f));
                        await game.Steps(180);
                        await game.CaptureTo(Shot("b-beans-close"));
                        if (standingSoft.FallGuys.Count > 0)
                        {
                            var guy = standingSoft.FallGuys[0];
                            game.SetCamera(guy.Hips + new Vector3(1.2f, 1.0f, 2.2f), guy.Hips + new Vector3(0f, 0.3f, 0f));
                            await game.Steps(2);
                            await game.CaptureTo(Shot("b-fallguy"));
                            // A ball in the belly: the whole body dents, then it stands again
                            var ball = Props.Ball(game, 0.2f, 12f, Look.Navy, guy.Hips + new Vector3(-2f, 0.5f, 0f));
                            game.AddToScene(ball);
                            await game.Steps(1);
                            ball.Get<BodyComponent>()!.LinearVelocity = new Vector3(14f, 0f, 0f);
                            await game.Steps(6);
                            await game.CaptureTo(Shot("b-fallguy-hit"));
                            await game.Steps(240);
                            game.SetCamera(guy.Hips + new Vector3(1.2f, 1.0f, 2.2f), guy.Hips + new Vector3(0f, 0.3f, 0f));
                            await game.Steps(2);
                            await game.CaptureTo(Shot("b-fallguy-after"));
                            report.Add($"{index:00} Fall Guy 4 s after the ball: grounded {guy.Body.IsGrounded}, hips at {guy.Hips.Y:0.00} m, on {guy.Body.Ground?.Entity.Name}");
                        }
                        game.SetCamera(scene.Eye, scene.Target);
                        break;
                    case TortureLabScene lab:
                        // The extreme cases, held for the whole long run: five turns each way, ropes pulled all the way out
                        lab.SetTwisting(true);
                        lab.SetPulling(true);
                        await game.Steps(660);
                        foreach (var (name, eye, target) in new[]
                        {
                            ("press", new Vector3(-3.0f, 2.2f, 2.2f), new Vector3(-4.6f, 0.8f, -1.2f)),
                            ("rollers", new Vector3(-0.4f, 2.4f, 2.0f), new Vector3(-1.6f, 1.0f, -1.2f)),
                            ("quartering", new Vector3(2.8f, 2.8f, 2.4f), new Vector3(1.6f, 1.2f, -1.2f)),
                            ("fan", new Vector3(6.6f, 2.4f, 2.4f), new Vector3(4.8f, 1.4f, -1.4f)),
                            ("sausage", new Vector3(-2.2f, 1.8f, 4.6f), new Vector3(-2.6f, 1.1f, 2.0f)),
                            ("pile-driver", new Vector3(4.8f, 1.6f, 4.6f), new Vector3(3.4f, 0.6f, 2.2f)),
                        })
                        {
                            game.SetCamera(eye, target);
                            await game.Steps(2);
                            await game.CaptureTo(Shot("b-" + name));
                        }
                        report.Add(string.Format(CultureInfo.InvariantCulture, "{0:00} torture lab: sausage at {1:0.0} turns each way, quartering ropes {2:0.00} m out", index, lab.Turns, lab.Pull));
                        game.SetCamera(scene.Eye, scene.Target);
                        break;
                    case RopesScene ropes:
                        await game.Press(Keys.I);
                        await game.Press(Keys.M);
                        if (ropes.Crane is { } ropeCrane)
                        {
                            ropeCrane.Load.LinearVelocity = new Vector3(0f, 0f, 4f);
                            ropeCrane.Load.Awake = true;
                        }
                        await game.Steps(40);
                        foreach (var (name, eye, target) in new[]
                        {
                            ("crane", new Vector3(-3.6f, 2.8f, 2.4f), new Vector3(-5.0f, 1.8f, -1.6f)),
                            ("hanging", new Vector3(-1.4f, 2.4f, 1.6f), new Vector3(-2.4f, 1.8f, -1.6f)),
                            ("cable-car", new Vector3(2.0f, 3.0f, 1.6f), new Vector3(1.2f, 2.6f, -1.8f)),
                            ("swing", new Vector3(6.4f, 2.0f, 1.6f), new Vector3(5.0f, 1.2f, -1.4f)),
                            ("trampoline", new Vector3(5.2f, 2.4f, 5.6f), new Vector3(3.6f, 1.2f, 2.4f)),
                        })
                        {
                            game.SetCamera(eye, target);
                            await game.Steps(15);
                            await game.CaptureTo(Shot("b-" + name));
                        }
                        foreach (var line in ropes.Status())
                            report.Add($"{index:00} ropes: {line}");
                        game.SetCamera(scene.Eye, scene.Target);
                        break;
                    case FinaleScene:
                        game.SetOverlay(false);
                        await game.Steps(120);
                        await game.CaptureTo(Shot("b-clean"));
                        await game.Press(Keys.B);
                        game.SetCamera(new Vector3(-1.5f, 3.4f, 7.5f), new Vector3(-1.5f, 0.8f, 0f));
                        await game.Steps(120);
                        await game.CaptureTo(Shot("b-close"));
                        game.SetOverlay(true);
                        game.SetCamera(scene.Eye, scene.Target);
                        break;
                }

                // Long run: frame times without a limit, and the highest and fastest body of each kind
                var probe = new Probe();
                game.ClearFrameTimes();
                for (int done = 0; done < stabilitySteps; done += 30)
                {
                    await game.Steps(30);
                    probe.Sample(game.SceneSystem.SceneInstance.RootScene);
                }
                report.Add($"{index:00} {scene.Name}: no limit, {game.FpsText()}");
                report.Add($"{index:00}   {game.StepTiming()}");
                report.Add($"{index:00}   after {stabilitySteps} steps: {probe.Summary()}");
                if (scene is VideoScene video)
                {
                    foreach (var line in video.Status())
                        report.Add($"{index:00}   status: {line}");
                }
                await game.CaptureTo(Shot($"c-after-{stabilitySteps}"));
            }

            // At the default limit of the window
            game.FpsLimit = DemoSettings.DefaultFpsLimit;
            foreach (var (scene, index) in picked)
            {
                game.LoadScene(scene);
                await game.Steps(120);
                game.ClearFrameTimes();
                await game.Steps(480);
                report.Add($"{index:00} {scene.Name}: limit {game.FpsLimit}, {game.FpsText()}");
            }
            game.FpsLimit = 0;
        }, g => RunGameTest(g));
        Directory.CreateDirectory(folder);
        File.WriteAllLines(Path.Combine(folder, "report.txt"), report);
        Console.WriteLine(string.Join(Environment.NewLine, report));
    }

    /// <summary>
    /// GPU against CPU deformation of the soft bodies, on the scenes with the most of them: per mode, the soft body processor on the CPU,
    /// the frame on the GPU and the frame rate without a limit, two passes in alternating order; then the same frozen instant captured in
    /// both modes for a pixel diff. STRIDE_BEPU_DEMO_DEFORMATION=1 (or =captures for the captures only), report and captures in the tour folder.
    /// </summary>
    [SkippableFact]
    public static void DeformationBench()
    {
        var setting = Environment.GetEnvironmentVariable("STRIDE_BEPU_DEMO_DEFORMATION");
        Skip.IfNot(setting is "1" or "captures", "Deformation bench, set STRIDE_BEPU_DEMO_DEFORMATION=1 to run it");
        var game = new VideoDemoGame(Scenes(), DemoSettings.Label, DemoSettings.CaptureFolder);
        var folder = Path.Combine(DemoSettings.TourFolder, "deformation");
        var report = new List<string>();
        var benched = game.Scenes.Select((scene, i) => (Scene: scene, Index: i + 1))
            .Where(s => s.Scene is SoftBodiesScene or StandingSoftBodiesScene or FinaleScene or DeformationStressScene).ToList();
        SoftBodyDeformationMode[] modes = [SoftBodyDeformationMode.Cpu, SoftBodyDeformationMode.Gpu];
        DemoSettings.RunTour(game, async () =>
        {
            game.FpsLimit = 0;
            foreach (var (scene, index) in benched)
            {
                for (int pass = 0; pass < (setting == "captures" ? 0 : 2); pass++)
                {
                    foreach (var mode in pass == 0 ? modes : modes.Reverse())
                    {
                        game.LoadScene(scene);
                        game.Deformation = mode;
                        // Effects of both modes compile during the first warm up
                        await game.Steps(pass == 0 ? 240 : 120);
                        game.ClearFrameTimes();
                        await game.Steps(360);
                        report.Add($"{index:00} {scene.Name} | {mode} | pass {pass + 1} | {game.FpsText()} | {game.DeformationTiming()}");
                    }
                }

                // One frozen instant in both modes, without the overlay: physics paused, animations of the skeletons held
                game.LoadScene(scene);
                game.Deformation = SoftBodyDeformationMode.Cpu;
                await game.Steps(150);
                game.SetOverlay(false);
                foreach (var component in game.SceneSystem.SceneInstance.RootScene.Entities.SelectMany(e => e.Components))
                {
                    if (component is Bean.Animator bean)
                        bean.Speed = 0f;
                    else if (component is ProceduralCharacter.Animator character)
                        character.Paused = true;
                }
                await game.Press(Keys.Space);
                await game.Frames(10);
                await game.CaptureTo(Path.Combine(folder, $"{index:00}-cpu"));
                // Control: the same mode 30 frames later, what is left moving without the physics
                await game.Frames(30);
                await game.CaptureTo(Path.Combine(folder, $"{index:00}-cpu-control"));
                game.Deformation = SoftBodyDeformationMode.Gpu;
                await game.Frames(30);
                await game.CaptureTo(Path.Combine(folder, $"{index:00}-gpu"));
                // The rigid wireframes over the soft models
                await game.Press(Keys.V);
                await game.Frames(30);
                await game.CaptureTo(Path.Combine(folder, $"{index:00}-gpu-rigid"));
                game.Deformation = SoftBodyDeformationMode.Cpu;
                await game.Frames(30);
                await game.CaptureTo(Path.Combine(folder, $"{index:00}-cpu-rigid"));
                for (int view = 1; view < VideoDemoGame.Views.Length; view++)
                    await game.Press(Keys.V);
                await game.Press(Keys.Space);
                game.SetOverlay(true);
            }
        }, g => RunGameTest(g));
        Directory.CreateDirectory(folder);
        File.WriteAllLines(Path.Combine(folder, "report.txt"), report);
        Console.WriteLine(string.Join(Environment.NewLine, report));
    }

    /// <summary> Active bodies, constraints and synchronized batches of the solver, and the most constrained body of each kind of object </summary>
    /// <remarks> Bepu puts the constraints of a body in separate batches: one body in many constraints multiplies the batches, past 64 it uses its fallback batch </remarks>
    private static string SolverStats(VideoDemoGame game)
    {
        var bepu = game.SceneSystem.SceneInstance.RootScene.Entities.SelectMany(e => e.Components).OfType<CollidableComponent>().Select(c => c.Simulation).First(s => s is not null)!;
        var solver = bepu.Simulation.Solver;
        int constraints = 0;
        for (int b = 0; b < solver.ActiveSet.Batches.Count; b++)
        {
            for (int t = 0; t < solver.ActiveSet.Batches[b].TypeBatches.Count; t++)
                constraints += solver.ActiveSet.Batches[b].TypeBatches[t].ConstraintCount;
        }

        var most = new Dictionary<string, int>();
        var bodies = bepu.Simulation.Bodies.ActiveSet;
        for (int i = 0; i < bodies.Count; i++)
        {
            var owner = bepu.GetComponent(new global::BepuPhysics.Collidables.CollidableReference(global::BepuPhysics.Collidables.CollidableMobility.Dynamic, bodies.IndexToHandle[i]));
            var name = $"{owner.Entity?.Name} ({owner.GetType().Name})";
            most[name] = Math.Max(most.GetValueOrDefault(name), bodies.Constraints[i].Count);
        }
        var fallback = solver.ActiveSet.Batches.Count > solver.FallbackBatchThreshold ? "used" : "unused";
        return $"{bodies.Count} active bodies, {constraints} active constraints in {solver.ActiveSet.Batches.Count} batches (fallback {fallback}); most constraints on one body: "
            + string.Join(", ", most.OrderByDescending(m => m.Value).Select(m => $"{m.Key} {m.Value}"));
    }

    private static Vector3? HumanHips(VideoDemoGame game) => game.SceneSystem.SceneInstance.RootScene.Entities
        .Select(e => e.Get<SkeletonBodyComponent>()).FirstOrDefault(b => b is { BodyCount: > 0 }) is { } body ? body.GetBodyPosition(0) : null;

    /// <summary> The highest point and the largest speed reached by each named kind of physical object </summary>
    private sealed class Probe
    {
        private readonly Dictionary<string, (float Height, float Speed)> _max = new();
        private (string Name, float Speed, Vector3 At) _fastest;
        private readonly Dictionary<Entity, Vector3> _last = new();

        public void Sample(Scene scene)
        {
            foreach (var entity in scene.Entities.SelectMany(Descendants))
            {
                foreach (var component in entity.Components)
                {
                    switch (component)
                    {
                        case BodyComponent { Simulation: not null, Kinematic: false } body:
                            Record(entity.Name, body.Position, body.LinearVelocity);
                            _last[entity] = body.Position;
                            break;
                        case SkeletonBodyComponent bones:
                            for (int i = 0; i < bones.BodyCount; i++)
                                Record(entity.Name + " (bones)", bones.GetBodyPosition(i), bones.GetBodyLinearVelocity(i));
                            break;
                        case SoftBodyComponent soft:
                            for (int i = 0; i < soft.ParticleCount; i += 4)
                                Record(entity.Name + " (particles)", soft.GetParticlePosition(i), soft.GetParticleVelocity(i));
                            break;
                    }
                }
            }
        }

        private void Record(string name, Vector3 position, Vector3 velocity)
        {
            var (height, speed) = _max.GetValueOrDefault(name, (float.MinValue, 0f));
            _max[name] = (MathF.Max(height, position.Y), MathF.Max(speed, velocity.Length()));
            if (velocity.Length() > _fastest.Speed)
                _fastest = (name, velocity.Length(), position);
        }

        public string Summary()
        {
            var highest = _max.OrderByDescending(m => m.Value.Height).Take(3).Select(m => string.Format(CultureInfo.InvariantCulture, "{0} {1:0.0} m", m.Key, m.Value.Height));
            var fastest = _max.OrderByDescending(m => m.Value.Speed).Take(3).Select(m => string.Format(CultureInfo.InvariantCulture, "{0} {1:0.0} m/s", m.Key, m.Value.Speed));
            var farthest = _last.OrderByDescending(l => new Vector2(l.Value.X, l.Value.Z).Length()).FirstOrDefault();
            return "highest: " + string.Join(", ", highest) + "  |  fastest: " + string.Join(", ", fastest)
                + string.Format(CultureInfo.InvariantCulture, "  |  fastest sample at ({0:0.0}, {1:0.0}, {2:0.0})  |  farthest rigid body at the end: {3} ({4:0.0}, {5:0.0}, {6:0.0})",
                    _fastest.At.X, _fastest.At.Y, _fastest.At.Z, farthest.Key?.Name, farthest.Value.X, farthest.Value.Y, farthest.Value.Z);
        }

        private static IEnumerable<Entity> Descendants(Entity entity) => new[] { entity }.Concat(entity.Transform.Children.SelectMany(child => Descendants(child.Entity)));
    }
}
