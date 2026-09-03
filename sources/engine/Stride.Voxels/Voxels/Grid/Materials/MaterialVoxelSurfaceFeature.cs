// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core;
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
    /// Order matters, as it does for any material feature: added first, the field's own palette is
    /// the base colour and a diffuse feature after it overrides that, which is how a user replaces
    /// the palette with a map of their own.
    /// </para>
    /// </remarks>
    [DataContract("MaterialVoxelSurfaceFeature")]
    [Display("Voxel Grid Surface")]
    public class MaterialVoxelSurfaceFeature : MaterialFeature, IMaterialSurfaceFeature
    {
        /// <summary>
        /// The traversal is mixed into this feature's shader, not composed into it, so its members
        /// keep the names they were declared with and its parameters take no path.
        /// </summary>
        private const string NoComposition = "";

        /// <summary>How the ray finds the surface, and where the samples come from.</summary>
        [DataMemberIgnore]
        public IVoxelGridTraversal Traversal { get; set; }

        /// <summary>
        /// How far along a ray the surface is still looked for, in grid local units. Zero lets the
        /// traversal run to the far side of the grid.
        /// </summary>
        [DataMember(10)]
        [Display("Max Distance")]
        public float MaxDistance { get; set; }

        /// <summary>See <see cref="VoxelGridFieldKeys.Debug"/>. Taken from STRIDE_VOXEL_DEBUG when set.</summary>
        [DataMemberIgnore]
        public float Debug { get; set; } = DebugFromEnvironment();

        private static float DebugFromEnvironment()
            => float.TryParse(System.Environment.GetEnvironmentVariable("STRIDE_VOXEL_DEBUG"), out var mode) ? mode : 0f;

        public override void GenerateShader(MaterialGeneratorContext context)
        {
            if (Traversal?.Source == null)
                return;

            // The traversal and its source are mixed in beside the surface shader rather than
            // composed into it, so all three share one scope. That is what lets the field's texture
            // be declared once and read once, and bind the way any material texture binds; composed,
            // each scope would hold its own copy of the declaration and the texture would bind to a
            // variable nothing reads.
            var mixin = new ShaderMixinSource();
            mixin.Mixins.Add(new ShaderClassSource("MaterialSurfaceVoxelGrid"));
            if (Traversal.GetShaderSource() is ShaderMixinSource traversal)
                foreach (var part in traversal.Mixins)
                    mixin.Mixins.Add(part);


            // Set here, at generation time, and not afterwards on the material's own parameters.
            //
            // The generator gives every member of a surface feature a path of its own - the compiled
            // effect asks for VoxelGridTraversalDDA.VoxelGridCellSize.layers[1].materialPixelStage,
            // not the bare name - and that index is not something this feature can know. Setting the
            // bare key later therefore binds nothing at all, silently: the shader runs against a
            // field of zero dimensions and finds no surface anywhere. Set on the context, the path
            // is applied for us.
            //
            // Nothing is lost by only doing it once. What changes when a game digs is the contents
            // of the texture, not which texture is bound nor how big the grid is.
            context.AddShaderSource(MaterialShaderStage.Pixel, mixin);

            // The pixel shader has to run in the depth-only passes too - the Z prepass and the shadow
            // maps - and by default it does not: those passes rasterise the mesh with the vertex
            // stage alone, which for this material means the box. The prepass then holds the depth
            // of the box's faces, the real surface behind them fails the depth test in the main
            // pass, and nothing shows but a hole the shape of the box that hides whatever stands in
            // it. The shadow map likewise casts the shadow of a box. This is the switch alpha cutoff
            // uses for the same reason: what the fragment keeps is decided in the pixel stage.
            context.Parameters.Set(MaterialKeys.UsePixelShaderWithDepthPass, true);
            ApplyParameters(context.Parameters);
        }

        /// <summary>
        /// Writes the field's parameters into the material pass, at the composition path the
        /// generated shader put the traversal under.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="GenerateShader"/> and called every frame, because the samples,
        /// the grid's size and its transform change while the shader does not - a game that digs a
        /// hole must not recompile a material to show it.
        /// </remarks>
        public void ApplyParameters(ParameterCollection parameters)
        {
            if (Traversal?.Source == null)
                return;

            Traversal.UpdateLayout(NoComposition);
            Traversal.ApplyParameters(parameters);
            parameters.Set(VoxelGridFieldKeys.MaxDistance, MaxDistance);
            parameters.Set(VoxelGridFieldKeys.Debug, Debug);
        }
    }
}
