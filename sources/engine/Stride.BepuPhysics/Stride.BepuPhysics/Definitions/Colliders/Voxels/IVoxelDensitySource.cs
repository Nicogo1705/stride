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
/// The sample packing is a type parameter; the built-in sources below are examples, not a closed set.
/// Implementations live in unmanaged memory next to the shape and may hold a <see cref="Buffer{T}"/>
/// or a raw pointer, nothing managed. Density is read on the physics thread once per sample per
/// query, so keep it to an index and a load.
/// </para>
/// <para>
/// Each source reserves three consecutive Bepu shape type ids from <see cref="ShapeTypeIdBase"/>.
/// Bepu's built-in ids end at 8; the sources here use 12 through 20.
/// </para>
/// </remarks>
public interface IVoxelDensitySource
{
    /// <summary>First of three consecutive shape type ids: base + 0 box, + 1 sphere, + 2 triangle.</summary>
    static abstract int ShapeTypeIdBase { get; }

    /// <summary>Samples along X. One more than the number of cells.</summary>
    int SamplesX { get; }
    /// <summary>Samples along Y. One more than the number of cells.</summary>
    int SamplesY { get; }
    /// <summary>Samples along Z. One more than the number of cells.</summary>
    int SamplesZ { get; }

    /// <summary>Density of one sample, comparable against the collider's iso level.</summary>
    /// <remarks>Coordinates are clamped in range before this is called; implementations do not check.</remarks>
    float Density(int x, int y, int z);
}

/// <summary>
/// Density and material packed one per <see cref="ushort"/>, density in bits 0-7 and material in
/// bits 8-15, laid out x-major with z varying fastest.
/// </summary>
/// <remarks>The material half is ignored by collision.</remarks>
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

/// <summary>One byte of density per sample, x-major with z varying fastest.</summary>
/// <remarks>Also fits a game keeping density and material in two parallel arrays.</remarks>
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

/// <summary>One float of density per sample, x-major with z varying fastest, used as is.</summary>
/// <remarks>Suits a signed distance field; a field centred on zero takes an iso level of zero.</remarks>
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
