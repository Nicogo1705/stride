// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using Stride.BepuPhysics.Definitions.Colliders;
using Stride.BepuPhysics.Components;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Harness.Demo;
using Stride.Rendering.ProceduralModels;

namespace Stride.BepuPhysics.Soft.Tests.VideoDemo;

/// <summary> A scene of the video showcase: a feature and its pull request, a checkered ground, labels anchored in the scene </summary>
internal abstract class VideoScene : BepuDemoScene
{
    public override string Group => "Video";

    /// <summary> The feature shown, as in the overlay and the menu </summary>
    public abstract string Title { get; }

    /// <summary> The pull request(s) the feature comes from </summary>
    public abstract string Pr { get; }

    public override string Name => $"{Title}  ·  {Pr}";

    /// <summary> Shows Bepu's wireframes while the debug view is on « Auto » </summary>
    public virtual bool WireframeByDefault => false;

    /// <summary> Starts with the camera turning slowly around <see cref="DemoScene.Target"/> (C toggles it) </summary>
    public virtual bool AutoOrbit => false;

    /// <summary> Keys of this scene only, one line </summary>
    public virtual string? SceneKeys => null;

    protected virtual bool HasGround => true;

    /// <summary> Texts drawn at points of the scene </summary>
    public virtual IEnumerable<(Vector3 World, string Text)> Labels() => Enumerable.Empty<(Vector3, string)>();

    /// <summary> The scene's own keys, read while the menu is closed </summary>
    public virtual void UpdateInput(VideoDemoGame game) { }

    /// <summary> Panels of the scene over the image, only while the overlay is visible </summary>
    public virtual void DrawOverlay(VideoDemoGame game) { }

    public sealed override void Build(DemoGame game)
    {
        Look.Reset(game.GraphicsDevice);
        if (HasGround)
            Look.Ground(game);
        BuildScene((VideoDemoGame)game);
    }

    protected abstract void BuildScene(VideoDemoGame game);

    /// <summary> G: a handful of colored shapes falling on the middle of the scene </summary>
    public override void Drop(DemoGame game)
    {
        var random = new Random();
        for (int i = 0; i < 10; i++)
        {
            var position = Target + new Vector3((float)random.NextDouble() * 2f - 1f, 5f + i * 0.6f, (float)random.NextDouble() * 2f - 1f);
            game.AddToScene(Props.Rigid(game, i, position, Quaternion.RotationYawPitchRoll(i, i * 0.7f, 0f)));
        }
    }
}

/// <summary> Small builders shared by the scenes </summary>
internal static class Props
{
    /// <summary> One of four rigid shapes in the palette's colors, picked by <paramref name="index"/> </summary>
    public static Entity Rigid(DemoGame game, int index, Vector3 position, Quaternion rotation, float scale = 1f)
    {
        var color = Look.Accents[index % Look.Accents.Length];
        (PrimitiveProceduralModelBase shape, ICollider collider) = (index % 4) switch
        {
            0 => ((PrimitiveProceduralModelBase)new CubeProceduralModel { Size = new Vector3(0.4f * scale) }, (ICollider)new CompoundCollider { Colliders = { new BoxCollider { Size = new Vector3(0.4f * scale), Mass = 1f } } }),
            1 => (new SphereProceduralModel { Radius = 0.22f * scale, Tessellation = 24 }, new CompoundCollider { Colliders = { new SphereCollider { Radius = 0.22f * scale, Mass = 1f } } }),
            2 => (new CapsuleProceduralModel { Radius = 0.14f * scale, Length = 0.3f * scale, Tessellation = 16 }, new CompoundCollider { Colliders = { new CapsuleCollider { Radius = 0.14f * scale, Length = 0.3f * scale, Mass = 1f } } }),
            _ => (new CylinderProceduralModel { Radius = 0.2f * scale, Height = 0.3f * scale, Tessellation = 24 }, new CompoundCollider { Colliders = { new CylinderCollider { Radius = 0.2f * scale, Length = 0.3f * scale, Mass = 1f } } }),
        };
        var entity = new Entity("objet") { new ModelComponent(Look.Model(game, shape, color, 0.55f)), new BodyComponent { Collider = collider } };
        entity.Transform.Position = position;
        entity.Transform.Rotation = rotation;
        return entity;
    }

