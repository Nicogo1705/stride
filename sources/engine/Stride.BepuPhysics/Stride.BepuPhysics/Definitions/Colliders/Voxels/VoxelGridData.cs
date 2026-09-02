// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using BepuPhysics.Collidables;

namespace Stride.BepuPhysics.Definitions.Colliders.Voxels;

/// <summary>
/// A voxel density field and every piece of geometry derived from it.
/// </summary>
/// <remarks>
/// <para>
/// How the samples are packed is <typeparamref name="TSource"/>'s business - see
/// <see cref="IVoxelDensitySource"/>. This adds what geometry needs on top: the cell size, the iso
/// level, and the marching-cubes and surface-nets constructions.
/// </para>
/// <para>
/// The source holds <em>samples</em>, not cells: a grid of n cells per axis needs n+1 samples per
/// axis, because a cell reads the eight samples at its corners. Cell (cx, cy, cz) spans local
/// [cx, cx+1] x [cy, cy+1] x [cz, cz+1] scaled by <see cref="CellSize"/>, with the grid origin at
/// the local origin.
/// </para>
/// <para>
/// Split in two on purpose, cheap tests apart from expensive constructions: asking <em>whether</em>
/// a cell contributes anything costs a handful of sample reads (<see cref="CubeIndex"/>,
/// <see cref="CellIsSolid"/>, <see cref="SurfaceNetsEdgeStraddles"/>), while actually building a
/// triangle is only paid for the children the narrow phase goes on to test. A broad phase query
/// touching a thousand cells therefore does a thousand cheap tests and a handful of expensive ones.
/// </para>
/// <para>
/// This lives in unmanaged memory - Bepu keeps shapes in its own pools and hands them back as raw
/// pointers - so neither it nor the source may hold anything managed.
/// </para>
/// </remarks>
public struct VoxelGridData<TSource> where TSource : unmanaged, IVoxelDensitySource
{
    /// <summary>Where the samples come from and how they are packed.</summary>
    public TSource Source;

    /// <summary>Edge length of one cell, in world units.</summary>
    public float CellSize;

    /// <summary>Density at or above which a sample counts as solid.</summary>
    public float IsoLevel;

    /// <summary>Flips the winding of every generated triangle. See VoxelCollider.InvertWinding.</summary>
    public bool InvertWinding;

    /// <summary>Reads the samples on the outer faces of the grid as air, closing the volume.</summary>
    /// <remarks>See VoxelCollider.SealBorder for why this is a choice rather than a rule.</remarks>
    public bool SealBorder;

    public readonly int SamplesX => Source.SamplesX;
    public readonly int SamplesY => Source.SamplesY;
    public readonly int SamplesZ => Source.SamplesZ;

    public readonly int CellsX => Source.SamplesX - 1;
    public readonly int CellsY => Source.SamplesY - 1;
    public readonly int CellsZ => Source.SamplesZ - 1;
    public readonly int CellCount => CellsX * CellsY * CellsZ;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly int CellIndex(int cx, int cy, int cz) => (cx * CellsY + cy) * CellsZ + cz;

    public readonly void DecomposeCell(int cellIndex, out int cx, out int cy, out int cz)
    {
        cz = cellIndex % CellsZ;
        var remainder = cellIndex / CellsZ;
        cy = remainder % CellsY;
        cx = remainder / CellsY;
    }

    /// <summary>
    /// Density of one sample, with no bounds check.
    /// </summary>
    /// <remarks>
    /// Everything below reads the eight corners of a cell, and a cell's corners are inside the
    /// sample grid by construction - cell indices stop one short of the sample count on every axis.
    /// So the hot path does not clamp. <see cref="Density"/> is the checked version for callers
    /// that may be outside.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly float DensityAt(int x, int y, int z)
    {
        // The seal belongs here rather than in the checked accessor below, because this is the one
        // every corner read goes through. A surface exists only where the field crosses the iso
        // level, and a grid whose edge is solid never crosses there - so without this the body has
        // no walls and no floor. It looks closed when drawn, because a ray entering from outside
        // stops on the box, and is open when collided against, because nothing is there.
        if (SealBorder && (x <= 0 || y <= 0 || z <= 0 || x >= SamplesX - 1 || y >= SamplesY - 1 || z >= SamplesZ - 1))
            return 0f;

        return Source.Density(x, y, z);
    }

    /// <summary>Density of one sample, clamping to the edge of the grid.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly float Density(int x, int y, int z)
        => DensityAt(
            Math.Clamp(x, 0, SamplesX - 1),
            Math.Clamp(y, 0, SamplesY - 1),
            Math.Clamp(z, 0, SamplesZ - 1));

    /// <summary>
    /// Whether a cell is solid enough to carry a box or sphere child: the mean of its eight corners
    /// is at or above the iso level.
    /// </summary>
    /// <remarks>
    /// Deliberately the mean rather than all-eight, which erodes the surface by a cell and drops
    /// characters through the ground, or any-of-eight, which inflates it by a cell. The mean stays
    /// within half a cell of the iso-surface on either side.
    /// </remarks>
    public readonly bool CellIsSolid(int cx, int cy, int cz)
    {
        // Outside the grid counts as empty, so a caller asking about a neighbour off the edge gets
        // the answer it expects rather than an out of range read.
        if ((uint)cx >= (uint)CellsX || (uint)cy >= (uint)CellsY || (uint)cz >= (uint)CellsZ)
            return false;

        var x1 = cx + 1;
        var y1 = cy + 1;
        var z1 = cz + 1;
        var sum =
            DensityAt(cx, cy, cz) + DensityAt(x1, cy, cz) + DensityAt(x1, cy, z1) + DensityAt(cx, cy, z1) +
            DensityAt(cx, y1, cz) + DensityAt(x1, y1, cz) + DensityAt(x1, y1, z1) + DensityAt(cx, y1, z1);
        return sum * 0.125f >= IsoLevel;
    }

