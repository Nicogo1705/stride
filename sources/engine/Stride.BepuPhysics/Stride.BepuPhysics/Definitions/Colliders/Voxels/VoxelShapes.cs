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
/// What the shared voxel machinery needs from each voxel shape.
/// </summary>
/// <remarks>
/// Bounds, bounding box queries and ray traversal are the same grid walk for every shape, written
/// once in <see cref="VoxelShapeHelpers"/>. Not generic over the density source: the walk only needs the cell layout.
/// </remarks>
public unsafe interface IVoxelShape
{
    /// <summary>Bepu type id of the child shape: Box.Id, Sphere.Id or Triangle.Id.</summary>
    static abstract int ChildShapeTypeId { get; }

    /// <summary>Upper bound on what <see cref="GetCellChildren"/> can report for one cell.</summary>
    static abstract int MaxChildrenPerCell { get; }

    /// <summary>How many cells beyond its owning cell a child may reach.</summary>
    /// <remarks>Zero when children sit inside their own cell; one for surface nets, whose quads span four neighbouring cells.</remarks>
    static abstract int ChildReach { get; }

    /// <summary>Cells along X.</summary>
    int CellsX { get; }
    /// <summary>Cells along Y.</summary>
    int CellsY { get; }
    /// <summary>Cells along Z.</summary>
    int CellsZ { get; }
    /// <summary>Edge length of one cell, in world units.</summary>
    float CellSize { get; }

    /// <summary>Children this cell contributes, as child indices.</summary>
    /// <remarks>
    /// Decides existence from a few sample reads without building geometry; <c>GetLocalChild</c> and
    /// <see cref="RayTestChild"/> build it only for children actually tested.
    /// </remarks>
    int GetCellChildren(int cx, int cy, int cz, Span<int> childIndices);

    /// <summary>Tests one child against a ray already expressed in the shape's local space.</summary>
    bool RayTestChild(int childIndex, Vector3 origin, Vector3 direction, out float t, out Vector3 normal);

    /// <summary>Writes a child's convex shape data to <paramref name="destination"/> and its local position. Returns the byte count written.</summary>
    int WriteChildShapeData(int childIndex, out Vector3 localPosition, void* destination);
}

/// <summary>
/// The grid walks every voxel shape shares: bounds, bounding box queries, and ray traversal.
/// </summary>
/// <remarks>
/// No acceleration structure: a bounding box maps to a cell range by division and a ray walks the
/// cells it crosses in order, so there is no tree to build or refit when a voxel is edited.
/// </remarks>
public static unsafe class VoxelShapeHelpers
{
    /// <summary>Bounds of the whole grid under an orientation, from the eight corners of its box.</summary>
    public static void ComputeBounds<TShape>(ref TShape shape, Quaternion orientation, out Vector3 min, out Vector3 max)
        where TShape : unmanaged, IVoxelShape
    {
        var localMax = new Vector3(shape.CellsX, shape.CellsY, shape.CellsZ) * shape.CellSize;
        Matrix3x3.CreateFromQuaternion(orientation, out var basis);
        min = new Vector3(float.MaxValue);
        max = new Vector3(float.MinValue);
        for (int i = 0; i < 8; ++i)
        {
            var corner = new Vector3(
                (i & 1) != 0 ? localMax.X : 0f,
                (i & 2) != 0 ? localMax.Y : 0f,
                (i & 4) != 0 ? localMax.Z : 0f);
            Matrix3x3.Transform(corner, basis, out var rotated);
            min = Vector3.Min(rotated, min);
            max = Vector3.Max(rotated, max);
        }
    }

