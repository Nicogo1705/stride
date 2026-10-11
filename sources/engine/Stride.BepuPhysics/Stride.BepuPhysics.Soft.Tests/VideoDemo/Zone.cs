// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using Stride.BepuPhysics.Definitions;
using Stride.BepuPhysics.Definitions.Colliders;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Rendering;
using Stride.Rendering.Materials;
using Stride.Rendering.ProceduralModels;

namespace Stride.BepuPhysics.Soft.Tests.VideoDemo;

/// <summary> A text drawn at a point of the world, on a panel of <see cref="Background"/> when set </summary>
internal sealed record SceneLabel(Vector3 World, string Text, Color? Background = null);

/// <summary>
/// One area of the world on its flat pad, with its own simulation: it steps only while the camera is near, and stays drawn when it does not.
/// Everything moves on timers; nothing waits for a key.
/// </summary>
internal abstract class Zone
{
    private float _time;

    public abstract string Name { get; }

    public abstract string Description { get; }

    /// <summary> Middle of the pad, on the ground </summary>
    public Vector3 Origin { get; private set; }

    /// <summary> The zone's simulation, an index into <see cref="BepuConfiguration.BepuSimulations"/> </summary>
    public int Index { get; private set; }

    public IndexBasedSimulationSelector Selector { get; private set; } = new();

    public BepuSimulation? Simulation { get; set; }

    /// <summary> Whether the zone's simulation steps </summary>
    public bool Active => Simulation is { Enabled: true };

    /// <summary> Seconds the zone has simulated </summary>
    public float Time => _time;

    public virtual Vector3 Eye => Origin + new Vector3(0.5f, 5f, 12f);

    public virtual Vector3 Target => Origin + new Vector3(0f, 1f, 0f);

    public virtual IReadOnlyList<SkeletonBodyMode> LegendModes => Array.Empty<SkeletonBodyMode>();

    public virtual string LegendCaption => "SkeletonBodyComponent.Mode (body color)";

    public void Place(Vector3 origin, int index)
    {
        Origin = origin;
        Index = index;
        Selector = new IndexBasedSimulationSelector { Index = index };
        _time = 0f;
    }

    public abstract void Build(VideoDemoGame game, WorldTerrain terrain);

    /// <summary> Called every frame while the zone simulates: spawning and timed pushes </summary>
    public void Update(VideoDemoGame game, float seconds)
    {
        _time += seconds;
        Tick(game, _time, seconds);
    }

    protected virtual void Tick(VideoDemoGame game, float time, float seconds) { }

    public virtual IEnumerable<SceneLabel> Labels() => Enumerable.Empty<SceneLabel>();

    public virtual IEnumerable<string> Status() => Enumerable.Empty<string>();

    /// <summary> Removes an entity added earlier, such as the oldest of a stream of dropped objects </summary>
    protected static void Remove(VideoDemoGame game, Entity entity) => game.SceneSystem.SceneInstance.RootScene.Entities.Remove(entity);

    /// <summary> Whether the phase of a cycle of <paramref name="period"/> seconds crossed <paramref name="at"/> during the last frame </summary>
    protected static bool Every(float time, float seconds, float period, float at = 0f)
    {
        float now = MathUtil.Mod(time - at, period), before = now - seconds;
        return before < 0f && seconds > 0f;
    }
}

/// <summary> Builders shared by the zones </summary>
internal static class Kit
{
    public static readonly Color[] JellyColors = { new(255, 90, 140), new(130, 230, 100), new(255, 185, 40), new(150, 110, 255), new(70, 200, 240) };

    public static readonly Color[] FleshColors = { new(255, 120, 170), new(160, 120, 255), new(150, 220, 80), new(90, 190, 255), new(255, 160, 110) };

    /// <summary> A kinematic body with a model, driven by SetTargetPose </summary>
    public static Entity Kinematic(VideoDemoGame game, string name, PrimitiveProceduralModelBase shape, ColliderBase collider, Material material, Vector3 position, Quaternion? rotation = null)
    {
        var entity = new Entity(name)
        {
            new ModelComponent(Look.Model(game, shape, material)),
            new BodyComponent { Kinematic = true, Collider = new CompoundCollider { Colliders = { collider } } },
        };
        entity.Transform.Position = position;
        entity.Transform.Rotation = rotation ?? Quaternion.Identity;
        game.AddToScene(entity);
        return entity;
    }

