// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using Stride.Core.Diagnostics;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering.ComputeEffect;
using Stride.Rendering.Materials;
using Stride.Rendering.Voxels.VoxelGI;
using GraphicsBuffer = Stride.Graphics.Buffer;

namespace Stride.Rendering.Voxels.Grid
{
    /// <summary>One field the GI takes straight from its samples.</summary>
    public struct VoxelGridInjectionEntry
    {
        public IVoxelGridTraversal Traversal;
        public Matrix World;
        public Vector3 Extent;
        public GraphicsBuffer Table;
    }

    /// <summary>
    /// The colour and emission of each material id, as the injector reads them: read off the
    /// materials' constant parameters, two float4 per id.
    /// </summary>
    public sealed class VoxelGridInjectionTable : IDisposable
    {
        public const int Capacity = 256;

        public GraphicsBuffer Buffer { get; }

        private readonly Vector4[] entries = new Vector4[Capacity * 2];
        private readonly List<Material> last = [];

        public VoxelGridInjectionTable(GraphicsDevice device)
        {
            Buffer = GraphicsBuffer.Structured.New(device, Capacity * 2, 16);
        }

        public bool NeedsUpdate(IReadOnlyList<Material> materials)
        {
            if (materials.Count != last.Count)
                return true;
            for (int i = 0; i < materials.Count; i++)
                if (!ReferenceEquals(materials[i], last[i]))
                    return true;
            return false;
        }

        public void Update(CommandList commandList, IReadOnlyList<Material> materials)
        {
            Array.Clear(entries);
            last.Clear();
            for (int id = 0; id < Capacity; id++)
            {
                var material = id < materials.Count ? materials[id] : null;
                if (id < materials.Count)
                    last.Add(material);

                var parameters = material?.Passes.Count > 0 ? material.Passes[0].Parameters : null;
                var colour = parameters != null && parameters.ContainsKey(MaterialKeys.DiffuseValue) ? parameters.Get(MaterialKeys.DiffuseValue) : new Color4(1, 1, 1, 1);
                var emissive = parameters != null && parameters.ContainsKey(MaterialKeys.EmissiveValue) ? parameters.Get(MaterialKeys.EmissiveValue) : new Color4(0, 0, 0, 0);
                var intensity = parameters != null && parameters.ContainsKey(MaterialKeys.EmissiveIntensity) ? parameters.Get(MaterialKeys.EmissiveIntensity) : 0f;

                entries[id * 2] = new Vector4(colour.R, colour.G, colour.B, 1);
                entries[id * 2 + 1] = new Vector4(emissive.R * intensity, emissive.G * intensity, emissive.B * intensity, 1);
            }
            Buffer.SetData(commandList, entries);
        }

        public void Dispose() => Buffer.Dispose();
    }

    /// <summary>
    /// Writes voxel fields into the GI's fragment buffer straight from their samples, where the
    /// voxelizer would have rasterised their proxy boxes. See <c>VoxelGridInjectShader</c>.
    /// </summary>
    public static class VoxelGridInjection
    {
        /// <summary>The fields to inject this frame. Filled by the grid processor before the GI draws.</summary>
        public static List<VoxelGridInjectionEntry> Entries { get; } = [];

        private static readonly ProfilingKey ProfilingKey = new("Voxelization: Field injection");

        private static ComputeEffectShader shader;
        private static bool warnedPacker;

        /// <summary>
        /// Injects every field into the rings a voxelization pass just rasterised, so the arrangement
        /// that follows finds them in the buffer beside the meshes' fragments.
        /// </summary>
        public static void Inject(RenderDrawContext context, VoxelizationPass pass)
        {
            if (Entries.Count == 0 || pass.storer is not VoxelStorerClipmap storer || storer.FragmentsBuffer == null)
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
                        GlobalLogger.GetLogger("VoxelGridInjection").Warning("Field injection packs fragments as VoxelFragmentPackFloatR11G11B10; the volume's packer differs, so its fields are left to the voxelizer.");
                    warnedPacker = true;
                    continue;
                }

                for (int ring = firstRing; ring <= lastRing; ring++)
                {
                    var slot = single ? ring % storer.FragmentSlots : ring;
                    foreach (var entry in Entries)
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

                        shader.ThreadNumbers = new Int3(8, 8, 8);
                        shader.ThreadGroupCounts = new Int3((resolution.X + 7) / 8, (resolution.Y + 7) / 8, (resolution.Z + 7) / 8);
                        shader.Draw(context);
                    }
                }
            }
        }
    }
}
