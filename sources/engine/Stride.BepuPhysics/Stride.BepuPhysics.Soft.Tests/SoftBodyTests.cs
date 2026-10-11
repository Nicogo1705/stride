// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Linq;
using System.Threading.Tasks;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using Stride.BepuPhysics.Definitions;
using Stride.BepuPhysics.Definitions.Colliders;
using Stride.BepuPhysics.Definitions.Contacts;
using Stride.BepuPhysics.Soft.Topology;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics.GeometricPrimitives;
using Stride.Graphics.Regression;
using Stride.Rendering;
using Stride.Rendering.ProceduralModels;
using Xunit;

namespace Stride.BepuPhysics.Soft.Tests;

public class SoftBodyTests : GameTestBase
{
    [Fact]
    public static void VolumetricBodyFallsAndRests()
    {
        RunScene(async scene =>
        {
            var body = new VolumetricSoftBodyComponent { Resolution = 4 };
            var entity = scene.Add(body, new CubeProceduralModel(), new Vector3(0f, 2f, 0f));
            Assert.Equal(5 * 5 * 5, body.ParticleCount);

            await Seconds(body, 3f);

            // Resting on the floor, slightly squashed by its own weight
            Assert.InRange(entity.Transform.Position.Y, 0.4f, 0.55f);
            Assert.True(Vector3.Distance(Vector3.Transform(Vector3.UnitY, entity.Transform.Rotation), Vector3.UnitY) < 0.05f);
            for (int i = 0; i < body.ParticleCount; i++)
                Assert.True(body.GetParticlePosition(i).Y > -0.01f);
        });
    }

    [Fact]
    public static void LatticeKeepsTheSolverBatchesBelowTheFallbackThreshold()
    {
        RunScene(async scene =>
        {
            // Every particle of a body is constrained to its neighbors, each constraint of a particle needs a batch of its own
            var body = new VolumetricSoftBodyComponent { Resolution = 6 };
            scene.Add(body, new CubeProceduralModel(), new Vector3(0f, 0.6f, 0f));
            await Seconds(body, 0.3f);

            var solver = body.Simulation!.Simulation.Solver;
            int most = Enumerable.Range(0, body.ParticleCount).Max(i => body.Particle(i).Constraints.Count);
            Assert.True(most <= 40, $"A particle is in {most} constraints");
            Assert.True(solver.ActiveSet.Batches.Count <= solver.FallbackBatchThreshold, $"{solver.ActiveSet.Batches.Count} batches");
        });
    }

    [Fact]
    public static void ClothHangsFromItsPins()
    {
        RunScene(async scene =>
        {
            // Pinned along the edge at x = -0.5
            var edge = new BoundingBox(new Vector3(-0.51f, -1f, -1f), new Vector3(-0.49f, 1f, 1f));
            var cloth = new ClothComponent { Pins = { new SoftBodyPin { Region = edge } } };
            scene.Add(cloth, new PlaneProceduralModel { Size = new Vector2(1f), Tessellation = new Int2(10), Normal = NormalDirection.UpY, GenerateBackFace = true }, new Vector3(0f, 2f, 0f));
            Assert.Equal(11 * 11, cloth.ParticleCount);

            // It swings like a pendulum around the pinned edge, which never moves, and the free edge comes down to about a meter below it
            var lowest = float.MaxValue;
            var simulation = cloth.Simulation!;
            for (int step = 0; step < 120; step++)
            {
                await simulation.AfterUpdate();
                for (int i = 0; i < cloth.ParticleCount; i++)
                {
                    var position = cloth.GetParticlePosition(i);
                    // Pinned particles are held by a stiff spring, they give a little under the weight of the cloth
                    if (cloth.IsParticlePinned(i))
                        Assert.InRange(position.Y, 1.97f, 2.03f);
                    else
                        Assert.True(position.Y < 2.05f, $"Particle {i} at {position}");
                    lowest = MathF.Min(lowest, position.Y);
                }
            }
            Assert.InRange(lowest, 0.9f, 1.1f);
        });
    }

