# Stride.Voxels

Voxel cone tracing for global illumination, and voxel fields drawn straight from their samples.
Two halves that share one clipmap: the GI voxelizes the scene into rings of a 3D texture and
cone-traces them back as bounced light and reflections; the grid (`Voxels/Grid`) draws a density
field with ordinary Stride materials, casts its shadows, and writes itself into those same rings
so the GI lights it without rasterising a proxy.

Directory map, in the order data flows:

| Folder | Holds |
|--------|-------|
| `Voxelization/` | `VoxelVolumeComponent` and its processor; the storage (`VoxelStorage/`, clipmaps of rings and mips), the layouts (`Layout/`: isotropic, paired, anisotropic - how many directions a voxel keeps), the attributes (`Attributes/`: what is voxelized, radiance + opacity being the one GI needs), the rasterising methods (`VoxelizationMethod/`: dominant axis, single axis, tri-axis, each with its multisampled target), the modifiers (`Modifiers/`: opacify), and the fragment packers. |
| `Marching/` | The marchers: cones (`VoxelMarchCone`, per-mipmap, edit-mode), beams for debug views, and the sets that arrange several cones over a hemisphere (`MarchSets/`: 6, 12, random). |
| `Light/` | `LightVoxel`, the light type that turns a volume into an environment light, and `LightVoxelRenderer`, which composes marchers and samplers into the lighting shader and can trace the diffuse cones into a reduced buffer (`VoxelGI/`). |
| `GraphicsCompositor/` | `ForwardRendererVoxels`, the forward renderer that runs the voxelization passes, and `VoxelRenderer`, which owns each volume's device resources and the field injector. `DefaultGraphicsCompositorVoxels` is a ready compositor. |
| `Grid/` | The voxel field: component, sources, traversal, occupancy pyramid, resolve pass, injector, and the material feature that lets any material draw it. See [docs/grid.md](docs/grid.md). |
| `docs/` | `grid.md` for the field pipeline, `ROADMAP.md` for where this is going. |

## Using the GI

Three pieces, in a scene with a compositor that has the voxelization stages
(`DefaultGraphicsCompositorVoxels`, or the recipe in the asset store package's README):

1. A `VoxelVolumeComponent` on an entity at the centre of the area to light: `VoxelVolumeSize`,
   `AproximateVoxelSize` (which sets the ring count), a `Storage` (`VoxelStorageClipmaps`), a
   `VoxelizationMethod`, and one attribute in `Attributes` (`VoxelAttributeEmissionOpacity` with a
   layout).
2. A `LightComponent` whose type is `LightVoxel` pointing at that volume, with a
   `DiffuseMarcher` (a cone set) and optionally a `SpecularMarcher` (one cone). Its
   `BounceIntensityScale` defaults to zero: set it, or the second bounce is not re-injected.
3. Materials as usual. Emissive ones light the room through the voxels without being lights.

Costs, so they can be traded against each other: the atlas is `resolution³ × rings × directions
× bytes per voxel`, and it is the one thing a small GPU cannot afford - at 32³ isotropic in 10-bit
storage it is a few megabytes, at 256³ paired in half floats it is over a gigabyte. Voxelization is
a scene render per ring per frame, multiplied by the multisample count; one ring per frame is the
default. Cone cost is cones × steps per shaded pixel, and `LightVoxel.ScreenSpaceDivisor` traces
them at 1/N of the screen when the compositor has a depth-only stage for the resolver to read.

## Using a field

A `VoxelGridComponent` with a source (`VoxelGridSourceTexture3D` or a packed buffer), a traversal
(`VoxelGridTraversalDDA`, with an occupancy pyramid for the skips), and one material per id in
`Materials`. The field draws through the normal material path, casts shadows when `CastShadows`
is set, and is injected into any GI volume that covers it when `InjectIntoGI` is set - the whole
pipeline is in [docs/grid.md](docs/grid.md).

## Conventions in this module

- Shader classes sit in a `Shaders/` folder beside the C# that composes them, and keys generated
  from them in `XxxKeys.cs` next to the `.sdsl`.
- Device resources are owned by whoever creates them and disposed on the same path: volumes by
  `VoxelRenderer.ReleaseGoneVolumes`, the injector by the renderer, grids by their processor.
- Half precision is kept where the original code used it; the storage formats are the user's
  choice through the layout's `StorageFormat`.
- Anything that works around a Direct3D 11 compiler limitation says so in a comment, and is
  gated on `STRIDE_GRAPHICS_API_DIRECT3D11` when it can be.
