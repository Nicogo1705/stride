# SDSL: eight tutorials

Each one builds on the last and is a working pair of SDSL and C#. The concepts they use are in
[sdsl-guide.md](sdsl-guide.md); the section numbers in brackets point there. Put `.sdsl` and
`.sdfx` files in a project that references the Stride SDK; the build generates a `XxxKeys` class
for each, and the shader itself is compiled the first time something asks for it.

1. Tint the screen - an image effect
2. Tint from C# - a constant and its key
3. A ComputeColor node - a class in a material's graph
4. Pick the tint at compile time - generics
5. Choose the node from C# - compositions and an effect
6. A stream from vertex to pixel - `stage stream`
7. A material feature - a layer in the surface
8. Wrap a compiled material - a layer in front of another material's

## 1. Tint the screen

An `ImageEffectShader` draws a full-screen quad and calls `Shading()` per pixel. `Texture0` is the
input the effect was given.

```hlsl
shader TintShader : ImageEffectShader
{
    stage override float4 Shading()
    {
        float4 color = Texture0.Sample(LinearSampler, streams.TexCoord);
        return float4(color.rgb * float3(1.0, 0.8, 0.6), color.a);
    }
};
```

```csharp
var tint = new ImageEffectShader("TintShader");
// In a scene renderer, or a post effect stage, once per frame:
tint.SetInput(0, sourceTexture);
tint.SetOutput(targetTexture);
tint.Draw(drawContext);
```

Nothing more: the effect system wraps the class in a permutation of its own name. [4]

## 2. Tint from C#

Add a constant. Its frequency is `PerMaterial` - "set when the effect's own parameters are set".

```hlsl
shader TintShader : ImageEffectShader
{
    cbuffer PerMaterial
    {
        stage float3 TintColor;
    }

    stage override float4 Shading()
    {
        float4 color = Texture0.Sample(LinearSampler, streams.TexCoord);
        return float4(color.rgb * TintColor, color.a);
    }
};
```

```csharp
tint.Parameters.Set(TintShaderKeys.TintColor, new Vector3(1.0f, 0.8f, 0.6f));
```

`TintShaderKeys` was generated from the shader. Set the value before `Draw`, as often as it
changes. Give every constant a default with `= value` in the shader when one makes sense, and keep
in mind that a constant nothing sets is not zero. [1, 5]

## 3. A ComputeColor node

A material's colour inputs (diffuse, emissive, glossiness...) are graphs of `ComputeColor` nodes.
A class that overrides `Compute()` is a node you can plug in anywhere a texture could go.

```hlsl
shader ComputeColorStripes : ComputeColor, PositionStream4
{
    override float4 Compute()
    {
        float band = frac(streams.Position.y * 4.0);
        return band < 0.5 ? float4(1, 1, 1, 1) : float4(0.2, 0.2, 0.2, 1);
    }
};
```

```csharp
var material = Material.New(GraphicsDevice, new MaterialDescriptor
{
    Attributes =
    {
        Diffuse = new MaterialDiffuseMapFeature(new ComputeShaderClassColor { MixinReference = "ComputeColorStripes" }),
        DiffuseModel = new MaterialDiffuseLambertModelFeature(),
    },
});
```

`PositionStream4` is where `streams.Position` is declared; inheriting it says "I read this
stream". The node runs in the vertex stage too, so read only streams that exist there - a mesh
position does, your own pixel-only stream does not. [6]

## 4. Pick the tint at compile time

A generic parameter is a constant the compiler folds. Each argument is a distinct shader.

```hlsl
shader ComputeColorStripes<float Bands, bool Vertical> : ComputeColor, PositionStream4
{
    override float4 Compute()
    {
        float along = Vertical ? streams.Position.x : streams.Position.y;
        float band = frac(along * Bands);
        return band < 0.5 ? float4(1, 1, 1, 1) : float4(0.2, 0.2, 0.2, 1);
    }
};
```

