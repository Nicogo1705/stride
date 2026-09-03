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
    /// <para>
    /// These are the keys the shaders declare with <c>[Link("VoxelGridField.X")]</c> - without the
    /// <c>Keys</c> suffix, which the engine strips from a key class's name when it names the key,
    /// the way <c>MaterialKeys.DiffuseMap</c> is <c>Material.DiffuseMap</c>. The names here are the
    /// contract: a key is found by that string, so a mismatch on either side binds nothing, silently
    /// - the effect asks for one name, the material holds another, and every read comes back zero.
    /// </para>
    /// <para>
    /// Fixed names rather than the generated, per-shader ones because of where the shaders end up.
    /// A member left unlinked takes the path it was reached by into its parameter name - composed
    /// into an image effect that is one path, mixed into a material's surface array it is
    /// <c>layers[N].materialPixelStage</c> with an N nothing outside the generator knows - and a
    /// value set under any other name reaches nothing. Linking is how the engine's own material
    /// textures get through a composition of any depth, and it is what lets the image effect and the
    /// material share one set of keys.
    /// </para>
    /// </remarks>
    public static class VoxelGridFieldKeys
    {
        /// <summary>Samples per axis.</summary>
        public static readonly ValueParameterKey<Int3> SampleCount = ParameterKeys.NewValue<Int3>();

        /// <summary>The field as a 3D texture: density in red, albedo in the rest.</summary>
        public static readonly ObjectParameterKey<Texture> Texture = ParameterKeys.NewObject<Texture>();

        /// <summary>The field as a structured buffer of packed samples.</summary>
        public static readonly ObjectParameterKey<Buffer> Data = ParameterKeys.NewObject<Buffer>();

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

        /// <summary>
        /// Diagnostic view of the material surface. 0 shades normally; 1 paints the proxy box green
        /// where the ray met the field and red where it did not, on the box's own depth; 2 paints the
        /// traced normal at the surface's depth. Set from STRIDE_VOXEL_DEBUG when that is defined.
        /// </summary>
        public static readonly ValueParameterKey<float> Debug = ParameterKeys.NewValue<float>();
    }
}
