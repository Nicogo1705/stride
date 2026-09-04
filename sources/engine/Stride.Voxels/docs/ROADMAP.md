# Stride.Voxels — direction

Where this library is going, and why. Written after the profiling pass on the
StrideVoxelGI gallery (Sept 2026), which established the numbers this plan leans on.

## The premise

Stride.Voxels today does two separable jobs:

1. **Turn triangles into voxels** — rasterize the scene through a geometry shader into
   the clipmap, with MSAA, once per frame.
2. **Trace the voxels** — the anisotropic mip chain, the cone marchers, and the
   integration into the light group.

Job 1 is where essentially all the cost lives. On the gallery, voxelization was ~96% of
the voxel frame time, and the second bounce alone (12 cones x 12 steps per voxelization
fragment) was 62% of the whole frame. Halving the bounce cone count took the frame from
29.9 ms to ~20 ms with no visible difference — the win came entirely from doing job 1
less, not from doing job 2 better.

Job 2 is the valuable part, and it is independent of where the voxels came from.

## The direction

**Split the two, and let the voxel data come from anywhere.**

A game that is *already* voxel-based knows its occupancy. Making it rasterize its
geometry so the library can rediscover that occupancy is pure waste — it pays the single
most expensive pass in the system to compute something it already had. The library
should accept the grid directly.

This turns Stride.Voxels from "a GI effect for triangle scenes" into "a voxel
representation of a scene, plus everything you can trace against it", with triangle
rasterization becoming one of several ways to fill it.

## Planned work, in order

### 1. Decouple the storage from the voxelization

Give the clipmap a public fill path that does not go through the rasterizer: a compute
shader (or a direct upload) writing occupancy + radiance + material into the brick pool.
The existing rasterizing voxelizer becomes one producer among others.

The consumer side — mip chain, cone marchers, `LightVoxel` integration — is unchanged.
This is the enabling step for everything below and the only one that is strictly required.

**Done for fields.** `Voxels/Grid` holds the seam and its consumers: `IVoxelGridSource` (SDSL and
C#) describes a packing once, `VoxelGridSourceTexture3D` and `VoxelGridSourcePackedBuffer` are two
examples of one, `VoxelGridResolveRenderer` draws a grid straight from it, and `VoxelGridInjector`
is the fill path: a compute pass (`VoxelGridInjectShader`) that writes a field's surface voxels,
colour and emission into the clipmap's fragment buffer beside what the rasterizer wrote, so the
arrangement, the mips and the cones never know which producer a voxel came from. What remains is
the same path for data that is not a field - a direct radiance upload - which is a packer away.

### 2. DDA traversal alongside cone tracing

Add exact grid traversal (DDA, cell by cell) as a marcher, next to the existing cone
marchers. Where a cone gives a soft, cheap, approximate answer, a DDA ray gives an exact
one: sharp shadows, primary visibility, precise occlusion.

**Done for primary visibility and shadows.** `IVoxelGridTraversal` is the seam, `VoxelGridTraversalDDA`
walks the grid cell by cell over a min/max occupancy pyramid, `VoxelGridResolveShader` traces one
ray per pixel and writes depth, so rasterized content composites against voxels with an ordinary
depth test, and `Occlude` answers the shadow-map casters with a cheaper walk that stops at the
first solid cell. Cone tracing for GI is unchanged and untouched.

**This is deliberately not SDF.** Sphere tracing needs a distance that is correct
*everywhere*, not just near the surface; a marching-cubes density field only guarantees
the sign, and sphere tracing across it walks through walls. DDA needs nothing but
occupancy, and — by solving the intersection against the trilinear field inside each
straddling cell — reproduces exactly the same iso-surface a marching-cubes or surface-nets
mesh would have produced. No new data, no new format, no visual change.

Empty space is handled by a **coarse occupancy mip pyramid** ("this 8^3 brick is empty,
skip it"), which is most of what a real SDF buys, for a fraction of the memory and none of
the build cost — and it is the same pyramid already wanted for distant LOD and for GI.

A true distance field stays in reserve, for the day view distance is the wall and brick
skipping is not enough.

### 3. Consumers of the DDA path

Primary visibility for voxel content: a compute pass marches the grid and writes depth +
normal + material. Everything downstream of the G-buffer is untouched — triangle content
(props, characters, machines) rasterizes on top with an ordinary depth test. This is not
a renderer rewrite; it replaces the *source* of one category of geometry.

The consequences for a voxel game are the point of the exercise:

- No meshing. No marching cubes, no surface nets, and no chunk-seam artifacts, because
  there are no chunk meshes.
- No per-chunk vertex/index buffers — usually the largest memory line.
- **No re-meshing on edit.** Digging becomes a texel write. That is the real prize for a
  mining/building game: no regeneration, no queue, no latency.
- Distant LOD is a mip of the grid — the same structure that serves the GI.

### 4. Physics from the same data (done)

A Bepu collidable that reads the same voxel array and generates contacts on the fly,
replacing the mesh collider built from a GPU readback.

## Stride.RT

A separate package, later. Hardware ray tracing is D3D12/Vulkan only, so it can never
replace the raster path — it can only be a second one, selected at build time.

The traversal seam above is where it will plug in: a hardware traced implementation of
`IVoxelGridTraversal` leaves every consumer of it unchanged, which is why the traversal is an
interface rather than a function.

When it exists, Stride.Voxels should be able to *use* it where it helps, and fall back to
its own tracing when it is off or unavailable. The two are complementary, not competing:

- The clipmap is a fine fallback GI, and stays the D3D11 answer.
- RT's real win is scaling in shadow-casting light count — one TLAS amortizes the
  geometry cost across every light, where shadow maps re-submit the scene per light —
  plus the deletion of whole subsystems (shadow atlas, cascades, bias, SSR, probe bakes).
  It is not a general speedup; for a single directional light a cascade beats it.

**Do not route voxel traversal through DXR.** Procedural primitives (AABB + intersection
shader) are the slow path of hardware RT, and a DDA over a regular grid beats them on this
exact case — while also running on D3D11. Hardware RT is for the triangle content.

## Non-goals

- Replacing the rasterizing voxelizer. It stays, for scenes that are genuinely triangles.
- A true SDF as a foundation. It is an optimization to reach for once measured, not a
  starting point.
