// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection.CollisionTasks;
using BepuPhysics.Trees;
using BepuUtilities;
using BepuUtilities.Memory;
using NRigidPose = BepuPhysics.RigidPose;

namespace Stride.BepuPhysics.Definitions.Colliders.Voxels;

/// <summary>
/// What the shared voxel machinery needs from each of the shapes below.
/// </summary>
/// <remarks>
/// The three shapes differ only in what a cell contributes to the narrow phase. Everything else -
/// bounds, overlap queries, ray traversal - is the same grid walk, written once in
/// <see cref="VoxelShapeHelpers"/> and reached through this interface.
/// </remarks>
public unsafe interface IVoxelShape
{
    /// <summary>Bepu type id of the child shape: Box.Id, Sphere.Id or Triangle.Id.</summary>
    static abstract int ChildShapeTypeId { get; }

    /// <summary>The density field. Returned by value; take a local copy rather than re-reading it per cell.</summary>
    VoxelGridData Grid { get; }

    /// <summary>Upper bound on what <see cref="GetCellChildren"/> can report for one cell.</summary>
    static abstract int MaxChildrenPerCell { get; }

    /// <summary>
    /// Children this cell actually contributes, as child indices. Zero for a cell the surface does
    /// not touch, which is the common case and the reason nothing is stored per child.
    /// </summary>
    int GetCellChildren(int cx, int cy, int cz, Span<int> childIndices);

    /// <summary>Tests one child against a ray already expressed in the shape's local space.</summary>
    bool RayTestChild(int childIndex, Vector3 origin, Vector3 direction, out float t, out Vector3 normal);

    /// <summary>
    /// Writes a child's convex shape data to <paramref name="destination"/> and reports where that
    /// child sits in local space. Returns the number of bytes written.
    /// </summary>
    int WriteChildShapeData(int childIndex, out Vector3 localPosition, void* destination);
}

/// <summary>
/// The grid walks every voxel shape shares: bounds, bounding box queries, and ray traversal.
/// </summary>
/// <remarks>
/// None of this uses an acceleration structure, and that is the point. Bepu's own
/// <see cref="Mesh"/> - and the voxel collidable in Bepu's demos - build a bounding volume tree
/// over the children, because a triangle soup has no structure to exploit. A regular grid already
/// is the structure: a bounding box maps to a range of cell indices by division, and a ray walks
/// the cells it crosses in order. So there is no tree to build when the collidable is created and
/// none to refit when a voxel is edited.
/// </remarks>
public static unsafe class VoxelShapeHelpers
{
    /// <summary>Bounds of the whole grid under an orientation, from the eight corners of its box.</summary>
    public static void ComputeBounds(in VoxelGridData grid, Quaternion orientation, out Vector3 min, out Vector3 max)
    {
        grid.ComputeLocalBounds(out var localMin, out var localMax);
        Matrix3x3.CreateFromQuaternion(orientation, out var basis);
        min = new Vector3(float.MaxValue);
        max = new Vector3(float.MinValue);
        for (int i = 0; i < 8; ++i)
        {
            var corner = new Vector3(
                (i & 1) != 0 ? localMax.X : localMin.X,
                (i & 2) != 0 ? localMax.Y : localMin.Y,
                (i & 4) != 0 ? localMax.Z : localMin.Z);
            Matrix3x3.Transform(corner, basis, out var rotated);
            min = Vector3.Min(rotated, min);
            max = Vector3.Max(rotated, max);
        }
    }

    /// <summary>
    /// Reports every child overlapping a local space bounding box to a breakable enumerator. This
    /// is the shape of query Bepu's boundary smoothing would use; it is also the simplest way to
    /// express the grid walk, so the other two overloads are written in terms of the same loop.
    /// </summary>
    public static void EnumerateOverlaps<TShape, TEnumerator>(ref TShape shape, Vector3 min, Vector3 max, ref TEnumerator enumerator)
        where TShape : unmanaged, IVoxelShape
        where TEnumerator : IBreakableForEach<int>
    {
        var grid = shape.Grid;
        if (!grid.GetCellRange(min, max, out var x0, out var y0, out var z0, out var x1, out var y1, out var z1))
            return;
        Span<int> children = stackalloc int[TShape.MaxChildrenPerCell];
        for (int x = x0; x <= x1; ++x)
        {
            for (int y = y0; y <= y1; ++y)
            {
                for (int z = z0; z <= z1; ++z)
                {
                    var count = shape.GetCellChildren(x, y, z, children);
                    for (int i = 0; i < count; ++i)
                    {
                        if (!enumerator.LoopBody(children[i]))
                            return;
                    }
                }
            }
        }
    }

