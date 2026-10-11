// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using BepuPhysics.CollisionDetection;
using Stride.BepuPhysics.Definitions;
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

    private static BoneShape ArmShape() => new() { NodeName = "Arm", AlongBone = false, Shape = new BoxCollider { Size = new Vector3(0.6f, 0.1f, 0.1f), PositionLocal = new Vector3(0.3f, 0, 0) } };

    private static int NodeIndex(Entity entity, string name) => Array.FindIndex(entity.Get<ModelComponent>().Skeleton.Nodes, n => n.Name == name);

    [Fact]
    public static void GeneratedCapsulesSpanEachSegment()
    {
        var shapes = BoneCollider.Generate(new Model { Skeleton = CreateSkeleton() }, 0.05f);

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
    public static void GeneratedCapsulesFollowTheDeformingBonesOnly()
    {
        // An armature node and footsteps under the root, a twist bone next to the forearm: none of them deforms the mesh or carries the chain
        var skeleton = new Skeleton
        {
            Nodes =
            [
                Node("Root", -1, Vector3.Zero),
                Node("Armature", 0, Vector3.Zero),
                Node("Hips", 1, new Vector3(0, 1, 0)),
                Node("Footsteps", 1, Vector3.Zero),
                Node("Arm", 2, new Vector3(0.2f, 0, 0)),
                Node("Forearm", 4, new Vector3(0.3f, 0, 0)),
                Node("ArmTwist", 4, new Vector3(0.15f, 0, 0)),
                Node("Hand", 5, new Vector3(0.3f, 0, 0)),
            ],
        };
        int[] deforming = [2, 4, 5, 6];
        var mesh = new Mesh { Skinning = new MeshSkinningDefinition { Bones = deforming.Select(n => new MeshBoneDefinition { NodeIndex = n, LinkToMeshMatrix = Matrix.Identity }).ToArray() } };
        var model = new Model { Skeleton = skeleton };
        model.Meshes.Add(mesh);

        var shapes = BoneCollider.Generate(model, 0.05f);

        // Hips to arm, arm to forearm, forearm to hand; nothing from the armature, the footsteps or the twist bone
        Assert.Equal(["Hips", "Arm", "Forearm"], shapes.Select(s => s.NodeName));
    }

    [Fact]
    public static void ShapesLieAlongTheirBoneByDefault()
    {
        var game = new GameTest();
        game.Script.AddTask(async () =>
        {
            game.ScreenShotAutomationEnabled = false;
            // The arm points along +X, from 0.2 m right of the hips to the hand 0.6 m further
            var (_, body) = AddCharacter(game.SceneSystem.SceneInstance.RootScene, Vector3.Zero, new BoneShape { NodeName = "Arm" });
            await game.Script.NextFrame();

            var axis = Vector3.Transform(Vector3.UnitY, body.GetBodyOrientation(0));
            Assert.True(Vector3.Distance(body.GetBodyPosition(0), new Vector3(0.5f, 1f, 0f)) < 1e-4f, $"capsule at {body.GetBodyPosition(0)}");
            Assert.True(Vector3.Distance(axis, Vector3.UnitX) < 1e-4f, $"capsule axis {axis}");
            game.Exit();
        });
        RunGameTest(game);
    }

    [Fact]
    public static void GeneratedCapsulesWeighAsWaterOfTheirVolume()
    {
        var game = new GameTest();
        game.Script.AddTask(async () =>
        {
            game.ScreenShotAutomationEnabled = false;
            var body = new SkeletonBodyComponent();
            var collider = (BoneCollider)body.Collider;
            collider.GeneratedRadius = 0.05f;
            var entity = new Entity { new ModelComponent { Model = new Model { Skeleton = CreateSkeleton() } }, body };
            game.SceneSystem.SceneInstance.RootScene.Entities.Add(entity);
            await game.Script.NextFrame();

            // The arm capsule: 0.6 m from arm to hand, caps included
            var arm = (CapsuleCollider)body.GetBoneShape(1).Shape;
            float volume = MathF.PI * 0.05f * 0.05f * (arm.Length + 4f / 3f * 0.05f);
            Assert.True(MathUtil.WithinEpsilon(1f / collider.GetCreatedInertia(1).InverseMass, 1000f * volume, 1e-3f), $"{1f / collider.GetCreatedInertia(1).InverseMass} kg");
            game.Exit();
        });
        RunGameTest(game);
    }

    [Fact]
    public static void BodiesReachThePoseDrawnLastFrame()
    {
        var game = new GameTest();
        game.Script.AddTask(async () =>
        {
            game.ScreenShotAutomationEnabled = false;
            var scene = game.SceneSystem.SceneInstance.RootScene;
            var missing = new BoneShape { NodeName = "Tail", AlongBone = false, Shape = new SphereCollider() };
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
            var leg = new BoneShape { NodeName = "Leg", AlongBone = false, Shape = new CapsuleCollider { Radius = 0.08f, Length = 0.6f, PositionLocal = new Vector3(0, -0.4f, 0) } };
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
            var hips = new BoneShape { NodeName = "Hips", AlongBone = false, Shape = new SphereCollider { Radius = 0.3f } };
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

    // Hips, arm and leg of the test skeleton as dynamic bodies 0, 1 and 2
    private static BoneShape[] RagdollShapes() =>
    [
        new() { NodeName = "Hips", Shape = new BoxCollider { Size = new Vector3(0.3f, 0.2f, 0.2f), Mass = 4f } },
        ArmShape(),
        new() { NodeName = "Leg", Shape = new CapsuleCollider { Radius = 0.08f, Length = 0.6f, PositionLocal = new Vector3(0, -0.4f, 0), Mass = 2f } },
    ];

    private static Matrix World(Entity entity, string node) => entity.Get<ModelComponent>().Skeleton.NodeTransformations[NodeIndex(entity, node)].WorldMatrix;

    /// <summary> How far the child node is from where its parent node puts it, the separation of the joint between them </summary>
    private static float JointSeparation(Entity entity, string parent, string child)
    {
        var position = entity.Get<ModelComponent>().Skeleton.Nodes[NodeIndex(entity, child)].Transform.Position;
        return Vector3.Distance(World(entity, child).TranslationVector, Vector3.TransformCoordinate(position, World(entity, parent)));
    }

    /// <summary> The swing and twist angles of the child node relative to its parent around <paramref name="axis"/>, both identity at rest </summary>
    private static (float Swing, float Twist) JointAngles(Entity entity, string parent, string child, Vector3 axis)
    {
        World(entity, parent).Decompose(out _, out Quaternion parentRotation, out _);
        World(entity, child).Decompose(out _, out Quaternion childRotation, out _);
        var relative = childRotation * Quaternion.Invert(parentRotation);
        if (relative.W < 0f)
            relative = -relative;
        float swing = MathF.Acos(Math.Clamp(Vector3.Dot(axis, Vector3.Transform(axis, relative)), -1f, 1f));
        float twist = 2f * MathF.Atan2(Vector3.Dot(new Vector3(relative.X, relative.Y, relative.Z), axis), relative.W);
        return (swing, MathF.Abs(twist));
    }

    [Fact]
    public static void RagdollFallsAndRestsOnTheGround()
    {
        var game = new GameTest();
        game.Script.AddTask(async () =>
        {
            game.ScreenShotAutomationEnabled = false;
            var scene = game.SceneSystem.SceneInstance.RootScene;
            var ground = new Entity { new StaticComponent { Collider = new CompoundCollider { Colliders = { new BoxCollider { Size = new Vector3(20, 1, 20) } } } } };
            ground.Transform.Position = new Vector3(0, -0.5f, 0);
            scene.Entities.Add(ground);
            var (entity, body) = AddCharacter(scene, new Vector3(0, 1, 0), RagdollShapes());
            // The arm ends up lower than the hips, its limits are reached
            entity.Transform.Rotation = Quaternion.RotationX(0.3f);

            await game.Script.NextFrame();
            body.Mode = SkeletonBodyMode.Ragdoll;
            for (int frame = 0; frame < 240; frame++)
                await game.Script.NextFrame();

            for (int i = 0; i < body.BodyCount; i++)
            {
                Assert.True(body.GetBodyPosition(i).Y > 0.04f, $"body {i} at {body.GetBodyPosition(i)}, below the ground");
                Assert.True(body.GetBodyLinearVelocity(i).Length() < 0.05f, $"body {i} still moving at {body.GetBodyLinearVelocity(i)}");
            }

            Assert.True(body.GetBodyPosition(0).Y < 0.5f, $"hips at {body.GetBodyPosition(0)}, did not fall");
            // At rest, the ragdoll falls asleep
            Assert.Equal(0, body.Simulation!.Simulation.Bodies.ActiveSet.Count);
            Assert.True(JointSeparation(entity, "Hips", "Arm") < 0.01f, $"arm joint apart by {JointSeparation(entity, "Hips", "Arm")}");
            Assert.True(JointSeparation(entity, "Hips", "Leg") < 0.01f, $"leg joint apart by {JointSeparation(entity, "Hips", "Leg")}");
            var limits = new BoneJoint();
            foreach (var (node, axis) in new[] { ("Arm", Vector3.UnitX), ("Leg", -Vector3.UnitY) })
            {
                var (swing, twist) = JointAngles(entity, "Hips", node, axis);
                Assert.True(swing < limits.MaximumSwingAngle + 0.05f, $"{node} swings by {swing} rad");
                Assert.True(twist < limits.MaximumTwistAngle + 0.05f, $"{node} twists by {twist} rad");
            }

            game.Exit();
        });
        RunGameTest(game);
    }

    [Fact]
    public static void GeneratedRagdollHoldsTogether()
    {
        var game = new GameTest();
        game.Script.AddTask(async () =>
        {
            game.ScreenShotAutomationEnabled = false;
            var scene = game.SceneSystem.SceneInstance.RootScene;
            var ground = new Entity { new StaticComponent { Collider = new CompoundCollider { Colliders = { new BoxCollider { Size = new Vector3(20, 1, 20) } } } } };
            ground.Transform.Position = new Vector3(0, -0.5f, 0);
            scene.Entities.Add(ground);
            var body = new SkeletonBodyComponent { Mode = SkeletonBodyMode.Ragdoll };
            ((BoneCollider)body.Collider).GeneratedRadius = 0.05f;
            var entity = new Entity { new ModelComponent { Model = new Model { Skeleton = CreateSkeleton() } }, body };
            entity.Transform.Position = new Vector3(0, 0.5f, 0);
            entity.Transform.Rotation = Quaternion.RotationZ(0.5f);
            scene.Entities.Add(entity);

            for (int frame = 0; frame < 180; frame++)
                await game.Script.NextFrame();

            // Two capsules start from the hips: the second one is welded to the first, the arm and the leg are joined to the first
            Assert.Equal(4, body.BodyCount);
            Assert.Equal("Hips", body.GetBoneShape(2).NodeName);
            float restDistance = Vector3.Distance(new Vector3(0.1f, 0, 0), new Vector3(0, -0.05f, 0));
            Assert.True(MathF.Abs(Vector3.Distance(body.GetBodyPosition(0), body.GetBodyPosition(2)) - restDistance) < 0.01f, "the capsules of the hips came apart");
            Assert.True(body.GetBodyPosition(0).Y < 0.5f, $"hips at {body.GetBodyPosition(0)}, did not fall");
            Assert.True(JointSeparation(entity, "Hips", "Arm") < 0.01f, $"arm joint apart by {JointSeparation(entity, "Hips", "Arm")}");
            Assert.True(JointSeparation(entity, "Hips", "Leg") < 0.01f, $"leg joint apart by {JointSeparation(entity, "Hips", "Leg")}");

            game.Exit();
        });
        RunGameTest(game);
    }

    [Fact]
    public static void TheSkeletonFollowsTheRagdoll()
    {
        var game = new GameTest();
        game.Script.AddTask(async () =>
        {
            game.ScreenShotAutomationEnabled = false;
            var scene = game.SceneSystem.SceneInstance.RootScene;
            var (entity, body) = AddCharacter(scene, Vector3.Zero, RagdollShapes());
            entity.Transform.Scale = new Vector3(1.5f);
            body.Rebuild();
            body.Mode = SkeletonBodyMode.Ragdoll;
            body.BlendDuration = 0f;

            var skeleton = entity.Get<ModelComponent>().Skeleton;
            for (int frame = 0; frame < 20; frame++)
                await game.Script.NextFrame();
            // The step runs before drawing, pausing it lets the nodes without a body catch up
            body.Simulation!.Enabled = false;
            await game.Script.NextFrame();

            // Falling: the arm node sits where its body puts it, scaled, the hand without a body follows the arm
            Assert.True(body.GetBodyPosition(1).Y < 1.4f, $"arm at {body.GetBodyPosition(1)}, did not fall");
            var arm = World(entity, "Arm");
            var expected = body.GetBodyPosition(1) - Vector3.Transform(new Vector3(0.3f * 1.5f, 0, 0), body.GetBodyOrientation(1));
            Assert.True(Vector3.Distance(expected, arm.TranslationVector) < 1e-3f, $"arm node at {arm.TranslationVector} instead of {expected}");
            Assert.True(Vector3.Distance(Vector3.TransformCoordinate(new Vector3(0.6f, 0, 0), arm), World(entity, "Hand").TranslationVector) < 1e-3f, "the hand does not follow the arm");
            Assert.True(MathF.Abs(arm.Right.Length() - 1.5f) < 1e-3f, $"arm node scaled by {arm.Right.Length()}");
            Assert.True(skeleton.NodeTransformations[NodeIndex(entity, "Arm")].Flags.HasFlag(ModelNodeFlags.OverrideWorldMatrix));

            body.Mode = SkeletonBodyMode.Animated;
            body.Simulation.Enabled = true;
            await game.Script.NextFrame();
            Assert.False(skeleton.NodeTransformations[NodeIndex(entity, "Arm")].Flags.HasFlag(ModelNodeFlags.OverrideWorldMatrix));
            Assert.True(Vector3.Distance(new Vector3(0.3f, 1.5f, 0), World(entity, "Arm").TranslationVector) < 1e-3f, "the arm node is not back to the animation");

            game.Exit();
        });
        RunGameTest(game);
    }

    [Fact]
    public static void LeavingTheAnimationKeepsTheMomentum()
    {
        var game = new GameTest();
        game.Script.AddTask(async () =>
        {
            game.ScreenShotAutomationEnabled = false;
            var scene = game.SceneSystem.SceneInstance.RootScene;
            var (entity, body) = AddCharacter(scene, Vector3.Zero, RagdollShapes());
            body.Simulation!.PoseGravity = Vector3.Zero;
            body.Simulation.PoseLinearDamping = 0f;

            // The animated character runs at 3 m/s
            var velocity = new Vector3(3f, 0, 0);
            for (int frame = 0; frame < 10; frame++)
            {
                entity.Transform.Position += velocity / 60f;
                await game.Script.NextFrame();
            }

            body.Mode = SkeletonBodyMode.Ragdoll;
            for (int frame = 0; frame < 20; frame++)
                await game.Script.NextFrame();

            for (int i = 0; i < body.BodyCount; i++)
                Assert.True(Vector3.Distance(velocity, body.GetBodyLinearVelocity(i)) < 0.05f, $"body {i} at {body.GetBodyLinearVelocity(i)} m/s");

            game.Exit();
        });
        RunGameTest(game);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public static void OnlyBonesWhichAreNotJoinedCollide(bool legOverlapsArm, bool selfCollision)
    {
        float fastest = float.NaN;
        var game = new GameTest();
        game.Script.AddTask(async () =>
        {
            game.ScreenShotAutomationEnabled = false;
            var scene = game.SceneSystem.SceneInstance.RootScene;
            // The hips overlap the arm and the leg they are joined to; the wide leg also overlaps the arm
            var hips = new BoneShape { NodeName = "Hips", Shape = new SphereCollider { Radius = 0.3f } };
            var leg = legOverlapsArm
                ? new BoneShape { NodeName = "Leg", Shape = new BoxCollider { Size = new Vector3(0.8f, 0.3f, 0.3f), PositionLocal = new Vector3(0.4f, 0, 0) } }
                : RagdollShapes()[2];
            var (_, body) = AddCharacter(scene, Vector3.Zero, hips, ArmShape(), leg);
            body.Simulation!.PoseGravity = Vector3.Zero;
            body.SelfCollision = selfCollision;
            body.Mode = SkeletonBodyMode.Ragdoll;

            for (int frame = 0; frame < 30; frame++)
                await game.Script.NextFrame();

            // Overlapping bodies which collide push each other apart, the others stay at rest
            fastest = Enumerable.Range(0, body.BodyCount).Max(i => body.GetBodyLinearVelocity(i).Length() + body.GetBodyAngularVelocity(i).Length());
            game.Exit();
        });
        RunGameTest(game);

        if (legOverlapsArm && selfCollision)
            Assert.True(fastest > 0.1f, $"bodies at {fastest} m/s, the leg and the arm did not collide");
        else
            Assert.True(fastest < 1e-3f, $"bodies at {fastest} m/s, bones which should not collide did");
    }

    [Theory]
    [InlineData(SkeletonBodyMode.Active)]
    [InlineData(SkeletonBodyMode.Ragdoll)]
    public static void ActiveRagdollReachesTheAnimatedPose(SkeletonBodyMode mode)
    {
        float angle = float.NaN;
        var game = new GameTest();
        game.Script.AddTask(async () =>
        {
            game.ScreenShotAutomationEnabled = false;
            var scene = game.SceneSystem.SceneInstance.RootScene;
            var (entity, body) = AddCharacter(scene, Vector3.Zero, RagdollShapes());
            body.Simulation!.PoseGravity = Vector3.Zero;
            await game.Script.NextFrame();

            // The animation raises the arm while the bodies are still at rest
            var target = Quaternion.RotationZ(0.6f);
            entity.Get<ModelComponent>().Skeleton.NodeTransformations[NodeIndex(entity, "Arm")].Transform.Rotation = target;
            body.Mode = mode;
            for (int frame = 0; frame < 90; frame++)
                await game.Script.NextFrame();

            var relative = body.GetBodyOrientation(1) * Quaternion.Invert(body.GetBodyOrientation(0));
            angle = 2f * MathF.Acos(MathF.Min(1f, MathF.Abs(Quaternion.Dot(relative, target))));
            game.Exit();
        });
        RunGameTest(game);

        if (mode == SkeletonBodyMode.Active)
            Assert.True(angle < 0.05f, $"{angle} rad from the animated pose");
        else
            Assert.True(angle > 0.5f, $"{angle} rad from the animated pose without servos");
    }

    private static Entity AddGround(Scene scene)
    {
        var ground = new Entity { new StaticComponent { Collider = new CompoundCollider { Colliders = { new BoxCollider { Size = new Vector3(20, 1, 20) } } } } };
        ground.Transform.Position = new Vector3(0, -0.5f, 0);
        scene.Entities.Add(ground);
        return ground;
    }

    /// <summary> The angle between the hips body and its upright rest orientation </summary>
    private static float HipsTilt(SkeletonBodyComponent body) => 2f * MathF.Acos(MathF.Min(1f, MathF.Abs(body.GetBodyOrientation(0).W)));

    [Theory]
    [InlineData(SkeletonBodyMode.Standing)]
    [InlineData(SkeletonBodyMode.Active)]
    public static void StandingHoldsTheCharacterUp(SkeletonBodyMode mode)
    {
        Vector3 hips = default;
        float tilt = float.NaN;
        var game = new GameTest();
        game.Script.AddTask(async () =>
        {
            game.ScreenShotAutomationEnabled = false;
            var scene = game.SceneSystem.SceneInstance.RootScene;
            AddGround(scene);
            // The leg ends 0.12 m above the ground: without the root servos the stiff leg props the hips up for a while, then falls
            var (_, body) = AddCharacter(scene, Vector3.Zero, RagdollShapes());
            await game.Script.NextFrame();
            body.Mode = mode;
            for (int frame = 0; frame < 180; frame++)
                await game.Script.NextFrame();

            hips = body.GetBodyPosition(0);
            tilt = HipsTilt(body);
            game.Exit();
        });
        RunGameTest(game);

        if (mode == SkeletonBodyMode.Standing)
        {
            Assert.True(MathF.Abs(hips.Y - 1f) < 0.06f, $"hips at {hips}, not at their animated height");
            Assert.True(tilt < 0.1f, $"hips tilted by {tilt} rad");
        }
        else
        {
            Assert.True(hips.Y < 0.8f, $"hips at {hips}, an active ragdoll without root servo should fall");
        }
    }

    [Fact]
    public static void StandingRecoversFromAHit()
    {
        var game = new GameTest();
        game.Script.AddTask(async () =>
        {
            game.ScreenShotAutomationEnabled = false;
            var scene = game.SceneSystem.SceneInstance.RootScene;
            AddGround(scene);
            var (_, body) = AddCharacter(scene, Vector3.Zero, RagdollShapes());
            body.Mode = SkeletonBodyMode.Standing;
            for (int frame = 0; frame < 60; frame++)
                await game.Script.NextFrame();

            // A 20 kg ball at 12 m/s into the hips
            var ball = new BodyComponent { Collider = new CompoundCollider { Colliders = { new SphereCollider { Radius = 0.15f, Mass = 20f } } } };
            var ballEntity = new Entity { ball };
            ballEntity.Transform.Position = new Vector3(-1.5f, 1f, 0.05f);
            scene.Entities.Add(ballEntity);
            await game.Script.NextFrame();
            ball.LinearVelocity = new Vector3(12f, 0, 0);

            float worstTilt = 0f, lowest = float.MaxValue;
            for (int frame = 0; frame < 60; frame++)
            {
                await game.Script.NextFrame();
                worstTilt = MathF.Max(worstTilt, HipsTilt(body));
                lowest = MathF.Min(lowest, body.GetBodyPosition(0).Y);
            }
            Assert.True(worstTilt > 0.3f || lowest < 0.8f, $"the hit did not knock the character: tilt {worstTilt} rad, lowest {lowest} m");

            // Within three seconds, upright at its standing height again, wherever it was pushed
            for (int frame = 0; frame < 180; frame++)
                await game.Script.NextFrame();
            Assert.True(MathF.Abs(body.GetBodyPosition(0).Y - 1f) < 0.06f, $"hips at {body.GetBodyPosition(0)}");
            Assert.True(HipsTilt(body) < 0.1f, $"hips tilted by {HipsTilt(body)} rad");

            game.Exit();
        });
        RunGameTest(game);
    }

    // A small humanoid of 46 kg: hips at 1 m, chest, head, two legs reaching down to 5 cm above the ground
    private static Skeleton CreateHumanoid() => new()
    {
        Nodes =
        [
            Node("Root", -1, Vector3.Zero),
            Node("Hips", 0, new Vector3(0, 1, 0)),
            Node("Chest", 1, new Vector3(0, 0.15f, 0)),
            Node("Head", 2, new Vector3(0, 0.4f, 0)),
            Node("Leg.L", 1, new Vector3(0.1f, -0.05f, 0)),
            Node("Leg.R", 1, new Vector3(-0.1f, -0.05f, 0)),
        ],
    };

    private static BoneShape[] HumanoidShapes() =>
    [
        new() { NodeName = "Hips", Shape = new BoxCollider { Size = new Vector3(0.3f, 0.2f, 0.2f), Mass = 10f } },
        new() { NodeName = "Chest", Shape = new BoxCollider { Size = new Vector3(0.35f, 0.35f, 0.2f), PositionLocal = new Vector3(0, 0.2f, 0), Mass = 15f } },
        new() { NodeName = "Head", Shape = new SphereCollider { Radius = 0.11f, PositionLocal = new Vector3(0, 0.12f, 0), Mass = 5f } },
        new() { NodeName = "Leg.L", Shape = new CapsuleCollider { Radius = 0.07f, Length = 0.76f, PositionLocal = new Vector3(0, -0.45f, 0), Mass = 8f } },
        new() { NodeName = "Leg.R", Shape = new CapsuleCollider { Radius = 0.07f, Length = 0.76f, PositionLocal = new Vector3(0, -0.45f, 0), Mass = 8f } },
    ];

    [Theory]
    [InlineData(-MathF.PI / 2f, false)]
    [InlineData(MathF.PI / 2f, false)]
    [InlineData(-MathF.PI / 2f, true)]
    [InlineData(MathF.PI / 2f, true)]
    public static void StandingGetsUpFromTheGround(float lyingAngle, bool walking)
    {
        float hips = float.NaN, head = float.NaN, tilt = float.NaN, chestTilt = float.NaN;
        var game = new GameTest();
        game.Script.AddTask(async () =>
        {
            game.ScreenShotAutomationEnabled = false;
            var scene = game.SceneSystem.SceneInstance.RootScene;
            AddGround(scene);
            // Built lying on its back (or its belly) as a ragdoll, which settles on the ground
            var body = new SkeletonBodyComponent { Mode = SkeletonBodyMode.Ragdoll };
            ((BoneCollider)body.Collider).Bones.AddRange(HumanoidShapes());
            var entity = new Entity { new ModelComponent { Model = new Model { Skeleton = CreateHumanoid() } }, body };
            entity.Transform.Position = new Vector3(0, 0.15f, 1f);
            entity.Transform.Rotation = Quaternion.RotationX(lyingAngle);
            scene.Entities.Add(entity);
            for (int frame = 0; frame < 60; frame++)
                await game.Script.NextFrame();
            Assert.True(body.GetBodyPosition(2).Y < 0.3f, $"head at {body.GetBodyPosition(2)}, the character is not lying");

            // The entity, which gives the animated pose, stands upright where the character lies; it never goes through Animated
            entity.Transform.Position = new Vector3(body.GetBodyPosition(0).X, 0, body.GetBodyPosition(0).Z);
            entity.Transform.Rotation = Quaternion.Identity;
            body.Mode = SkeletonBodyMode.Standing;
            var nodes = entity.Get<ModelComponent>().Skeleton.NodeTransformations;
            for (int frame = 0; frame < 240; frame++)
            {
                // The legs keep walking in place, swinging 0.55 rad back and forth, feet against the ground
                if (walking)
                {
                    float swing = 0.55f * MathF.Sin(frame / 60f * 0.8f * MathF.Tau);
                    nodes[NodeIndex(entity, "Leg.L")].Transform.Rotation = Quaternion.RotationX(swing);
                    nodes[NodeIndex(entity, "Leg.R")].Transform.Rotation = Quaternion.RotationX(-swing);
                }
                await game.Script.NextFrame();
            }

            hips = body.GetBodyPosition(0).Y;
            head = body.GetBodyPosition(2).Y;
            tilt = HipsTilt(body);
            chestTilt = 2f * MathF.Acos(MathF.Min(1f, MathF.Abs(body.GetBodyOrientation(1).W)));
            game.Exit();
        });
        RunGameTest(game);

        // Upright: the hips at their animated height, the hips and the chest within 6 degrees of vertical
        string state = $"hips at {hips} m tilted {tilt} rad, chest tilted {chestTilt} rad, head at {head} m";
        Assert.True(MathF.Abs(hips - 1f) < 0.08f, state);
        Assert.True(head > hips + 0.5f, state);
        Assert.True(tilt < 0.1f && chestTilt < 0.1f, state);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static void StandingDoesNotStandOnItsOwnCollidables(bool onChild)
    {
        Vector3 hips = default;
        CollidableComponent? ground = null, floor = null;
        var game = new GameTest();
        game.Script.AddTask(async () =>
        {
            game.ScreenShotAutomationEnabled = false;
            var scene = game.SceneSystem.SceneInstance.RootScene;
            floor = AddGround(scene).Get<StaticComponent>();

            // A plate under the hips belonging to the character, on its entity or a child, which the bones go through
            var (entity, body) = AddHumanoid(scene, Vector3.Zero, Quaternion.Identity, SkeletonBodyMode.Animated);
            var plate = new StaticComponent { CollisionLayer = CollisionLayer.Layer1, Collider = new CompoundCollider { Colliders = { new BoxCollider { Size = new Vector3(1f, 0.05f, 1f), PositionLocal = new Vector3(0, 0.6f, 0) } } } };
            if (onChild)
                entity.AddChild(new Entity { plate });
            else
                entity.Add(plate);
            await game.Script.NextFrame();
            body.Simulation!.CollisionMatrix.Set(CollisionLayer.Layer0, CollisionLayer.Layer1, false);

            body.Mode = SkeletonBodyMode.Standing;
            for (int frame = 0; frame < 180; frame++)
                await game.Script.NextFrame();

            hips = body.GetBodyPosition(0);
            ground = body.Ground;
            game.Exit();
        });
        RunGameTest(game);

        Assert.Same(floor, ground);
        Assert.True(MathF.Abs(hips.Y - 1f) < 0.08f, $"hips at {hips}, not at their animated height above the floor");
    }

    [Fact]
    public static void GhostBonesCollideWithNothing()
    {
        Vector3 hips = default;
        CollidableComponent? hit = null, floor = null;
        var game = new GameTest();
        game.Script.AddTask(async () =>
        {
            game.ScreenShotAutomationEnabled = false;
            var scene = game.SceneSystem.SceneInstance.RootScene;
            floor = AddGround(scene).Get<StaticComponent>();
            var (_, body) = AddHumanoid(scene, new Vector3(0, 0.5f, 0), Quaternion.Identity, SkeletonBodyMode.Animated);
            body.Ghost = true;
            await game.Script.NextFrame();

            // Rays go through the bones to the floor, and the ragdoll falls through it
            body.Simulation!.RayCast(new Vector3(0, 3f, 0), -Vector3.UnitY, 10f, out var result);
            hit = result.Collidable;
            body.Mode = SkeletonBodyMode.Ragdoll;
            for (int frame = 0; frame < 60; frame++)
                await game.Script.NextFrame();
            hips = body.GetBodyPosition(0);
            game.Exit();
        });
        RunGameTest(game);

        Assert.Same(floor, hit);
        Assert.True(hips.Y < -1f, $"hips at {hips}, the bones should fall through the floor");
    }

    private static (Entity Entity, SkeletonBodyComponent Body) AddHumanoid(Scene scene, Vector3 position, Quaternion rotation, SkeletonBodyMode mode)
    {
        var body = new SkeletonBodyComponent { Mode = mode };
        ((BoneCollider)body.Collider).Bones.AddRange(HumanoidShapes());
        var entity = new Entity { new ModelComponent { Model = new Model { Skeleton = CreateHumanoid() } }, body };
        entity.Transform.Position = position;
        entity.Transform.Rotation = rotation;
        scene.Entities.Add(entity);
        return (entity, body);
    }

    [Fact]
    public static void StandingFallsFreelyFromHigh()
    {
        float landing = float.NaN;
        var game = new GameTest();
        game.Script.AddTask(async () =>
        {
            game.ScreenShotAutomationEnabled = false;
            var scene = game.SceneSystem.SceneInstance.RootScene;
            AddGround(scene);
            // Standing, 5 m above the ground: nothing within the reach of its legs
            var (_, body) = AddHumanoid(scene, new Vector3(0, 5, 0), Quaternion.Identity, SkeletonBodyMode.Standing);
            for (int frame = 1; frame <= 180 && float.IsNaN(landing); frame++)
            {
                await game.Script.NextFrame();
                if (frame == 20)
                    Assert.False(body.IsGrounded, $"grounded with the hips at {body.GetBodyPosition(0)}");
                if (body.GetBodyPosition(0).Y < 1.2f)
                    landing = frame / 60f;
            }
            game.Exit();
        });
        RunGameTest(game);

        // The hips fall 4.8 m: 0.99 s in free fall
        Assert.True(landing < 1.3f, $"hips reached the ground after {landing} s, the fall is braked");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public static void StandingStandsOnWhatIsUnderIt(bool dynamicCrate)
    {
        Vector3 hips = default, crateMoved = default;
        bool grounded = false;
        CollidableComponent? ground = null;
        CollidableComponent? crate = null;
        var game = new GameTest();
        game.Script.AddTask(async () =>
        {
            game.ScreenShotAutomationEnabled = false;
            var scene = game.SceneSystem.SceneInstance.RootScene;
            AddGround(scene);
            // A 1 x 0.5 x 1 m crate, 200 kg when dynamic
            var collider = new CompoundCollider { Colliders = { new BoxCollider { Size = new Vector3(1f, 0.5f, 1f), Mass = 200f } } };
            crate = dynamicCrate ? new BodyComponent { Collider = collider } : new StaticComponent { Collider = collider };
            var crateEntity = new Entity { crate };
            crateEntity.Transform.Position = new Vector3(0, 0.25f, 0);
            scene.Entities.Add(crateEntity);

            // Lying across the crate as a ragdoll, hips above its center
            var (entity, body) = AddHumanoid(scene, new Vector3(0, 0.62f, 1f), Quaternion.RotationX(-MathF.PI / 2f), SkeletonBodyMode.Ragdoll);
            for (int frame = 0; frame < 60; frame++)
                await game.Script.NextFrame();
            var crateStart = crate.Entity.Transform.WorldMatrix.TranslationVector;

            // The entity stands upright on the floor beside the crate: the height comes from what is under the hips
            entity.Transform.Position = new Vector3(body.GetBodyPosition(0).X, 0, body.GetBodyPosition(0).Z);
            entity.Transform.Rotation = Quaternion.Identity;
            body.Mode = SkeletonBodyMode.Standing;
            for (int frame = 0; frame < 240; frame++)
                await game.Script.NextFrame();

            hips = body.GetBodyPosition(0);
            grounded = body.IsGrounded;
            ground = body.Ground;
            crateMoved = crate.Entity.Transform.WorldMatrix.TranslationVector - crateStart;
            game.Exit();
        });
        RunGameTest(game);

        Assert.True(MathF.Abs(hips.Y - 1.5f) < 0.1f, $"hips at {hips}, not standing on the crate (1.5 m)");
        Assert.True(crateMoved.Length() < 0.05f, $"the crate moved by {crateMoved}");
        Assert.True(grounded);
        Assert.Same(crate, ground);
    }

    [Theory]
    [InlineData(0.5f, false)]
    [InlineData(3f, true)]
    public static void StandingLiftIsLimitedByItsStrength(float strength, bool stands)
    {
        float height = float.NaN;
        var game = new GameTest();
        game.Script.AddTask(async () =>
        {
            game.ScreenShotAutomationEnabled = false;
            var scene = game.SceneSystem.SceneInstance.RootScene;
            AddGround(scene);
            // The leg ends 0.12 m above the ground: the lift alone carries the weight
            var (_, body) = AddCharacter(scene, Vector3.Zero, RagdollShapes());
            body.StandingStrength = strength;
            body.Mode = SkeletonBodyMode.Standing;
            for (int frame = 0; frame < 180; frame++)
                await game.Script.NextFrame();

            height = body.GetBodyPosition(0).Y;
            game.Exit();
        });
        RunGameTest(game);

        if (stands)
            Assert.True(MathF.Abs(height - 1f) < 0.06f, $"hips at {height} m with a strength of {strength}");
        else
            Assert.True(height < 0.93f, $"hips at {height} m: a strength of {strength} cannot carry the weight");
    }

    [Fact]
    public static void ReturningToTheAnimationBlends()
    {
        var game = new GameTest();
        game.Script.AddTask(async () =>
        {
            game.ScreenShotAutomationEnabled = false;
            var scene = game.SceneSystem.SceneInstance.RootScene;
            var ground = new Entity { new StaticComponent { Collider = new CompoundCollider { Colliders = { new BoxCollider { Size = new Vector3(20, 1, 20) } } } } };
            ground.Transform.Position = new Vector3(0, -0.5f, 0);
            scene.Entities.Add(ground);
            var (entity, body) = AddCharacter(scene, Vector3.Zero, RagdollShapes());
            body.Mode = SkeletonBodyMode.Ragdoll;
            for (int frame = 0; frame < 90; frame++)
                await game.Script.NextFrame();

            // Lying on the ground, then back to the animated pose over 0.25 s
            var animatedHips = new Vector3(0, 1, 0);
            float distance = Vector3.Distance(animatedHips, World(entity, "Hips").TranslationVector);
            Assert.True(distance > 0.5f, $"hips {distance} m from the animated pose before blending");
            body.Mode = SkeletonBodyMode.Animated;
            var skeleton = entity.Get<ModelComponent>().Skeleton;
            int blendingFrames = 0;
            for (int frame = 0; frame < 20; frame++)
            {
                await game.Script.NextFrame();
                float next = Vector3.Distance(animatedHips, World(entity, "Hips").TranslationVector);
                if (next > 1e-4f)
                {
                    Assert.True(next < distance, $"frame {frame}: hips {next} m from the animated pose, was {distance}");
                    blendingFrames++;
                }

                distance = next;
            }

            Assert.InRange(blendingFrames, 13, 15);
            Assert.True(distance < 1e-4f);
            Assert.False(skeleton.NodeTransformations[NodeIndex(entity, "Hips")].Flags.HasFlag(ModelNodeFlags.OverrideWorldMatrix));
            // The kinematic bodies are back on their nodes
            Assert.True(Vector3.Distance(new Vector3(0.5f, 1, 0), body.GetBodyPosition(1)) < 1e-3f, $"arm body at {body.GetBodyPosition(1)}");

            game.Exit();
        });
        RunGameTest(game);
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