```csharp
new ComputeShaderClassColor { MixinReference = "ComputeColorStripes", /* generics: */ }
// or, when building a mixin by hand:
mixin.Mixins.Add(new ShaderClassSource("ComputeColorStripes", 8.0f, true));
```

`ComputeShaderClassColor` takes its generic arguments from the `Generics` collection in the
editor; from code, the `ShaderClassSource` form is the direct one. Use a generic when the value
picks a *shape* of code (a loop count, which branch exists); use a constant when the value merely
changes. Each generic value is another permutation to compile. [2]

## 5. Choose the node from C#

An effect with a composition slot picks the class from a permutation key.

```hlsl
shader TintShader : ImageEffectShader
{
    compose ComputeColor Source;

    stage override float4 Shading()
    {
        float4 color = Texture0.Sample(LinearSampler, streams.TexCoord);
        return float4(color.rgb * Source.Compute().rgb, color.a);
    }
};
```

```hlsl
effect TintEffect
{
    using params TintShaderKeys;

    mixin TintShader;
    if (TintShaderKeys.Source != null)
        mixin compose Source = TintShaderKeys.Source;
}
```

`TintShaderKeys.Source` is a `PermutationParameterKey<ShaderSource>` because the slot's type is a
shader. Fill it:

```csharp
var tint = new ImageEffectShader("TintEffect");
tint.Parameters.Set(TintShaderKeys.Source, new ShaderClassSource("ComputeColorStripes", 8.0f, true));
```

Any `ComputeColor` fits: a `ShaderClassSource("ComputeColorConstantColorLink", "MyKeys.Color")`
reads a colour from a key, a `ComputeColorTexture` reads a texture. Constants the composed class
declares get their keys prefixed by the slot: `SomeKeys.X.ComposeWith("Source")`. Change the
source and you have changed the permutation - a compile the first time, a cache hit after. [3, 4]

## 6. A stream from vertex to pixel

A `stage stream` is written in one stage and read in a later one; the compiler builds the
interpolator.

```hlsl
shader HeightFade : ShaderBase, PositionStream4, Transformation
{
    stage stream float WorldHeight;

    stage override void VSMain()
    {
        base.VSMain();
        streams.WorldHeight = mul(streams.Position, World).y;
    }

    stage override void PSMain()
    {
        base.PSMain();
        float fade = saturate(streams.WorldHeight / 10.0);
        streams.ColorTarget = float4(streams.ColorTarget.rgb * fade, streams.ColorTarget.a);
    }
};
```

Mixed into a material's effect (the next tutorial shows where), `WorldHeight` becomes a vertex
output and a pixel input with no declaration on your part. Two rules: write it in the vertex stage
before anything reads it in the pixel stage, and make it a `float`, not a `bool`. `base.VSMain()`
calls whatever `VSMain` came before this class in the mixed program. [1]

## 7. A material feature

A feature is a C# class that adds shader classes to the material's surface layers. The pixel side
is a `IMaterialSurfacePixel` with `Compute()`, and it reads and writes the `mat*` streams the
lighting reads afterwards.

```hlsl
shader MaterialSurfaceHeightTint : IMaterialSurfacePixel, MaterialPixelStream, PositionStream4, Transformation
{
    cbuffer PerMaterial
    {
        [Link("HeightTint.Top")]
        stage float3 TopColor;
    }

    override void Compute()
    {
        float height = saturate(streams.PositionWS.y / 10.0);
        streams.matDiffuse.rgb = lerp(streams.matDiffuse.rgb, TopColor, height);
    }
};
```

```csharp
public static class HeightTintKeys
{
    public static readonly ValueParameterKey<Vector3> Top = ParameterKeys.NewValue(new Vector3(1, 1, 1));
}

[DataContract("MaterialHeightTintFeature")]
[Display("Height tint")]
public class MaterialHeightTintFeature : MaterialFeature, IMaterialDiffuseFeature
{
    public Vector3 Top { get; set; } = new Vector3(1, 1, 1);

    public override void GenerateShader(MaterialGeneratorContext context)
    {
        context.Parameters.Set(HeightTintKeys.Top, Top);
        context.AddShaderSource(MaterialShaderStage.Pixel, new ShaderClassSource("MaterialSurfaceHeightTint"));
    }
}
```

