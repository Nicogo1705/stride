# Video showcase demo

One world on a voxel terrain, a zone per feature: voxel playground, rigid and soft characters in every `SkeletonBodyMode`,
soft garden, torture lab, ropes and cables (rope bridge, crane, cable car, swing, trampoline), deformation stress.

## Requirements

The demo runs on the shared demo harness, compiled into this test project from a separate repository.
Clone it next to your other repositories, at exactly this path (the `.csproj` imports it from there):

```
git clone https://github.com/Nicogo1705/StrideHarness %USERPROFILE%\source\repos\StrideHarness
```

Without it the project still builds, but without the demo.

## Build

From the root of the repository (Windows, Direct3D 11):

```
dotnet build sources/engine/Stride.BepuPhysics/Stride.BepuPhysics.Soft.Tests/Stride.BepuPhysics.Soft.Tests.csproj -c Release -p:StrideTestRuntimeIdentifier=win-x64 -p:StrideSkipAutoPack=true -p:StridePlatforms=Windows -p:StrideGraphicsApis=Direct3D11 -p:StrideGraphicsApi=Direct3D11
```

## Run

The demo is an opt-in test, picked by environment variables (bash syntax):

```
# Interactive
STRIDE_BEPU_DEMO=1 STRIDE_TESTS_GPU=1 STRIDE_DEMO_LABEL="video-demo" dotnet test sources/engine/Stride.BepuPhysics/Stride.BepuPhysics.Soft.Tests/Stride.BepuPhysics.Soft.Tests.csproj --no-build -c Release -p:StrideGraphicsApis=Direct3D11 -p:StrideGraphicsApi=Direct3D11 --filter "FullyQualifiedName~VideoShowcaseDemo.Interactive"

# Scripted tour (report and captures in the tour folder): VIDEO_TOUR_ONLY=6 for one place, VIDEO_TOUR_VIEWS=0 to skip the debug views, VIDEO_TOUR_STEPS
STRIDE_BEPU_DEMO_TOUR=1 STRIDE_TESTS_GPU=1 dotnet test ... --filter "FullyQualifiedName~VideoShowcaseDemo.Tour"

# Gallery: a capture of every place, compact overlay
STRIDE_DEMO_GALLERY=1 STRIDE_BEPU_DEMO_TOUR=1 STRIDE_TESTS_GPU=1 dotnet test ... --filter "FullyQualifiedName~VideoShowcaseDemo.Tour"
```

`STRIDE_DEMO_CAPTURES` sets where P saves its screenshots (default: `harness/captures` at the root of the repository).

## Keys

Esc lists the places, Z Q S D / A E move (AZERTY), right drag looks around, left button grabs with the gravity gun,
middle click throws a ball, V cycles the debug views, H the overlay, K the frame rate cap, P saves a screenshot and the state.
