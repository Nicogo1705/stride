// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

#if STRIDE_GRAPHICS_API_VULKAN
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Stride.Core.Mathematics;
using Xunit;

namespace Stride.Graphics.Tests;

/// <summary>
/// CPU cost of recording and submitting a frame heavy in image transitions: render targets cleared then sampled,
/// a mip chain written mip by mip, spread over several command lists. Run in Release with STRIDE_TESTS_GPU=1;
/// set STRIDE_BENCH_OUT to a file to get the per-frame times.
/// </summary>
public class BenchVulkanImageLayouts : GraphicTestGameBase
{
    private const int WarmupFrames = 200;
    private const int MeasuredFrames = 2000;
    private const int CommandListCount = 4;
    private const int TargetsPerList = 16;
    private const int MipCount = 8;

    public BenchVulkanImageLayouts()
    {
        GraphicsDeviceManager.DeviceCreationFlags = DeviceCreationFlags.None;
        GraphicsDeviceManager.SynchronizeWithVerticalRetrace = false;
    }

    [SkippableFact]
    public void RecordAndSubmitTransitions()
    {
        Skip.If(Environment.GetEnvironmentVariable("STRIDE_BENCH_OUT") is null, "Benchmark: set STRIDE_BENCH_OUT to run it.");

        PerformTest(game =>
        {
            var device = game.GraphicsDevice;
            var targets = new Texture[CommandListCount * TargetsPerList];
            for (int i = 0; i < targets.Length; i++)
                targets[i] = Texture.New2D(device, 64, 64, PixelFormat.R8G8B8A8_UNorm, TextureFlags.ShaderResource | TextureFlags.RenderTarget);

            var chain = Texture.New2D(device, 256, 256, MipCount, PixelFormat.R8G8B8A8_UNorm, TextureFlags.ShaderResource | TextureFlags.RenderTarget);
            var mips = new Texture[MipCount];
            for (int mip = 0; mip < MipCount; mip++)
                mips[mip] = chain.ToTextureView(new TextureViewDescription { Type = ViewType.Single, MipLevel = mip, Format = chain.Format, Flags = TextureFlags.ShaderResource | TextureFlags.RenderTarget });

            var lists = new CommandList[CommandListCount];
            for (int i = 0; i < lists.Length; i++)
                lists[i] = CommandList.New(device);
            var compiled = new CompiledCommandList[CommandListCount];

            var times = new List<double>(MeasuredFrames);
            var stopwatch = new Stopwatch();
            for (int frame = 0; frame < WarmupFrames + MeasuredFrames; frame++)
            {
                stopwatch.Restart();
                for (int l = 0; l < lists.Length; l++)
                {
                    var list = lists[l];
                    list.Reset();
                    for (int t = 0; t < TargetsPerList; t++)
                    {
                        var target = targets[l * TargetsPerList + t];
                        list.Clear(target, Color.CornflowerBlue);
                        list.ResourceBarrierTransition(target, BarrierLayout.ShaderResource);
                    }
                    if (l == 0)
                    {
                        for (int mip = 1; mip < MipCount; mip++)
                        {
                            list.ResourceBarrierTransition(mips[mip - 1], BarrierLayout.ShaderResource);
                            list.Clear(mips[mip], Color.Black);
                        }
                        list.ResourceBarrierTransition(chain, BarrierLayout.ShaderResource);
                    }
                    compiled[l] = list.Close();
                }
                device.ExecuteCommandLists(compiled.Length, compiled);
                stopwatch.Stop();

                if (frame >= WarmupFrames)
                    times.Add(stopwatch.Elapsed.TotalMilliseconds);
                device.WaitIdle();
            }

            times.Sort();
            var median = times[times.Count / 2];
            var p95 = times[(int) (times.Count * 0.95)];
            var line = $"{GraphicsDevice.Platform};{device.Adapter.Description};frames={times.Count};median_ms={median:F4};p95_ms={p95:F4};mean_ms={times.Average():F4}";
            File.AppendAllText(Environment.GetEnvironmentVariable("STRIDE_BENCH_OUT")!, line + Environment.NewLine);

            foreach (var list in lists)
                list.Dispose();
        });
    }
}
#endif
