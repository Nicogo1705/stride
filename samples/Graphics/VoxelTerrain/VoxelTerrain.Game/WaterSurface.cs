// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System.Collections.Generic;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Images;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;
using Stride.Rendering.ProceduralModels;

namespace VoxelTerrain;

/// <summary>
/// The water as drawn: a mesh over the simulation's domain, lifted to the simulated surface in the
/// vertex stage, and four flat strips of sea out to the horizon around it. One transparent
/// material does the look (WaterSurfaceColor.sdsl), reading the scene behind it; a tint over the
/// screen says when the camera is under the surface.
/// </summary>
public sealed class WaterSurface
{
    private readonly WaterSim sim;
    private readonly Entity domain;
    private readonly List<Entity> far = [];
    private readonly Material material;
    private readonly Material farMaterial;
    private readonly Vignetting? underwater;
    private float time;

    public bool Underwater { get; private set; }

    public WaterSurface(Game game, Scene scene, WaterSim sim, Color3 sky, Vignetting? underwaterTint)
    {
        this.sim = sim;
        underwater = underwaterTint;
        var device = game.GraphicsDevice;
        material = Build(device, simulated: true);
        farMaterial = Build(device, simulated: false);
        foreach (var m in new[] { material, farMaterial })
            m.Passes[0].Parameters.Set(WaterSurfaceColorKeys.WaterSkyColor, new Vector3(sky.R, sky.G, sky.B));

        // The domain's mesh: a column per quad, so the surface can bend at the shore.
        var plane = new PlaneProceduralModel { Size = new Vector2(WaterSim.Extent), Tessellation = new Int2(WaterSim.Size), MaterialInstance = { Material = material } };
        domain = new Entity("Water") { new ModelComponent((Model)plane.Generate(game.Services)) { IsShadowCaster = false } };
        scene.Entities.Add(domain);

        // The far sea, in four strips around the domain to the horizon; the same look, still.
        const float reach = 12000f;
        var side = (reach - WaterSim.Extent) * 0.5f;
        var strips = new (Vector2 Size, Vector2 Offset)[]
        {
            (new Vector2(reach, side), new Vector2(0f, WaterSim.Extent * 0.5f + side * 0.5f)),
            (new Vector2(reach, side), new Vector2(0f, -(WaterSim.Extent * 0.5f + side * 0.5f))),
            (new Vector2(side, WaterSim.Extent), new Vector2(WaterSim.Extent * 0.5f + side * 0.5f, 0f)),
            (new Vector2(side, WaterSim.Extent), new Vector2(-(WaterSim.Extent * 0.5f + side * 0.5f), 0f)),
        };
        foreach (var (size, offset) in strips)
        {
            var strip = new PlaneProceduralModel { Size = size, Tessellation = new Int2(16), MaterialInstance = { Material = farMaterial } };
            var entity = new Entity("Sea") { new ModelComponent((Model)strip.Generate(game.Services)) { IsShadowCaster = false } };
            entity.Transform.Position = new Vector3(offset.X, WaterSim.SeaLevel, offset.Y);
            far.Add(entity);
            scene.Entities.Add(entity);
        }

    }

    private static Material Build(GraphicsDevice device, bool simulated)
    {
        var descriptor = new MaterialDescriptor
        {
            Attributes =
            {
                Displacement = simulated
                    ? new MaterialDisplacementMapFeature(new ComputeShaderClassScalar { MixinReference = "WaterSurfaceDisplace" })
                    {
                        Intensity = new ComputeFloat(1f),
                        ScaleAndBias = false,
                        Stage = DisplacementMapStage.Vertex,
                    }
                    : null,
                Surface = new MaterialNormalMapFeature(new ComputeShaderClassColor { MixinReference = "WaterSurfaceNormal" }) { ScaleAndBias = true, IsXYNormal = false },
                Diffuse = new MaterialDiffuseMapFeature(new ComputeColor(new Color4(0f, 0f, 0f, 1f))),
                DiffuseModel = new MaterialDiffuseLambertModelFeature(),
                MicroSurface = new MaterialGlossinessMapFeature(new ComputeFloat(0.94f)),
                Specular = new MaterialMetalnessMapFeature(new ComputeFloat(0f)),
                SpecularModel = new MaterialSpecularMicrofacetModelFeature { Environment = new MaterialSpecularMicrofacetEnvironmentGGXPolynomial() },
                // The look is the emission: the scene behind, absorbed and reflected, composed in the shader.
                Emissive = new MaterialEmissiveMapFeature(new ComputeShaderClassColor { MixinReference = "WaterSurfaceColor" }) { Intensity = new ComputeFloat(1f), UseAlpha = false },
                Transparency = new MaterialTransparencyBlendFeature { Alpha = new ComputeFloat(1f) },
            },
        };
        var material = Material.New(device, descriptor);
        // Seen from above and from below.
        foreach (var pass in material.Passes)
            pass.CullMode = CullMode.None;
        material.Passes[0].Parameters.Set(WaterSurfaceBaseKeys.WaterSimulated, simulated ? 1f : 0f);
        return material;
    }

    /// <summary>Follows the domain, binds the step's textures, and decides whether the camera is under water.</summary>
    public void Update(Vector3 camera, float seconds)
    {
        time += seconds;
        var half = WaterSim.Extent * 0.5f;
        domain.Transform.Position = new Vector3(sim.Origin.X + half, 0f, sim.Origin.Z + half);
        var centre = new Vector3(sim.Origin.X + half, WaterSim.SeaLevel, sim.Origin.Z + half);
        var offsets = new[] { new Vector3(0f, 0f, 1f), new Vector3(0f, 0f, -1f), new Vector3(1f, 0f, 0f), new Vector3(-1f, 0f, 0f) };
        const float reach = 12000f;
        var side = (reach - WaterSim.Extent) * 0.5f;
        for (int i = 0; i < far.Count; i++)
            far[i].Transform.Position = centre + offsets[i] * (half + side * 0.5f);

        foreach (var m in new[] { material, farMaterial })
        {
            var p = m.Passes[0].Parameters;
            p.Set(WaterSurfaceBaseKeys.WaterHeight, sim.Height);
            p.Set(WaterSurfaceBaseKeys.WaterBed, sim.Bed);
            p.Set(WaterSurfaceBaseKeys.WaterVelocity, sim.Velocity);
            p.Set(WaterSurfaceBaseKeys.WaterOriginXZ, new Vector2(sim.Origin.X, sim.Origin.Z));
            p.Set(WaterSurfaceBaseKeys.WaterExtent, WaterSim.Extent);
            p.Set(WaterSurfaceBaseKeys.WaterCell, WaterSim.Cell);
            p.Set(WaterSurfaceBaseKeys.WaterSeaLevel, WaterSim.SeaLevel);
            p.Set(WaterSurfaceBaseKeys.WaterTime, time);
        }

        // Under the water when the column's surface is above the camera: the sea's, or the domain's
        // water over the ground the camera stands above, which the height field's own bed decides.
        Underwater = sim.SurfaceAt(camera) is { } surface && camera.Y < surface;
        // The tint from under the surface: the post-process vignette, over the whole frame.
        if (underwater is not null)
            underwater.Enabled = Underwater;
        // The ground seen through the water: absorbed over the length of the ray, in the terrain's own material.
        VoxelTerrainScene.TerrainMaterial?.Passes[0].Parameters.Set(TerrainSurfaceColorKeys.TerrainUnderwaterAbsorb, Underwater ? 0.35f : 0f);
    }
}
