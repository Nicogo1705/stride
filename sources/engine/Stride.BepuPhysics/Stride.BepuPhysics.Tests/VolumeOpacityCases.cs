// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Engine.Processors;
using Stride.Extensions;
using Stride.Games;
using Stride.Graphics;
using Stride.Graphics.GeometricPrimitives;
using Stride.Graphics.Regression;
using Stride.Rendering;
using Stride.Rendering.Colors;
using Stride.Rendering.Lights;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;
using Xunit;

namespace Stride.BepuPhysics.Tests
{
    /// <summary>
    /// One small scene per case: a material with OpacityThickness and another Stride material in front of or behind it.
    /// Writes &lt;case&gt;-0-top.png (from above, the camera marked by a cone) and &lt;case&gt;-1-view.png to VOLUME_CASES_OUT.
    /// </summary>
    public class VolumeOpacityCases : GameTestBase
    {
        private static readonly Vector3 ViewFrom = new(0f, 2.7f, 3.4f), ViewTo = new(0f, -0.2f, -0.3f);

        private sealed class Capture(Stride.Core.IServiceRegistry services) : GameSystemBase(services)
        {
            public string Path;

            public override void Draw(GameTime gameTime)
            {
                if (Path == null)
                    return;
                var game = (Game)Game;
                using var stream = File.Create(Path);
                game.GraphicsDevice.Presenter.BackBuffer.Save(game.GraphicsContext.CommandList, stream, ImageFileType.Png);
                Path = null;
            }
        }

        // Colour code: red solid, green alpha, yellow additive, white glass, orange absorbing volume, purple scattering volume, grey scenery
        private static readonly Color4 Solid = new(0.9f, 0.1f, 0.1f, 1f), AlphaColour = new(0.1f, 0.85f, 0.1f, 1f), AdditiveColour = new(1f, 0.8f, 0.05f, 1f), GlassColour = new(0.95f, 0.95f, 0.95f, 1f), Scenery = new(0.55f, 0.55f, 0.55f, 1f), SceneryDark = new(0.35f, 0.35f, 0.38f, 1f);
        private static readonly Color Absorbing = new(1f, 0.55f, 0.05f), Scattering = new(0.65f, 0.3f, 1f), ScatteringLight = new(0.85f, 0.65f, 1f), ScatteringDeep = new(0.45f, 0.2f, 0.85f);

        private static MaterialDescriptor Lit(Color4 colour, float gloss = 0.4f) => new()
        {
            Attributes =
            {
                Diffuse = new MaterialDiffuseMapFeature(new ComputeColor(colour)),
                DiffuseModel = new MaterialDiffuseLambertModelFeature(),
                MicroSurface = new MaterialGlossinessMapFeature(new ComputeFloat(gloss)),
                Specular = new MaterialMetalnessMapFeature(new ComputeFloat(0f)),
                SpecularModel = new MaterialSpecularMicrofacetModelFeature { Environment = new MaterialSpecularMicrofacetEnvironmentGGXPolynomial() },
            },
        };

        private static MaterialDescriptor Volume(Color colour, float alpha, float thickness, MaterialVolumeMedium medium)
        {
            var descriptor = Lit(new Color4(colour.ToColor3(), 1), 0.9f);
            descriptor.Attributes.Transparency = new MaterialTransparencyBlendFeature
            {
                Alpha = new ComputeFloat(alpha),
                Tint = new ComputeColor(medium == MaterialVolumeMedium.Absorbing ? colour : Color.White),
                OpacityThickness = thickness,
                Medium = medium,
            };
            return descriptor;
        }

        private static MaterialDescriptor AlphaBlend(Color4 colour)
        {
            var descriptor = Lit(colour);
            descriptor.Attributes.Transparency = new MaterialTransparencyBlendFeature { Alpha = new ComputeFloat(0.75f) };
            return descriptor;
        }

        private static MaterialDescriptor Additive(Color4 colour)
        {
            var descriptor = Lit(colour);
            descriptor.Attributes.Transparency = new MaterialTransparencyAdditiveFeature { Alpha = new ComputeFloat(1f) };
            return descriptor;
        }

