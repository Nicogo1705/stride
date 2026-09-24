// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using Stride.Engine;
using Stride.Graphics;
using VoxelTerrain;

// The scene is built in code once the game is up, so that everything the sample shows is in one
// readable place (VoxelTerrainScene) rather than spread over assets.
//
//   --rings=N          rings around the camera (default 10: 8 km out at the defaults, 16 km across)
//   --ring-samples=N   samples along each axis of a ring (default 129; cubic in cost)
//   --cell=F           world size of the finest cell (default 0.25)
//   --seed=N           another world
//   --gi=off|low|medium|high   the indirect light's tier at start (default medium)
//   --shadow-rings=N   how many of the finest rings cast shadows (default 3)
//   --no-lod           no level of detail inside a ring
//   --beam=N           beam pre-pass block size in pixels, 0 to disable
//   --pos=x,y,z        where the camera starts
//   --shot=FILE --exit-after=SECONDS   save a screenshot, print the frame rate, quit
//   --profiler=fps|cpu|gpu   open the engine's profiler page

var options = TerrainOptions.Parse(args);

using var game = new Game();

// A Debug build turns on the D3D11 debug layer, and voxelization trips a validation message each
// frame (the clipmap is rebound for writing while the previous frame's lighting still reads it,
// which D3D resolves on its own); the console then fills with it and nothing else is readable.
game.GraphicsDeviceManager.DeviceCreationFlags = DeviceCreationFlags.None;

game.Script.AddTask(async () =>
{
    await game.Script.NextFrame();
    VoxelTerrainScene.Build(game, options);
});

game.Run();
