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

        private static readonly ProfilingKey GIResolveProfilingKey = new ProfilingKey("VoxelGI: Screen-space resolve");

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
            ResolveScreenSpaceGI(context, drawContext);

            base.DrawView(context, drawContext, eyeIndex, eyeCount);

            // Voxel Debug if enabled
            if (VoxelVisualization != null)
            {
                VoxelVisualization.VoxelRenderer = VoxelRenderer;
                VoxelVisualization.Draw(drawContext, viewOutputTarget);
            }
        }

        /// <summary>
        /// Traces the diffuse cones into the reduced-resolution buffer before the opaque pass reads it.
        /// </summary>
        /// <remarks>
        /// The pass reads depth, so a depth-only prepass through <c>GBufferRenderStage</c> runs first.
        /// Without such a stage the light marches inline and this does nothing.
        /// </remarks>
        private void ResolveScreenSpaceGI(RenderContext context, RenderDrawContext drawContext)
        {
            if (!GIResolver.Requested || GBufferRenderStage == null)
                return;

            var commandList = drawContext.CommandList;
            var depthStencil = commandList.DepthStencilBuffer;
            if (depthStencil == null)
                return;

            using (drawContext.QueryManager.BeginProfile(Color.Green, GIResolveProfilingKey))
            {
                using (drawContext.PushRenderTargetsAndRestore())
                {
                    commandList.Clear(depthStencil, DepthStencilClearOptions.DepthBuffer);
                    commandList.SetRenderTarget(depthStencil, null);

                    context.RenderSystem.Draw(drawContext, context.RenderView, GBufferRenderStage);
                }

                var depth = drawContext.Resolver.ResolveDepthStencil(depthStencil);

                using (drawContext.PushRenderTargetsAndRestore())
                {
                    GIResolver.Draw(drawContext, depth, new Size2(depthStencil.Width, depthStencil.Height));
                }
            }
        }
    }
}


