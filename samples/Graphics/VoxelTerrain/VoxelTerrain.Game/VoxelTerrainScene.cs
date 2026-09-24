// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Stride.BepuPhysics;
using Stride.BepuPhysics.Definitions.Colliders;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Colors;
using Stride.Rendering.Compositing;
using Stride.Rendering.Lights;
using Stride.Rendering.Voxels.Grid;
using Stride.UI;
using Stride.UI.Controls;
using Stride.UI.Panels;

namespace VoxelTerrain;

/// <summary>What the world is built with, from the command line.</summary>
public sealed class TerrainOptions
{
    /// <summary>Rings around the camera, finest first; each doubles the reach of the one inside it.</summary>
    public int Rings { get; set; } = 7;

    /// <summary>Samples along each axis of every ring. Cubic in memory and generation cost.</summary>
    public int RingSamples { get; set; } = 129;

    /// <summary>World size of the finest ring's cell.</summary>
    public float CellSize { get; set; } = 0.25f;

    public int Seed { get; set; } = 1;

    /// <summary>The indirect light's tier, or null to start without it.</summary>
    public GIQuality? GI { get; set; } = GIQuality.Medium;

    /// <summary>How many of the finest rings write the shadow maps.</summary>
    public int ShadowRings { get; set; } = 3;

    public bool LevelOfDetail { get; set; } = true;
    public int BeamBlockSize { get; set; } = 8;

    /// <summary>Where the camera starts; null puts it above the ground at the origin.</summary>
    public Vector3? Position { get; set; }

    /// <summary>Save a screenshot here and quit after <see cref="ExitAfter"/> seconds; for unattended runs.</summary>
    public string? Shot { get; set; }
    public float ExitAfter { get; set; }

    /// <summary>The engine's profiler overlay to open at start: fps, cpu or gpu; null for none.</summary>
    public string? Profiler { get; set; }

    /// <summary>
    /// --rings=N --ring-samples=N --cell=F --seed=N --gi=off|low|medium|high --shadow-rings=N
    /// --no-lod --beam=N --pos=x,y,z --shot=FILE --exit-after=SECONDS
    /// </summary>
    public static TerrainOptions Parse(string[] args)
    {
        var options = new TerrainOptions();
        string? Option(string name)
        {
            var prefix = "--" + name + "=";
            return args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
        }
        static float Float(string? text, float fallback) =>
            float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;
        static int Int(string? text, int fallback) => int.TryParse(text, out var value) ? value : fallback;

        options.Rings = Math.Clamp(Int(Option("rings"), options.Rings), 1, 12);
        options.RingSamples = Math.Clamp(Int(Option("ring-samples"), options.RingSamples), 17, 513);
        options.CellSize = Float(Option("cell"), options.CellSize);
        options.Seed = Int(Option("seed"), options.Seed);
        options.ShadowRings = Int(Option("shadow-rings"), options.ShadowRings);
        options.LevelOfDetail = !args.Contains("--no-lod");
        options.BeamBlockSize = Int(Option("beam"), options.BeamBlockSize);
        options.Shot = Option("shot");
        options.ExitAfter = Float(Option("exit-after"), 0f);
        options.Profiler = Option("profiler")?.ToLowerInvariant();
        options.GI = Option("gi")?.ToLowerInvariant() switch
        {
            "off" => null,
            "low" => GIQuality.Low,
            "high" => GIQuality.High,
            "medium" => GIQuality.Medium,
            _ => options.GI,
        };
        if (Option("pos")?.Split(',') is { Length: 3 } parts)
            options.Position = new Vector3(Float(parts[0], 0f), Float(parts[1], 0f), Float(parts[2], 0f));
        return options;
    }
}

/// <summary>
/// Builds the whole scene in code: the clipmap of rings, a sun, an ambient and a sky, a GI volume
/// that follows the camera, a camera that flies and digs.
/// </summary>
public static class VoxelTerrainScene
{
    public static TerrainClipmap? Clipmap { get; private set; }
    public static TerrainOptions Options { get; private set; } = new();

    private static Game? game;
    private static Scene? scene;
    private static Entity? camera;
    private static Entity? giVolume;
    private static readonly List<(LightComponent Light, float Intensity)> sceneLights = [];
    private static Color3 sky;
    private static float skyIntensity;

