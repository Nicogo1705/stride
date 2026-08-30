// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Sean Boettger <sean@whypenguins.com>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Rendering.Lights;

namespace Stride.Rendering.Voxels.VoxelGI
{
    /// <summary>
    /// A light casting from a voxel representation.
    /// </summary>
    [DataContract("LightVoxel")]
    [Display("Voxel")]
    public class LightVoxel : IEnvironmentLight
    {
        [DataMember(1)]
        public VoxelVolumeComponent Volume { get; set; }
        [DataMember(10)]
        public int AttributeIndex { get; set; } = 0;

        [DataMember(20)]
        public IVoxelMarchSet DiffuseMarcher { get; set; } = new VoxelMarchSetHemisphere6(new VoxelMarchConePerMipmap());
        [DataMember(30)]
        public IVoxelMarchMethod SpecularMarcher { get; set; } = new VoxelMarchCone(30, 0.5f, 1.0f);

        [DataMember(40)]
        public float BounceIntensityScale { get; set; }
        [DataMember(50)]
        public float SpecularIntensityScale { get; set; }

        /// <summary>
        /// Roughness above which the specular cone is not traced at all - the march is the most
        /// expensive part of the voxel light, and on a rough surface its result is a blur the
        /// diffuse cones already approximate. Faded out over a small window below the cutoff to
        /// avoid a visible seam. 1 (the default) traces every surface, as before.
        /// </summary>
        [DataMember(55)]
        [DataMemberRange(0.0, 1.0, 0.01, 0.1, 2)]
        public float SpecularRoughnessCutoff { get; set; } = 1.0f;

        /// <summary>
        /// Trace the diffuse cones into a buffer this many times smaller than the screen along each
        /// axis, instead of once per shaded pixel: 1 marches inline as before, 2 traces a quarter of
        /// the cones, 4 a sixteenth. The shaded pixel then reads that buffer, weighting its taps by
        /// depth so the reduction does not drag light across a silhouette.
        /// <para>
        /// Bounced light is low frequency, so the resolution it is traced at costs far less than
        /// the resolution it is applied at. This is the knob for a machine that cannot afford the
        /// cones at all: it needs a depth-only render stage on the compositor to prime the depth
        /// buffer, and falls back to marching inline when there is none.
        /// </para>
        /// </summary>
        [DataMember(57)]
        [DataMemberRange(1, 4, 1, 1, 0)]
        public int ScreenSpaceDivisor { get; set; } = 1;

        public bool Update(RenderLight light)
        {
            return true;
        }
    }
}
