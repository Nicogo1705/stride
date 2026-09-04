// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using Stride.Core;
using Stride.Core.Diagnostics;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering.ComputeEffect;
using Stride.Rendering.Materials;
using GraphicsBuffer = Stride.Graphics.Buffer;

namespace Stride.Rendering.Voxels.Grid
{
    /// <summary>One field the GI takes straight from its samples.</summary>
    public struct VoxelGridInjectionEntry
    {
        /// <summary>The traversal that samples the field.</summary>
        public IVoxelGridTraversal Traversal;
        /// <summary>The field's world matrix.</summary>
        public Matrix World;
        /// <summary>The field's extent in its own units.</summary>
        public Vector3 Extent;
        /// <summary>The material table: colour and emission per id.</summary>
        public GraphicsBuffer Table;

        /// <summary>How much of the previous frame's light the field's colour bounces back; zero for emission only.</summary>
        public float Bounce;
    }

    /// <summary>
    /// The colour and emission of each material id, as the injector reads them.
    /// </summary>
    /// <remarks>
    /// Read off the materials' constant parameters, two float4 per id, and uploaded again only
    /// when a value changed - so a material edited at runtime reaches the GI on the next frame.
    /// </remarks>
    public sealed class VoxelGridInjectionTable : IDisposable
    {
        /// <summary>Material ids are a byte: this many entries.</summary>
        public const int Capacity = 256;

        /// <summary>The device buffer the injector reads: two float4 per id.</summary>
        public GraphicsBuffer Buffer { get; }

        private readonly Vector4[] entries = new Vector4[Capacity * 2];
        private readonly Vector4[] scratch = new Vector4[Capacity * 2];
        private bool uploaded;

        /// <summary>Allocates the table; nothing is uploaded until <see cref="Refresh"/>.</summary>
        public VoxelGridInjectionTable(GraphicsDevice device)
        {
            Buffer = GraphicsBuffer.Structured.New(device, Capacity * 2, 16);
        }

        /// <summary>Reads the materials and uploads the table when anything in it changed.</summary>
        public void Refresh(CommandList commandList, IReadOnlyList<Material> materials)
        {
            Array.Clear(scratch);
            for (int id = 0; id < Capacity && id < materials.Count; id++)
            {
                var parameters = materials[id]?.Passes.Count > 0 ? materials[id].Passes[0].Parameters : null;
                var colour = parameters != null && parameters.ContainsKey(MaterialKeys.DiffuseValue) ? parameters.Get(MaterialKeys.DiffuseValue) : new Color4(1, 1, 1, 1);
                var emissive = parameters != null && parameters.ContainsKey(MaterialKeys.EmissiveValue) ? parameters.Get(MaterialKeys.EmissiveValue) : new Color4(0, 0, 0, 0);
                var intensity = parameters != null && parameters.ContainsKey(MaterialKeys.EmissiveIntensity) ? parameters.Get(MaterialKeys.EmissiveIntensity) : 0f;

                scratch[id * 2] = new Vector4(colour.R, colour.G, colour.B, 1);
                scratch[id * 2 + 1] = new Vector4(emissive.R * intensity, emissive.G * intensity, emissive.B * intensity, 1);
            }

            var same = uploaded;
            for (int i = 0; same && i < entries.Length; i++)
                same = entries[i] == scratch[i];
            if (same)
                return;

            Array.Copy(scratch, entries, entries.Length);
            Buffer.SetData(commandList, entries);
            uploaded = true;
        }

        /// <summary>Releases the device buffer.</summary>
        public void Dispose() => Buffer.Dispose();
    }

    /// <summary>The fields to inject this frame, as the grid processor lists them on the visibility group.</summary>
    /// <remarks>A class round the list because a property key's type must serialize, and a list of entries holding a device buffer must not try.</remarks>
    [DataContract]
    public sealed class VoxelGridInjectionList
    {
        /// <summary>The fields, cleared and refilled by the processor every frame.</summary>
        [DataMemberIgnore]
        public readonly List<VoxelGridInjectionEntry> Entries = [];
    }

    /// <summary>
    /// Writes voxel fields into the GI's fragment buffer straight from their samples, where the
    /// voxelizer would have rasterised their proxy boxes. See <c>VoxelGridInjectShader</c>.
    /// </summary>
    /// <remarks>
    /// One per <see cref="VoxelRenderer"/>, which owns its shader; the fields to inject reach it
    /// through the visibility group, listed there by the grid processor each frame, the way the
    /// volumes themselves reach the renderer.
    /// </remarks>
    public sealed class VoxelGridInjector : IDisposable
    {
        /// <summary>The fields to inject this frame, listed by the grid processor on the visibility group.</summary>
        public static readonly PropertyKey<VoxelGridInjectionList> CurrentEntries = new("VoxelGridInjector.CurrentEntries", typeof(VoxelGridInjector));

        private static readonly ProfilingKey ProfilingKey = new("Voxelization: Field injection");

        private ComputeEffectShader shader;
        private bool warnedPacker;

