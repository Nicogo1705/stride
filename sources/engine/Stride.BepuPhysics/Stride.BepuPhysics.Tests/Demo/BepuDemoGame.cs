// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Stride.BepuPhysics;
using Stride.BepuPhysics.Components;
using Stride.BepuPhysics.Debug;
using Stride.BepuPhysics.Definitions.Colliders;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Input;
using Stride.Rendering.ProceduralModels;

namespace Stride.Harness.Demo;

/// <summary>
/// <see cref="DemoGame"/> for Bepu scenes: the physics step timed as in the benchmarks, pause and single steps,
/// one thread or all, and a gravity gun to grab, pull and throw what is under the crosshair.
/// </summary>
public class BepuDemoGame : DemoGame
{
    private StepTimer _timer = new();
    private IHeld? _held;
    private float _heldDistance;
    private bool _stepOnce;
    private int _allThreads;
    private DebugRenderComponent? _wireframe;
    private string? _aim;
    private bool _aimGrabbable;

    public BepuDemoGame(IEnumerable<DemoScene> scenes, string buildLabel, string captureFolder) : base(scenes, buildLabel, captureFolder)
    {
    }

    /// <summary> The simulation of the current scene, found from any of its collidables </summary>
    protected BepuSimulation? Simulation { get; private set; }

