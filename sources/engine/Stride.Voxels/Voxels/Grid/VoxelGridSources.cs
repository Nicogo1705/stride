// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Shaders;
using GraphicsBuffer = Stride.Graphics.Buffer;

namespace Stride.Rendering.Voxels.Grid
{
    /// <summary>
    /// A grid held in a 3D texture: density in red, albedo in green, blue and alpha.
    /// </summary>
    /// <remarks>
    /// The form a generator writing straight from a compute shader tends to produce, and the one
    /// that costs least to sample. A single channel texture works too - the albedo comes back black,
    /// which a renderer can ignore in favour of a flat colour.
    /// </remarks>
    [DataContract(DefaultMemberMode = DataMemberMode.Default)]
    [Display("3D texture")]
    public class VoxelGridSourceTexture3D : IVoxelGridSource
    {
        /// <summary>The samples. Not serialized: a voxel world is produced, not authored in a scene.</summary>
        [DataMemberIgnore]
        public Texture Texture { get; set; }

        /// <summary>
        /// Samples per axis, one more than the cells per axis. Taken from the texture when left at
        /// zero, which is right unless the texture is larger than the grid it carries.
        /// </summary>
        public Int3 SampleCount { get; set; }


        public ShaderSource GetShaderSource() => new ShaderClassSource("VoxelGridSourceTexture3D");

        public void UpdateLayout(string compositionName)
        {
            // Nothing to lay out: the shader links its members to VoxelGridFieldKeys by name.
        }

        public void ApplyParameters(ParameterCollection parameters)
        {
            parameters.Set(VoxelGridFieldKeys.Texture, Texture);
            var count = SampleCount;
            if (count.X <= 0 && Texture != null)
                count = new Int3(Texture.Width, Texture.Height, Texture.Depth);
            parameters.Set(VoxelGridFieldKeys.SampleCount, count);
        }
    }

    /// <summary>
    /// A grid held in a structured buffer, one value per sample, density in bits 0-7 and material in
    /// bits 8-15, laid out x-major with z varying fastest.
    /// </summary>
    /// <remarks>
    /// The packing a voxel game commonly keeps its chunks in, where uploading is a widen from 16 to
    /// 32 bits and nothing else. Material becomes a colour through a hash, which reads well enough
    /// to see the world; a game with real materials overrides <c>Albedo</c> in a source of its own
    /// rather than bending its data to fit one imposed here.
    /// </remarks>
    [DataContract(DefaultMemberMode = DataMemberMode.Default)]
    [Display("Packed buffer")]
    public class VoxelGridSourcePackedBuffer : IVoxelGridSource
    {
        /// <summary>The samples, one uint each. Not serialized; a voxel world is produced, not authored.</summary>
        [DataMemberIgnore]
        public GraphicsBuffer Data { get; set; }

        /// <summary>Samples per axis, one more than the cells per axis.</summary>
        public Int3 SampleCount { get; set; }


        public ShaderSource GetShaderSource() => new ShaderClassSource("VoxelGridSourcePackedBuffer");

        public void UpdateLayout(string compositionName)
        {
            // Nothing to lay out: the shader links its members to VoxelGridFieldKeys by name.
        }

        public void ApplyParameters(ParameterCollection parameters)
        {
            parameters.Set(VoxelGridFieldKeys.Data, Data);
            parameters.Set(VoxelGridFieldKeys.SampleCount, SampleCount);
        }
    }
}