    /// <summary>The batched query the convex-compound and compound-pair collision tasks drive.</summary>
    public static void FindLocalOverlaps<TShape, TOverlaps, TSubpairOverlaps>(ref Buffer<OverlapQueryForPair> pairs, BufferPool pool, ref TOverlaps overlaps)
        where TShape : unmanaged, IVoxelShape
        where TOverlaps : struct, ICollisionTaskOverlaps<TSubpairOverlaps>
        where TSubpairOverlaps : struct, ICollisionTaskSubpairOverlaps
    {
        for (int i = 0; i < pairs.Length; ++i)
        {
            ref var pair = ref pairs[i];
            ref var shape = ref Unsafe.AsRef<TShape>(pair.Container);
            ref var subpairOverlaps = ref overlaps.GetOverlapsForPair(i);
            var collector = new OverlapCollector<TSubpairOverlaps>(Unsafe.AsPointer(ref subpairOverlaps), pool);
            EnumerateOverlaps(ref shape, pair.Min, pair.Max, ref collector);
        }
    }

    /// <summary>
    /// The swept query used by sweep tests. The sweep is handled by expanding the box to cover the
    /// whole swept volume rather than walking it - conservative, so it can report a child the sweep
    /// would have missed, which costs a narrow phase test and never a wrong answer.
    /// </summary>
    public static void FindLocalOverlaps<TShape, TOverlaps>(Vector3 min, Vector3 max, Vector3 sweep, float maximumT, BufferPool pool, void* overlaps, ref TShape shape)
        where TShape : unmanaged, IVoxelShape
        where TOverlaps : ICollisionTaskSubpairOverlaps
    {
        var sweptOffset = sweep * maximumT;
        var sweptMin = Vector3.Min(min, min + sweptOffset);
        var sweptMax = Vector3.Max(max, max + sweptOffset);
        var collector = new OverlapCollector<TOverlaps>(overlaps, pool);
        EnumerateOverlaps(ref shape, sweptMin, sweptMax, ref collector);
    }

    /// <summary>Adds every reported child index to one of Bepu's subpair overlap collections.</summary>
    private struct OverlapCollector<TSubpairOverlaps>(void* overlaps, BufferPool pool) : IBreakableForEach<int>
        where TSubpairOverlaps : ICollisionTaskSubpairOverlaps
    {
        private readonly void* overlaps = overlaps;
        private readonly BufferPool pool = pool;

        public bool LoopBody(int childIndex)
        {
            Unsafe.AsRef<TSubpairOverlaps>(overlaps).Allocate(pool) = childIndex;
            return true;
        }
    }

