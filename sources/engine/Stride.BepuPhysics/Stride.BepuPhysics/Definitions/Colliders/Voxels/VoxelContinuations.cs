// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Numerics;
using System.Runtime.CompilerServices;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.CollisionDetection.CollisionTasks;
using BepuUtilities;
using BepuUtilities.Memory;
using NRigidPose = BepuPhysics.RigidPose;

namespace Stride.BepuPhysics.Definitions.Colliders.Voxels;

/// <summary>
/// Tells the collision batcher what to do with the manifolds produced by testing something convex
/// against the children of a voxel shape.
/// </summary>
/// <remarks>
/// <para>
/// The children are computed on the fly, so there is no stored shape for the batcher to point at.
/// Each child's convex data is written into the batcher's own shape cache as it is requested -
/// twelve bytes for a box, four for a sphere, thirty-six for a triangle - and discarded once the
/// batch flushes.
/// </para>
/// <para>
/// The manifolds are combined with a <see cref="NonconvexReduction"/>, not the
/// <see cref="MeshReduction"/> a triangle mesh would get. MeshReduction is what removes the bumps a
/// character feels when sliding across the internal edges between triangles, but in this version of
/// Bepu it is bound to the concrete <see cref="Mesh"/> type: it stores a raw pointer to the shape
/// and hands it back as a <c>Mesh*</c> at flush time, so pointing it at a voxel shape would read
/// the wrong fields. Bepu's own voxel sample makes the same trade for the same reason. The
/// consequence is real - see the remarks on <see cref="VoxelCollider"/>.
/// </para>
/// </remarks>
public unsafe struct ConvexVoxelContinuations<TShape> : IConvexCompoundContinuationHandler<NonconvexReduction>
    where TShape : unmanaged, IVoxelShape
{
    public readonly CollisionContinuationType CollisionContinuationType => CollisionContinuationType.NonconvexReduction;

    public ref NonconvexReduction CreateContinuation<TCallbacks>(
        ref CollisionBatcher<TCallbacks> collisionBatcher, int childCount, in BoundsTestedPair pair, in OverlapQueryForPair pairQuery, out int continuationIndex)
        where TCallbacks : struct, ICollisionCallbacks
        => ref collisionBatcher.NonconvexReductions.CreateContinuation(childCount, collisionBatcher.Pool, out continuationIndex);

    /// <summary>
    /// Materializes one child of the voxel shape into the batcher's shape cache. Shared with the
    /// compound handler below, which needs exactly the same thing for the B side of its pairs.
    /// </summary>
    public static void GetChildData<TCallbacks>(
        ref CollisionBatcher<TCallbacks> collisionBatcher, in BoundsTestedPair pair, int shapeTypeA, int childIndexB,
        out NRigidPose childPoseB, out int childTypeB, out void* childShapeDataB)
        where TCallbacks : struct, ICollisionCallbacks
    {
        ref var shape = ref Unsafe.AsRef<TShape>(pair.B);
        // Large enough for any of the three child types; the exact count written is returned.
        var childData = stackalloc byte[64];
        var size = shape.WriteChildShapeData(childIndexB, out var localPosition, childData);
        QuaternionEx.TransformWithoutOverlap(localPosition, pair.OrientationB, out childPoseB.Position);
        childPoseB.Orientation = Quaternion.Identity;
        childTypeB = TShape.ChildShapeTypeId;
        collisionBatcher.CacheShapeB(shapeTypeA, childTypeB, childData, size, out childShapeDataB);
    }

    public void ConfigureContinuationChild<TCallbacks>(
        ref CollisionBatcher<TCallbacks> collisionBatcher, ref NonconvexReduction continuation, int continuationChildIndex, in BoundsTestedPair pair, int shapeTypeA, int childIndexB,
        out NRigidPose childPoseB, out int childTypeB, out void* childShapeDataB)
        where TCallbacks : struct, ICollisionCallbacks
    {
        ref var continuationChild = ref continuation.Children[continuationChildIndex];
        GetChildData(ref collisionBatcher, pair, shapeTypeA, childIndexB, out childPoseB, out childTypeB, out childShapeDataB);
        if (pair.FlipMask < 0)
        {
            continuationChild.ChildIndexA = childIndexB;
            continuationChild.ChildIndexB = 0;
            continuationChild.OffsetA = childPoseB.Position;
            continuationChild.OffsetB = default;
        }
        else
        {
            continuationChild.ChildIndexA = 0;
            continuationChild.ChildIndexB = childIndexB;
            continuationChild.OffsetA = default;
            continuationChild.OffsetB = childPoseB.Position;
        }
    }
}

