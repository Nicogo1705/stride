// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Sean Boettger <sean@whypenguins.com>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using Stride.Core.Collections;
using Stride.Core.Diagnostics;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering.Skyboxes;
using Stride.Shaders;
using Stride.Rendering.Voxels;
using Stride.Rendering.Shadows;
using Stride.Rendering.Lights;
using Stride.Engine.Processors;

namespace Stride.Rendering.Voxels.VoxelGI
{
    /// <summary>
    /// Light renderer for <see cref="LightVoxel"/>.
    /// </summary>
    public class LightVoxelRenderer : LightGroupRendererBase
    {
        private readonly Dictionary<RenderLight, LightVoxelShaderGroup> lightShaderGroupsPerVoxel = new Dictionary<RenderLight, LightVoxelShaderGroup>();
        private PoolListStruct<LightVoxelShaderGroup> pool = new PoolListStruct<LightVoxelShaderGroup>(8, CreateLightVoxelShaderGroup);

        public override Type[] LightTypes { get; } = { typeof(LightVoxel) };

        public LightVoxelRenderer()
        {
            IsEnvironmentLight = true;
        }


        public override void Reset()
        {
            base.Reset();

            foreach (var lightShaderGroup in lightShaderGroupsPerVoxel)
                lightShaderGroup.Value.Reset();

            lightShaderGroupsPerVoxel.Clear();
            pool.Reset();
        }

        /// <inheritdoc/>
        public override void ProcessLights(ProcessLightsParameters parameters)
        {
            foreach (var index in parameters.LightIndices)
            {
                // For now, we allow only one cubemap at once
                var light = parameters.LightCollection[index];

                // Prepare LightVoxelShaderGroup
                LightVoxelShaderGroup lightShaderGroup;
                if (!lightShaderGroupsPerVoxel.TryGetValue(light, out lightShaderGroup))
                {
                    lightShaderGroup = pool.Add();
                    lightShaderGroup.Light = light;

                    lightShaderGroupsPerVoxel.Add(light, lightShaderGroup);
                }
            }

            // Consume all the lights
            parameters.LightIndices.Clear();
        }

        public override void UpdateShaderPermutationEntry(ForwardLightingRenderFeature.LightShaderPermutationEntry shaderEntry)
        {
            foreach (var cubemap in lightShaderGroupsPerVoxel)
            {
                shaderEntry.EnvironmentLights.Add(cubemap.Value);
            }
        }

        private static LightVoxelShaderGroup CreateLightVoxelShaderGroup()
        {
            return new LightVoxelShaderGroup(new ShaderMixinGeneratorSource("LightVoxelEffect"));
        }

        private class LightVoxelShaderGroup : LightShaderGroup
        {
            private ValueParameterKey<float> intensityKey;
            private ValueParameterKey<float> specularIntensityKey;
            private ValueParameterKey<float> specularRoughnessCutoffKey;
            private ValueParameterKey<float> specularOffsetKey;
            private ValueParameterKey<Vector3> skyLightKey;
            private ValueParameterKey<float> giResolveEnabledKey;
            private ValueParameterKey<Vector4> giResolveSizesKey;
            private ObjectParameterKey<Texture> giResolveTextureKey;

            private string compositionName;
            private ShaderSourceCollection resolveSamplers;
            private ShaderSource resolveMarcher;

            private PermutationParameterKey<ShaderSource> diffuseMarcherKey;
            private PermutationParameterKey<ShaderSource> bounceMarcherKey;
            private ValueParameterKey<float> bounceMarchEnabledKey;
            private PermutationParameterKey<ShaderSource> specularMarcherKey;
            private PermutationParameterKey<ShaderSourceCollection> attributeSamplersKey;

            public RenderLight Light { get; set; }

            VoxelAttribute traceAttribute = null;

            public LightVoxelShaderGroup(ShaderSource mixin) : base(mixin)
            {
                HasEffectPermutations = true;
                traceAttribute = null;
            }

            ProcessedVoxelVolume GetProcessedVolume()
            {
                var lightVoxel = ((LightVoxel)Light.Type);
                if (lightVoxel.Volume == null)
                {
                    throw new ArgumentNullException("No Voxel Volume Component selected for voxel light.");
                }
                var voxelVolumeProcessor = lightVoxel.Volume.Entity.EntityManager.GetProcessor<VoxelVolumeProcessor>();
                if (voxelVolumeProcessor == null)
                    return null;

                ProcessedVoxelVolume processedVolume = voxelVolumeProcessor.GetProcessedVolumeForComponent(lightVoxel.Volume);
                return processedVolume;
            }

