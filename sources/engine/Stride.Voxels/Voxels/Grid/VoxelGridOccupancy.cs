// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using Stride.Core.Mathematics;
using Stride.Graphics;

namespace Stride.Rendering.Voxels.Grid
{
    /// <summary>Density of one sample, as the field the grid is drawn from would read it.</summary>
    public delegate float VoxelDensityReader(int x, int y, int z);

    /// <summary>
    /// The least and greatest density in every brick of a grid, at every brick size, so a ray can
    /// leap over the bricks that hold no surface.
    /// </summary>
    /// <remarks>
    /// A min/max pyramid held as the mip chain of a 3D texture: level 0 is bricks of two cells, each level doubles
    /// the brick. A brick whose maximum is below the iso level holds no surface and is crossed in one step.
    /// Bricks overlap by one sample, since a cell's surface depends on its eight corners; the overlap is what makes
    /// skipping safe. Built on the CPU from the raw samples and rebuilt only for the box an edit touched. The sealed
    /// border is not baked in: sealing only lowers densities, so the pyramid errs towards looking, never skipping.
    /// </remarks>
    public sealed class VoxelGridOccupancy : IDisposable
    {
        /// <summary>The pyramid: min in red, max in green, one mip per level, bricks of two cells at the base.</summary>
        public Texture Texture { get; }

        /// <summary>Levels in the pyramid. Level 1 is bricks of two cells; level N bricks of 2^N.</summary>
        public int Levels { get; }

        /// <summary>Samples along each axis of the grid this covers.</summary>
        public Int3 SampleCount { get; }

        // Per level, min and max interleaved, one pair per brick, x fastest as the texture is laid out.
        private readonly byte[][] levels;
        private readonly int[] sizes;

        /// <summary>Allocates the pyramid for a field of that many samples; nothing is built until the first update.</summary>
        public VoxelGridOccupancy(GraphicsDevice device, Int3 sampleCount)
        {
            SampleCount = sampleCount;

            // A power of two over the cells, so every level divides cleanly and no brick index ever
            // falls off the end of a mip that rounded down. Cells beyond the grid read as air.
            var cells = Math.Max(Math.Max(sampleCount.X, sampleCount.Y), sampleCount.Z) - 1;
            var baseSize = 1;
            while (baseSize * 2 < cells)
                baseSize *= 2;

            Levels = 1;
            for (var size = baseSize; size > 1; size /= 2)
                Levels++;

            sizes = new int[Levels];
            levels = new byte[Levels][];
            for (int level = 0; level < Levels; level++)
            {
                sizes[level] = Math.Max(1, baseSize >> level);
                levels[level] = new byte[sizes[level] * sizes[level] * sizes[level] * 2];
            }

            Texture = Texture.New3D(device, baseSize, baseSize, baseSize, Levels, PixelFormat.R8G8_UNorm, TextureFlags.ShaderResource, GraphicsResourceUsage.Default);
        }

        /// <summary>Rebuilds the whole pyramid from the samples.</summary>
        public void Update(CommandList commandList, VoxelDensityReader density)
            => Update(commandList, density, Int3.Zero, SampleCount - Int3.One);

        /// <summary>Rebuilds the bricks that cover the samples in a box, inclusive, at every level, and uploads only those.</summary>
        public void Update(CommandList commandList, VoxelDensityReader density, Int3 minSample, Int3 maxSample)
        {
            var samples = SampleCount;
            minSample = Int3.Max(minSample, Int3.Zero);
            maxSample = Int3.Min(maxSample, samples - Int3.One);
            if (minSample.X > maxSample.X || minSample.Y > maxSample.Y || minSample.Z > maxSample.Z)
                return;

            // A base brick b covers samples 2b to 2b + 2 inclusive, so the bricks a sample range
            // touches run from (min - 2) / 2 rounded up to max / 2.
            var first = Int3.Max((minSample - new Int3(1)) / 2, Int3.Zero);
            var last = Int3.Min(maxSample / 2, new Int3(sizes[0] - 1));

            for (int bz = first.Z; bz <= last.Z; bz++)
                for (int by = first.Y; by <= last.Y; by++)
                    for (int bx = first.X; bx <= last.X; bx++)
                    {
                        var low = 1f;
                        var high = 0f;
                        for (int z = 0; z <= 2; z++)
                            for (int y = 0; y <= 2; y++)
                                for (int x = 0; x <= 2; x++)
                                {
                                    var sx = bx * 2 + x;
                                    var sy = by * 2 + y;
                                    var sz = bz * 2 + z;
                                    var value = sx < samples.X && sy < samples.Y && sz < samples.Z ? density(sx, sy, sz) : 0f;
                                    low = Math.Min(low, value);
                                    high = Math.Max(high, value);
                                }

                        Write(0, bx, by, bz, low, high);
                    }

            Upload(commandList, 0, first, last);

            // Each level above is the union of its eight children, which the overlap makes exact.
            for (int level = 1; level < Levels; level++)
            {
                first /= 2;
                last /= 2;
                var child = levels[level - 1];
                var childSize = sizes[level - 1];

                for (int bz = first.Z; bz <= last.Z; bz++)
                    for (int by = first.Y; by <= last.Y; by++)
                        for (int bx = first.X; bx <= last.X; bx++)
                        {
                            byte low = 255;
                            byte high = 0;
                            for (int z = 0; z <= 1; z++)
                                for (int y = 0; y <= 1; y++)
                                    for (int x = 0; x <= 1; x++)
                                    {
                                        var cx = Math.Min(bx * 2 + x, childSize - 1);
                                        var cy = Math.Min(by * 2 + y, childSize - 1);
                                        var cz = Math.Min(bz * 2 + z, childSize - 1);
                                        var index = ((cz * childSize + cy) * childSize + cx) * 2;
                                        low = Math.Min(low, child[index]);
                                        high = Math.Max(high, child[index + 1]);
                                    }

                            var target = ((bz * sizes[level] + by) * sizes[level] + bx) * 2;
                            levels[level][target] = low;
                            levels[level][target + 1] = high;
                        }

                Upload(commandList, level, first, last);
            }
        }

        private void Write(int level, int bx, int by, int bz, float low, float high)
        {
            // Rounded outwards, so a brick never claims to be emptier than it is.
            var index = ((bz * sizes[level] + by) * sizes[level] + bx) * 2;
            levels[level][index] = (byte)Math.Clamp(MathF.Floor(low * 255f), 0f, 255f);
            levels[level][index + 1] = (byte)Math.Clamp(MathF.Ceiling(high * 255f), 0f, 255f);
        }

        private void Upload(CommandList commandList, int level, Int3 first, Int3 last)
        {
            var size = sizes[level];
            var extent = last - first + Int3.One;
            var source = levels[level];

            var box = new byte[extent.X * extent.Y * extent.Z * 2];
            var rowBytes = extent.X * 2;
            for (int z = 0; z < extent.Z; z++)
                for (int y = 0; y < extent.Y; y++)
                {
                    var from = (((first.Z + z) * size + first.Y + y) * size + first.X) * 2;
                    var to = (z * extent.Y + y) * rowBytes;
                    System.Buffer.BlockCopy(source, from, box, to, rowBytes);
                }

            var region = new ResourceRegion(first.X, first.Y, first.Z, last.X + 1, last.Y + 1, last.Z + 1);
            Texture.SetData(commandList, box, 0, level, region);
        }

        /// <summary>Releases the pyramid texture.</summary>
        public void Dispose() => Texture.Dispose();
    }
}