        private static MaterialDescriptor ThinGlass() => new()
        {
            Attributes =
            {
                Diffuse = new MaterialDiffuseMapFeature(new ComputeColor(GlassColour)),
                DiffuseModel = new MaterialDiffuseLambertModelFeature(),
                MicroSurface = new MaterialGlossinessMapFeature(new ComputeFloat(0.95f)),
                Specular = new MaterialMetalnessMapFeature(new ComputeFloat(0f)),
                SpecularModel = new MaterialSpecularThinGlassModelFeature(),
                Transparency = new MaterialTransparencyBlendFeature { Alpha = new ComputeFloat(0.5f) },
            },
        };

        // Front face at z = 0, from depth thin at x = -h to thick at x = +h: several thicknesses in one shot
        private static GeometricMeshData<VertexPositionNormalTexture> Wedge(float h, float height, float thin, float thick)
        {
            Vector3 f0 = new(-h, 0, 0), f1 = new(h, 0, 0), f2 = new(h, height, 0), f3 = new(-h, height, 0);
            Vector3 b0 = new(-h, 0, -thin), b1 = new(h, 0, -thick), b2 = new(h, height, -thick), b3 = new(-h, height, -thin);
            var inside = new Vector3(0, height / 2, -(thin + thick) / 4);
            Vector3[][] faces = [[f0, f1, f2, f3], [b0, b1, b2, b3], [f0, f3, b3, b0], [f1, f2, b2, b1], [f3, f2, b2, b3], [f0, f1, b1, b0]];
            var vertices = new List<VertexPositionNormalTexture>();
            var indices = new List<int>();
            foreach (var face in faces)
            {
                var centre = (face[0] + face[1] + face[2] + face[3]) / 4;
                var normal = Vector3.Normalize(Vector3.Cross(face[1] - face[0], face[2] - face[0]));
                if (Vector3.Dot(normal, centre - inside) < 0)
                    normal = -normal;
                var clockwise = Vector3.Dot(Vector3.Cross(face[1] - face[0], face[2] - face[0]), normal) < 0;
                int start = vertices.Count;
                foreach (var v in face)
                    vertices.Add(new VertexPositionNormalTexture(v, normal, Vector2.Zero));
                indices.AddRange(clockwise ? [start, start + 1, start + 2, start, start + 2, start + 3] : [start, start + 2, start + 1, start, start + 3, start + 2]);
            }
            return new GeometricMeshData<VertexPositionNormalTexture>(vertices.ToArray(), indices.ToArray(), false);
        }

        // Bounds from the vertices: culling and shadow cascades use them
        private static Entity Make(GraphicsDevice device, GeometricMeshData<VertexPositionNormalTexture> data, MaterialDescriptor descriptor, Vector3 position, Quaternion? rotation = null)
        {
            var primitive = new GeometricPrimitive(device, data);
            var bounds = BoundingBox.FromPoints(System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(data.Vertices, vertex => vertex.Position)));
            var model = new Model { Material.New(device, descriptor) };
            model.Add(new Mesh { Draw = primitive.ToMeshDraw(), MaterialIndex = 0, BoundingBox = bounds });
            var entity = new Entity { new ModelComponent { Model = model } };
            entity.Transform.Position = position;
            entity.Transform.Rotation = rotation ?? Quaternion.Identity;
            return entity;
        }

