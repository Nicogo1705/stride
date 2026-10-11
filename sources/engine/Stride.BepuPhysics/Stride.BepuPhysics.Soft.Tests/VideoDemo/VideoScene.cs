// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using Stride.BepuPhysics.Definitions;
using Stride.BepuPhysics.Definitions.Colliders;
using Stride.BepuPhysics.Components;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Harness.Demo;
using Stride.Rendering.ProceduralModels;

namespace Stride.BepuPhysics.Soft.Tests.VideoDemo;

/// <summary> A scene of the video showcase: a feature and its pull requests, labels anchored in the scene </summary>
/// <remarks> Nothing is ever put back automatically: what is thrown away stays away </remarks>
internal abstract class VideoScene : BepuDemoScene
{
    public override string Group => "Video";

    /// <summary> The feature shown, as in the overlay and the menu </summary>
    public abstract string Title { get; }

    /// <summary> The pull request(s) the feature comes from </summary>
    public abstract string Pr { get; }

    public override string Name => $"{Title}  ·  {Pr}";

    /// <summary> The debug view the scene opens with, an index into <see cref="VideoDemoGame.Views"/> </summary>
    public virtual int DefaultDebugView => VideoDemoGame.NoDebugView;


    /// <summary> The modes listed in the color legend, none for a scene without skeletons </summary>
    public virtual IReadOnlyList<SkeletonBodyMode> LegendModes => Array.Empty<SkeletonBodyMode>();

    /// <summary> What carries the mode color in this scene, the title of the legend </summary>
    public virtual string LegendCaption => "SkeletonBodyComponent.Mode (body color)";

    /// <summary> Texts drawn at points of the scene </summary>
    public virtual IEnumerable<SceneLabel> Labels() => Enumerable.Empty<SceneLabel>();

    /// <summary> Panels of the scene over the image, only while the overlay is visible </summary>
    public virtual void DrawOverlay(VideoDemoGame game) { }

    /// <summary> Physics threads of every scene: past about eight, dispatching the small jobs of soft bodies and ragdolls costs more than it saves </summary>
    public const int PhysicsThreads = 8;

    public override void OnSimulationReady(BepuSimulation simulation)
    {
        simulation.ThreadCount = Math.Min(PhysicsThreads, Environment.ProcessorCount);
    }

    public sealed override void Build(DemoGame game)
    {
        Look.Reset(game.GraphicsDevice);
        BuildScene((VideoDemoGame)game);
    }

    protected abstract void BuildScene(VideoDemoGame game);
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
        var entity = new Entity("prop") { new ModelComponent(Look.Model(game, shape, color, 0.55f)), new BodyComponent { Collider = collider } };
        entity.Transform.Position = position;
        entity.Transform.Rotation = rotation;
        return entity;
    }

    /// <summary> A dynamic ball </summary>
    public static Entity Ball(DemoGame game, float radius, float mass, Color color, Vector3 position, float gloss = 0.7f)
    {
        var entity = new Entity("ball")
        {
            new ModelComponent(Look.Model(game, new SphereProceduralModel { Radius = radius, Tessellation = 28 }, color, gloss)),
            new BodyComponent { Collider = new CompoundCollider { Colliders = { new SphereCollider { Radius = radius, Mass = mass } } } },
        };
        entity.Transform.Position = position;
        return entity;
    }

    /// <summary> A dynamic crate </summary>
    public static Entity Crate(DemoGame game, Vector3 size, float mass, Vector3 position, Color? color = null)
    {
        var entity = new Entity("crate")
        {
            new BodyComponent { Collider = new CompoundCollider { Colliders = { new BoxCollider { Size = size, Mass = mass } } } },
            new ModelComponent(Look.Model(game, new CubeProceduralModel { Size = size }, color ?? Look.Wood, 0.35f)),
        };
        entity.Transform.Position = position;
        return entity;
    }

    /// <summary> A static cylinder standing on the ground </summary>
    public static Entity Pillar(DemoGame game, Vector3 foot, float radius, float height, Color color, float gloss = 0.5f)
    {
        var entity = new Entity("pillar")
        {
            new StaticComponent { Collider = new CompoundCollider { Colliders = { new CylinderCollider { Radius = radius, Length = height } } } },
            new ModelComponent(Look.Model(game, new CylinderProceduralModel { Radius = radius, Height = height, Tessellation = 40 }, color, gloss)),
        };
        entity.Transform.Position = foot + new Vector3(0f, height / 2f, 0f);
        game.AddToScene(entity);
        return entity;
    }
}

/// <summary> Calls a delegate before each physics step with the time since the scene started: kinematic motions, wind, servos </summary>
internal sealed class StepDriver(Action<float, float> step) : ScriptComponent, ISimulationUpdate
{
    private float _time;

    /// <summary> The simulation of the zone the entity is in, set before it joins the scene </summary>
    public ISimulationSelector SimulationSelector { get; set; } = SceneBasedSimulationSelector.Shared;

    public void SimulationUpdate(BepuSimulation simulation, float simTimeStep)
    {
        _time += simTimeStep;
        step(_time, simTimeStep);
    }

    public void AfterSimulationUpdate(BepuSimulation simulation, float simTimeStep) { }
}

/// <summary> Drops <paramref name="count"/> objects, one every <paramref name="interval"/> seconds, then stops; nothing is recycled </summary>
internal sealed class Dropper(DemoGame game, Func<int, Entity> create, Func<int, Vector3> spawnPoint, float interval, int count) : SyncScript
{
    private float _time = interval;
    private int _dropped;

    public int Dropped => _dropped;

    /// <summary> Whether objects drop: off while their zone does not simulate </summary>
    public Func<bool> Active { get; set; } = () => true;

    public override void Update()
    {
        if (_dropped >= count || !Active())
            return;
        _time += (float)Game.UpdateTime.Elapsed.TotalSeconds;
        if (_time < interval)
            return;
        _time = 0f;
        var entity = create(_dropped);
        entity.Transform.Position = spawnPoint(_dropped);
        game.AddToScene(entity);
        _dropped++;
    }
}
