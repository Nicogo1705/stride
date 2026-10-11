// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stride.BepuPhysics.Debug;
using Stride.BepuPhysics.Debug.Effects;
using Stride.BepuPhysics.Debug.Effects.RenderFeatures;
using Stride.BepuPhysics.Definitions;
using Stride.BepuPhysics.Definitions.Colliders;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Graphics.Regression;
using Stride.Rendering;
using Stride.Rendering.Compositing;
using Stride.Rendering.ProceduralModels;
using Xunit;

namespace Stride.BepuPhysics.Tests
{
    public class DebugWireframeTests : GameTestBase
    {
        [Fact]
        public static void ColliderLinesShowOverTheirOwnModel()
        {
            // The debug sphere's facets sit inside the curved surface: the model used to hide most of its lines
            var (withLines, withoutLines) = Render(colliderRadius: 0.5f, modelRadius: 0.5f);
            var changed = 0;
            var total = 0;
            ForEachSpherePixel(withLines, (x, y, color) =>
            {
                total++;
                if (Differs(color, withoutLines[x, y]))
                    changed++;
            });
            Assert.True(changed > total / 40, $"{changed} of {total} pixels of the sphere show a collider line");
        }

        [Fact]
        public static void ColliderInsideItsModelShowsAtHalfOpacity()
        {
            // The model hides every line of the smaller collider: they are drawn, blended half and half with the model
            var (inside, insideWithout) = Render(colliderRadius: 0.4f, modelRadius: 0.5f);
            var (fitting, fittingWithout) = Render(colliderRadius: 0.5f, modelRadius: 0.5f);
            var lineColor = MostFrequentChangedColor(fitting, fittingWithout).ToColor4().ToLinear();

            int changed = 0, halfBlended = 0;
            ForEachSpherePixel(inside, (x, y, color) =>
            {
                var model = insideWithout[x, y];
                if (!Differs(color, model))
                    return;
                changed++;
                // Blending happens in linear space
                var expected = (Color)((lineColor + model.ToColor4().ToLinear()) * 0.5f).ToSRgb();
                if (Math.Abs(color.R - expected.R) <= 4 && Math.Abs(color.G - expected.G) <= 4 && Math.Abs(color.B - expected.B) <= 4)
                    halfBlended++;
            });
            Assert.True(changed > 500, $"{changed} pixels show a line of a 0.4 collider in a 0.5 model");
            Assert.True(halfBlended > changed * 9 / 10, $"{halfBlended} of the {changed} line pixels blend half and half with the model");
        }

        [Fact]
        public static void HiddenLinesCanBeTurnedOff()
        {
            var (inside, insideWithout) = Render(colliderRadius: 0.4f, modelRadius: 0.5f, hiddenLineOpacity: 0f);
            var changed = 0;
            ForEachSpherePixel(inside, (x, y, color) =>
            {
                if (Differs(color, insideWithout[x, y]))
                    changed++;
            });
            Assert.True(changed == 0, $"{changed} pixels show a line of a 0.4 collider in a 0.5 model with hidden lines off");
        }

        private static bool Differs(Color a, Color b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) > 30;

        /// <summary> The color of the lines: the most frequent one among the sphere pixels that differ between both frames </summary>
        private static Color MostFrequentChangedColor(Frame withLines, Frame withoutLines)
        {
            var counts = new Dictionary<Color, int>();
            ForEachSpherePixel(withLines, (x, y, color) =>
            {
                if (Differs(color, withoutLines[x, y]))
                    counts[color] = counts.GetValueOrDefault(color) + 1;
            });
            return counts.MaxBy(pair => pair.Value).Key;
        }

        // The pixels inside the sphere's outline, which the camera centers
        private static void ForEachSpherePixel(Frame image, Action<int, int, Color> action)
        {
            int cx = image.Width / 2, cy = image.Height / 2, radius = image.Height / 4;
            for (int y = cy - radius; y < cy + radius; y++)
            {
                for (int x = cx - radius; x < cx + radius; x++)
                {
                    if ((x - cx) * (x - cx) + (y - cy) * (y - cy) < radius * radius)
                        action(x, y, image[x, y]);
                }
            }
        }