        // The thickness material under test, around the origin
        private static Entity[] Subject(GraphicsDevice device, char kind) => kind switch
        {
            // Absorbing glass wedge: 5 cm thick on the left, 1 m on the right
            'A' => [Make(device, Wedge(0.9f, 1.2f, 0.05f, 1f), Volume(Absorbing, 0.6f, 0.5f, MaterialVolumeMedium.Absorbing), new Vector3(0, 0, 0.5f))],
            // Scattering jelly wedge
            'S' => [Make(device, Wedge(0.9f, 1.2f, 0.05f, 1f), Volume(Scattering, 0.7f, 0.5f, MaterialVolumeMedium.Scattering), new Vector3(0, 0, 0.5f))],
            // Water: a box filling a pit whose bottom slopes from 0.1 m to 1.2 m deep, sunk below it so the water ends at the ground
            'W' => [Make(device, GeometricPrimitive.Cube.New(new Vector3(2.8f, 2.5f, 2.4f)), Volume(ScatteringDeep, 0.7f, 0.6f, MaterialVolumeMedium.Scattering), new Vector3(0, -1.25f, 0)),
                    Make(device, GeometricPrimitive.Cube.New(new Vector3(2.4f, 0.1f, 2.6f)), Lit(Scenery), new Vector3(0, -0.65f, 0), Quaternion.RotationX(0.45f))],
            // Toxic gas: a box going through a wall and into the ground
            'G' => [Make(device, GeometricPrimitive.Cube.New(new Vector3(3f, 1.8f, 0.25f)), Lit(SceneryDark), new Vector3(0, 0.9f, -0.6f)),
                    Make(device, GeometricPrimitive.Cube.New(new Vector3(2f, 1.4f, 2f)), Gas(), new Vector3(0, 0.4f, -0.4f))],
            _ => throw new ArgumentException(kind.ToString()),
        };

        private static MaterialDescriptor Gas()
        {
            var descriptor = Volume(Scattering, 0.5f, 1.5f, MaterialVolumeMedium.Scattering);
            descriptor.Attributes.MicroSurface = null;
            descriptor.Attributes.Specular = null;
            descriptor.Attributes.SpecularModel = null;
            return descriptor;
        }

        private static Entity Scaled(Entity entity, Vector3 scale)
        {
            entity.Transform.Scale = scale;
            return entity;
        }

        // Gas whose density is drifting noise, from a shader node of the world position
        private static MaterialDescriptor NoisyGas()
        {
            var descriptor = Volume(Scattering, 0.6f, 0.8f, MaterialVolumeMedium.Scattering);
            // A gas has no surface to reflect light
            descriptor.Attributes.MicroSurface = null;
            descriptor.Attributes.Specular = null;
            descriptor.Attributes.SpecularModel = null;
            ((MaterialTransparencyBlendFeature)descriptor.Attributes.Transparency).Density = new ComputeShaderClassScalar { MixinReference = "VolumeOpacityHashDensity" };
            return descriptor;
        }

        // Another Stride material, as a small object
        private static Entity Other(GraphicsDevice device, string other, Vector3 position) => other switch
        {
            "opaque" => Make(device, GeometricPrimitive.Sphere.New(0.35f), Lit(Solid), position),
            "alpha" => Make(device, GeometricPrimitive.Cube.New(new Vector3(0.8f, 0.8f, 0.25f)), AlphaBlend(AlphaColour), position),
            "additive" => Make(device, GeometricPrimitive.Cube.New(new Vector3(0.8f, 0.8f, 0.25f)), Additive(AdditiveColour), position),
            "glass" => Make(device, GeometricPrimitive.Sphere.New(0.4f, 32), ThinGlass(), position),
            "absorbing" => Make(device, GeometricPrimitive.Cube.New(0.6f), Volume(Absorbing, 0.8f, 0.3f, MaterialVolumeMedium.Absorbing), position),
            "scattering" => Make(device, GeometricPrimitive.Sphere.New(0.4f, 32), Volume(ScatteringLight, 0.75f, 0.4f, MaterialVolumeMedium.Scattering), position),
            _ => throw new ArgumentException(other),
        };

        private static readonly string[] Others = { "opaque", "alpha", "additive", "glass", "absorbing", "scattering" };

        // Fly camera, AZERTY and QWERTY: ZQSD / W / arrows to move, A/E down and up, right mouse to look, shift to go faster
        private sealed class FlyCamera : SyncScript
        {
            private float yaw, pitch = -0.3f;

