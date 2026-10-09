// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Games;
using Stride.Graphics;
using Stride.Graphics.GeometricPrimitives;
using Stride.Graphics.Regression;
using Stride.Extensions;
using Stride.Rendering;
using Stride.Rendering.Colors;
using Stride.Rendering.Lights;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;
using Xunit;

namespace Stride.BepuPhysics.Tests
{
    /// <summary>
    /// Measured bench of OpacityThickness: each tile lines up layers along one camera ray in a known order; wedges show a range of thicknesses.
    /// Writes the render and the exact geometry as JSON to VOLUME_BENCH_OUT; volume_bench.py compares sample points with the analytic colour.
    /// </summary>
    public class VolumeOpacityBench : GameTestBase
    {
        // Layer kinds: A closed absorbing wedge, S closed scattering wedge, W open scattering surface seen from above,
        // U open scattering surface seen from below (camera in the water), P plain alpha pane; O:k is an opaque reference of k's colour.
        private const float BackgroundZ = -12f;
        private const float Thin = 0.05f, Thick = 1f, WaterTilt = 0.6f, HalfSizePerDepth = 0.055f;
        private const float FovY = 40f;
        private const int Width = 1280, Height = 720, Columns = 8, Rows = 6;
        private const int TopFrame = 20, BenchFrame = 40;
        private static readonly float[] SlotDepth = { 5.5f, 7.2f, 8.9f };

        private static readonly string[] Cases =
        {
            "-", "O:S", "O:W", "O:P",
            "A", "S", "W", "P",
            "AA", "AS", "AW", "AP", "SA", "SS", "SW", "SP",
            "WA", "WS", "WP", "PA", "PS", "PW", "PP",
            "ASW", "AWS", "SAW", "SWA", "WAS", "WSA",
            "APW", "AWP", "PAW", "PWA", "WAP", "WPA",
            "ASP", "PSA", "SPA",
        };

        private sealed class Capture(Stride.Core.IServiceRegistry services, (int Frame, string Path)[] shots) : GameSystemBase(services)
        {
            public override void Draw(GameTime gameTime)
            {
                foreach (var (frame, path) in shots)
                {
                    if (frame != gameTime.FrameCount)
                        continue;
                    var game = (Game)Game;
                    using var stream = File.Create(path);
                    game.GraphicsDevice.Presenter.BackBuffer.Save(game.GraphicsContext.CommandList, stream, ImageFileType.Png);
                }
            }
        }

        private record Params(Color3 Colour, float Alpha, float Thickness, MaterialVolumeMedium Medium);

        private static readonly Dictionary<char, Params> Kinds = new()
        {
            ['A'] = new(new Color3(1f, 0.55f, 0.05f), 0.6f, 0.5f, MaterialVolumeMedium.Absorbing),
            ['S'] = new(new Color3(1f, 0.35f, 0.55f), 0.5f, 0.5f, MaterialVolumeMedium.Scattering),
            ['W'] = new(new Color3(0.25f, 0.6f, 0.75f), 0.5f, 2f, MaterialVolumeMedium.Scattering),
            ['P'] = new(new Color3(0.2f, 0.8f, 0.2f), 0.5f, 0f, MaterialVolumeMedium.Absorbing),
        };
        private static readonly Color3 Background = new(0.5f, 0.5f, 0.5f);

        private static Material Make(GraphicsDevice device, Color3 colour, Params p)
        {
            var descriptor = new MaterialDescriptor
            {
                Attributes =
                {
                    Diffuse = new MaterialDiffuseMapFeature(new ComputeColor(new Color4(colour, 1))),
                    DiffuseModel = new MaterialDiffuseLambertModelFeature(),
                },
            };
            if (p != null)
            {
                descriptor.Attributes.Transparency = new MaterialTransparencyBlendFeature
                {
                    Alpha = new ComputeFloat(p.Alpha),
                    // Absorbing filters through the tint; the others blend their lit colour, untinted
                    Tint = new ComputeColor(p.Medium == MaterialVolumeMedium.Absorbing && p.Thickness > 0 ? new Color4(p.Colour, 1) : Color4.White),
                    OpacityThickness = p.Thickness,
                    Medium = p.Medium,
                };
            }
            return Material.New(device, descriptor);
        }

