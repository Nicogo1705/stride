// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Linq;
using Xunit;

using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Games;
using Stride.Graphics;
using Stride.Graphics.Regression;
using Stride.Rendering;
using Stride.Rendering.Compositing;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;
using Stride.Rendering.ProceduralModels;

namespace Stride.Engine.Tests;

public class TestOpacityThickness : GameTestBase
{
    private const float Alpha = 0.6f;
    private const float OpacityThickness = 0.5f;
    // Frames left to the effects to be requested, then to compile, before the pixel is read
    private const int MinFrames = 5;
    private const int MaxFrames = 600;
    // Two 8-bit steps, for the sRGB round trip of the back buffer
    private const float Tolerance = 2f / 255;

    /// <summary>
    /// An absorbing volume in front of a white background lets each channel through as <c>(1 - Alpha * (1 - Tint))</c>
    /// to the power of its thickness over <see cref="MaterialTransparencyBlendFeature.OpacityThickness"/>.
    /// </summary>
    [Theory]
    [InlineData(0.5f, false)]
    [InlineData(1f, false)]
    [InlineData(1f, true)]
    public static void AbsorbingVolumeTransmitsByItsThickness(float depth, bool separateSegments)
    {
        Color4 center = default;
        RunScene(depth, separateSegments, plainMaterial: null, pixel => center = pixel);

        // A red tint lets red through whatever the thickness, and filters green and blue by 1 - Alpha per OpacityThickness
        var expected = MathF.Pow(1 - Alpha, depth / OpacityThickness);
        Assert.True(MathUtil.WithinEpsilon(center.R, 1f, Tolerance), $"Red is {center.R}, expected 1.");
        Assert.True(MathUtil.WithinEpsilon(center.G, expected, Tolerance), $"Green is {center.G}, expected {expected}.");
        Assert.True(MathUtil.WithinEpsilon(center.B, expected, Tolerance), $"Blue is {center.B}, expected {expected}.");
    }

    /// <summary>
    /// Drawing a volume next to a plain transparent material leaves that material's parameters untouched.
    /// </summary>
    [Fact]
    public static void PlainTransparentMaterialKeepsItsParameters()
    {
        Material plain = null;
        RunScene(1f, false, plainMaterial: device => plain = CreateMaterial(device, opacityThickness: 0), _ => { });

        Assert.All(plain.Passes, pass => Assert.False(pass.Parameters.ContainsKey(MaterialVolumeKeys.Absorption)));
    }

    private static void RunScene(float depth, bool separateSegments, Func<GraphicsDevice, Material> plainMaterial, Action<Color4> readCenter)
    {
        var game = new SceneGame();
        game.Script.AddTask(async () =>
        {
            game.ScreenShotAutomationEnabled = false;
            var device = game.GraphicsDevice;
            var camera = new CameraComponent { NearClipPlane = 0.1f, FarClipPlane = 100f };
            var compositor = GraphicsCompositorHelper.CreateDefault(false, camera: camera, clearColor: Color.White, graphicsProfile: GraphicsProfile.Level_11_0);
            ((ForwardRenderer)((SceneCameraRenderer)compositor.Game).Child).SeparateVolumeSegments = separateSegments;
            game.SceneSystem.GraphicsCompositor = compositor;

            // The camera looks down -Z at a box whose centre ray crosses it over its depth
            var scene = game.SceneSystem.SceneInstance.RootScene;
            scene.Entities.Add(new Entity { camera });
            scene.Entities.Add(CreateBox(game.Services, CreateMaterial(device, OpacityThickness), new Vector3(4, 4, depth), new Vector3(0, 0, -5)));
            if (plainMaterial != null)
                scene.Entities.Add(CreateBox(game.Services, plainMaterial(device), new Vector3(1, 1, 1), new Vector3(3, 0, -8)));

            var meshRenderFeature = compositor.RenderFeatures.OfType<MeshRenderFeature>().Single();
            do
            {
                await game.Script.NextFrame();
            }
            while ((game.UpdateTime.FrameCount < MinFrames || IsCompiling(meshRenderFeature)) && game.UpdateTime.FrameCount < MaxFrames);

            // Two more frames: one draws with the compiled effects, the next reads it
            game.ReadCenter = true;
            await game.Script.NextFrame();
            await game.Script.NextFrame();
            readCenter(game.Center);
            game.Exit();
        });
        RunGameTest(game);
    }

    private static Material CreateMaterial(GraphicsDevice device, float opacityThickness)
    {
        return Material.New(device, new MaterialDescriptor
        {
            Attributes =
            {
                Diffuse = new MaterialDiffuseMapFeature(new ComputeColor(Color.Black)),
                DiffuseModel = new MaterialDiffuseLambertModelFeature(),
                Transparency = new MaterialTransparencyBlendFeature
                {
                    Alpha = new ComputeFloat(Alpha),
                    Tint = new ComputeColor(new Color4(1, 0, 0, 1)),
                    OpacityThickness = opacityThickness,
                    Medium = MaterialVolumeMedium.Absorbing,
                },
            },
        });
    }

    private static Entity CreateBox(IServiceRegistry services, Material material, Vector3 size, Vector3 position)
    {
        var cube = new CubeProceduralModel { Size = size, MaterialInstance = { Material = material } };
        var entity = new Entity { new ModelComponent(new ProceduralModelDescriptor(cube).GenerateModel(services)) };
        entity.Transform.Position = position;
        return entity;
    }

    private static bool IsCompiling(MeshRenderFeature meshRenderFeature)
    {
        var renderEffects = meshRenderFeature.RenderData.GetData(meshRenderFeature.RenderEffectKey);
        var slotCount = meshRenderFeature.EffectPermutationSlotCount;
        foreach (var renderObject in meshRenderFeature.RenderObjects)
        {
            for (int slot = 0; slot < slotCount; slot++)
            {
                if (renderEffects[renderObject.StaticObjectNode * slotCount + slot] is { State: RenderEffectState.Compiling })
                    return true;
            }
        }
        return false;
    }

    private sealed class SceneGame : GameTestBase
    {
        public bool ReadCenter;
        public Color4 Center;

        public SceneGame()
        {
            GraphicsDeviceManager.PreferredGraphicsProfile = [GraphicsProfile.Level_11_0];
            GraphicsDeviceManager.ShaderProfile = GraphicsProfile.Level_11_0;
        }

        protected override void Initialize()
        {
            base.Initialize();
            // After the scene, before the frame is presented
            GameSystems.Add(new CenterReader(Services, this) { DrawOrder = int.MaxValue, Visible = true });
        }

        private sealed class CenterReader(IServiceRegistry services, SceneGame game) : GameSystemBase(services)
        {
            public override void Draw(GameTime gameTime)
            {
                if (!game.ReadCenter)
                    return;

                var backBuffer = game.GraphicsDevice.Presenter.BackBuffer;
                var pixels = backBuffer.GetData<Color>(game.GraphicsContext.CommandList);
                var center = pixels[backBuffer.Height / 2 * backBuffer.Width + backBuffer.Width / 2].ToVector4();
                game.Center = backBuffer.Format.IsSRgb
                    ? new Color4(MathUtil.SRgbToLinear(center.X), MathUtil.SRgbToLinear(center.Y), MathUtil.SRgbToLinear(center.Z), center.W)
                    : new Color4(center);
            }
        }
    }
}