            public float Yaw { get => yaw; set => yaw = value; }
            public float Pitch { get => pitch; set => pitch = value; }

            public override void Update()
            {
                var input = Input;
                if (input.IsMouseButtonDown(Stride.Input.MouseButton.Right))
                {
                    yaw -= input.MouseDelta.X * 3f;
                    pitch = MathUtil.Clamp(pitch - input.MouseDelta.Y * 3f, -1.5f, 1.5f);
                }
                Entity.Transform.Rotation = Quaternion.RotationYawPitchRoll(yaw, pitch, 0);

                var move = Vector3.Zero;
                if (input.IsKeyDown(Stride.Input.Keys.Z) || input.IsKeyDown(Stride.Input.Keys.W) || input.IsKeyDown(Stride.Input.Keys.Up)) move.Z -= 1;
                if (input.IsKeyDown(Stride.Input.Keys.S) || input.IsKeyDown(Stride.Input.Keys.Down)) move.Z += 1;
                if (input.IsKeyDown(Stride.Input.Keys.Q) || input.IsKeyDown(Stride.Input.Keys.Left)) move.X -= 1;
                if (input.IsKeyDown(Stride.Input.Keys.D) || input.IsKeyDown(Stride.Input.Keys.Right)) move.X += 1;
                if (input.IsKeyDown(Stride.Input.Keys.E) || input.IsKeyDown(Stride.Input.Keys.Space)) move.Y += 1;
                if (input.IsKeyDown(Stride.Input.Keys.A) || input.IsKeyDown(Stride.Input.Keys.LeftCtrl)) move.Y -= 1;
                var speed = (input.IsKeyDown(Stride.Input.Keys.LeftShift) ? 12f : 4f) * (float)Game.UpdateTime.Elapsed.TotalSeconds;
                Entity.Transform.Position += Vector3.Transform(move, Entity.Transform.Rotation) * speed;
            }
        }

