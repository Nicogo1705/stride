// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering.Materials;
using GraphicsBuffer = Stride.Graphics.Buffer;

namespace Stride.Rendering.Voxels.Grid
{
    /// <summary>
    /// The materials a voxel grid's samples point at, as the shaders read them: one entry per
    /// material id, taken from ordinary <see cref="Material"/> assets.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A sample carries an id, a byte, and the id is an index into this. The user lists materials
    /// on the component and authors them as they author any material; what the field's shaders
    /// need from each - its colour, how rough it is, how metallic, what it emits - is read off the
    /// material's parameters here and packed into a small buffer, three float4 per entry, that the
    /// traversal blends between the eight corners of the cell a ray landed in.
    /// </para>
    /// <para>
    /// Read off the parameters, not the asset: a material feature fed a constant leaves that
    /// constant under a known key (<see cref="MaterialKeys.DiffuseValue"/> and its kin), which is
    /// as true of a material loaded from an asset as of one built at runtime. A feature fed a
    /// texture leaves no such value; it falls back on the default below, since a byte per sample
    /// has no texture coordinates to offer it.
    /// </para>
    /// </remarks>
    public sealed class VoxelGridPalette : IDisposable
    {
        /// <summary>Ids a byte can hold.</summary>
        public const int Capacity = 256;

        /// <summary>float4 per entry: colour; emissive with intensity in w; glossiness, metalness.</summary>
        public const int Stride = 3;

        /// <summary>The entries, laid out as <see cref="Stride"/> float4 per id.</summary>
        public GraphicsBuffer Buffer { get; }

        private readonly Vector4[] entries = new Vector4[Capacity * Stride];
        private readonly List<Material> lastMaterials = [];

        public VoxelGridPalette(GraphicsDevice device)
        {
            Buffer = GraphicsBuffer.Structured.New(device, Capacity * Stride, 16);
        }

        /// <summary>
        /// Whether the list differs from what was last uploaded, by membership: a material edited in
        /// place is a new object once the asset reloads, so reference equality is enough.
        /// </summary>
        public bool NeedsUpdate(IReadOnlyList<Material> materials)
        {
            if (materials.Count != lastMaterials.Count)
                return true;
            for (int i = 0; i < materials.Count; i++)
                if (!ReferenceEquals(materials[i], lastMaterials[i]))
                    return true;
            return false;
        }

        /// <summary>Reads the materials' parameters and uploads the whole table.</summary>
        public void Update(CommandList commandList, IReadOnlyList<Material> materials)
        {
            Array.Clear(entries);
            lastMaterials.Clear();

            for (int id = 0; id < Capacity; id++)
            {
                var material = id < materials.Count ? materials[id] : null;
                if (id < materials.Count)
                    lastMaterials.Add(material);

                var parameters = material?.Passes.Count > 0 ? material.Passes[0].Parameters : null;

                var colour = parameters?.Get(MaterialKeys.DiffuseValue) ?? new Color4(1, 1, 1, 1);
                var glossiness = parameters?.Get(MaterialKeys.GlossinessValue) ?? 0f;
                var metalness = parameters?.Get(MaterialKeys.MetalnessValue) ?? 0f;
                var emissive = parameters?.Get(MaterialKeys.EmissiveValue) ?? new Color4(0, 0, 0, 0);
                var intensity = parameters?.Get(MaterialKeys.EmissiveIntensity) ?? 0f;

                entries[id * Stride + 0] = new Vector4(colour.R, colour.G, colour.B, 1);
                entries[id * Stride + 1] = new Vector4(emissive.R, emissive.G, emissive.B, intensity);
                entries[id * Stride + 2] = new Vector4(glossiness, metalness, 0, 0);
            }

            Buffer.SetData(commandList, entries);
        }

        public void Dispose() => Buffer.Dispose();
    }
}
