# SDSL: the concepts

SDSL is HLSL with classes. A shader is a class; a program is several classes mixed into one; a
program's variations are produced by an *effect* that decides, from parameters, which classes go
in. This document explains those three things and the machinery under them, with the engine's own
shaders as examples. Read it before the tutorials; refer back to the last section when something
compiles and does nothing.

Every SDSL file has one `shader` (or `effect`) of the same name as the file, in a namespace that
is not enforced yet: shader names are global, so pick them as if they were.

## 1. A shader is a class

```hlsl
shader ComputeColorVoxelDiffuse : ComputeColor, MaterialPixelStream
{
    override float4 Compute()
    {
        return streams.matDiffuse;
    }
};
```

`ComputeColor` declares `float4 Compute()`; this class inherits it and overrides it. A class can
declare methods, `override` methods of its bases, and mark methods `abstract` to force a derived
class to implement them (see the pitfalls before using `abstract`). It can hold variables of three
kinds, described below: constants, resources and streams.

Inheritance is *flattening*, not virtual dispatch. When several classes are mixed into a program,
the compiler lays them out in order and the last definition of a method wins, whichever class it
came from. That is what `override` means here: "mine replaces what came before". There is no
`base.` chain across arbitrary classes, only `base.Method()` for the definition that preceded yours
in the flattened order.

### Constants: `cbuffer`

```hlsl
cbuffer PerMaterial
{
    stage float VoxelGridCellSize;
    stage int VoxelGridMaxSteps;
}
```

A `cbuffer` block groups constants the CPU sets. Its name is a *frequency*: `PerFrame`,
`PerView`, `PerDraw`, `PerMaterial`, `PerLighting`. Stride fills each group at the moment it
changes: `PerView` once per camera, `PerDraw` once per object, `PerMaterial` once per material.
Put a value in the group that matches how often it changes and the engine uploads it that often.

Every constant gets a C# key. The compiler generates a `XxxKeys` class per shader: for
`VoxelGridResolveShader.VoxelGridMaxDistance` you get
`VoxelGridResolveShaderKeys.VoxelGridMaxDistance`, a `ValueParameterKey<float>`. Set it on a
`ParameterCollection` and it lands in the constant buffer.

### Resources: `rgroup`

```hlsl
rgroup PerMaterial
{
    stage Texture3D<float2> VoxelGridTexture;
    stage StructuredBuffer<uint> VoxelGridData;
}
```

Textures, buffers and samplers go in an `rgroup` of the same frequencies. Their keys are
`ObjectParameterKey<Texture>` and `ObjectParameterKey<Buffer>`.

### Streams: what crosses the stages

```hlsl
stage stream float4 PositionWS;
stage stream float3 normalWS;
```

A `stream` is a variable that crosses shader stages: written in the vertex shader, interpolated,
read in the pixel shader. Accessed as `streams.PositionWS`. The compiler works out which streams
each stage needs and builds the vertex output and pixel input structures for you - that is the
whole reason for the keyword. Rules that follow from it:

- A stream read in a stage must have been written somewhere before that stage (in the same stage
  earlier, or in an earlier stage). One read in the pixel shader and written nowhere becomes a
  vertex input the mesh does not have, and the effect fails to build.
- A `bool` cannot cross from vertex to pixel shader; use a `float` and compare.
- `stage` on a stream means one instance shared by the whole program, whatever class declared it
  and however deep. Nearly every stream you write should be `stage stream`.

### `stage` on everything else

`stage` on a constant, a resource or a method means the same thing: hoist it to the root of the
program so that every class, including ones composed in a scope of their own (next section), sees
the one and only instance. A `stage` constant declared twice, by two classes, is one constant.

## 2. Mixins: several classes, one program

A program is a `ShaderMixinSource`: a list of classes and, for each composition slot, another
`ShaderMixinSource`. The engine builds them in C#:

```csharp
var mixin = new ShaderMixinSource();
mixin.Mixins.Add(new ShaderClassSource("MaterialSurfaceVoxelGrid"));
mixin.Mixins.Add(new ShaderClassSource("VoxelGridTraversalDDA", 1)); // a generic argument
mixin.Mixins.Add(new ShaderClassSource("VoxelGridSourceTexture3D"));
```

Order matters twice. Methods: the last definition wins. Bases: a class that inherits another
brings it in before itself, so an `override` in a derived class beats the base even if the base is
listed later. The flattened program has one copy of each class, wherever it was reached from.

A class can also be **generic**: `shader VoxelGridTraversalDDA<int TSurface>` is instantiated
with `new ShaderClassSource("VoxelGridTraversalDDA", 2)`. Inside, `TSurface` is a compile-time
constant: `bool cubes = TSurface == 0;` folds, and the branches of the other forms are gone from the
bytecode. Each argument value is a different shader and a different cache entry. Generic
parameters can be `int`, `float`, `bool`, a `MemberName` (the name of a stream or member, used to
write a class that sets "the stream you name"), a `LinkType` or a `Semantic`.