    /// <summary> A dynamic ball </summary>
    public static Entity Ball(DemoGame game, float radius, float mass, Color color, Vector3 position)
    {
        var entity = new Entity("balle")
        {
            new ModelComponent(Look.Model(game, new SphereProceduralModel { Radius = radius, Tessellation = 28 }, color, 0.7f)),
            new BodyComponent { Collider = new CompoundCollider { Colliders = { new SphereCollider { Radius = radius, Mass = mass } } } },
        };
        entity.Transform.Position = position;
        return entity;
    }

    /// <summary> A static cylinder standing on the ground </summary>
    public static Entity Pillar(DemoGame game, Vector3 foot, float radius, float height, Color color)
    {
        var entity = new Entity("pilier")
        {
            new StaticComponent { Collider = new CompoundCollider { Colliders = { new CylinderCollider { Radius = radius, Length = height } } } },
            new ModelComponent(Look.Model(game, new CylinderProceduralModel { Radius = radius, Height = height, Tessellation = 32 }, color, 0.5f)),
        };
        entity.Transform.Position = foot + new Vector3(0f, height / 2f, 0f);
        game.AddToScene(entity);
        return entity;
    }

    /// <summary> Puts a body back at <paramref name="position"/>, at rest </summary>
    public static void Respawn(Entity entity, Vector3 position, Quaternion rotation)
    {
        if (entity.Get<BodyComponent>() is { } body)
        {
            body.Teleport(position, rotation);
            body.LinearVelocity = Vector3.Zero;
            body.AngularVelocity = Vector3.Zero;
            body.Awake = true;
        }
        else if (entity.Get<SoftBodyComponent>() is { } soft)
        {
            soft.Teleport(position, rotation);
            soft.LinearVelocity = Vector3.Zero;
            soft.Awake = true;
        }
    }

    public static Vector3 PositionOf(Entity entity) => entity.Get<BodyComponent>() is { Simulation: not null } body ? body.Position : entity.Transform.WorldMatrix.TranslationVector;
}

/// <summary> Calls a delegate before each physics step with the time since the scene started: kinematic motions, wind, servos </summary>
internal sealed class StepDriver(Action<float, float> step) : ScriptComponent, ISimulationUpdate
{
    private float _time;

    public void SimulationUpdate(BepuSimulation simulation, float simTimeStep)
    {
        _time += simTimeStep;
        step(_time, simTimeStep);
    }

    public void AfterSimulationUpdate(BepuSimulation simulation, float simTimeStep) { }
}

/// <summary>
/// Drops new objects at a rate up to a count, then reuses the oldest one; objects that leave the area come back from the top.
/// The scene keeps moving forever without ever being reloaded.
/// </summary>
internal sealed class Spawner(DemoGame game, Func<int, Entity> create, Func<int, Vector3> spawnPoint, float interval, int count, Vector3 center, float radius) : SyncScript
{
    private readonly List<Entity> _items = new();
    private float _time;
    private int _spawned;

    public IReadOnlyList<Entity> Items => _items;

    public override void Update()
    {
        _time += (float)Game.UpdateTime.Elapsed.TotalSeconds;
        if (_time >= interval)
        {
            _time = 0f;
            if (_items.Count < count)
            {
                var entity = create(_spawned);
                entity.Transform.Position = spawnPoint(_spawned);
                game.AddToScene(entity);
                _items.Add(entity);
            }
            else
            {
                // The oldest one goes back to the top, so the pile keeps churning
                var oldest = _items[0];
                _items.RemoveAt(0);
                _items.Add(oldest);
                Props.Respawn(oldest, spawnPoint(_spawned), RandomRotation(_spawned));
            }
            _spawned++;
        }

        foreach (var entity in _items)
        {
            var position = Props.PositionOf(entity);
            var flat = new Vector2(position.X - center.X, position.Z - center.Z);
            if (position.Y < -4f || flat.Length() > radius)
                Props.Respawn(entity, spawnPoint(_spawned++), RandomRotation(_spawned));
        }
    }

    public static Quaternion RandomRotation(int seed) => Quaternion.RotationYawPitchRoll(seed * 1.7f, seed * 0.9f, seed * 0.4f);
}
