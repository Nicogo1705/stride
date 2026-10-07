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
    /// Readers only ask how solid a sample is and which material it carries, so the packing is a
    /// composition: a game with its own layout implements this in a few lines of SDSL instead of
    /// converting its data. The CPU counterpart is Stride.BepuPhysics' <c>IVoxelDensitySource</c>.
    /// </remarks>
    public interface IVoxelGridSource
    {
        /// <summary>Samples along each axis.</summary>
        Int3 SampleCount { get; }

        /// <summary>The shader implementing <c>IVoxelGridSource</c>, to be mixed in beside a traversal.</summary>
        ShaderSource GetShaderSource();

        /// <summary>Writes this source's parameters, under <see cref="VoxelGridFieldKeys"/>, into a collection.</summary>
        void ApplyParameters(ParameterCollection parameters);
    }
}