## 3. Compositions: a slot filled from C#

```hlsl
shader VoxelGridResolveShader : ImageEffectShader
{
    compose IVoxelGridTraversal Traversal;
    ...
    if (!Traversal.Trace(origin, direction, reach, hitDistance, normal)) discard;
}
```

`compose` declares a slot of an interface type; C# fills it with a mixin implementing that
interface:

```csharp
shader.Parameters.Set(VoxelGridResolveShaderKeys.Traversal, traversal.GetShaderSource());
```

A composition is a **scope of its own**. Everything the composed mixin declares - its constants,
its resources, its streams - gets a copy under the slot's name, and its parameter keys get the
slot's path prefixed: a `cbuffer` member `VoxelGridCellSize` inside the `Traversal` slot is set with
`VoxelGridTraversalDDAKeys.VoxelGridCellSize.ComposeWith("Traversal")`. Two slots filled with the
same class have two copies of everything. This is what lets a light shader run twelve lights of
three kinds through one `foreach`:

```hlsl
compose DirectLightGroup directLightGroups[];
...
foreach (var group in directLightGroups)
    group.PrepareDirectLights();
```

An array slot is filled with `mixin.AddCompositionToArray("directLightGroups", source)`.

When you do *not* want a copy - one texture shared by the shader and a helper class - inherit or
mix the helper in beside the shader rather than composing it. The voxel grid's traversal is mixed
into the material's surface shader for exactly that reason: mixed, the field's texture is declared
once and bound like any material texture; composed, it would be a second declaration under a
second name that nothing binds.

### `[Link]`: a fixed name through any depth

```hlsl
rgroup PerMaterial
{
    [Link("VoxelGridField.Texture")]
    stage Texture3D<float2> VoxelGridTexture;
}
```

Whatever path a class was reached by, `[Link]` binds the member to a key of that exact name. Declare
the key in C# in a class whose name ends in `Keys`, and drop the suffix in the link: the class
`VoxelGridFieldKeys` names its keys `VoxelGridField.X`, the way `MaterialKeys.DiffuseMap` is
`Material.DiffuseMap`. This is how a material feature reaches a texture from inside
`layers[3].materialPixelStage`, a path nobody outside the material generator can predict.

## 4. Effects: from parameters to a program

An `.sdfx` file describes a *family* of programs and picks one from parameters:

```hlsl
effect VoxelGridResolveEffect
{
    using params VoxelGridResolveShaderKeys;

    mixin VoxelGridResolveShader;
    if (VoxelGridResolveShaderKeys.Traversal != null)
    {
        mixin compose Traversal = VoxelGridResolveShaderKeys.Traversal;
    }
}
```

Statements:

- `mixin X;` adds a class. `mixin X<3, true>;` adds a generic instantiation.
- `mixin compose slot = Keys.Something;` fills a slot from a `PermutationParameterKey<ShaderSource>`.
  `mixin compose slots += (source);` appends to an array slot.
- `mixin child Name;` runs a child effect (`partial effect Name`) that adds to the same program:
  the forward renderer's `ShadowMapCaster`, `ZPrepass`, `GBuffer` are children of
  `StrideForwardShadingEffect`.
- `mixin macro Keys.X;` defines a preprocessor macro for the whole compile.
- `using params Keys;` makes a key class's permutation keys readable as variables, and `if` on
  them selects. A `PermutationParameterKey<T>` is one whose value changes the *program*, not just
  a constant: `MaterialKeys.PixelStageSurfaceShaders` (a shader source), `LightingKeys.DirectLightGroups`
  (a collection of them), `MaterialKeys.UsePixelShaderWithDepthPass` (a bool).

Every distinct set of permutation values is one **permutation**: one program, compiled once and
cached on disk under the effect's name and a hash of its inputs. Ask for a value no permutation
has been compiled with and the frame that asks pays a compile - which is the stall you feel when
a scene shows something new. Shaders compile in the background; the object waits, undrawn, until
its program exists.

Where effects are named: a `ForwardRenderer` draws each render stage with an effect name
(`StrideForwardShadingEffect`) and the mesh render feature asks the effect system for the
permutation matching the material's and mesh's parameters. An `ImageEffectShader("MyEffect")`
draws a full-screen quad with an effect. A `ComputeEffectShader { ShaderSourceName = "MyEffect" }`
dispatches a compute effect. Materials never name an effect: the material generator produces the
mixin the forward effect composes as `materialPixelStage`.

## 5. Parameters: how a value reaches a shader

