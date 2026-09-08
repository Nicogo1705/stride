// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Collections.Generic;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering.Compositing;
using Stride.Rendering.Images;

namespace Stride.Rendering.Voxels.Grid
{
    /// <summary>One grid to resolve, as the pass sees it.</summary>
    public struct VoxelGridResolveEntry
    {
        /// <summary>The traversal that finds the surface.</summary>
        public IVoxelGridTraversal Traversal;
        /// <summary>The grid's world matrix.</summary>
        public Matrix World;
        /// <summary>How far a ray is walked, in the grid's own units.</summary>
        public float MaxDistance;
        /// <summary>The pattern that decides a pixel on a material boundary.</summary>
        public VoxelMaterialDither Dither;
        /// <summary>The byte that identifies this grid in the targets.</summary>
        public int GridIndex;
        /// <summary>Level-of-detail bias in levels; positive is coarser. NaN turns the level of detail off.</summary>
        public float LodBias;
        /// <summary>Pixels along a side of the block one beam ray walks ahead of the resolve; 0 walks every pixel from the box.</summary>
        public int BeamBlockSize;
    }

    /// <summary>
    /// Walks every grid once per pixel and keeps the nearest surface: its normal, its material and
    /// grid, its position and depth, in three textures the grids' materials then read.
    /// </summary>
    /// <remarks>The targets are kept across frames and remade when the view changes size; <see cref="TargetsVersion"/> tells a material when to bind them again.</remarks>
    public sealed class VoxelGridResolveRenderer : ImageEffect
    {
        private readonly ImageEffectShader shader = new("VoxelGridResolveEffect");
        private readonly ImageEffectShader beamShader = new("VoxelGridBeamEffect");

        /// <summary>The grids to resolve this frame. Filled by whoever owns the grids before the pass draws.</summary>
        public List<VoxelGridResolveEntry> Grids { get; } = [];

        /// <summary>The surface normal, with 1 in w where a surface was found.</summary>
        public Texture Normal { get; private set; }

        /// <summary>The material id in r and the grid index in g, a byte each.</summary>
        public Texture Material { get; private set; }

        /// <summary>The surface's world position, with its depth in w.</summary>
        public Texture Position { get; private set; }

        private Texture depth;
        private Texture beam;

        /// <summary>Counts the times the targets were remade; a reader binds them again when it changes.</summary>
        public int TargetsVersion { get; private set; }

        protected override void InitializeCore()
        {
            base.InitializeCore();
            shader.DepthStencilState = DepthStencilStates.Default;
        }