    /// <summary>Local-space centre of a cell.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly Vector3 CellCentre(int cx, int cy, int cz)
        => new Vector3(cx + 0.5f, cy + 0.5f, cz + 0.5f) * CellSize;

    /// <summary>Bounds of the whole grid in local space. Analytic; it never walks the samples.</summary>
    public readonly void ComputeLocalBounds(out Vector3 min, out Vector3 max)
    {
        min = Vector3.Zero;
        max = new Vector3(CellsX, CellsY, CellsZ) * CellSize;
    }

    // -- Marching cubes ----------------------------------------------------------------------

    /// <summary>
    /// The eight corner offsets of a cell, three bytes each, in the order the triangle table indexes
    /// them. A span of bytes rather than of a small struct so the compiler hands back a pointer into
    /// the data section instead of allocating on every call.
    /// </summary>
    public static ReadOnlySpan<byte> CubeCorners =>
    [
        0, 0, 0,  1, 0, 0,  1, 0, 1,  0, 0, 1,
        0, 1, 0,  1, 1, 0,  1, 1, 1,  0, 1, 1,
    ];

    /// <summary>The two corners each of the twelve cube edges connects.</summary>
    public static ReadOnlySpan<byte> EdgeCorners =>
    [
        0, 1, 1, 2, 2, 3, 3, 0,
        4, 5, 5, 6, 6, 7, 7, 4,
        0, 4, 1, 5, 2, 6, 3, 7,
    ];

    /// <summary>
    /// Classifies a cell into a marching-cubes case index. Eight sample reads, and the only thing a
    /// cheap test needs.
    /// </summary>
    /// <remarks>
    /// A bit is set when the corner is <em>air</em>, density below the iso level, which is the
    /// convention the table below is tabulated for.
    /// </remarks>
    public readonly int CubeIndex(int cx, int cy, int cz)
    {
        var x1 = cx + 1;
        var y1 = cy + 1;
        var z1 = cz + 1;
        var iso = IsoLevel;
        var cubeIndex = 0;
        if (DensityAt(cx, cy, cz) < iso) cubeIndex |= 1 << 0;
        if (DensityAt(x1, cy, cz) < iso) cubeIndex |= 1 << 1;
        if (DensityAt(x1, cy, z1) < iso) cubeIndex |= 1 << 2;
        if (DensityAt(cx, cy, z1) < iso) cubeIndex |= 1 << 3;
        if (DensityAt(cx, y1, cz) < iso) cubeIndex |= 1 << 4;
        if (DensityAt(x1, y1, cz) < iso) cubeIndex |= 1 << 5;
        if (DensityAt(x1, y1, z1) < iso) cubeIndex |= 1 << 6;
        if (DensityAt(cx, y1, z1) < iso) cubeIndex |= 1 << 7;
        return cubeIndex;
    }

    /// <summary>Local-space point where the iso-surface crosses one edge of a cell.</summary>
    public readonly Vector3 EdgePosition(int cx, int cy, int cz, int edge)
    {
        var a = EdgeCorners[edge * 2] * 3;
        var b = EdgeCorners[edge * 2 + 1] * 3;
        var ax = cx + CubeCorners[a];
        var ay = cy + CubeCorners[a + 1];
        var az = cz + CubeCorners[a + 2];
        var bx = cx + CubeCorners[b];
        var by = cy + CubeCorners[b + 1];
        var bz = cz + CubeCorners[b + 2];
        var densityA = DensityAt(ax, ay, az);
        var densityB = DensityAt(bx, by, bz);
        var delta = densityB - densityA;
        // A zero delta means both corners sit exactly on the iso level; the midpoint is as good an
        // answer as any, and it avoids dividing by zero.
        var t = MathF.Abs(delta) > 1e-6f ? Math.Clamp((IsoLevel - densityA) / delta, 0f, 1f) : 0.5f;
        return Vector3.Lerp(new Vector3(ax, ay, az), new Vector3(bx, by, bz), t) * CellSize;
    }

    /// <summary>
    /// Which way the field rises across a cell, from differences of its eight corners. Points into
    /// matter, so an outward normal is this reversed.
    /// </summary>
    public readonly Vector3 CellGradient(int cx, int cy, int cz)
    {
        var x1 = cx + 1;
        var y1 = cy + 1;
        var z1 = cz + 1;

        var d000 = Density(cx, cy, cz);
        var d100 = Density(x1, cy, cz);
        var d010 = Density(cx, y1, cz);
        var d001 = Density(cx, cy, z1);
        var d110 = Density(x1, y1, cz);
        var d101 = Density(x1, cy, z1);
        var d011 = Density(cx, y1, z1);
        var d111 = Density(x1, y1, z1);

        return new Vector3(
            (d100 + d110 + d101 + d111) - (d000 + d010 + d001 + d011),
            (d010 + d110 + d011 + d111) - (d000 + d100 + d001 + d101),
            (d001 + d101 + d011 + d111) - (d000 + d100 + d010 + d110));
    }

    /// <summary>Number of marching-cubes triangles a cell can produce.</summary>
    public const int MaxMarchingCubesTrianglesPerCell = 5;

