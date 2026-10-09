// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.Trees;
using Stride.BepuPhysics.Components;
using Stride.BepuPhysics.Definitions;
using Stride.BepuPhysics.Definitions.Colliders;
using Stride.BepuPhysics.Systems;
using Stride.Core;
using Stride.Core.Diagnostics;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Engine.Design;
using NRigidPose = BepuPhysics.RigidPose;
using NQuaternion = System.Numerics.Quaternion;
using NVector3 = System.Numerics.Vector3;

namespace Stride.BepuPhysics;

/// <summary>
/// Kinematic bodies following the nodes of the skeleton of the <see cref="ModelComponent"/> on the same entity, one per shape of its <see cref="BoneCollider"/>.
/// </summary>
/// <remarks>
/// Bodies are driven toward their node by velocity, so what they push receives the motion of the animation.
/// Raycasts, overlaps and contact events report this component for any of them, with the index of the bone shape as the child index.
/// The pose of the nodes is the one of the previous frame: animations are evaluated while drawing, after the simulation steps.
/// </remarks>
[DataContract(Inherited = true)]
[DefaultEntityComponentProcessor(typeof(SkeletonBodyProcessor), ExecutionMode = ExecutionMode.Runtime)]
[ComponentCategory("Physics - Bepu")]
public class SkeletonBodyComponent : CollidableComponent
{
    private static readonly Logger Logger = GlobalLogger.GetLogger(nameof(SkeletonBodyComponent));

    private float _teleportDistance = 1f;
    private BodyHandle[] _bodies = [];
    private int[] _nodes = [];
    private readonly Dictionary<int, int> _shapeOfBody = new();
    private StepListener? _stepListener;

    /// <summary> Creates a component whose shapes come from a <see cref="BoneCollider"/> </summary>
    [SetsRequiredMembers]
    public SkeletonBodyComponent()
    {
        Collider = new BoneCollider();
    }

    /// <summary>
    /// A body further than this from its node, in a single step, jumps to it instead of moving there.
    /// </summary>
    /// <remarks> Avoids sweeping through the scene with a huge velocity when the character is teleported. </remarks>
    [Display(category: CategoryForces)]
    [DefaultValue(1f)]
    public float TeleportDistance
    {
        get => _teleportDistance;
        set => _teleportDistance = MathF.Max(0f, value);
    }

    /// <summary> The amount of bodies, one per bone shape whose node exists in the skeleton; zero until this is part of a simulation </summary>
    [DataMemberIgnore]
    public int BodyCount => _bodies.Length;

    internal override int CollidableCount => Simulation is null ? 0 : _bodies.Length;

    internal override bool ShouldCalculateInertia => false;

    protected internal override CollidableReference? CollidableReference => _bodies.Length > 0 && Simulation is not null ? Body(0).CollidableReference : null;

    protected override ref MaterialProperties MaterialProperties => ref Simulation!.CollidableMaterials[_bodies[0]];

    protected internal override NRigidPose? Pose
    {
        get
        {
            if (Simulation is null)
                return null;
            Entity.Transform.WorldMatrix.Decompose(out _, out Quaternion rotation, out var position);
            return new NRigidPose(position.ToNumeric(), rotation.ToNumeric());
        }
    }

    /// <summary> The bone shape behind the body at <paramref name="childIndex"/>, as reported by hits and contacts </summary>
    /// <param name="childIndex"> Index of the body, below <see cref="BodyCount"/> </param>
    /// <returns> The entry of the <see cref="BoneCollider"/> this body was created from </returns>
    public BoneShape GetBoneShape(int childIndex) => ((BoneCollider)Collider).GetCreated(childIndex).Bone;

    /// <summary> The index of the skeleton node followed by the body at <paramref name="childIndex"/> </summary>
    /// <param name="childIndex"> Index of the body, below <see cref="BodyCount"/> </param>
    /// <returns> An index into <see cref="Rendering.SkeletonUpdater.NodeTransformations"/> </returns>
    public int GetNodeIndex(int childIndex) => _nodes[childIndex];

    /// <summary> The current world position of the body at <paramref name="childIndex"/> </summary>
    /// <param name="childIndex"> Index of the body, below <see cref="BodyCount"/> </param>
    public Vector3 GetBodyPosition(int childIndex) => Body(childIndex).Pose.Position.ToStride();

    /// <summary> The current world orientation of the body at <paramref name="childIndex"/> </summary>
    /// <param name="childIndex"> Index of the body, below <see cref="BodyCount"/> </param>
    public Quaternion GetBodyOrientation(int childIndex) => Body(childIndex).Pose.Orientation.ToStride();