    public static void Build(Game game, TerrainOptions options)
    {
        VoxelTerrainScene.game = game;
        Options = options;
        scene = game.SceneSystem.SceneInstance.RootScene;
        GIQuality = options.GI ?? GIQuality.Medium;

        // -- the world ------------------------------------------------------------------------------
        Clipmap = new TerrainClipmap(game, scene, options.Rings, options.RingSamples, options.CellSize, options.Seed,
            TerrainMaterials.Build(game.GraphicsDevice), options.ShadowRings, options.LevelOfDetail, options.BeamBlockSize);

        // -- light and sky --------------------------------------------------------------------------
        var sun = new Entity("Sun")
        {
            new LightComponent
            {
                Type = new LightDirectional
                {
                    Color = new ColorRgbProvider(new Color3(1f, 0.95f, 0.85f)),
                    Shadow =
                    {
                        Enabled = true,
                        Size = LightShadowMapSize.Large,
                        Filter = new LightShadowMapFilterTypePcf(),
                        // A surface found by a ray shadows itself along a sawtooth at grazing
                        // angles without some bias; the normal offset pushes the receiver towards the light.
                        BiasParameters = { DepthBias = 0.015f, NormalOffsetScale = 12f },
                    },
                },
                Intensity = 12f,
            },
        };
        sun.Transform.Rotation = Quaternion.RotationYawPitchRoll(0.9f, -0.75f, 0);
        scene.Entities.Add(sun);

        var ambient = new Entity("Ambient")
        {
            new LightComponent { Type = new LightAmbient { Color = new ColorRgbProvider(new Color3(0.50f, 0.60f, 0.78f)) }, Intensity = 1f },
        };
        scene.Entities.Add(ambient);
        sceneLights.Clear();
        sceneLights.Add((sun.Get<LightComponent>(), 12f));
        sceneLights.Add((ambient.Get<LightComponent>(), 1f));

        // A daylight sky: the frame's clear colour, and what the GI cones see past the field.
        sky = new Color3(0.47f, 0.66f, 0.92f);
        skyIntensity = 1f;
        if (FindForwardRenderer(game.SceneSystem.GraphicsCompositor?.Game) is { } forward)
            forward.Clear.Color = new Color4(sky.R, sky.G, sky.B, 1f);

        // -- camera ---------------------------------------------------------------------------------
        var digger = new Digger();
        var scripts = new List<EntityComponent>
        {
            new CameraComponent
            {
                VerticalFieldOfView = 65f,
                NearClipPlane = 0.2f,
                FarClipPlane = 4000f,
                Slot = game.SceneSystem.GraphicsCompositor!.Cameras[0].ToSlotId(),
            },
            new ClipmapFollower(),
            new Hud { Digger = digger },
        };
        // An unattended run measures a still camera: no flying, no digging, whatever the mouse does
        // to the window that just took the focus.
        var unattended = options.Shot is not null || options.ExitAfter > 0f;
        if (unattended)
            scripts.Add(new UnattendedRun { Shot = options.Shot, ExitAfter = options.ExitAfter });
        else
        {
            scripts.Add(new FlyCamera());
            scripts.Add(digger);
        }
        camera = new Entity("Camera");
        foreach (var component in scripts)
            camera.Add(component);
        // Above the ground at the origin: the height is the shader's, evaluated once here on the CPU.
        camera.Transform.Position = options.Position ?? new Vector3(0f, TerrainHeightCpu.Height(0f, 0f, options.Seed) + 12f, 0f);
        camera.Transform.Rotation = Quaternion.RotationYawPitchRoll(MathUtil.Pi * 0.75f, -0.15f, 0);
        scene.Entities.Add(camera);
        scene.Entities.Add(BuildReticle());

        // Rings placed and generated before the first frame draws them, so the balls have ground to land on.
        Clipmap.Update(camera.Transform.Position);

        // Something to drop, so contacts are visible rather than asserted.
        var random = new Random(7);
        for (int i = 0; i < 12; ++i)
        {
            var body = new Entity($"Ball{i}")
            {
                new BodyComponent { Collider = new CompoundCollider { Colliders = { new SphereCollider { Radius = 0.35f } } } },
            };
            body.Transform.Position = camera.Transform.Position + new Vector3((float)(random.NextDouble() - 0.5) * 6f, 2f + i * 0.8f, (float)(random.NextDouble() - 0.5) * 6f);
            scene.Entities.Add(body);
        }

        if (options.GI is not null)
            ToggleGI();

        // The engine's own profiler pages; the GPU one is where the voxel passes show their cost.
        if (options.Profiler is { } page)
        {
            game.ProfilingSystem.EnableProfiling();
            game.ProfilingSystem.FilteringMode = page switch
            {
                "gpu" => Stride.Profiling.GameProfilingResults.GpuEvents,
                "cpu" => Stride.Profiling.GameProfilingResults.CpuEvents,
                _ => Stride.Profiling.GameProfilingResults.Fps,
            };
        }
    }