    /// <summary>Clamps a local space bounding box to the cells it can touch. False when it misses the grid.</summary>
    public static bool GetCellRange<TShape>(ref TShape shape, Vector3 min, Vector3 max, out int x0, out int y0, out int z0, out int x1, out int y1, out int z1)
        where TShape : unmanaged, IVoxelShape
    {
        var inverseCellSize = 1f / shape.CellSize;
        x0 = (int)MathF.Floor(min.X * inverseCellSize);
        y0 = (int)MathF.Floor(min.Y * inverseCellSize);
        z0 = (int)MathF.Floor(min.Z * inverseCellSize);
        x1 = (int)MathF.Floor(max.X * inverseCellSize);
        y1 = (int)MathF.Floor(max.Y * inverseCellSize);
        z1 = (int)MathF.Floor(max.Z * inverseCellSize);
        if (x1 < 0 || y1 < 0 || z1 < 0 || x0 >= shape.CellsX || y0 >= shape.CellsY || z0 >= shape.CellsZ)
        {
            x0 = y0 = z0 = 0;
            x1 = y1 = z1 = -1;
            return false;
        }
        // Widened by the reach: a child owned by a cell just outside this range can still lie
        // inside the box being asked about.
        var reach = TShape.ChildReach;
        x0 = Math.Max(x0 - reach, 0);
        y0 = Math.Max(y0 - reach, 0);
        z0 = Math.Max(z0 - reach, 0);
        x1 = Math.Min(x1 + reach, shape.CellsX - 1);
        y1 = Math.Min(y1 + reach, shape.CellsY - 1);
        z1 = Math.Min(z1 + reach, shape.CellsZ - 1);
        return true;
    }