    /// <summary>How many triangles a classified cell produces. A table scan, no geometry.</summary>
    public static int MarchingCubesTriangleCount(int cubeIndex)
    {
        if (cubeIndex == 0 || cubeIndex == 255)
            return 0;
        var offset = cubeIndex * 16;
        for (int slot = 0; slot < MaxMarchingCubesTrianglesPerCell; ++slot)
        {
            if (TriangleTable[offset + slot * 3] < 0)
                return slot;
        }
        return MaxMarchingCubesTrianglesPerCell;
    }

    /// <summary>
    /// The slot'th marching-cubes triangle of an already classified cell.
    /// </summary>
    /// <remarks>
    /// Takes the case index rather than recomputing it, so a caller walking several slots of one
    /// cell pays for the classification once.
    /// </remarks>
    public readonly bool TryGetMarchingCubesTriangle(int cx, int cy, int cz, int cubeIndex, int slot, out Triangle triangle)
    {
        triangle = default;
        if (cubeIndex == 0 || cubeIndex == 255 || slot >= MaxMarchingCubesTrianglesPerCell)
            return false;
        var offset = cubeIndex * 16 + slot * 3;
        var e0 = TriangleTable[offset];
        if (e0 < 0)
            return false;
        var e1 = TriangleTable[offset + 1];
        var e2 = TriangleTable[offset + 2];
        // Reversed relative to the table, which is the winding convention that puts the face normal
        // on the air side for the air-is-set classification above.
        var a = EdgePosition(cx, cy, cz, e2);
        var b = EdgePosition(cx, cy, cz, e1);
        var c = EdgePosition(cx, cy, cz, e0);
        triangle = new Triangle(a, b, c);

        // Oriented against the field rather than trusted from the table: density rises into matter,
        // so the outward direction is the gradient reversed. One sided triangles make this the
        // difference between a surface that collides and one that lets everything through from the
        // wrong side.
        var outward = -CellGradient(cx, cy, cz);
        // cross(C - A, B - A), not the other way round: a Bepu triangle faces along that, being
        // clockwise in a right handed frame. Taking the usual counter-clockwise convention here
        // orients every face outward and then collides on the inside of all of them.
        var facing = Vector3.Cross(triangle.C - triangle.A, triangle.B - triangle.A);
        if (Vector3.Dot(facing, outward) < 0 != InvertWinding)
            (triangle.B, triangle.C) = (triangle.C, triangle.B);

        return true;
    }

    // -- Surface nets ------------------------------------------------------------------------

    /// <summary>Number of surface-nets triangles a cell can own: two per axis.</summary>
    public const int MaxSurfaceNetsTrianglesPerCell = 6;

    /// <summary>
    /// Whether the edge leaving a cell's minimum corner along an axis crosses the iso level, and the
    /// four cells sharing it all exist. Two sample reads and three comparisons - this is the cheap
    /// test that decides whether a cell owns a quad at all.
    /// </summary>
    /// <remarks>
    /// No validity check on the four cells beyond their indices: an edge inside the grid is shared
    /// by four cells that all straddle the surface if it does, so only the border needs excluding.
    /// </remarks>
    public readonly bool SurfaceNetsEdgeStraddles(int cx, int cy, int cz, int axis, out bool solidAtOrigin)
    {
        solidAtOrigin = DensityAt(cx, cy, cz) >= IsoLevel;

        // The quad spans the four cells around the edge, which step back by one on the two axes the
        // edge does not run along. At the low border those cells do not exist, so no quad is owned.
        int nx = cx, ny = cy, nz = cz;
        switch (axis)
        {
            case 0:
                if (cy < 1 || cz < 1)
                    return false;
                nx = cx + 1;
                break;
            case 1:
                if (cz < 1 || cx < 1)
                    return false;
                ny = cy + 1;
                break;
            default:
                if (cx < 1 || cy < 1)
                    return false;
                nz = cz + 1;
                break;
        }
        return solidAtOrigin != (DensityAt(nx, ny, nz) >= IsoLevel);
    }

    /// <summary>
    /// The surface-nets vertex of a cell: the mean of the iso-surface crossings on its edges.
    /// False for a cell the surface does not pass through.
    /// </summary>
    public readonly bool TryGetSurfaceNetsVertex(int cx, int cy, int cz, out Vector3 vertex)
    {
        vertex = default;
        if ((uint)cx >= (uint)CellsX || (uint)cy >= (uint)CellsY || (uint)cz >= (uint)CellsZ)
            return false;
        var cubeIndex = CubeIndex(cx, cy, cz);
        if (cubeIndex == 0 || cubeIndex == 255)
            return false;
        var sum = Vector3.Zero;
        var count = 0;
        for (int edge = 0; edge < 12; ++edge)
        {
            var maskA = 1 << EdgeCorners[edge * 2];
            var maskB = 1 << EdgeCorners[edge * 2 + 1];
            if (((cubeIndex & maskA) != 0) == ((cubeIndex & maskB) != 0))
                continue;
            sum += EdgePosition(cx, cy, cz, edge);
            ++count;
        }
        if (count == 0)
            return false;
        vertex = sum / count;
        return true;
    }

