// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using Stride.BepuPhysics.Components;
using Stride.BepuPhysics.Definitions;
using Stride.BepuPhysics.Definitions.Colliders;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Harness.Demo;

namespace Stride.BepuPhysics.Soft.Tests.VideoDemo;

/// <summary>
/// The mollusc with a physical spine: a <see cref="SkeletonBodyComponent"/> in Standing mode on its bones (the crawl wave is the servos' target),
/// and the soft flesh following that skeleton (FollowSkeleton): flesh and bones are joined and pull on each other, and never collide.
/// Only the head is pulled toward a point moving ahead, the rest is physics.
/// </summary>
internal static class PhysicalWorm
{
    public static Entity Add(DemoGame game, Color color, Vector3 position, float yaw, Func<float, Vector3> carrot)
    {
        var flesh = Mollusc.Body();
        var bones = new SkeletonBodyComponent { Mode = SkeletonBodyMode.Standing, SelfCollision = false, ActiveSpringFrequency = 6f, ActiveMaximumForce = 60f };
        float half = Mollusc.Length / 2f + Mollusc.Radius * 0.6f, step = 2f * half / (Mollusc.Bones - 1);
        var collider = (BoneCollider)bones.Collider;
        for (int i = 0; i < Mollusc.Bones; i++)
        {
            // A capsule from each node toward the next, just inside the flesh
            collider.Bones.Add(new BoneShape
            {
                NodeName = "Bone" + i,
                AlongBone = false,
                Shape = new CapsuleCollider { Radius = 0.17f, Length = step * 0.5f, PositionLocal = new Vector3(0f, i < Mollusc.Bones - 1 ? step / 2f : 0f, 0f), Mass = 0.5f },
            });
        }
        var entity = new Entity("physical worm") { new ModelComponent(Mollusc.Model(game, color)), flesh, bones };
        entity.Transform.Position = position + new Vector3(0f, Mollusc.Radius + 0.05f, 0f);
        entity.Transform.Rotation = Mollusc.Orientation(yaw);
        entity.Add(new Driver(bones, carrot));
        game.AddToScene(entity);
        return entity;
    }

    /// <summary> Plays the crawl wave on the nodes every frame, pulls the head toward the carrot before each step, keeps the entity on the body </summary>
    private sealed class Driver(SkeletonBodyComponent bones, Func<float, Vector3> carrot) : SyncScript, ISimulationUpdate
    {
        private float _time, _stepTime;

        /// <summary> The simulation of the zone the entity is in, set before it joins the scene </summary>
        public ISimulationSelector SimulationSelector { get; set; } = SceneBasedSimulationSelector.Shared;

        public override void Update()
        {
            _time += (float)Game.UpdateTime.Elapsed.TotalSeconds;
            MolluscDriver.Animate(Entity.Get<ModelComponent>()!.Skeleton, _time);
        }

        public void SimulationUpdate(BepuSimulation simulation, float simTimeStep)
        {
            _stepTime += simTimeStep;
            if (bones.BodyCount < Mollusc.Bones || bones.Mode == SkeletonBodyMode.Animated)
                return;
            var tail = bones.GetBodyPosition(0);
            var head = bones.GetBodyPosition(Mollusc.Bones - 1);

            // The entity stays on the body: nodes without a body and the bounds of the flesh follow it
            var axis = head - tail;
            float yaw = MathF.Atan2(-axis.Z, axis.X);
            Entity.Transform.Position = (head + tail) / 2f;
            Entity.Transform.Rotation = Mollusc.Orientation(yaw);

            // Lost below the world (pushed off an edge): it stays there, nothing to drive
            if (head.Y < -3f)
                return;

            // The head is pulled toward a point running ahead; nothing else is driven
            var toward = carrot(_stepTime) - head;
            toward.Y = 0f;
            if (toward.Length() < 0.2f)
                return;
            var wanted = Vector3.Normalize(toward) * 1.1f;
            var body = VideoDemoGame.BoneBody(bones, Mollusc.Bones - 1);
            var velocity = body.Velocity.Linear;
            body.Velocity.Linear = new System.Numerics.Vector3(velocity.X + (wanted.X - velocity.X) * 0.3f, velocity.Y, velocity.Z + (wanted.Z - velocity.Z) * 0.3f);
            body.Awake = true;
        }

        public void AfterSimulationUpdate(BepuSimulation simulation, float simTimeStep) { }
    }

    /// <summary> A point going around a circle at <paramref name="speed"/> m/s </summary>
    public static Func<float, Vector3> Circle(Vector3 center, float radius, float speed, float start) => time =>
    {
        float angle = start + speed * time / radius;
        return center + new Vector3(radius * MathF.Cos(angle), 0f, -radius * MathF.Sin(angle));
    };
}
