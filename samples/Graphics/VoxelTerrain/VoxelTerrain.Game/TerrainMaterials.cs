// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;

namespace VoxelTerrain;

/// <summary>
/// The terrain's material: one ordinary material for every id of a ring, whose colour and gloss
/// come from the resolved material id in TerrainSurface.sdsl. Drawing every id in one pass
/// matters: each material of a grid is a full-screen pass over the proxy box, in the depth
/// prepass, the opaque pass and the voxelization passes alike.
/// </summary>
public static class TerrainMaterials
{
    public const byte Air = 0, Grass = 1, Dirt = 2, Rock = 3, Sand = 4, GrassDeep = 5, GrassDry = 6, RockPale = 7, RockDark = 8, Water = 9, Snow = 10;

    public static Material Build(GraphicsDevice device)
    {
        var descriptor = new MaterialDescriptor
        {
            Attributes =
            {
                Diffuse = new MaterialDiffuseMapFeature(new ComputeShaderClassColor { MixinReference = "TerrainSurfaceColor" }),
                DiffuseModel = new MaterialDiffuseLambertModelFeature(),
                MicroSurface = new MaterialGlossinessMapFeature(new ComputeShaderClassScalar { MixinReference = "TerrainSurfaceGloss" }),
                Specular = new MaterialMetalnessMapFeature(new ComputeFloat(0f)),
                // The polynomial environment term: the default lookup table is a texture nothing
                // supplies to a material built at runtime, and metal then reads as black.
                SpecularModel = new MaterialSpecularMicrofacetModelFeature { Environment = new MaterialSpecularMicrofacetEnvironmentGGXPolynomial() },
            },
        };
        return Material.New(device, descriptor);
    }
}
