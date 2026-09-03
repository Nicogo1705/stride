// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core.Mathematics;
using Stride.Shaders;

namespace Stride.Rendering.Voxels.Grid
{
    /// <summary>
    /// Where a voxel grid gets its samples, and how they are packed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every voxel game packs its samples differently - a byte of density, a float, a density byte
    /// and a material byte in one value, two parallel arrays - and none of that concerns the things
    /// that read the grid, which only ask how solid a sample is and what colour it carries. So the
    /// packing is a composition: the two implementations here are examples, not a closed set, and a
    /// game with its own layout writes a dozen lines of SDSL rather than converting its data.
    /// </para>
    /// <para>
    /// The mirror of this seam on the CPU is Stride.BepuPhysics' <c>IVoxelDensitySource</c>. Same
    /// idea for the same reason: describe a packing once, and let collision, meshing, tracing and
    /// lighting all read it.
    /// </para>
    /// </remarks>
    public interface IVoxelGridSource
    {
        /// <summary>Samples along each axis.</summary>
        /// <remarks>
        /// On the interface because it is the field's extent rather than a detail of how the samples
        /// are packed: whatever bounds, draws or collides with the grid needs it, and every packing
        /// has one. Both implementations already carried it.
        /// </remarks>
        Int3 SampleCount { get; }

        /// <summary>The shader implementing <c>IVoxelGridSource</c>, to be mixed in beside a traversal.</summary>
        ShaderSource GetShaderSource();

        /// <summary>Writes this source's parameters, under <see cref="VoxelGridFieldKeys"/>, into a collection.</summary>
        void ApplyParameters(ParameterCollection parameters);
    }
}