    /// <summary>
    /// The slot'th surface-nets triangle owned by a cell.
    /// </summary>
    /// <remarks>
    /// A cell owns the quads of the three edges leaving its minimum corner; slots 0-5 are
    /// (edge X, edge Y, edge Z) x (first triangle, second triangle). Ownership by the minimum corner
    /// is what emits every quad exactly once. Existence is decided far more cheaply by
    /// <see cref="SurfaceNetsEdgeStraddles"/>; this is the construction.
    /// </remarks>
    public readonly bool TryGetSurfaceNetsTriangle(int cx, int cy, int cz, int slot, out Triangle triangle)
    {
        triangle = default;
        var axis = slot / 2;
        if (!SurfaceNetsEdgeStraddles(cx, cy, cz, axis, out var solidAtOrigin))
            return false;

        // The four cells around that edge, walked as a cycle: (0,0) (0,-1) (-1,-1) (-1,0) in the two
        // axes the edge does not run along.
        Span<Vector3> quad = stackalloc Vector3[4];
        for (int i = 0; i < 4; ++i)
        {
            var stepA = i >= 2 ? -1 : 0;
            var stepB = i == 1 || i == 2 ? -1 : 0;
            int qx = cx, qy = cy, qz = cz;
            switch (axis)
            {
                case 0: qy += stepA; qz += stepB; break;
                case 1: qz += stepA; qx += stepB; break;
                default: qx += stepA; qy += stepB; break;
            }
            if (!TryGetSurfaceNetsVertex(qx, qy, qz, out quad[i]))
                return false;
        }

        triangle = (slot & 1) != 0
            ? new Triangle(quad[0], quad[2], quad[3])
            : new Triangle(quad[0], quad[1], quad[2]);

        // Which way the face has to look is known exactly, not guessed: matter is on the side of the
        // edge whose sample is solid, so the outward direction is the edge's axis, signed by that.
        // Orienting each triangle against it is what makes every face of the surface collide -
        // Bepu triangles are one sided, and a rule applied globally gets one set of faces right and
        // the opposite set wrong, which reads as a world where only the ground is solid.
        var outward = new Vector3(
            axis == 0 ? (solidAtOrigin ? 1f : -1f) : 0f,
            axis == 1 ? (solidAtOrigin ? 1f : -1f) : 0f,
            axis == 2 ? (solidAtOrigin ? 1f : -1f) : 0f);

        // cross(C - A, B - A), not the other way round: a Bepu triangle faces along that, being
        // clockwise in a right handed frame. Taking the usual counter-clockwise convention here
        // orients every face outward and then collides on the inside of all of them.
        var facing = Vector3.Cross(triangle.C - triangle.A, triangle.B - triangle.A);
        if (Vector3.Dot(facing, outward) < 0 != InvertWinding)
            (triangle.B, triangle.C) = (triangle.C, triangle.B);

        return true;
    }