            VoxelAttribute GetTraceAttr()
            {
                var lightVoxel = ((LightVoxel)Light.Type);

                ProcessedVoxelVolume processedVolume = GetProcessedVolume();
                if (processedVolume == null)
                    return null;

                if (processedVolume.OutputAttributes.Count > lightVoxel.AttributeIndex)
                {
                    return processedVolume.OutputAttributes[lightVoxel.AttributeIndex];
                }
                else
                {
                    throw new ArgumentOutOfRangeException("Tried to access attribute index " + lightVoxel.AttributeIndex.ToString() + " (zero-indexed) when the Voxel Volume Component has only " + processedVolume.OutputAttributes.Count.ToString() + " attributes.");
                }
            }
            public override void UpdateLayout(string compositionName)
            {
                base.UpdateLayout(compositionName);

                traceAttribute = GetTraceAttr();

                this.compositionName = compositionName;

                intensityKey = LightVoxelShaderKeys.Intensity.ComposeWith(compositionName);
                specularIntensityKey = LightVoxelShaderKeys.SpecularIntensity.ComposeWith(compositionName);
                specularRoughnessCutoffKey = LightVoxelShaderKeys.SpecularRoughnessCutoff.ComposeWith(compositionName);
                specularOffsetKey = LightVoxelShaderKeys.SpecularOffset.ComposeWith(compositionName);
                skyLightKey = LightVoxelShaderKeys.SkyLight.ComposeWith(compositionName);
                giResolveEnabledKey = LightVoxelShaderKeys.GIResolveEnabled.ComposeWith(compositionName);
                giResolveSizesKey = LightVoxelShaderKeys.GIResolveSizes.ComposeWith(compositionName);
                giResolveTextureKey = LightVoxelShaderKeys.GIResolveTexture.ComposeWith(compositionName);

                diffuseMarcherKey = LightVoxelShaderKeys.diffuseMarcher.ComposeWith(compositionName);
                bounceMarcherKey = LightVoxelShaderKeys.bounceMarcher.ComposeWith(compositionName);
                bounceMarchEnabledKey = LightVoxelShaderKeys.BounceMarchEnabled.ComposeWith(compositionName);
                specularMarcherKey = LightVoxelShaderKeys.specularMarcher.ComposeWith(compositionName);
                attributeSamplersKey = MarchAttributesKeys.AttributeSamplers.ComposeWith(compositionName);

                if (traceAttribute != null)
                {
                    if (((LightVoxel)Light.Type).DiffuseMarcher != null)
                        ((LightVoxel)Light.Type).DiffuseMarcher.UpdateMarchingLayout("diffuseMarcher." + compositionName);
                    if (((LightVoxel)Light.Type).SpecularMarcher != null)
                        ((LightVoxel)Light.Type).SpecularMarcher.UpdateMarchingLayout("specularMarcher." + compositionName);
                    ((LightVoxel)Light.Type).BounceMarcher?.UpdateMarchingLayout("bounceMarcher." + compositionName);
                    traceAttribute.UpdateSamplingLayout("AttributeSamplers[0]." + compositionName);
                }
            }

            public override void ApplyEffectPermutations(RenderEffect renderEffect)
            {
                if (traceAttribute != null)
                {
                    ShaderSourceCollection collection = new ShaderSourceCollection
                    {
                        traceAttribute.GetSamplingShader()
                    };
                    renderEffect.EffectValidator.ValidateParameter(attributeSamplersKey, collection);

                    if (((LightVoxel)Light.Type).DiffuseMarcher != null)
                        renderEffect.EffectValidator.ValidateParameter(diffuseMarcherKey, ((LightVoxel)Light.Type).DiffuseMarcher.GetMarchingShader(0));
                    if (((LightVoxel)Light.Type).SpecularMarcher != null)
                        renderEffect.EffectValidator.ValidateParameter(specularMarcherKey, ((LightVoxel)Light.Type).SpecularMarcher.GetMarchingShader(0));

                    // The bounce composition must always be filled: an empty compose does not compile.
                    // With no bounce marcher the diffuse marcher fills it and BounceMarchEnabled stays zero.
                    var bounce = ((LightVoxel)Light.Type).BounceMarcher ?? ((LightVoxel)Light.Type).DiffuseMarcher;
                    if (bounce != null)
                        renderEffect.EffectValidator.ValidateParameter(bounceMarcherKey, bounce.GetMarchingShader(0));
                }
            }