    [Fact]
    public static void RaysAndContactsReportTheSoftBody()
    {
        RunScene(async scene =>
        {
            var events = new CountingHandler();
            var body = new VolumetricSoftBodyComponent { Resolution = 3, ContactEventHandler = events };
            scene.Add(body, new CubeProceduralModel(), new Vector3(0f, 1f, 0f));

            await Seconds(body, 1.5f);

            Assert.True(body.Simulation!.RayCast(new Vector3(0f, 5f, 0f), -Vector3.UnitY, 10f, out var hit));
            Assert.Same(body, hit.Collidable);
            Assert.True(body.RayCast(new Vector3(5f, 0.3f, 0f), -Vector3.UnitX, 10f, out hit));
            Assert.Same(body, hit.Collidable);

            // Touching the floor, its own particles touching each other do not count
            Assert.NotEmpty(events.Others);
            Assert.All(events.Others, other => Assert.IsType<StaticComponent>(other));
        });
    }

    [Fact]
    public static void OneTouchingEventPerStepWithNormalsTowardsTheBody()
    {
        RunScene(async scene =>
        {
            var events = new TouchingHandler();
            var body = new VolumetricSoftBodyComponent { Resolution = 3, ContactEventHandler = events, SleepThreshold = 0f };
            scene.Add(body, new CubeProceduralModel(), new Vector3(0f, 1f, 0f));

            await Seconds(body, 1.5f);
            for (int step = 0; step < 10; step++)
            {
                events.Calls = 0;
                await body.Simulation!.AfterUpdate();
                Assert.Equal(1, events.Calls);
            }

            // Resting on the floor through several particles, every normal points from the floor up to the body
            Assert.True(events.Groups > 1);
            Assert.All(events.Normals, normal => Assert.True(normal.Y > 0.9f, $"Normal {normal}"));
        });
    }

    [Fact]
    public static void ParticlesRespectTheCollisionMatrix()
    {
        RunScene(async scene =>
        {
            var lower = new VolumetricSoftBodyComponent { Resolution = 3, CollisionLayer = CollisionLayer.Layer1 };
            var upper = new VolumetricSoftBodyComponent { Resolution = 3, CollisionLayer = CollisionLayer.Layer2 };
            scene.Add(lower, new CubeProceduralModel(), new Vector3(0f, 0.5f, 0f));
            var upperEntity = scene.Add(upper, new CubeProceduralModel(), new Vector3(0f, 2f, 0f));
            lower.Simulation!.CollisionMatrix.Set(CollisionLayer.Layer1, CollisionLayer.Layer2, false);

            await Seconds(upper, 2f);

            // Fell through the lower body onto the floor
            Assert.InRange(upperEntity.Transform.Position.Y, 0.35f, 0.6f);
        });
    }

