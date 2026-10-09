// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Diagnostics;
using System.IO;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Extensions;
using Stride.Games;
using Stride.Graphics;
using Stride.Graphics.GeometricPrimitives;
using Stride.Graphics.Regression;
using Stride.Rendering;
using Stride.Rendering.Colors;
using Stride.Rendering.Lights;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;
using Xunit;

namespace Stride.BepuPhysics.Tests
{
    /// <summary>
    /// Frame and GPU times of OpacityThickness: 12 volumes stacked in front of the camera at 1080p, no vsync.
    /// Run with STRIDE_TESTS_GPU=1, in Release, with nothing else building.
    /// </summary>
    public class VolumeOpacityPerf : GameTestBase
    {
        // A visible window cycling the variants every 8 s, the frame time in its title
        [Fact]
        public static void Live()
        {
            if (Environment.GetEnvironmentVariable("VOLUME_PERF_LIVE") != "1")
                return;

            // A visible window: hidden, the GPU driver keeps the card at idle clocks
            ForceInteractiveMode = true;
            var game = new GameTest();
            game.GraphicsDeviceManager.PreferredGraphicsProfile = [GraphicsProfile.Level_11_0];
            game.GraphicsDeviceManager.ShaderProfile = GraphicsProfile.Level_11_0;
            game.GraphicsDeviceManager.PreferredBackBufferWidth = 1920;
            game.GraphicsDeviceManager.PreferredBackBufferHeight = 1080;
            game.GraphicsDeviceManager.SynchronizeWithVerticalRetrace = false;
            game.GraphicsDeviceManager.DeviceCreationFlags = DeviceCreationFlags.None;
            game.IsFixedTimeStep = false;

            game.Script.AddTask(async () =>
            {
                game.ScreenShotAutomationEnabled = false;
                game.TreatNotFocusedLikeMinimized = false;
                game.MinimizedMinimumUpdateRate.MinimumElapsedTime = TimeSpan.Zero;
                game.WindowMinimumUpdateRate.MinimumElapsedTime = TimeSpan.Zero;
                var scene = game.SceneSystem.SceneInstance.RootScene;
                var device = game.GraphicsDevice;
                var camera = new CameraComponent();
                game.SceneSystem.GraphicsCompositor = Stride.Rendering.Compositing.GraphicsCompositorHelper.CreateDefault(true, camera: camera);
                var forward = (Stride.Rendering.Compositing.ForwardRenderer)((Stride.Rendering.Compositing.SceneCameraRenderer)game.SceneSystem.GraphicsCompositor.Game).Child;
                var stage = forward.VolumeThicknessRenderStage;
                scene.Entities.Add(new Entity { camera });
                var sun = new Entity { new LightComponent { Type = new LightDirectional { Color = new ColorRgbProvider(Color.White), Shadow = { Enabled = Environment.GetEnvironmentVariable("VOLUME_PERF_SHADOWS") == "1" } }, Intensity = 3f } };
                sun.Transform.Rotation = Quaternion.RotationYawPitchRoll(0.4f, -0.8f, 0f);
                scene.Entities.Add(sun);
                scene.Entities.Add(new Entity { new LightComponent { Type = new LightAmbient { Color = new ColorRgbProvider(Color.White) }, Intensity = 0.3f } });

                MaterialDescriptor Lit(Color4 colour) => new()
                {
                    Attributes =
                    {
                        Diffuse = new MaterialDiffuseMapFeature(new ComputeColor(colour)),
                        DiffuseModel = new MaterialDiffuseLambertModelFeature(),
                        MicroSurface = new MaterialGlossinessMapFeature(new ComputeFloat(0.7f)),
                        Specular = new MaterialMetalnessMapFeature(new ComputeFloat(0f)),
                        SpecularModel = new MaterialSpecularMicrofacetModelFeature { Environment = new MaterialSpecularMicrofacetEnvironmentGGXPolynomial() },
                    },
                };
                Entity Make(GeometricPrimitive primitive, MaterialDescriptor descriptor, Vector3 position)
                {
                    var model = new Model { Material.New(device, descriptor) };
                    model.Add(new Mesh { Draw = primitive.ToMeshDraw(), MaterialIndex = 0 });
                    var entity = new Entity { new ModelComponent { Model = model } };
                    entity.Transform.Position = position;
                    return entity;
                }

                scene.Entities.Add(Make(GeometricPrimitive.Cube.New(device, new Vector3(60f, 1f, 60f)), Lit(new Color4(0.8f, 0.75f, 0.6f, 1f)), new Vector3(0, -3.5f, -20f)));
                scene.Entities.Add(Make(GeometricPrimitive.Cube.New(device, new Vector3(60f, 30f, 1f)), Lit(new Color4(0.5f, 0.6f, 0.7f, 1f)), new Vector3(0, 10f, -40f)));

                string[] variants = ["opaque-only", "alpha", "absorbing", "absorbing-nopass", "scattering", "scattering-nopass"];
                var sets = new System.Collections.Generic.Dictionary<string, Entity[]>();
                foreach (var variant in variants)
                {
                    var list = new System.Collections.Generic.List<Entity>();
                    for (int i = 0; i < (variant == "opaque-only" ? 1 : 12); i++)
                    {
                        var descriptor = Lit(new Color4(0.3f + 0.05f * i, 0.6f, 0.9f - 0.05f * i, 1f));
                        if (variant != "opaque-only")
                        {
                            descriptor.Attributes.Transparency = new MaterialTransparencyBlendFeature
                            {
                                Alpha = new ComputeFloat(0.15f),
                                Tint = new ComputeColor(new Color4(0.8f, 0.9f, 1f, 1f)),
                                OpacityThickness = variant == "alpha" ? 0f : 1f,
                                Medium = variant.StartsWith("scattering") ? MaterialVolumeMedium.Scattering : MaterialVolumeMedium.Absorbing,
                            };
                        }
                        var z = -3f - i * 2.2f;
                        var size = 2f * -z;
                        list.Add(Make(GeometricPrimitive.Cube.New(device, new Vector3(size * 1.9f, size, 1f)), descriptor, new Vector3(0, 0, z)));
                    }
                    sets[variant] = list.ToArray();
                }

                // Two rounds of every variant, 8 s each
                for (int round = 0; round < 2; round++)
                {
                    foreach (var variant in variants)
                    {
                        forward.VolumeThicknessRenderStage = variant.EndsWith("-nopass") ? null : stage;
                        foreach (var entity in sets[variant])
                            scene.Entities.Add(entity);
                        var shown = Stopwatch.StartNew();
                        var second = Stopwatch.StartNew();
                        int frames = 0;
                        while (shown.Elapsed.TotalSeconds < 8)
                        {
                            await game.Script.NextFrame();
                            frames++;
                            if (second.Elapsed.TotalSeconds >= 0.5)
                            {
                                game.Window.Title = $"{variant}: {second.Elapsed.TotalMilliseconds / frames:F2} ms ({frames / second.Elapsed.TotalSeconds:F0} FPS)";
                                var log = Environment.GetEnvironmentVariable("VOLUME_PERF_LOG");
                                if (log != null)
                                    File.AppendAllText(log, $"round {round + 1}\t{game.Window.Title}\n");
                                frames = 0;
                                second.Restart();
                            }
                        }
                        foreach (var entity in sets[variant])
                            scene.Entities.Remove(entity);
                    }
                }
                game.Exit();
            });
            RunGameTest(game);
        }