    /// <summary>Adds or removes a ball of material at a point.</summary>
    public static void Edit(Vector3 centre, float radius, bool fill) => Clipmap?.Dig(centre, radius, fill, TerrainMaterials.Rock);

    // -- switches for the HUD -------------------------------------------------------------------------

    public static bool GIEnabled => giVolume is not null;
    public static GIQuality GIQuality { get; private set; }

    /// <summary>Puts a GI volume around the camera, or takes it away.</summary>
    public static void ToggleGI()
    {
        if (scene is null || camera is null)
            return;
        if (giVolume is not null)
        {
            scene.Entities.Remove(giVolume);
            giVolume = null;
            return;
        }
        // Half a kilometre across at five rings: the finest ring is 32 m around the camera at a
        // quarter-metre voxel, the outermost holds the middle distance at four metres, and each
        // terrain ring injects itself at the GI ring's own level of detail.
        giVolume = VoxelGI.Create(camera.Transform, 512f, 5, GIQuality, sky, skyIntensity);
        giVolume.Transform.Position = camera.Transform.Position;
        scene.Entities.Add(giVolume);
    }

    public static void CycleGIQuality()
    {
        GIQuality = GIQuality switch { GIQuality.Low => GIQuality.Medium, GIQuality.Medium => GIQuality.High, _ => GIQuality.Low };
        if (!GIEnabled)
            return;
        ToggleGI();
        ToggleGI();
    }

    /// <summary>Which surface the walk stops on: cubes, the smooth trilinear surface, or surface nets. Every ring follows.</summary>
    public static VoxelSurfaceForm Surface
    {
        get => (Clipmap?.Rings[0].Grid.Traversal as VoxelGridTraversalDDA)?.Surface ?? VoxelSurfaceForm.MarchingCubes;
        set
        {
            if (Clipmap is null)
                return;
            foreach (var ring in Clipmap.Rings)
                if (ring.Grid.Traversal is VoxelGridTraversalDDA dda)
                    dda.Surface = value;
        }
    }

    public static void CycleSurface() => Surface = Surface switch
    {
        VoxelSurfaceForm.Cubes => VoxelSurfaceForm.MarchingCubes,
        VoxelSurfaceForm.MarchingCubes => VoxelSurfaceForm.SurfaceNets,
        _ => VoxelSurfaceForm.Cubes,
    };

    /// <summary>Whether the sun and the ambient are on. Off is intensity zero: a light that leaves the scene changes every material's lighting permutation and recompiles its shader.</summary>
    public static bool LightsEnabled
    {
        get => sceneLights.Count > 0 && sceneLights[0].Light.Intensity > 0f;
        set { foreach (var (light, intensity) in sceneLights) light.Intensity = value ? intensity : 0f; }
    }

    /// <summary>Whether the near rings write the shadow maps: each cascade walks every casting ring again.</summary>
    public static bool CastShadows
    {
        get => Clipmap?.Rings[0].Grid.CastShadows ?? true;
        set
        {
            if (Clipmap is null)
                return;
            for (int i = 0; i < Clipmap.Rings.Count; i++)
                Clipmap.Rings[i].Grid.CastShadows = value && i < Options.ShadowRings;
        }
    }

    /// <summary>The camera's forward renderer, whose clear colour is the sky.</summary>
    private static ForwardRenderer? FindForwardRenderer(ISceneRenderer? renderer) => renderer switch
    {
        SceneRendererCollection collection => collection.Children.Select(FindForwardRenderer).FirstOrDefault(f => f != null),
        SceneCameraRenderer cameraRenderer => FindForwardRenderer(cameraRenderer.Child),
        ForwardRenderer forward => forward,
        _ => null,
    };

    /// <summary>A small square at the centre of the screen: the aim.</summary>
    private static Entity BuildReticle()
    {
        var dot = new Border
        {
            Width = 6,
            Height = 6,
            BorderThickness = new Thickness(3, 3, 3, 3),
            BorderColor = new Color(1f, 1f, 1f, 0.85f),
        };
        dot.SetCanvasRelativePosition(new Vector3(0.5f, 0.5f, 0f));
        dot.SetCanvasPinOrigin(new Vector3(0.5f, 0.5f, 0f));
        var canvas = new Canvas();
        canvas.Children.Add(dot);
        return new Entity("Reticle")
        {
            new UIComponent
            {
                Page = new UIPage { RootElement = canvas },
                IsFullScreen = true,
                Resolution = new Vector3(1280, 720, 1000),
                RenderGroup = RenderGroup.Group31,
            },
        };
    }
}

/// <summary>Moves the rings with the entity it sits on: the camera.</summary>
public sealed class ClipmapFollower : SyncScript
{
    public override void Update() => VoxelTerrainScene.Clipmap?.Update(Entity.Transform.Position);
}

