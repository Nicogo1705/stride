// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Stride.Core.Mathematics;
using Stride.Graphics.Regression;
using Stride.Harness.Demo;
using Stride.Input;
using Xunit;

namespace Stride.BepuPhysics.Soft.Tests.VideoDemo;

/// <summary>
/// The video showcase: ten scenes that run forever, one per feature, then a finale with all of them.
/// Opt-in: STRIDE_BEPU_DEMO=1 (interactive), STRIDE_BEPU_DEMO_SMOKE=1 (smoke test), STRIDE_BEPU_DEMO_TOUR=1 (scripted tour).
/// </summary>
public class VideoShowcaseDemo : GameTestBase
{
    internal static List<DemoScene> Scenes() => new()
    {
        new TitleScene(),
        new JellyScene(),
        new ClothScene(),
        new MolluscScene(),
        new HitboxScene(),
        new StandingScene(),
        new VoxelScene(),
        new OpacityScene(),
        new TargetPoseScene(),
        new FinaleScene(),
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

    /// <summary> Every scene captured twice (after 2 s, then after an interaction and 5 s more), the finale four times; FPS per scene in fps.txt </summary>
    [SkippableFact]
    public static void Tour()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("STRIDE_BEPU_DEMO_TOUR") == "1", "Demo tour, set STRIDE_BEPU_DEMO_TOUR=1 to run it");
        var game = new VideoDemoGame(Scenes(), DemoSettings.Label, DemoSettings.CaptureFolder);
        var folder = DemoSettings.TourFolder;
        var report = new List<string>();
        DemoSettings.RunTour(game, async () =>
        {
            // Shaders compile in the background on first use: warm every scene up before the shots
            foreach (var scene in game.Scenes.Where((_, i) => Environment.GetEnvironmentVariable("VIDEO_TOUR_ONLY") is null || Environment.GetEnvironmentVariable("VIDEO_TOUR_ONLY")!.Split(',').Contains((i + 1).ToString())))
            {
                game.LoadScene(scene);
                await game.Steps(150);
            }
            int index = 1;
            var only = Environment.GetEnvironmentVariable("VIDEO_TOUR_ONLY")?.Split(',').Select(int.Parse).ToHashSet();
            foreach (var scene in game.Scenes)
            {
                if (only is not null && !only.Contains(index))
                {
                    index++;
                    continue;
                }
                string Shot(string name) => Path.Combine(folder, $"{index:00}-{name}");
                game.LoadScene(scene);
                await game.Steps(120);
                await game.CaptureTo(Shot("a"));
                game.ClearFrameTimes();
                switch (scene)
                {
                    case VoxelScene:
                        game.SetCamera(new Vector3(0f, 7f, 9f), new Vector3(0f, 1.5f, 0f));
                        await game.Frames(2);
                        await game.Press(Keys.X);
                        await game.Press(Keys.X);
                        break;
                    case OpacityScene:
                        await game.Steps(240);
                        await game.CaptureTo(Shot("a2-on"));
                        await game.Press(Keys.O);
                        await game.Frames(2);
                        await game.CaptureTo(Shot("a3-off"));
                        await game.Press(Keys.O);
                        break;
                    case TargetPoseScene:
                        await game.Steps(200);
                        await game.Press(Keys.V);
                        await game.Press(Keys.V);
                        await game.Frames(2);
                        await game.CaptureTo(Shot("a2-faces-arriere"));
                        await PressViews(game, 4);
                        break;
                    case StandingScene:
                        await game.Press(Keys.L);
                        break;
                    case FinaleScene:
                        game.SetOverlay(false);
                        await game.Steps(240);
                        await game.CaptureTo(Shot("b-sans-overlay"));
                        game.SetCamera(new Vector3(-2f, 3f, 6f), new Vector3(-1f, 0.8f, 0f));
                        await game.Steps(180);
                        await game.CaptureTo(Shot("c-proche"));
                        game.SetOverlay(true);
                        await PressViews(game, 3);
                        await game.Frames(2);
                        await game.CaptureTo(Shot("c2-particules"));
                        await game.Press(Keys.V);
                        await game.Frames(2);
                        await game.CaptureTo(Shot("c3-particules-modeles"));
                        await PressViews(game, 2);
                        // The pose of a P capture where the sky took most of the screen
                        const float yaw = 0.5204f, pitch = -0.0021f;
                        var eye = new Vector3(-22.86f, 39.335f, 13.062f);
                        game.SetCamera(eye, eye + new Vector3(-MathF.Sin(yaw) * MathF.Cos(pitch), MathF.Sin(pitch), -MathF.Cos(yaw) * MathF.Cos(pitch)));
                        await game.Frames(3);
                        await game.CaptureTo(Shot("d-ciel"));
                        break;
                    default:
                        await game.Press(Keys.F);
                        break;
                }
                await game.Steps(300);
                await game.CaptureTo(Shot("b"));
                report.Add($"{index:00} {scene.Name}: {game.FpsText()}");
                index++;
            }

            // The tour runs without a limit; K from there goes 30, 60, then 144. Measured on a light scene, where the limit
            // holds, and on the finale, whose physics steps take longer than a frame at 144 FPS
            for (int i = 0; i < 3; i++)
                await game.Press(Keys.K);
            foreach (var (scene, name) in new[] { (game.Scenes[^2], "11-limite-fps-leger"), (game.Scenes[^1], "11-limite-fps-finale") })
            {
                game.LoadScene(scene);
                await game.Steps(60);
                game.ClearFrameTimes();
                await game.Steps(240);
                await game.CaptureTo(Path.Combine(folder, name));
                report.Add($"{name}: limit {game.FpsLimit}, {game.FpsText()}");
            }
            game.FpsLimit = 0;
        }, g => RunGameTest(g));
        Directory.CreateDirectory(folder);
        File.WriteAllLines(Path.Combine(folder, "fps.txt"), report);
        Console.WriteLine(string.Join(Environment.NewLine, report));
    }

    private static async System.Threading.Tasks.Task PressViews(VideoDemoGame game, int count)
    {
        for (int i = 0; i < count; i++)
            await game.Press(Keys.V);
    }
}
