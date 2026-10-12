// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Stride.Core.Diagnostics;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics.Regression;
using Stride.Harness.Demo;
using Xunit;

namespace Stride.BepuPhysics.Soft.Tests.VideoDemo;

/// <summary>
/// The video showcase: one world on a voxel terrain, a zone per feature (soft bodies, characters in every mode, a torture lab, ropes, a voxel playground, a stress test).
/// Opt-in: STRIDE_BEPU_DEMO=1 (interactive), STRIDE_BEPU_DEMO_SMOKE=1 (smoke test), STRIDE_BEPU_DEMO_TOUR=1 (scripted tour).
/// </summary>
public class VideoShowcaseDemo : GameTestBase
{
    internal static List<DemoScene> Scenes() => new() { new WorldScene() };

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
    /// Every place of the world (the zones, then the overview): a shot on arrival, every debug view with its frame rate, a few close-ups,
    /// a long run watched for anything flying away; then the overview with every zone simulating, and every place at the 144 FPS limit.
    /// VIDEO_TOUR_DEFORMATION=Cpu or Gpu, VIDEO_TOUR_ONLY=1,3 (places), VIDEO_TOUR_VIEWS=0 to skip the views, VIDEO_TOUR_STEPS; report in report.txt
    /// </summary>
    [SkippableFact]
    public static void Tour()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("STRIDE_BEPU_DEMO_TOUR") == "1", "Demo tour, set STRIDE_BEPU_DEMO_TOUR=1 to run it");
        var game = new VideoDemoGame(Scenes(), DemoSettings.Label, DemoSettings.CaptureFolder);
        var folder = DemoSettings.TourFolder;
        Progress report;
        int stabilitySteps = int.TryParse(Environment.GetEnvironmentVariable("VIDEO_TOUR_STEPS"), out var steps) ? steps : 900;
        var only = Environment.GetEnvironmentVariable("VIDEO_TOUR_ONLY") is { Length: > 0 } list ? list.Split(',').Select(int.Parse).ToHashSet() : null;
        bool views = Environment.GetEnvironmentVariable("VIDEO_TOUR_VIEWS") != "0";
        var progress = Path.Combine(folder, "progress.txt");
        Directory.CreateDirectory(folder);
        File.WriteAllText(progress, string.Empty);
        // Also written as it goes, to follow a long tour
        report = new Progress(progress);
        // Warnings and errors logged during the tour (a pin holding no particle, a missing asset...) are reported and fail it
        var problems = new List<string>();
        void Logged(ILogMessage message)
        {
            if (message.Type < LogMessageType.Warning)
                return;
            var line = $"{message.Type}: [{message.Module}] {message.Text}";
            lock (problems)
            {
                if (!problems.Contains(line))
                    problems.Add(line);
            }
        }
        GlobalLogger.GlobalMessageLogged += Logged;
        try
        {
            DemoSettings.RunTour(game, async () =>
            {
                game.LoadScene(game.Scenes[0]);
                await game.Frames(2);
                var world = game.World!;
                if (Enum.TryParse<SoftBodyDeformationMode>(Environment.GetEnvironmentVariable("VIDEO_TOUR_DEFORMATION"), true, out var deformation))
                    game.SetOption("Soft body deformation", deformation == SoftBodyDeformationMode.Gpu ? "GPU" : "CPU");
                report.Add($"Soft body deformation: {game.Deformation}  |  terrain: {world.Terrain?.Vertices} vertices");

                var picked = game.Places.Select((place, i) => (Place: place, Index: i + 1)).Where(p => only is null || only.Contains(p.Index)).ToList();
                if (Environment.GetEnvironmentVariable("VIDEO_TOUR_PROFILE") == "1")
                {
                    // Step time and solver batches of each zone's simulation alone, the camera on the zone
                    foreach (var (place, index) in picked)
                    {
                        var zone = world.Zones.FirstOrDefault(z => z.Name == place.Name);
                        world.Solo = zone;
                        await Arrive(game, place);
                        await game.Steps(120);
                        game.ClearStepTiming();
                        await game.Steps(240);
                        report.Add($"{index:00} {place.Name}: {game.StepTiming()}");
                        if (zone?.Simulation is { } simulation)
                            report.Add($"{index:00}   solver: {SolverStats(simulation)}");
                    }
                    world.Solo = null;
                    return;
                }

                // Shaders compile in the background on first use: visit every place before the shots
                foreach (var (place, index) in picked)
                {
                    await Arrive(game, place);
                    game.ClearFrameTimes();
                    await game.Steps(90);
                    report.Add($"{index:00} warm up {place.Name}: {game.FpsText()}");
                }
                foreach (var (place, index) in picked)
                {
                    string Shot(string name) => Path.Combine(folder, $"{index:00}-{name}");
                    var zone = world.Zones.FirstOrDefault(z => z.Name == place.Name);
                    await Arrive(game, place);
                    await game.Steps(150);
                    await game.CaptureTo(Shot("a-start"));
                    if (views)
                    {
                        // Every debug view from the place's camera, with its frame rate
                        for (int view = 0; view < VideoDemoGame.Views.Length; view++)
                        {
                            game.SetDebugView(view);
                            await game.Frames(10);
                            game.ClearFrameTimes();
                            await game.Steps(120);
                            report.Add($"{index:00} view {VideoDemoGame.Views[view]}: {game.FpsText()}");
                            await game.CaptureTo(Shot($"a-view{view}"));
                        }
                        game.SetDebugView(VideoDemoGame.NoDebugView);
                    }

                    // Close-ups, the overlay hidden
                    var closeUps = zone switch
                    {
                        PlaygroundZone playground => new[] { ("overhang", playground.OverhangEye, playground.OverhangTarget) },
                        CharactersZone => new[]
                        {
                            ("animated-ragdoll", zone.Origin + new Vector3(-4.5f, 2.4f, 6.5f), zone.Origin + new Vector3(-4.5f, 1.2f, 0f)),
                            ("active-standing", zone.Origin + new Vector3(4.5f, 2.4f, 6.5f), zone.Origin + new Vector3(4.5f, 1.0f, 0f)),
                        },
                        TortureZone => new[]
                        {
                            ("press-rollers", zone.Origin + new Vector3(-3.4f, 2.4f, 2.6f), zone.Origin + new Vector3(-3.4f, 1.0f, -2.5f)),
                            ("quarter-fan", zone.Origin + new Vector3(3.4f, 2.6f, 2.4f), zone.Origin + new Vector3(3.4f, 1.3f, -2.5f)),
                            ("sausage-rack-driver", zone.Origin + new Vector3(0f, 2.8f, 7.6f), zone.Origin + new Vector3(0f, 1.0f, 2.5f)),
                        },
                        RopesZone => new[] { ("swing-trampoline", zone.Origin + new Vector3(5.4f, 2.4f, 6.4f), zone.Origin + new Vector3(3.2f, 1.2f, 0.8f)) },
                        _ => Array.Empty<(string, Vector3, Vector3)>(),
                    };
                    game.SetOverlay(false);
                    foreach (var (name, eye, target) in closeUps)
                    {
                        game.SetCamera(eye, target);
                        await game.Frames(3);
                        await game.Steps(60);
                        await game.CaptureTo(Shot("b-" + name));
                    }
                    game.SetOverlay(true);
                    await Arrive(game, place);

                    // Long run: frame times without a limit, and the highest and fastest body of each kind
                    var probe = new Probe();
                    game.ClearFrameTimes();
                    game.ClearStepTiming();
                    for (int done = 0; done < stabilitySteps; done += 30)
                    {
                        await game.Steps(30);
                        probe.Sample(game.SceneSystem.SceneInstance.RootScene);
                    }
                    report.Add($"{index:00} {place.Name}: no limit, {game.FpsText()}  |  {world.ActiveZones} zones simulating");
                    report.Add($"{index:00}   {game.StepTiming()}");
                    report.Add($"{index:00}   after {stabilitySteps} steps: {probe.Summary()}");
                    foreach (var line in zone?.Status() ?? Enumerable.Empty<string>())
                        report.Add($"{index:00}   status: {line}");
                    await game.CaptureTo(Shot($"c-after-{stabilitySteps}"));
                }

                // At the default limit of the window, before every zone simulates: the overview measure leaves the simulations behind, catching up for a while
                game.FpsLimit = DemoSettings.DefaultFpsLimit;
                foreach (var (place, index) in picked)
                {
                    await Arrive(game, place);
                    await game.Steps(120);
                    game.ClearFrameTimes();
                    game.ClearStepTiming();
                    await game.Steps(480);
                    report.Add($"{index:00} {place.Name}: limit {game.FpsLimit}, {game.FpsText()}  |  {game.StepTiming()}");
                    foreach (var line in world.Zones.FirstOrDefault(z => z.Name == place.Name)?.Status() ?? Enumerable.Empty<string>())
                        report.Add($"{index:00}   status: {line}");
                }
                game.FpsLimit = 0;

                // The overview with only the zones near the camera simulating, then all of them
                if (game.Places.FirstOrDefault(p => p.Name == "Overview") is { } overview)
                {
                    await Arrive(game, overview);
                    foreach (var all in new[] { false, true })
                    {
                        world.ActivateAll = all;
                        await game.Steps(120);
                        game.ClearStepTiming();
                        game.ClearFrameTimes();
                        await game.Steps(360);
                        report.Add($"overview, {(all ? "every zone" : "nearest zone only")} simulating ({world.ActiveZones}): {game.FpsText()}  |  {game.StepTiming()}");
                    }
                    world.ActivateAll = false;
                    await game.Frames(3);
                    game.SetOverlay(false);
                    await game.Steps(10);
                    await game.CaptureTo(Path.Combine(folder, "overview-clean"));
                    game.SetOverlay(true);
                }
            }, g => RunGameTest(g));
        }
        finally
        {
            GlobalLogger.GlobalMessageLogged -= Logged;
        }
        foreach (var problem in problems)
            report.Add("LOGGED " + problem);
        Directory.CreateDirectory(folder);
        File.WriteAllLines(Path.Combine(folder, "report.txt"), report);
        Console.WriteLine(string.Join(Environment.NewLine, report));
        Assert.True(problems.Count == 0, $"{problems.Count} warnings or errors logged during the tour:{Environment.NewLine}{string.Join(Environment.NewLine, problems)}");
    }

    /// <summary>
    /// GPU against CPU deformation of the soft bodies, at the zones with the most of them: per mode, the soft body processor on the CPU,
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
        SoftBodyDeformationMode[] modes = [SoftBodyDeformationMode.Cpu, SoftBodyDeformationMode.Gpu];
        DemoSettings.RunTour(game, async () =>
        {
            game.FpsLimit = 0;
            game.LoadScene(game.Scenes[0]);
            await game.Frames(2);
            var world = game.World!;
            var benched = world.Zones.Select((zone, i) => (Zone: zone, Index: i + 1)).Where(z => z.Zone is CharactersZone or GardenZone or StressZone).ToList();
            foreach (var (zone, index) in benched)
            {
                var place = game.Places.First(p => p.Name == zone.Name);
                for (int pass = 0; pass < (setting == "captures" ? 0 : 2); pass++)
                {
                    foreach (var mode in pass == 0 ? modes : modes.Reverse())
                    {
                        await Arrive(game, place);
                        game.Deformation = mode;
                        // Effects of both modes compile during the first warm up
                        await game.Steps(pass == 0 ? 240 : 120);
                        game.ClearFrameTimes();
                        await game.Steps(360);
                        report.Add($"{index:00} {zone.Name} | {mode} | pass {pass + 1} | {game.FpsText()} | {game.DeformationTiming()}");
                    }
                }

                // One frozen instant in both modes, without the overlay: physics stopped, animations of the skeletons held
                await Arrive(game, place);
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
                world.Frozen = true;
                await game.Frames(10);
                await game.CaptureTo(Path.Combine(folder, $"{index:00}-cpu"));
                // Control: the same mode 30 frames later, what is left moving without the physics
                await game.Frames(30);
                await game.CaptureTo(Path.Combine(folder, $"{index:00}-cpu-control"));
                game.Deformation = SoftBodyDeformationMode.Gpu;
                await game.Frames(30);
                await game.CaptureTo(Path.Combine(folder, $"{index:00}-gpu"));
                // The rigid wireframes over the soft models
                game.SetDebugView(VideoDemoGame.CollidersView);
                await game.Frames(30);
                await game.CaptureTo(Path.Combine(folder, $"{index:00}-gpu-rigid"));
                game.Deformation = SoftBodyDeformationMode.Cpu;
                await game.Frames(30);
                await game.CaptureTo(Path.Combine(folder, $"{index:00}-cpu-rigid"));
                game.SetDebugView(VideoDemoGame.NoDebugView);
                world.Frozen = false;
                game.SetOverlay(true);
            }
        }, g => RunGameTest(g));
        Directory.CreateDirectory(folder);
        File.WriteAllLines(Path.Combine(folder, "report.txt"), report);
        Console.WriteLine(string.Join(Environment.NewLine, report));
    }

    /// <summary> Moves the camera, then lets the zones switch before any step </summary>
    /// <remarks> Steps awaits a simulation; one disabled while awaited never steps again </remarks>
    private static async System.Threading.Tasks.Task Arrive(VideoDemoGame game, DemoPlace place)
    {
        game.GoTo(place);
        await game.Frames(3);
    }

    /// <summary> A report also appended to a file line by line </summary>
    private sealed class Progress(string path) : List<string>
    {
        public new void Add(string line)
        {
            base.Add(line);
            File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}");
        }
    }

    /// <summary> Active bodies, constraints and synchronized batches of the solver, and the most constrained body of each kind of object </summary>
    /// <remarks> Bepu puts the constraints of a body in separate batches: one body in many constraints multiplies the batches, past 64 it uses its fallback batch </remarks>
    private static string SolverStats(BepuSimulation bepu)
    {
        var solver = bepu.Simulation.Solver;
        int constraints = 0;
        for (int b = 0; b < solver.ActiveSet.Batches.Count; b++)
        {
            for (int t = 0; t < solver.ActiveSet.Batches[b].TypeBatches.Count; t++)
                constraints += solver.ActiveSet.Batches[b].TypeBatches[t].ConstraintCount;
        }

        var most = new Dictionary<string, int>();
        var bodiesOf = new Dictionary<string, (int Bodies, int Constraints)>();
        var bodies = bepu.Simulation.Bodies.ActiveSet;
        for (int i = 0; i < bodies.Count; i++)
        {
            var owner = bepu.GetComponent(new global::BepuPhysics.Collidables.CollidableReference(global::BepuPhysics.Collidables.CollidableMobility.Dynamic, bodies.IndexToHandle[i]));
            var name = $"{owner.Entity?.Name} ({owner.GetType().Name})";
            most[name] = Math.Max(most.GetValueOrDefault(name), bodies.Constraints[i].Count);
            var (count, links) = bodiesOf.GetValueOrDefault(name);
            bodiesOf[name] = (count + 1, links + bodies.Constraints[i].Count);
        }
        var fallback = solver.ActiveSet.Batches.Count > solver.FallbackBatchThreshold ? "used" : "unused";
        return $"{bodies.Count} active bodies, {constraints} active constraints in {solver.ActiveSet.Batches.Count} batches (fallback {fallback}); most constraints on one body: "
            + string.Join(", ", most.OrderByDescending(m => m.Value).Take(6).Select(m => $"{m.Key} {m.Value}"))
            + "; heaviest objects (bodies, constraint ends): " + string.Join(", ", bodiesOf.OrderByDescending(b => b.Value.Constraints).Take(8).Select(b => $"{b.Key} {b.Value.Bodies}/{b.Value.Constraints}"));
    }

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
            // Lowest rigid body: anything under the terrain fell through it
            var lowest = _last.OrderBy(l => l.Value.Y).FirstOrDefault();
            return "highest: " + string.Join(", ", highest) + "  |  fastest: " + string.Join(", ", fastest)
                + string.Format(CultureInfo.InvariantCulture, "  |  fastest sample at ({0:0.0}, {1:0.0}, {2:0.0})  |  lowest rigid body at the end: {3} ({4:0.0}, {5:0.0}, {6:0.0})",
                    _fastest.At.X, _fastest.At.Y, _fastest.At.Z, lowest.Key?.Name, lowest.Value.X, lowest.Value.Y, lowest.Value.Z);
        }

        private static IEnumerable<Entity> Descendants(Entity entity) => new[] { entity }.Concat(entity.Transform.Children.SelectMany(child => Descendants(child.Entity)));
    }
}