    /// <summary>
    /// Walks the cells a ray crosses, in order, testing the children of each.
    /// </summary>
    /// <remarks>
    /// A three dimensional DDA: clip the ray to the grid box, then step from cell to cell along
    /// whichever axis reaches its next boundary first. Cells are visited in increasing distance, so
    /// a handler that narrows <paramref name="maximumT"/> on a hit stops the walk almost
    /// immediately - which is what makes this cheap for the common short probe.
    /// </remarks>
    public static void RayTest<TShape, TRayHitHandler>(ref TShape shape, in NRigidPose pose, in RayData ray, ref float maximumT, ref TRayHitHandler hitHandler)
        where TShape : unmanaged, IVoxelShape
        where TRayHitHandler : struct, IShapeRayHitHandler
    {
        var grid = shape.Grid;
        Matrix3x3.CreateFromQuaternion(pose.Orientation, out var orientation);
        Matrix3x3.TransformTranspose(ray.Origin - pose.Position, orientation, out var origin);
        Matrix3x3.TransformTranspose(ray.Direction, orientation, out var direction);

        grid.ComputeLocalBounds(out var boundsMin, out var boundsMax);
        if (!TryClipToBox(origin, direction, boundsMin, boundsMax, maximumT, out var tEnter, out var tExit))
            return;

        var cellSize = grid.CellSize;
        var entry = origin + direction * tEnter;
        var x = Math.Clamp((int)MathF.Floor(entry.X / cellSize), 0, grid.CellsX - 1);
        var y = Math.Clamp((int)MathF.Floor(entry.Y / cellSize), 0, grid.CellsY - 1);
        var z = Math.Clamp((int)MathF.Floor(entry.Z / cellSize), 0, grid.CellsZ - 1);

        ComputeAxisStep(origin.X, direction.X, x, cellSize, tEnter, out var stepX, out var tMaxX, out var tDeltaX);
        ComputeAxisStep(origin.Y, direction.Y, y, cellSize, tEnter, out var stepY, out var tMaxY, out var tDeltaY);
        ComputeAxisStep(origin.Z, direction.Z, z, cellSize, tEnter, out var stepZ, out var tMaxZ, out var tDeltaZ);

        Span<int> children = stackalloc int[TShape.MaxChildrenPerCell];
        var t = tEnter;
        while (t <= tExit && t <= maximumT)
        {
            var count = shape.GetCellChildren(x, y, z, children);
            for (int i = 0; i < count; ++i)
            {
                var childIndex = children[i];
                if (!hitHandler.AllowTest(childIndex))
                    continue;
                if (shape.RayTestChild(childIndex, origin, direction, out var hitT, out var normal) && hitT <= maximumT)
                {
                    Matrix3x3.Transform(normal, orientation, out normal);
                    hitHandler.OnRayHit(ray, ref maximumT, hitT, normal, childIndex);
                }
            }

            if (tMaxX < tMaxY && tMaxX < tMaxZ)
            {
                x += stepX;
                t = tMaxX;
                tMaxX += tDeltaX;
                if (x < 0 || x >= grid.CellsX)
                    return;
            }
            else if (tMaxY < tMaxZ)
            {
                y += stepY;
                t = tMaxY;
                tMaxY += tDeltaY;
                if (y < 0 || y >= grid.CellsY)
                    return;
            }
            else
            {
                z += stepZ;
                t = tMaxZ;
                tMaxZ += tDeltaZ;
                if (z < 0 || z >= grid.CellsZ)
                    return;
            }
        }
    }

    /// <summary>Ray bundle overload; Bepu batches rays, we simply walk them one at a time.</summary>
    public static void RayTest<TShape, TRayHitHandler>(ref TShape shape, in NRigidPose pose, ref RaySource rays, ref TRayHitHandler hitHandler)
        where TShape : unmanaged, IVoxelShape
        where TRayHitHandler : struct, IShapeRayHitHandler
    {
        for (int i = 0; i < rays.RayCount; ++i)
        {
            rays.GetRay(i, out var ray, out var maximumT);
            RayTest(ref shape, pose, *ray, ref *maximumT, ref hitHandler);
        }
    }

    /// <summary>Slab test clipping a ray to a local space box, bounded by the ray's maximum length.</summary>
    private static bool TryClipToBox(Vector3 origin, Vector3 direction, Vector3 min, Vector3 max, float maximumT, out float tEnter, out float tExit)
    {
        tEnter = 0f;
        tExit = maximumT;
        for (int axis = 0; axis < 3; ++axis)
        {
            var o = axis == 0 ? origin.X : axis == 1 ? origin.Y : origin.Z;
            var d = axis == 0 ? direction.X : axis == 1 ? direction.Y : direction.Z;
            var lo = axis == 0 ? min.X : axis == 1 ? min.Y : min.Z;
            var hi = axis == 0 ? max.X : axis == 1 ? max.Y : max.Z;
            if (MathF.Abs(d) < 1e-9f)
            {
                if (o < lo || o > hi)
                    return false;
                continue;
            }
            var inverse = 1f / d;
            var t0 = (lo - o) * inverse;
            var t1 = (hi - o) * inverse;
            if (t0 > t1)
                (t0, t1) = (t1, t0);
            tEnter = MathF.Max(tEnter, t0);
            tExit = MathF.Min(tExit, t1);
            if (tEnter > tExit)
                return false;
        }
        return true;
    }

