# VoxelCollider — colliding against a voxel field without meshing it

## What it replaces

A game whose world is a voxel density field — anything meshed with marching cubes, surface nets or
dual contouring — currently has only one way to give that world physics on `Stride.BepuPhysics`:
mesh the chunk, keep the vertices and indices on the CPU so a `MeshCollider` can exist, and let Bepu
build a bounding volume tree over the triangles. Every terrain edit re-meshes, re-uploads and
rebuilds that tree. When the mesh is generated on the GPU, it also has to be read back.

All of that exists only because Bepu needs triangles.

`VoxelCollider` holds the field instead. There is no mesh, no readback, no per-chunk tree and no
rebuild: editing terrain is `SetVoxel`, one store, and the collidable's bounds do not change.

## Data

How samples are packed is a type parameter, not a convention to convert to. Collision asks the field
one question — how solid is this sample — so `IVoxelDensitySource` is that one method plus the grid
dimensions, and the packing stays the game's own.

Three sources ship: `PackedVoxelSource` (density and material in one `ushort`, the layout a voxel
game commonly uploads to the GPU), `ByteVoxelSource` (one byte of density — also the answer for a
game keeping density and material in two parallel arrays: hand over the density one), and
`FloatVoxelSource` (used as is, the natural fit for a signed distance field, with `IsoLevel` set to
zero). A game with a different packing writes its own in a dozen lines.

A grid of n cells per axis takes n+1 samples per axis, because a cell reads the eight samples at its
corners.

`VoxelCollider` is the ready-made collider over `PackedVoxelSource`, owning its samples in native
memory:

```csharp
var collider = new VoxelCollider { CellSize = 0.5f, IsoLevel = 0.5f, Form = VoxelChildForm.TriangleSurfaceNets };
collider.SetData(33, 33, 33, chunkSamples);
Entity.Add(new StaticComponent { Collider = collider });

// Later, digging:
collider.SetVoxel(x, y, z, 0);
```

`SetData` copies into memory the collider owns, so it survives attach and detach cycles and is freed
on `Dispose` or finalization. Writes through `SetVoxel` are visible to the simulation immediately —
which is the point — so make them between steps, not during one.

For any other packing, or to avoid the copy entirely when the field already lives in unmanaged
memory, derive from `VoxelColliderBase<TSource>` and implement `TryGetSource`. The base class
implements `ICollider` — whose members are internal — so a game-side collider inherits them rather
than having to declare them. Register the source's collision tasks once per simulation with
`VoxelCollisionTasks.Register<TSource>(simulation)`, giving it a `ShapeTypeIdBase` free in that
simulation; the built-in sources take 12 through 20.

## Forms

`Form` picks what an occupied cell presents to the narrow phase. Nothing is stored per child in any
of them; the child is computed from the field each time the narrow phase asks.

| Form | Surface | Notes |
| --- | --- | --- |
| `Box` | The cell grid | Cheapest child test. Disagrees with a smooth rendered surface by up to half a cell. |
| `Sphere` | Inscribed spheres | Rounds off the lattice corners a character catches on, at the cost of gaps along cell diagonals. |
| `TriangleMarchingCubes` | The marching-cubes iso-surface | Up to five triangles per cell. |
| `TriangleSurfaceNets` | The surface-nets iso-surface | Far fewer, larger triangles for the same field. The recommended triangle form. |

A cell counts as solid for the box and sphere forms when the mean of its eight corners reaches the
iso level, which keeps the surface within half a cell of the rendered one either way rather than
eroding it (characters fall through) or inflating it (characters float).

The triangle forms use the standard marching-cubes case table with the air-is-set corner convention
and linear edge interpolation. Set `IsoLevel` to the value the renderer meshes with, or the
collision surface will sit beside the visible one. Bepu triangles collide on one side only, so if
the surface pushes bodies into the ground rather than out of it, `InvertWinding` corrects it.

## How it works

The shapes implement `IHomogeneousCompoundShape<TChild, TChildWide>` and
`IBoundsQueryableCompound`, the same pair `Mesh` implements, with one difference that is the whole
point: **no acceleration structure**. `Mesh` traverses a tree because a triangle soup has no
structure to exploit; a regular grid already is the structure. A bounding box query divides by the
cell size and walks a range of indices, and a ray walks the cells it crosses with a DDA, so a short
probe touches a handful of cells. Nothing has to be built when the collidable is created, and
nothing has to be refit when a voxel changes.

Children are materialized into the collision batcher's shape cache as they are requested and
discarded when the batch flushes.

Collision and sweep tasks are registered from `BepuSimulation` after `Simulation.Create`, three
shape type ids per density source — Bepu's built-ins end at `Mesh` = 8. Voxel-versus-voxel is
deliberately not registered: a voxel collidable is terrain.

## Cost

A query is overwhelmingly cells that contribute nothing, so deciding *whether* a cell contributes is
kept apart from building what it contributes. Asking costs eight sample reads for the box, sphere
and marching-cubes forms — one classification — and six for surface nets, which only tests three
edges for a sign change. Vertices, interpolation and triangles are built later, in `GetLocalChild`
and the ray test, and only for the children the narrow phase goes on to test.

A cell's eight corners are inside the sample grid by construction, so the hot path does no bounds
clamping at all; the checked accessor exists only for callers that may be outside. Bounds are
analytic. Overlap walks run with z varying fastest, matching the sample layout, so they follow cache
lines.

## Room left for other consumers

`IVoxelDensitySource` names nothing from physics, and nothing from Bepu: it is three dimensions and
a density lookup. That is deliberate. The same field a game hands to collision is the field a voxel
renderer meshes, a global illumination clipmap is filled from, and a distance-field or DDA tracer
walks - and a game should describe its packing once, not once per consumer. Should such a consumer
arrive, the interface can move to an assembly both sides reference without touching a call site, and
`VoxelGridData<TSource>` already carries the shared iso-surface constructions rather than hiding them
inside the shapes.

## Known limitations

**No boundary smoothing on the triangle forms.** Bepu removes the bumps a character feels crossing
internal edges with a `MeshReduction`, and in BepuPhysics 2.5.0-beta.28 that type is bound to the
concrete `Mesh`: it keeps a raw pointer to the shape and casts it back to `Mesh*` at flush time, so
aiming it at another homogeneous compound would read the wrong fields. Bepu's own voxel sample makes
the same trade, and its author calls boundary smoothing for voxels "a not-easy exercise". Upstream
Bepu has since generalized `MeshReduction` over any homogeneous compound with triangle children via
thunks; on a Bepu that has them, the triangle forms can switch to `ConvexMeshContinuations` and this
limitation disappears. Until then `TriangleSurfaceNets`, having far fewer edges, suffers least.

**Entity scale is ignored.** The grid's only scale is `CellSize`.

**The physics debug view builds a mesh.** It is the one place that walks the whole field, since
drawing needs geometry that collision never materializes. Boxes get a box per solid cell, spheres an
inscribed octahedron, and the triangle forms the actual iso-surface. It runs only when the debug
view is on.

**Not yet exercised at runtime.** This compiles clean and follows the shape and continuation
contracts as Bepu's own voxel sample uses them, but no scene has run it. The parts most likely to
need a fix on first contact are the surface-nets quad winding and whether anything in the sweep path
loops over `ChildCount`, which for the triangle forms is cells x 6 rather than a triangle count.
