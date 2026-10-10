// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.IO;
using System.Linq;
using Stride.Core.Mathematics;
using Stride.Input;
using Stride.Graphics.Regression;
using Xunit;

namespace Stride.Harness.Demo;

/// <summary>
/// Opens the interactive demo of the rigid scenes; it runs until the window is closed.
/// Opt-in: set STRIDE_BEPU_DEMO=1. STRIDE_DEMO_LABEL names the build (« AVANT (master) »…),
/// STRIDE_DEMO_CAPTURES is the folder the P key writes to. Run a Release build on the GPU (STRIDE_TESTS_GPU=1).
/// </summary>
public class RigidDemo : GameTestBase
{
    [SkippableFact]
    public static void Interactive()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("STRIDE_BEPU_DEMO") == "1", "Interactive demo, set STRIDE_BEPU_DEMO=1 to open it");

        ForceInteractiveMode = true;
        var game = new BepuDemoGame(RigidScenes.All(), DemoSettings.Label, DemoSettings.CaptureFolder);
        RunGameTest(game);
    }

    /// <summary> A scripted tour through the simulated keyboard and mouse, one capture per step. Opt-in: STRIDE_BEPU_DEMO_TOUR=1 </summary>
    [SkippableFact]
    public static void Tour()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("STRIDE_BEPU_DEMO_TOUR") == "1", "Demo tour, set STRIDE_BEPU_DEMO_TOUR=1 to run it");
        var game = new BepuDemoGame(RigidScenes.All(), DemoSettings.Label, DemoSettings.CaptureFolder);
        DemoSettings.RunTour(game, () => RigidScenes.Tour(game, "avant-"), g => RunGameTest(g));
    }

    /// <summary> Loads every scene for a few frames and saves one P capture, without a window. Opt-in: STRIDE_BEPU_DEMO_SMOKE=1 </summary>
    [SkippableFact]
    public static void Smoke()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("STRIDE_BEPU_DEMO_SMOKE") == "1", "Demo smoke test, set STRIDE_BEPU_DEMO_SMOKE=1 to run it");
        DemoSettings.RunSmoke(new BepuDemoGame(RigidScenes.All(), DemoSettings.Label, DemoSettings.CaptureFolder), game => RunGameTest(game));
    }
}
