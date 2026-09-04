# The voxel grid: a field drawn with materials

`Voxels/Grid` draws a density field - a 3D texture or a packed buffer of samples - as a surface,
with ordinary Stride materials, shadows, and a place in the GI, without ever building a mesh. This
document is the pipeline end to end, in the order a frame runs it.

## The pieces

| Type | Role |
|------|------|
| `VoxelGridComponent` | The field on an entity: `Source`, `Traversal`, `Extent`, `CellSize`, `IsoLevel`, `Materials` (one per id, up to 256), `Dither`, `CastShadows`, `InjectIntoGI`, `DebugView`. |
| `IVoxelGridSource` (`VoxelGridSourceTexture3D`, `VoxelGridSourcePackedBuffer`) | How samples are read: a C# side that binds the resource, an SDSL side that answers `Density(cell)` and `MaterialId(cell)`. |
| `IVoxelGridTraversal` / `VoxelGridTraversalDDA` | How the surface is found: `Trace` for a hit with a normal, `Occlude` for a yes/no, `SurfaceMaterialId` for which material is at a point, `SurfaceProbe` and `BrickState` for whoever samples the field on its own grid. |
| `VoxelGridOccupancy` | The min/max pyramid the walk skips with: an RG8 3D texture with mips, each level a brick of 2^L cells holding the least and greatest density inside. Updated by region when the field is edited. |
| `VoxelGridProcessor` | Runs it all: keeps the per-grid state, wraps the materials, inserts the resolve pass into the compositor, lists the fields for the injector. |
| `VoxelGridResolvePass` / `VoxelGridResolveRenderer` / `VoxelGridResolveShader` | One ray per pixel, once per frame, for every grid: writes normal, material id + grid index, world position + depth, and a depth buffer. |
| `MaterialVoxelSurfaceFeature`, `MaterialSurfaceVoxelGrid*.sdsl` | The material side: a layer put in front of a compiled material's own that reads the resolved targets, and a shadow material that occludes. |
| `VoxelGridInjector` / `VoxelGridInjectShader` | The GI side: a compute pass writing the field's surface voxels into the GI's fragment buffer, colour and emission from the materials. |

## A frame

1. **The processor** (`VoxelGridProcessor.Draw`) makes sure the resolve pass is first in the scene
   camera renderer, the proxy box and the wrapped materials exist, the occupancy pyramid is up to
   date, and the material table for the injector is uploaded. It then registers the grid with the
   resolve renderer and lists it on the visibility group for the injector.

2. **The resolve pass** runs before anything else draws. For each grid, a full-screen pass builds
   the ray through the pixel from the inverse view-projection (so it is right under any
   projection), transforms it into the grid's space, and calls `Trace`. A hit writes three
   targets and depth: the normal with a 1 in w that says "resolved"; the material id and grid
   index, a byte each; the world position with the depth in w. Several grids resolve against one
   another through the depth buffer.

3. **The materials** draw. Each id of a grid is one draw of the grid's proxy box with the compiled
   material *wrapped*: a copy of its pass parameters with `MaterialSurfaceVoxelGridResolve` put in
   front of its own surface layers. That layer reads the targets at the pixel, discards unless the
   resolved id and grid index are its own, and replaces the mesh's normal, position and depth with
   the resolved ones. Everything after it - the material's diffuse, normal map, lighting - runs as
   for a mesh. The box is drawn with front-face culling so the camera can stand inside it.

   Where two ids meet, `SurfaceMaterialId` picks one per pixel: the eight ids round the point are
   weighted, and a threshold (sharp, Bayer 4×4, Bayer 8×8, or interleaved gradient noise, from
   `Dither`) decides, so a boundary reads as a dithered blend rather than a hard edge.

4. **Shadows.** With `CastShadows`, a shadow material is drawn in the shadow-map caster passes: it
   knows it is in one through the `IsShadowMapCasterPass` stream (see below), traces with
   `Occlude`, and writes the hit's depth. In any other pass it discards.

5. **The GI.** After each voxelization pass has rasterised the scene into a ring, the injector
   runs one thread per GI voxel over the field: `BrickState` skips bricks that are all air or all
   solid, `SurfaceProbe` says whether the surface passes through the voxel and gives the normal,
   and the voxel's colour and emission (from the material table) are packed the way the default
   packer packs and written with the same atomics the rasterizer uses. The arrangement, the mips
   and the cones never know which producer a voxel came from.

   The material's own draw, meanwhile, must not also voxelize: in a voxelization pass the layer
   sets the `VoxelizationSkip` stream so the pass stores nothing for it, because `discard` stops
   the raster output but not the unordered-access writes that follow it.

## The walk

`VoxelGridTraversalDDA` is a level-descending DDA over the occupancy pyramid: at each step it
reads the coarsest brick that contains the point, skips the whole brick when its max is below the
iso level (all air) or, for `Occlude`, stops when its min is above it (all solid), and descends a
level otherwise, down to single cells.

In a cell the surface can pass through, the eight corners are loaded once (`CornersNear`,
`CornersFar`) and everything else is computed from them: the straddle test, four probes along the
segment the ray gives the cell (one at the exit misses a surface that enters and leaves within
the cell - a shallow silhouette does), a bisection between the last point outside and the first
inside, and the normal as the exact gradient of the trilinear field at the hit. That is the same
iso-surface a marching-cubes mesh of the field would have, with no mesh.

`Occlude` skips the bisection: the first probe inside is the hit, at most a quarter of a cell
past the surface, never short of it - what a shadow needs.

Two surface modes are compile-time (`TSurface`): 0 draws cells as cubes, 1 the smooth trilinear
surface, 2 surface nets (the facet's own orientation is kept for the normal). `SealBorder` closes
the field at its edges so a ray never sees the inside of a box.

## The two pass streams

Two questions a material layer has to answer are "which pass am I in": the shadow-map caster
passes and the voxelization pass. Neither can be answered with a `stage` method overridden from
the pass's shader - that override is never resolved across a composition, and a `stage bool`
member is a uniform - so both are `stage stream float`s written in the vertex layer to zero, and
set to one by the pass:

- `ShadowMapCasterPassInfo.IsShadowMapCasterPass`, set by `ShadowMapCasterPassMarker`, mixed into
  the three `ShadowMapCaster*.sdfx` effects (in their dithered and discard branches, the only ones
  that reach a material's pixel stage).
- `VoxelizationPassInfo.IsVoxelizationPass` and `VoxelizationSkip`, set in
  `VoxelizeToFragments.PSMain`; the pass stores a fragment only when the skip is below one half.

## Editing at runtime

The field is edited by writing to its texture (or buffer) and calling
`VoxelGridOccupancy.Invalidate(region)`: the pyramid is rebuilt for the bricks that region touches,
level by level, not for the whole field. The GI picks the change up on the ring's next injection.
A material edited at runtime - a colour, an emissive intensity - reaches the injector's table on
the next frame; it compares values and uploads only on change.

## What it costs

On an RTX 4090 at 1080p, the demo's 128³ field: the resolve pass is around half a millisecond, a
material draw a tenth, the shadow casters under a millisecond with `Occlude`, the injection a few
hundredths - against about twenty milliseconds for the first version that walked every cell and
rasterised itself into the GI. On a small GPU the same ratios hold; the resolve pass scales with
pixels, the shadows with shadow-map texels, and both with how many bricks a ray crosses, which is
what the pyramid keeps small.
