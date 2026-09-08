// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Sean Boettger <sean@whypenguins.com>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering.Images;

namespace Stride.Rendering.Voxels.VoxelGI
{
    /// <summary>
    /// State shared between the reduced-resolution GI pass and the light shader. The light group fills
    /// the request during Prepare, the pass draws during Draw, and the shader reads the texture the next frame.
    /// </summary>
    public class VoxelGIResolveState
    {
        /// <summary>Where the pass wrote the diffuse GI, or null before the first one has run.</summary>
        public Texture Texture;

        /// <summary>(1/screen width, 1/screen height, 1/buffer width, 1/buffer height).</summary>
        public Vector4 Sizes;

        /// <summary>How many times smaller than the screen the buffer is. 1 disables the pass.</summary>
        public int Divisor = 1;

        /// <summary>Set by the light group when it has filled <see cref="Parameters"/> for this frame.</summary>
        public bool Requested;

        /// <summary>The pass's own parameters, filled by the light group so both agree on the marcher.</summary>
        public ParameterCollection Parameters;
    }

    /// <summary>
    /// Traces the diffuse cones into a reduced-resolution buffer, once per pixel of that buffer rather than per shaded pixel.
    /// </summary>
    /// <remarks>
    /// Reads the depth buffer, so a Z prepass must have filled it: <see cref="ForwardRendererVoxels"/> runs one,
    /// and the light falls back to marching inline when the compositor has no depth-only stage.
    /// </remarks>
    public class VoxelGIResolver : IDisposable
    {
        public static readonly PropertyKey<VoxelGIResolveState> Current =
            new PropertyKey<VoxelGIResolveState>("VoxelGIResolver.Current", typeof(VoxelGIResolver));

        private readonly VoxelGIResolveState state = new VoxelGIResolveState();
        private ImageEffectShader resolveEffect;
        private Texture target;

        /// <summary>
        /// Publishes the shared state for the light group. The buffer itself is allocated on the first Draw.
        /// </summary>
        public void Initialize(RenderContext context)
        {
            resolveEffect ??= new ImageEffectShader("VoxelGIResolveEffect");
            resolveEffect.Initialize(context);
            state.Parameters = resolveEffect.Parameters;

            context.VisibilityGroup?.Tags.Set(Current, state);
        }

        /// <summary>Whether a light asked for the pass this frame.</summary>
        public bool Requested => state.Requested && state.Divisor > 1;

        /// <summary>
        /// Traces the cones into the buffer. <paramref name="depth"/> is the depth buffer as a shader resource,
        /// <paramref name="screenSize"/> the resolution being shaded.
        /// </summary>
        public void Draw(RenderDrawContext drawContext, Texture depth, Size2 screenSize)
        {
            if (!Requested || depth == null)
                return;

            var width = Math.Max(1, screenSize.Width / state.Divisor);
            var height = Math.Max(1, screenSize.Height / state.Divisor);

            if (target == null || target.Width != width || target.Height != height)
            {
                target?.Dispose();

                // Half float: the cones return HDR radiance, and the alpha carries the projected
                // depth, where 8 bits could not tell two surfaces apart at all.
                target = Texture.New2D(drawContext.GraphicsDevice, width, height,
                                       PixelFormat.R16G16B16A16_Float,
                                       TextureFlags.ShaderResource | TextureFlags.RenderTarget);
            }

            state.Texture = target;
            state.Sizes = new Vector4(1.0f / screenSize.Width, 1.0f / screenSize.Height, 1.0f / width, 1.0f / height);

            // An image effect gets none of the view's constants; set the ones needed to rebuild a world position from depth.
            var renderView = drawContext.RenderContext.RenderView;
            Matrix.Invert(ref renderView.Projection, out var projectionInverse);
            Matrix.Invert(ref renderView.View, out var viewInverse);

            resolveEffect.Parameters.Set(TransformationKeys.ProjectionInverse, projectionInverse);
            resolveEffect.Parameters.Set(TransformationKeys.ViewInverse, viewInverse);
            resolveEffect.Parameters.Set(TransformationKeys.Eye, new Vector4(viewInverse.TranslationVector, 1.0f));

            resolveEffect.Parameters.Set(DepthBaseKeys.DepthStencil, depth);
            resolveEffect.SetOutput(target);
            resolveEffect.Draw(drawContext, "VoxelGI.Resolve");

            state.Requested = false;
        }

        public void Dispose()
        {
            target?.Dispose();
            target = null;
            state.Texture = null;

            resolveEffect?.Dispose();
            resolveEffect = null;
        }
    }
}
