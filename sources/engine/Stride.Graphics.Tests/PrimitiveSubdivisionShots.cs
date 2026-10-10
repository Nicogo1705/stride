// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using Stride.Core.Mathematics;
using Stride.Games;
using Stride.Graphics.GeometricPrimitives;
using Stride.Rendering;

namespace Stride.Graphics.Tests
{
    /// <summary>
    /// Writes one image per procedural primitive and subdivision value (filled surface and its triangle edges)
    /// to PRIMITIVE_SHOTS_OUT. Skipped when the variable is not set.
    /// </summary>
    public class PrimitiveSubdivisionShots : GraphicTestGameBase
    {
        private static readonly (string Name, Func<GeometricMeshData<VertexPositionNormalTexture>> Create, Matrix World)[] Cases =
        [
            ("capsule-0", () => GeometricPrimitive.Capsule.New(1.4f, 0.4f, 8, 1, 1, false, lengthRings: 0), Matrix.Identity),
            ("capsule-8", () => GeometricPrimitive.Capsule.New(1.4f, 0.4f, 8, 1, 1, false, lengthRings: 8), Matrix.Identity),
            ("cylinder-0", () => GeometricPrimitive.Cylinder.New(1.8f, 0.5f, 24, 1, 1, false, heightRings: 0), Matrix.Identity),
            ("cylinder-8", () => GeometricPrimitive.Cylinder.New(1.8f, 0.5f, 24, 1, 1, false, heightRings: 8), Matrix.Identity),
            ("cube-0", () => GeometricPrimitive.Cube.New(1.3f, 1, 1, false, subdivisions: 0), Matrix.Identity),
            ("cube-6", () => GeometricPrimitive.Cube.New(1.3f, 1, 1, false, subdivisions: 6), Matrix.Identity),
        ];

        private readonly string output;
        private EffectInstance effect;
        private Texture fill, edges;
        private GeometricPrimitive[] primitives;

        public PrimitiveSubdivisionShots() : this(null) { }

        private PrimitiveSubdivisionShots(string output)
        {
            this.output = output;
            GraphicsDeviceManager.PreferredBackBufferWidth = 600;
            GraphicsDeviceManager.PreferredBackBufferHeight = 600;
            GraphicsDeviceManager.DeviceCreationFlags = DeviceCreationFlags.None;
            GraphicsDeviceManager.ShaderProfile = GraphicsProfile.Level_11_0;
        }

        protected override async Task LoadContent()
        {
            await base.LoadContent();

            effect = new EffectInstance(new Effect(GraphicsDevice, SpriteEffect.Bytecode));
            fill = Texture.New2D(GraphicsDevice, 1, 1, PixelFormat.R8G8B8A8_UNorm, new[] { new Color(205, 212, 222, 255) });
            edges = Texture.New2D(GraphicsDevice, 1, 1, PixelFormat.R8G8B8A8_UNorm, new[] { new Color(20, 40, 110, 255) });
            primitives = Array.ConvertAll(Cases, c => new GeometricPrimitive(GraphicsDevice, c.Create()));
        }

        protected override void RegisterTests()
        {
            base.RegisterTests();

            // One case per frame.
            for (int i = 0; i < Cases.Length; i++)
            {
                var index = i;
                FrameGameSystem.Draw(() => DrawCase(index));
            }
        }

        private void DrawCase(int index)
        {
            var commandList = GraphicsContext.CommandList;
            commandList.Clear(GraphicsDevice.Presenter.BackBuffer, Color.White);
            commandList.Clear(GraphicsDevice.Presenter.DepthStencilBuffer, DepthStencilClearOptions.DepthBuffer | DepthStencilClearOptions.Stencil);
            commandList.SetRenderTargetAndViewport(GraphicsDevice.Presenter.DepthStencilBuffer, GraphicsDevice.Presenter.BackBuffer);

            var (name, _, world) = Cases[index];
            var primitive = primitives[index];

            var view = Matrix.LookAtRH(new Vector3(1.9f, 1.5f, 2.4f), Vector3.Zero, Vector3.UnitY);
            var projection = Matrix.PerspectiveFovRH(MathF.PI / 4, 1, 0.1f, 20);
            var viewProjection = view * projection;

            // Filled surface, then its edges drawn slightly larger so they are not hidden by the depth test.
            primitive.PipelineState.State.RasterizerState = RasterizerStates.CullBack;
            effect.Parameters.Set(TexturingKeys.Texture0, fill);
            effect.Parameters.Set(SpriteBaseKeys.MatrixTransform, world * viewProjection);
            effect.UpdateEffect(GraphicsDevice);
            primitive.Draw(GraphicsContext, effect);

            primitive.PipelineState.State.RasterizerState = new RasterizerStateDescription(CullMode.Back) { FillMode = FillMode.Wireframe };
            effect.Parameters.Set(TexturingKeys.Texture0, edges);
            effect.Parameters.Set(SpriteBaseKeys.MatrixTransform, Matrix.Scaling(1.004f) * world * viewProjection);
            primitive.Draw(GraphicsContext, effect);

            Directory.CreateDirectory(output);
            using var stream = File.Create(Path.Combine(output, name + ".png"));
            GraphicsDevice.Presenter.BackBuffer.Save(commandList, stream, ImageFileType.Png);
        }

        [SkippableFact]
        public void RenderPrimitiveSubdivisions()
        {
            var output = Environment.GetEnvironmentVariable("PRIMITIVE_SHOTS_OUT");
            Skip.If(output is null, "PRIMITIVE_SHOTS_OUT is not set");

            var game = new PrimitiveSubdivisionShots(output) { ScreenShotAutomationEnabled = false };
            RunGameTest(game);
        }
    }
}