    /// <summary> Waits for physics steps of the current scene, or frames until its simulation is found </summary>
    public async System.Threading.Tasks.Task Steps(int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (Simulation is { } simulation)
                await simulation.AfterUpdate();
            else
                await Script.NextFrame();
        }
    }

    protected override string SubjectKeys => "Viser au réticule  |  clic gauche : attraper / tirer (molette : distance)  |  F : lancer ce qu'on tient, ou une balle  |  G : faire tomber des objets  |  Espace : pause  |  N : un pas  |  T : 1 thread / tous";

    protected override void OnSceneLoaded(DemoScene scene)
    {
        _held = null;
        Simulation = null;
        _timer = new StepTimer();
        AddToScene(new Entity { _timer });
    }

    protected override void UpdateSubject(float seconds)
    {
        if (Simulation is null)
        {
            Simulation = SceneSystem.SceneInstance.RootScene.Entities.SelectMany(e => e.Components).OfType<CollidableComponent>().Select(c => c.Simulation).FirstOrDefault(s => s is not null);
            if (Simulation is null)
                return;
            _allThreads = Simulation.ThreadCount;
            if (CurrentScene is BepuDemoScene loaded)
                loaded.OnSimulationReady(Simulation);
        }
        var simulation = Simulation;

        if (Input.IsKeyPressed(Keys.Space))
            simulation.Enabled = !simulation.Enabled;
        if (_stepOnce)
        {
            simulation.Enabled = false;
            _stepOnce = false;
        }
        if (Input.IsKeyPressed(Keys.N) && simulation.Enabled == false)
        {
            simulation.Enabled = true;
            _stepOnce = true;
        }
        if (Input.IsKeyPressed(Keys.T))
        {
            simulation.ThreadCount = simulation.ThreadCount == 1 ? _allThreads : 1;
            _timer.Clear();
        }
        if (Input.IsKeyPressed(Keys.G) && CurrentScene is BepuDemoScene bepuScene)
            bepuScene.Drop(this);

        UpdateGravityGun(simulation);
    }

    protected override IEnumerable<string> SubjectStatus()
    {
        var (mean, p95, count) = _timer.Window();
        var threads = Simulation is null ? "-" : Simulation.ThreadCount == 1 ? "1 thread" : Simulation.ThreadCount <= 0 ? "tous les threads" : $"{Simulation.ThreadCount} threads";
        var state = Simulation is { Enabled: false } ? "  |  EN PAUSE" : string.Empty;
        yield return string.Format(CultureInfo.InvariantCulture, "Pas de simulation (2 s glissantes, {0} pas) : moyenne {1:0.000} ms, p95 {2:0.000} ms  |  {3}{4}", count, mean, p95, threads, state);
        if (CurrentScene is BepuDemoScene scene)
        {
            foreach (var line in scene.Status())
                yield return line;
        }
    }

    /// <summary> Something the gravity gun holds </summary>
    protected interface IHeld
    {
        bool Valid { get; }
        void Pull(Vector3 target, float responsiveness);
        void Launch(Vector3 velocity);
    }

    /// <summary> What the gravity gun grabs from a hit, null for nothing; derived demos add their own collidables </summary>
    /// <remarks> Kinematic bodies are grabbed too: they follow the velocity they are given, without gravity </remarks>
    protected virtual IHeld? Grab(HitInfo hit) => hit.Collidable is BodyComponent body ? new HeldBody(body, hit.Point) : null;

    /// <summary> What the crosshair aims at, for the overlay; null when <see cref="Grab"/> would not take it </summary>
    protected virtual string? DescribeGrabbable(CollidableComponent collidable) => collidable switch
    {
        BodyComponent { Kinematic: true } => "corps cinématique (attrapable, suit le tir sans gravité)",
        BodyComponent => "corps rigide (attrapable)",
        _ => null,
    };

    protected override CrosshairState Crosshair => _held is not null ? CrosshairState.Holding : _aim is null ? CrosshairState.Idle : _aimGrabbable ? CrosshairState.Target : CrosshairState.Blocked;

    protected override string? AimDescription => _held is not null ? "objet tenu (F : lancer, molette : distance)" : _aim;

    protected override IReadOnlyList<string> DebugViews { get; } = new[] { "Off", "Rigides (wireframe Bepu)" };

    protected override void OnViewReady()
    {
        // Key None: V drives it, not the component's own F11
        _wireframe = new DebugRenderComponent { Key = Keys.None, Visible = false };
        SceneSystem.SceneInstance.RootScene.Entities.Add(new Entity("Bepu debug render") { _wireframe });
    }

    protected override void OnDebugViewChanged(int view) => ShowRigidWireframe(view == 1);

    /// <summary> Shows Bepu's wireframe of the colliders </summary>
    protected void ShowRigidWireframe(bool visible)
    {
        if (_wireframe is not null)
            _wireframe.Visible = visible;
    }

    private void UpdateGravityGun(BepuSimulation simulation)
    {
        var world = CameraEntity.Transform.WorldMatrix;
        var origin = world.TranslationVector;
        var forward = Vector3.Normalize(Vector3.TransformNormal(-Vector3.UnitZ, world));

        // The ray starts at the center of the screen, where the crosshair is
        var aiming = simulation.RayCast(origin, forward, 200f, out var hit);
        _aimGrabbable = aiming && DescribeGrabbable(hit.Collidable) is not null;
        _aim = aiming ? DescribeGrabbable(hit.Collidable) ?? (hit.Collidable is StaticComponent ? "statique (pas attrapable)" : hit.Collidable.GetType().Name + " (pas attrapable)") : null;

        if (Input.IsMouseButtonPressed(MouseButton.Left) && aiming)
        {
            _held = Grab(hit);
            _heldDistance = hit.Distance;
        }
        else if (Input.IsMouseButtonReleased(MouseButton.Left))
        {
            _held = null;
        }

        if (_held is { Valid: true } held)
        {
            _heldDistance = Math.Clamp(_heldDistance + Input.MouseWheelDelta * 0.4f, 1f, 60f);
            if (Input.IsKeyPressed(Keys.F))
            {
                held.Launch(forward * 16f);
                _held = null;
            }
            else
            {
                held.Pull(origin + forward * _heldDistance, 12f);
            }
        }
        else if (Input.IsKeyPressed(Keys.F) || Input.IsMouseButtonPressed(MouseButton.Middle))
        {
            ThrowBall(origin + forward, forward * 16f);
        }

    }

    private void ThrowBall(Vector3 position, Vector3 velocity)
    {
        var body = new BodyComponent { Collider = new CompoundCollider { Colliders = { new SphereCollider { Radius = 0.25f, Mass = 4f } } } };
        var ball = new Entity { new ModelComponent(Shape(new SphereProceduralModel { Radius = 0.25f, Tessellation = 24 }, Color.DimGray)), body };
        ball.Transform.Position = position;
        AddToScene(ball);
        body.LinearVelocity = velocity;
    }

    private sealed class HeldBody(BodyComponent body, Vector3 point) : IHeld
    {
        private readonly Vector3 _offset = Vector3.Transform(point - body.Position, Quaternion.Invert(body.Orientation));

        public bool Valid => body.Simulation is not null;

        public void Pull(Vector3 target, float responsiveness)
        {
            body.LinearVelocity = (target - body.Position - Vector3.Transform(_offset, body.Orientation)) * responsiveness;
            body.AngularVelocity *= 0.9f;
            body.Awake = true;
        }

        public void Launch(Vector3 velocity) => body.LinearVelocity = velocity;
    }

    /// <summary> Times each step from the simulation's own hooks, as the benchmarks do, and keeps the last two seconds </summary>
    private sealed class StepTimer : ScriptComponent, ISimulationUpdate
    {
        private readonly Queue<(long At, double Milliseconds)> _samples = new();
        private long _start;

        public void SimulationUpdate(BepuSimulation simulation, float simTimeStep) => _start = Stopwatch.GetTimestamp();

        public void AfterSimulationUpdate(BepuSimulation simulation, float simTimeStep)
        {
            var now = Stopwatch.GetTimestamp();
            lock (_samples)
            {
                _samples.Enqueue((now, Stopwatch.GetElapsedTime(_start, now).TotalMilliseconds));
                while (_samples.Count > 0 && Stopwatch.GetElapsedTime(_samples.Peek().At, now).TotalSeconds > 2.0)
                    _samples.Dequeue();
            }
        }

        public void Clear()
        {
            lock (_samples)
                _samples.Clear();
        }

        public (double Mean, double P95, int Count) Window()
        {
            double[] values;
            lock (_samples)
                values = _samples.Select(s => s.Milliseconds).ToArray();
            if (values.Length == 0)
                return (0, 0, 0);
            Array.Sort(values);
            return (values.Average(), values[Math.Min(values.Length - 1, (int)(values.Length * 0.95))], values.Length);
        }
    }
}

