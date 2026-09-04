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
    }

    /// <summary>
    /// Walks every grid once per pixel and keeps the nearest surface: its normal, its material and
    /// grid, its position and depth, in three textures the grids' materials then read.
    /// </summary>
    /// <remarks>
    /// The targets are kept from frame to frame and remade when the view changes size; a material
    /// binds them by key and is told, through <see cref="TargetsVersion"/>, when to bind them
    /// again.
    /// </remarks>
    public sealed class VoxelGridResolveRenderer : ImageEffect
    {
        private readonly ImageEffectShader shader = new("VoxelGridResolveEffect");

        /// <summary>The grids to resolve this frame. Filled by whoever owns the grids before the pass draws.</summary>
        public List<VoxelGridResolveEntry> Grids { get; } = [];

        /// <summary>The surface normal, with 1 in w where a surface was found.</summary>
        public Texture Normal { get; private set; }

        /// <summary>The material id in r and the grid index in g, a byte each.</summary>
        public Texture Material { get; private set; }

        /// <summary>The surface's world position, with its depth in w.</summary>
        public Texture Position { get; private set; }

        private Texture depth;

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
            commandList.Clear(depth, DepthStencilClearOptions.DepthBuffer);
            commandList.Clear(Normal, Color4.Black);
            commandList.Clear(Material, Color4.Black);
            commandList.Clear(Position, Color4.Black);

            var viewProjection = renderView.ViewProjection;
            Matrix.Invert(ref viewProjection, out var viewProjectionInverse);

            foreach (var grid in Grids)
            {
                if (grid.Traversal?.Source == null)
                    continue;

                var world = grid.World;
                Matrix.Invert(ref world, out var worldInverse);

                grid.Traversal.ApplyParameters(shader.Parameters);
                shader.Parameters.Set(VoxelGridResolveShaderKeys.Traversal, grid.Traversal.GetShaderSource());
                shader.Parameters.Set(VoxelGridResolveShaderKeys.VoxelGridViewProjection, viewProjection);
                shader.Parameters.Set(VoxelGridResolveShaderKeys.VoxelGridViewProjectionInverse, viewProjectionInverse);
                shader.Parameters.Set(VoxelGridResolveShaderKeys.VoxelGridWorld, world);
                shader.Parameters.Set(VoxelGridResolveShaderKeys.VoxelGridWorldInverse, worldInverse);
                shader.Parameters.Set(VoxelGridResolveShaderKeys.VoxelGridMaxDistance, grid.MaxDistance);
                shader.Parameters.Set(VoxelGridResolveShaderKeys.VoxelGridDither, (int)grid.Dither);
                shader.Parameters.Set(VoxelGridResolveShaderKeys.VoxelGridIndex, grid.GridIndex);

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
            shader.Dispose();
            base.Destroy();
        }
    }

    /// <summary>
    /// The compositor's slot for the resolve: runs the renderer over the grids its owner listed,
    /// before the scene is drawn, inside the camera's renderer so it has the camera's view.
    /// </summary>
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
