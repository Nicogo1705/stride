// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using BepuPhysics;
using Stride.BepuPhysics.Debug;
using Stride.BepuPhysics.Definitions;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Extensions;
using Stride.Harness.Demo;
using Stride.Input;
using Stride.Rendering.Compositing;
using Stride.Rendering.Images;
using Stride.Rendering.Lights;
using Stride.Rendering.ProceduralModels;

namespace Stride.BepuPhysics.Soft.Tests.VideoDemo;

/// <summary>
/// The Bepu demo dressed for a video: sky, soft shadows, a palette, labels anchored in the scene, an overlay that hides,
/// an orbiting camera, and a gravity gun that also takes soft bodies and the bones of ragdolls.
/// </summary>
internal sealed class VideoDemoGame : BepuDemoGame
{
    private static readonly FieldInfo SkeletonBodies = typeof(SkeletonBodyComponent).GetField("_bodies", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private Entity? _sky;
    private int _overlayState;
    private bool _orbit;
    private float _orbitAngle, _orbitRadius, _orbitHeight;
    private readonly List<double> _frameTimes = new();
    private long _lastFrame;

    public VideoDemoGame(IEnumerable<DemoScene> scenes, string buildLabel, string captureFolder) : base(scenes, buildLabel, captureFolder)
    {
        KeyHelpVisible = false;
    }

    /// <summary> Frame times in milliseconds since the scene was loaded </summary>
    public IReadOnlyList<double> FrameTimes => _frameTimes;

    public void ClearFrameTimes() => _frameTimes.Clear();

    /// <summary> Hides or shows the whole overlay, as the third press of H does </summary>
    public void SetOverlay(bool visible)
    {
        _overlayState = visible ? 0 : 2;
        OverlayVisible = visible;
        KeyHelpVisible = false;
    }

    /// <summary> The handle of one body of a skeleton, read by reflection: the component has no API to push a single bone </summary>
    public static BodyReference BoneBody(SkeletonBodyComponent bones, int index) => bones.Simulation!.Simulation.Bodies[((BodyHandle[])SkeletonBodies.GetValue(bones)!)[index]];

    protected override IReadOnlyList<string> DebugViews { get; } = new[] { "Auto (selon la scène)", "Colliders (wireframe Bepu)", "Colliders + faces arrière", "Off" };

    protected override void OnDebugViewChanged(int view)
    {
        var scene = CurrentScene as VideoScene;
        ShowRigidWireframe(view is 1 or 2 || (view == 0 && scene?.WireframeByDefault == true));
        if (DebugRenderer is { } renderer)
            renderer.ShowBackFaces = view == 2;
    }

    private DebugRenderComponent? DebugRenderer => SceneSystem.SceneInstance.RootScene.Entities.Select(e => e.Get<DebugRenderComponent>()).FirstOrDefault(c => c is not null);

    protected override void OnViewReady()
    {
        base.OnViewReady();
        Look.Reset(GraphicsDevice);
        var root = SceneSystem.SceneInstance.RootScene.Entities;

        // Sun: a warm key light with soft, stable cascades close to the camera; a cool fill from the other side, no shadow
        foreach (var light in root.Select(e => e.Get<LightComponent>()).Where(l => l is not null))
        {
            switch (light!.Type)
            {
                case LightDirectional directional:
                    light.Intensity = 3.2f;
                    directional.Color = new Stride.Rendering.Colors.ColorRgbProvider(new Color3(1f, 0.95f, 0.86f));
                    if (directional.Shadow is LightDirectionalShadowMap shadow)
                    {
                        shadow.Size = LightShadowMapSize.XLarge;
                        shadow.CascadeCount = LightShadowMapCascadeCount.FourCascades;
                        shadow.Filter = new LightShadowMapFilterTypePcf { FilterSize = LightShadowMapFilterTypePcfSize.Filter7x7 };
                        shadow.StabilizationMode = LightShadowMapStabilizationMode.ViewSnapping;
                        shadow.DepthRange.IsAutomatic = false;
                        shadow.DepthRange.ManualMinDistance = 0.1f;
                        shadow.DepthRange.ManualMaxDistance = 40f;
                    }
                    light.Entity.Transform.Rotation = Quaternion.RotationYawPitchRoll(0.75f, -0.85f, 0f);
                    break;
                case LightAmbient ambient:
                    ambient.Color = new Stride.Rendering.Colors.ColorRgbProvider(new Color3(0.78f, 0.85f, 1f));
                    light.Intensity = 0.55f;
                    break;
            }
        }
        var fill = new Entity("fill") { new LightComponent { Type = new LightDirectional { Color = new Stride.Rendering.Colors.ColorRgbProvider(new Color3(0.65f, 0.75f, 1f)) }, Intensity = 0.6f } };
        fill.Transform.Rotation = Quaternion.RotationYawPitchRoll(0.75f + MathUtil.Pi, -0.45f, 0f);
        root.Add(fill);

        // The sphere's triangles turned inside out, so it is seen from the camera at its center
        var skyData = Stride.Graphics.GeometricPrimitives.GeometricPrimitive.Sphere.New(400f, 48, toLeftHanded: true);
        var skyBounds = new BoundingBox(new Vector3(-400f), new Vector3(400f));
        var skyModel = new Stride.Rendering.Model { Look.Sky(GraphicsDevice) };
        skyModel.Add(new Stride.Rendering.Mesh { Draw = new Stride.Graphics.GeometricPrimitives.GeometricPrimitive(GraphicsDevice, skyData).ToMeshDraw(), MaterialIndex = 0, BoundingBox = skyBounds });
        _sky = new Entity("ciel") { new ModelComponent(skyModel) { IsShadowCaster = false } };
        root.Add(_sky);

        // A touch of bloom on top of the tone mapping of the default compositor
        if (FindForwardRenderer(SceneSystem.GraphicsCompositor.Game) is { PostEffects: PostProcessingEffects effects })
        {
            // FXAA is left off: enabled from code over this compositor it turns the image black
            effects.BrightFilter.Enabled = true;
            effects.BrightFilter.Threshold = 1.6f;
            effects.Bloom.Enabled = true;
            effects.Bloom.Amount = 0.08f;
            effects.Bloom.Radius = 12f;
        }
    }

    private static ForwardRenderer? FindForwardRenderer(ISceneRenderer? renderer) => renderer switch
    {
        ForwardRenderer forward => forward,
        SceneCameraRenderer camera => FindForwardRenderer(camera.Child),
        SceneRendererCollection collection => collection.Children.Select(FindForwardRenderer).FirstOrDefault(f => f is not null),
        _ => null,
    };

    protected override void OnSceneLoaded(DemoScene scene)
    {
        base.OnSceneLoaded(scene);
        _frameTimes.Clear();
        _orbit = scene is VideoScene { AutoOrbit: true };
        if (_orbit)
            StartOrbit();
    }

    private void StartOrbit()
    {
        var target = CurrentScene!.Target;
        var offset = CameraEntity.Transform.Position - target;
        _orbitAngle = MathF.Atan2(offset.X, offset.Z);
        _orbitRadius = new Vector2(offset.X, offset.Z).Length();
        _orbitHeight = offset.Y;
    }

    protected override void UpdateSubject(float seconds)
    {
        if (Input.IsKeyPressed(Keys.H))
        {
            // Compact overlay, then every key, then nothing at all for clean shots
            _overlayState = (_overlayState + 1) % 3;
            OverlayVisible = _overlayState != 2;
            KeyHelpVisible = _overlayState == 1;
        }
        if (Input.IsKeyPressed(Keys.R) && CurrentScene is { } current)
        {
            LoadScene(current);
            Flash("Scène remise à zéro");
            return;
        }
        if (Input.IsKeyPressed(Keys.C))
        {
            _orbit = !_orbit;
            if (_orbit)
                StartOrbit();
            Flash("Caméra orbitale : " + (_orbit ? "on" : "off"));
        }
        if (_orbit && CurrentScene is { } scene)
        {
            _orbitAngle += seconds * 0.12f;
            var eye = scene.Target + new Vector3(MathF.Sin(_orbitAngle) * _orbitRadius, _orbitHeight, MathF.Cos(_orbitAngle) * _orbitRadius);
            SetCamera(eye, scene.Target);
        }

        base.UpdateSubject(seconds);
        if (CurrentScene is VideoScene video && Simulation is not null)
            video.UpdateInput(this);
    }

    protected override void BeforeDraw()
    {
        base.BeforeDraw();
        if (_sky is not null)
            _sky.Transform.Position = CameraEntity.Transform.Position;
        var now = Stopwatch.GetTimestamp();
        if (_lastFrame != 0)
            _frameTimes.Add(Stopwatch.GetElapsedTime(_lastFrame, now).TotalMilliseconds);
        _lastFrame = now;
    }

    protected override IEnumerable<(string Theme, string Keys)> SubjectKeys()
    {
        yield return ("Vidéo", "H : aide complète / overlay caché  |  C : caméra orbitale  |  R : remettre la scène à zéro  |  V : wireframes de debug Bepu");
        yield return ("Physique", "clic gauche : attraper (rigides, corps mous, os)  |  molette : distance  |  F : lancer / balle  |  G : pluie d'objets  |  Espace : pause");
        if (CurrentScene is VideoScene { SceneKeys: { } keys })
            yield return ("Scène", keys);
    }

    protected override IEnumerable<string> SubjectStatus()
    {
        if (Simulation is { Enabled: false })
            yield return "EN PAUSE (Espace)";
        if (CurrentScene is VideoScene scene)
        {
            foreach (var line in scene.Status())
                yield return line;
        }
    }

    protected override void DrawCustomOverlay()
    {
        if (!OverlayVisible || CurrentScene is not VideoScene scene)
            return;
        var camera = CameraEntity.Get<CameraComponent>();
        if (camera is null)
            return;
        var size = ScreenSize;
        foreach (var (world, text) in scene.Labels())
        {
            var screen = Vector3.Project(world, 0, 0, size.X, size.Y, 0, 1, camera.ViewProjectionMatrix);
            if (screen.Z is < 0f or > 1f || screen.X < 0 || screen.Y < 0 || screen.X > size.X || screen.Y > size.Y)
                continue;
            var measure = MeasureText(text, 18, true);
            var corner = new Vector2(screen.X - measure.X / 2f, screen.Y - measure.Y / 2f);
            FillRect(new RectangleF(corner.X - 8, corner.Y - 4, measure.X + 16, measure.Y + 8), new Color(20, 22, 34, 170));
            DrawText(text, corner, Color.White, 18, true);
        }
        scene.DrawOverlay(this);
    }

    // Helpers the scenes draw their own panels with
    public Int2 Screen => ScreenSize;

    public void Panel(RectangleF area, Color color) => FillRect(area, color);

    public void Write(string text, Vector2 position, Color color, int size = 17, bool bold = false) => DrawText(text, position, color, size, bold);

    public Vector2 Measure(string text, int size = 17, bool bold = false) => MeasureText(text, size, bold);

    protected override string? DescribeGrabbable(CollidableComponent collidable) => collidable switch
    {
        ClothComponent => "tissu (attrapable)",
        SoftBodyComponent => "corps mou (attrapable)",
        SkeletonBodyComponent { Mode: SkeletonBodyMode.Animated } => "os animé (attrapable : le perso passe en Ragdoll)",
        SkeletonBodyComponent bones => $"os en {bones.Mode} (attrapable)",
        _ => base.DescribeGrabbable(collidable),
    };

    protected override IHeld? Grab(HitInfo hit)
    {
        switch (hit.Collidable)
        {
            case SkeletonBodyComponent bones when hit.ChildIndex >= 0 && hit.ChildIndex < bones.BodyCount:
                // An animated character lets go of its animation, keeping the momentum of its bones
                if (bones.Mode == SkeletonBodyMode.Animated)
                    bones.Mode = SkeletonBodyMode.Ragdoll;
                return new HeldBone(bones, hit.ChildIndex, hit.Point);
            case SoftBodyComponent soft:
                var particles = new List<(int Particle, Vector3 Offset, float Grip)>();
                var closest = float.MaxValue;
                for (int i = 0; i < soft.ParticleCount; i++)
                    closest = MathF.Min(closest, Vector3.Distance(soft.GetParticlePosition(i), hit.Point));
                var reach = closest + 0.35f;
                for (int i = 0; i < soft.ParticleCount; i++)
                {
                    var offset = soft.GetParticlePosition(i) - hit.Point;
                    var distance = offset.Length();
                    if (distance <= reach && !soft.IsParticlePinned(i))
                        particles.Add((i, offset, MathF.Max(0.15f, 1f - distance / reach)));
                }
                return particles.Count > 0 ? new HeldSoftBody(soft, particles) : null;
            default:
                return base.Grab(hit);
        }
    }

    private sealed class HeldBone : IHeld
    {
        private readonly SkeletonBodyComponent _bones;
        private readonly int _index;
        private readonly Vector3 _offset;

        public HeldBone(SkeletonBodyComponent bones, int index, Vector3 point)
        {
            _bones = bones;
            _index = index;
            _offset = Vector3.Transform(point - bones.GetBodyPosition(index), Quaternion.Invert(bones.GetBodyOrientation(index)));
        }

        public bool Valid => _bones.Simulation is not null && _bones.Mode != SkeletonBodyMode.Animated && _index < _bones.BodyCount;

        public void Pull(Vector3 target, float responsiveness)
        {
            var velocity = (target - _bones.GetBodyPosition(_index) - Vector3.Transform(_offset, _bones.GetBodyOrientation(_index))) * responsiveness;
            var body = BoneBody(_bones, _index);
            body.Velocity.Linear = new System.Numerics.Vector3(velocity.X, velocity.Y, velocity.Z);
            body.Velocity.Angular *= 0.9f;
            body.Awake = true;
        }

        public void Launch(Vector3 velocity)
        {
            var body = BoneBody(_bones, _index);
            body.Velocity.Linear = new System.Numerics.Vector3(velocity.X, velocity.Y, velocity.Z);
            body.Awake = true;
        }
    }

    private sealed class HeldSoftBody(SoftBodyComponent soft, List<(int Particle, Vector3 Offset, float Grip)> particles) : IHeld
    {
        public bool Valid => soft.ParticleCount > 0;

        public void Pull(Vector3 target, float responsiveness)
        {
            foreach (var (particle, offset, grip) in particles)
            {
                var pull = (target + offset - soft.GetParticlePosition(particle)) * responsiveness;
                if (pull.Length() > 10f)
                    pull = Vector3.Normalize(pull) * 10f;
                soft.SetParticleVelocity(particle, Vector3.Lerp(soft.GetParticleVelocity(particle), pull, grip));
            }
        }

        public void Launch(Vector3 velocity)
        {
            for (int i = 0; i < soft.ParticleCount; i++)
                soft.SetParticleVelocity(i, velocity);
        }
    }

    /// <summary> Median frames per second and the 5 % slowest frame, over the frames since the scene was loaded (or the last clear) </summary>
    public (double Median, double Low) Fps()
    {
        if (_frameTimes.Count == 0)
            return (0, 0);
        var sorted = _frameTimes.OrderBy(t => t).ToArray();
        return (1000.0 / sorted[sorted.Length / 2], 1000.0 / sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * 0.95))]);
    }

    public string FpsText()
    {
        var (median, low) = Fps();
        return string.Format(CultureInfo.InvariantCulture, "{0:0} FPS médian, {1:0} au 95e centile lent ({2} frames)", median, low, _frameTimes.Count);
    }
}
