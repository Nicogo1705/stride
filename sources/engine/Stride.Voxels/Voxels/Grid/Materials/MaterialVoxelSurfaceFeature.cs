// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core;
using Stride.Core.Annotations;
using Stride.Rendering.Materials;
using Stride.Shaders;

namespace Stride.Rendering.Voxels.Grid
{
    /// <summary>
    /// Makes a material resolve its surface from a voxel grid instead of from the mesh it is drawn
    /// on, so a voxel body goes down the path an ordinary model goes down.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The model this belongs on is a box the size of the grid, and it is the box the renderer
    /// culls, sorts, transforms and rasterises. This feature runs at the front of its material and
    /// replaces the box's surface with the field's - the point, the normal and the depth the
    /// rasteriser keeps - so everything after it is the mesh path untouched: the material's own
    /// diffuse, gloss and metalness features, the lights and shadow maps of the forward renderer,
    /// the depth test against ordinary geometry, and the effects that read the depth buffer
    /// afterwards.
    /// </para>
    /// <para>
    /// Which is the point. A pass of its own would have to reimplement each of those, and each
    /// reimplementation drifts from what a mesh does, in a way a user notices as "the voxels look
    /// wrong" long before anyone can say which of the ten things is missing.
    /// </para>
    /// <para>
    /// The field's colour is left in <c>matColorBase</c> for the diffuse slot to read through
    /// <c>ComputeColorVoxelAlbedo</c>; a material that puts a texture in that slot instead simply
    /// does not consult the palette.
    /// </para>
    /// </remarks>
    [DataContract("MaterialVoxelSurfaceFeature")]
    [Display("Voxel Grid Surface")]
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

        /// <summary>See <see cref="VoxelGridFieldKeys.Debug"/>.</summary>
        [DataMemberIgnore]
        public float Debug { get; set; }

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

            // The depth-only passes - Z prepass and shadow maps - rasterise the mesh with the vertex
            // stage alone unless told otherwise, and for this material the mesh is the proxy box: the
            // prepass would hold the box's depth and the real surface behind it would fail the depth
            // test, and the shadow would be a cube's. The same switch alpha cutoff uses.
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
            parameters.Set(VoxelGridFieldKeys.Debug, Debug);
        }
    }
}
