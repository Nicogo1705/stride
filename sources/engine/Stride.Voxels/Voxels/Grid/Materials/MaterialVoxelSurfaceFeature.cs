// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core;
using Stride.Core.Annotations;
using Stride.Rendering.Materials;
using Stride.Shaders;

namespace Stride.Rendering.Voxels.Grid
{
    /// <summary>
    /// Makes a material walk a voxel grid in the shadow caster passes, and draw nothing elsewhere.
    /// </summary>
    /// <remarks>
    /// The camera's view of a grid is resolved once by <see cref="VoxelGridResolveRenderer"/> and
    /// drawn by the grid's own materials; the shadow maps are other views, one per cascade or
    /// face, and each has to find the field's surface from the light. This feature is what the
    /// grid's shadow model carries: in a caster pass it asks the traversal only where the ray meets
    /// the field, and in every other pass it discards.
    /// </remarks>
    [DataContract("MaterialVoxelSurfaceFeature")]
    [Display("Voxel Grid Shadow")]
    public class MaterialVoxelSurfaceFeature : MaterialFeature, IMaterialSurfaceFeature
    {
        /// <summary>How the ray finds the surface, and where the samples come from.</summary>
        [DataMemberIgnore]
        public IVoxelGridTraversal Traversal { get; set; }

        /// <summary>
        /// How far along a ray the surface is still looked for, in grid local units. Zero lets the
        /// traversal run to the far side of the grid.
        /// </summary>
        /// <userdoc>How far a ray looks for the surface, in the grid's own units. Leave at zero to reach the far side of the grid.</userdoc>
        [DataMember(10)]
        [DataMemberRange(0.0, 3)]
        [Display("Max Distance")]
        public float MaxDistance { get; set; }

        public override void GenerateShader(MaterialGeneratorContext context)
        {
            if (Traversal?.Source == null)
                return;

            // Mixed, not composed: the surface shader, the traversal and its source share one scope,
            // so the field's resource is declared once and bound the way any material texture is.
            var mixin = new ShaderMixinSource();
            mixin.Mixins.Add(new ShaderClassSource("MaterialSurfaceVoxelGrid"));
            if (Traversal.GetShaderSource() is ShaderMixinSource traversal)
                foreach (var part in traversal.Mixins)
                    mixin.Mixins.Add(part);

            context.AddShaderSource(MaterialShaderStage.Pixel, mixin);

            // The vertex half defines the pass stream the pixel half reads, in every pass.
            context.AddShaderSource(MaterialShaderStage.Vertex, new ShaderClassSource("MaterialSurfaceVoxelGridVertex"));

            // The caster passes rasterise with the vertex stage alone unless told otherwise, and
            // for this material the mesh is the proxy box: the shadow would be a cube's.
            context.Parameters.Set(MaterialKeys.UsePixelShaderWithDepthPass, true);
            ApplyParameters(context.Parameters);
        }

        /// <summary>
        /// Writes the field's parameters under the fixed names the shaders link to.
        /// </summary>
        /// <remarks>
        /// Called at generation and then every frame on the material pass, which is the collection
        /// the mesh render feature copies from; a value that is not there is not bound.
        /// </remarks>
        public void ApplyParameters(ParameterCollection parameters)
        {
            if (Traversal?.Source == null)
                return;

            Traversal.ApplyParameters(parameters);
            parameters.Set(VoxelGridFieldKeys.MaxDistance, MaxDistance);
        }
    }
}