            public override void ApplyViewParameters(RenderDrawContext context, int viewIndex, ParameterCollection parameters)
            {
                base.ApplyViewParameters(context, viewIndex, parameters);

                var lightVoxel = ((LightVoxel)Light.Type);

                if (lightVoxel.Volume == null)
                    return;
                ProcessedVoxelVolume processedVolume = GetProcessedVolume();
                if (processedVolume == null)
                    return;

                var intensity = Light.Intensity;
                var intensityBounceScale = lightVoxel.BounceIntensityScale;
                var specularIntensity = lightVoxel.SpecularIntensityScale * intensity;

                VoxelViewContext viewContext = new VoxelViewContext(processedVolume.passList, viewIndex);
                if (viewContext.IsVoxelView)
                {
                    intensity *= intensityBounceScale / 3.141592f;
                    specularIntensity = 0.0f;
                }

                // Only a voxel view marches the bounce set, and only when there is one to march.
                parameters.Set(bounceMarchEnabledKey, viewContext.IsVoxelView && lightVoxel.BounceMarcher != null ? 1.0f : 0.0f);

                parameters.Set(intensityKey, intensity);
                parameters.Set(specularIntensityKey, specularIntensity);
                parameters.Set(specularRoughnessCutoffKey, lightVoxel.SpecularRoughnessCutoff);
                parameters.Set(specularOffsetKey, lightVoxel.SpecularOffset);
                parameters.Set(skyLightKey, (Vector3)lightVoxel.SkyColor * lightVoxel.SkyIntensity);

                var resolved = PrepareScreenSpaceResolve(context, lightVoxel, viewContext);

                parameters.Set(giResolveEnabledKey, resolved != null ? 1.0f : 0.0f);
                if (resolved != null)
                {
                    parameters.Set(giResolveTextureKey, resolved.Texture);
                    parameters.Set(giResolveSizesKey, resolved.Sizes);
                }

                if (traceAttribute != null)
                {
                    lightVoxel.DiffuseMarcher?.ApplyMarchingParameters(parameters);
                    lightVoxel.SpecularMarcher?.ApplyMarchingParameters(parameters);
                    lightVoxel.BounceMarcher?.ApplyMarchingParameters(parameters);
                    traceAttribute.ApplySamplingParameters(viewContext, parameters);
                }
            }

            /// <summary>
            /// Requests a reduced-resolution trace from <see cref="VoxelGIResolver"/> for this frame and fills its parameters.
            /// Returns the state to read from, or null when marching inline.
            /// </summary>
            /// <remarks>
            /// The marcher and attribute hold one set of composed keys, so the resolver's parameters are filled here
            /// and the light's own layout restored afterwards. The texture returned is the one written last frame.
            /// </remarks>
            private static bool warnedNoResolver;

            private VoxelGIResolveState PrepareScreenSpaceResolve(RenderDrawContext context, LightVoxel lightVoxel, VoxelViewContext viewContext)
            {
                // A voxel view is voxelizing the scene into the clipmaps, not shading a screen:
                // there is no depth buffer of it and nothing to reduce.
                if (viewContext.IsVoxelView || lightVoxel.ScreenSpaceDivisor <= 1
                    || traceAttribute == null || lightVoxel.DiffuseMarcher == null)
                    return null;

                var state = context.RenderContext.VisibilityGroup?.Tags.Get(VoxelGIResolver.Current);
                if (state?.Parameters == null)
                {
                    // Asked for and not available: the compositor has no ForwardRendererVoxels, or
                    // no depth-only stage for it to read. Said once, since the light would
                    // otherwise march inline forever with nothing to show why.
                    if (!warnedNoResolver)
                        GlobalLogger.GetLogger("LightVoxelRenderer").Warning("A voxel light asks for a screen-space divisor but the compositor has no resolver to trace into (ForwardRendererVoxels with a depth-only stage); its cones are traced per pixel instead.");
                    warnedNoResolver = true;
                    return null;
                }

                state.Divisor = lightVoxel.ScreenSpaceDivisor;
                state.Requested = true;

                resolveSamplers ??= new ShaderSourceCollection { traceAttribute.GetSamplingShader() };
                resolveMarcher ??= lightVoxel.DiffuseMarcher.GetMarchingShader(0);

                lightVoxel.DiffuseMarcher.UpdateMarchingLayout("diffuseMarcher");
                traceAttribute.UpdateSamplingLayout("AttributeSamplers[0]");

                state.Parameters.Set(VoxelGIResolveShaderKeys.diffuseMarcher, resolveMarcher);
                state.Parameters.Set(VoxelGIResolveShaderKeys.SkyLight, (Vector3)lightVoxel.SkyColor * lightVoxel.SkyIntensity);
                state.Parameters.Set(MarchAttributesKeys.AttributeSamplers, resolveSamplers);
                lightVoxel.DiffuseMarcher.ApplyMarchingParameters(state.Parameters);
                traceAttribute.ApplySamplingParameters(new VoxelViewContext(false), state.Parameters);

                lightVoxel.DiffuseMarcher.UpdateMarchingLayout("diffuseMarcher." + compositionName);
                lightVoxel.SpecularMarcher?.UpdateMarchingLayout("specularMarcher." + compositionName);
                traceAttribute.UpdateSamplingLayout("AttributeSamplers[0]." + compositionName);

                return state.Texture != null ? state : null;
            }
        }
    }
}

