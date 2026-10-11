// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using Stride.BepuPhysics.Definitions.Colliders;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Harness.Demo;
using Stride.Rendering;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;
using Stride.Rendering.ProceduralModels;

namespace Stride.BepuPhysics.Soft.Tests.VideoDemo;

/// <summary> The palette and the materials shared by every scene of the video showcase </summary>
internal static class Look
{
    // One palette for the whole video: warm, saturated accents over neutral scenery
    public static readonly Color Coral = new(255, 111, 89);
    public static readonly Color Sun = new(255, 193, 69);
    public static readonly Color Teal = new(46, 196, 182);
    public static readonly Color Blue = new(61, 125, 216);
    public static readonly Color Violet = new(142, 108, 239);
    public static readonly Color Mint = new(123, 211, 137);
    public static readonly Color Pink = new(255, 143, 171);
    public static readonly Color Navy = new(72, 84, 122); // slate blue: anything darker reads as black once lit
    public static readonly Color Ivory = new(237, 234, 226);
    public static readonly Color Wood = new(192, 133, 82);
    public static readonly Color Stone = new(150, 146, 140);

    public static readonly Color[] Accents = { Coral, Sun, Teal, Blue, Violet, Mint, Pink };

    private static readonly Dictionary<(Color, float, float), Material> Lit = new();
    private static Material? _floor;
    private static Texture? _sky;
    private static GraphicsDevice? _device;

    /// <summary> Materials are per device: a new game (smoke test, tour) starts a new cache </summary>
    public static void Reset(GraphicsDevice device)
    {
        if (_device == device)
            return;
        _device = device;
        Lit.Clear();
        _floor = null;
        _sky = null;
    }

    /// <summary> A lit material with a specular highlight; the environment term is the polynomial one (materials made at run time have no DFG table) </summary>
    public static Material Paint(GraphicsDevice device, Color color, float gloss = 0.45f, float metal = 0f)
    {
        if (!Lit.TryGetValue((color, gloss, metal), out var material))
            Lit[(color, gloss, metal)] = material = Material.New(device, Describe(color, gloss, metal));
        return material;
    }

    public static MaterialDescriptor Describe(Color color, float gloss = 0.45f, float metal = 0f) => new()
    {
        Attributes =
        {
            Diffuse = new MaterialDiffuseMapFeature(new ComputeColor(color.ToColor4())),
            DiffuseModel = new MaterialDiffuseLambertModelFeature(),
            MicroSurface = new MaterialGlossinessMapFeature(new ComputeFloat(gloss)),
            Specular = new MaterialMetalnessMapFeature(new ComputeFloat(metal)),
            SpecularModel = new MaterialSpecularMicrofacetModelFeature { Environment = new MaterialSpecularMicrofacetEnvironmentGGXPolynomial() },
        },
    };

    /// <summary> A see-through material whose opacity grows with the thickness crossed (OpacityThickness), or a plain alpha blend when <paramref name="thickness"/> is 0 </summary>
    public static Material Volume(GraphicsDevice device, Color color, float alpha, float thickness, MaterialVolumeMedium medium, float gloss = 0.9f)
    {
        var descriptor = Describe(color, gloss);
        descriptor.Attributes.Transparency = new MaterialTransparencyBlendFeature
        {
            Alpha = new ComputeFloat(alpha),
            Tint = new ComputeColor(medium == MaterialVolumeMedium.Absorbing ? color : Color.White),
            OpacityThickness = thickness,
            Medium = medium,
        };
        return Material.New(device, descriptor);
    }

    /// <summary> A faint see-through copy of a color, for ghosts of target poses </summary>
    public static Material Ghost(GraphicsDevice device, Color color, float alpha = 0.28f)
    {
        var descriptor = Describe(color, 0.2f);
        descriptor.Attributes.Transparency = new MaterialTransparencyBlendFeature { Alpha = new ComputeFloat(alpha) };
        return Material.New(device, descriptor);
    }

    /// <summary> Stripes running along V, around the U direction of the texture: on a capsule, lines along its length that a twist turns into a spiral </summary>
    public static Material Striped(GraphicsDevice device, Color color, Color stripe, int count, float gloss = 0.7f)
    {
        const int width = 256;
        var image = Image.New2D(width, 4, MipMapCount.Auto, PixelFormat.R8G8B8A8_UNorm_SRgb);
        for (int mip = 0; mip < image.Description.MipLevels; mip++)
        {
            var buffer = image.PixelBuffer[mip];
            var pixels = new Color[buffer.Width * buffer.Height];
            int step = width / buffer.Width;
            for (int x = 0; x < buffer.Width; x++)
            {
                // Each level averages the full resolution stripes over its texels
                Vector4 sum = Vector4.Zero;
                for (int sx = 0; sx < step; sx++)
                    sum += ((x * step + sx) * count * 2 / width % 2 == 0 ? color : stripe).ToVector4();
                for (int y = 0; y < buffer.Height; y++)
                    pixels[y * buffer.Width + x] = new Color(sum / step);
            }
            buffer.SetPixels(pixels);
        }
        var descriptor = Describe(color, gloss);
        descriptor.Attributes.Diffuse = new MaterialDiffuseMapFeature(new ComputeTextureColor(Texture.New(device, image)) { Filtering = TextureFilter.Anisotropic });
        return Material.New(device, descriptor);
    }

