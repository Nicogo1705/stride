// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering.Voxels;
using Stride.Rendering.Voxels.VoxelGI;

namespace VoxelTerrain;

/// <summary>How much the indirect light costs: the clipmap resolution, how many directions a voxel keeps, and how many cones a pixel traces.</summary>
public enum GIQuality
{
    /// <summary>64³ isotropic rings, 6 short cones at half the screen. For a small GPU.</summary>
    Low,
    /// <summary>128³ isotropic rings, 6 cones at half the screen.</summary>
    Medium,
    /// <summary>128³ paired rings, 12 long cones at full resolution.</summary>
    High,
}

/// <summary>
/// Voxel cone tracing GI around the camera, from the engine's own pieces: a
/// <see cref="VoxelVolumeComponent"/> that keeps a clipmap of rings voxelized around an entity,
/// and a <see cref="LightVoxel"/> environment light that cone-traces those rings back as bounced
/// light and reflections. The terrain writes itself into the rings directly (the grid's
/// InjectIntoGI), so nothing is rasterized for it.
/// </summary>
public static class VoxelGI
{
    /// <summary>
    /// A volume that follows a transform, sized for an open field: the finest ring keeps a few
    /// metres around the camera and each further ring covers twice the distance, so the far field
    /// is lit at a coarser voxel rather than not at all.
    /// </summary>
    /// <param name="volumeSize">Edge of the outermost ring, in world units.</param>
    /// <param name="rings">Nested rings; each halves the voxel size of the one outside it.</param>
    public static Entity Create(TransformComponent follow, float volumeSize, int rings, GIQuality quality, Color3 sky, float skyIntensity)
    {
        var (resolution, paired, cones, steps, specularSteps, specularRatio, msaa, divisor, roughnessCutoff) = quality switch
        {
            GIQuality.Low => (VoxelStorageClipmaps.Resolutions.x64, false, 6, 5, 16, 1.0f, MultisampleCount.X2, 2, 0.7f),
            GIQuality.High => (VoxelStorageClipmaps.Resolutions.x128, true, 12, 12, 60, 0.6f, MultisampleCount.X8, 1, 1.0f),
            _ => (VoxelStorageClipmaps.Resolutions.x128, false, 6, 7, 30, 1.0f, MultisampleCount.X4, 2, 0.9f),
        };
        rings = Math.Clamp(rings, 1, VoxelStorageClipmaps.MaxClipMapCount(resolution));

        VoxelLayoutBase layout = paired ? new VoxelLayoutAnisotropicPaired() : new VoxelLayoutIsotropic();
        layout.StorageFormat = VoxelLayoutBase.StorageFormats.RGBA16F;
        var attribute = new VoxelAttributeEmissionOpacity
        {
            VoxelLayout = (IVoxelLayout)layout,
            // Radiance halves per mip rather than falling with the fill count; the bounce below is doubled to match.
            LightFalloff = VoxelAttributeEmissionOpacity.LightFalloffs.PhysicallyBased,
        };
        // Thin geometry is thickened a little so a wall a voxel wide still occludes.
        attribute.Modifiers.Add(new VoxelModifierEmissionOpacityOpacify { Amount = 1f });

        var volume = new VoxelVolumeComponent
        {
            VoxelizationMethod = new VoxelizationMethodDominantAxis { MultisampleCount = msaa },
            Storage = new VoxelStorageClipmaps
            {
                ClipResolution = resolution,
                UpdatesPerFrame = VoxelStorageClipmaps.UpdateMethods.SingleClipmap,
                DownsampleFinerClipMaps = true,
            },
            VoxelGridSnapping = true,
            VoxelVolumeSize = volumeSize,
            // The ring count comes from the voxel size: the finest ring is the volume halved (rings - 1) times.
            AproximateVoxelSize = volumeSize / (1 << (rings - 1)) / (int)resolution,
        };
        volume.Attributes.Add(attribute);

        // The cones start a few voxels out from the surface: nearer, and a smooth surface grazes its
        // own voxel shell at fixed angles as the cones climb the mips, and wears rings.
        const float coneOffset = 4f;
        var diffuse = new VoxelMarchConePerMipmap(1.0f, steps);
        IVoxelMarchSet diffuseSet = cones >= 12 ? new VoxelMarchSetHemisphere12(diffuse) : new VoxelMarchSetHemisphere6(diffuse);
        ((VoxelMarchSetBase)diffuseSet).Offset = coneOffset;

        var light = new LightComponent
        {
            // One bounce loses energy; with the physically based falloff, two is what reads as lit.
            Intensity = 2f,
            Type = new LightVoxel
            {
                Volume = volume,
                AttributeIndex = 0,
                DiffuseMarcher = diffuseSet,
                SpecularMarcher = new VoxelMarchCone(specularSteps, 0.5f, specularRatio, 24f),
                // Re-inject the previous frame's lit voxels, so light bounces more than once.
                BounceIntensityScale = 1f,
                SpecularIntensityScale = 1f,
                SpecularRoughnessCutoff = roughnessCutoff,
                SpecularOffset = coneOffset,
                SkyColor = sky,
                SkyIntensity = skyIntensity,
                // Trace the diffuse cones at 1/N of the screen; the compositor's depth-only stage lets the resolver upsample.
                ScreenSpaceDivisor = divisor,
            },
        };

        var entity = new Entity("Voxel GI") { volume, new FollowTransform { Target = follow } };
        entity.AddChild(new Entity("Voxel GI Light") { light });
        return entity;
    }
}

/// <summary>Keeps an entity at another transform's position, without taking its rotation.</summary>
public sealed class FollowTransform : SyncScript
{
    public TransformComponent? Target { get; set; }

    public override void Update()
    {
        if (Target is null)
            return;
        Target.UpdateWorldMatrix();
        Entity.Transform.Position = Target.WorldMatrix.TranslationVector;
    }
}