        // Convex faces wound clockwise seen from outside (Stride's front faces), normals flat
        private static GeometricMeshData<VertexPositionNormalTexture> Mesh(Vector3 inside, params Vector3[][] faces)
        {
            var vertices = new List<VertexPositionNormalTexture>();
            var indices = new List<int>();
            foreach (var face in faces)
            {
                var centre = face.Aggregate(Vector3.Zero, (a, b) => a + b) / face.Length;
                var normal = Vector3.Normalize(Vector3.Cross(face[1] - face[0], face[2] - face[0]));
                if (Vector3.Dot(normal, centre - inside) < 0)
                    normal = -normal;
                var clockwise = Vector3.Dot(Vector3.Cross(face[1] - face[0], face[2] - face[0]), normal) < 0;
                int start = vertices.Count;
                foreach (var v in face)
                    vertices.Add(new VertexPositionNormalTexture(v, normal, Vector2.Zero));
                for (int i = 1; i + 1 < face.Length; i++)
                {
                    indices.Add(start);
                    indices.Add(start + (clockwise ? i : i + 1));
                    indices.Add(start + (clockwise ? i + 1 : i));
                }
            }
            return new GeometricMeshData<VertexPositionNormalTexture>(vertices.ToArray(), indices.ToArray(), false);
        }

        // Front face at local z = 0, back face from z = -thin at x = -h to z = -thick at x = +h
        private static GeometricMeshData<VertexPositionNormalTexture> Wedge(float h, float thin, float thick)
        {
            Vector3 f0 = new(-h, -h, 0), f1 = new(h, -h, 0), f2 = new(h, h, 0), f3 = new(-h, h, 0);
            Vector3 b0 = new(-h, -h, -thin), b1 = new(h, -h, -thick), b2 = new(h, h, -thick), b3 = new(-h, h, -thin);
            var inside = new Vector3(0, 0, -(thin + thick) / 4);
            return Mesh(inside, [f0, f1, f2, f3], [b0, b1, b2, b3], [f0, f3, b3, b0], [f1, f2, b2, b1], [f3, f2, b2, b3], [f0, f1, b1, b0]);
        }

        // A quad from z = 0 at x = -h to z = -tilt at x = +h, facing +z (towards the camera) or -z
        private static GeometricMeshData<VertexPositionNormalTexture> Surface(float h, float tilt, bool facingCamera)
        {
            Vector3 v0 = new(-h, -h, 0), v1 = new(h, -h, -tilt), v2 = new(h, h, -tilt), v3 = new(-h, h, 0);
            return Mesh(new Vector3(0, 0, facingCamera ? -1 : 1), [v0, v1, v2, v3]);
        }

        // A closed slab whose front goes from z = 0 at x = -h to z = -tilt at x = +h, and whose back is at z = -depth
        private static GeometricMeshData<VertexPositionNormalTexture> WaterSlab(float h, float tilt, float depth)
        {
            Vector3 f0 = new(-h, -h, 0), f1 = new(h, -h, -tilt), f2 = new(h, h, -tilt), f3 = new(-h, h, 0);
            // Flared so that its sides never cut a ray before the background does
            var w = h * 1.6f;
            Vector3 b0 = new(-w, -w, -depth), b1 = new(w, -w, -depth), b2 = new(w, w, -depth), b3 = new(-w, w, -depth);
            return Mesh(new Vector3(0, 0, -depth / 2), [f0, f1, f2, f3], [b0, b1, b2, b3], [f0, f3, b3, b0], [f1, f2, b2, b1], [f3, f2, b2, b3], [f0, f1, b1, b0]);
        }

        private static Entity Add(Scene scene, GraphicsDevice device, GeometricMeshData<VertexPositionNormalTexture> data, Material material, Vector3 position, Quaternion rotation)
        {
            var model = new Model { material };
            // Bounds from the vertices: sorting, culling and shadows use them
            var primitive = new GeometricPrimitive(device, data);
            var bounds = BoundingBox.FromPoints(System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(data.Vertices, vertex => vertex.Position)));
            model.Add(new Mesh { Draw = primitive.ToMeshDraw(), MaterialIndex = 0, BoundingBox = bounds });
            var entity = new Entity { new ModelComponent { Model = model } };
            entity.Transform.Position = position;
            entity.Transform.Rotation = rotation;
            scene.Entities.Add(entity);
            return entity;
        }

