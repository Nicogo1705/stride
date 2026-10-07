// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Shaders;

namespace Stride.Rendering.Voxels.Grid
{
    public partial class VoxelGridInjectShaderKeys
    {
        public static readonly PermutationParameterKey<ShaderSource> Traversal = ParameterKeys.NewPermutation<ShaderSource>();
    }
}