    /// <summary>Per axis setup for the traversal: which way to step, and when the next boundary arrives.</summary>
    private static void ComputeAxisStep(float origin, float direction, int cell, float cellSize, float tEnter, out int step, out float tMax, out float tDelta)
    {
        if (MathF.Abs(direction) < 1e-9f)
        {
            step = 0;
            tMax = float.MaxValue;
            tDelta = float.MaxValue;
            return;
        }
        step = direction > 0 ? 1 : -1;
        tDelta = MathF.Abs(cellSize / direction);
        var boundary = (cell + (step > 0 ? 1 : 0)) * cellSize;
        tMax = (boundary - origin) / direction;
        // Numerical drift at the entry face can put the first boundary marginally behind us.
        if (tMax < tEnter)
            tMax = tEnter + tDelta;
    }
}

/// <summary>
/// A voxel grid presented to the narrow phase as one box per occupied cell.
/// </summary>
public unsafe struct VoxelBoxShape : IHomogeneousCompoundShape<Box, BoxWide>, IBoundsQueryableCompound, IVoxelShape
{
    /// <summary>Ids past Bepu's built-ins, which end at Mesh = 8. Must be unique within a simulation.</summary>
    public const int Id = 12;
    public static int TypeId => Id;
    public static int ChildShapeTypeId => Box.Id;
    public static int MaxChildrenPerCell => 1;

    public VoxelGridData GridData;
    public readonly VoxelGridData Grid => GridData;

    public readonly int ChildCount => GridData.CellCount;

    public static ShapeBatch CreateShapeBatch(BufferPool pool, int initialCapacity, Shapes shapeBatches)
        => new HomogeneousCompoundShapeBatch<VoxelBoxShape, Box, BoxWide>(pool, initialCapacity);

    public readonly int GetCellChildren(int cx, int cy, int cz, Span<int> childIndices)
    {
        if (!GridData.CellIsSolid(cx, cy, cz))
            return 0;
        childIndices[0] = GridData.CellIndex(cx, cy, cz);
        return 1;
    }

    public readonly void GetLocalChild(int childIndex, out Box childShape)
        => childShape = new Box(GridData.CellSize, GridData.CellSize, GridData.CellSize);

    public readonly void GetPosedLocalChild(int childIndex, out Box childShape, out NRigidPose childPose)
    {
        GetLocalChild(childIndex, out childShape);
        GridData.DecomposeCell(childIndex, out var cx, out var cy, out var cz);
        childPose = new NRigidPose(GridData.CellCentre(cx, cy, cz));
    }

    public readonly void GetLocalChild(int childIndex, ref BoxWide childShapeWide)
    {
        var half = GridData.CellSize * 0.5f;
        GatherScatter.GetFirst(ref childShapeWide.HalfWidth) = half;
        GatherScatter.GetFirst(ref childShapeWide.HalfHeight) = half;
        GatherScatter.GetFirst(ref childShapeWide.HalfLength) = half;
    }

    public readonly bool RayTestChild(int childIndex, Vector3 origin, Vector3 direction, out float t, out Vector3 normal)
    {
        GetPosedLocalChild(childIndex, out var box, out var pose);
        return box.RayTest(pose, origin, direction, out t, out normal);
    }

    public readonly int WriteChildShapeData(int childIndex, out Vector3 localPosition, void* destination)
    {
        GridData.DecomposeCell(childIndex, out var cx, out var cy, out var cz);
        localPosition = GridData.CellCentre(cx, cy, cz);
        var half = new Vector3(GridData.CellSize * 0.5f);
        Unsafe.Write(destination, half);
        return sizeof(Box);
    }

    public readonly void ComputeBounds(Quaternion orientation, out Vector3 min, out Vector3 max)
        => VoxelShapeHelpers.ComputeBounds(GridData, orientation, out min, out max);

    public void RayTest<TRayHitHandler>(in NRigidPose pose, in RayData ray, ref float maximumT, BufferPool pool, ref TRayHitHandler hitHandler)
        where TRayHitHandler : struct, IShapeRayHitHandler
        => VoxelShapeHelpers.RayTest(ref this, pose, ray, ref maximumT, ref hitHandler);

    public void RayTest<TRayHitHandler>(in NRigidPose pose, ref RaySource rays, BufferPool pool, ref TRayHitHandler hitHandler)
        where TRayHitHandler : struct, IShapeRayHitHandler
        => VoxelShapeHelpers.RayTest(ref this, pose, ref rays, ref hitHandler);