    /// <summary> A see-through jelly, scattering or absorbing by its color index </summary>
    public static Entity AddJelly(VideoDemoGame game, PrimitiveProceduralModelBase shape, Vector3 position, int color, int resolution, bool gravity = true)
    {
        var medium = color % 2 == 0 ? MaterialVolumeMedium.Scattering : MaterialVolumeMedium.Absorbing;
        var jelly = new Entity("jelly")
        {
            new ModelComponent(Look.Model(game, shape, Look.Volume(game.GraphicsDevice, JellyColors[color % JellyColors.Length], 0.85f, 0.3f, medium))),
            new VolumetricSoftBodyComponent { Resolution = resolution, Stiffness = 10f, ShearStiffness = 9f, VolumeStiffness = 12f, Damping = 0.3f, Mass = 1.5f, Gravity = gravity },
        };
        jelly.Transform.Position = position;
        game.AddToScene(jelly);
        return jelly;
    }

    /// <summary> A soft ball </summary>
    public static Entity AddSoftBall(VideoDemoGame game, Vector3 position, float radius, Color color, int resolution = 5, float mass = 0.6f)
    {
        var ball = new Entity("soft ball")
        {
            new ModelComponent(Look.Model(game, new SphereProceduralModel { Radius = radius, Tessellation = 32 }, color, 0.85f)),
            new VolumetricSoftBodyComponent { Resolution = resolution, Stiffness = 14f, ShearStiffness = 14f, VolumeStiffness = 12f, Damping = 0.15f, Mass = mass },
        };
        ball.Transform.Position = position;
        game.AddToScene(ball);
        return ball;
    }

    /// <summary> A round arena whose top is at <paramref name="top"/>, a pillar in the middle with a bar sweeping at hip height </summary>
    public static void BuildArena(VideoDemoGame game, Vector3 top, float radius, float sweeperLength)
    {
        var arena = new Entity("arena")
        {
            new StaticComponent { Collider = new CompoundCollider { Colliders = { new CylinderCollider { Radius = radius, Length = 0.3f } } } },
            new ModelComponent(Look.Model(game, new CylinderProceduralModel { Radius = radius, Height = 0.3f, Tessellation = 96 }, new Color(205, 190, 240), 0.55f)),
        };
        arena.Transform.Position = top - new Vector3(0f, 0.15f, 0f);
        game.AddToScene(arena);
        Props.Pillar(game, top, 0.3f, 1.0f, Look.Ivory, 0.6f);
        var size = new Vector3(sweeperLength, 0.2f, 0.2f);
        var sweeper = new BodyComponent { Kinematic = true, Collider = new CompoundCollider { Colliders = { new BoxCollider { Size = size } } } };
        var entity = new Entity("sweeper") { new ModelComponent(Look.Model(game, new CubeProceduralModel { Size = size }, Look.Navy, 0.8f)), sweeper };
        entity.Transform.Position = top + new Vector3(0f, 0.32f, 0f);
        float angle = 0f;
        entity.Add(new StepDriver((time, step) =>
        {
            // Faster, then slower, so nothing on the arena can learn the rhythm
            angle += step * (1.0f + 0.5f * MathF.Sin(time * 0.35f));
            sweeper.SetTargetPose(top + new Vector3(0f, 0.32f, 0f), Quaternion.RotationY(angle));
        }));
        game.AddToScene(entity);
    }

    /// <summary> A ragdoll hanging head down from a gallows at <paramref name="foot"/> by a rope tied to its left foot bone (BodyAnchor with a NodeName) </summary>
    public static SkeletonBodyComponent AddHangingRagdoll(VideoDemoGame game, Vector3 foot)
    {
        var navy = Look.Paint(game.GraphicsDevice, Look.Navy, 0.5f);
        foreach (var x in new[] { -0.9f, 0.9f })
            Look.StaticBox(game, new Vector3(0.14f, 3.6f, 0.14f), foot + new Vector3(x, 1.8f, 0f), navy, "gallows post");
        var beam = Look.StaticBox(game, new Vector3(1.94f, 0.16f, 0.16f), foot + new Vector3(0f, 3.66f, 0f), navy, "gallows beam");
        // Head down: the feet, 0.06 m above the origin of the entity and 0.11 m to its side, are now below it and on the other side
        var (human, body, _) = Humans.Add(game, SkeletonBodyMode.Ragdoll, foot + new Vector3(0.11f, 2.6f, 0f), 0f, new ProceduralCharacter.Animator { Paused = true }, "hanging ragdoll", Quaternion.RotationZ(MathUtil.Pi));
        Ropes.Add(game, foot + new Vector3(0f, 3.58f, 0f), foot + new Vector3(0f, 2.54f, 0f), 0.045f, Look.Ivory, beam, human, toNode: "Foot.L", mass: 30f, stiffness: 90f, name: "ankle rope");
        return body;
    }

    /// <summary> Whether a skeleton stands: on the ground, its trunk upright </summary>
    public static bool Upright(SkeletonBodyComponent body, int trunk) =>
        body.IsGrounded && body.BodyCount > trunk && Vector3.Transform(Vector3.UnitY, body.GetBodyOrientation(trunk)).Y > 0.8f;
}
