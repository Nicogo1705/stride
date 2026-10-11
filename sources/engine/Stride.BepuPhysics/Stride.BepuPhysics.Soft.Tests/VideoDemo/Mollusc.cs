// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using Stride.BepuPhysics.Components;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Harness.Demo;
using Stride.Rendering;
using Stride.Graphics.GeometricPrimitives;
using Stride.Rendering.ProceduralModels;

namespace Stride.BepuPhysics.Soft.Tests.VideoDemo;

/// <summary> A mollusc made in code: a capsule over a chain of bones, crawling by a wave running along its body </summary>
internal static class Mollusc
{
    public const int Bones = 8;
    public const float Radius = 0.22f;
    public const float Length = 1.8f;
    public const string Head = "Bone7";

    /// <summary> A capsule along Y over <see cref="Bones"/> nodes from its tail to its head, with rings along its length so it can bend </summary>
    public static Model Model(DemoGame game, Color color)
    {
        var model = Look.Model(game, new CapsuleProceduralModel { Length = Length, Radius = Radius, Tessellation = 20, LengthRings = 16 }, color, 0.75f);
        var nodes = new ModelNodeDefinition[Bones + 1];
        nodes[0] = new ModelNodeDefinition { Name = "Root", ParentIndex = -1, Transform = { Scale = Vector3.One, Rotation = Quaternion.Identity }, Flags = ModelNodeFlags.Default };
        float half = Length / 2f + Radius * 0.6f, step = 2f * half / (Bones - 1);
        for (int i = 1; i <= Bones; i++)
        {
            var position = i == 1 ? new Vector3(0f, -half, 0f) : new Vector3(0f, step, 0f);
            nodes[i] = new ModelNodeDefinition { Name = "Bone" + (i - 1), ParentIndex = i - 1, Transform = { Position = position, Scale = Vector3.One, Rotation = Quaternion.Identity }, Flags = ModelNodeFlags.Default };
        }
        model.Skeleton = new Skeleton { Nodes = nodes };
        return model;
    }

    public static VolumetricSoftBodyComponent Body() => new() { Resolution = 11, FollowSkeleton = true, SkeletonStiffness = 3f, SkeletonDamping = 0.35f, Mass = 4f };

    /// <summary> The model's Y axis turned to the horizontal heading <paramref name="yaw"/> (zero is +X) </summary>
    public static Quaternion Orientation(float yaw) => Quaternion.RotationMatrix(Matrix.RotationZ(-MathUtil.PiOverTwo) * Matrix.RotationY(yaw));

    public static Vector3 Forward(float yaw) => new(MathF.Cos(yaw), 0f, -MathF.Sin(yaw));

    /// <summary> Crawls around a circle at <paramref name="speed"/> meters per second, counter clockwise seen from above </summary>
    public static Func<float, (Vector3 Position, float Yaw)> Circle(Vector3 center, float radius, float speed, float start) => time =>
    {
        var angle = start + speed * time / radius;
        return (center + new Vector3(radius * MathF.Cos(angle), Radius + 0.02f, -radius * MathF.Sin(angle)), angle + MathUtil.PiOverTwo);
    };

    /// <summary> Crawls along +X from <paramref name="start"/> until its head is pressed into a wall, then backs off, forever </summary>
    public static Func<float, (Vector3 Position, float Yaw)> BackAndForth(Vector3 start, float distance, float speed) => time =>
    {
        float period = 2f * distance / speed + 2f;
        float t = time % period;
        float x = t < distance / speed ? t * speed : t < distance / speed + 1.2f ? distance : t < 2f * distance / speed + 1.2f ? distance - (t - distance / speed - 1.2f) * speed : 0f;
        return (start + new Vector3(x - Length / 2f - Radius, Radius + 0.02f, 0f), 0f);
    };

    /// <summary> A sail of cloth whose front edge follows the head bone of <paramref name="mollusc"/> (WorldAnchor with a NodeName), heading <paramref name="yaw"/> </summary>
    public static Entity AddSail(DemoGame game, Entity mollusc, float yaw, Color color)
    {
        var head = mollusc.Transform.Position + Forward(yaw) * (Length / 2f + Radius * 0.6f);
        var edge = new SoftBodyPin { Region = new BoundingBox(new Vector3(0.39f, -1f, -1f), new Vector3(0.41f, 1f, 1f)), Anchor = new WorldAnchor { Entity = mollusc, NodeName = Head } };
        var sail = new Entity("sail")
        {
            new ModelComponent(Look.Model(game, new PlaneProceduralModel { Size = new Vector2(0.8f, 0.55f), Tessellation = new Int2(16, 10), Normal = NormalDirection.UpZ, GenerateBackFace = true }, color, 0.3f)),
            new ClothComponent { Pins = { edge } },
        };
        sail.Transform.Position = head - Forward(yaw) * 0.4f + new Vector3(0f, Radius + 0.25f, 0f);
        sail.Transform.Rotation = Quaternion.RotationY(yaw);
        game.AddToScene(sail);
        return sail;
    }

    public static Entity Add(DemoGame game, Color color, MolluscDriver driver, Func<float, (Vector3 Position, float Yaw)> path)
    {
        var body = Body();
        var entity = new Entity("mollusc") { new ModelComponent(Model(game, color)), body };
        var (position, yaw) = path(0f);
        entity.Transform.Position = position;
        entity.Transform.Rotation = Orientation(yaw);
        game.AddToScene(entity);
        driver.Add(entity, path);
        return entity;
    }
}

/// <summary> Moves each mollusc along its path and plays its crawl, before each physics step </summary>
internal sealed class MolluscDriver : ScriptComponent, ISimulationUpdate
{
    private readonly List<(Entity Entity, Func<float, (Vector3 Position, float Yaw)> Path)> _molluscs = new();

    public int Count => _molluscs.Count;

    public float Time { get; private set; }

    public void Add(Entity entity, Func<float, (Vector3 Position, float Yaw)> path) => _molluscs.Add((entity, path));

    public void SimulationUpdate(BepuSimulation simulation, float simTimeStep)
    {
        Time += simTimeStep;
        foreach (var (entity, path) in _molluscs)
        {
            var (position, yaw) = path(Time);
            entity.Transform.Position = position;
            entity.Transform.Rotation = Mollusc.Orientation(yaw);
            Animate(entity.Get<ModelComponent>()!.Skeleton, Time);
        }
    }

    public void AfterSimulationUpdate(BepuSimulation simulation, float simTimeStep) { }

    /// <summary> A wave of the heading running from the head to the tail </summary>
    public static void Animate(SkeletonUpdater skeleton, float time)
    {
        var nodes = skeleton.NodeTransformations;
        float parentHeading = 0f;
        for (int bone = 1; bone < nodes.Length; bone++)
        {
            float heading = 0.5f * MathF.Sin(MathUtil.TwoPi * 0.7f * time - bone * 0.9f);
            nodes[bone].Transform.Rotation = Quaternion.RotationX(heading - parentHeading);
            parentHeading = heading;
        }
    }
}
