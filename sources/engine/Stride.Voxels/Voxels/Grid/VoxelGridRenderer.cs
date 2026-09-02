// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering.Images;

namespace Stride.Rendering.Voxels.Grid
{
    /// <summary>
    /// Draws a voxel grid by tracing one ray per pixel, writing colour and depth.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Writing depth is what makes this a pass rather than an overlay. The grid takes its place in
    /// the depth buffer and the rest of the scene rasterizes against it as it would against any
    /// other geometry, so nothing downstream has to know the terrain was never triangles.
    /// </para>
    /// <para>
    /// This is the half of the voxel pipeline that does not go through the rasterizer. A game that
    /// is already voxels hands its chunk over as it stores it - see <see cref="IVoxelGridSource"/> -
    /// instead of drawing triangles for a voxelizer to turn back into voxels it already had.
    /// </para>
    /// <para>
    /// Usage: set <see cref="Traversal"/> (and its source), point <see cref="World"/> at where the
    /// grid sits, then <c>SetDepthOutput(depthBuffer, colorTarget)</c> and <c>Draw</c> from a
    /// compositor stage. Pixels the ray misses are discarded, leaving both targets untouched, so the
    /// pass can run before or after anything else.
    /// </para>
    /// </remarks>
    [DataContract("VoxelGridRenderer")]
    public class VoxelGridRenderer : ImageEffect
    {
        private readonly ImageEffectShader shader = new ImageEffectShader("VoxelGridRenderEffect");

        /// <summary>How rays find the surface, and where the samples come from.</summary>
        public IVoxelGridTraversal Traversal { get; set; } = new VoxelGridTraversalDDA();

        /// <summary>
        /// Where the grid sits. Its local origin is the grid's minimum corner, and it spans
        /// cells * cellSize along each axis from there.
        /// </summary>
        public Matrix World { get; set; } = Matrix.Identity;

        /// <summary>How far a ray may travel, in world units, before giving up.</summary>
        public float MaxDistance { get; set; } = 1000.0f;

        /// <summary>
        /// What the pass draws instead of the grid: 0 the grid, 1 the ray direction, 2 the ray
        /// origin in grid space, 3 origin plus direction.
        /// </summary>
        /// <remarks>
        /// A pass producing nothing looks identical whether the rays are wrong, the composition is
        /// empty or the target is not the one on screen. These separate those in one run each.
        /// </remarks>
        public int DebugMode { get; set; }

        /// <summary>Extent of the grid in its own space, needed only by the box debug view.</summary>
        public Vector3 DebugBounds { get; set; }

        /// <summary>Direction the key light travels.</summary>
        public Vector3 LightDirection { get; set; } = Vector3.Normalize(new Vector3(-0.5f, -1.0f, -0.3f));

        /// <summary>Colour of the key light.</summary>
        public Color3 LightColor { get; set; } = new Color3(1.0f, 0.97f, 0.9f);

        /// <summary>Flat fill so faces turned away from the light are not black.</summary>
        public Color3 AmbientColor { get; set; } = new Color3(0.25f, 0.28f, 0.35f);

        protected override void InitializeCore()
        {
            base.InitializeCore();

            // Writes depth, and is never rejected by what the depth buffer already held. A pass that
            // establishes primary visibility has nothing to be occluded by: run before the scene it
            // gives later geometry something to test against, and run after it, the comparison would
            // be against a buffer holding whatever the previous pass left - which silently discards
            // every pixel and costs nothing, so it looks like the pass never ran.
            shader.DepthStencilState = new DepthStencilStateDescription(true, true)
            {
                DepthBufferFunction = CompareFunction.Always,
            };
        }

        protected override void DrawCore(RenderDrawContext context)
        {
            if (Traversal?.Source == null)
                return;

            var renderView = context.RenderContext.RenderView;
            if (renderView == null)
                return;

            Traversal.UpdateLayout("Traversal");
            Traversal.ApplyParameters(shader.Parameters);
            shader.Parameters.Set(VoxelGridRenderShaderKeys.Traversal, Traversal.GetShaderSource());

            var viewProjection = renderView.ViewProjection;
            Matrix.Invert(ref viewProjection, out var viewProjectionInverse);
            var world = World;
            Matrix.Invert(ref world, out var worldInverse);
            var view = renderView.View;
            Matrix.Invert(ref view, out var viewInverse);

            shader.Parameters.Set(VoxelGridRenderShaderKeys.VoxelGridViewProjection, viewProjection);
            shader.Parameters.Set(VoxelGridRenderShaderKeys.VoxelGridViewProjectionInverse, viewProjectionInverse);
            shader.Parameters.Set(VoxelGridRenderShaderKeys.VoxelGridWorld, world);
            shader.Parameters.Set(VoxelGridRenderShaderKeys.VoxelGridWorldInverse, worldInverse);
            shader.Parameters.Set(VoxelGridRenderShaderKeys.VoxelGridEyePosition, viewInverse.TranslationVector);
            shader.Parameters.Set(VoxelGridRenderShaderKeys.VoxelGridMaxDistance, MaxDistance);
            shader.Parameters.Set(VoxelGridRenderShaderKeys.VoxelGridDebugMode, DebugMode);
            shader.Parameters.Set(VoxelGridRenderShaderKeys.VoxelGridDebugBounds, DebugBounds);
            shader.Parameters.Set(VoxelGridRenderShaderKeys.VoxelGridLightDirection, Vector3.Normalize(LightDirection));
            shader.Parameters.Set(VoxelGridRenderShaderKeys.VoxelGridLightColor, LightColor.ToVector3());
            shader.Parameters.Set(VoxelGridRenderShaderKeys.VoxelGridAmbientColor, AmbientColor.ToVector3());

            // The depth surface this effect was given is the one the grid has to land in, so it is
            // passed through rather than letting the shader allocate an output of its own.
            if (DepthStencil != null)
                shader.SetDepthOutput(DepthStencil, GetSafeOutput(0));
            else
                shader.SetOutput(GetSafeOutput(0));

            shader.Draw(context, name: "VoxelGridRender");
        }

        protected override void Destroy()
        {
            shader.Dispose();
            base.Destroy();
        }
    }
}