        // A free-camera demo of every case in one scene: right mouse + move keys to fly, T toggles OpacityThickness
        [Fact]
        public static void Demo()
        {
            if (Environment.GetEnvironmentVariable("VOLUME_DEMO") != "1")
                return;

            ForceInteractiveMode = true;
            var game = new GameTest();
            game.GraphicsDeviceManager.PreferredGraphicsProfile = [GraphicsProfile.Level_11_0];
            game.GraphicsDeviceManager.ShaderProfile = GraphicsProfile.Level_11_0;
            game.GraphicsDeviceManager.PreferredBackBufferWidth = 1600;
            game.GraphicsDeviceManager.PreferredBackBufferHeight = 900;
            game.GraphicsDeviceManager.DeviceCreationFlags = DeviceCreationFlags.None;

            game.Script.AddTask(async () =>
            {
                game.ScreenShotAutomationEnabled = false;
                var scene = game.SceneSystem.SceneInstance.RootScene;
                var device = game.GraphicsDevice;
                var camera = new CameraComponent { VerticalFieldOfView = 60f };
                game.SceneSystem.GraphicsCompositor = Stride.Rendering.Compositing.GraphicsCompositorHelper.CreateDefault(true, camera: camera, clearColor: new Color4(0.45f, 0.6f, 0.8f, 1f));
                var forward = (Stride.Rendering.Compositing.ForwardRenderer)((Stride.Rendering.Compositing.SceneCameraRenderer)game.SceneSystem.GraphicsCompositor.Game).Child;
                var stage = forward.VolumeThicknessRenderStage;
                var cameraEntity = new Entity("camera") { camera, new FlyCamera() };
                cameraEntity.Transform.Position = new Vector3(0, 3.5f, 9f);
                scene.Entities.Add(cameraEntity);

                var sun = new Entity { new LightComponent { Type = new LightDirectional { Color = new ColorRgbProvider(Color.White), Shadow = { Enabled = true, Filter = new LightShadowMapFilterTypePcf { FilterSize = LightShadowMapFilterTypePcfSize.Filter5x5 } } }, Intensity = 2.5f } };
                sun.Transform.Rotation = Quaternion.RotationYawPitchRoll(0.5f, -1f, 0f);
                scene.Entities.Add(sun);
                scene.Entities.Add(new Entity { new LightComponent { Type = new LightAmbient { Color = new ColorRgbProvider(Color.White) }, Intensity = 0.5f } });

                void Place(Entity[] entities, Vector3 offset)
                {
                    foreach (var entity in entities)
                    {
                        entity.Transform.Position += offset;
                        scene.Entities.Add(entity);
                    }
                }

                // Ground around a pit at the origin, a striped wall at the back
                var sand = Lit(Scenery);
                Place([Make(device, GeometricPrimitive.Cube.New(new Vector3(30f, 1.5f, 13.8f)), sand, new Vector3(0, -0.75f, 8.1f))], Vector3.Zero);
                Place([Make(device, GeometricPrimitive.Cube.New(new Vector3(30f, 1.5f, 10f)), sand, new Vector3(0, -0.75f, -6.2f))], Vector3.Zero);
                Place([Make(device, GeometricPrimitive.Cube.New(new Vector3(13.6f, 1.5f, 2.4f)), sand, new Vector3(-8.2f, -0.75f, 0))], Vector3.Zero);
                Place([Make(device, GeometricPrimitive.Cube.New(new Vector3(13.6f, 1.5f, 2.4f)), sand, new Vector3(8.2f, -0.75f, 0))], Vector3.Zero);
                Color4[] stripes = [new(0.1f, 0.1f, 0.1f, 1f), new(0.95f, 0.95f, 0.95f, 1f)];
                for (int i = 0; i < 30; i++)
                    Place([Make(device, GeometricPrimitive.Cube.New(new Vector3(0.4f, 2.5f, 0.1f)), Lit(stripes[i % 2]), new Vector3(-6f + i * 0.4f, 1.25f, -8f))], Vector3.Zero);

                // Water in the pit with fog and a glass block in front of it, a glass cube under it
                Place(Subject(device, 'W'), Vector3.Zero);
                Place([Other(device, "scattering", new Vector3(0.5f, 0.45f, 1.7f)), Other(device, "absorbing", new Vector3(-0.6f, 0.35f, 1.7f)), Other(device, "absorbing", new Vector3(0.3f, -0.45f, -0.3f))], Vector3.Zero);
                // Glass and jelly wedges with other materials around them
                Place([.. Subject(device, 'A'), Other(device, "alpha", new Vector3(0.3f, 0.45f, -1.2f)), Other(device, "glass", new Vector3(-0.5f, 0.45f, 1.7f))], new Vector3(-4.5f, 0, 0));
                Place([.. Subject(device, 'S'), Other(device, "additive", new Vector3(0.3f, 0.45f, 1.7f)), Other(device, "opaque", new Vector3(0.3f, 0.45f, -1.2f))], new Vector3(4.5f, 0, 0));
                // Gas through a wall and the ground
                Place(Subject(device, 'G'), new Vector3(0, 0, -4f));
                // A big gas zone to walk into, on the right
                Place([Scaled(Make(device, GeometricPrimitive.Cube.New(1f), NoisyGas(), new Vector3(10f, 1f, 4f)), new Vector3(5f, 3f, 5f))], Vector3.Zero);
                // A glass pane in that gas
                Place([Make(device, GeometricPrimitive.Cube.New(new Vector3(1.5f, 1.2f, 0.05f)), Volume(Absorbing, 0.6f, 0.05f, MaterialVolumeMedium.Absorbing), new Vector3(10f, 0.9f, 4f))], Vector3.Zero);
                // And a solid cube in it
                Place([Make(device, GeometricPrimitive.Cube.New(0.7f), Lit(Solid), new Vector3(11.3f, 0.35f, 5.2f))], Vector3.Zero);

                // Replays saved poses: one capture per pose, OpacityThickness on then off
                if (Environment.GetEnvironmentVariable("VOLUME_DEMO_SHOTS") is { } shots)
                {
                    var capture = new Capture(game.Services) { DrawOrder = int.MaxValue, Visible = true, Enabled = true };
                    game.GameSystems.Add(capture);
                    var fly = cameraEntity.Get<FlyCamera>();
                    var index = 0;
                    foreach (var line in File.ReadAllLines(shots))
                    {
                        var v = line.Replace(',', '.').Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        float F(int i) => float.Parse(v[i], System.Globalization.CultureInfo.InvariantCulture);
                        cameraEntity.Transform.Position = new Vector3(F(1), F(2), F(3));
                        fly.Yaw = F(5);
                        fly.Pitch = F(7);
                        foreach (var mode in new[] { "on", "off", "segments" })
                        {
                            forward.VolumeThicknessRenderStage = mode == "off" ? null : stage;
                            forward.SeparateVolumeSegments = mode == "segments";
                            for (int frame = 0; frame < 30; frame++)
                                await game.Script.NextFrame();
                            // Several consecutive frames, to catch a flicker
                            var repeat = int.TryParse(Environment.GetEnvironmentVariable("VOLUME_DEMO_REPEAT"), out var r) ? r : 1;
                            for (int shot = 0; shot < repeat; shot++)
                            {
                                capture.Path = Path.Combine(Path.GetDirectoryName(shots), repeat > 1 ? $"pose{index}-{mode}-{shot:D2}.png" : $"pose{index}-{mode}.png");
                                await game.Script.NextFrame();
                            }
                            await game.Script.NextFrame();
                        }
                        index++;
                    }
                    game.Exit();
                    return;
                }

                var enabled = true;
                while (true)
                {
                    if (game.Input.IsKeyPressed(Stride.Input.Keys.T))
                    {
                        enabled = !enabled;
                        forward.VolumeThicknessRenderStage = enabled ? stage : null;
                    }
                    if (game.Input.IsKeyPressed(Stride.Input.Keys.V))
                        forward.SeparateVolumeSegments = !forward.SeparateVolumeSegments;
                    var position = cameraEntity.Transform.Position;
                    var fly = cameraEntity.Get<FlyCamera>();
                    var pose = $"pos {position.X:F2} {position.Y:F2} {position.Z:F2} yaw {fly.Yaw:F2} pitch {fly.Pitch:F2}";
                    if (game.Input.IsKeyPressed(Stride.Input.Keys.P) && Environment.GetEnvironmentVariable("VOLUME_DEMO_POSES") is { } poses)
                        File.AppendAllText(poses, pose + Environment.NewLine);
                    game.Window.Title = $"OpacityThickness {(enabled ? "on" : "off")} (T), SeparateVolumeSegments {forward.SeparateVolumeSegments} (V) | red solid, green alpha, yellow additive, white glass, orange absorbing, purple scattering | {pose} (P saves) | right mouse, ZQSD, A/E";
                    await game.Script.NextFrame();
                }
            });
            RunGameTest(game);
        }

