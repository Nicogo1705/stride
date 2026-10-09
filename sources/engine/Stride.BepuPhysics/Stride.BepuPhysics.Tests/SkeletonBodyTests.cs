// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using BepuPhysics.CollisionDetection;
using Stride.BepuPhysics.Definitions.Colliders;
using Stride.BepuPhysics.Definitions.Contacts;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics.Regression;
using Stride.Rendering;
using Xunit;

namespace Stride.BepuPhysics.Tests;

public class SkeletonBodyTests : GameTestBase
{
    // Root, Hips at 1 m, an arm along +X from the hips and a leg pointing down
    private static Skeleton CreateSkeleton() => new()
    {
        Nodes =
        [
            Node("Root", -1, Vector3.Zero),
            Node("Hips", 0, new Vector3(0, 1, 0)),
            Node("Arm", 1, new Vector3(0.2f, 0, 0)),
            Node("Hand", 2, new Vector3(0.6f, 0, 0)),
            Node("Leg", 1, new Vector3(0, -0.1f, 0)),
            Node("Foot", 4, new Vector3(0, -0.8f, 0)),
        ],
    };

    private static ModelNodeDefinition Node(string name, int parent, Vector3 position) => new()
    {
        Name = name,
        ParentIndex = parent,
        Transform = { Position = position, Rotation = Quaternion.Identity, Scale = Vector3.One },
        Flags = ModelNodeFlags.Default,
    };

    private static (Entity Entity, SkeletonBodyComponent Body) AddCharacter(Scene scene, Vector3 position, params BoneShape[] bones)
    {
        var body = new SkeletonBodyComponent();
        ((BoneCollider)body.Collider).Bones.AddRange(bones);
        var entity = new Entity { new ModelComponent { Model = new Model { Skeleton = CreateSkeleton() } }, body };
        entity.Transform.Position = position;
        scene.Entities.Add(entity);
        return (entity, body);
    }

    private static BoneShape ArmShape() => new() { NodeName = "Arm", Shape = new BoxCollider { Size = new Vector3(0.6f, 0.1f, 0.1f), PositionLocal = new Vector3(0.3f, 0, 0) } };

    private static int NodeIndex(Entity entity, string name) => Array.FindIndex(entity.Get<ModelComponent>().Skeleton.Nodes, n => n.Name == name);

    [Fact]
    public static void GeneratedCapsulesSpanEachSegment()
    {
        var shapes = BoneCollider.Generate(CreateSkeleton(), 0.05f);

        // Hips to arm, arm to hand, hips to leg, leg to foot; nothing from the root
        Assert.Equal(["Hips", "Arm", "Hips", "Leg"], shapes.Select(s => s.NodeName));
        var hand = (CapsuleCollider)shapes[1].Shape;
        Assert.Equal(new Vector3(0.3f, 0, 0), hand.PositionLocal);
        Assert.Equal(0.6f - 2 * 0.05f, hand.Length, 1e-5f);
        // The capsule's axis, local Y, points along the segment
        var axis = Vector3.Transform(Vector3.UnitY, hand.RotationLocal);
        Assert.True(Vector3.Distance(axis, Vector3.UnitX) < 1e-5f);
    }

    [Fact]
    public static void BodiesReachThePoseDrawnLastFrame()
    {
        var game = new GameTest();
        game.Script.AddTask(async () =>
        {
            game.ScreenShotAutomationEnabled = false;
            var scene = game.SceneSystem.SceneInstance.RootScene;
            var missing = new BoneShape { NodeName = "Tail", Shape = new SphereCollider() };
            var (entity, body) = AddCharacter(scene, new Vector3(2, 0, 0), ArmShape(), missing);
            entity.Transform.Scale = new Vector3(2f);

            await game.Script.NextFrame();
            // The node without a match is skipped
            Assert.Equal(1, body.BodyCount);

            var skeleton = entity.Get<ModelComponent>().Skeleton;
            int arm = NodeIndex(entity, "Arm");
            int turningFrames = 0;
            for (int frame = 1; frame < 30; frame++)
            {
                // Drawn at the end of this frame, reached by the simulation step of the next one
                var rotation = Quaternion.RotationZ(frame * 0.1f);
                skeleton.NodeTransformations[arm].Transform.Rotation = rotation;
                await game.Script.NextFrame();

                // Hips at (2, 2, 0) once scaled, the arm 0.4 m further, the box centered 0.6 m along it
                var expected = new Vector3(2.4f, 2, 0) + Vector3.Transform(new Vector3(0.6f, 0, 0), rotation);
                Assert.True(Vector3.Distance(expected, body.GetBodyPosition(0)) < 1e-3f, $"frame {frame}: {body.GetBodyPosition(0)} instead of {expected}");
                Assert.True(MathF.Abs(Quaternion.Dot(rotation, body.GetBodyOrientation(0))) > 0.9999f, $"frame {frame}: orientation differs");
                // Turning at 0.1 rad per 60th of a second; zero when a frame took a second step, already at the pose
                var angular = body.GetBodyAngularVelocity(0);
                if (angular != Vector3.Zero)
                {
                    Assert.True(Vector3.Distance(new Vector3(0, 0, 6), angular) < 0.05f, $"frame {frame}: angular velocity {angular}");
                    turningFrames++;
                }
            }

            Assert.True(turningFrames > 20, $"{turningFrames} frames with an angular velocity");

            game.Exit();
        });
        RunGameTest(game);
    }

