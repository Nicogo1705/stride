// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.IO;

namespace Stride.Harness.Demo;

/// <summary> Settings the launch command passes to a demo </summary>
public static class DemoSettings
{
    /// <summary> Folder of the scripted tour's captures, under the P folder </summary>
    public static string TourFolder => Path.Combine(CaptureFolder, "tour");

    /// <summary>
    /// Runs a scripted tour in a visible window, driven through the simulated keyboard and mouse,
    /// and rethrows its failure after the game exits
    /// </summary>
    public static void RunTour(DemoGame game, Func<System.Threading.Tasks.Task> tour, Action<Graphics.Regression.GameTestBase> run)
    {
        Exception? failure = null;
        game.ShowWindow();
        game.Script.AddTask(async () =>
        {
            try
            {
                game.ScreenShotAutomationEnabled = false;
                await game.Frames(3);
                await tour();
            }
            catch (Exception e)
            {
                failure = e;
            }
            game.Exit();
        });
        run(game);
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
    }

    /// <summary> Runs <see cref="DemoGame.SmokeTest"/> and rethrows its failure after the game exits </summary>
    public static void RunSmoke(DemoGame game, Action<Graphics.Regression.GameTestBase> run)
    {
        Exception? failure = null;
        game.Script.AddTask(async () =>
        {
            try
            {
                var capture = await game.SmokeTest();
                Console.WriteLine("Demo smoke test: every scene loaded, P wrote " + capture + ".png and .txt");
            }
            catch (Exception e)
            {
                failure = e;
            }
            game.Exit();
        });
        run(game);
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
    }

    public static string Label => Environment.GetEnvironmentVariable("STRIDE_DEMO_LABEL") is { Length: > 0 } label ? label : "(build sans étiquette)";

    public static string CaptureFolder => Environment.GetEnvironmentVariable("STRIDE_DEMO_CAPTURES") is { Length: > 0 } folder
        ? folder
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "PR", "demo", "Logs");
}