A `ParameterCollection` holds values under keys. The engine gathers several for one draw: the
material pass's, the mesh's, the render view's, the frame's. A value in the material pass's
collection lands in `PerMaterial`; the render feature copies whatever the effect's constant
buffers declare from whichever collection holds the key.

Two consequences people meet the hard way:

- A key not set anywhere is not zero; it is whatever the constant buffer held. Clamp loop counts
  taken from parameters, or an unbound `MaxSteps` walks a loop until the graphics device is
  removed.
- A key set under the wrong name binds nothing and says nothing. When something reads as zero,
  open the effect's cache folder (below) and read `_meta.txt`: it lists the parameter names the
  compiled effect actually expects.

## 6. Materials: features and layers

A `Material` is not a shader; it is a `MaterialDescriptor` of *features* - diffuse, normal map,
glossiness, emissive, tessellation - which the `MaterialGenerator` turns into shader sources under
`MaterialKeys.PixelStageSurfaceShaders`, `VertexStageSurfaceShaders` and so on. Each feature adds a
class implementing `IMaterialSurfacePixel` (`override void Compute()`) to an array composition
called `layers` inside a `MaterialSurfaceArray`. At the end of the pixel stage the lighting shader
reads the streams the layers left: `matDiffuse`, `matGlossiness`, `matSpecular`, `matEmissive`,
`normalWS`, and shades.

Some rules of that pipeline that matter when you write a feature:

- The generator adds lighting only when the material has a diffuse feature, and emission only
  when it has an emissive one. A material with nothing but a custom surface feature shades black.
- A `ComputeColor` node (a texture, a constant, a shader class) is evaluated in the vertex stage
  as well as the pixel stage. A node that reads a stream must read one the material resets in both
  stages: `matColorBase`, `matDiffuse`, the `mat*` family. A stream of your own, written in the pixel
  stage only, is unwritten in the vertex stage and turns into a vertex input the mesh lacks.
- A compiled `Material` keeps no descriptor, but its pass parameters hold the generated sources
  under the permutation keys. A layer can be put in front of a compiled material's own by copying its
  pass parameters and rebuilding the `layers` array - see the last tutorial.

## 7. What the compiler does to your code

SDSL is parsed to SPIR-V (a Stride dialect; it is cross-compiled to every backend, Direct3D
included, so nothing about it implies Vulkan), the classes are mixed at the SPIR-V level, the
result is optimised and legalised with spirv-opt, translated to HLSL by SPIRV-Cross and compiled by
the platform's compiler - FXC for Direct3D 11. Each shader class is compiled once and cached as
SPIR-V under `cache/shaders/`; each effect permutation is cached under `cache/effects/<Effect>/`
with, in a debug build, the HLSL it produced (`*_ps.hlsl`, `*_vs.hlsl`) and the parameters it
expects (`*_meta.txt`). Those dumps are the ground truth when a shader does not do what it says.

## 8. Pitfalls, in the order they cost time

- **Early `return` inside a function that gets inlined into a loop, or inside a material's
  `Compute`.** It reaches FXC as a one-iteration loop to unroll and FXC fails with
  "internal error: argument pulled into unrelated predicate". One exit per function; `? :` instead
  of `if (...) return`.
- **`break` or `return` inside a walk loop** miscompiles on some targets. End loops by clearing
  their condition.
- **A stream read in the pixel stage that no stage wrote**: a vertex input the mesh has not got.
  See sections 1 and 6.
- **A `stage` method declared inside a composition and overridden from the root** is not
  resolved; the permutation never compiles and nothing says so. Pass the fact in a `stage stream`
  instead (the shadow caster marker does this).
- **`abstract` on a method of an interface that a composed class inherits** ends in a
  `KeyNotFoundException` in the mixer. Give the method a default body.
- **A `stage` variable that is neither stream nor cbuffer is a constant**: writing to it fails
  SPIR-V validation ("cannot store to Uniform Blocks").
- **A key declared with the `Keys` suffix in `[Link]`** binds nothing: `[Link("VoxelGridField.X")]`,
  not `[Link("VoxelGridFieldKeys.X")]`.
- **`half` is a type.** It cannot name a parameter.
- **`discard` does not stop unordered access writes** that follow it in the same pixel shader; a
  material that discards inside a pass that stores fragments has to tell the pass, not just the
  rasteriser.
- **A generic or MemberName instantiation edited on disk** used to be served stale from
  `cache/shaders` for as long as the cache lived; fixed, but when a shader change seems ignored,
  deleting `cache/shaders` is still the first thing to try.
- **`int3` arithmetic on the result of an inherited method** inside a material shader is refused
  ("Unsupported type for GetElementType"); pass such values as parameters from the CPU.