    [Fact]
    public static void PinsFollowAnAnchorNode()
    {
        RunScene(async scene =>
        {
            // A skeleton whose second node sits at the cloth's pinned edge
            var anchorModel = scene.Model(new CubeProceduralModel { Size = new Vector3(0.1f) }, Chain("Hand", 1, new Vector3(-0.5f, 2f, 0f), Vector3.UnitY));
            var anchor = scene.Add(null, anchorModel, Vector3.Zero);

            var edge = new BoundingBox(new Vector3(-0.51f, -1f, -1f), new Vector3(-0.49f, 1f, 1f));
            var cloth = new ClothComponent { Pins = { new SoftBodyPin { Region = edge, Anchor = new WorldAnchor { Entity = anchor, NodeName = "Hand0" } } } };
            scene.Add(cloth, new PlaneProceduralModel { Size = new Vector2(1f), Tessellation = new Int2(10), Normal = NormalDirection.UpY, GenerateBackFace = true }, new Vector3(0f, 2f, 0f));
            await Seconds(cloth, 0.1f);

            var pinned = Enumerable.Range(0, cloth.ParticleCount).Where(cloth.IsParticlePinned).ToArray();
            Assert.NotEmpty(pinned);
            var before = pinned.Select(cloth.GetParticlePosition).ToArray();

            // Moving the node, not the entity, carries the pinned particles along
            anchor.Get<ModelComponent>()!.Skeleton.NodeTransformations[1].Transform.Position += new Vector3(1f, 0.5f, 0f);
            await Seconds(cloth, 0.5f);

            for (int i = 0; i < pinned.Length; i++)
                Assert.True(Vector3.Distance(cloth.GetParticlePosition(pinned[i]), before[i] + new Vector3(1f, 0.5f, 0f)) < 0.02f, $"Pinned particle {pinned[i]} at {cloth.GetParticlePosition(pinned[i])}");
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public static void PinnedParticlesPullTheirBodyAnchor(bool anchorFirst)
    {
        RunScene(async scene =>
        {
            // A floating block above the pinned edge of a cloth, added before or after the cloth
            var block = new BodyComponent { Gravity = false, Collider = new CompoundCollider { Colliders = { new BoxCollider { Size = new Vector3(0.1f), Mass = 1f } } } };
            var anchor = new Entity { block };
            anchor.Transform.Position = new Vector3(-0.5f, 2.3f, 0f);
            if (anchorFirst)
                scene.Add(anchor);

            var edge = new BoundingBox(new Vector3(-0.51f, -1f, -1f), new Vector3(-0.49f, 1f, 1f));
            var cloth = new ClothComponent { Pins = { new SoftBodyPin { Region = edge, Anchor = new BodyAnchor { Body = block } } } };
            scene.Add(cloth, new PlaneProceduralModel { Size = new Vector2(1f), Tessellation = new Int2(10), Normal = NormalDirection.UpY, GenerateBackFace = true }, new Vector3(0f, 2f, 0f));
            if (anchorFirst == false)
                scene.Add(anchor);

            // The weight of the cloth drags the block down
            await Seconds(cloth, 1f);
            Assert.True(block.Position.Y < 2f, $"Block at {block.Position}");
        });
    }

    [Fact]
    public static void PinWithoutRegionHoldsTheParticlesClosestToItsAnchor()
    {
        RunScene(async scene =>
        {
            // Hooks at two corners of a cloth, and a point between two vertices of its far edge
            var hooks = new[] { new Vector3(-0.5f, 2f, -0.5f), new Vector3(0.5f, 2f, -0.5f) };
            var pins = hooks.Select(h => new SoftBodyPin { Anchor = new WorldAnchor { Entity = new Entity { Transform = { Position = h } } } }).ToList();
            pins.Add(new SoftBodyPin { Region = new BoundingBox(new Vector3(0.05f, 0f, 0.5f), new Vector3(0.05f, 0f, 0.5f)) });
            foreach (var pin in pins.Take(2))
                scene.Add(((WorldAnchor)pin.Anchor!).Entity!);
            var cloth = new ClothComponent();
            cloth.Pins.AddRange(pins);
            scene.Add(cloth, new PlaneProceduralModel { Size = new Vector2(1f), Tessellation = new Int2(10), Normal = NormalDirection.UpY, GenerateBackFace = true }, new Vector3(0f, 2f, 0f));
            await Seconds(cloth, 0.1f);

            var pinned = Enumerable.Range(0, cloth.ParticleCount).Where(cloth.IsParticlePinned).Select(i => cloth.Topology!.RestPositions[i]).ToList();
            Assert.Equal(4, pinned.Count);
            Assert.Contains(new Vector3(-0.5f, 0f, -0.5f), pinned);
            Assert.Contains(new Vector3(0.5f, 0f, -0.5f), pinned);
            Assert.Contains(pinned, p => MathUtil.NearEqual(p.Z, 0.5f) && MathUtil.NearEqual(p.X, 0f));
            Assert.Contains(pinned, p => MathUtil.NearEqual(p.Z, 0.5f) && MathUtil.NearEqual(p.X, 0.1f));
        });
    }

    [Fact]
    public static void PinnedParticlesDoNotPushTheirBodyAnchor()
    {
        RunScene(async scene =>
        {
            // The pinned edge of a weightless cloth runs through a floating block
            var block = new BodyComponent { Gravity = false, Collider = new CompoundCollider { Colliders = { new BoxCollider { Size = new Vector3(0.08f), Mass = 1f } } } };
            var anchor = new Entity { block };
            anchor.Transform.Position = new Vector3(-0.5f, 2f, 0f);
            scene.Add(anchor);

            var edge = new BoundingBox(new Vector3(-0.51f, -1f, -1f), new Vector3(-0.49f, 1f, 1f));
            var cloth = new ClothComponent { Gravity = false, Pins = { new SoftBodyPin { Region = edge, Anchor = new BodyAnchor { Body = block } } } };
            scene.Add(cloth, new PlaneProceduralModel { Size = new Vector2(1f), Tessellation = new Int2(10), Normal = NormalDirection.UpY, GenerateBackFace = true }, new Vector3(0f, 2f, 0f));

            // Contacts between the pinned particles and the block would push it, then the particles after it, forever
            await Seconds(cloth, 2f);
            Assert.True(Vector3.Distance(block.Position, new Vector3(-0.5f, 2f, 0f)) < 0.05f, $"Block at {block.Position}");
            Assert.True(block.LinearVelocity.Length() < 0.05f, $"Block moving at {block.LinearVelocity}");
        });
    }

    [Fact]
    public static void ParticlesFollowTheSkeleton()
    {
        RunScene(async scene =>
        {
            // A capsule standing along a chain of four bones, floating
            var model = scene.Model(new CapsuleProceduralModel { Length = 1.5f, Radius = 0.25f, Tessellation = 16 }, Chain("Bone", 4, new Vector3(0f, -0.75f, 0f), new Vector3(0f, 0.5f, 0f)));
            var body = new VolumetricSoftBodyComponent { Resolution = 6, FollowSkeleton = true, Gravity = false };
            var entity = scene.Add(body, model, new Vector3(0f, 2f, 0f));

            await Seconds(body, 1f);
            Assert.InRange(MaximumDistanceToSkeleton(body, entity), 0f, 0.01f);

            // Bending the last bone and moving the entity: the particles get there, the entity is not moved by them
            var skeleton = entity.Get<ModelComponent>()!.Skeleton;
            skeleton.NodeTransformations[4].Transform.Rotation = Quaternion.RotationZ(MathF.PI / 3f);
            entity.Transform.Position += new Vector3(1f, 0f, 0f);
            await Seconds(body, 0.1f);
            Assert.True(MaximumDistanceToSkeleton(body, entity) > 0.1f, "The particles should lag behind the skeleton");
            await Seconds(body, 2f);
            Assert.InRange(MaximumDistanceToSkeleton(body, entity), 0f, 0.01f);
            Assert.Equal(new Vector3(1f, 2f, 0f), entity.Transform.Position);
        });
    }

    [Fact]
    public static void BodyFollowingTheSkeletonDentsOnContactThenRecovers()
    {
        RunScene(async scene =>
        {
            var model = scene.Model(new CapsuleProceduralModel { Length = 1.5f, Radius = 0.25f, Tessellation = 16 }, Chain("Bone", 4, new Vector3(0f, -0.75f, 0f), new Vector3(0f, 0.5f, 0f)));
            var body = new VolumetricSoftBodyComponent { Resolution = 6, FollowSkeleton = true, Gravity = false };
            var entity = scene.Add(body, model, new Vector3(0f, 2f, 0f));
            entity.Transform.Rotation = Quaternion.RotationZ(MathF.PI / 2f); // Lying along x

            var ball = new Entity { new BodyComponent { Collider = new CompoundCollider { Colliders = { new SphereCollider { Radius = 0.2f, Mass = 20f } } } } };
            ball.Transform.Position = new Vector3(0f, 3f, 0f);
            scene.Add(ball);

            float deepest = 0f;
            for (int step = 0; step < 60; step++)
            {
                await body.Simulation!.AfterUpdate();
                deepest = MathF.Max(deepest, MaximumDistanceToSkeleton(body, entity));
            }
            Assert.True(deepest > 0.05f, $"The ball should dent the body, deepest {deepest}");

            scene.Remove(ball);
            await Seconds(body, 2f);
            Assert.InRange(MaximumDistanceToSkeleton(body, entity), 0f, 0.01f);
        });
    }

    [Fact]
    public static void FleshAndPhysicalBonesPullOnEachOther()
    {
        RunScene(async scene =>
        {
            // A capsule lying along a ragdoll spine of four bones; flesh and bones on layers which do not touch, so only the joints link them
            var model = scene.Model(new CapsuleProceduralModel { Length = 1.5f, Radius = 0.25f, Tessellation = 16 }, Chain("Bone", 4, new Vector3(0f, -0.75f, 0f), new Vector3(0f, 0.5f, 0f)));
            var flesh = new VolumetricSoftBodyComponent { Resolution = 6, FollowSkeleton = true, SkeletonStiffness = 8f, CollisionLayer = CollisionLayer.Layer1 };
            var bones = new SkeletonBodyComponent { Mode = SkeletonBodyMode.Ragdoll, SelfCollision = false, CollisionLayer = CollisionLayer.Layer2 };
            for (int i = 0; i < 4; i++)
                ((BoneCollider)bones.Collider).Bones.Add(new BoneShape { NodeName = "Bone" + i, Shape = new SphereCollider { Radius = 0.1f, Mass = 0.1f } });
            var entity = new Entity { new ModelComponent(model), flesh, bones };
            entity.Transform.Position = new Vector3(0f, 0.3f, 0f);
            entity.Transform.Rotation = Quaternion.RotationZ(MathF.PI / 2f);
            scene.Add(entity);
            flesh.Simulation!.CollisionMatrix.Set(CollisionLayer.Layer1, CollisionLayer.Layer2, false);
            await Seconds(flesh, 1f);

            // Resting inside the flesh, not on the floor; thrown up, the flesh carries the bones along
            var start = bones.GetBodyPosition(0);
            Assert.True(start.Y > 0.2f, $"Bone at {start}");
            flesh.LinearVelocity = new Vector3(0f, 6f, 0f);
            await Seconds(flesh, 0.3f);
            Assert.True(bones.GetBodyPosition(0).Y - start.Y > 0.3f, $"Bone from {start} to {bones.GetBodyPosition(0)}");
        });
    }

    [Fact]
    public static void BoneBodiesAreJoinedToABoundedNumberOfParticles()
    {
        RunScene(async scene =>
        {
            // A fine lattice around two bones: hundreds of particles for each
            var model = scene.Model(new CapsuleProceduralModel { Length = 1.2f, Radius = 0.3f, Tessellation = 16 }, Chain("Bone", 2, new Vector3(0f, -0.6f, 0f), new Vector3(0f, 0.6f, 0f)));
            var flesh = new VolumetricSoftBodyComponent { Resolution = 10, FollowSkeleton = true, Gravity = false };
            var bones = new SkeletonBodyComponent { Mode = SkeletonBodyMode.Ragdoll, SelfCollision = false };
            for (int i = 0; i < 2; i++)
                ((BoneCollider)bones.Collider).Bones.Add(new BoneShape { NodeName = "Bone" + i, Shape = new SphereCollider { Radius = 0.1f, Mass = 1f } });
            var entity = new Entity { new ModelComponent(model), flesh, bones };
            entity.Transform.Position = new Vector3(0f, 2f, 0f);
            scene.Add(entity);
            await Seconds(flesh, 0.1f);

            var simulation = flesh.Simulation!.Simulation;
            var active = simulation.Bodies.ActiveSet;
            int boneBodies = 0;
            for (int i = 0; i < active.Count; i++)
            {
                if (flesh.Simulation.GetComponent(new CollidableReference(CollidableMobility.Dynamic, active.IndexToHandle[i])) != bones)
                    continue;
                boneBodies++;
                Assert.True(active.Constraints[i].Count <= 24, $"A bone is in {active.Constraints[i].Count} constraints");
            }
            Assert.Equal(2, boneBodies);
            Assert.True(simulation.Solver.ActiveSet.Batches.Count <= simulation.Solver.FallbackBatchThreshold, $"{simulation.Solver.ActiveSet.Batches.Count} batches");
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static void StandingSkeletonInsideItsFleshStandsOnTheFloor(bool ghost)
    {
        RunScene(async scene =>
        {
            // A soft capsule from the floor to 1.2 m around a Standing spine whose hips are at 0.45 m: there is flesh under the hips
            var skeleton = new Skeleton
            {
                Nodes =
                [
                    new ModelNodeDefinition { Name = "Root", ParentIndex = -1, Transform = { Scale = Vector3.One, Rotation = Quaternion.Identity }, Flags = ModelNodeFlags.Default },
                    new ModelNodeDefinition { Name = "Hips", ParentIndex = 0, Transform = { Position = new Vector3(0f, 0.45f, 0f), Scale = Vector3.One, Rotation = Quaternion.Identity }, Flags = ModelNodeFlags.Default },
                    new ModelNodeDefinition { Name = "Head", ParentIndex = 1, Transform = { Position = new Vector3(0f, 0.45f, 0f), Scale = Vector3.One, Rotation = Quaternion.Identity }, Flags = ModelNodeFlags.Default },
                ],
            };
            var model = scene.Model(new CapsuleProceduralModel { Length = 0.6f, Radius = 0.3f, Tessellation = 16, LocalOffset = new Vector3(0f, 0.6f, 0f) }, skeleton);
            var flesh = new VolumetricSoftBodyComponent { Resolution = 6, FollowSkeleton = true, SkeletonStiffness = 10f, Mass = 20f };
            var bones = new SkeletonBodyComponent { Mode = SkeletonBodyMode.Standing, SelfCollision = false, Ghost = ghost };
            var collider = (BoneCollider)bones.Collider;
            collider.Bones.Add(new BoneShape { NodeName = "Hips", Shape = new SphereCollider { Radius = 0.1f, Mass = 5f } });
            collider.Bones.Add(new BoneShape { NodeName = "Head", Shape = new SphereCollider { Radius = 0.1f, Mass = 3f } });
            var entity = scene.Add(flesh, model, new Vector3(0f, 0.02f, 0f));
            entity.Add(bones);

            await Seconds(flesh, 3f);
            var hips = bones.GetBodyPosition(0);
            Assert.True(bones.IsGrounded && bones.Ground is StaticComponent, $"Standing on {bones.Ground}");
            Assert.True(MathF.Abs(hips.Y - 0.45f) < 0.1f, $"Hips at {hips}");
            Assert.True(bones.GetBodyPosition(1).Y > hips.Y + 0.3f, $"Head at {bones.GetBodyPosition(1)}");
        });
    }

    /// <summary> How far the free particle farthest from its place on the skeleton is </summary>
    private static float MaximumDistanceToSkeleton(VolumetricSoftBodyComponent body, Entity entity)
    {
        var skeleton = ModelNodes.Refresh(entity)!;
        var binding = SkeletonBinding.Build(body.SourceModel!.Skeleton, body.Topology!.RestPositions)!;
        float maximum = 0f;
        for (int i = 0; i < body.ParticleCount; i++)
            maximum = MathF.Max(maximum, Vector3.Distance(body.GetParticlePosition(i), binding.Target(i, skeleton.NodeTransformations)));
        return maximum;
    }

    /// <summary> A root node then <paramref name="count"/> nodes named <paramref name="name"/>0, 1… each one <paramref name="step"/> from its parent </summary>
    private static Skeleton Chain(string name, int count, Vector3 first, Vector3 step)
    {
        var nodes = new ModelNodeDefinition[count + 1];
        nodes[0] = new ModelNodeDefinition { Name = "Root", ParentIndex = -1, Transform = { Scale = Vector3.One, Rotation = Quaternion.Identity }, Flags = ModelNodeFlags.Default };
        for (int i = 1; i <= count; i++)
            nodes[i] = new ModelNodeDefinition { Name = name + (i - 1), ParentIndex = i - 1, Transform = { Position = i == 1 ? first : step, Scale = Vector3.One, Rotation = Quaternion.Identity }, Flags = ModelNodeFlags.Default };
        return new Skeleton { Nodes = nodes };
    }

    private sealed class CountingHandler : IContactHandler
    {
        public bool NoContactResponse => false;
        public readonly System.Collections.Generic.List<CollidableComponent> Others = new();

        public void OnStartedTouching<TManifold>(Contacts<TManifold> contacts) where TManifold : unmanaged, IContactManifold<TManifold>
        {
            Others.Add(contacts.Other);
        }
    }

    private sealed class TouchingHandler : IContactHandler
    {
        public bool NoContactResponse => false;
        public int Calls, Groups;
        public readonly System.Collections.Generic.List<Vector3> Normals = new();

        public void OnTouching<TManifold>(Contacts<TManifold> contacts) where TManifold : unmanaged, IContactManifold<TManifold>
        {
            Calls++;
            Groups = contacts.Groups.Length;
            foreach (var contact in contacts)
                Normals.Add(contact.Normal);
        }
    }

    private static async Task Seconds(SoftBodyComponent body, float seconds)
    {
        var simulation = body.Simulation!;
        for (int steps = (int)(seconds * 60f); steps > 0; steps--)
            await simulation.AfterUpdate();
    }

    private static void RunScene(Func<TestScene, Task> test)
    {
        var game = new TestGame();
        Exception? failure = null;
        game.Script.AddTask(async () =>
        {
            try
            {
                game.ScreenShotAutomationEnabled = false;
                var floor = new Entity { new StaticComponent { Collider = new CompoundCollider { Colliders = { new BoxCollider { Size = new Vector3(20f, 1f, 20f) } } } } };
                floor.Transform.Position = new Vector3(0f, -0.5f, 0f);
                game.SceneSystem.SceneInstance.RootScene.Entities.Add(floor);
                await game.Script.NextFrame();

                await test(new TestScene(game));
            }
            catch (Exception e)
            {
                failure = e; // Thrown from a script it would only be logged, and the game would never exit
            }
            game.Exit();
        });
        RunGameTest(game);
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
    }

    private sealed class TestScene(TestGame game)
    {
        /// <summary> Adds an entity with a model generated from <paramref name="shape"/> and <paramref name="body"/> </summary>
        public Entity Add(SoftBodyComponent body, PrimitiveProceduralModelBase shape, Vector3 position) => Add(body, Model(shape, null), position);

        /// <summary> Adds an entity with <paramref name="model"/> and, if any, <paramref name="body"/> </summary>
        public Entity Add(SoftBodyComponent? body, Model model, Vector3 position)
        {
            var entity = new Entity { new ModelComponent(model) };
            if (body is not null)
                entity.Add(body);
            entity.Transform.Position = position;
            Add(entity);
            return entity;
        }

        public void Add(Entity entity) => game.SceneSystem.SceneInstance.RootScene.Entities.Add(entity);

        public void Remove(Entity entity) => game.SceneSystem.SceneInstance.RootScene.Entities.Remove(entity);

        public Model Model(PrimitiveProceduralModelBase shape, Skeleton? skeleton)
        {
            var model = new ProceduralModelDescriptor(shape).GenerateModel(game.Services);
            model.Skeleton = skeleton;
            return model;
        }
    }

    private sealed class TestGame : GameTestBase
    {
        public TestGame()
        {
            IsFixedTimeStep = true;
            ForceOneUpdatePerDraw = true;
            IsDrawDesynchronized = false;
            TargetElapsedTime = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 60);
        }
    }
}
