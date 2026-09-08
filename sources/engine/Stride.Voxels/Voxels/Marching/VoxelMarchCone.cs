// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Sean Boettger <sean@whypenguins.com>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System;
using System.Collections.Generic;
using System.Text;
using Stride.Core;
using Stride.Shaders;

namespace Stride.Rendering.Voxels
{
    [DataContract(DefaultMemberMode = DataMemberMode.Default)]
    [Display("Cone")]
    public class VoxelMarchCone : IVoxelMarchMethod
    {
        [DataMember(0)]
        /// <summary>
        /// Reads step count, scale and cone ratio from a constant buffer instead of compiling them in,
        /// so they can be tuned without a new shader.
        /// </summary>
        public bool EditMode = false;
        [DataMember(10)]
        public bool Fast = false;
        [DataMember(20)]
        public int Steps = 9;
        [DataMember(30)]
        public float StepScale = 1.0f;
        [DataMember(40)]
        public float ConeRatio = 1.0f;
        [DataMember(50)]
        public float StartOffset = 1.0f;

        /// <summary>
        /// Furthest the cone may travel, in world units, or zero for no limit.
        /// </summary>
        /// <remarks>A uniform rather than a template argument, so changing it compiles no new permutation.</remarks>
        [DataMember(60)]
        public float MaxDistance = 0.0f;

        public VoxelMarchCone()
        {

        }
        public VoxelMarchCone(int steps, float stepScale, float ratio, float maxDistance = 0.0f)
        {
            Steps = steps;
            StepScale = stepScale;
            ConeRatio = ratio;
            MaxDistance = maxDistance;
            EditMode = false;
        }
        public ShaderSource GetMarchingShader(int attrID)
        {
            var mixin = new ShaderMixinSource();
            if (EditMode)
            {
                mixin.Mixins.Add(new ShaderClassSource("VoxelMarchConeEditMode"));
            }
            else
            {
                mixin.Mixins.Add(new ShaderClassSource("VoxelMarchCone", Steps, StepScale, ConeRatio, StartOffset));
                mixin.Macros.Add(new ShaderMacro("sampleFunction", Fast ? "SampleNearestMip" : "Sample"));
            }
            mixin.Macros.Add(new ShaderMacro("AttributeID", attrID));

            return mixin;
        }

        ValueParameterKey<int> StepsKey;
        ValueParameterKey<float> StepScaleKey;
        ValueParameterKey<float> ConeRatioKey;
        ValueParameterKey<int> FastKey;
        ValueParameterKey<float> OffsetKey;
        ValueParameterKey<float> MaxDistanceKey;
        public void UpdateMarchingLayout(string compositionName)
        {
            if (!EditMode)
                MaxDistanceKey = VoxelMarchConeKeys.maxTraceDistance.ComposeWith(compositionName);
            if (EditMode)
            {
                StepsKey = VoxelMarchConeEditModeKeys.steps.ComposeWith(compositionName);
                StepScaleKey = VoxelMarchConeEditModeKeys.stepScale.ComposeWith(compositionName);
                ConeRatioKey = VoxelMarchConeEditModeKeys.coneRatio.ComposeWith(compositionName);
                FastKey = VoxelMarchConeEditModeKeys.fast.ComposeWith(compositionName);
                OffsetKey = VoxelMarchConeEditModeKeys.offset.ComposeWith(compositionName);
            }
        }
        public void ApplyMarchingParameters(ParameterCollection parameters)
        {
            if (!EditMode)
                parameters.Set(MaxDistanceKey, MaxDistance);
            if (EditMode)
            {
                parameters.Set(StepsKey, Steps);
                parameters.Set(StepScaleKey, StepScale);
                parameters.Set(ConeRatioKey, ConeRatio);
                parameters.Set(FastKey, Fast ? 1 : 0);
                parameters.Set(OffsetKey, StartOffset);
            }
        }
    }
}
