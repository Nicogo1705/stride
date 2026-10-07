// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Sean Boettger <sean@whypenguins.com>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Core.Storage;
using Stride.Core.Threading;
using Stride.Graphics;
using Stride.Rendering.Shadows;
using Stride.Rendering.Voxels;
using Stride.Shaders;

namespace Stride.Rendering.Voxels
{
    /// <summary>
    /// A render feature that computes and uploads info for voxelization
    /// </summary>
    public class VoxelRenderFeature : SubRenderFeature
    {
        [DataMemberIgnore]
        public static readonly PropertyKey<Dictionary<VoxelVolumeComponent, ProcessedVoxelVolume>> CurrentProcessedVoxelVolumes = new PropertyKey<Dictionary<VoxelVolumeComponent, ProcessedVoxelVolume>>("VoxelRenderFeature.CurrentProcessedVoxelVolumes", typeof(VoxelRenderFeature));

        private Dictionary<VoxelVolumeComponent, ProcessedVoxelVolume> renderVoxelVolumeData;
        
        private LogicalGroupReference VoxelizerStorerCasterKey;
        private readonly Dictionary<ObjectId, ParameterCollection> parametersByLayout = new();
        private StaticObjectPropertyKey<RenderEffect> renderEffectKey;

        protected override void InitializeCore()
        {
            base.InitializeCore();
            renderEffectKey = ((RootEffectRenderFeature)RootRenderFeature).RenderEffectKey;
            VoxelizerStorerCasterKey = ((RootEffectRenderFeature)RootRenderFeature).CreateViewLogicalGroup("VoxelizerStorer");
        }

        public override void PrepareEffectPermutations(RenderDrawContext context)
        {
            renderVoxelVolumeData = Context.VisibilityGroup.Tags.Get(CurrentProcessedVoxelVolumes);
            if (renderVoxelVolumeData == null) return;


            var renderEffects = RootRenderFeature.RenderData.GetData(renderEffectKey);
            int effectSlotCount = ((RootEffectRenderFeature)RootRenderFeature).EffectPermutationSlotCount;


            var rootEffectRenderFeature = ((RootEffectRenderFeature)RootRenderFeature);

            if (rootEffectRenderFeature == null) return;

            foreach (var processedVolumeKeyValue in renderVoxelVolumeData)
            {
                var processedVolume = processedVolumeKeyValue.Value;

                foreach (VoxelizationPass pass in processedVolume.passList.passes)
                {
                    pass.storer.UpdateVoxelizationLayout("Storage");
                    for (int i = 0; i < pass.AttributesIndirect.Count; i++)
                    {
                        var attr = pass.AttributesIndirect[i];
                        attr.UpdateVoxelizationLayout($"AttributesIndirect[{i}]");
                    }
                }
                foreach (var group in processedVolume.groupedPasses)
                {
                    //Each pass in a group should have identical shaders
                    var pass = group[0];

                    Dispatcher.ForEach(RootRenderFeature.RenderObjects, renderObject =>
                    {
                        var renderMesh = (RenderMesh)renderObject;

                        var staticObjectNode = renderMesh.StaticObjectNode;


                        var effectSlot = rootEffectRenderFeature.GetEffectPermutationSlot(RenderSystem.RenderStages[pass.view.RenderStages[0].Index]);
                        {

                            var staticEffectObjectNode = staticObjectNode * effectSlotCount + effectSlot.Index;
                            var renderEffect = renderEffects[staticEffectObjectNode];

                            // Skip effects not used during this frame
                            if (renderEffect != null)
                            {
                                renderEffect.EffectValidator.ValidateParameter(VoxelizeToFragmentsKeys.Storage, pass.source);
                                renderEffect.EffectValidator.ValidateParameter(VoxelizeToFragmentsKeys.RequireGeometryShader, pass.storer.RequireGeometryShader() || pass.method.RequireGeometryShader());
                                renderEffect.EffectValidator.ValidateParameter(VoxelizeToFragmentsKeys.GeometryShaderMaxVertexCount, pass.storer.GeometryShaderOutputCount() * pass.method.GeometryShaderOutputCount());
                            }
                        }
                    });
                }
            }
        }
        public override void Prepare(RenderDrawContext context)
        {
            renderVoxelVolumeData = Context.VisibilityGroup.Tags.Get(CurrentProcessedVoxelVolumes);
            if (renderVoxelVolumeData == null) return;

            foreach (var processedVolumeKeyValue in renderVoxelVolumeData)
            {
                var processedVolume = processedVolumeKeyValue.Value;
                foreach (VoxelizationPass pass in processedVolume.passList.passes)
                {
                    var viewFeature = pass.view.Features[RootRenderFeature.Index];


                    // Find a PerView layout from an effect in normal state
                    ViewResourceGroupLayout firstViewLayout = null;
                    foreach (var viewLayout in viewFeature.Layouts)
                    {
                        // Only process view layouts in normal state
                        if (viewLayout.State != RenderEffectState.Normal)
                            continue;

                        var viewLighting = viewLayout.GetLogicalGroup(VoxelizerStorerCasterKey);
                        if (viewLighting.Hash != ObjectId.Empty)
                        {
                            firstViewLayout = viewLayout;
                            break;
                        }
                    }

                    // Nothing found for this view (no effects in normal state)
                    if (firstViewLayout == null)
                        continue;

                    // One parameter set per distinct layout: an effect that never stores (a material that discards in the
                    // voxelizer) has its fragment buffer compiled out and a layout of its own
                    parametersByLayout.Clear();
                    foreach (var viewLayout in viewFeature.Layouts)
                    {
                        if (viewLayout.State != RenderEffectState.Normal)
                            continue;

                        var voxelizerStorer = viewLayout.GetLogicalGroup(VoxelizerStorerCasterKey);
                        if (voxelizerStorer.Hash == ObjectId.Empty)
                            continue;

                        if (!parametersByLayout.TryGetValue(voxelizerStorer.Hash, out var viewParameters))
                        {
                            var viewParameterLayout = new ParameterCollectionLayout();
                            viewParameterLayout.ProcessLogicalGroup(viewLayout, ref voxelizerStorer);
                            viewParameters = new ParameterCollection();
                            viewParameters.UpdateLayout(viewParameterLayout);

                            pass.storer.ApplyVoxelizationParameters(viewParameters);
                            foreach (var attr in processedVolume.Attributes)
                                attr.Attribute.ApplyVoxelizationParameters(viewParameters);
                            parametersByLayout.Add(voxelizerStorer.Hash, viewParameters);
                        }

                        var resourceGroup = viewLayout.Entries[pass.view.Index].Resources;
                        resourceGroup.UpdateLogicalGroup(ref voxelizerStorer, viewParameters);
                    }
                }
            }
        }
    }
}