    public readonly void FindLocalOverlaps<TOverlaps, TSubpairOverlaps>(ref Buffer<OverlapQueryForPair> pairs, BufferPool pool, Shapes shapes, ref TOverlaps overlaps)
        where TOverlaps : struct, ICollisionTaskOverlaps<TSubpairOverlaps>
        where TSubpairOverlaps : struct, ICollisionTaskSubpairOverlaps
        => VoxelShapeHelpers.FindLocalOverlaps<VoxelBoxShape, TOverlaps, TSubpairOverlaps>(ref pairs, pool, ref overlaps);

    public void FindLocalOverlaps<TOverlaps>(Vector3 min, Vector3 max, Vector3 sweep, float maximumT, BufferPool pool, Shapes shapes, void* overlaps)
        where TOverlaps : ICollisionTaskSubpairOverlaps
        => VoxelShapeHelpers.FindLocalOverlaps<VoxelBoxShape, TOverlaps>(min, max, sweep, maximumT, pool, overlaps, ref this);

    public void FindLocalOverlaps<TEnumerator>(Vector3 min, Vector3 max, BufferPool pool, Shapes shapes, ref TEnumerator enumerator)
        where TEnumerator : IBreakableForEach<int>
        => VoxelShapeHelpers.EnumerateOverlaps(ref this, min, max, ref enumerator);

    /// <summary>The grid buffer is owned by the collider, not by the shape; nothing to release here.</summary>
    public readonly void Dispose(BufferPool pool) { }
}

/// <summary>
/// A voxel grid presented to the narrow phase as one sphere inscribed in each occupied cell.
/// </summary>
public unsafe struct VoxelSphereShape : IHomogeneousCompoundShape<Sphere, SphereWide>, IBoundsQueryableCompound, IVoxelShape
{
    public const int Id = 13;
    public static int TypeId => Id;
    public static int ChildShapeTypeId => Sphere.Id;
    public static int MaxChildrenPerCell => 1;

    public VoxelGridData GridData;
    public readonly VoxelGridData Grid => GridData;

    public readonly int ChildCount => GridData.CellCount;

    public static ShapeBatch CreateShapeBatch(BufferPool pool, int initialCapacity, Shapes shapeBatches)
        => new HomogeneousCompoundShapeBatch<VoxelSphereShape, Sphere, SphereWide>(pool, initialCapacity);

    public readonly int GetCellChildren(int cx, int cy, int cz, Span<int> childIndices)
    {
        if (!GridData.CellIsSolid(cx, cy, cz))
            return 0;
        childIndices[0] = GridData.CellIndex(cx, cy, cz);
        return 1;
    }

    public readonly void GetLocalChild(int childIndex, out Sphere childShape)
        => childShape = new Sphere(GridData.CellSize * 0.5f);

    public readonly void GetPosedLocalChild(int childIndex, out Sphere childShape, out NRigidPose childPose)
    {
        GetLocalChild(childIndex, out childShape);
        GridData.DecomposeCell(childIndex, out var cx, out var cy, out var cz);
        childPose = new NRigidPose(GridData.CellCentre(cx, cy, cz));
    }

    public readonly void GetLocalChild(int childIndex, ref SphereWide childShapeWide)
        => GatherScatter.GetFirst(ref childShapeWide.Radius) = GridData.CellSize * 0.5f;

    public readonly bool RayTestChild(int childIndex, Vector3 origin, Vector3 direction, out float t, out Vector3 normal)
    {
        GetPosedLocalChild(childIndex, out var sphere, out var pose);
        return sphere.RayTest(pose, origin, direction, out t, out normal);
    }

    public readonly int WriteChildShapeData(int childIndex, out Vector3 localPosition, void* destination)
    {
        GridData.DecomposeCell(childIndex, out var cx, out var cy, out var cz);
        localPosition = GridData.CellCentre(cx, cy, cz);
        var radius = GridData.CellSize * 0.5f;
        Unsafe.Write(destination, radius);
        return sizeof(Sphere);
    }

    public readonly void ComputeBounds(Quaternion orientation, out Vector3 min, out Vector3 max)
        => VoxelShapeHelpers.ComputeBounds(GridData, orientation, out min, out max);

    public void RayTest<TRayHitHandler>(in NRigidPose pose, in RayData ray, ref float maximumT, BufferPool pool, ref TRayHitHandler hitHandler)
        where TRayHitHandler : struct, IShapeRayHitHandler
        => VoxelShapeHelpers.RayTest(ref this, pose, ray, ref maximumT, ref hitHandler);