Two things to notice. `[Link("HeightTint.Top")]` binds the constant to `HeightTintKeys.Top`
whatever depth the layer ends up at, and `HeightTint` is the key class's name minus `Keys`.
`IMaterialDiffuseFeature` puts the feature in the diffuse slot, so it runs after the diffuse map
has written `matDiffuse` and the generator adds lighting; a feature in no recognised slot draws
black. Add it to a descriptor's `Attributes.Diffuse` and the material is done. [3, 6]

## 8. Wrap a compiled material

A `Material` asset that is already compiled has no descriptor, but its pass parameters hold the
generated shader sources. A layer of your own can be put in front of its own layers by copying the
parameters and rebuilding the surface array - the voxel grid does this to draw each of its
material ids with an unmodified Stride material.

```hlsl
shader MaterialSurfaceReadGBuffer : IMaterialSurfacePixel, MaterialPixelStream, NormalStream, PositionStream4
{
    rgroup PerMaterial
    {
        [Link("MyGBuffer.Normal")]
        stage Texture2D NormalTexture;
    }

    override void Compute()
    {
        // Replace what the mesh gave the material with what a pass computed earlier: here the
        // normal, read where this pixel is.
        float4 packed = NormalTexture.Load(int3(streams.ShadingPosition.xy, 0));
        if (packed.w < 0.5)
            discard;
        streams.normalWS = packed.xyz;
    }
};
```

```csharp
static Material Wrap(Material source, Texture normalTexture)
{
    var pass = source.Passes[0];
    var parameters = new ParameterCollection(pass.Parameters); // a copy; the source is untouched

    var pixel = parameters.Get(MaterialKeys.PixelStageSurfaceShaders) as ShaderMixinSource ?? new ShaderMixinSource();
    var layers = new ShaderArraySource();
    layers.Add(new ShaderClassSource("MaterialSurfaceReadGBuffer"));
    foreach (var layer in ExistingLayers(pixel))
        layers.Add(layer);

    var surface = new ShaderMixinSource();
    surface.Mixins.Add(new ShaderClassSource("MaterialSurfaceArray"));
    surface.Compositions["layers"] = layers;
    parameters.Set(MaterialKeys.PixelStageSurfaceShaders, surface);
    parameters.Set(MyGBufferKeys.Normal, normalTexture);

    var wrapped = new Material();
    wrapped.Passes.Add(new MaterialPass(parameters) { HasTransparency = pass.HasTransparency, CullMode = pass.CullMode });
    return wrapped;
}

static IEnumerable<ShaderSource> ExistingLayers(ShaderMixinSource pixel)
    => pixel.Compositions.TryGetValue("layers", out var existing) && existing is ShaderArraySource array
        ? array.Values
        : Array.Empty<ShaderSource>();
```

The wrapped material is a new permutation of the forward effect: the source's own layers, one of
yours in front. Everything the source's parameters held - textures, constants, its keys - is in
the copy, so the layers behind yours find what they expect. Set your own keys on the copy; the
`[Link]` names make them reachable from inside the array. The one thing the copy does not carry is
the descriptor, so a wrapped material cannot be wrapped again from a descriptor - wrap it from its
parameters, the same way. [3, 6]

## Where to look when something is wrong

The compiled HLSL and the parameter list for every permutation are under `cache/effects/` next
to the executable, in debug builds. Read `_meta.txt` to see which keys the effect binds, and the
`_ps.hlsl` to see what your SDSL became. When the effect will not compile, FXC's rejected HLSL is
dumped to `%TEMP%\stride-fxc-fail-*` with the error at the line it failed on. When a shader edit
appears to change nothing, delete `cache/shaders/`.