        protected override void DrawCore(RenderDrawContext context)
        {
            var renderView = context.RenderContext.RenderView;
            if (renderView == null)
                return;

            var width = (int)renderView.ViewSize.X;
            var height = (int)renderView.ViewSize.Y;
            if (width <= 0 || height <= 0)
                return;

            EnsureTargets(width, height);

            // The pass borrows the command list's targets and hands them back: what runs next
            // clears and draws into whatever is bound, and must find its own.
            using var restore = context.PushRenderTargetsAndRestore();

            // Nothing to resolve, nothing to clear: no material reads the targets while no grid
            // lists itself, and the frame they are read again they are written first.
            if (Grids.Count == 0)
                return;

            var commandList = context.CommandList;
            // Zero in every channel: the normal's w is what says "resolved", and Color4.Black carries a 1 there.
            commandList.Clear(depth, DepthStencilClearOptions.DepthBuffer);
            commandList.Clear(Normal, new Color4(0, 0, 0, 0));
            commandList.Clear(Material, new Color4(0, 0, 0, 0));
            commandList.Clear(Position, new Color4(0, 0, 0, 0));

            var viewProjection = renderView.ViewProjection;
            Matrix.Invert(ref viewProjection, out var viewProjectionInverse);

            foreach (var grid in Grids)
            {
                if (grid.Traversal?.Source == null)
                    continue;

                var world = grid.World;
                Matrix.Invert(ref world, out var worldInverse);

                var viewSize = new Vector2(width, height);
                var block = grid.BeamBlockSize;
                if (block > 0)
                {
                    // One conservative ray per block, into a texture a block-th the view's size, before the pixels walk.
                    EnsureBeam((width + block - 1) / block, (height + block - 1) / block);
                    grid.Traversal.ApplyParameters(beamShader.Parameters);
                    beamShader.Parameters.Set(VoxelGridBeamShaderKeys.Traversal, grid.Traversal.GetShaderSource());
                    beamShader.Parameters.Set(VoxelGridRayKeys.VoxelGridViewProjectionInverse, viewProjectionInverse);
                    beamShader.Parameters.Set(VoxelGridRayKeys.VoxelGridWorldInverse, worldInverse);
                    beamShader.Parameters.Set(VoxelGridRayKeys.VoxelGridViewSize, viewSize);
                    beamShader.Parameters.Set(VoxelGridBeamShaderKeys.VoxelGridBeamBlock, block);
                    beamShader.SetOutput(beam);
                    beamShader.Draw(context, name: "VoxelGridBeam");
                }

                grid.Traversal.ApplyParameters(shader.Parameters);
                shader.Parameters.Set(VoxelGridResolveShaderKeys.Traversal, grid.Traversal.GetShaderSource());
                shader.Parameters.Set(VoxelGridResolveShaderKeys.VoxelGridViewProjection, viewProjection);
                shader.Parameters.Set(VoxelGridRayKeys.VoxelGridViewProjectionInverse, viewProjectionInverse);
                shader.Parameters.Set(VoxelGridResolveShaderKeys.VoxelGridWorld, world);
                shader.Parameters.Set(VoxelGridRayKeys.VoxelGridWorldInverse, worldInverse);
                shader.Parameters.Set(VoxelGridRayKeys.VoxelGridViewSize, viewSize);
                shader.Parameters.Set(VoxelGridResolveShaderKeys.VoxelGridBeam, block > 0 ? beam : null);
                shader.Parameters.Set(VoxelGridResolveShaderKeys.VoxelGridBeamBlock, block);
                shader.Parameters.Set(VoxelGridResolveShaderKeys.VoxelGridMaxDistance, grid.MaxDistance);
                shader.Parameters.Set(VoxelGridResolveShaderKeys.VoxelGridDither, (int)grid.Dither);
                shader.Parameters.Set(VoxelGridResolveShaderKeys.VoxelGridIndex, grid.GridIndex);

                // How many of the grid's units one pixel spans per unit of distance: the pixel's
                // angle, times how the world's unit reads in the grid's own.
                var projection = renderView.Projection;
                var pixelAngle = projection.M22 != 0 ? 2.0f / (System.Math.Abs(projection.M22) * height) : 0f;
                var localScale = new Vector3(worldInverse.M11, worldInverse.M12, worldInverse.M13).Length();
                var lodOn = !float.IsNaN(grid.LodBias);
                shader.Parameters.Set(VoxelGridResolveShaderKeys.VoxelGridLodPixel, lodOn ? pixelAngle * localScale : 0f);
                shader.Parameters.Set(VoxelGridResolveShaderKeys.VoxelGridLodBias, lodOn ? grid.LodBias : 0f);

                // Depth tested against the grids already resolved, so the nearest surface wins.
                shader.SetDepthOutput(depth, Normal, Material, Position);
                shader.Draw(context, name: "VoxelGridResolve");
            }
        }

        private void EnsureTargets(int width, int height)
        {
            if (Normal != null && Normal.Width == width && Normal.Height == height)
                return;

            ReleaseTargets();
            var device = GraphicsDevice;
            Normal = Texture.New2D(device, width, height, PixelFormat.R16G16B16A16_Float, TextureFlags.ShaderResource | TextureFlags.RenderTarget);
            Material = Texture.New2D(device, width, height, PixelFormat.R8G8_UNorm, TextureFlags.ShaderResource | TextureFlags.RenderTarget);
            Position = Texture.New2D(device, width, height, PixelFormat.R32G32B32A32_Float, TextureFlags.ShaderResource | TextureFlags.RenderTarget);
            depth = Texture.New2D(device, width, height, PixelFormat.D32_Float, TextureFlags.DepthStencil);
            TargetsVersion++;
        }

        private void EnsureBeam(int width, int height)
        {
            if (beam != null && beam.Width == width && beam.Height == height)
                return;

            beam?.Dispose();
            beam = Texture.New2D(GraphicsDevice, width, height, PixelFormat.R32_Float, TextureFlags.ShaderResource | TextureFlags.RenderTarget);
        }

        private void ReleaseTargets()
        {
            Normal?.Dispose();
            Material?.Dispose();
            Position?.Dispose();
            depth?.Dispose();
            Normal = Material = Position = depth = null;
        }

        protected override void Destroy()
        {
            ReleaseTargets();
            beam?.Dispose();
            beam = null;
            shader.Dispose();
            beamShader.Dispose();
            base.Destroy();
        }
    }

    /// <summary>The compositor's slot for the resolve: runs the renderer over the listed grids before the scene is drawn, inside the camera's renderer so it has its view.</summary>
    public sealed class VoxelGridResolvePass : SceneRendererBase
    {
        /// <summary>The renderer this pass drives; grids register with it each frame.</summary>
        [Stride.Core.DataMemberIgnore]
        public VoxelGridResolveRenderer Renderer { get; } = new();

        protected override void DrawCore(RenderContext context, RenderDrawContext drawContext)
        {
            Renderer.Draw(drawContext);
        }

        protected override void Destroy()
        {
            Renderer.Dispose();
            base.Destroy();
        }
    }
}
