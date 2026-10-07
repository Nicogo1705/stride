// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core.Mathematics;
using Stride.Graphics;

namespace Stride.Rendering.Voxels.Grid
{
    /// <summary>
    /// The parameters a voxel field is drawn with, under names the shaders link to directly.
    /// </summary>
    /// <remarks>
    /// The shaders declare these with <c>[Link("VoxelGridField.X")]</c> (the engine strips the <c>Keys</c> suffix).
    /// Linking gives fixed names whatever path a shader is reached by; an unlinked member mixed into a material's
    /// surface array would be named <c>layers[N].materialPixelStage...</c>. A name mismatch binds nothing, silently.
    /// </remarks>
    public static class VoxelGridFieldKeys
    {
        /// <summary>Samples per axis.</summary>
        public static readonly ValueParameterKey<Int3> SampleCount = ParameterKeys.NewValue<Int3>();

        public static readonly ValueParameterKey<int> LodLevels = ParameterKeys.NewValue<int>();

        /// <summary>The field as an R8G8 3D texture: density in red, material id in green.</summary>
        public static readonly ObjectParameterKey<Texture> Texture = ParameterKeys.NewObject<Texture>();

        /// <summary>The field as a structured buffer of packed samples.</summary>
        public static readonly ObjectParameterKey<Buffer> Data = ParameterKeys.NewObject<Buffer>();

        /// <summary>The min/max pyramid over the field, see <see cref="VoxelGridOccupancy"/>.</summary>
        public static readonly ObjectParameterKey<Texture> Occupancy = ParameterKeys.NewObject<Texture>();

        /// <summary>Levels in the pyramid; 0 when there is none and every cell is walked.</summary>
        public static readonly ValueParameterKey<int> OccupancyLevels = ParameterKeys.NewValue<int>();

        /// <summary>Edge length of one cell, in grid local units.</summary>
        public static readonly ValueParameterKey<float> CellSize = ParameterKeys.NewValue<float>();

        /// <summary>Density at or above which a sample is solid.</summary>
        public static readonly ValueParameterKey<float> IsoLevel = ParameterKeys.NewValue<float>();

        /// <summary>1 to read the grid's outer samples as air.</summary>
        public static readonly ValueParameterKey<float> SealBorder = ParameterKeys.NewValue<float>();

        /// <summary>Ceiling on the cells one ray may visit.</summary>
        public static readonly ValueParameterKey<int> MaxSteps = ParameterKeys.NewValue<int>();

        /// <summary>How far along a ray the surface is looked for, in grid local units.</summary>
        public static readonly ValueParameterKey<float> MaxDistance = ParameterKeys.NewValue<float>();

        /// <summary>The resolve pass's normal target: the surface normal, with 1 in w where a surface was found.</summary>
        public static readonly ObjectParameterKey<Texture> ResolveNormal = ParameterKeys.NewObject<Texture>();

        /// <summary>The resolve pass's material target: the material id in r and the grid index in g, a byte each.</summary>
        public static readonly ObjectParameterKey<Texture> ResolveMaterial = ParameterKeys.NewObject<Texture>();

        /// <summary>The resolve pass's position target: the surface's world position, with its depth in w.</summary>
        public static readonly ObjectParameterKey<Texture> ResolvePosition = ParameterKeys.NewObject<Texture>();

        /// <summary>The material id a wrapped material draws, or -1 for every id of its grid.</summary>
        public static readonly ValueParameterKey<int> MaterialId = ParameterKeys.NewValue<int>();

        /// <summary>1 when the field reaches the GI straight from its samples rather than through the voxelizer.</summary>
        public static readonly ValueParameterKey<float> Injected = ParameterKeys.NewValue<float>();

        /// <summary>Which grid a wrapped material belongs to.</summary>
        public static readonly ValueParameterKey<int> GridIndex = ParameterKeys.NewValue<int>();

        /// <summary>Diagnostic view: 0 draws normally; 1 paints the proxy box green where the pixel is the material's and red where not; 2 paints the resolved normal.</summary>
        public static readonly ValueParameterKey<float> Debug = ParameterKeys.NewValue<float>();
    }
}