    [Fact]
    public static void RayCastReportsTheBoneHit()
    {
        var game = new GameTest();
        game.Script.AddTask(async () =>
        {
            game.ScreenShotAutomationEnabled = false;
            var scene = game.SceneSystem.SceneInstance.RootScene;
            var leg = new BoneShape { NodeName = "Leg", Shape = new CapsuleCollider { Radius = 0.08f, Length = 0.6f, PositionLocal = new Vector3(0, -0.4f, 0) } };
            var (_, body) = AddCharacter(scene, Vector3.Zero, ArmShape(), leg);

            await game.Script.NextFrame();
            await game.Script.NextFrame();

            var simulation = body.Simulation!;
            // Down onto the arm, then sideways through the leg
            Assert.True(simulation.RayCast(new Vector3(0.5f, 3, 0), -Vector3.UnitY, 10f, out var onArm));
            Assert.Same(body, onArm.Collidable);
            Assert.Equal("Arm", body.GetBoneShape(onArm.ChildIndex).NodeName);
            Assert.Equal(NodeIndex(body.Entity, "Arm"), body.GetNodeIndex(onArm.ChildIndex));

            Assert.True(simulation.RayCast(new Vector3(-3, 0.5f, 0), Vector3.UnitX, 10f, out var onLeg));
            Assert.Same(body, onLeg.Collidable);
            Assert.Equal("Leg", body.GetBoneShape(onLeg.ChildIndex).NodeName);

            // Same answer when testing the component alone
            Assert.True(body.RayCast(new Vector3(-3, 0.5f, 0), Vector3.UnitX, 10f, out var onLegOnly));
            Assert.Equal(onLeg.ChildIndex, onLegOnly.ChildIndex);

            game.Exit();
        });
        RunGameTest(game);
    }

    [Fact]
    public static void BonesOfOneCharacterNeverTouch()
    {
        var game = new GameTest();
        game.Script.AddTask(async () =>
        {
            game.ScreenShotAutomationEnabled = false;
            var scene = game.SceneSystem.SceneInstance.RootScene;
            var counter = new BoneContactCounter();
            // Overlapping shapes on the hips and the arm
            var hips = new BoneShape { NodeName = "Hips", Shape = new SphereCollider { Radius = 0.3f } };
            var (_, body) = AddCharacter(scene, Vector3.Zero, hips, ArmShape());
            body.ContactEventHandler = counter;

            var skeleton = body.Entity.Get<ModelComponent>().Skeleton;
            int arm = NodeIndex(body.Entity, "Arm");
            for (int frame = 0; frame < 30; frame++)
            {
                skeleton.NodeTransformations[arm].Transform.Rotation = Quaternion.RotationZ(frame * 0.1f);
                await game.Script.NextFrame();
            }

            Assert.Equal(2, body.BodyCount);
            Assert.Equal(0, counter.SelfContacts);
            game.Exit();
        });
        RunGameTest(game);
    }

    [Fact]
    public static void ASwingingBonePassesItsVelocityOn()
    {
        var (ballVelocity, armVelocity) = SwingAtBall(teleport: false);
        var (_, teleportedArmVelocity) = SwingAtBall(teleport: true);

        // The center of the box turns at 6 rad/s, 0.3 m from the shoulder: the body carries that motion into contacts
        Assert.True(MathF.Abs(armVelocity.Length() - 1.8f) < 0.1f, $"arm at {armVelocity} m/s");
        Assert.Equal(Vector3.Zero, teleportedArmVelocity);
        // The arm turns counterclockwise, the ball flies off ahead of it
        Assert.True(ballVelocity.Length() > 1.5f, $"ball at {ballVelocity} m/s");
        Assert.True(Vector3.Dot(ballVelocity, Vector3.Transform(Vector3.UnitY, Quaternion.RotationZ(0.9f))) > 0f, $"ball at {ballVelocity} m/s");
    }

    /// <summary> Swings the arm of a character into a resting ball, returns the velocity of the ball afterwards and the one of the arm before the impact </summary>
    private static (Vector3 Ball, Vector3 Arm) SwingAtBall(bool teleport)
    {
        Vector3 ballVelocity = default, armVelocity = default;
        var game = new GameTest();
        game.Script.AddTask(async () =>
        {
            game.ScreenShotAutomationEnabled = false;
            var scene = game.SceneSystem.SceneInstance.RootScene;
            var (_, body) = AddCharacter(scene, Vector3.Zero, ArmShape());
            if (teleport)
                body.TeleportDistance = 0f;

            // Floating in the arc of the arm, half a meter from the shoulder
            var ball = new BodyComponent { Collider = new CompoundCollider { Colliders = { new SphereCollider { Radius = 0.1f } } }, Gravity = false };
            var ballEntity = new Entity { ball };
            ballEntity.Transform.Position = new Vector3(0.2f, 1, 0) + Vector3.Transform(new Vector3(0.5f, 0, 0), Quaternion.RotationZ(0.9f));
            scene.Entities.Add(ballEntity);

            var skeleton = body.Entity.Get<ModelComponent>().Skeleton;
            int arm = NodeIndex(body.Entity, "Arm");
            for (int frame = 0; frame < 20; frame++)
            {
                skeleton.NodeTransformations[arm].Transform.Rotation = Quaternion.RotationZ(frame * 0.1f);
                await game.Script.NextFrame();
                if (frame == 4)
                    armVelocity = body.GetBodyLinearVelocity(0);
            }

            ballVelocity = ball.LinearVelocity;
            game.Exit();
        });
        RunGameTest(game);
        return (ballVelocity, armVelocity);
    }

    private class BoneContactCounter : IContactHandler
    {
        public int SelfContacts;

        public bool NoContactResponse => false;

        public void OnStartedTouching<TManifold>(Contacts<TManifold> contacts) where TManifold : unmanaged, IContactManifold<TManifold>
        {
            if (ReferenceEquals(contacts.EventSource, contacts.Other))
                SelfContacts++;
        }
    }
}