        [Fact]
        public static void FiltersHideTheirKindOfCollidable()
        {
            var game = new GameTest();
            game.GraphicsDeviceManager.PreferredGraphicsProfile = [GraphicsProfile.Level_11_0];
            game.GraphicsDeviceManager.ShaderProfile = GraphicsProfile.Level_11_0;
            game.Script.AddTask(async () =>
            {
                game.ScreenShotAutomationEnabled = false;
                var compositor = GraphicsCompositorHelper.CreateDefault(false, graphicsProfile: GraphicsProfile.Level_11_0);
                game.SceneSystem.GraphicsCompositor = compositor;
                var scene = game.SceneSystem.SceneInstance.RootScene;
                var debug = new DebugRenderComponent { Visible = true };
                scene.Entities.Add(new Entity { debug, new CameraComponent { Slot = compositor.Cameras[0].ToSlotId() } });
                var statics = Add(scene, new StaticComponent { Collider = Sphere() }, -4f);
                var dynamic = Add(scene, new BodyComponent { Collider = Sphere() }, -1.5f);
                var kinematic = Add(scene, new BodyComponent { Collider = Sphere(), Kinematic = true }, 1.5f);
                var character = Add(scene, new CharacterComponent { Collider = Sphere() }, 4f);
                dynamic.Simulation!.PoseGravity = Vector3.Zero;
                for (int i = 0; i < 3; i++) await game.Script.NextFrame();

                var visibility = game.SceneSystem.SceneInstance.VisibilityGroups.First();
                async Task<CollidableComponent[]> Shown()
                {
                    await game.Script.NextFrame();
                    var wireframes = visibility.RenderObjects.OfType<WireFrameRenderObject>().Where(w => w.Enabled).ToList();
                    return new CollidableComponent[] { statics, dynamic, kinematic, character }
                        .Where(c => wireframes.Any(w => Vector3.Distance(w.WorldMatrix.TranslationVector, c.Entity.Transform.Position) < 0.5f)).ToArray();
                }

                Assert.Equal([statics, dynamic, kinematic, character], await Shown());
                debug.ShowStatics = false;
                Assert.Equal([dynamic, kinematic, character], await Shown());
                debug.ShowStatics = true;
                debug.ShowDynamicBodies = false;
                Assert.Equal([statics, kinematic, character], await Shown());
                debug.ShowDynamicBodies = true;
                debug.ShowKinematicBodies = false;
                Assert.Equal([statics, dynamic, character], await Shown());
                debug.ShowKinematicBodies = true;
                debug.ShowCharacters = false;
                Assert.Equal([statics, dynamic, kinematic], await Shown());
                debug.ShowCharacters = true;

                // Sleep is read every frame: a body falling asleep or woken up follows the filter
                debug.ShowSleepingBodies = false;
                Assert.Equal([statics, dynamic, kinematic, character], await Shown());
                dynamic.Awake = false;
                Assert.Equal([statics, kinematic, character], await Shown());
                dynamic.Awake = true;
                Assert.Equal([statics, dynamic, kinematic, character], await Shown());
                game.Exit();
            });
            RunGameTest(game);
        }

        [Fact]
        public static void SkeletonBodiesFilterByTheirMode()
        {
            var game = new GameTest();
            game.GraphicsDeviceManager.PreferredGraphicsProfile = [GraphicsProfile.Level_11_0];
            game.GraphicsDeviceManager.ShaderProfile = GraphicsProfile.Level_11_0;
            game.Script.AddTask(async () =>
            {
                game.ScreenShotAutomationEnabled = false;
                var compositor = GraphicsCompositorHelper.CreateDefault(false, graphicsProfile: GraphicsProfile.Level_11_0);
                game.SceneSystem.GraphicsCompositor = compositor;
                var scene = game.SceneSystem.SceneInstance.RootScene;
                var debug = new DebugRenderComponent { Visible = true, ShowKinematicBodies = false };
                scene.Entities.Add(new Entity { debug, new CameraComponent { Slot = compositor.Cameras[0].ToSlotId() } });
                var body = new SkeletonBodyComponent { Mode = SkeletonBodyMode.Animated };
                ((BoneCollider)body.Collider).GeneratedRadius = 0.05f;
                var skeleton = new Skeleton
                {
                    Nodes =
                    [
                        new ModelNodeDefinition { Name = "Root", ParentIndex = -1, Transform = { Rotation = Quaternion.Identity, Scale = Vector3.One } },
                        new ModelNodeDefinition { Name = "Hips", ParentIndex = 0, Transform = { Position = new Vector3(0, 1, 0), Rotation = Quaternion.Identity, Scale = Vector3.One } },
                        new ModelNodeDefinition { Name = "Leg", ParentIndex = 1, Transform = { Position = new Vector3(0, -0.8f, 0), Rotation = Quaternion.Identity, Scale = Vector3.One } },
                    ],
                };
                scene.Entities.Add(new Entity { new ModelComponent { Model = new Model { Skeleton = skeleton } }, body });
                for (int i = 0; i < 3; i++) await game.Script.NextFrame();

                var visibility = game.SceneSystem.SceneInstance.VisibilityGroups.First();
                async Task<bool> Shown()
                {
                    await game.Script.NextFrame();
                    return visibility.RenderObjects.OfType<WireFrameRenderObject>().Any(w => w.Enabled);
                }

                // Animated bones are kinematic, a ragdoll is simulated
                Assert.False(await Shown());
                body.Mode = SkeletonBodyMode.Ragdoll;
                Assert.True(await Shown());
                debug.ShowDynamicBodies = false;
                Assert.False(await Shown());
                game.Exit();
            });
            RunGameTest(game);
        }