    /// <summary> The current linear velocity of the body at <paramref name="childIndex"/> </summary>
    /// <param name="childIndex"> Index of the body, below <see cref="BodyCount"/> </param>
    public Vector3 GetBodyLinearVelocity(int childIndex) => Body(childIndex).Velocity.Linear.ToStride();

    /// <summary> The current angular velocity of the body at <paramref name="childIndex"/>, in world space </summary>
    /// <param name="childIndex"> Index of the body, below <see cref="BodyCount"/> </param>
    public Vector3 GetBodyAngularVelocity(int childIndex) => Body(childIndex).Velocity.Angular.ToStride();

    /// <summary> Recreates the bodies, to apply changes made to the <see cref="BoneCollider"/> or to the skeleton </summary>
    public void Rebuild() => TryUpdateFeatures();

    internal override CollidableReference GetCollidableReference(int index) => Body(index).CollidableReference;

    internal override int GetChildIndex(CollidableReference collidable, int shapeChildIndex)
    {
        return collidable.Mobility != CollidableMobility.Static && _shapeOfBody.TryGetValue(collidable.BodyHandle.Value, out var index) ? index : shapeChildIndex;
    }

    internal override bool TryGetShapePose(int index, out NRigidPose pose)
    {
        if (Simulation is null || index >= _bodies.Length)
        {
            pose = default;
            return false;
        }

        pose = Body(index).Pose;
        return true;
    }

    /// <summary> The bone shapes whose node is in the skeleton, and the scale of that node; false when the entity has no skeleton yet </summary>
    internal bool TryResolveBones(out List<(BoneShape Bone, float Scale)> resolved)
    {
        resolved = new();
        if (Entity?.Get<ModelComponent>() is not { Model: not null } model || model.Skeleton is not { } skeleton)
            return false;

        var collider = (BoneCollider)Collider;
        var bones = collider.Bones.Count > 0 || collider.GeneratedRadius <= 0f ? collider.Bones : BoneCollider.Generate(skeleton.Nodes, collider.GeneratedRadius);

        UpdateNodeMatrices(model);
        var nodes = new List<int>();
        foreach (var bone in bones)
        {
            int node = Array.FindIndex(skeleton.Nodes, n => n.Name == bone.NodeName);
            if (node < 0)
            {
                Logger.Warning($"{nameof(SkeletonBodyComponent)} on '{Entity.Name}': no node named '{bone.NodeName}' in the skeleton, its shape is skipped");
                continue;
            }

            skeleton.NodeTransformations[node].WorldMatrix.Decompose(out var scale, out Quaternion _, out _);
            // Bepu shapes have no scale, the shape is resized by the average scale of its node
            resolved.Add((bone, MathF.Abs(scale.X + scale.Y + scale.Z) / 3f));
            nodes.Add(node);
        }

        _nodes = nodes.ToArray();
        return resolved.Count > 0;
    }

    protected override void AttachInner(NRigidPose pose, BodyInertia shapeInertia, TypedIndex shapeIndex)
    {
        Debug.Assert(Simulation is not null);

        if (Collider is not BoneCollider collider)
            throw new InvalidOperationException($"{nameof(SkeletonBodyComponent)} on '{Entity.Name}' needs a {nameof(BoneCollider)}, not a {Collider.GetType().Name}");
        var skeleton = Entity.Get<ModelComponent>()!.Skeleton;
        var bodies = Simulation.Simulation.Bodies;

        _bodies = new BodyHandle[_nodes.Length];
        _shapeOfBody.Clear();
        var activity = new BodyActivityDescription(0.01f);
        for (int i = 0; i < _bodies.Length; i++)
        {
            var description = BodyDescription.CreateKinematic(TargetPose(skeleton, i, collider), new CollidableDescription(collider.GetCreatedIndex(i)), activity);
            var handle = bodies.Add(description);
            _bodies[i] = handle;
            _shapeOfBody[handle.Value] = i;
            Simulation.SetBodyOwner(handle, this);
            Simulation.CollidableMaterials.Allocate(handle) = new();
        }

        _stepListener = new StepListener(this);
        Simulation.Register(_stepListener);
    }

    protected override void DetachInner()
    {
        Debug.Assert(Simulation is not null);

        if (_stepListener is not null)
        {
            Simulation.Unregister(_stepListener);
            _stepListener = null;
        }

        foreach (var handle in _bodies)
        {
            Simulation.Simulation.Bodies.Remove(handle);
            Simulation.SetBodyOwner(handle, null);
        }

        _bodies = [];
        _shapeOfBody.Clear();
    }

