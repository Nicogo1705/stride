# Voxel physics — a Bepu collidable over the voxel grid

Companion to ROADMAP.md step 4, and now a description of what was built rather than a proposal.
The code lives in `Stride.BepuPhysics`, not here, because `ICollider` is partly internal — see
*Where it had to live* below.

## What it replaces

The arrangement a voxel game on `Stride.BepuPhysics` is forced into today: the marching-cubes
compute shader produces a mesh on the GPU, the vertex and index bytes are read back and kept on the
CPU purely so `MeshCollider` can exist, and Bepu builds a per-chunk bounding volume tree over those
triangles. Every terrain edit re-meshes, re-reads and rebuilds that tree.

`VoxelCollider` deletes all of it. It holds the density field and nothing else: no mesh, no
readback, no per-chunk tree, no rebuild. Digging is `SetVoxel`, one store, and the collidable's
bounds do not even change.

## The pieces

| File | Role |
| --- | --- |
| `Definitions/Colliders/VoxelCollider.cs` | The `ICollider` a `StaticComponent` or `BodyComponent` carries. Owns the sample buffer, exposes `SetData` / `SetVoxel` / `GetVoxel`. |
| `Definitions/Colliders/Voxels/VoxelChildForm.cs` | `Box`, `Sphere`, `TriangleMarchingCubes`, `TriangleSurfaceNets`. |
| `Definitions/Colliders/Voxels/VoxelGridData.cs` | The field, and every piece of geometry derived from it: cell solidity, the marching-cubes case table and edge interpolation, the surface-nets vertex and quads. |
| `Definitions/Colliders/Voxels/VoxelShapes.cs` | Three Bepu shapes — box, sphere, triangle children — plus the grid walks they share. |
| `Definitions/Colliders/Voxels/VoxelContinuations.cs` | What the collision batcher does with the resulting manifolds. |
| `Definitions/Colliders/Voxels/VoxelCollisionTasks.cs` | Registration, called once from `BepuSimulation`. |

## Data

One `ushort` per sample, density in bits 0-7 and material in bits 8-15, x-major with z varying
fastest — the layout a voxel game already uses to upload a chunk, so one array serves rendering and
collision. A grid of n cells per axis takes n+1 samples per axis, because a cell reads the eight
samples at its corners.

`SetData` copies into native memory the collider owns, so it survives attach and detach cycles and
is freed on `Dispose` or finalization. Writes through `SetVoxel` are visible to the simulation
immediately — which is the point — so make them between steps, not during one.

## How it works in Bepu

`BepuPhysics.Collidables.Mesh` is not special; it is a public interface implementation, and the
voxel shapes follow the same recipe:

- `IHomogeneousCompoundShape<TChild, TChildWide>` — one child type for the whole shape. Each child
  is *computed* from the field on demand; nothing is stored per child.
- `IBoundsQueryableCompound` — Bepu hands over the other collidable's bounding box, and the shape
  enumerates the children overlapping it. Where `Mesh` traverses a tree, this divides by the cell
  size and walks a range of indices: **a regular grid already is the acceleration structure.** Ray
  tests likewise walk the cells the ray crosses with a DDA, so a short probe touches a handful.
- Collision and sweep tasks are registered on `Simulation.NarrowPhase` after `Simulation.Create`,
  with type ids 12, 13 and 14 (Bepu's built-ins end at `Mesh` = 8).

## The forms

`Box` and `Sphere` give one child per solid cell — a cell counts as solid when the mean of its eight
corners reaches the iso level, which stays within half a cell of the rendered surface either way.
Sphere rounds off the lattice corners characters would otherwise catch on, at the cost of gaps along
cell diagonals.

`TriangleMarchingCubes` and `TriangleSurfaceNets` reproduce the rendered iso-surface exactly: same
table, same corner ordering, same air-is-set case convention, same linear edge interpolation, same
reversed emission order as the renderer's compute shader. Child indices are `cellIndex * 6 + slot`;
marching cubes uses five of the six slots and leaves one empty, which costs nothing because slots
are an indexing convention rather than storage.

Bepu triangles collide on one side only, so if the surface pushes bodies *into* the ground rather
than out of it, `InvertWinding` is the correction.

## Known limitations

**No boundary smoothing on the triangle forms.** Bepu removes the bumps a character feels crossing
internal edges with a `MeshReduction`, and in the version Stride ships (2.5.0-beta.28) that type is
bound to the concrete `Mesh`: it keeps a raw pointer to the shape and casts it back to `Mesh*` at
flush time, so aiming it at a voxel shape would read the wrong fields. Bepu's own voxel sample makes
the same trade, and its author calls boundary smoothing for voxels "a not-easy exercise". Upstream
`master` has since generalized `MeshReduction` over any homogeneous compound with triangle children
via thunks; when Stride moves to a Bepu that has them, the triangle forms can switch to
`ConvexMeshContinuations` and this limitation disappears. Until then `TriangleSurfaceNets` is the
better triangle form, having far fewer and larger triangles than marching cubes.

**Entity scale is ignored.** The grid's only scale is `CellSize`.

**Nothing is drawn in the physics debug view.** There is no mesh to hand it, and building one would
rebuild exactly what this collider exists to avoid.

**Compiled, not yet run.** Everything above builds clean against beta.28, both through the dev
package path and around it, but no scene has exercised it yet. The parts most likely to need a fix
on first contact are the surface-nets quad winding and whether anything in Bepu's sweep path loops
over `ChildCount`, which for the triangle forms is cells x 6 rather than a triangle count.
