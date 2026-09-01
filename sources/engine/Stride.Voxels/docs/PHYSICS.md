# Voxel physics — a Bepu collidable over the voxel grid

Companion to ROADMAP.md step 4. Question answered here: can a Bepu collider take the same
voxel data and generate contacts on the fly, instead of colliding against a generated mesh?

**Yes.** Bepu v2 is built for it, and every extension point needed is public in the version
Stride ships (BepuPhysics 2.5.0-beta.28). There are two real frictions, both on the Stride
side rather than the Bepu side.

## What it replaces

The current arrangement for a voxel game on Stride.BepuPhysics: the marching-cubes compute
shader produces a mesh on the GPU, the vertex and index bytes are read back and kept on the
CPU purely so `MeshCollider` can exist, and Bepu builds a per-chunk BVH over those triangles.
Every terrain edit re-meshes, re-reads, and rebuilds that BVH.

A voxel collidable deletes all of it: no CPU-side mesh copy, no readback, no per-chunk tree
build, and an edit costs one array write.

## How it works in Bepu

`BepuPhysics.Collidables.Mesh` is not special — it is a public interface implementation, and
a voxel shape follows the same recipe:

- Implement `IHomogeneousCompoundShape<Box, BoxWide>` (public): `ChildCount`,
  `GetLocalChild`, `GetPosedLocalChild`, `ComputeBounds`, `RayTest`. Each "child" is a voxel
  as a box; nothing is stored, the child is *computed* from the packed voxel array on demand.
- Implement `BepuPhysics.CollisionDetection.CollisionTasks.IBoundsQueryableCompound`
  (public). This is the one that matters for contacts on the fly: Bepu hands it the bounding
  box of the other collidable, and the shape enumerates only the voxels overlapping it —
  a bounded triple loop over the grid, no acceleration structure needed, because a regular
  grid *is* the acceleration structure.
- Register the shape's collision tasks on `Simulation.NarrowPhase.CollisionTaskRegistry`
  (`Register` is public) after `Simulation.Create`, and give the shape a `TypeId` past the
  built-ins via `CreateShapeBatch` / `HomogeneousCompoundShapeBatch`.

This is the same shape Bepu's own voxel demo uses; it is a supported pattern, not a hack.

## The two frictions

**1. `ICollider` is partly internal.** `Stride.BepuPhysics.Definitions.Colliders.ICollider`
declares `Component`, `TryAttach`, `Detach`, `AppendModel` and `RayTest` as `internal`, so a
custom collider *cannot* be written from game code. It has to live inside
`Stride.BepuPhysics`, or in an assembly listed in its `InternalsVisibleTo` — which is
exactly how `Stride.BepuPhysics.Soft`, `._2D`, `.Navigation` and `.Debug` already extend it.

So the voxel collider is an engine-side change. Either add it to Stride.BepuPhysics, or
create a sibling package and add it to the `InternalsVisibleTo` list.

**2. The data has to be reachable from the physics thread.** The collidable holds a
reference to the game's voxel array, so writes from terrain edits and reads from the narrow
phase have to be ordered — the same discipline the chunk workers already use for
`HasDensity` / `LightDirty` release barriers.

## Shape of the surface

Voxels-as-boxes gives a blocky collision surface, which will not match a smooth
marching-cubes visual. Three options, cheapest first:

- Accept it. For a mining game the mismatch is often under the character radius.
- Emit the marching-cubes triangle for straddling cells as the child shape instead of a box
  (`IHomogeneousCompoundShape<Triangle, TriangleWide>`) — computed from the same trilinear
  field, so it matches the rendered surface exactly, still with nothing stored.
- Keep meshes for the few chunks under the player and voxels everywhere else.

The second is the one that actually matches ROADMAP.md step 2: the same iso-surface
solve, once for rendering and once for contacts, from one array.