        private static CompoundCollider Sphere() => new() { Colliders = { new SphereCollider { Radius = 0.5f } } };

        private static T Add<T>(Scene scene, T collidable, float x) where T : CollidableComponent
        {
            var entity = new Entity { collidable };
            entity.Transform.Position = new Vector3(x, 0, 0);
            scene.Entities.Add(entity);
            return collidable;
        }

        /// <summary> A sphere model and a sphere collider at the origin, rendered with and without the collider's wireframe </summary>
        // 0.5 m sphere seen from 1.5 m: it spans about half the image height
        private static (Frame WithLines, Frame WithoutLines) Render(float colliderRadius, float modelRadius, float hiddenLineOpacity = 0.5f)
        {
            Frame? withLines = null, withoutLines = null;
            var game = new GameTest();
            game.GraphicsDeviceManager.PreferredGraphicsProfile = [GraphicsProfile.Level_11_0];
            game.GraphicsDeviceManager.ShaderProfile = GraphicsProfile.Level_11_0;
            game.Script.AddTask(async () =>
            {
                game.ScreenShotAutomationEnabled = false;
                var compositor = GraphicsCompositorHelper.CreateDefault(false, clearColor: Color.Gray, graphicsProfile: GraphicsProfile.Level_11_0);
                // The debug view uses the wireframe feature the compositor already has
                compositor.RenderFeatures.Add(new SinglePassWireframeRenderFeature { HiddenLineOpacity = hiddenLineOpacity });
                game.SceneSystem.GraphicsCompositor = compositor;

                var debug = new DebugRenderComponent { Visible = true };
                var collidable = new Entity
                {
                    new ModelComponent(new SphereProceduralModel { Radius = modelRadius }.Generate(game.Services)),
                    new StaticComponent { Collider = new CompoundCollider { Colliders = { new SphereCollider { Radius = colliderRadius } } } },
                    debug,
                };
                var camera = new Entity { new CameraComponent { Slot = compositor.Cameras[0].ToSlotId() } };
                camera.Transform.Position = new Vector3(0, 0, 1.5f);

                var scene = game.SceneSystem.SceneInstance.RootScene;
                scene.Entities.Add(camera);
                scene.Entities.Add(collidable);

                for (int i = 0; i < 10; i++) await game.Script.NextFrame();
                withLines = Capture(game);
                debug.Visible = false;
                for (int i = 0; i < 10; i++) await game.Script.NextFrame();
                withoutLines = Capture(game);
                game.Exit();
            });
            RunGameTest(game);
            return (withLines!, withoutLines!);
        }

        private static Frame Capture(Game game)
        {
            using var image = game.GraphicsDevice.Presenter.BackBuffer.GetDataAsImage(game.GraphicsContext.CommandList);
            var pixels = image.PixelBuffer[0];
            var bgra = pixels.Format is PixelFormat.B8G8R8A8_UNorm or PixelFormat.B8G8R8A8_UNorm_SRgb;
            var colors = pixels.GetPixels<Color>();
            if (bgra)
            {
                for (int i = 0; i < colors.Length; i++)
                    colors[i] = new Color(colors[i].B, colors[i].G, colors[i].R, colors[i].A);
            }
            return new Frame(pixels.Width, pixels.Height, colors);
        }

        private sealed record Frame(int Width, int Height, Color[] Pixels)
        {
            public Color this[int x, int y] => Pixels[y * Width + x];
        }
    }
}