        /// <summary>
        /// Injects every listed field into the rings a voxelization pass just rasterised, so the
        /// arrangement that follows finds them in the buffer beside the meshes' fragments.
        /// </summary>
        public void Inject(RenderDrawContext context, VoxelizationPass pass, IReadOnlyList<VoxelGridInjectionEntry> entries)
        {
            if (entries == null || entries.Count == 0 || pass.storer is not VoxelStorerClipmap storer || storer.FragmentsBuffer == null)
                return;

            shader ??= new ComputeEffectShader(context.RenderContext) { ShaderSourceName = "VoxelGridInjectEffect" };

            using var profile = context.QueryManager.BeginProfile(Color.Black, ProfilingKey);

            var resolution = new Int3((int)storer.ClipMapResolution.X, (int)storer.ClipMapResolution.Y, (int)storer.ClipMapResolution.Z);
            var single = storer.UpdatesOneClipPerFrame();
            var firstRing = single ? storer.ClipMapCurrent : 0;
            var lastRing = single ? storer.ClipMapCurrent : storer.ClipMapCount - 1;

            foreach (var attribute in pass.AttributesIndirect)
            {
                if (attribute is not VoxelAttributeEmissionOpacity emission || emission.VoxelLayout is not VoxelLayoutBase layout)
                    continue;

                // The packing is the packer's; this shader packs as the default one does.
                if (layout.StorageMethod is VoxelStorageMethodIndirect { TempStorageFormat: not VoxelFragmentPackFloatR11G11B10 })
                {
                    if (!warnedPacker)
                        GlobalLogger.GetLogger("VoxelGridInjector").Warning("Field injection packs fragments as VoxelFragmentPackFloatR11G11B10; the volume's packer differs, so its fields are left to the voxelizer.");
                    warnedPacker = true;
                    continue;
                }

                for (int ring = firstRing; ring <= lastRing; ring++)
                {
                    var slot = single ? ring % storer.FragmentSlots : ring;
                    foreach (var entry in entries)
                    {
                        if (entry.Traversal?.Source == null || entry.Table == null)
                            continue;

                        var world = entry.World;
                        Matrix.Invert(ref world, out var worldInverse);

                        var parameters = shader.Parameters;
                        entry.Traversal.ApplyParameters(parameters);
                        parameters.Set(VoxelGridInjectShaderKeys.Traversal, entry.Traversal.GetShaderSource());
                        parameters.Set(VoxelGridInjectShaderKeys.VoxelFragments, storer.FragmentsBuffer);
                        parameters.Set(VoxelGridInjectShaderKeys.VoxelGridInjectTable, entry.Table);
                        parameters.Set(VoxelGridInjectShaderKeys.VoxelGridWorld, world);
                        parameters.Set(VoxelGridInjectShaderKeys.VoxelGridWorldInverse, worldInverse);
                        parameters.Set(VoxelGridInjectShaderKeys.VoxelGridExtent, entry.Extent);
                        parameters.Set(VoxelGridInjectShaderKeys.ClipResolution, resolution);
                        parameters.Set(VoxelGridInjectShaderKeys.ClipSlot, slot);
                        parameters.Set(VoxelGridInjectShaderKeys.StorageUints, storer.storageUints);
                        parameters.Set(VoxelGridInjectShaderKeys.BufferOffset, emission.BufferOffset);
                        parameters.Set(VoxelGridInjectShaderKeys.DirectionCount, layout.DirectionCount);
                        parameters.Set(VoxelGridInjectShaderKeys.ClipOffsetScale, storer.PerMapOffsetScale[ring]);

                        // The bounce reads the previous frame's rings, one coarser than this one
                        // where there is one - a blurrier answer, which is what an irradiance is.
                        var previous = layout.StorageTexture as VoxelStorageTextureClipmap;
                        var bounceRing = Math.Min(ring + 1, storer.ClipMapCount - 1);
                        var bounce = previous?.ClipMaps != null && entry.Bounce > 0 ? layout.maxBrightness * entry.Bounce : 0f;
                        parameters.Set(VoxelGridInjectShaderKeys.VoxelGridPreviousClipMaps, previous?.ClipMaps);
                        parameters.Set(VoxelGridInjectShaderKeys.BounceRing, bounceRing);
                        parameters.Set(VoxelGridInjectShaderKeys.BounceOffsetScale, previous != null ? previous.PerMapOffsetScaleCurrent[bounceRing] : Vector4.Zero);
                        parameters.Set(VoxelGridInjectShaderKeys.ClipMapCount, storer.ClipMapCount);
                        parameters.Set(VoxelGridInjectShaderKeys.BounceScale, bounce);

                        shader.ThreadNumbers = new Int3(8, 8, 8);
                        shader.ThreadGroupCounts = new Int3((resolution.X + 7) / 8, (resolution.Y + 7) / 8, (resolution.Z + 7) / 8);
                        shader.Draw(context);
                    }
                }
            }
        }

        /// <summary>Releases the compute shader.</summary>
        public void Dispose()
        {
            shader?.Dispose();
            shader = null;
        }
    }
}
