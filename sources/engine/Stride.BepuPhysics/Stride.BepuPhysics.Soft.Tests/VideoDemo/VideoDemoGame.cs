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
using Stride.Core.Diagnostics;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Extensions;
using Stride.Games;
using Stride.Graphics;
using Stride.Harness.Demo;
using Stride.Input;
using Stride.Rendering;
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

    private ParticleView? _particles;
    private int _overlayState;
    private bool _orbit;
    private float _orbitAngle, _orbitRadius, _orbitHeight;
    private readonly List<double> _frameTimes = new();
    private long _lastFrame;
    private SoftBodyDeformationMode _deformation = SoftBodyDeformationMode.Gpu;

    // Timing of the deformation: the soft body processor on the CPU, the whole frame on the GPU (timestamps read a few frames later)
    private const int GpuTimerLatency = 4;
    private readonly QueryPool?[] _gpuTimers = new QueryPool?[GpuTimerLatency];
    private readonly long[] _gpuTicks = new long[2];
    private readonly List<double> _gpuFrameTimes = new();
    private readonly List<double> _deformationTimes = new();
    private System.Threading.Channels.ChannelReader<ProfilingEvent>? _profilingEvents;
    private int _gpuFrame;

    public VideoDemoGame(IEnumerable<DemoScene> scenes, string buildLabel, string captureFolder) : base(scenes, buildLabel, captureFolder)
    {
        KeyHelpVisible = false;
    }

    /// <summary> Frame times in milliseconds since the scene was loaded </summary>
    public IReadOnlyList<double> FrameTimes => _frameTimes;

    public void ClearFrameTimes()
    {
        _frameTimes.Clear();
        _gpuFrameTimes.Clear();
        _deformationTimes.Clear();
    }

    /// <summary> Where the soft bodies of every scene are deformed, F4 switches it </summary>
    public SoftBodyDeformationMode Deformation
    {
        get => _deformation;
        set
        {
            _deformation = value;
            ApplyDeformation();
        }
    }

    private void ApplyDeformation()
    {
        foreach (var body in SoftBodies())
            body.DeformationMode = _deformation;
    }

    /// <summary> Switches to a debug view as V does </summary>
    public void SetDebugView(int view)
    {
        DebugView = view;
        OnDebugViewChanged(view);
    }

    /// <summary> Hides the whole overlay, as the third press of H does, or shows it back in its compact form </summary>
    public void SetOverlay(bool visible)
    {
        _overlayState = visible ? 0 : 2;
        OverlayVisible = visible;
        KeyHelpVisible = false;
    }

    /// <summary> The handle of one body of a skeleton, read by reflection: the component has no API to push a single bone </summary>
    public static BodyReference BoneBody(SkeletonBodyComponent bones, int index) => bones.Simulation!.Simulation.Bodies[((BodyHandle[])SkeletonBodies.GetValue(bones)!)[index]];

    /// <summary> The debug views, in the order V goes through them </summary>
    public static readonly string[] Views = { "Colliders", "Colliders + springs", "Soft (without mesh)", "No debug draw" };

    public const int CollidersView = 0, CollidersAndSpringsView = 1, SoftView = 2, NoDebugView = 3;

    // Bepu's debug view draws the colliders and a sphere per particle; the springs and the particles' own markers are drawn by the demo.
    // They sit inside the soft models, the soft view hides those models
    protected override IReadOnlyList<string> DebugViews => Views;

    protected override void OnDebugViewChanged(int view)
    {
        ShowRigidWireframe(view is CollidersView or CollidersAndSpringsView);
        _particles ??= new ParticleView(this);
        _particles.Visible = view is CollidersAndSpringsView or SoftView;
        _particles.Markers = view == SoftView;
        foreach (var body in SoftBodies())
        {
            if (body.Entity.Get<ModelComponent>() is { } model)
                model.Enabled = view != SoftView;
        }
    }

    // Soft bodies can sit on child entities (a sail on its mollusc)
    private IEnumerable<SoftBodyComponent> SoftBodies() => SceneSystem.SceneInstance.RootScene.Entities.SelectMany(Descendants).SelectMany(e => e.Components.OfType<SoftBodyComponent>());

    private static IEnumerable<Entity> Descendants(Entity entity) => new[] { entity }.Concat(entity.Transform.Children.SelectMany(child => Descendants(child.Entity)));

    protected override void OnSceneUnloading(DemoScene scene)
    {
        base.OnSceneUnloading(scene);
        _particles?.Clear();
    }

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

        // A skybox rather than a sphere around the camera: a sky in the depth buffer at 400 m gets the ambient occlusion's
        // depth precision noise, rings and black dots
        root.Add(new Entity("sky") { new BackgroundComponent { Texture = Look.Sky(GraphicsDevice), Intensity = 1f, Is2D = false } });

        // Antialiasing, ambient occlusion and a touch of bloom on top of the tone mapping of the default compositor
        if (FindForwardRenderer(SceneSystem.GraphicsCompositor.Game) is { PostEffects: PostProcessingEffects effects })
        {
            // DisableAll (called by CreateDefault) also disables the range compression FXAA's first path needs, which leaves the image black:
            // run FXAA last, after tone mapping, with the green channel as luminance so the alpha of captures stays opaque
            effects.Bloom.StableConvolution = false;
            if (effects.Antialiasing is FXAAEffect fxaa)
            {
                fxaa.InputLuminanceInAlpha = false;
                fxaa.Enabled = true;
            }
            effects.AmbientOcclusion.Enabled = true;
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
        if (scene is VideoScene video)
            DebugView = video.DefaultDebugView;
        ClearFrameTimes();
        _orbit = false;
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
            // Compact overlay (scene and pull request), then everything with the keys, then nothing at all for clean shots
            _overlayState = (_overlayState + 1) % 3;
            OverlayVisible = _overlayState != 2;
            KeyHelpVisible = _overlayState == 1;
        }
        if (Input.IsKeyPressed(Keys.R) && CurrentScene is { } current)
        {
            LoadScene(current);
            Flash("Scene reset");
            return;
        }
        if (Input.IsKeyPressed(Keys.F4))
        {
            Deformation = _deformation == SoftBodyDeformationMode.Gpu ? SoftBodyDeformationMode.Cpu : SoftBodyDeformationMode.Gpu;
            Flash("Soft body deformation: " + DeformationText);
        }
        if (Input.IsKeyPressed(Keys.C))
        {
            _orbit = !_orbit;
            if (_orbit)
                StartOrbit();
            Flash("Orbit camera: " + (_orbit ? "on" : "off"));
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
        // Bodies created since the last frame (a scene load, a cannon) take the current mode
        ApplyDeformation();
        // After the physics step, as the soft body processor deforms the models: particles and meshes show the same instant
        _particles?.Update();
        var now = Stopwatch.GetTimestamp();
        if (_lastFrame != 0)
            _frameTimes.Add(Stopwatch.GetElapsedTime(_lastFrame, now).TotalMilliseconds);
        _lastFrame = now;
    }

    // One help for the whole demo: every key once, R is the scene reset (it also puts the camera back)
    protected override IEnumerable<(string Theme, string Keys)> SharedKeys()
    {
        yield return ("Camera", "Z Q S D: move  |  A / E: down / up  |  right click: look  |  wheel, Shift: speed  |  C: orbit camera");
        yield return ("Demo", "Esc: scenes  |  R: reset the scene  |  H: overlay full / none / compact  |  P: capture  |  " + FpsLimitKeyHelp);
        yield return ("View", "V: next debug view (" + string.Join(" / ", DebugViews) + ")  |  F4: soft body deformation GPU / CPU");
    }

    protected override IEnumerable<(string Theme, string Keys)> SubjectKeys()
    {
        yield return ("Physics", "left click: grab (rigid bodies, soft bodies, bones)  |  wheel: distance  |  F: throw / ball  |  G: object rain  |  Space: pause  |  N: one step");
        if (CurrentScene is VideoScene { SceneKeys: { } keys })
            yield return ("Scene", keys);
    }

    protected override (string Theme, string Keys)? CollapsedKeyHint => null;

    protected override bool ShowBuildLabel => _overlayState == 1;

    // Compact: the feature and its pull request only; full: what to look at, the scene's state, timings and the aim
    protected override IEnumerable<string> OverlayLines()
    {
        if (_overlayState == 0)
        {
            if (CurrentScene is VideoScene scene)
                yield return scene.Name;
            yield break;
        }
        foreach (var line in base.OverlayLines())
            yield return line;
    }

    protected override IEnumerable<string> SubjectStatus()
    {
        if (Simulation is { Enabled: false })
            yield return "PAUSED (Space)";
        yield return "Soft body deformation: " + DeformationText + " (F4)  |  " + DeformationTiming(OverlayWindow);
        if (CurrentScene is VideoScene scene)
        {
            foreach (var line in scene.Status())
                yield return line;
        }
    }

    /// <summary> The rolling mean and 95th percentile of the physics step, as the full overlay shows them </summary>
    public string StepTiming() => base.SubjectStatus().FirstOrDefault() ?? string.Empty;

    protected override IEnumerable<string> SubjectReport()
    {
        yield return StepTiming();
    }

    private const float LabelTop = 44f;
    private readonly List<RectangleF> _labelAreas = new();

    protected override void DrawCustomOverlay()
    {
        if (!OverlayVisible || CurrentScene is not VideoScene scene)
            return;
        var camera = CameraEntity.Get<CameraComponent>();
        if (camera is null)
            return;
        var size = ScreenSize;
        _labelAreas.Clear();
        foreach (var (world, text) in scene.Labels())
        {
            var screen = Vector3.Project(world, 0, 0, size.X, size.Y, 0, 1, camera.ViewProjectionMatrix);
            if (screen.Z is < 0f or > 1f || screen.X < 0 || screen.Y < 0 || screen.X > size.X || screen.Y > size.Y)
                continue;
            var measure = MeasureText(text, 18, true);
            // Whole on screen, below the title line of the compact overlay
            var corner = new Vector2(Math.Clamp(screen.X - measure.X / 2f, 8f, MathF.Max(8f, size.X - measure.X - 8f)), MathF.Max(screen.Y - measure.Y / 2f, LabelTop));
            var area = new RectangleF(corner.X - 8, corner.Y - 4, measure.X + 16, measure.Y + 8);
            // The first of two overlapping labels, such as two characters of the same kind side by side
            if (Overlaps(area))
                continue;
            _labelAreas.Add(area);
            FillRect(area, new Color(20, 22, 34, 170));
            DrawText(text, corner, Color.White, 18, true);
        }
        scene.DrawOverlay(this);
        if (scene.LegendModes.Count > 0)
            Modes.DrawLegend(this, scene.LegendModes, scene.LegendCaption);
    }

    private bool Overlaps(RectangleF area)
    {
        foreach (var other in _labelAreas)
        {
            if (other.Intersects(area))
                return true;
        }
        return false;
    }

    public Vector3 CameraPosition => CameraEntity.Transform.Position;

    // Helpers the scenes draw their own panels with
    public Int2 Screen => ScreenSize;

    public void Panel(RectangleF area, Color color) => FillRect(area, color);

    public void Write(string text, Vector2 position, Color color, int size = 17, bool bold = false) => DrawText(text, position, color, size, bold);

    public Vector2 Measure(string text, int size = 17, bool bold = false) => MeasureText(text, size, bold);

    protected override string? DescribeGrabbable(CollidableComponent collidable) => collidable switch
    {
        ClothComponent => "cloth (grabbable)",
        SoftBodyComponent when collidable.Entity.Get<SkeletonBodyComponent>() is { Ghost: true } => "soft body on ghost bones (grabbable)",
        SoftBodyComponent when collidable.Entity.Get<SkeletonBodyComponent>() is not null => "flesh on a physical skeleton (grabbed by the nearest bone)",
        SoftBodyComponent => "soft body (grabbable)",
        SkeletonBodyComponent { Mode: SkeletonBodyMode.Animated } => "animated bone (grabbable: the character goes Ragdoll)",
        SkeletonBodyComponent bones => $"{bones.Mode} bone (grabbable)",
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
            case SoftBodyComponent when hit.Collidable.Entity.Get<SkeletonBodyComponent>() is { BodyCount: > 0, Ghost: false } inner:
                // Flesh on a physical skeleton: hold the bone nearest to the point, the flesh follows it
                int nearest = 0;
                for (int i = 1; i < inner.BodyCount; i++)
                {
                    if (Vector3.DistanceSquared(inner.GetBodyPosition(i), hit.Point) < Vector3.DistanceSquared(inner.GetBodyPosition(nearest), hit.Point))
                        nearest = i;
                }
                if (inner.Mode == SkeletonBodyMode.Animated)
                    inner.Mode = SkeletonBodyMode.Ragdoll;
                return new HeldBone(inner, nearest, inner.GetBodyPosition(nearest));
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

    /// <summary> The particles of every soft body as small octahedra, pinned ones in red, as in the soft body sample's particle view, and the springs along the edges of its cells or triangles as lines </summary>
    private sealed class ParticleView(VideoDemoGame game)
    {
        private readonly Dictionary<SoftBodyComponent, View> _views = new();

        private sealed record View(Entity Entity, Stride.Graphics.Buffer VertexBuffer, Stride.Graphics.Buffer IndexBuffer, VertexPositionNormalTexture[] Vertices, Stride.Graphics.Buffer LineBuffer, Stride.Graphics.Buffer LineIndexBuffer, VertexPositionNormalTexture[] LineVertices, int FreeIndices, int PinnedIndices);

        public bool Visible { get; set; }

        /// <summary> Whether the octahedra are drawn, or only the springs when Bepu's wireframe already shows the particles </summary>
        public bool Markers { get; set; } = true;

        private static float RadiusOf(SoftBodyComponent body) => body.Collider is Colliders.ParticleCollider collider ? collider.ActualRadius * 0.4f : 0.02f;

        public void Update()
        {
            var bodies = Visible ? game.SoftBodies().Where(b => b.ParticleCount > 0).ToHashSet() : new HashSet<SoftBodyComponent>();

            foreach (var body in _views.Keys.Where(b => !bodies.Contains(b) || _views[b].Vertices.Length != b.ParticleCount * 6).ToArray())
                Remove(body);

            foreach (var body in bodies)
            {
                if (!_views.TryGetValue(body, out var view))
                    _views[body] = view = Create(body);
                Refresh(body, view);
            }
        }

        public void Clear()
        {
            foreach (var body in _views.Keys.ToArray())
                Remove(body);
        }

        private void Remove(SoftBodyComponent body)
        {
            var view = _views[body];
            game.SceneSystem.SceneInstance.RootScene.Entities.Remove(view.Entity);
            view.VertexBuffer.Dispose();
            view.IndexBuffer.Dispose();
            view.LineBuffer.Dispose();
            view.LineIndexBuffer.Dispose();
            _views.Remove(body);
        }

        private View Create(SoftBodyComponent body)
        {
            // Six corners of an octahedron per particle, the free particles' triangles first, then the pinned ones'
            ReadOnlySpan<int> faces = [0, 2, 4, 2, 1, 4, 1, 3, 4, 3, 0, 4, 2, 0, 5, 1, 2, 5, 3, 1, 5, 0, 3, 5];
            var indices = new List<int>();
            int free = 0;
            foreach (var pinned in new[] { false, true })
            {
                for (int p = 0; p < body.ParticleCount; p++)
                {
                    if (body.IsParticlePinned(p) != pinned)
                        continue;
                    foreach (var corner in faces)
                        indices.Add(p * 6 + corner);
                }
                if (!pinned)
                    free = indices.Count;
            }

            var device = game.GraphicsDevice;
            var vertices = new VertexPositionNormalTexture[body.ParticleCount * 6];
            var vertexBuffer = Stride.Graphics.Buffer.Vertex.New(device, bufferSize: vertices.Length * VertexPositionNormalTexture.Layout.VertexStride, usage: GraphicsResourceUsage.Dynamic);
            var indexBuffer = Stride.Graphics.Buffer.Index.New(device, indices.ToArray());
            var vertexBinding = new VertexBufferBinding(vertexBuffer, VertexPositionNormalTexture.Layout, vertices.Length);
            var indexBinding = new IndexBufferBinding(indexBuffer, true, indices.Count);
            var everywhere = new BoundingBox(new Vector3(-1e6f), new Vector3(1e6f));

            var model = new Model { BoundingBox = everywhere };
            model.Materials.Add(new MaterialInstance(Look.Paint(device, Look.Sun, 0.3f)));
            model.Materials.Add(new MaterialInstance(Look.Paint(device, Color.Red, 0.3f)));
            model.Meshes.Add(new Mesh { MaterialIndex = 0, BoundingBox = everywhere, Draw = new MeshDraw { PrimitiveType = PrimitiveType.TriangleList, DrawCount = free, VertexBuffers = [vertexBinding], IndexBuffer = indexBinding } });
            model.Meshes.Add(new Mesh { MaterialIndex = 1, BoundingBox = everywhere, Draw = new MeshDraw { PrimitiveType = PrimitiveType.TriangleList, StartLocation = free, DrawCount = indices.Count - free, VertexBuffers = [vertexBinding], IndexBuffer = indexBinding } });

            // The springs: one vertex per particle, a line per stretch edge
            var lineVertices = new VertexPositionNormalTexture[body.ParticleCount];
            var lineIndices = new List<int>();
            foreach (var edge in body.Topology!.Edges)
            {
                if (edge.Kind == Topology.EdgeKind.Stretch)
                    lineIndices.AddRange([edge.A, edge.B]);
            }
            var lineBuffer = Stride.Graphics.Buffer.Vertex.New(device, bufferSize: lineVertices.Length * VertexPositionNormalTexture.Layout.VertexStride, usage: GraphicsResourceUsage.Dynamic);
            var lineIndexBuffer = Stride.Graphics.Buffer.Index.New(device, lineIndices.Count > 0 ? lineIndices.ToArray() : [0, 0]);
            model.Materials.Add(new MaterialInstance(Look.Paint(device, Look.Teal, 0.2f)));
            model.Meshes.Add(new Mesh
            {
                MaterialIndex = 2, BoundingBox = everywhere,
                Draw = new MeshDraw { PrimitiveType = PrimitiveType.LineList, DrawCount = lineIndices.Count, VertexBuffers = [new VertexBufferBinding(lineBuffer, VertexPositionNormalTexture.Layout, lineVertices.Length)], IndexBuffer = new IndexBufferBinding(lineIndexBuffer, true, Math.Max(2, lineIndices.Count)) },
            });

            var entity = new Entity("particles") { new ModelComponent(model) { IsShadowCaster = false } };
            game.SceneSystem.SceneInstance.RootScene.Entities.Add(entity);
            return new View(entity, vertexBuffer, indexBuffer, vertices, lineBuffer, lineIndexBuffer, lineVertices, free, indices.Count - free);
        }

        private void Refresh(SoftBodyComponent body, View view)
        {
            var meshes = view.Entity.Get<ModelComponent>()!.Model.Meshes;
            meshes[0].Draw.DrawCount = Markers ? view.FreeIndices : 0;
            meshes[1].Draw.DrawCount = Markers ? view.PinnedIndices : 0;
            var radius = RadiusOf(body);
            for (int p = 0; p < body.ParticleCount; p++)
            {
                var position = body.GetParticlePosition(p);
                for (int axis = 0; axis < 6; axis++)
                {
                    var direction = Vector3.Zero;
                    direction[axis / 2] = axis % 2 == 0 ? 1f : -1f;
                    view.Vertices[p * 6 + axis] = new VertexPositionNormalTexture(position + direction * radius, direction, Vector2.Zero);
                }
            }
            view.VertexBuffer.SetData<VertexPositionNormalTexture>(game.GraphicsContext.CommandList, view.Vertices.AsSpan());
            for (int p = 0; p < body.ParticleCount; p++)
                view.LineVertices[p] = new VertexPositionNormalTexture(body.GetParticlePosition(p), Vector3.UnitY, Vector2.Zero);
            view.LineBuffer.SetData<VertexPositionNormalTexture>(game.GraphicsContext.CommandList, view.LineVertices.AsSpan());
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

    private string DeformationText => _deformation == SoftBodyDeformationMode.Gpu ? "GPU" : "CPU";

    protected override void Draw(GameTime gameTime)
    {
        // The pool written GpuTimerLatency frames ago is read, then written again for this frame
        var commandList = GraphicsContext.CommandList;
        ref var pool = ref _gpuTimers[_gpuFrame % GpuTimerLatency];
        pool ??= QueryPool.New(GraphicsDevice, QueryType.Timestamp, 2);
        if (_gpuFrame >= GpuTimerLatency && pool.TryGetData(_gpuTicks))
            _gpuFrameTimes.Add((_gpuTicks[1] - _gpuTicks[0]) * 1000.0 / GraphicsDevice.TimestampFrequency);
        commandList.WriteTimestamp(pool, 0);
        base.Draw(gameTime);
        commandList.WriteTimestamp(pool, 1);
        _gpuFrame++;

        if (_profilingEvents is null)
        {
            Profiler.Enable(Rendering.SoftBodyProcessor.DrawKey);
            _profilingEvents = Profiler.Subscribe();
        }
        while (_profilingEvents.TryRead(out var profilingEvent))
        {
            if (profilingEvent.Key == Rendering.SoftBodyProcessor.DrawKey && profilingEvent.Type == ProfilingMessageType.End)
                _deformationTimes.Add(profilingEvent.ElapsedTime.TotalMilliseconds);
        }
    }

    private const int OverlayWindow = 120; // Frames the overlay averages over

    /// <summary>
    /// Mean and 95th percentile of the soft body processor (deformation and upload, CPU) and of the frame on the GPU,
    /// over the last <paramref name="window"/> frames since the last clear
    /// </summary>
    public string DeformationTiming(int window = int.MaxValue) => string.Format(CultureInfo.InvariantCulture, "processor CPU {0}  |  frame GPU {1}", Stats(_deformationTimes, window), Stats(_gpuFrameTimes, window));

    private static string Stats(List<double> times, int window)
    {
        if (times.Count == 0)
            return "-";
        var sorted = times.Skip(Math.Max(0, times.Count - window)).OrderBy(t => t).ToArray();
        return string.Format(CultureInfo.InvariantCulture, "{0:0.000} ms (p95 {1:0.000})", sorted.Average(), sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * 0.95))]);
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
        return string.Format(CultureInfo.InvariantCulture, "{0:0} FPS median, {1:0} at the slow 95th percentile ({2} frames)", median, low, _frameTimes.Count);
    }
}