        private static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);
        private static string V(Vector3 v) => $"[{F(v.X)},{F(v.Y)},{F(v.Z)}]";

        [Theory]
        [InlineData("stage", true, "")]
        [InlineData("segments", true, "")]
        [InlineData("nostage", false, "")]
        [InlineData("insideA", true, "A")]
        [InlineData("insideS", true, "S")]
        public static void Bench(string variant, bool withStage, string inside)
        {
            var root = Environment.GetEnvironmentVariable("VOLUME_BENCH_OUT");
            if (root is null)
                return;
            Directory.CreateDirectory(root);

            var game = new GameTest();
            game.GraphicsDeviceManager.PreferredGraphicsProfile = [GraphicsProfile.Level_11_0];
            game.GraphicsDeviceManager.ShaderProfile = GraphicsProfile.Level_11_0;
            game.GraphicsDeviceManager.PreferredBackBufferWidth = Width;
            game.GraphicsDeviceManager.PreferredBackBufferHeight = Height;

            game.Script.AddTask(async () =>
            {
                game.ScreenShotAutomationEnabled = false;
                game.GameSystems.Add(new Capture(game.Services, [(TopFrame, Path.Combine(root, $"{variant}-0-top.png")), (BenchFrame, Path.Combine(root, $"{variant}.png"))]) { DrawOrder = int.MaxValue, Visible = true, Enabled = true });
                var scene = game.SceneSystem.SceneInstance.RootScene;
                var device = game.GraphicsDevice;
                var camera = new CameraComponent { VerticalFieldOfView = FovY, NearClipPlane = 0.1f, FarClipPlane = 100f, UseCustomAspectRatio = true, AspectRatio = (float)Width / Height };
                game.SceneSystem.GraphicsCompositor = Stride.Rendering.Compositing.GraphicsCompositorHelper.CreateDefault(false, camera: camera, clearColor: new Color4(0, 0, 0, 1));
                if (game.SceneSystem.GraphicsCompositor.Game is Stride.Rendering.Compositing.SceneCameraRenderer { Child: Stride.Rendering.Compositing.ForwardRenderer forward })
                {
                    forward.VolumeThicknessRenderStage = withStage ? forward.VolumeThicknessRenderStage : null;
                    forward.SeparateVolumeSegments = variant == "segments";
                }
                var cameraEntity = new Entity("camera") { camera };
                scene.Entities.Add(cameraEntity);
                scene.Entities.Add(new Entity { new LightComponent { Type = new LightAmbient { Color = new ColorRgbProvider(Color.White) }, Intensity = 1f } });

                var materials = Kinds.ToDictionary(k => k.Key, k => Make(device, k.Value.Colour, k.Value));
                var opaque = Kinds.ToDictionary(k => k.Key, k => Make(device, k.Value.Colour, null));
                Add(scene, device, GeometricPrimitive.Plane.New(100f, 100f, normalDirection: NormalDirection.UpZ), Make(device, Background, null), new Vector3(0, 0, BackgroundZ), Quaternion.Identity);

                // A closed volume around the camera, its back face at z = -4
                if (inside != "")
                    Add(scene, device, GeometricPrimitive.Cube.New(new Vector3(60f, 60f, 6f)), materials[inside[0]], new Vector3(0, 0, -1f), Quaternion.Identity);

                var tanY = MathF.Tan(MathUtil.DegreesToRadians(FovY) / 2);
                var tanX = tanY * Width / Height;
                var json = new StringBuilder();
                json.Append($"{{\"width\":{Width},\"height\":{Height},\"tan\":[{F(tanX)},{F(tanY)}],\"background_z\":{F(BackgroundZ)},\"stage\":{(withStage ? "true" : "false")},\"inside\":\"{inside}\",\"inside_back_z\":-4");
                json.Append($",\"background\":{V((Vector3)Background)},\"kinds\":{{");
                json.Append(string.Join(",", Kinds.Select(k => $"\"{k.Key}\":{{\"colour\":{V((Vector3)k.Value.Colour)},\"alpha\":{F(k.Value.Alpha)},\"thickness\":{F(k.Value.Thickness)}}}")));
                json.Append("},\"tiles\":[");

                for (int i = 0; i < Cases.Length; i++)
                {
                    int column = i % Columns, row = i / Columns;
                    var ndc = new Vector2((column + 0.5f) / Columns * 2 - 1, 1 - (row + 0.5f) / Rows * 2);
                    var direction = Vector3.Normalize(new Vector3(ndc.X * tanX, ndc.Y * tanY, -1f));
                    // Each tile faces its ray: local -z along it
                    var rotation = Quaternion.BetweenDirections(-Vector3.UnitZ, direction);
                    var matrix = Matrix.RotationQuaternion(rotation);
                    var layers = new List<string>();
                    var name = Cases[i];
                    var reference = name.StartsWith("O:");
                    var kinds = reference ? name.Substring(2) : name == "-" ? "" : name;
                    for (int slot = 0; slot < kinds.Length; slot++)
                    {
                        var kind = kinds[slot];
                        var depth = SlotDepth[slot];
                        var h = HalfSizePerDepth * depth;
                        var origin = direction * depth;
                        string shape;
                        if (reference)
                        {
                            Add(scene, device, Surface(h, 0, true), opaque[kind], origin, rotation);
                            shape = $"\"kind\":\"O\",\"of\":\"{kind}\",\"tilt\":0";
                        }
                        else if (kind is 'A' or 'S')
                        {
                            Add(scene, device, Wedge(h, Thin, Thick), materials[kind], origin, rotation);
                            shape = $"\"kind\":\"{kind}\",\"thin\":{F(Thin)},\"thick\":{F(Thick)}";
                        }
                        else
                        {
                            var tilt = kind == 'P' ? 0 : WaterTilt;
                            // Water: a slab from a tilted surface to far behind the background, which hides its back
                            Add(scene, device, kind == 'P' ? Surface(h, tilt, true) : WaterSlab(h, tilt, -BackgroundZ / -direction.Z - depth + 1.5f), materials[kind], origin, rotation);
                            shape = $"\"kind\":\"{kind}\",\"tilt\":{F(tilt)}";
                        }
                        layers.Add($"{{{shape},\"origin\":{V(origin)},\"h\":{F(h)},\"x_axis\":{V(new Vector3(matrix.M11, matrix.M12, matrix.M13))},\"y_axis\":{V(new Vector3(matrix.M21, matrix.M22, matrix.M23))},\"z_axis\":{V(new Vector3(matrix.M31, matrix.M32, matrix.M33))}}}");
                    }
                    if (i > 0)
                        json.Append(',');
                    json.Append($"{{\"name\":\"{name}\",\"ndc\":[{F(ndc.X)},{F(ndc.Y)}],\"layers\":[{string.Join(",", layers)}]}}");
                }
                json.Append("]}");
                File.WriteAllText(Path.Combine(root, $"{variant}.json"), json.ToString());

                // Top view first, with a marker where the bench camera stands
                var marker = Add(scene, device, GeometricPrimitive.Sphere.New(0.4f), Make(device, new Color3(1, 1, 1), null), Vector3.Zero, Quaternion.Identity);
                var topLight = new Entity { new LightComponent { Type = new LightDirectional { Color = new ColorRgbProvider(Color.White) }, Intensity = 2f } };
                topLight.Transform.Rotation = Quaternion.RotationYawPitchRoll(0.5f, -1.1f, 0f);
                scene.Entities.Add(topLight);
                cameraEntity.Transform.Position = new Vector3(0, 22f, -6.5f);
                cameraEntity.Transform.Rotation = Quaternion.RotationX(-MathF.PI / 2);
                while (game.UpdateTime.FrameCount < TopFrame + 2)
                    await game.Script.NextFrame();

                scene.Entities.Remove(marker);
                scene.Entities.Remove(topLight);
                cameraEntity.Transform.Position = Vector3.Zero;
                cameraEntity.Transform.Rotation = Quaternion.Identity;
                while (game.UpdateTime.FrameCount < BenchFrame + 2)
                    await game.Script.NextFrame();
                game.Exit();
            });
            RunGameTest(game);
        }
    }
}
