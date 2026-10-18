// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using Stride.Rendering;
using Xunit;

namespace Stride.Engine.Tests
{
    public class VisibilityGroupTests
    {
        [Fact]
        public void ReevaluatingRenderStages_ReusesAndClearsActiveRenderStages()
        {
            var renderSystem = new RenderSystem();
            var opaqueStage = new RenderStage("Opaque", "Main");
            var shadowStage = new RenderStage("Shadow", "ShadowMapCaster");
            renderSystem.RenderStages.Add(opaqueStage);
            renderSystem.RenderStages.Add(shadowStage);

            var renderFeature = new TestRenderFeature();
            renderFeature.RenderStageSelectors.Add(new SimpleGroupToRenderStageSelector { RenderStage = opaqueStage, EffectName = "TestEffect" });
            var shadowSelector = new SimpleGroupToRenderStageSelector { RenderStage = shadowStage, EffectName = "TestShadowEffect" };
            renderFeature.RenderStageSelectors.Add(shadowSelector);
            renderSystem.RenderFeatures.Add(renderFeature);

            using var visibilityGroup = new VisibilityGroup(renderSystem);
            var renderObject = new TestRenderObject();
            visibilityGroup.RenderObjects.Add(renderObject);

            var activeRenderStages = renderObject.ActiveRenderStages;
            Assert.Equal("TestEffect", activeRenderStages[opaqueStage.Index].EffectSelector.EffectName);
            Assert.Equal("TestShadowEffect", activeRenderStages[shadowStage.Index].EffectSelector.EffectName);

            // Removing and adding the object again reevaluates its stages; the shadow stage no longer matches its group
            shadowSelector.RenderGroup = RenderGroupMask.Group1;
            visibilityGroup.RenderObjects.Remove(renderObject);
            visibilityGroup.RenderObjects.Add(renderObject);

            Assert.Same(activeRenderStages, renderObject.ActiveRenderStages);
            Assert.Equal("TestEffect", activeRenderStages[opaqueStage.Index].EffectSelector.EffectName);
            Assert.False(activeRenderStages[shadowStage.Index].Active);
        }

        private class TestRenderObject : RenderObject
        {
        }

        private class TestRenderFeature : RootRenderFeature
        {
            public override Type SupportedRenderObjectType => typeof(TestRenderObject);
        }
    }
}
