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

        /// <summary>
        /// The cone set traced while voxelizing, when <see cref="BounceIntensityScale"/> feeds the
        /// indirect light back into the voxels. Null marches <see cref="DiffuseMarcher"/> there too.
        /// </summary>
        /// <remarks>
        /// The two views ask the same question and can afford very different answers. What a shaded
        /// pixel receives is looked at directly, and is traced once per pixel - or once per pixel of
        /// a reduced buffer, see <see cref="ScreenSpaceDivisor"/>. What a voxelized fragment
        /// receives is written into a voxel, averaged with everything else in it, mipmapped, and
        /// then read back through a cone that integrates a mip: it is blurred twice before anyone
        /// sees it, and there is no screen-space reduction to spread its cost over. It is therefore
        /// both the more expensive of the two and the one that can least tell the difference.
        /// <para>
        /// Give it a distinct instance, never the same object as <see cref="DiffuseMarcher"/>: a
        /// marcher holds one set of composed parameter keys, and two compositions sharing an
        /// instance leave one of them unwritten.
        /// </para>
        /// </remarks>
        [DataMember(35)]
        public IVoxelMarchSet BounceMarcher { get; set; }

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
        /// How far along the normal, in voxels, the specular cone starts from the shaded point.
        /// One voxel (the default) starts it at the edge of the surface's own voxel shell, which a
        /// filtered fetch still half reads; a cone that grazes its own shell at fixed angles as it
        /// climbs the mips draws rings on a curved surface, and starting further out clears them.
        /// </summary>
        [DataMember(56)]
        [DataMemberRange(0.5, 4.0, 0.1, 0.5, 2)]
        public float SpecularOffset { get; set; } = 1.0f;

        /// <summary>
        /// Trace the diffuse cones into a buffer this many times smaller than the screen along each
        /// axis, instead of once per shaded pixel: 1 marches inline, 2 traces a quarter of the
        /// cones, 4 a sixteenth.
        /// </summary>
        /// <remarks>
        /// The shaded pixel reads that buffer, weighting its taps by depth so the reduction does not
        /// drag light across a silhouette. Bounced light is low frequency, so the resolution it is
        /// traced at costs far less than the resolution it is applied at. This is the knob for a
        /// machine that cannot afford the cones at all: it needs a depth-only render stage on the
        /// compositor to prime the depth buffer, and falls back to marching inline (with one warning
        /// in the log) when there is none.
        /// </remarks>
        [DataMember(57)]
        [DataMemberRange(1, 4, 1, 1, 0)]
        public int ScreenSpaceDivisor { get; set; } = 1;

        public bool Update(RenderLight light)
        {
            return true;
        }
    }
}