        [Theory]
        [InlineData("opaque-only")]
        [InlineData("alpha")]
        [InlineData("absorbing")]
        [InlineData("scattering")]
        [InlineData("absorbing-nopass")]
        [InlineData("scattering-nopass")]
        [InlineData("absorbing-segments")]
        [InlineData("scattering-segments")]
        public static void Perf(string variant)
        {
            var root = Environment.GetEnvironmentVariable("VOLUME_PERF_OUT");
            if (root is null)
                return;
            Directory.CreateDirectory(root);

            ForceInteractiveMode = true;
            var game = new GameTest();
            game.GraphicsDeviceManager.PreferredGraphicsProfile = [GraphicsProfile.Level_11_0];
            game.GraphicsDeviceManager.ShaderProfile = GraphicsProfile.Level_11_0;
            var half = Environment.GetEnvironmentVariable("VOLUME_PERF_HALF") == "1";
            game.GraphicsDeviceManager.PreferredBackBufferWidth = half ? 960 : 1920;
            game.GraphicsDeviceManager.PreferredBackBufferHeight = half ? 540 : 1080;
            game.GraphicsDeviceManager.SynchronizeWithVerticalRetrace = false;
            // No D3D debug layer: it costs more than what is measured
            game.GraphicsDeviceManager.DeviceCreationFlags = DeviceCreationFlags.None;
            game.IsFixedTimeStep = false;

            game.Script.AddTask(async () =>
            {
                game.ScreenShotAutomationEnabled = false;
                game.MinimizedMinimumUpdateRate.MinimumElapsedTime = TimeSpan.Zero;
                game.WindowMinimumUpdateRate.MinimumElapsedTime = TimeSpan.Zero;
                var scene = game.SceneSystem.SceneInstance.RootScene;
                var device = game.GraphicsDevice;
                var camera = new CameraComponent();
                game.SceneSystem.GraphicsCompositor = Stride.Rendering.Compositing.GraphicsCompositorHelper.CreateDefault(true, camera: camera);
                scene.Entities.Add(new Entity { camera });
                if (game.SceneSystem.GraphicsCompositor.Game is Stride.Rendering.Compositing.SceneCameraRenderer { Child: Stride.Rendering.Compositing.ForwardRenderer forward })
                {
                    forward.VolumeThicknessRenderStage = variant.EndsWith("-nopass") ? null : forward.VolumeThicknessRenderStage;
                    forward.SeparateVolumeSegments = variant.EndsWith("-segments");
                }
                var sun = new Entity { new LightComponent { Type = new LightDirectional { Color = new ColorRgbProvider(Color.White), Shadow = { Enabled = Environment.GetEnvironmentVariable("VOLUME_PERF_SHADOWS") == "1" } }, Intensity = 3f } };
                sun.Transform.Rotation = Quaternion.RotationYawPitchRoll(0.4f, -0.8f, 0f);
                scene.Entities.Add(sun);
                scene.Entities.Add(new Entity { new LightComponent { Type = new LightAmbient { Color = new ColorRgbProvider(Color.White) }, Intensity = 0.3f } });

                MaterialDescriptor Lit(Color4 colour) => new()
                {
                    Attributes =
                    {
                        Diffuse = new MaterialDiffuseMapFeature(new ComputeColor(colour)),
                        DiffuseModel = new MaterialDiffuseLambertModelFeature(),
                        MicroSurface = new MaterialGlossinessMapFeature(new ComputeFloat(0.7f)),
                        Specular = new MaterialMetalnessMapFeature(new ComputeFloat(0f)),
                        SpecularModel = new MaterialSpecularMicrofacetModelFeature { Environment = new MaterialSpecularMicrofacetEnvironmentGGXPolynomial() },
                    },
                };
                void Add(GeometricPrimitive primitive, MaterialDescriptor descriptor, Vector3 position)
                {
                    var model = new Model { Material.New(device, descriptor) };
                    model.Add(new Mesh { Draw = primitive.ToMeshDraw(), MaterialIndex = 0 });
                    var entity = new Entity { new ModelComponent { Model = model } };
                    entity.Transform.Position = position;
                    scene.Entities.Add(entity);
                }

                // A floor and a back wall
                Add(GeometricPrimitive.Cube.New(device, new Vector3(60f, 1f, 60f)), Lit(new Color4(0.8f, 0.75f, 0.6f, 1f)), new Vector3(0, -3.5f, -20f));
                Add(GeometricPrimitive.Cube.New(device, new Vector3(60f, 30f, 1f)), Lit(new Color4(0.5f, 0.6f, 0.7f, 1f)), new Vector3(0, 10f, -40f));

                // 12 boxes, each wider than the view at its distance
                for (int i = 0; i < 12; i++)
                {
                    var descriptor = Lit(new Color4(0.3f + 0.05f * i, 0.6f, 0.9f - 0.05f * i, 1f));
                    if (variant != "opaque-only")
                    {
                        descriptor.Attributes.Transparency = new MaterialTransparencyBlendFeature
                        {
                            Alpha = new ComputeFloat(0.15f),
                            Tint = new ComputeColor(new Color4(0.8f, 0.9f, 1f, 1f)),
                            OpacityThickness = variant == "alpha" ? 0f : 1f,
                            Medium = variant.StartsWith("scattering") ? MaterialVolumeMedium.Scattering : MaterialVolumeMedium.Absorbing,
                        };
                    }
                    if (variant == "opaque-only" && i > 0)
                        break;
                    var z = -3f - i * 2.2f;
                    var size = 2f * -z;
                    Add(GeometricPrimitive.Cube.New(device, new Vector3(size * 1.9f, size, 1f)), descriptor, new Vector3(0, 0, z));
                }

                // Warm up (effect compilation), then time 400 frames
                while (game.UpdateTime.FrameCount < 120)
                    await game.Script.NextFrame();
                var frameTimes = new double[400];
                var watch = Stopwatch.StartNew();
                for (int frame = 0; frame < frameTimes.Length; frame++)
                {
                    await game.Script.NextFrame();
                    frameTimes[frame] = watch.Elapsed.TotalMilliseconds;
                    watch.Restart();
                }
                Array.Sort(frameTimes);
                File.AppendAllText(Path.Combine(root, "perf.txt"), $"{variant}\tmedian {frameTimes[frameTimes.Length / 2]:F3} ms\tp95 {frameTimes[frameTimes.Length * 95 / 100]:F3} ms\n");

                // Time per profiling key over 100 more frames (GPU keys are the render passes)
                Stride.Core.Diagnostics.Profiler.EnableAll();
                var events = Stride.Core.Diagnostics.Profiler.Subscribe();
                var totals = new System.Collections.Generic.Dictionary<string, double>();
                var profiledStart = game.UpdateTime.FrameCount;
                while (game.UpdateTime.FrameCount < profiledStart + 100)
                {
                    await game.Script.NextFrame();
                    while (events.TryRead(out var e))
                    {
                        if (e.Type != Stride.Core.Diagnostics.ProfilingMessageType.End)
                            continue;
                        var key = $"{e.Key.Name} [thread {e.ThreadId}]";
                        totals.TryGetValue(key, out var sum);
                        totals[key] = sum + e.ElapsedTime.TotalMilliseconds;
                    }
                }
                foreach (var pair in System.Linq.Enumerable.OrderByDescending(totals, t => t.Value))
                    File.AppendAllText(Path.Combine(root, $"gpu-{variant}.txt"), $"{pair.Value / 100:F3} ms\t{pair.Key}\n");
                game.Exit();
            });
            RunGameTest(game);
        }
    }
}