/// <summary>For a run without anyone at the keyboard: a screenshot, the frame rate on the console, and exit.</summary>
public sealed class UnattendedRun : SyncScript
{
    public string? Shot { get; set; }
    public float ExitAfter { get; set; } = 10f;

    private float elapsed;
    private int frames;
    private float measured;
    private bool done;

    public override void Update()
    {
        if (done)
            return;
        var dt = (float)Game.UpdateTime.Elapsed.TotalSeconds;
        elapsed += dt;
        // The first half is warm-up: shaders compile, rings generate. The second half is measured.
        if (elapsed > ExitAfter * 0.5f)
        {
            frames++;
            measured += dt;
        }
        if (elapsed < ExitAfter)
            return;
        done = true;

        var average = frames > 0 ? measured / frames * 1000f : 0f;
        Console.WriteLine($"[VoxelTerrain] {frames} frames over {measured:0.0} s: {average:0.00} ms per frame, {(average > 0 ? 1000f / average : 0):0.0} fps");
        if (VoxelTerrainScene.Clipmap is { } clipmap)
            Console.WriteLine($"[VoxelTerrain] rings: {string.Join(", ", clipmap.Rings.Select(r => $"L{r.Level} x{r.Generations}"))}");
        if (Shot is not null)
        {
            try
            {
                using var stream = System.IO.File.Create(Shot);
                Game.GraphicsDevice.Presenter.BackBuffer.Save(Game.GraphicsContext.CommandList, stream, ImageFileType.Png);
                Console.WriteLine($"[VoxelTerrain] screenshot: {Shot}");
            }
            catch (Exception e)
            {
                Console.WriteLine($"[VoxelTerrain] screenshot failed: {e.Message}");
            }
        }
        ((Game)Game).Exit();
    }
}

/// <summary>The ground height of TerrainNoise.sdsl, on the CPU, for placing things before the GPU has run.</summary>
public static class TerrainHeightCpu
{
    public static float Height(float x, float z, int seed)
    {
        var q = new Vector2(x, z) + new Vector2(seed * 1000f);
        var continent = Fbm2(q, 1f / 1400f, 4);
        var baseHeight = (continent - 0.45f) * 220f;
        var mountain = MathUtil.SmoothStep(MathUtil.Clamp((continent - 0.55f) / 0.25f, 0f, 1f));
        var ridged = Ridge2(q + new Vector2(500f), 1f / 520f, 5);
        var mountains = ridged * 340f * mountain;
        var hills = (Fbm2(q + new Vector2(200f), 1f / 160f, 4) - 0.5f) * 44f;
        var detail = (Fbm2(q + new Vector2(900f), 1f / 22f, 3) - 0.5f) * 5f;
        return baseHeight + mountains + hills + detail;
    }

    private static float Fbm2(Vector2 p, float frequency, int octaves)
    {
        float sum = 0f, weight = 0.5f, total = 0f;
        for (int i = 0; i < octaves; i++)
        {
            sum += Noise2(p * frequency) * weight;
            total += weight;
            weight *= 0.5f;
            frequency *= 2.03f;
            p += new Vector2(31.7f, 11.3f);
        }
        return sum / total;
    }

    private static float Ridge2(Vector2 p, float frequency, int octaves)
    {
        float sum = 0f, weight = 0.5f, total = 0f;
        for (int i = 0; i < octaves; i++)
        {
            var n = 1f - MathF.Abs(Noise2(p * frequency) * 2f - 1f);
            sum += n * n * weight;
            total += weight;
            weight *= 0.5f;
            frequency *= 2.05f;
            p += new Vector2(17.3f, 29.1f);
        }
        return sum / total;
    }

    private static float Noise2(Vector2 p)
    {
        var xi = MathF.Floor(p.X);
        var yi = MathF.Floor(p.Y);
        var fx = p.X - xi;
        var fy = p.Y - yi;
        fx = fx * fx * (3f - 2f * fx);
        fy = fy * fy * (3f - 2f * fy);
        var a = Hash((int)xi, (int)yi);
        var b = Hash((int)xi + 1, (int)yi);
        var c = Hash((int)xi, (int)yi + 1);
        var d = Hash((int)xi + 1, (int)yi + 1);
        return (a + (b - a) * fx) * (1f - fy) + (c + (d - c) * fx) * fy;
    }

    private static float Hash(int x, int y)
    {
        var h = unchecked((uint)x * 374761393u + (uint)y * 668265263u);
        h = (h ^ (h >> 13)) * 1274126177u;
        return (h ^ (h >> 16)) / 4294967295f;
    }
}