    internal override void OnMaterialPropertiesUpdated()
    {
        var materials = Simulation!.CollidableMaterials;
        var material = materials[_bodies[0]];
        for (int i = 1; i < _bodies.Length; i++)
            materials[_bodies[i]] = material;
    }

    internal override void RayTest<TRayHitHandler>(in Vector3 origin, in Vector3 dir, ref float maximumT, ref TRayHitHandler hitHandler)
    {
        if (Simulation is null)
            return;

        var collider = (BoneCollider)Collider;
        var ray = new RayData { Origin = origin.ToNumeric(), Direction = dir.ToNumeric() };
        var shapes = Simulation.Simulation.Shapes;
        for (int i = 0; i < _bodies.Length; i++)
        {
            var index = collider.GetCreatedIndex(i);
            var handler = new ChildRayHitHandler<TRayHitHandler>(hitHandler, i);
            shapes[index.Type].RayTest(index.Index, Body(i).Pose, ray, ref maximumT, Simulation.BufferPool, ref handler);
            hitHandler = handler.Inner;
        }
    }

    private BodyReference Body(int index) => Simulation!.Simulation.Bodies[_bodies[index]];

    private NRigidPose TargetPose(Rendering.SkeletonUpdater skeleton, int index, BoneCollider collider)
    {
        ref var node = ref skeleton.NodeTransformations[_nodes[index]];
        var shape = collider.GetCreated(index).Bone.Shape;
        node.WorldMatrix.Decompose(out _, out Quaternion rotation, out _);
        var position = Vector3.TransformCoordinate(shape.PositionLocal, node.WorldMatrix);
        return new NRigidPose(position.ToNumeric(), (shape.RotationLocal * rotation).ToNumeric());
    }

    /// <summary> The node matrices are computed while drawing, this makes them valid before the first frame is drawn </summary>
    private void UpdateNodeMatrices(ModelComponent model)
    {
        var skeleton = model.Skeleton;
        Entity.Transform.UpdateWorldMatrix();
        skeleton.NodeTransformations[0].LocalMatrix = Entity.Transform.WorldMatrix;
        skeleton.UpdateMatrices();
    }

    private void FollowNodes(float deltaTime)
    {
        var collider = (BoneCollider)Collider;
        var skeleton = Entity.Get<ModelComponent>()!.Skeleton;
        for (int i = 0; i < _bodies.Length; i++)
        {
            var target = TargetPose(skeleton, i, collider);
            var body = Body(i);
            var offset = target.Position - body.Pose.Position;
            if (offset.LengthSquared() > _teleportDistance * _teleportDistance)
            {
                body.Pose = target;
                body.Velocity = default;
                body.UpdateBounds();
                continue;
            }

            // The rotation which brings the current orientation onto the target, taken the short way around
            var delta = target.Orientation * NQuaternion.Conjugate(body.Pose.Orientation);
            if (delta.W < 0f)
                delta = -delta;
            float sinHalfAngle = MathF.Sqrt(delta.X * delta.X + delta.Y * delta.Y + delta.Z * delta.Z);
            var angular = sinHalfAngle > 1e-7f
                ? new NVector3(delta.X, delta.Y, delta.Z) * (2f * MathF.Atan2(sinHalfAngle, delta.W) / (sinHalfAngle * deltaTime))
                : NVector3.Zero;

            var velocity = new global::BepuPhysics.BodyVelocity(offset / deltaTime, angular);
            body.Velocity = velocity;
            if (velocity.Linear != NVector3.Zero || velocity.Angular != NVector3.Zero)
                body.Awake = true;
        }
    }

    private sealed class StepListener(SkeletonBodyComponent owner) : ISimulationUpdate
    {
        public Entity Entity => owner.Entity;

        public void SimulationUpdate(BepuSimulation simulation, float simTimeStep) => owner.FollowNodes(simTimeStep);

        public void AfterSimulationUpdate(BepuSimulation simulation, float simTimeStep) { }
    }

    /// <summary> Reports hits on the shape of a single body under the index of that body </summary>
    private struct ChildRayHitHandler<T>(T inner, int child) : IShapeRayHitHandler where T : struct, IShapeRayHitHandler
    {
        public T Inner = inner;

        public bool AllowTest(int childIndex) => Inner.AllowTest(child);

        public void OnRayHit(in RayData ray, ref float maximumT, float t, NVector3 normal, int childIndex) => Inner.OnRayHit(ray, ref maximumT, t, normal, child);
    }
}
