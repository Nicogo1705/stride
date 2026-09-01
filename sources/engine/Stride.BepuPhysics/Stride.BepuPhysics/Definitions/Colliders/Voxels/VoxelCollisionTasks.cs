// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.CollisionDetection.CollisionTasks;
using BepuPhysics.CollisionDetection.SweepTasks;

namespace Stride.BepuPhysics.Definitions.Colliders.Voxels;

/// <summary>
/// Teaches a simulation how to collide and sweep against the voxel shapes.
/// </summary>
/// <remarks>
/// Bepu discovers shape types through registered tasks rather than reflection, so every pairing has
/// to be spelled out: each of the six convex types against each voxel shape, plus the two compound
/// types, plus the sweep equivalents. That is twenty-four tasks per voxel shape and seventy-two in
/// total, which sounds worse than it is - they are empty generic instantiations, and the cost is
/// paid once when the simulation is created.
/// </remarks>
public static class VoxelCollisionTasks
{
    /// <summary>
    /// Registers every collision and sweep task for the three voxel shapes. Call once per
    /// simulation, after it has been created.
    /// </summary>
    public static void Register(Simulation simulation)
    {
        Register<VoxelBoxShape, Box, BoxWide>(simulation);
        Register<VoxelSphereShape, Sphere, SphereWide>(simulation);
        Register<VoxelTriangleShape, Triangle, TriangleWide>(simulation);
    }

    private static void Register<TShape, TChild, TChildWide>(Simulation simulation)
        where TShape : unmanaged, IHomogeneousCompoundShape<TChild, TChildWide>, IVoxelShape
        where TChild : unmanaged, IConvexShape
        where TChildWide : unmanaged, IShapeWide<TChild>
    {
        var collisions = simulation.NarrowPhase.CollisionTaskRegistry;
        collisions.Register(new ConvexCompoundCollisionTask<Sphere, TShape, ConvexCompoundOverlapFinder<Sphere, SphereWide, TShape>, ConvexVoxelContinuations<TShape>, NonconvexReduction>());
        collisions.Register(new ConvexCompoundCollisionTask<Capsule, TShape, ConvexCompoundOverlapFinder<Capsule, CapsuleWide, TShape>, ConvexVoxelContinuations<TShape>, NonconvexReduction>());
        collisions.Register(new ConvexCompoundCollisionTask<Box, TShape, ConvexCompoundOverlapFinder<Box, BoxWide, TShape>, ConvexVoxelContinuations<TShape>, NonconvexReduction>());
        collisions.Register(new ConvexCompoundCollisionTask<Triangle, TShape, ConvexCompoundOverlapFinder<Triangle, TriangleWide, TShape>, ConvexVoxelContinuations<TShape>, NonconvexReduction>());
        collisions.Register(new ConvexCompoundCollisionTask<Cylinder, TShape, ConvexCompoundOverlapFinder<Cylinder, CylinderWide, TShape>, ConvexVoxelContinuations<TShape>, NonconvexReduction>());
        collisions.Register(new ConvexCompoundCollisionTask<ConvexHull, TShape, ConvexCompoundOverlapFinder<ConvexHull, ConvexHullWide, TShape>, ConvexVoxelContinuations<TShape>, NonconvexReduction>());

        collisions.Register(new CompoundPairCollisionTask<Compound, TShape, CompoundPairOverlapFinder<Compound, TShape>, CompoundVoxelContinuations<Compound, TShape>, NonconvexReduction>());
        collisions.Register(new CompoundPairCollisionTask<BigCompound, TShape, CompoundPairOverlapFinder<BigCompound, TShape>, CompoundVoxelContinuations<BigCompound, TShape>, NonconvexReduction>());

        // Voxels never collide with voxels: a voxel collidable is terrain, and two pieces of terrain
        // do not need contacts. Leaving that pairing unregistered is deliberate.

        var sweeps = simulation.NarrowPhase.SweepTaskRegistry;
        sweeps.Register(new ConvexHomogeneousCompoundSweepTask<Sphere, SphereWide, TShape, TChild, TChildWide, ConvexCompoundSweepOverlapFinder<Sphere, TShape>>());
        sweeps.Register(new ConvexHomogeneousCompoundSweepTask<Capsule, CapsuleWide, TShape, TChild, TChildWide, ConvexCompoundSweepOverlapFinder<Capsule, TShape>>());
        sweeps.Register(new ConvexHomogeneousCompoundSweepTask<Box, BoxWide, TShape, TChild, TChildWide, ConvexCompoundSweepOverlapFinder<Box, TShape>>());
        sweeps.Register(new ConvexHomogeneousCompoundSweepTask<Triangle, TriangleWide, TShape, TChild, TChildWide, ConvexCompoundSweepOverlapFinder<Triangle, TShape>>());
        sweeps.Register(new ConvexHomogeneousCompoundSweepTask<Cylinder, CylinderWide, TShape, TChild, TChildWide, ConvexCompoundSweepOverlapFinder<Cylinder, TShape>>());
        sweeps.Register(new ConvexHomogeneousCompoundSweepTask<ConvexHull, ConvexHullWide, TShape, TChild, TChildWide, ConvexCompoundSweepOverlapFinder<ConvexHull, TShape>>());

        sweeps.Register(new CompoundHomogeneousCompoundSweepTask<Compound, TShape, TChild, TChildWide, CompoundPairSweepOverlapFinder<Compound, TShape>>());
        sweeps.Register(new CompoundHomogeneousCompoundSweepTask<BigCompound, TShape, TChild, TChildWide, CompoundPairSweepOverlapFinder<BigCompound, TShape>>());
    }
}
