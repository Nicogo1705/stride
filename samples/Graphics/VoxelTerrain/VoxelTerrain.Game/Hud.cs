// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System.Linq;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Input;
using Stride.Profiling;
using Stride.Rendering.Voxels.Grid;

namespace VoxelTerrain;

/// <summary>The keys, what they are set to, and what the aim is on - printed through the engine's debug text, so no font asset is needed.</summary>
public sealed class Hud : SyncScript
{
    public Digger? Digger { get; set; }

    private DebugTextSystem? debugText;

    public override void Start() => debugText = Services.GetService<DebugTextSystem>();

    public override void Update()
    {
        if (Input.IsKeyPressed(Keys.G))
            VoxelTerrainScene.ToggleGI();
        if (Input.IsKeyPressed(Keys.B))
            VoxelTerrainScene.CycleSurface();
        if (Input.IsKeyPressed(Keys.L))
            VoxelTerrainScene.LightsEnabled = !VoxelTerrainScene.LightsEnabled;
        if (Input.IsKeyPressed(Keys.H))
            VoxelTerrainScene.CastShadows = !VoxelTerrainScene.CastShadows;
        if (Input.IsKeyPressed(Keys.O))
            VoxelTerrainScene.CycleGIQuality();

        var y = 10;
        void Line(string text)
        {
            debugText?.Print(text, new Int2(10, y));
            y += 18;
        }

        var clipmap = VoxelTerrainScene.Clipmap;
        var position = Entity.Transform.Position;
        Line("Right mouse: look, + WASD/ZQSD/arrows fly, C/E down/up, Shift fast");
        Line($"Left mouse: dig    F: fill    T: pour water    aim: {Digger?.Status ?? "-"}    brushes: {clipmap?.BrushCount ?? 0}");
        if (VoxelTerrainScene.Water is { } water)
            Line($"Water: {WaterSim.Size}^2 columns of {WaterSim.Cell} m, {water.Steps} steps{(VoxelTerrainScene.WaterSurface?.Underwater == true ? ", under water" : "")}");
        Line($"G: voxel GI {(VoxelTerrainScene.GIEnabled ? "on" : "off")}    O: GI quality {VoxelTerrainScene.GIQuality}");
        Line($"B: surface {VoxelTerrainScene.Surface switch { VoxelSurfaceForm.Cubes => "cubes", VoxelSurfaceForm.SurfaceNets => "surface nets", _ => "smooth" }}");
        Line($"L: sun and ambient {(VoxelTerrainScene.LightsEnabled ? "on" : "off")}    H: terrain shadows {(VoxelTerrainScene.CastShadows ? "on" : "off")}");
        if (clipmap is not null)
        {
            var outer = clipmap.Rings[^1];
            Line($"{clipmap.Rings.Count} rings of {clipmap.Samples}^3, cells {clipmap.Rings[0].CellSize:0.###} m to {outer.CellSize:0.###} m, {clipmap.Extent(outer) / 1000f:0.##} km across. No mesh, no chunks.");
            Line($"Generations: {string.Join(" ", clipmap.Rings.Select(r => r.Generations))}    at {position.X:0},{position.Y:0},{position.Z:0}    {Game.UpdateTime.FramePerSecond:0} fps");
        }
    }
}