    public void RayTest<TRayHitHandler>(in NRigidPose pose, ref RaySource rays, BufferPool pool, ref TRayHitHandler hitHandler)
        where TRayHitHandler : struct, IShapeRayHitHandler
        => VoxelShapeHelpers.RayTest(ref this, pose, ref rays, ref hitHandler);

    public readonly void FindLocalOverlaps<TOverlaps, TSubpairOverlaps>(ref Buffer<OverlapQueryForPair> pairs, BufferPool pool, Shapes shapes, ref TOverlaps overlaps)
        where TOverlaps : struct, ICollisionTaskOverlaps<TSubpairOverlaps>
        where TSubpairOverlaps : struct, ICollisionTaskSubpairOverlaps
        => VoxelShapeHelpers.FindLocalOverlaps<VoxelSphereShape, TOverlaps, TSubpairOverlaps>(ref pairs, pool, ref overlaps);

    public void FindLocalOverlaps<TOverlaps>(Vector3 min, Vector3 max, Vector3 sweep, float maximumT, BufferPool pool, Shapes shapes, void* overlaps)
        where TOverlaps : ICollisionTaskSubpairOverlaps
        => VoxelShapeHelpers.FindLocalOverlaps<VoxelSphereShape, TOverlaps>(min, max, sweep, maximumT, pool, overlaps, ref this);

    public void FindLocalOverlaps<TEnumerator>(Vector3 min, Vector3 max, BufferPool pool, Shapes shapes, ref TEnumerator enumerator)
        where TEnumerator : IBreakableForEach<int>
        => VoxelShapeHelpers.EnumerateOverlaps(ref this, min, max, ref enumerator);

    public readonly void Dispose(BufferPool pool) { }
}

/// <summary>
/// A voxel grid presented to the narrow phase as the triangles of its iso-surface, generated by
/// either marching cubes or surface nets from the same field the renderer meshes.
/// </summary>
/// <remarks>
/// One shape covers both algorithms: they differ only in how a cell turns into triangles, and both
/// address at most <see cref="VoxelGridData.MaxSurfaceNetsTrianglesPerCell"/> children per cell.
/// Marching cubes uses five of those six slots and leaves the last empty, which costs nothing -
/// slots are an indexing convention, never storage.
/// </remarks>
public unsafe struct VoxelTriangleShape : IHomogeneousCompoundShape<Triangle, TriangleWide>, IBoundsQueryableCompound, IVoxelShape
{
    public const int Id = 14;
    public static int TypeId => Id;
    public static int ChildShapeTypeId => Triangle.Id;
    public static int MaxChildrenPerCell => SlotsPerCell;

    /// <summary>Child index granularity: childIndex = cellIndex * SlotsPerCell + slot.</summary>
    public const int SlotsPerCell = VoxelGridData.MaxSurfaceNetsTrianglesPerCell;

    public VoxelGridData GridData;

    /// <summary>Surface nets when set, marching cubes when clear.</summary>
    public bool SurfaceNets;

    public readonly VoxelGridData Grid => GridData;

    public readonly int ChildCount => GridData.CellCount * SlotsPerCell;

    public static ShapeBatch CreateShapeBatch(BufferPool pool, int initialCapacity, Shapes shapeBatches)
        => new HomogeneousCompoundShapeBatch<VoxelTriangleShape, Triangle, TriangleWide>(pool, initialCapacity);

    private readonly bool TryGetTriangle(int cx, int cy, int cz, int slot, out Triangle triangle)
        => SurfaceNets
            ? GridData.TryGetSurfaceNetsTriangle(cx, cy, cz, slot, out triangle)
            : slot < VoxelGridData.MaxMarchingCubesTrianglesPerCell
                ? GridData.TryGetMarchingCubesTriangle(cx, cy, cz, slot, out triangle)
                : Miss(out triangle);

    private static bool Miss(out Triangle triangle)
    {
        triangle = default;
        return false;
    }

    public readonly int GetCellChildren(int cx, int cy, int cz, Span<int> childIndices)
    {
        var cellIndex = GridData.CellIndex(cx, cy, cz);
        var count = 0;
        for (int slot = 0; slot < SlotsPerCell; ++slot)
        {
            if (TryGetTriangle(cx, cy, cz, slot, out _))
                childIndices[count++] = cellIndex * SlotsPerCell + slot;
        }
        return count;
    }