    /// <summary> A procedural model with one material </summary>
    public static Model Model(DemoGame game, PrimitiveProceduralModelBase shape, Material material)
    {
        var model = new ProceduralModelDescriptor(shape).GenerateModel(game.Services);
        model.Materials.Clear();
        model.Materials.Add(new MaterialInstance(material));
        return model;
    }

    public static Model Model(DemoGame game, PrimitiveProceduralModelBase shape, Color color, float gloss = 0.45f) => Model(game, shape, Paint(game.GraphicsDevice, color, gloss));

    /// <summary> A soft checkerboard of 2 m tiles, mipmapped so it fades evenly toward the horizon </summary>
    public static Material Floor(GraphicsDevice device)
    {
        if (_floor is not null)
            return _floor;
        const int size = 256;
        var image = Image.New2D(size, size, MipMapCount.Auto, PixelFormat.R8G8B8A8_UNorm_SRgb);
        var light = new Color(214, 209, 199);
        var dark = new Color(190, 185, 176);
        var line = new Color(170, 165, 157);
        for (int mip = 0; mip < image.Description.MipLevels; mip++)
        {
            var buffer = image.PixelBuffer[mip];
            int width = buffer.Width;
            var pixels = new Color[width * width];
            // Each level is the average of the full resolution pattern over its texels, so distant tiles blend into one tone
            int step = size / width;
            for (int y = 0; y < width; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    Vector4 sum = Vector4.Zero;
                    for (int sy = 0; sy < step; sy++)
                    {
                        for (int sx = 0; sx < step; sx++)
                            sum += Texel(x * step + sx, y * step + sy).ToVector4();
                    }
                    pixels[y * width + x] = new Color(sum / (step * step));
                }
            }
            buffer.SetPixels(pixels);
        }
        Color Texel(int x, int y)
        {
            // Two tiles per texture repeat, with a thin darker joint
            int half = size / 2;
            bool joint = x % half < 2 || y % half < 2;
            if (joint)
                return line;
            return ((x / half) + (y / half)) % 2 == 0 ? light : dark;
        }
        var texture = Texture.New(device, image);
        var color = new ComputeTextureColor(texture) { Filtering = TextureFilter.Anisotropic };
        var descriptor = new MaterialDescriptor
        {
            Attributes =
            {
                Diffuse = new MaterialDiffuseMapFeature(color),
                DiffuseModel = new MaterialDiffuseLambertModelFeature(),
                MicroSurface = new MaterialGlossinessMapFeature(new ComputeFloat(0.3f)),
                Specular = new MaterialMetalnessMapFeature(new ComputeFloat(0f)),
                SpecularModel = new MaterialSpecularMicrofacetModelFeature { Environment = new MaterialSpecularMicrofacetEnvironmentGGXPolynomial() },
            },
        };
        return _floor = Material.New(device, descriptor);
    }

    /// <summary>
    /// A vertical gradient in the equirectangular layout of a skybox, drawn by a <see cref="BackgroundComponent"/>: deep blue at the zenith, hazy near the horizon.
    /// The background leaves the depth buffer at the far plane, where ambient occlusion sees a flat plane and leaves the sky alone.
    /// </summary>
    public static Texture Sky(GraphicsDevice device)
    {
        if (_sky is not null)
            return _sky;
        const int height = 128;
        var image = Image.New2D(4, height, MipMapCount.One, PixelFormat.R8G8B8A8_UNorm_SRgb);
        var pixels = new Color[4 * height];
        var zenith = new Color4(0.24f, 0.45f, 0.78f, 1f);
        var horizon = new Color4(0.80f, 0.86f, 0.93f, 1f);
        var ground = new Color4(0.72f, 0.70f, 0.67f, 1f);
        for (int y = 0; y < height; y++)
        {
            // v goes from the top pole (0) to the bottom pole (1)
            float latitude = MathUtil.PiOverTwo - MathUtil.Pi * (y + 0.5f) / height;
            float up = MathF.Sin(latitude);
            var color = up >= 0f ? Color4.Lerp(horizon, zenith, MathF.Pow(up, 0.55f)) : Color4.Lerp(horizon, ground, MathF.Min(1f, -up * 6f));
            for (int x = 0; x < 4; x++)
                pixels[y * 4 + x] = (Color)color;
        }
        image.PixelBuffer[0].SetPixels(pixels);
        return _sky = Texture.New(device, image);
    }

    /// <summary> A static box whose top is at <paramref name="top"/> </summary>
    public static Entity StaticBox(DemoGame game, Vector3 size, Vector3 center, Material material, string name = "block")
    {
        var entity = new Entity(name)
        {
            new StaticComponent { Collider = new CompoundCollider { Colliders = { new BoxCollider { Size = size } } } },
            new ModelComponent(Model(game, new CubeProceduralModel { Size = size }, material)),
        };
        entity.Transform.Position = center;
        game.AddToScene(entity);
        return entity;
    }

    /// <summary> The checkered ground of every scene, its top at y = 0; wide enough that nothing rolled away falls off its edge during a take </summary>
    public static void Ground(DemoGame game, float size = 1000f)
    {
        var floor = new Entity("ground")
        {
            new StaticComponent { Collider = new CompoundCollider { Colliders = { new BoxCollider { Size = new Vector3(size, 1f, size) } } } },
            new ModelComponent(Model(game, new CubeProceduralModel { Size = new Vector3(size, 1f, size), UvScale = new Vector2(size / 4f) }, Floor(game.GraphicsDevice))),
        };
        floor.Transform.Position = new Vector3(0f, -0.5f, 0f);
        game.AddToScene(floor);
    }
}