    /// <summary>
    /// Marching cubes case table: sixteen entries per case, edge indices in groups of three,
    /// terminated by -1. The standard table, in the corner and edge ordering of
    /// <see cref="CubeCorners"/> and <see cref="EdgeCorners"/> above.
    /// </summary>
    public static ReadOnlySpan<sbyte> TriangleTable =>
    [
        -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 0, 8, 3, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 0, 1, 9, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 1, 8, 3, 9, 8, 1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        1, 2, 10, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 0, 8, 3, 1, 2, 10, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 9, 2, 10, 0, 2, 9, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 2, 8, 3, 2, 10, 8, 10, 9, 8, -1, -1, -1, -1, -1, -1, -1,
        3, 11, 2, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 0, 11, 2, 8, 11, 0, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 1, 9, 0, 2, 3, 11, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 1, 11, 2, 1, 9, 11, 9, 8, 11, -1, -1, -1, -1, -1, -1, -1,
        3, 10, 1, 11, 10, 3, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 0, 10, 1, 0, 8, 10, 8, 11, 10, -1, -1, -1, -1, -1, -1, -1, 3, 9, 0, 3, 11, 9, 11, 10, 9, -1, -1, -1, -1, -1, -1, -1, 9, 8, 10, 10, 8, 11, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        4, 7, 8, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 4, 3, 0, 7, 3, 4, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 0, 1, 9, 8, 4, 7, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 4, 1, 9, 4, 7, 1, 7, 3, 1, -1, -1, -1, -1, -1, -1, -1,
        1, 2, 10, 8, 4, 7, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 3, 4, 7, 3, 0, 4, 1, 2, 10, -1, -1, -1, -1, -1, -1, -1, 9, 2, 10, 9, 0, 2, 8, 4, 7, -1, -1, -1, -1, -1, -1, -1, 2, 10, 9, 2, 9, 7, 2, 7, 3, 7, 9, 4, -1, -1, -1, -1,
        8, 4, 7, 3, 11, 2, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 11, 4, 7, 11, 2, 4, 2, 0, 4, -1, -1, -1, -1, -1, -1, -1, 9, 0, 1, 8, 4, 7, 2, 3, 11, -1, -1, -1, -1, -1, -1, -1, 4, 7, 11, 9, 4, 11, 9, 11, 2, 9, 2, 1, -1, -1, -1, -1,
        3, 10, 1, 3, 11, 10, 7, 8, 4, -1, -1, -1, -1, -1, -1, -1, 1, 11, 10, 1, 4, 11, 1, 0, 4, 7, 11, 4, -1, -1, -1, -1, 4, 7, 8, 9, 0, 11, 9, 11, 10, 11, 0, 3, -1, -1, -1, -1, 4, 7, 11, 4, 11, 9, 9, 11, 10, -1, -1, -1, -1, -1, -1, -1,
        9, 5, 4, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 9, 5, 4, 0, 8, 3, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 0, 5, 4, 1, 5, 0, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 8, 5, 4, 8, 3, 5, 3, 1, 5, -1, -1, -1, -1, -1, -1, -1,
        1, 2, 10, 9, 5, 4, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 3, 0, 8, 1, 2, 10, 4, 9, 5, -1, -1, -1, -1, -1, -1, -1, 5, 2, 10, 5, 4, 2, 4, 0, 2, -1, -1, -1, -1, -1, -1, -1, 2, 10, 5, 3, 2, 5, 3, 5, 4, 3, 4, 8, -1, -1, -1, -1,
        9, 5, 4, 2, 3, 11, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 0, 11, 2, 0, 8, 11, 4, 9, 5, -1, -1, -1, -1, -1, -1, -1, 0, 5, 4, 0, 1, 5, 2, 3, 11, -1, -1, -1, -1, -1, -1, -1, 2, 1, 5, 2, 5, 8, 2, 8, 11, 4, 8, 5, -1, -1, -1, -1,
        10, 3, 11, 10, 1, 3, 9, 5, 4, -1, -1, -1, -1, -1, -1, -1, 4, 9, 5, 0, 8, 1, 8, 10, 1, 8, 11, 10, -1, -1, -1, -1, 5, 4, 0, 5, 0, 11, 5, 11, 10, 11, 0, 3, -1, -1, -1, -1, 5, 4, 8, 5, 8, 10, 10, 8, 11, -1, -1, -1, -1, -1, -1, -1,
        9, 7, 8, 5, 7, 9, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 9, 3, 0, 9, 5, 3, 5, 7, 3, -1, -1, -1, -1, -1, -1, -1, 0, 7, 8, 0, 1, 7, 1, 5, 7, -1, -1, -1, -1, -1, -1, -1, 1, 5, 3, 3, 5, 7, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        9, 7, 8, 9, 5, 7, 10, 1, 2, -1, -1, -1, -1, -1, -1, -1, 10, 1, 2, 9, 5, 0, 5, 3, 0, 5, 7, 3, -1, -1, -1, -1, 8, 0, 2, 8, 2, 5, 8, 5, 7, 10, 5, 2, -1, -1, -1, -1, 2, 10, 5, 2, 5, 3, 3, 5, 7, -1, -1, -1, -1, -1, -1, -1,
        7, 9, 5, 7, 8, 9, 3, 11, 2, -1, -1, -1, -1, -1, -1, -1, 9, 5, 7, 9, 7, 2, 9, 2, 0, 2, 7, 11, -1, -1, -1, -1, 2, 3, 11, 0, 1, 8, 1, 7, 8, 1, 5, 7, -1, -1, -1, -1, 11, 2, 1, 11, 1, 7, 7, 1, 5, -1, -1, -1, -1, -1, -1, -1,
        9, 5, 8, 8, 5, 7, 10, 1, 3, 10, 3, 11, -1, -1, -1, -1, 5, 7, 0, 5, 0, 9, 7, 11, 0, 1, 0, 10, 11, 10, 0, -1, 11, 10, 0, 11, 0, 3, 10, 5, 0, 8, 0, 7, 5, 7, 0, -1, 11, 10, 5, 7, 11, 5, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        10, 6, 5, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 0, 8, 3, 5, 10, 6, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 9, 0, 1, 5, 10, 6, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 1, 8, 3, 1, 9, 8, 5, 10, 6, -1, -1, -1, -1, -1, -1, -1,
        1, 6, 5, 2, 6, 1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 1, 6, 5, 1, 2, 6, 3, 0, 8, -1, -1, -1, -1, -1, -1, -1, 9, 6, 5, 9, 0, 6, 0, 2, 6, -1, -1, -1, -1, -1, -1, -1, 5, 9, 8, 5, 8, 2, 5, 2, 6, 3, 2, 8, -1, -1, -1, -1,
        2, 3, 11, 10, 6, 5, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 11, 0, 8, 11, 2, 0, 10, 6, 5, -1, -1, -1, -1, -1, -1, -1, 0, 1, 9, 2, 3, 11, 5, 10, 6, -1, -1, -1, -1, -1, -1, -1, 5, 10, 6, 1, 9, 2, 9, 11, 2, 9, 8, 11, -1, -1, -1, -1,
        6, 3, 11, 6, 5, 3, 5, 1, 3, -1, -1, -1, -1, -1, -1, -1, 0, 8, 11, 0, 11, 5, 0, 5, 1, 5, 11, 6, -1, -1, -1, -1, 3, 11, 6, 0, 3, 6, 0, 6, 5, 0, 5, 9, -1, -1, -1, -1, 6, 5, 9, 6, 9, 11, 11, 9, 8, -1, -1, -1, -1, -1, -1, -1,
        5, 10, 6, 4, 7, 8, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 4, 3, 0, 4, 7, 3, 6, 5, 10, -1, -1, -1, -1, -1, -1, -1, 1, 9, 0, 5, 10, 6, 8, 4, 7, -1, -1, -1, -1, -1, -1, -1, 10, 6, 5, 1, 9, 7, 1, 7, 3, 7, 9, 4, -1, -1, -1, -1,
        6, 1, 2, 6, 5, 1, 4, 7, 8, -1, -1, -1, -1, -1, -1, -1, 1, 2, 5, 5, 2, 6, 3, 0, 4, 3, 4, 7, -1, -1, -1, -1, 8, 4, 7, 9, 0, 5, 0, 6, 5, 0, 2, 6, -1, -1, -1, -1, 7, 3, 9, 7, 9, 4, 3, 2, 9, 5, 9, 6, 2, 6, 9, -1,
        3, 11, 2, 7, 8, 4, 10, 6, 5, -1, -1, -1, -1, -1, -1, -1, 5, 10, 6, 4, 7, 2, 4, 2, 0, 2, 7, 11, -1, -1, -1, -1, 0, 1, 9, 4, 7, 8, 2, 3, 11, 5, 10, 6, -1, -1, -1, -1, 9, 2, 1, 9, 11, 2, 9, 4, 11, 7, 11, 4, 5, 10, 6, -1,
        8, 4, 7, 3, 11, 5, 3, 5, 1, 5, 11, 6, -1, -1, -1, -1, 5, 1, 11, 5, 11, 6, 1, 0, 11, 7, 11, 4, 0, 4, 11, -1, 0, 5, 9, 0, 6, 5, 0, 3, 6, 11, 6, 3, 8, 4, 7, -1, 6, 5, 9, 6, 9, 11, 4, 7, 9, 7, 11, 9, -1, -1, -1, -1,
        10, 4, 9, 6, 4, 10, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 4, 10, 6, 4, 9, 10, 0, 8, 3, -1, -1, -1, -1, -1, -1, -1, 10, 0, 1, 10, 6, 0, 6, 4, 0, -1, -1, -1, -1, -1, -1, -1, 8, 3, 1, 8, 1, 6, 8, 6, 4, 6, 1, 10, -1, -1, -1, -1,
        1, 4, 9, 1, 2, 4, 2, 6, 4, -1, -1, -1, -1, -1, -1, -1, 3, 0, 8, 1, 2, 9, 2, 4, 9, 2, 6, 4, -1, -1, -1, -1, 0, 2, 4, 4, 2, 6, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 8, 3, 2, 8, 2, 4, 4, 2, 6, -1, -1, -1, -1, -1, -1, -1,
        10, 4, 9, 10, 6, 4, 11, 2, 3, -1, -1, -1, -1, -1, -1, -1, 0, 8, 2, 2, 8, 11, 4, 9, 10, 4, 10, 6, -1, -1, -1, -1, 3, 11, 2, 0, 1, 6, 0, 6, 4, 6, 1, 10, -1, -1, -1, -1, 6, 4, 1, 6, 1, 10, 4, 8, 1, 2, 1, 11, 8, 11, 1, -1,
        9, 6, 4, 9, 3, 6, 9, 1, 3, 11, 6, 3, -1, -1, -1, -1, 8, 11, 1, 8, 1, 0, 11, 6, 1, 9, 1, 4, 6, 4, 1, -1, 3, 11, 6, 3, 6, 0, 0, 6, 4, -1, -1, -1, -1, -1, -1, -1, 6, 4, 8, 11, 6, 8, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        7, 10, 6, 7, 8, 10, 8, 9, 10, -1, -1, -1, -1, -1, -1, -1, 0, 7, 3, 0, 10, 7, 0, 9, 10, 6, 7, 10, -1, -1, -1, -1, 10, 6, 7, 1, 10, 7, 1, 7, 8, 1, 8, 0, -1, -1, -1, -1, 10, 6, 7, 10, 7, 1, 1, 7, 3, -1, -1, -1, -1, -1, -1, -1,
        1, 2, 6, 1, 6, 8, 1, 8, 9, 8, 6, 7, -1, -1, -1, -1, 2, 6, 9, 2, 9, 1, 6, 7, 9, 0, 9, 3, 7, 3, 9, -1, 7, 8, 0, 7, 0, 6, 6, 0, 2, -1, -1, -1, -1, -1, -1, -1, 7, 3, 2, 6, 7, 2, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        2, 3, 11, 10, 6, 8, 10, 8, 9, 8, 6, 7, -1, -1, -1, -1, 2, 0, 7, 2, 7, 11, 0, 9, 7, 6, 7, 10, 9, 10, 7, -1, 1, 8, 0, 1, 7, 8, 1, 10, 7, 6, 7, 10, 2, 3, 11, -1, 11, 2, 1, 11, 1, 7, 10, 6, 1, 6, 7, 1, -1, -1, -1, -1,
        8, 9, 6, 8, 6, 7, 9, 1, 6, 11, 6, 3, 1, 3, 6, -1, 0, 9, 1, 11, 6, 7, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 7, 8, 0, 7, 0, 6, 3, 11, 0, 11, 6, 0, -1, -1, -1, -1, 7, 11, 6, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        7, 6, 11, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 3, 0, 8, 11, 7, 6, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 0, 1, 9, 11, 7, 6, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 8, 1, 9, 8, 3, 1, 11, 7, 6, -1, -1, -1, -1, -1, -1, -1,
        10, 1, 2, 6, 11, 7, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 1, 2, 10, 3, 0, 8, 6, 11, 7, -1, -1, -1, -1, -1, -1, -1, 2, 9, 0, 2, 10, 9, 6, 11, 7, -1, -1, -1, -1, -1, -1, -1, 6, 11, 7, 2, 10, 3, 10, 8, 3, 10, 9, 8, -1, -1, -1, -1,
        7, 2, 3, 6, 2, 7, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 7, 0, 8, 7, 6, 0, 6, 2, 0, -1, -1, -1, -1, -1, -1, -1, 2, 7, 6, 2, 3, 7, 0, 1, 9, -1, -1, -1, -1, -1, -1, -1, 1, 6, 2, 1, 8, 6, 1, 9, 8, 8, 7, 6, -1, -1, -1, -1,
        10, 7, 6, 10, 1, 7, 1, 3, 7, -1, -1, -1, -1, -1, -1, -1, 10, 7, 6, 1, 7, 10, 1, 8, 7, 1, 0, 8, -1, -1, -1, -1, 0, 3, 7, 0, 7, 10, 0, 10, 9, 6, 10, 7, -1, -1, -1, -1, 7, 6, 10, 7, 10, 8, 8, 10, 9, -1, -1, -1, -1, -1, -1, -1,
        6, 8, 4, 11, 8, 6, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 3, 6, 11, 3, 0, 6, 0, 4, 6, -1, -1, -1, -1, -1, -1, -1, 8, 6, 11, 8, 4, 6, 9, 0, 1, -1, -1, -1, -1, -1, -1, -1, 9, 4, 6, 9, 6, 3, 9, 3, 1, 11, 3, 6, -1, -1, -1, -1,
        6, 8, 4, 6, 11, 8, 2, 10, 1, -1, -1, -1, -1, -1, -1, -1, 1, 2, 10, 3, 0, 11, 0, 6, 11, 0, 4, 6, -1, -1, -1, -1, 4, 11, 8, 4, 6, 11, 0, 2, 9, 2, 10, 9, -1, -1, -1, -1, 10, 9, 3, 10, 3, 2, 9, 4, 3, 11, 3, 6, 4, 6, 3, -1,
        8, 2, 3, 8, 4, 2, 4, 6, 2, -1, -1, -1, -1, -1, -1, -1, 0, 4, 2, 4, 6, 2, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 1, 9, 0, 2, 3, 4, 2, 4, 6, 4, 3, 8, -1, -1, -1, -1, 1, 9, 4, 1, 4, 2, 2, 4, 6, -1, -1, -1, -1, -1, -1, -1,
        8, 1, 3, 8, 6, 1, 8, 4, 6, 6, 10, 1, -1, -1, -1, -1, 10, 1, 0, 10, 0, 6, 6, 0, 4, -1, -1, -1, -1, -1, -1, -1, 4, 6, 3, 4, 3, 8, 6, 10, 3, 0, 3, 9, 10, 9, 3, -1, 10, 9, 4, 6, 10, 4, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        4, 9, 5, 7, 6, 11, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 0, 8, 3, 4, 9, 5, 11, 7, 6, -1, -1, -1, -1, -1, -1, -1, 5, 0, 1, 5, 4, 0, 7, 6, 11, -1, -1, -1, -1, -1, -1, -1, 11, 7, 6, 8, 3, 4, 3, 5, 4, 3, 1, 5, -1, -1, -1, -1,
        9, 5, 4, 10, 1, 2, 7, 6, 11, -1, -1, -1, -1, -1, -1, -1, 6, 11, 7, 1, 2, 10, 0, 8, 3, 4, 9, 5, -1, -1, -1, -1, 7, 6, 11, 5, 4, 10, 4, 2, 10, 4, 0, 2, -1, -1, -1, -1, 3, 4, 8, 3, 5, 4, 3, 2, 5, 10, 5, 2, 11, 7, 6, -1,
        7, 2, 3, 7, 6, 2, 5, 4, 9, -1, -1, -1, -1, -1, -1, -1, 9, 5, 4, 0, 8, 6, 0, 6, 2, 6, 8, 7, -1, -1, -1, -1, 3, 6, 2, 3, 7, 6, 1, 5, 0, 5, 4, 0, -1, -1, -1, -1, 6, 2, 8, 6, 8, 7, 2, 1, 8, 4, 8, 5, 1, 5, 8, -1,
        9, 5, 4, 10, 1, 6, 1, 7, 6, 1, 3, 7, -1, -1, -1, -1, 1, 6, 10, 1, 7, 6, 1, 0, 7, 8, 7, 0, 9, 5, 4, -1, 4, 0, 10, 4, 10, 5, 0, 3, 10, 6, 10, 7, 3, 7, 10, -1, 7, 6, 10, 7, 10, 8, 5, 4, 10, 4, 8, 10, -1, -1, -1, -1,
        6, 9, 5, 6, 11, 9, 11, 8, 9, -1, -1, -1, -1, -1, -1, -1, 3, 6, 11, 0, 6, 3, 0, 5, 6, 0, 9, 5, -1, -1, -1, -1, 0, 11, 8, 0, 5, 11, 0, 1, 5, 5, 6, 11, -1, -1, -1, -1, 6, 11, 3, 6, 3, 5, 5, 3, 1, -1, -1, -1, -1, -1, -1, -1,
        1, 2, 10, 9, 5, 11, 9, 11, 8, 11, 5, 6, -1, -1, -1, -1, 0, 11, 3, 0, 6, 11, 0, 9, 6, 5, 6, 9, 1, 2, 10, -1, 11, 8, 5, 11, 5, 6, 8, 0, 5, 10, 5, 2, 0, 2, 5, -1, 6, 11, 3, 6, 3, 5, 2, 10, 3, 10, 5, 3, -1, -1, -1, -1,
        5, 8, 9, 5, 2, 8, 5, 6, 2, 3, 8, 2, -1, -1, -1, -1, 9, 5, 6, 9, 6, 0, 0, 6, 2, -1, -1, -1, -1, -1, -1, -1, 1, 5, 8, 1, 8, 0, 5, 6, 8, 3, 8, 2, 6, 2, 8, -1, 1, 5, 6, 2, 1, 6, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        1, 3, 6, 1, 6, 10, 3, 8, 6, 5, 6, 9, 8, 9, 6, -1, 10, 1, 0, 10, 0, 6, 9, 5, 0, 5, 6, 0, -1, -1, -1, -1, 0, 3, 8, 5, 6, 10, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 10, 5, 6, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        11, 5, 10, 7, 5, 11, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 11, 5, 10, 11, 7, 5, 8, 3, 0, -1, -1, -1, -1, -1, -1, -1, 5, 11, 7, 5, 10, 11, 1, 9, 0, -1, -1, -1, -1, -1, -1, -1, 10, 7, 5, 10, 11, 7, 9, 8, 1, 8, 3, 1, -1, -1, -1, -1,
        11, 1, 2, 11, 7, 1, 7, 5, 1, -1, -1, -1, -1, -1, -1, -1, 0, 8, 3, 1, 2, 7, 1, 7, 5, 7, 2, 11, -1, -1, -1, -1, 9, 7, 5, 9, 2, 7, 9, 0, 2, 2, 11, 7, -1, -1, -1, -1, 7, 5, 2, 7, 2, 11, 5, 9, 2, 3, 2, 8, 9, 8, 2, -1,
        2, 5, 10, 2, 3, 5, 3, 7, 5, -1, -1, -1, -1, -1, -1, -1, 8, 2, 0, 8, 5, 2, 8, 7, 5, 10, 2, 5, -1, -1, -1, -1, 9, 0, 1, 5, 10, 3, 5, 3, 7, 3, 10, 2, -1, -1, -1, -1, 9, 8, 2, 9, 2, 1, 8, 7, 2, 10, 2, 5, 7, 5, 2, -1,
        1, 3, 5, 3, 7, 5, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 0, 8, 7, 0, 7, 1, 1, 7, 5, -1, -1, -1, -1, -1, -1, -1, 9, 0, 3, 9, 3, 5, 5, 3, 7, -1, -1, -1, -1, -1, -1, -1, 9, 8, 7, 5, 9, 7, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        5, 8, 4, 5, 10, 8, 10, 11, 8, -1, -1, -1, -1, -1, -1, -1, 5, 0, 4, 5, 11, 0, 5, 10, 11, 11, 3, 0, -1, -1, -1, -1, 0, 1, 9, 8, 4, 10, 8, 10, 11, 10, 4, 5, -1, -1, -1, -1, 10, 11, 4, 10, 4, 5, 11, 3, 4, 9, 4, 1, 3, 1, 4, -1,
        2, 5, 1, 2, 8, 5, 2, 11, 8, 4, 5, 8, -1, -1, -1, -1, 0, 4, 11, 0, 11, 3, 4, 5, 11, 2, 11, 1, 5, 1, 11, -1, 0, 2, 5, 0, 5, 9, 2, 11, 5, 4, 5, 8, 11, 8, 5, -1, 9, 4, 5, 2, 11, 3, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        2, 5, 10, 3, 5, 2, 3, 4, 5, 3, 8, 4, -1, -1, -1, -1, 5, 10, 2, 5, 2, 4, 4, 2, 0, -1, -1, -1, -1, -1, -1, -1, 3, 10, 2, 3, 5, 10, 3, 8, 5, 4, 5, 8, 0, 1, 9, -1, 5, 10, 2, 5, 2, 4, 1, 9, 2, 9, 4, 2, -1, -1, -1, -1,
        8, 4, 5, 8, 5, 3, 3, 5, 1, -1, -1, -1, -1, -1, -1, -1, 0, 4, 5, 1, 0, 5, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 8, 4, 5, 8, 5, 3, 9, 0, 5, 0, 3, 5, -1, -1, -1, -1, 9, 4, 5, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        4, 11, 7, 4, 9, 11, 9, 10, 11, -1, -1, -1, -1, -1, -1, -1, 0, 8, 3, 4, 9, 7, 9, 11, 7, 9, 10, 11, -1, -1, -1, -1, 1, 10, 11, 1, 11, 4, 1, 4, 0, 7, 4, 11, -1, -1, -1, -1, 3, 1, 4, 3, 4, 8, 1, 10, 4, 7, 4, 11, 10, 11, 4, -1,
        4, 11, 7, 9, 11, 4, 9, 2, 11, 9, 1, 2, -1, -1, -1, -1, 9, 7, 4, 9, 11, 7, 9, 1, 11, 2, 11, 1, 0, 8, 3, -1, 11, 7, 4, 11, 4, 2, 2, 4, 0, -1, -1, -1, -1, -1, -1, -1, 11, 7, 4, 11, 4, 2, 8, 3, 4, 3, 2, 4, -1, -1, -1, -1,
        2, 9, 10, 2, 7, 9, 2, 3, 7, 7, 4, 9, -1, -1, -1, -1, 9, 10, 7, 9, 7, 4, 10, 2, 7, 8, 7, 0, 2, 0, 7, -1, 3, 7, 10, 3, 10, 2, 7, 4, 10, 1, 10, 0, 4, 0, 10, -1, 1, 10, 2, 8, 7, 4, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        4, 9, 1, 4, 1, 7, 7, 1, 3, -1, -1, -1, -1, -1, -1, -1, 4, 9, 1, 4, 1, 7, 0, 8, 1, 8, 7, 1, -1, -1, -1, -1, 4, 0, 3, 7, 4, 3, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 4, 8, 7, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        9, 10, 8, 10, 11, 8, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 3, 0, 9, 3, 9, 11, 11, 9, 10, -1, -1, -1, -1, -1, -1, -1, 0, 1, 10, 0, 10, 8, 8, 10, 11, -1, -1, -1, -1, -1, -1, -1, 3, 1, 10, 11, 3, 10, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        1, 2, 11, 1, 11, 9, 9, 11, 8, -1, -1, -1, -1, -1, -1, -1, 3, 0, 9, 3, 9, 11, 1, 2, 9, 2, 11, 9, -1, -1, -1, -1, 0, 2, 11, 8, 0, 11, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 3, 2, 11, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        2, 3, 8, 2, 8, 10, 10, 8, 9, -1, -1, -1, -1, -1, -1, -1, 9, 10, 2, 0, 9, 2, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 2, 3, 8, 2, 8, 10, 0, 1, 8, 1, 10, 8, -1, -1, -1, -1, 1, 10, 2, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        1, 3, 8, 9, 1, 8, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 0, 9, 1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 0, 3, 8, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
    ];
}