/// <summary> A <see cref="DemoScene"/> with physics: a key to drop objects, and its own status lines </summary>
public abstract class BepuDemoScene : DemoScene
{
    /// <summary> Called by G: drop a few objects onto the scene </summary>
    public virtual void Drop(DemoGame game)
    {
        var random = new Random();
        for (int i = 0; i < 10; i++)
        {
            var body = new BodyComponent { Collider = new CompoundCollider { Colliders = { new BoxCollider { Size = new Vector3(0.5f) } } } };
            var entity = new Entity { new ModelComponent(game.Shape(new CubeProceduralModel { Size = new Vector3(0.5f) }, Color.SandyBrown)), body };
            entity.Transform.Position = Target + new Vector3((float)random.NextDouble() * 2f - 1f, 6f + i * 0.6f, (float)random.NextDouble() * 2f - 1f);
            game.AddToScene(entity);
        }
    }

    /// <summary> Called once the scene's simulation exists (collision matrix, settings…) </summary>
    public virtual void OnSimulationReady(BepuSimulation simulation) { }

    /// <summary> Lines for the overlay: counts, events… </summary>
    public virtual IEnumerable<string> Status() => Enumerable.Empty<string>();

    public override IEnumerable<string> State() => Status();

    /// <summary> A static floor box, its top at y = 0 </summary>
    protected static void AddFloor(DemoGame game, Vector3 size, Color color)
    {
        var floor = new Entity
        {
            new StaticComponent { Collider = new CompoundCollider { Colliders = { new BoxCollider { Size = size } } } },
            new ModelComponent(game.Shape(new CubeProceduralModel { Size = size }, color)),
        };
        floor.Transform.Position = new Vector3(0f, -size.Y / 2f, 0f);
        game.AddToScene(floor);
    }
}
