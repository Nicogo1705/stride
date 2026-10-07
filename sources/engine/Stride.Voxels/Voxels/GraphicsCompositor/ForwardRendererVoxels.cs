// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Sean Boettger <sean@whypenguins.com>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Collections;
using Stride.Core.Diagnostics;
using Stride.Core.Mathematics;
using Stride.Core.Storage;
using Stride.Graphics;
using Stride.Rendering.Images;
using Stride.Rendering.Lights;
using Stride.Rendering.Shadows;
using Stride.Rendering.SubsurfaceScattering;
using Stride.VirtualReality;
using Stride.Rendering.Compositing;
using Stride.Rendering.Voxels.Debug;
using Stride.Rendering.Voxels.Grid;

namespace Stride.Rendering.Voxels
{
    /// <summary>
    /// Renders your game. It should use current <see cref="RenderContext.RenderView"/> and <see cref="CameraComponentRendererExtensions.GetCurrentCamera"/>.
    /// </summary>
    [Display("Forward & Voxel renderer")]
    public class ForwardRendererVoxels : ForwardRenderer
    {
        public IVoxelRenderer VoxelRenderer { get; set; }

        protected IShadowMapRenderer ShadowMapRenderer_notPrivate;

        public VoxelDebug VoxelVisualization { get; set; }

        /// <summary>
        /// Traces the diffuse cones into a reduced-resolution buffer when a voxel light requests it
        /// through <see cref="VoxelGI.LightVoxel.ScreenSpaceDivisor"/>; does nothing otherwise.
        /// </summary>
        [DataMemberIgnore]
        public VoxelGI.VoxelGIResolver GIResolver { get; } = new VoxelGI.VoxelGIResolver();

        /// <summary>
        /// Resolves the voxel grids of the scene before the opaque pass, bounded by the depth prepass
        /// so a ray stops at the opaque surface in front of a grid; grids register with it each frame.
        /// </summary>
        [DataMemberIgnore]
        public VoxelGridResolveRenderer GridResolver { get; } = new VoxelGridResolveRenderer();

        private static readonly ProfilingKey GIResolveProfilingKey = new ProfilingKey("VoxelGI: Screen-space resolve");
        private static readonly ProfilingKey GridResolveProfilingKey = new ProfilingKey("VoxelGrid: Resolve");

        protected override void InitializeCore()
        {
            ShadowMapRenderer_notPrivate = Context.RenderSystem.RenderFeatures.OfType<MeshRenderFeature>().FirstOrDefault()?.RenderFeatures.OfType<ForwardLightingRenderFeature>().FirstOrDefault()?.ShadowMapRenderer;
            base.InitializeCore();

            GIResolver.Initialize(Context);
        }

        protected override void Destroy()
        {
            (VoxelRenderer as IDisposable)?.Dispose();
            GIResolver.Dispose();
            GridResolver.Dispose();
            base.Destroy();
        }
        protected override void CollectCore(RenderContext context)
        {
            VoxelRenderer?.Collect(Context, ShadowMapRenderer_notPrivate);
            base.CollectCore(context);
        }
        protected override void DrawCore(RenderContext context, RenderDrawContext drawContext)
        {
            using (drawContext.PushRenderTargetsAndRestore())
            {
                VoxelRenderer?.Draw(drawContext, ShadowMapRenderer_notPrivate);
            }

            base.DrawCore(context, drawContext);
        }

        protected override void DrawView(RenderContext context, RenderDrawContext drawContext, int eyeIndex, int eyeCount)
        {
            ResolveFromDepth(context, drawContext);

            base.DrawView(context, drawContext, eyeIndex, eyeCount);

            // Voxel Debug if enabled
            if (VoxelVisualization != null)
            {
                VoxelVisualization.VoxelRenderer = VoxelRenderer;
                VoxelVisualization.Draw(drawContext, viewOutputTarget);
            }
        }

        /// <summary>
        /// Runs the passes that read the scene's depth before the opaque pass: the grid resolve and the
        /// diffuse cones into the reduced-resolution buffer.
        /// </summary>
        /// <remarks>
        /// A depth-only prepass through <c>GBufferRenderStage</c> fills the depth first. Without such a stage the
        /// grids resolve unbounded and the light marches inline.
        /// </remarks>
        private void ResolveFromDepth(RenderContext context, RenderDrawContext drawContext)
        {
            var grids = GridResolver.Grids.Count > 0;
            var gi = GIResolver.Requested;
            if (!grids && !gi)
                return;

            var commandList = drawContext.CommandList;
            var depthStencil = commandList.DepthStencilBuffer;

            // Emptied before the prepass: the grids' materials draw their box in it and read the
            // targets, and must find nothing resolved there.
            if (grids)
                GridResolver.Clear(drawContext);

            Texture depth = null;
            if (GBufferRenderStage != null && depthStencil != null)
            {
                using (drawContext.QueryManager.BeginProfile(Color.Green, CompositingProfilingKeys.GBuffer))
                using (drawContext.PushRenderTargetsAndRestore())
                {
                    commandList.Clear(depthStencil, DepthStencilClearOptions.DepthBuffer);
                    commandList.SetRenderTarget(depthStencil, null);

                    context.RenderSystem.Draw(drawContext, context.RenderView, GBufferRenderStage);
                }

                depth = drawContext.Resolver.ResolveDepthStencil(depthStencil);
            }

            if (grids)
            {
                using (drawContext.QueryManager.BeginProfile(Color.Green, GridResolveProfilingKey))
                {
                    GridResolver.Draw(drawContext, depth);
                }
            }

            if (gi && depth != null)
            {
                using (drawContext.QueryManager.BeginProfile(Color.Green, GIResolveProfilingKey))
                using (drawContext.PushRenderTargetsAndRestore())
                {
                    GIResolver.Draw(drawContext, depth, new Size2(depthStencil.Width, depthStencil.Height));
                }
            }
        }
    }
}