        [Theory]
        // off: OpacityThickness ignored (no stage); on: the default renderer; segments: SeparateVolumeSegments
        [InlineData("off")]
        [InlineData("on")]
        [InlineData("segments")]
        public static void Cases(string mode)
        {
            var root = Environment.GetEnvironmentVariable("VOLUME_CASES_OUT");
            if (root is null)
                return;
            var dir = Path.Combine(root, mode switch { "off" => "nostage", "on" => "stage", _ => "segments" });
            Directory.CreateDirectory(dir);

            var game = new GameTest();
            game.GraphicsDeviceManager.PreferredGraphicsProfile = [GraphicsProfile.Level_11_0];
            game.GraphicsDeviceManager.ShaderProfile = GraphicsProfile.Level_11_0;
            game.GraphicsDeviceManager.PreferredBackBufferWidth = 640;
            game.GraphicsDeviceManager.PreferredBackBufferHeight = 400;

            game.Script.AddTask(async () =>
            {
                game.ScreenShotAutomationEnabled = false;
                var capture = new Capture(game.Services) { DrawOrder = int.MaxValue, Visible = true, Enabled = true };
                game.GameSystems.Add(capture);
                var scene = game.SceneSystem.SceneInstance.RootScene;
                var device = game.GraphicsDevice;
                var camera = new CameraComponent { VerticalFieldOfView = 50f };
                game.SceneSystem.GraphicsCompositor = Stride.Rendering.Compositing.GraphicsCompositorHelper.CreateDefault(false, camera: camera, clearColor: new Color4(0.45f, 0.6f, 0.8f, 1f));
                if (game.SceneSystem.GraphicsCompositor.Game is Stride.Rendering.Compositing.SceneCameraRenderer { Child: Stride.Rendering.Compositing.ForwardRenderer forward })
                {
                    forward.VolumeThicknessRenderStage = mode == "off" ? null : forward.VolumeThicknessRenderStage;
                    forward.SeparateVolumeSegments = mode == "segments";
                }
                var cameraEntity = new Entity("camera") { camera };
                scene.Entities.Add(cameraEntity);

                var sun = new Entity { new LightComponent { Type = new LightDirectional { Color = new ColorRgbProvider(Color.White), Shadow = { Enabled = true, Filter = new LightShadowMapFilterTypePcf { FilterSize = LightShadowMapFilterTypePcfSize.Filter5x5 } } }, Intensity = 2.5f } };
                sun.Transform.Rotation = Quaternion.RotationYawPitchRoll(0.5f, -1f, 0f);
                scene.Entities.Add(sun);
                scene.Entities.Add(new Entity { new LightComponent { Type = new LightAmbient { Color = new ColorRgbProvider(Color.White) }, Intensity = 0.5f } });

                // The room: a floor around a pit, and a striped back wall to see what is filtered
                var sand = Lit(Scenery);
                scene.Entities.Add(Make(device, GeometricPrimitive.Cube.New(new Vector3(8f, 1.5f, 3.6f)), sand, new Vector3(0, -0.75f, 3.1f)));
                scene.Entities.Add(Make(device, GeometricPrimitive.Cube.New(new Vector3(8f, 1.5f, 3f)), sand, new Vector3(0, -0.75f, -2.7f)));
                scene.Entities.Add(Make(device, GeometricPrimitive.Cube.New(new Vector3(2.8f, 1.5f, 2.4f)), sand, new Vector3(-2.8f, -0.75f, 0)));
                scene.Entities.Add(Make(device, GeometricPrimitive.Cube.New(new Vector3(2.8f, 1.5f, 2.4f)), sand, new Vector3(2.8f, -0.75f, 0)));
                Color4[] stripes = [new(0.1f, 0.1f, 0.1f, 1f), new(0.95f, 0.95f, 0.95f, 1f)];
                for (int i = 0; i < 10; i++)
                    scene.Entities.Add(Make(device, GeometricPrimitive.Cube.New(new Vector3(0.4f, 2.5f, 0.1f)), Lit(stripes[i % 2]), new Vector3(-1.8f + i * 0.4f, 1.25f, -3f)));
                // The camera, seen from above
                var marker = Make(device, GeometricPrimitive.Cone.New(0.25f, 0.6f), Lit(new Color4(1f, 1f, 1f, 1f)), ViewFrom, Quaternion.RotationX(-MathF.PI / 2));

                var cases = new List<(string Name, Entity[] Entities, bool Ortho)>();
                foreach (var kind in "ASWG")
                {
                    cases.Add(($"{kind}-alone", Subject(device, kind), false));
                    cases.Add(($"{kind}-alone-orthographic", Subject(device, kind), true));
                    foreach (var other in Others)
                    {
                        // In front: between the camera and the subject. Behind: past it, under the water for W, past the wall for G
                        var behind = kind switch { 'W' => new Vector3(0.3f, -0.45f, -0.3f), 'G' => new Vector3(0.3f, 0.45f, -1f), _ => new Vector3(0.3f, 0.45f, -1.2f) };
                        cases.Add(($"{kind}-{other}-front", [.. Subject(device, kind), Other(device, other, new Vector3(0.3f, 0.45f, 1.7f))], false));
                        cases.Add(($"{kind}-{other}-behind", [.. Subject(device, kind), Other(device, other, behind)], false));
                    }
                }
                // Shadows on the flat ground in front of the pit: an opaque reference, absorbing glass, scattering gas
                Entity[] Shadowed() =>
                [
                    Make(device, GeometricPrimitive.Cube.New(0.5f), Lit(Solid), new Vector3(-1.1f, 0.25f, 1.9f)),
                    Make(device, GeometricPrimitive.Cube.New(0.5f), Volume(Absorbing, 0.6f, 0.5f, MaterialVolumeMedium.Absorbing), new Vector3(0f, 0.25f, 1.9f)),
                    Make(device, GeometricPrimitive.Cube.New(0.5f), Gas(), new Vector3(1.1f, 0.25f, 1.9f)),
                ];
                cases.Add(("X-shadows", Shadowed(), false));

                // Gas whose density is drifting noise
                cases.Add(("G-noise", [Make(device, GeometricPrimitive.Cube.New(new Vector3(3f, 1.6f, 0.25f)), Lit(SceneryDark), new Vector3(0, 0.9f, -0.6f)),
                    Scaled(Make(device, GeometricPrimitive.Cube.New(1f), NoisyGas(), new Vector3(0, 0.4f, -0.4f)), new Vector3(2f, 1.4f, 2f))], false));

                // The camera inside the gas, which goes into the ground and the wall
                cases.Add(("G-camera-inside", [.. Subject(device, 'G'), Make(device, GeometricPrimitive.Cube.New(new Vector3(4f, 4f, 5f)), Gas(), new Vector3(0, 1f, 1.9f))], false));
                cases.Add(("G-camera-inside-alpha-front", [.. Subject(device, 'G'), Make(device, GeometricPrimitive.Cube.New(new Vector3(4f, 4f, 5f)), Gas(), new Vector3(0, 1f, 1.9f)), Other(device, "alpha", new Vector3(0.3f, 0.45f, 1.7f))], false));
                cases.Add(("G-camera-inside-opaque-front", [.. Subject(device, 'G'), Make(device, GeometricPrimitive.Cube.New(new Vector3(4f, 4f, 5f)), Gas(), new Vector3(0, 1f, 1.9f)), Other(device, "opaque", new Vector3(0.3f, 0.45f, 1.7f))], false));

                foreach (var (name, entities, ortho) in cases)
                {
                    foreach (var entity in entities)
                        scene.Entities.Add(entity);

                    // 0: from above, the camera marked
                    scene.Entities.Add(marker);
                    cameraEntity.Transform.Position = new Vector3(0, 9.5f, 0.9f);
                    cameraEntity.Transform.Rotation = Quaternion.RotationX(-MathF.PI / 2);
                    await game.Script.NextFrame();
                    await game.Script.NextFrame();
                    capture.Path = Path.Combine(dir, $"{name}-0-top.png");
                    await game.Script.NextFrame();
                    await game.Script.NextFrame();

                    // 1: from the camera
                    scene.Entities.Remove(marker);
                    camera.Projection = ortho ? CameraProjectionMode.Orthographic : CameraProjectionMode.Perspective;
                    camera.OrthographicSize = 4.5f;
                    cameraEntity.Transform.Position = ViewFrom;
                    cameraEntity.Transform.Rotation = Quaternion.BetweenDirections(-Vector3.UnitZ, Vector3.Normalize(ViewTo - ViewFrom));
                    await game.Script.NextFrame();
                    await game.Script.NextFrame();
                    capture.Path = Path.Combine(dir, $"{name}-1-view.png");
                    await game.Script.NextFrame();
                    await game.Script.NextFrame();

                    foreach (var entity in entities)
                        scene.Entities.Remove(entity);
                }
                game.Exit();
            });
            RunGameTest(game);
        }
    }
}