/// <summary>
/// The same, for a compound on the A side rather than a single convex - a ragdoll or a vehicle
/// resting on the terrain.
/// </summary>
public unsafe struct CompoundVoxelContinuations<TCompoundA, TShape> : ICompoundPairContinuationHandler<NonconvexReduction>
    where TCompoundA : ICompoundShape
    where TShape : unmanaged, IVoxelShape
{
    public readonly CollisionContinuationType CollisionContinuationType => CollisionContinuationType.NonconvexReduction;

    public ref NonconvexReduction CreateContinuation<TCallbacks>(
        ref CollisionBatcher<TCallbacks> collisionBatcher, int totalChildCount, ref Buffer<ChildOverlapsCollection> pairOverlaps, ref Buffer<OverlapQueryForPair> pairQueries, in BoundsTestedPair pair, out int continuationIndex)
        where TCallbacks : struct, ICollisionCallbacks
        => ref collisionBatcher.NonconvexReductions.CreateContinuation(totalChildCount, collisionBatcher.Pool, out continuationIndex);

    public void GetChildAData<TCallbacks>(
        ref CollisionBatcher<TCallbacks> collisionBatcher, ref NonconvexReduction continuation, in BoundsTestedPair pair, int childIndexA,
        out NRigidPose childPoseA, out int childTypeA, out void* childShapeDataA)
        where TCallbacks : struct, ICollisionCallbacks
    {
        ref var compoundA = ref Unsafe.AsRef<TCompoundA>(pair.A);
        ref var compoundChildA = ref compoundA.GetChild(childIndexA);
        Compound.GetRotatedChildPose(compoundChildA.AsPose(), pair.OrientationA, out childPoseA);
        childTypeA = compoundChildA.ShapeIndex.Type;
        collisionBatcher.Shapes[childTypeA].GetShapeData(compoundChildA.ShapeIndex.Index, out childShapeDataA, out _);
    }

    public void ConfigureContinuationChild<TCallbacks>(
        ref CollisionBatcher<TCallbacks> collisionBatcher, ref NonconvexReduction continuation, int continuationChildIndex, in BoundsTestedPair pair, int childIndexA, int childTypeA, int childIndexB, in NRigidPose childPoseA,
        out NRigidPose childPoseB, out int childTypeB, out void* childShapeDataB)
        where TCallbacks : struct, ICollisionCallbacks
    {
        ref var continuationChild = ref continuation.Children[continuationChildIndex];
        ConvexVoxelContinuations<TShape>.GetChildData(ref collisionBatcher, pair, childTypeA, childIndexB, out childPoseB, out childTypeB, out childShapeDataB);
        if (pair.FlipMask < 0)
        {
            continuationChild.ChildIndexA = childIndexB;
            continuationChild.ChildIndexB = childIndexA;
            continuationChild.OffsetA = childPoseB.Position;
            continuationChild.OffsetB = childPoseA.Position;
        }
        else
        {
            continuationChild.ChildIndexA = childIndexA;
            continuationChild.ChildIndexB = childIndexB;
            continuationChild.OffsetA = childPoseA.Position;
            continuationChild.OffsetB = childPoseB.Position;
        }
    }
}
