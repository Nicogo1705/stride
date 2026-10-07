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
        /// The cone set traced while voxelizing, when <see cref="BounceIntensityScale"/> feeds indirect light back into the voxels.
        /// Null uses <see cref="DiffuseMarcher"/>.
        /// </summary>
        /// <remarks>
        /// Bounce light is averaged into voxels and mipmapped before it is read, so a cheaper set is enough here.
        /// Must be a distinct instance from <see cref="DiffuseMarcher"/>: a marcher holds one set of composed parameter keys.
        /// </remarks>
        [DataMember(35)]
        public IVoxelMarchSet BounceMarcher { get; set; }

        [DataMember(40)]
        public float BounceIntensityScale { get; set; }
        [DataMember(50)]
        public float SpecularIntensityScale { get; set; }

        /// <summary>
        /// Roughness above which the specular cone is not traced; faded out over a small window below the cutoff.
        /// 1 (the default) traces every surface.
        /// </summary>
        [DataMember(55)]
        [DataMemberRange(0.0, 1.0, 0.01, 0.1, 2)]
        public float SpecularRoughnessCutoff { get; set; } = 1.0f;

        /// <summary>
        /// Distance along the normal, in voxels, from which the specular cone starts.
        /// Starting further out keeps the cone from reading the surface's own voxel shell.
        /// </summary>
        [DataMember(56)]
        [DataMemberRange(0.5, 4.0, 0.1, 0.5, 2)]
        public float SpecularOffset { get; set; } = 1.0f;

        /// <summary>
        /// Radiance credited to a cone that leaves the volume or runs out of steps without hitting anything,
        /// scaled by the fraction of the cone still open. Black by default.
        /// </summary>
        [DataMember(57)]
        public Color3 SkyColor { get; set; } = new Color3(0, 0, 0);

        /// <summary>Multiplier on <see cref="SkyColor"/>.</summary>
        [DataMember(58)]
        [DataMemberRange(0.0, 10.0, 0.05, 0.5, 2)]
        public float SkyIntensity { get; set; } = 1.0f;

        /// <summary>
        /// Traces the diffuse cones into a buffer this many times smaller than the screen along each axis.
        /// 1 marches per shaded pixel.
        /// </summary>
        /// <remarks>
        /// Requires a depth-only render stage on the compositor to prime the depth buffer; without one
        /// the light marches inline and logs one warning.
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