    /// <summary>Reports every child overlapping a local space bounding box to a breakable enumerator.</summary>
    public static void EnumerateOverlaps<TShape, TEnumerator>(ref TShape shape, Vector3 min, Vector3 max, ref TEnumerator enumerator)
        where TShape : unmanaged, IVoxelShape
        where TEnumerator : IBreakableForEach<int>
    {
        if (!GetCellRange(ref shape, min, max, out var x0, out var y0, out var z0, out var x1, out var y1, out var z1))
            return;
        Span<int> children = stackalloc int[TShape.MaxChildrenPerCell];
        // z varies fastest, matching the sample layout, so the walk runs along cache lines.
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
    /// The swept query used by sweep tests. The box is expanded to cover the whole swept volume,
    /// which is conservative: it may report a child the sweep misses, never the reverse.
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

    /// <summary>Walks the cells a ray crosses, in order, testing the children of each.</summary>
    /// <remarks>
    /// A 3D DDA: clip the ray to the grid box, then step along the axis whose boundary is nearest.
    /// Cells come in increasing distance, so a handler narrowing <paramref name="maximumT"/> stops the walk early.
    /// </remarks>
    public static void RayTest<TShape, TRayHitHandler>(ref TShape shape, in NRigidPose pose, in RayData ray, ref float maximumT, ref TRayHitHandler hitHandler)
        where TShape : unmanaged, IVoxelShape
        where TRayHitHandler : struct, IShapeRayHitHandler
    {
        Matrix3x3.CreateFromQuaternion(pose.Orientation, out var orientation);
        Matrix3x3.TransformTranspose(ray.Origin - pose.Position, orientation, out var origin);
        Matrix3x3.TransformTranspose(ray.Direction, orientation, out var direction);

        var cellSize = shape.CellSize;
        var cellsX = shape.CellsX;
        var cellsY = shape.CellsY;
        var cellsZ = shape.CellsZ;
        var boundsMax = new Vector3(cellsX, cellsY, cellsZ) * cellSize;
        if (!TryClipToBox(origin, direction, boundsMax, maximumT, out var tEnter, out var tExit))
            return;

        var entry = origin + direction * tEnter;
        var x = Math.Clamp((int)MathF.Floor(entry.X / cellSize), 0, cellsX - 1);
        var y = Math.Clamp((int)MathF.Floor(entry.Y / cellSize), 0, cellsY - 1);
        var z = Math.Clamp((int)MathF.Floor(entry.Z / cellSize), 0, cellsZ - 1);

        ComputeAxisStep(origin.X, direction.X, x, cellSize, tEnter, out var stepX, out var tMaxX, out var tDeltaX);
        ComputeAxisStep(origin.Y, direction.Y, y, cellSize, tEnter, out var stepY, out var tMaxY, out var tDeltaY);
        ComputeAxisStep(origin.Z, direction.Z, z, cellSize, tEnter, out var stepZ, out var tMaxZ, out var tDeltaZ);

        Span<int> children = stackalloc int[TShape.MaxChildrenPerCell];
        var reach = TShape.ChildReach;
        var t = tEnter;
        while (t <= tExit && t <= maximumT)
        {
            // Test the block around this cell, not the cell alone: a child owned by a neighbour can
            // cover the point the ray is passing through (see ChildReach).
            for (int ox = -reach; ox <= reach; ++ox)
            {
                for (int oy = -reach; oy <= reach; ++oy)
                {
                    for (int oz = -reach; oz <= reach; ++oz)
                    {
                        var cx = x + ox;
                        var cy = y + oy;
                        var cz = z + oz;
                        if ((uint)cx >= (uint)cellsX || (uint)cy >= (uint)cellsY || (uint)cz >= (uint)cellsZ)
                            continue;

                        var count = shape.GetCellChildren(cx, cy, cz, children);
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
                    }
                }
            }

            if (tMaxX < tMaxY && tMaxX < tMaxZ)
            {
                x += stepX;
                t = tMaxX;
                tMaxX += tDeltaX;
                if ((uint)x >= (uint)cellsX)
                    return;
            }
            else if (tMaxY < tMaxZ)
            {
                y += stepY;
                t = tMaxY;
                tMaxY += tDeltaY;
                if ((uint)y >= (uint)cellsY)
                    return;
            }
            else
            {
                z += stepZ;
                t = tMaxZ;
                tMaxZ += tDeltaZ;
                if ((uint)z >= (uint)cellsZ)
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

    /// <summary>Slab test clipping a ray to the grid box, bounded by the ray's maximum length.</summary>
    private static bool TryClipToBox(Vector3 origin, Vector3 direction, Vector3 max, float maximumT, out float tEnter, out float tExit)
    {
        tEnter = 0f;
        tExit = maximumT;
        for (int axis = 0; axis < 3; ++axis)
        {
            var o = axis == 0 ? origin.X : axis == 1 ? origin.Y : origin.Z;
            var d = axis == 0 ? direction.X : axis == 1 ? direction.Y : direction.Z;
            var hi = axis == 0 ? max.X : axis == 1 ? max.Y : max.Z;
            if (MathF.Abs(d) < 1e-9f)
            {
                if (o < 0f || o > hi)
                    return false;
                continue;
            }
            var inverse = 1f / d;
            var t0 = -o * inverse;
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
public unsafe struct VoxelBoxShape<TSource> : IHomogeneousCompoundShape<Box, BoxWide>, IVoxelShape
    where TSource : unmanaged, IVoxelDensitySource
{
    /// <summary>The first of the three ids <typeparamref name="TSource"/> reserves.</summary>
    public static int TypeId => TSource.ShapeTypeIdBase;
    public static int ChildShapeTypeId => Box.Id;
    public static int MaxChildrenPerCell => 1;
    public static int ChildReach => 0;

    public VoxelGridData<TSource> GridData;

    public readonly int CellsX => GridData.CellsX;
    public readonly int CellsY => GridData.CellsY;
    public readonly int CellsZ => GridData.CellsZ;
    public readonly float CellSize => GridData.CellSize;

    public readonly int ChildCount => GridData.CellCount;

    public static ShapeBatch CreateShapeBatch(BufferPool pool, int initialCapacity, Shapes shapeBatches)
        => new HomogeneousCompoundShapeBatch<VoxelBoxShape<TSource>, Box, BoxWide>(pool, initialCapacity);

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
        Unsafe.Write(destination, new Vector3(GridData.CellSize * 0.5f));
        return sizeof(Box);
    }

    public void ComputeBounds(Quaternion orientation, out Vector3 min, out Vector3 max)
        => VoxelShapeHelpers.ComputeBounds(ref this, orientation, out min, out max);

    public void RayTest<TRayHitHandler>(in NRigidPose pose, in RayData ray, ref float maximumT, BufferPool pool, ref TRayHitHandler hitHandler)
        where TRayHitHandler : struct, IShapeRayHitHandler
        => VoxelShapeHelpers.RayTest(ref this, pose, ray, ref maximumT, ref hitHandler);

    public void RayTest<TRayHitHandler>(in NRigidPose pose, ref RaySource rays, BufferPool pool, ref TRayHitHandler hitHandler)
        where TRayHitHandler : struct, IShapeRayHitHandler
        => VoxelShapeHelpers.RayTest(ref this, pose, ref rays, ref hitHandler);

    public readonly void FindLocalOverlaps<TOverlaps, TSubpairOverlaps>(ref Buffer<OverlapQueryForPair> pairs, BufferPool pool, Shapes shapes, ref TOverlaps overlaps)
        where TOverlaps : struct, ICollisionTaskOverlaps<TSubpairOverlaps>
        where TSubpairOverlaps : struct, ICollisionTaskSubpairOverlaps
        => VoxelShapeHelpers.FindLocalOverlaps<VoxelBoxShape<TSource>, TOverlaps, TSubpairOverlaps>(ref pairs, pool, ref overlaps);

    public void FindLocalOverlaps<TOverlaps>(Vector3 min, Vector3 max, Vector3 sweep, float maximumT, BufferPool pool, Shapes shapes, void* overlaps)
        where TOverlaps : ICollisionTaskSubpairOverlaps
        => VoxelShapeHelpers.FindLocalOverlaps<VoxelBoxShape<TSource>, TOverlaps>(min, max, sweep, maximumT, pool, overlaps, ref this);

    public void FindLocalOverlaps<TEnumerator>(Vector3 min, Vector3 max, BufferPool pool, Shapes shapes, ref TEnumerator enumerator)
        where TEnumerator : IBreakableForEach<int>
        => VoxelShapeHelpers.EnumerateOverlaps(ref this, min, max, ref enumerator);

    /// <summary>The samples belong to the collider, not to the shape; nothing to release here.</summary>
    public readonly void Dispose(BufferPool pool) { }
}

/// <summary>
/// A voxel grid presented to the narrow phase as one sphere inscribed in each occupied cell.
/// </summary>
public unsafe struct VoxelSphereShape<TSource> : IHomogeneousCompoundShape<Sphere, SphereWide>, IVoxelShape
    where TSource : unmanaged, IVoxelDensitySource
{
    /// <summary>The second of the three ids <typeparamref name="TSource"/> reserves.</summary>
    public static int TypeId => TSource.ShapeTypeIdBase + 1;
    public static int ChildShapeTypeId => Sphere.Id;
    public static int MaxChildrenPerCell => 1;
    public static int ChildReach => 0;

    public VoxelGridData<TSource> GridData;

    public readonly int CellsX => GridData.CellsX;
    public readonly int CellsY => GridData.CellsY;
    public readonly int CellsZ => GridData.CellsZ;
    public readonly float CellSize => GridData.CellSize;

    public readonly int ChildCount => GridData.CellCount;

    public static ShapeBatch CreateShapeBatch(BufferPool pool, int initialCapacity, Shapes shapeBatches)
        => new HomogeneousCompoundShapeBatch<VoxelSphereShape<TSource>, Sphere, SphereWide>(pool, initialCapacity);

    public readonly int GetCellChildren(int cx, int cy, int cz, Span<int> childIndices)
    {
        if (!GridData.CellIsSolid(cx, cy, cz))
            return 0;
        childIndices[0] = GridData.CellIndex(cx, cy, cz);
        return 1;
    }

    public readonly void GetLocalChild(int childIndex, out Sphere childShape)
        => childShape = new Sphere(GridData.SphereRadius);

    public readonly void GetPosedLocalChild(int childIndex, out Sphere childShape, out NRigidPose childPose)
    {
        GetLocalChild(childIndex, out childShape);
        GridData.DecomposeCell(childIndex, out var cx, out var cy, out var cz);
        childPose = new NRigidPose(GridData.CellCentre(cx, cy, cz));
    }

    public readonly void GetLocalChild(int childIndex, ref SphereWide childShapeWide)
        => GatherScatter.GetFirst(ref childShapeWide.Radius) = GridData.SphereRadius;

    public readonly bool RayTestChild(int childIndex, Vector3 origin, Vector3 direction, out float t, out Vector3 normal)
    {
        GetPosedLocalChild(childIndex, out var sphere, out var pose);
        return sphere.RayTest(pose, origin, direction, out t, out normal);
    }

    public readonly int WriteChildShapeData(int childIndex, out Vector3 localPosition, void* destination)
    {
        GridData.DecomposeCell(childIndex, out var cx, out var cy, out var cz);
        localPosition = GridData.CellCentre(cx, cy, cz);
        Unsafe.Write(destination, GridData.CellSize * 0.5f);
        return sizeof(Sphere);
    }

    public void ComputeBounds(Quaternion orientation, out Vector3 min, out Vector3 max)
        => VoxelShapeHelpers.ComputeBounds(ref this, orientation, out min, out max);

    public void RayTest<TRayHitHandler>(in NRigidPose pose, in RayData ray, ref float maximumT, BufferPool pool, ref TRayHitHandler hitHandler)
        where TRayHitHandler : struct, IShapeRayHitHandler
        => VoxelShapeHelpers.RayTest(ref this, pose, ray, ref maximumT, ref hitHandler);

    public void RayTest<TRayHitHandler>(in NRigidPose pose, ref RaySource rays, BufferPool pool, ref TRayHitHandler hitHandler)
        where TRayHitHandler : struct, IShapeRayHitHandler
        => VoxelShapeHelpers.RayTest(ref this, pose, ref rays, ref hitHandler);

    public readonly void FindLocalOverlaps<TOverlaps, TSubpairOverlaps>(ref Buffer<OverlapQueryForPair> pairs, BufferPool pool, Shapes shapes, ref TOverlaps overlaps)
        where TOverlaps : struct, ICollisionTaskOverlaps<TSubpairOverlaps>
        where TSubpairOverlaps : struct, ICollisionTaskSubpairOverlaps
        => VoxelShapeHelpers.FindLocalOverlaps<VoxelSphereShape<TSource>, TOverlaps, TSubpairOverlaps>(ref pairs, pool, ref overlaps);

    public void FindLocalOverlaps<TOverlaps>(Vector3 min, Vector3 max, Vector3 sweep, float maximumT, BufferPool pool, Shapes shapes, void* overlaps)
        where TOverlaps : ICollisionTaskSubpairOverlaps
        => VoxelShapeHelpers.FindLocalOverlaps<VoxelSphereShape<TSource>, TOverlaps>(min, max, sweep, maximumT, pool, overlaps, ref this);

    public void FindLocalOverlaps<TEnumerator>(Vector3 min, Vector3 max, BufferPool pool, Shapes shapes, ref TEnumerator enumerator)
        where TEnumerator : IBreakableForEach<int>
        => VoxelShapeHelpers.EnumerateOverlaps(ref this, min, max, ref enumerator);

    public readonly void Dispose(BufferPool pool) { }
}

/// <summary>
/// A voxel grid presented to the narrow phase as the triangles of its iso-surface, from either
/// marching cubes or surface nets.
/// </summary>
/// <remarks>Both algorithms address at most six children per cell; marching cubes uses five slots. Slots are an indexing convention, not storage.</remarks>
public unsafe struct VoxelTriangleShape<TSource> : IHomogeneousCompoundShape<Triangle, TriangleWide>, IVoxelShape
    where TSource : unmanaged, IVoxelDensitySource
{
    /// <summary>The third of the three ids <typeparamref name="TSource"/> reserves.</summary>
    public static int TypeId => TSource.ShapeTypeIdBase + 2;
    public static int ChildShapeTypeId => Triangle.Id;
    public static int MaxChildrenPerCell => SlotsPerCell;

    /// <summary>One: surface-nets quads span the four cells around an edge, and marching-cubes triangles can touch any face of the cell.</summary>
    public static int ChildReach => 1;

    /// <summary>Child index granularity: childIndex = cellIndex * SlotsPerCell + slot.</summary>
    public const int SlotsPerCell = VoxelGridData<TSource>.MaxSurfaceNetsTrianglesPerCell;

    public VoxelGridData<TSource> GridData;

    /// <summary>Surface nets when set, marching cubes when clear.</summary>
    public bool SurfaceNets;

    public readonly int CellsX => GridData.CellsX;
    public readonly int CellsY => GridData.CellsY;
    public readonly int CellsZ => GridData.CellsZ;
    public readonly float CellSize => GridData.CellSize;

    public readonly int ChildCount => GridData.CellCount * SlotsPerCell;

    public static ShapeBatch CreateShapeBatch(BufferPool pool, int initialCapacity, Shapes shapeBatches)
        => new HomogeneousCompoundShapeBatch<VoxelTriangleShape<TSource>, Triangle, TriangleWide>(pool, initialCapacity);

    /// <summary>Which slots of a cell carry a triangle, without building any of them.</summary>
    /// <remarks>Marching cubes classifies the cell and reads the case table; surface nets tests three edges for a sign change.</remarks>
    public readonly int GetCellChildren(int cx, int cy, int cz, Span<int> childIndices)
    {
        var cellIndex = GridData.CellIndex(cx, cy, cz) * SlotsPerCell;
        var count = 0;
        if (SurfaceNets)
        {
            for (int axis = 0; axis < 3; ++axis)
            {
                if (!GridData.SurfaceNetsEdgeStraddles(cx, cy, cz, axis, out _))
                    continue;
                childIndices[count++] = cellIndex + axis * 2;
                childIndices[count++] = cellIndex + axis * 2 + 1;
            }
        }
        else
        {
            var triangles = VoxelGridData<TSource>.MarchingCubesTriangleCount(GridData.CubeIndex(cx, cy, cz));
            for (int slot = 0; slot < triangles; ++slot)
                childIndices[count++] = cellIndex + slot;
        }
        return count;
    }

    private readonly bool TryGetTriangle(int cx, int cy, int cz, int slot, out Triangle triangle)
        => SurfaceNets
            ? GridData.TryGetSurfaceNetsTriangle(cx, cy, cz, slot, out triangle)
            : GridData.TryGetMarchingCubesTriangle(cx, cy, cz, GridData.CubeIndex(cx, cy, cz), slot, out triangle);

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

    public void ComputeBounds(Quaternion orientation, out Vector3 min, out Vector3 max)
        => VoxelShapeHelpers.ComputeBounds(ref this, orientation, out min, out max);

    public void RayTest<TRayHitHandler>(in NRigidPose pose, in RayData ray, ref float maximumT, BufferPool pool, ref TRayHitHandler hitHandler)
        where TRayHitHandler : struct, IShapeRayHitHandler
        => VoxelShapeHelpers.RayTest(ref this, pose, ray, ref maximumT, ref hitHandler);

    public void RayTest<TRayHitHandler>(in NRigidPose pose, ref RaySource rays, BufferPool pool, ref TRayHitHandler hitHandler)
        where TRayHitHandler : struct, IShapeRayHitHandler
        => VoxelShapeHelpers.RayTest(ref this, pose, ref rays, ref hitHandler);

    public readonly void FindLocalOverlaps<TOverlaps, TSubpairOverlaps>(ref Buffer<OverlapQueryForPair> pairs, BufferPool pool, Shapes shapes, ref TOverlaps overlaps)
        where TOverlaps : struct, ICollisionTaskOverlaps<TSubpairOverlaps>
        where TSubpairOverlaps : struct, ICollisionTaskSubpairOverlaps
        => VoxelShapeHelpers.FindLocalOverlaps<VoxelTriangleShape<TSource>, TOverlaps, TSubpairOverlaps>(ref pairs, pool, ref overlaps);

    public void FindLocalOverlaps<TOverlaps>(Vector3 min, Vector3 max, Vector3 sweep, float maximumT, BufferPool pool, Shapes shapes, void* overlaps)
        where TOverlaps : ICollisionTaskSubpairOverlaps
        => VoxelShapeHelpers.FindLocalOverlaps<VoxelTriangleShape<TSource>, TOverlaps>(min, max, sweep, maximumT, pool, overlaps, ref this);

    public void FindLocalOverlaps<TEnumerator>(Vector3 min, Vector3 max, BufferPool pool, Shapes shapes, ref TEnumerator enumerator)
        where TEnumerator : IBreakableForEach<int>
        => VoxelShapeHelpers.EnumerateOverlaps(ref this, min, max, ref enumerator);

    public readonly void Dispose(BufferPool pool) { }
}