    public readonly void GetLocalChild(int childIndex, out Triangle childShape)
    {
        GridData.DecomposeCell(childIndex / SlotsPerCell, out var cx, out var cy, out var cz);
        // A slot with no triangle can only be reached by a caller that did not get the index from
        // GetCellChildren; a degenerate triangle collides with nothing, which is the right answer.
        TryGetTriangle(cx, cy, cz, childIndex % SlotsPerCell, out childShape);
    }

    public readonly void GetPosedLocalChild(int childIndex, out Triangle childShape, out NRigidPose childPose)
    {
        GetLocalChild(childIndex, out childShape);
        var centroid = (childShape.A + childShape.B + childShape.C) * (1f / 3f);
        childPose = new NRigidPose(centroid);
        childShape.A -= centroid;
        childShape.B -= centroid;
        childShape.C -= centroid;
    }

    public readonly void GetLocalChild(int childIndex, ref TriangleWide childShapeWide)
    {
        GetLocalChild(childIndex, out var triangle);
        Vector3Wide.WriteFirst(triangle.A, ref childShapeWide.A);
        Vector3Wide.WriteFirst(triangle.B, ref childShapeWide.B);
        Vector3Wide.WriteFirst(triangle.C, ref childShapeWide.C);
    }

    public readonly bool RayTestChild(int childIndex, Vector3 origin, Vector3 direction, out float t, out Vector3 normal)
    {
        GetLocalChild(childIndex, out var triangle);
        return Triangle.RayTest(triangle.A, triangle.B, triangle.C, origin, direction, out t, out normal);
    }

    public readonly int WriteChildShapeData(int childIndex, out Vector3 localPosition, void* destination)
    {
        GetPosedLocalChild(childIndex, out var triangle, out var pose);
        localPosition = pose.Position;
        Unsafe.Write(destination, triangle);
        return sizeof(Triangle);
    }

    public readonly void ComputeBounds(Quaternion orientation, out Vector3 min, out Vector3 max)
        => VoxelShapeHelpers.ComputeBounds(GridData, orientation, out min, out max);

    public void RayTest<TRayHitHandler>(in NRigidPose pose, in RayData ray, ref float maximumT, BufferPool pool, ref TRayHitHandler hitHandler)
        where TRayHitHandler : struct, IShapeRayHitHandler
        => VoxelShapeHelpers.RayTest(ref this, pose, ray, ref maximumT, ref hitHandler);

    public void RayTest<TRayHitHandler>(in NRigidPose pose, ref RaySource rays, BufferPool pool, ref TRayHitHandler hitHandler)
        where TRayHitHandler : struct, IShapeRayHitHandler
        => VoxelShapeHelpers.RayTest(ref this, pose, ref rays, ref hitHandler);

    public readonly void FindLocalOverlaps<TOverlaps, TSubpairOverlaps>(ref Buffer<OverlapQueryForPair> pairs, BufferPool pool, Shapes shapes, ref TOverlaps overlaps)
        where TOverlaps : struct, ICollisionTaskOverlaps<TSubpairOverlaps>
        where TSubpairOverlaps : struct, ICollisionTaskSubpairOverlaps
        => VoxelShapeHelpers.FindLocalOverlaps<VoxelTriangleShape, TOverlaps, TSubpairOverlaps>(ref pairs, pool, ref overlaps);

    public void FindLocalOverlaps<TOverlaps>(Vector3 min, Vector3 max, Vector3 sweep, float maximumT, BufferPool pool, Shapes shapes, void* overlaps)
        where TOverlaps : ICollisionTaskSubpairOverlaps
        => VoxelShapeHelpers.FindLocalOverlaps<VoxelTriangleShape, TOverlaps>(min, max, sweep, maximumT, pool, overlaps, ref this);

    public void FindLocalOverlaps<TEnumerator>(Vector3 min, Vector3 max, BufferPool pool, Shapes shapes, ref TEnumerator enumerator)
        where TEnumerator : IBreakableForEach<int>
        => VoxelShapeHelpers.EnumerateOverlaps(ref this, min, max, ref enumerator);

    public readonly void Dispose(BufferPool pool) { }
}
