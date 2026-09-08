// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering.Compositing;

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
        /// <summary>The grid's proxy box, in its own space, whose pixels are the ones that march.</summary>
        public MeshDraw Box;
    }

    /// <summary>
    /// Walks every grid once per pixel and keeps the nearest surface: its normal, its material and
    /// grid, its position and depth, in three textures the grids' materials then read.
    /// </summary>
    /// <remarks>
    /// Each grid is drawn as its proxy box, so only the pixels under it march, and the scene's depth,
    /// when given, ends a ray at the opaque surface in front of the grid. The targets are kept across
    /// frames and remade when the view changes size; <see cref="TargetsVersion"/> tells a material when to bind them again.
    /// </remarks>
    public sealed class VoxelGridResolveRenderer : IDisposable
    {
        private DynamicEffectInstance effect;
        private MutablePipelineState pipelineState;
        private Texture noSceneDepth;
        private bool cleared;

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

        /// <summary>
        /// Makes the targets fit the view and empties them, so nothing reads as resolved until <see cref="Draw"/> runs.
        /// </summary>
        /// <remarks>Call it before a depth prepass that draws the grids' materials: they read the targets and must find nothing.</remarks>
        public void Clear(RenderDrawContext context)
        {
            var renderView = context.RenderContext.RenderView;
            if (renderView == null)
                return;

            var width = (int)renderView.ViewSize.X;
            var height = (int)renderView.ViewSize.Y;
            if (width <= 0 || height <= 0)
                return;

            EnsureTargets(context.GraphicsDevice, width, height);

            var commandList = context.CommandList;
            // Zero in every channel: the normal's w is what says "resolved", and Color4.Black carries a 1 there.
            commandList.Clear(depth, DepthStencilClearOptions.DepthBuffer);
            commandList.Clear(Normal, new Color4(0, 0, 0, 0));
            commandList.Clear(Material, new Color4(0, 0, 0, 0));
            commandList.Clear(Position, new Color4(0, 0, 0, 0));
            cleared = true;
        }

        /// <summary>
        /// Resolves the listed grids into the targets.
        /// </summary>
        /// <param name="context">The draw context, with the view the grids are seen from.</param>
        /// <param name="sceneDepth">The scene's depth as a shader resource, the view's size, or null to march without it.</param>
        public void Draw(RenderDrawContext context, Texture sceneDepth)
        {
            var renderView = context.RenderContext.RenderView;
            if (renderView == null)
                return;

            if (!cleared)
                Clear(context);
            cleared = false;
            if (Normal == null || Grids.Count == 0)
                return;

            var device = context.GraphicsDevice;
            if (effect == null)
            {
                effect = new DynamicEffectInstance("VoxelGridResolveEffect");
                effect.Initialize(context.RenderContext.Services);
                pipelineState = new MutablePipelineState(device);
                pipelineState.State.SetDefaults();
                // Back faces, without the near and far clip: the box covers its pixels from inside
                // the camera or beyond the far plane alike. The depth written is the surface's own.
                pipelineState.State.RasterizerState = RasterizerStates.CullFront;
                pipelineState.State.RasterizerState.DepthClipEnable = false;
                pipelineState.State.DepthStencilState = DepthStencilStates.Default;
                pipelineState.State.BlendState = BlendStates.Opaque;
                // Bound in place of the scene's depth when there is none, so the slot is never empty.
                noSceneDepth = Texture.New2D(device, 1, 1, PixelFormat.R32_Float, TextureFlags.ShaderResource);
            }

            // The pass borrows the command list's targets and hands them back: what runs next
            // clears and draws into whatever is bound, and must find its own.
            using var restore = context.PushRenderTargetsAndRestore();

            var commandList = context.CommandList;
            commandList.ResourceBarrierTransition(depth, BarrierLayout.DepthStencilWrite);
            commandList.ResourceBarrierTransition(Normal, BarrierLayout.RenderTarget);
            commandList.ResourceBarrierTransition(Material, BarrierLayout.RenderTarget);
            commandList.ResourceBarrierTransition(Position, BarrierLayout.RenderTarget);
            if (sceneDepth != null)
                commandList.ResourceBarrierTransition(sceneDepth, BarrierLayout.ShaderResource);
            // Depth tested against the grids already resolved, so the nearest surface wins.
            commandList.SetRenderTargetsAndViewport(depth, Normal, Material, Position);

            var viewProjection = renderView.ViewProjection;
            Matrix.Invert(ref viewProjection, out var viewProjectionInverse);
            var parameters = effect.Parameters;
            parameters.Set(VoxelGridResolveShaderKeys.VoxelGridViewProjection, viewProjection);
            parameters.Set(VoxelGridResolveShaderKeys.VoxelGridViewProjectionInverse, viewProjectionInverse);
            parameters.Set(VoxelGridResolveShaderKeys.VoxelGridViewSize, new Vector2(Normal.Width, Normal.Height));
            parameters.Set(VoxelGridResolveShaderKeys.VoxelGridSceneDepth, sceneDepth ?? noSceneDepth);
            parameters.Set(VoxelGridResolveShaderKeys.VoxelGridSceneDepthBound, sceneDepth != null ? 1f : 0f);

            foreach (var grid in Grids)
            {
                var box = grid.Box;
                if (grid.Traversal?.Source == null || box == null)
                    continue;

                var world = grid.World;
                Matrix.Invert(ref world, out var worldInverse);
                Matrix.Multiply(ref world, ref viewProjection, out var worldViewProjection);

                grid.Traversal.ApplyParameters(parameters);
                parameters.Set(VoxelGridResolveShaderKeys.Traversal, grid.Traversal.GetShaderSource());
                parameters.Set(VoxelGridResolveShaderKeys.VoxelGridWorld, world);
                parameters.Set(VoxelGridResolveShaderKeys.VoxelGridWorldInverse, worldInverse);
                parameters.Set(VoxelGridResolveShaderKeys.VoxelGridWorldViewProjection, worldViewProjection);
                parameters.Set(VoxelGridResolveShaderKeys.VoxelGridMaxDistance, grid.MaxDistance);
                parameters.Set(VoxelGridResolveShaderKeys.VoxelGridDither, (int)grid.Dither);
                parameters.Set(VoxelGridResolveShaderKeys.VoxelGridIndex, grid.GridIndex);

                // How many of the grid's units one pixel spans per unit of distance: the pixel's
                // angle, times how the world's unit reads in the grid's own.
                var projection = renderView.Projection;
                var pixelAngle = projection.M22 != 0 ? 2.0f / (System.Math.Abs(projection.M22) * Normal.Height) : 0f;
                var localScale = new Vector3(worldInverse.M11, worldInverse.M12, worldInverse.M13).Length();
                var lodOn = !float.IsNaN(grid.LodBias);
                parameters.Set(VoxelGridResolveShaderKeys.VoxelGridLodPixel, lodOn ? pixelAngle * localScale : 0f);
                parameters.Set(VoxelGridResolveShaderKeys.VoxelGridLodBias, lodOn ? grid.LodBias : 0f);

                effect.UpdateEffect(device);
                var vertexBuffer = box.VertexBuffers[0];
                pipelineState.State.RootSignature = effect.RootSignature;
                pipelineState.State.EffectBytecode = effect.Effect.Bytecode;
                pipelineState.State.InputElements = vertexBuffer.Declaration.CreateInputElements();
                pipelineState.State.PrimitiveType = box.PrimitiveType;
                pipelineState.State.Output.CaptureState(commandList);
                pipelineState.Update();

                commandList.SetPipelineState(pipelineState.CurrentState);
                effect.Apply(context.GraphicsContext);
                commandList.SetVertexBuffer(0, vertexBuffer.Buffer, vertexBuffer.Offset, vertexBuffer.Stride);
                commandList.SetIndexBuffer(box.IndexBuffer.Buffer, box.IndexBuffer.Offset, box.IndexBuffer.Is32Bit);
                commandList.DrawIndexed(box.DrawCount, box.StartLocation);
            }
        }

        private void EnsureTargets(GraphicsDevice device, int width, int height)
        {
            if (Normal != null && Normal.Width == width && Normal.Height == height)
                return;

            ReleaseTargets();
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

        public void Dispose()
        {
            ReleaseTargets();
            effect?.Dispose();
            effect = null;
            noSceneDepth?.Dispose();
            noSceneDepth = null;
        }
    }

    /// <summary>The compositor's slot for the resolve when the camera's renderer is not a <see cref="ForwardRendererVoxels"/>: runs the renderer over the listed grids before the scene is drawn, without the scene's depth.</summary>
    public sealed class VoxelGridResolvePass : SceneRendererBase
    {
        /// <summary>The renderer this pass drives; grids register with it each frame.</summary>
        [Stride.Core.DataMemberIgnore]
        public VoxelGridResolveRenderer Renderer { get; } = new();

        protected override void DrawCore(RenderContext context, RenderDrawContext drawContext)
        {
            Renderer.Draw(drawContext, null);
        }

        protected override void Destroy()
        {
            Renderer.Dispose();
            base.Destroy();
        }
    }
}
