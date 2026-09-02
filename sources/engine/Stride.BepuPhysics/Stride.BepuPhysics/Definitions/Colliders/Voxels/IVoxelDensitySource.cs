// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Runtime.CompilerServices;
using BepuUtilities.Memory;

namespace Stride.BepuPhysics.Definitions.Colliders.Voxels;

/// <summary>
/// Where a voxel collidable reads its density from.
/// </summary>
/// <remarks>
/// <para>
/// Every game packs its voxels differently - a byte of density, a float, a density byte and a
/// material byte in one <see cref="ushort"/>, or separate arrays for each - and none of those
/// choices concern collision, which only ever asks one question: how solid is this sample. So the
/// packing is a type parameter, and the three built-in sources below are examples rather than a
/// closed set. A game with two parallel arrays supplies only the density one.
/// </para>
/// <para>
/// Implementations live in unmanaged memory alongside the shape - Bepu keeps shapes in its own
/// pools and hands them back as raw pointers - so a source may hold a <see cref="Buffer{T}"/> or a
/// raw pointer, and nothing managed. Density is read on the physics thread, once per sample per
/// query, so keep it to an index and a load.
/// </para>
/// <para>
/// Each source reserves three consecutive Bepu shape type ids through
/// <see cref="ShapeTypeIdBase"/>, one for each of the box, sphere and triangle shapes built over it.
/// Bepu's built-in ids end at 8 (Mesh), the sources here take 12 through 20, and a game defining its
/// own picks anything free and unique within its simulation.
/// </para>
/// </remarks>
public interface IVoxelDensitySource
{
    /// <summary>
    /// First of three consecutive shape type ids reserved for this source: base + 0 for the box
    /// shape, + 1 for the sphere shape, + 2 for the triangle shape.
    /// </summary>
    static abstract int ShapeTypeIdBase { get; }

    /// <summary>Samples along X. One more than the number of cells.</summary>
    int SamplesX { get; }
    /// <summary>Samples along Y. One more than the number of cells.</summary>
    int SamplesY { get; }
    /// <summary>Samples along Z. One more than the number of cells.</summary>
    int SamplesZ { get; }

    /// <summary>
    /// Density of one sample, normalized so that the collider's iso level is comparable against it.
    /// Coordinates are clamped in range before this is called, so implementations do not check.
    /// </summary>
    float Density(int x, int y, int z);
}

/// <summary>
/// Density and material packed one per <see cref="ushort"/>, density in bits 0-7 and material in
/// bits 8-15, laid out x-major with z varying fastest.
/// </summary>
/// <remarks>
/// The layout a voxel game commonly uses to upload a chunk to the GPU, so the same array can serve
/// rendering and collision. The material half is ignored here; collision has no use for it.
/// </remarks>
public struct PackedVoxelSource : IVoxelDensitySource
{
    public static int ShapeTypeIdBase => 12;

    /// <summary>Packed samples. Density is the low byte.</summary>
    public Buffer<ushort> Samples;

    public int SamplesX { get; set; }
    public int SamplesY { get; set; }
    public int SamplesZ { get; set; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly float Density(int x, int y, int z)
        => (Samples[(x * SamplesY + y) * SamplesZ + z] & 0xFF) * (1f / 255f);

    /// <summary>Material half of a sample, for callers that want it; unused by collision.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly byte Material(int x, int y, int z)
        => (byte)(Samples[(x * SamplesY + y) * SamplesZ + z] >> 8);
}

/// <summary>
/// One byte of density per sample, x-major with z varying fastest.
/// </summary>
/// <remarks>
/// Also the source to use for a game keeping density and material in two parallel arrays: hand over
/// the density one and leave the other out of physics entirely.
/// </remarks>
public struct ByteVoxelSource : IVoxelDensitySource
{
    public static int ShapeTypeIdBase => 15;

    public Buffer<byte> Samples;

    public int SamplesX { get; set; }
    public int SamplesY { get; set; }
    public int SamplesZ { get; set; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly float Density(int x, int y, int z)
        => Samples[(x * SamplesY + y) * SamplesZ + z] * (1f / 255f);
}

/// <summary>
/// One float of density per sample, x-major with z varying fastest, used as is.
/// </summary>
/// <remarks>
/// The natural source for a signed distance field or any generator working in continuous values.
/// Whatever range it produces, the collider's iso level is compared against it directly, so a field
/// centred on zero simply takes an iso level of zero.
/// </remarks>
public struct FloatVoxelSource : IVoxelDensitySource
{
    public static int ShapeTypeIdBase => 18;

    public Buffer<float> Samples;

    public int SamplesX { get; set; }
    public int SamplesY { get; set; }
    public int SamplesZ { get; set; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly float Density(int x, int y, int z)
        => Samples[(x * SamplesY + y) * SamplesZ + z];
}
