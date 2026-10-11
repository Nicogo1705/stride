// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using BepuPhysics.Trees;
using Stride.BepuPhysics.Components;
using Stride.BepuPhysics.Definitions;
using Stride.BepuPhysics.Definitions.Colliders;
using Stride.BepuPhysics.Systems;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Diagnostics;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Engine.Design;
using Stride.Rendering;
using NRigidPose = BepuPhysics.RigidPose;
using NQuaternion = System.Numerics.Quaternion;
using NVector3 = System.Numerics.Vector3;
using BodyIndexLoop = BepuUtilities.IForEach<int>;

namespace Stride.BepuPhysics;

/// <summary>
/// Bodies following the nodes of the skeleton of the <see cref="ModelComponent"/> on the same entity, one per shape of its <see cref="BoneCollider"/>,
/// kinematic while animated, or a ragdoll the skeleton follows.
/// </summary>
/// <remarks>
/// In <see cref="SkeletonBodyMode.Animated"/>, bodies are driven toward their node by velocity, so what they push receives the motion of the animation.
/// Raycasts, overlaps and contact events report this component for any of them, with the index of the bone shape as the child index.
/// The pose of the nodes is the one of the previous frame: animations are evaluated while drawing, after the simulation steps.
/// </remarks>
[DataContract(Inherited = true)]
[DefaultEntityComponentProcessor(typeof(SkeletonBodyProcessor), ExecutionMode = ExecutionMode.Runtime)]
[ComponentCategory("Physics - Bepu")]
public class SkeletonBodyComponent : CollidableComponent
{
    private const string CategoryRagdoll = "Ragdoll";

    private static readonly Logger Logger = GlobalLogger.GetLogger(nameof(SkeletonBodyComponent));
    private static readonly BoneJoint DefaultJoint = new();

    private float _teleportDistance = 1f;
    private BodyHandle[] _bodies = [];
    private int[] _nodes = [];
    private readonly Dictionary<int, int> _shapeOfBody = new();
    private StepListener? _stepListener;

    private SkeletonBodyMode _mode;
    private float _blendDuration = 0.25f;
    private float _activeSpringFrequency = 10f;
    private float _activeSpringDampingRatio = 1f;
    private float _activeMaximumForce = 500f;
    private float _standingLinearSpringFrequency = 4f;
    private float _standingAngularSpringFrequency = 8f;
    private float _standingStrength = 2f;

    private Vector3[] _nodeScales = [];
    // Per body: the body it is jointed or welded to and its node, -1 for none
    private int[] _jointParents = [];
    private int[] _jointNodes = [];
    // The first body of each node, the one whose pose is written back to the node
    private int[] _drivers = [];
    private readonly List<ConstraintHandle> _constraints = new();
    private ConstraintHandle[] _servos = [];
    private readonly List<(int Body, ConstraintHandle Linear, ConstraintHandle Angular)> _upright = new();
    private float _standingForce, _standingTorque;
    private float _bonesMass, _standingLever;
    private int _heldConstraintCount = -1; // Constraints on the bones when the weight held by the character was last measured
    private bool _ghost;
    private CollidableComponent? _ground;
    private float _groundHeight;
    private Matrix[] _animated = [];
    private (Vector3 Position, Quaternion Rotation)[] _blendFrom = [];
    private float _blendLength, _blendRemaining;
    private SkeletonUpdater? _drivenSkeleton;

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
    [DataMemberRange(0, 3)]
    [DefaultValue(1f)]
    public float TeleportDistance
    {
        get => _teleportDistance;
        set => _teleportDistance = MathF.Max(0f, value);
    }

    /// <summary>
    /// Whether the bodies follow the animation, or form a ragdoll the skeleton follows.
    /// </summary>
    /// <remarks>
    /// Leaving <see cref="SkeletonBodyMode.Animated"/> keeps the velocities the bodies had while following the animation.
    /// Coming back to it blends the nodes from the ragdoll to the animated pose over <see cref="BlendDuration"/>.
    /// <see cref="SkeletonBodyMode.Active"/> and <see cref="SkeletonBodyMode.Standing"/> pull toward the pose drawn on the previous frame, like the bodies of <see cref="SkeletonBodyMode.Animated"/>.
    /// The entity does not move with the ragdoll; the bounds of a skinned model follow its nodes.
    /// </remarks>
    [Display(category: CategoryRagdoll)]
    [DefaultValue(SkeletonBodyMode.Animated)]
    public SkeletonBodyMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value)
                return;

            var previous = _mode;
            _mode = value;
            if (Simulation is not null && _bodies.Length > 0)
                ApplyMode(previous);
        }
    }

    /// <summary>
    /// The limits of the joints, by child node; joints without an entry use the default values of <see cref="BoneJoint"/>.
    /// </summary>
    /// <remarks> Changes are applied when leaving <see cref="SkeletonBodyMode.Animated"/> or switching between the other modes. </remarks>
    [Display(category: CategoryRagdoll)]
    [MemberCollection(NotNullItems = true)]
    public List<BoneJoint> Joints { get; } = new();

    /// <summary>
    /// Whether bodies of this component which are not joined together collide with each other, like a hand with a thigh.
    /// </summary>
    /// <remarks> Bodies of the same node, or of a node and its closest ancestor with a shape, never collide. </remarks>
    [Display(category: CategoryRagdoll)]
    [DefaultValue(true)]
    public bool SelfCollision { get; set; } = true;

    /// <summary>
    /// The time, in seconds, to blend the nodes from the ragdoll back to the animation when <see cref="Mode"/> returns to <see cref="SkeletonBodyMode.Animated"/>.
    /// </summary>
    [Display(category: CategoryRagdoll)]
    [DataMemberRange(0, 3)]
    [DefaultValue(0.25f)]
    public float BlendDuration
    {
        get => _blendDuration;
        set => _blendDuration = MathF.Max(0f, value);
    }

    /// <summary>
    /// The frequency, in hertz, of the servos pulling the joints toward the animated pose in <see cref="SkeletonBodyMode.Active"/> and <see cref="SkeletonBodyMode.Standing"/>.
    /// </summary>
    [Display(category: CategoryRagdoll)]
    [DataMemberRange(0, 3)]
    [DefaultValue(10f)]
    public float ActiveSpringFrequency
    {
        get => _activeSpringFrequency;
        set => _activeSpringFrequency = MathF.Max(1e-3f, value);
    }

    /// <summary>
    /// The damping ratio of the servos of <see cref="SkeletonBodyMode.Active"/> and <see cref="SkeletonBodyMode.Standing"/>, 1 for a critically damped spring.
    /// </summary>
    [Display(category: CategoryRagdoll)]
    [DataMemberRange(0, 3)]
    [DefaultValue(1f)]
    public float ActiveSpringDampingRatio
    {
        get => _activeSpringDampingRatio;
        set => _activeSpringDampingRatio = MathF.Max(0f, value);
    }

    /// <summary>
    /// The largest torque each joint servo of <see cref="SkeletonBodyMode.Active"/> and <see cref="SkeletonBodyMode.Standing"/> may apply.
    /// </summary>
    [Display(category: CategoryRagdoll)]
    [DataMemberRange(0, 3)]
    [DefaultValue(500f)]
    public float ActiveMaximumForce
    {
        get => _activeMaximumForce;
        set => _activeMaximumForce = MathF.Max(0f, value);
    }

    /// <summary>
    /// The frequency, in hertz, of the servos lifting the root and the topmost body up to their animated height in <see cref="SkeletonBodyMode.Standing"/>.
    /// </summary>
    /// <remarks>
    /// The servos only push up, from what is under the hips (see <see cref="Ground"/>), up to the height of the hips above the feet in the animated pose;
    /// they leave the horizontal position free, so a character stands up where it lands, on the floor, a crate or another character.
    /// With nothing under it within the reach of its legs, it falls freely.
    /// </remarks>
    [Display(category: CategoryRagdoll)]
    [DataMemberRange(0, 3)]
    [DefaultValue(4f)]
    public float StandingLinearSpringFrequency
    {
        get => _standingLinearSpringFrequency;
        set => _standingLinearSpringFrequency = MathF.Max(1e-3f, value);
    }

    /// <summary>
    /// The frequency, in hertz, of the servos turning the root and the topmost body toward their animated orientation in <see cref="SkeletonBodyMode.Standing"/>.
    /// </summary>
    [Display(category: CategoryRagdoll)]
    [DataMemberRange(0, 3)]
    [DefaultValue(8f)]
    public float StandingAngularSpringFrequency
    {
        get => _standingAngularSpringFrequency;
        set => _standingAngularSpringFrequency = MathF.Max(1e-3f, value);
    }

    /// <summary>
    /// How strongly <see cref="SkeletonBodyMode.Standing"/> holds the character up, as a multiple of its weight; below 1, it cannot lift itself.
    /// </summary>
    /// <remarks>
    /// Each lifting servo may push with this many times the weight of all the bodies, those joined to the bones by other components included,
    /// and each turning servo apply that force
    /// at the distance between the root and the topmost body; a hit or a grab stronger than that wins.
    /// </remarks>
    [Display(category: CategoryRagdoll)]
    [DataMemberRange(0, 3)]
    [DefaultValue(2f)]
    public float StandingStrength
    {
        get => _standingStrength;
        set => _standingStrength = MathF.Max(0f, value);
    }

    /// <summary>
    /// Whether the bodies only carry the joints and servos of the skeleton: they collide with nothing and rays go through them.
    /// </summary>
    /// <remarks>
    /// For a character whose contacts come from other bodies joined to its bones, like soft flesh following the skeleton.
    /// The bodies keep the <see cref="ColliderBase.Mass"/> of their shape: lower it when the mass comes from those other bodies,
    /// but not much below a third of what each bone holds, or the solver lets the load sag.
    /// Changes are applied when the bodies are created, see <see cref="Rebuild"/>.
    /// </remarks>
    [Display(category: CategoryRagdoll)]
    [DefaultValue(false)]
    public bool Ghost
    {
        get => _ghost;
        set
        {
            _ghost = value;
            TryUpdateFeatures();
        }
    }

    /// <summary>
    /// What the character stands on in <see cref="SkeletonBodyMode.Standing"/>: the closest collidable under the hips within the reach of its animated legs, null when it is in the air.
    /// </summary>
    /// <remarks>
    /// Found by a ray cast down from the root body before each step, ignoring what belongs to the character:
    /// the collidables of its entity and of the children of the entity, and those attached to its bodies, like flesh joined to the bones.
    /// </remarks>
    [DataMemberIgnore]
    public CollidableComponent? Ground => _ground;

    /// <summary> Whether <see cref="Ground"/> found something to stand on </summary>
    [DataMemberIgnore]
    public bool IsGrounded => _ground is not null;

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

    internal override int GetFollowedNode(int index) => _nodes[index];

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

    internal override bool CanAttach(ICollider collider)
    {
        if (collider is BoneCollider)
            return true;

        Logger.Error($"{nameof(SkeletonBodyComponent)} on '{Entity?.Name}' needs a {nameof(BoneCollider)}, not a {collider.GetType().Name}; it is left out of the simulation");
        return false;
    }

    internal override bool AllowContactBetween(BodyHandle a, BodyHandle b)
    {
        if (SelfCollision == false)
            return false;

        int i = _shapeOfBody[a.Value];
        int j = _shapeOfBody[b.Value];
        return _nodes[i] != _nodes[j] && _jointNodes[i] != _nodes[j] && _jointNodes[j] != _nodes[i];
    }

    /// <summary> The bone shapes whose node is in the skeleton, the scale of that node and the pose of the shape in its space; false when the entity has no skeleton yet </summary>
    internal bool TryResolveBones(out List<(BoneShape Bone, float Scale, Vector3 Position, Quaternion Rotation)> resolved)
    {
        resolved = new();
        if (Entity?.Get<ModelComponent>() is not { Model: not null } model || model.Skeleton is not { } skeleton)
            return false;

        var collider = (BoneCollider)Collider;
        var bones = collider.Bones.Count > 0 || collider.GeneratedRadius <= 0f ? collider.Bones : BoneCollider.Generate(model.Model, collider.GeneratedRadius);

        UpdateNodeMatrices(model);
        var nodes = new List<int>();
        var scales = new List<Vector3>();
        foreach (var bone in bones)
        {
            int node = Array.FindIndex(skeleton.Nodes, n => n.Name == bone.NodeName);
            if (node < 0)
            {
                Logger.Warning($"{nameof(SkeletonBodyComponent)} on '{Entity.Name}': no node named '{bone.NodeName}' in the skeleton, its shape is skipped");
                continue;
            }

            skeleton.NodeTransformations[node].WorldMatrix.Decompose(out var scale, out Quaternion _, out _);
            var (position, rotation) = (bone.Shape.PositionLocal, bone.Shape.RotationLocal);
            if (bone.AlongBone && Array.FindIndex(skeleton.Nodes, n => n.ParentIndex == node) is var child and >= 0)
            {
                var segment = skeleton.Nodes[child].Transform.Position;
                if (segment.LengthSquared() > 1e-12f)
                {
                    var along = Quaternion.BetweenDirections(Vector3.UnitY, Vector3.Normalize(segment));
                    (position, rotation) = (segment * 0.5f + Vector3.Transform(position, along), rotation * along);
                }
            }
            // Bepu shapes have no scale, the shape is resized by the average scale of its node
            resolved.Add((bone, MathF.Abs(scale.X + scale.Y + scale.Z) / 3f, position, rotation));
            nodes.Add(node);
            scales.Add(scale);
        }

        _nodes = nodes.ToArray();
        _nodeScales = scales.ToArray();
        return resolved.Count > 0;
    }

    protected override void AttachInner(NRigidPose pose, BodyInertia shapeInertia, TypedIndex shapeIndex)
    {
        Debug.Assert(Simulation is not null);

        var collider = (BoneCollider)Collider;
        var skeleton = Entity.Get<ModelComponent>()!.Skeleton;
        var bodies = Simulation.Simulation.Bodies;

        _bodies = new BodyHandle[_nodes.Length];
        _shapeOfBody.Clear();
        var activity = new BodyActivityDescription(0.01f);
        for (int i = 0; i < _bodies.Length; i++)
        {
            // A body without a shape is left out of collision detection and ray casts
            var shape = _ghost ? default : collider.GetCreatedIndex(i);
            var description = BodyDescription.CreateKinematic(TargetPose(skeleton, i, collider), new CollidableDescription(shape), activity);
            var handle = bodies.Add(description);
            _bodies[i] = handle;
            _shapeOfBody[handle.Value] = i;
            Simulation.SetBodyOwner(handle, this);
            Simulation.CollidableMaterials.Allocate(handle) = new();
        }

        FindJoints(skeleton);
        if (_mode != SkeletonBodyMode.Animated)
            ApplyMode(SkeletonBodyMode.Animated);

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

        RemoveConstraints();
        ReleaseNodes();
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
        // Only matters once the bodies are dynamic, kinematic bodies ignore gravity
        material.Gravity = true;
        for (int i = 0; i < _bodies.Length; i++)
            materials[_bodies[i]] = material;
    }

    internal override void RayTest<TRayHitHandler>(in Vector3 origin, in Vector3 dir, ref float maximumT, ref TRayHitHandler hitHandler)
    {
        if (Simulation is null || _ghost)
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

    private NRigidPose TargetPose(SkeletonUpdater skeleton, int index, BoneCollider collider)
    {
        return BodyPose(skeleton.NodeTransformations[_nodes[index]].WorldMatrix, collider.GetCreatedLocal(index));
    }

    private static NRigidPose BodyPose(in Matrix node, (Vector3 Position, Quaternion Rotation) local)
    {
        node.Decompose(out _, out Quaternion rotation, out _);
        var position = Vector3.TransformCoordinate(local.Position, node);
        return new NRigidPose(position.ToNumeric(), (local.Rotation * rotation).ToNumeric());
    }

    /// <summary> The world matrix of the node of the body at <paramref name="index"/> placed at <paramref name="pose"/>, the inverse of <see cref="BodyPose"/> </summary>
    private Matrix NodeMatrix(int index, in NRigidPose pose)
    {
        var local = ((BoneCollider)Collider).GetCreatedLocal(index);
        var rotation = Quaternion.Invert(local.Rotation) * pose.Orientation.ToStride();
        var scale = _nodeScales[index];
        var position = pose.Position.ToStride() - Vector3.Transform(local.Position * scale, rotation);
        Matrix.Transformation(in scale, in rotation, in position, out var matrix);
        return matrix;
    }

    /// <summary> The node matrices are computed while drawing, this makes them valid before the first frame is drawn </summary>
    private void UpdateNodeMatrices(ModelComponent model)
    {
        var skeleton = model.Skeleton;
        Entity.Transform.UpdateWorldMatrix();
        skeleton.NodeTransformations[0].LocalMatrix = Entity.Transform.WorldMatrix;
        skeleton.UpdateMatrices();
    }

    /// <summary> Joins the first body of each node to the first body of its closest ancestor with one, and welds the other bodies of a node to its first </summary>
    private void FindJoints(SkeletonUpdater skeleton)
    {
        var firstOfNode = new Dictionary<int, int>();
        var drivers = new List<int>();
        _jointParents = new int[_bodies.Length];
        _jointNodes = new int[_bodies.Length];
        for (int i = 0; i < _bodies.Length; i++)
        {
            if (firstOfNode.TryGetValue(_nodes[i], out var first))
            {
                _jointParents[i] = first;
                _jointNodes[i] = _nodes[i];
                continue;
            }

            firstOfNode[_nodes[i]] = i;
            drivers.Add(i);
        }

        foreach (var i in drivers)
        {
            int parent = skeleton.Nodes[_nodes[i]].ParentIndex;
            while (parent >= 0 && firstOfNode.ContainsKey(parent) == false)
                parent = skeleton.Nodes[parent].ParentIndex;
            _jointParents[i] = parent >= 0 ? firstOfNode[parent] : -1;
            _jointNodes[i] = parent;
        }

        _drivers = drivers.ToArray();
        _blendFrom = new (Vector3, Quaternion)[_drivers.Length];
        _servos = new ConstraintHandle[_bodies.Length];
    }

    private void ApplyMode(SkeletonBodyMode previous)
    {
        var collider = (BoneCollider)Collider;
        RemoveConstraints();
        if (_mode == SkeletonBodyMode.Animated)
        {
            // Kinematic again, the velocities are kept until the next step drives the bodies
            for (int i = 0; i < _bodies.Length; i++)
                Body(i).SetLocalInertia(default);
            StartBlend();
            return;
        }

        if (previous == SkeletonBodyMode.Animated)
        {
            _blendRemaining = 0f;
            for (int i = 0; i < _bodies.Length; i++)
                Body(i).SetLocalInertia(Inertia(collider, i));
        }

        AddConstraints(collider);
    }

    private void AddConstraints(BoneCollider collider)
    {
        var simulation = Simulation!.Simulation;
        var skeleton = Entity.Get<ModelComponent>()!.Skeleton;

        // Joint frames come from the rest pose of the skeleton, placed where the entity is
        var nodes = skeleton.Nodes;
        var rest = new Matrix[nodes.Length];
        for (int n = 0; n < nodes.Length; n++)
        {
            if (nodes[n].ParentIndex < 0)
            {
                rest[n] = skeleton.NodeTransformations[n].LocalMatrix;
                continue;
            }

            ref readonly var transform = ref nodes[n].Transform;
            Matrix.Transformation(in transform.Scale, in transform.Rotation, in transform.Position, out var local);
            rest[n] = local * rest[nodes[n].ParentIndex];
        }

        var springs = new SpringSettings(30f, 1f);
        for (int i = 0; i < _bodies.Length; i++)
        {
            int parent = _jointParents[i];
            if (parent < 0)
                continue;

            var a = BodyPose(rest[_nodes[parent]], collider.GetCreatedLocal(parent));
            var b = BodyPose(rest[_nodes[i]], collider.GetCreatedLocal(i));
            var toLocalA = NQuaternion.Conjugate(a.Orientation);
            var toLocalB = NQuaternion.Conjugate(b.Orientation);
            if (_nodes[i] == _nodes[parent])
            {
                Add(parent, i, new Weld { LocalOffset = NVector3.Transform(b.Position - a.Position, toLocalA), LocalOrientation = toLocalA * b.Orientation, SpringSettings = springs });
                continue;
            }

            var anchor = rest[_nodes[i]].TranslationVector;
            var direction = b.Position.ToStride() - anchor;
            // A shape centered on its node has no direction of its own, the Y axis of the node is used instead
            direction = direction.LengthSquared() > 1e-8f ? direction : Vector3.TransformNormal(Vector3.UnitY, rest[_nodes[i]]);
            direction.Normalize();
            var axis = direction.ToNumeric();
            var basis = Quaternion.BetweenDirections(Vector3.UnitZ, direction).ToNumeric();
            var limits = FindJoint(GetBoneShape(i).NodeName);

            Add(parent, i, new BallSocket { LocalOffsetA = NVector3.Transform(anchor.ToNumeric() - a.Position, toLocalA), LocalOffsetB = NVector3.Transform(anchor.ToNumeric() - b.Position, toLocalB), SpringSettings = springs });
            Add(parent, i, new SwingLimit { AxisLocalA = NVector3.Transform(axis, toLocalA), AxisLocalB = NVector3.Transform(axis, toLocalB), MaximumSwingAngle = limits.MaximumSwingAngle, SpringSettings = springs });
            Add(parent, i, new TwistLimit { LocalBasisA = toLocalA * basis, LocalBasisB = toLocalB * basis, MinimumAngle = limits.MinimumTwistAngle, MaximumAngle = limits.MaximumTwistAngle, SpringSettings = springs });

            if (_mode is SkeletonBodyMode.Active or SkeletonBodyMode.Standing)
                _servos[i] = Add(parent, i, Servo(toLocalA * b.Orientation));
        }

        if (_mode == SkeletonBodyMode.Standing)
            AddUprightServos(collider, nodes);

        ConstraintHandle Add<TDescription>(int a, int b, in TDescription description) where TDescription : unmanaged, ITwoBodyConstraintDescription<TDescription>
        {
            var handle = simulation.Solver.Add(_bodies[a], _bodies[b], description);
            _constraints.Add(handle);
            return handle;
        }
    }

    private BoneJoint FindJoint(string nodeName)
    {
        foreach (var joint in Joints)
        {
            if (joint.NodeName == nodeName)
                return joint;
        }

        return DefaultJoint;
    }

    private AngularServo Servo(NQuaternion target) => new()
    {
        TargetRelativeRotationLocalA = target,
        SpringSettings = new SpringSettings(_activeSpringFrequency, _activeSpringDampingRatio),
        ServoSettings = new ServoSettings(float.MaxValue, 0f, _activeMaximumForce),
    };

    /// <summary> Holds up the bodies joined to no other one (the hips, usually) and the topmost body at rest (the head), so the whole trunk is lifted rather than turned from its base </summary>
    private void AddUprightServos(BoneCollider collider, ModelNodeDefinition[] nodes)
    {
        // Heights in the rest pose, in the space of the skeleton
        var rest = new Matrix[nodes.Length];
        for (int n = 0; n < nodes.Length; n++)
        {
            ref readonly var transform = ref nodes[n].Transform;
            Matrix.Transformation(in transform.Scale, in transform.Rotation, in transform.Position, out var local);
            rest[n] = nodes[n].ParentIndex < 0 ? local : local * rest[nodes[n].ParentIndex];
        }

        float mass = 0f;
        int root = -1, top = -1;
        float rootHeight = 0f, topHeight = float.MinValue;
        for (int i = 0; i < _bodies.Length; i++)
        {
            var inertia = Inertia(collider, i);
            mass += inertia.InverseMass > 0f ? 1f / inertia.InverseMass : 0f;
            float height = Vector3.TransformCoordinate(collider.GetCreatedLocal(i).Position, rest[_nodes[i]]).Y;
            if (_jointParents[i] < 0)
            {
                Add(i);
                if (root < 0)
                    (root, rootHeight) = (i, height);
            }
            else if (_nodes[i] != _nodes[_jointParents[i]] && height > topHeight)
            {
                (top, topHeight) = (i, height);
            }
        }

        // Forces in proportion to the weight of the whole character; turning acts over the height of the trunk
        _bonesMass = mass;
        _standingLever = top >= 0 ? MathF.Max(0.1f, (topHeight - rootHeight) * _nodeScales[root].Y) : 0.5f;
        _heldConstraintCount = -1;
        UpdateStandingStrength();
        // A topmost body barely above the root adds nothing to holding the trunk up
        if (top >= 0 && topHeight - rootHeight > 0.1f)
            Add(top);

        void Add(int body)
        {
            var pose = Body(body).Pose;
            var solver = Simulation!.Simulation.Solver;
            var linear = solver.Add(_bodies[body], Lift(pose.Position, 0f));
            var angular = solver.Add(_bodies[body], Turn(pose.Orientation));
            _constraints.Add(linear);
            _constraints.Add(angular);
            _upright.Add((body, linear, angular));
        }
    }

    /// <summary> The inertia of the body at <paramref name="index"/> once dynamic, from the mass of its shape </summary>
    private static BodyInertia Inertia(BoneCollider collider, int index) => collider.GetCreatedInertia(index);

    /// <summary> Sets the forces of the Standing servos from the weight of the bones and of the bodies other components join to them </summary>
    private void UpdateStandingStrength()
    {
        // Contacts come and go every step and hold nothing up: only joints count
        var solver = Simulation!.Simulation.Solver;
        int count = 0;
        for (int i = 0; i < _bodies.Length; i++)
        {
            var constraints = Body(i).Constraints;
            for (int c = 0; c < constraints.Count; c++)
            {
                if (NarrowPhase.IsContactConstraintType(solver.HandleToConstraint[constraints[c].ConnectingConstraintHandle.Value].TypeId) == false)
                    count++;
            }
        }
        if (count == _heldConstraintCount)
            return;
        _heldConstraintCount = count;

        float gravity = Simulation!.PoseGravity.Length();
        _standingForce = _standingStrength * (_bonesMass + HeldMass()) * (gravity > 0f ? gravity : 9.81f);
        _standingTorque = _standingForce * _standingLever;
    }

    /// <summary> The mass of the awake dynamic bodies of other components joined to the bones, each counted once </summary>
    private float HeldMass()
    {
        var simulation = Simulation!.Simulation;
        var own = new HashSet<ConstraintHandle>(_constraints);
        var collector = new BodyCollector { Indices = new HashSet<int>() };
        for (int i = 0; i < _bodies.Length; i++)
        {
            var constraints = Body(i).Constraints;
            for (int c = 0; c < constraints.Count; c++)
            {
                var handle = constraints[c].ConnectingConstraintHandle;
                // Only active constraints can be enumerated; the Standing servos keep the character awake
                ref var location = ref simulation.Solver.HandleToConstraint[handle.Value];
                if (own.Contains(handle) || location.SetIndex != 0 || NarrowPhase.IsContactConstraintType(location.TypeId))
                    continue;
                simulation.Solver.EnumerateConnectedBodyReferences(handle, ref collector);
            }
        }

        float mass = 0f;
        var active = simulation.Bodies.ActiveSet;
        foreach (var index in collector.Indices)
        {
            if (_shapeOfBody.ContainsKey(active.IndexToHandle[index].Value))
                continue;
            float inverseMass = active.DynamicsState[index].Inertia.Local.InverseMass;
            mass += inverseMass > 0f ? 1f / inverseMass : 0f;
        }
        return mass;
    }

    private struct BodyCollector : BodyIndexLoop
    {
        public HashSet<int> Indices;

        public void LoopBody(int bodyIndex) => Indices.Add(bodyIndex);
    }

    private OneBodyLinearServo Lift(NVector3 target, float maximumForce) => new()
    {
        Target = target,
        SpringSettings = new SpringSettings(_standingLinearSpringFrequency, _activeSpringDampingRatio),
        ServoSettings = new ServoSettings(float.MaxValue, 0f, maximumForce),
    };

    /// <summary> Finds the closest collidable under <paramref name="origin"/> within <paramref name="reach"/> which does not belong to the character </summary>
    private CollidableComponent? FindGround(Vector3 origin, float reach, out float height)
    {
        var handler = new GroundHitHandler(this);
        Simulation!.Simulation.RayCast(origin.ToNumeric(), -NVector3.UnitY, reach, Simulation.BufferPool, ref handler);
        height = origin.Y - handler.Distance;
        return handler.Ground;
    }

    /// <summary> Whether <paramref name="collidable"/> is part of the character: this component, a collidable of its entity or of a child, or one attached to its bodies </summary>
    private bool BelongsToCharacter(CollidableComponent collidable)
    {
        if (ReferenceEquals(collidable, this) || collidable.IsAttachedTo(this))
            return true;
        for (var entity = collidable.Entity; entity is not null; entity = entity.Transform.Parent?.Entity)
        {
            if (ReferenceEquals(entity, Entity))
                return true;
        }
        return false;
    }

    private OneBodyAngularServo Turn(NQuaternion target) => new()
    {
        TargetOrientation = target,
        SpringSettings = new SpringSettings(_standingAngularSpringFrequency, _activeSpringDampingRatio),
        ServoSettings = new ServoSettings(float.MaxValue, 0f, _standingTorque),
    };

    private void RemoveConstraints()
    {
        _upright.Clear();
        _ground = null;
        if (_constraints.Count == 0)
            return;

        // Constraints of a sleeping island are moved back to the solver's active set first
        var first = Body(0);
        first.Awake = true;
        var solver = Simulation!.Simulation.Solver;
        foreach (var handle in _constraints)
            solver.Remove(handle);
        _constraints.Clear();
    }

    /// <summary> Records the pose of the nodes driven by the ragdoll, the start of the blend toward the animation </summary>
    private void StartBlend()
    {
        _blendLength = _blendRemaining = _blendDuration;
        if (_drivenSkeleton is null || _blendLength <= 0f)
        {
            ReleaseNodes();
            return;
        }

        for (int k = 0; k < _drivers.Length; k++)
        {
            _drivenSkeleton.NodeTransformations[_nodes[_drivers[k]]].WorldMatrix.Decompose(out _, out Quaternion rotation, out var position);
            _blendFrom[k] = (position, rotation);
        }
    }

    /// <summary> Hands the nodes back to the animation </summary>
    private void ReleaseNodes()
    {
        if (_drivenSkeleton is { } skeleton)
        {
            foreach (var body in _drivers)
                skeleton.NodeTransformations[_nodes[body]].Flags &= ~ModelNodeFlags.OverrideWorldMatrix;
        }

        _drivenSkeleton = null;
        _blendRemaining = 0f;
    }

    /// <summary> The world matrices the animation gives to the nodes, ignoring the ones written by the ragdoll </summary>
    private void ComputeAnimatedMatrices(SkeletonUpdater skeleton)
    {
        var nodes = skeleton.Nodes;
        if (_animated.Length < nodes.Length)
            _animated = new Matrix[nodes.Length];

        for (int n = 0; n < nodes.Length; n++)
        {
            ref var local = ref skeleton.NodeTransformations[n].LocalMatrix;
            int parent = nodes[n].ParentIndex;
            _animated[n] = parent < 0 ? local : local * _animated[parent];
        }
    }

    private void BlendNodes(SkeletonUpdater skeleton, float deltaTime)
    {
        _blendRemaining = MathF.Max(0f, _blendRemaining - deltaTime);
        float amount = 1f - _blendRemaining / _blendLength;
        ComputeAnimatedMatrices(skeleton);
        for (int k = 0; k < _drivers.Length; k++)
        {
            int node = _nodes[_drivers[k]];
            _animated[node].Decompose(out var scale, out Quaternion rotation, out var position);
            var (fromPosition, fromRotation) = _blendFrom[k];
            rotation = Quaternion.Slerp(fromRotation, rotation, amount);
            position = Vector3.Lerp(fromPosition, position, amount);
            Matrix.Transformation(in scale, in rotation, in position, out skeleton.NodeTransformations[node].WorldMatrix);
        }

        if (_blendRemaining <= 0f)
            ReleaseNodes();
    }

    /// <summary> Points each servo at the rotation between its two bodies in the animated pose </summary>
    private void UpdateServos(SkeletonUpdater skeleton)
    {
        var collider = (BoneCollider)Collider;
        var solver = Simulation!.Simulation.Solver;
        ComputeAnimatedMatrices(skeleton);
        for (int i = 0; i < _bodies.Length; i++)
        {
            if (_jointParents[i] < 0 || _nodes[i] == _nodes[_jointParents[i]])
                continue;

            var a = BodyPose(_animated[_nodes[_jointParents[i]]], collider.GetCreatedLocal(_jointParents[i]));
            var b = BodyPose(_animated[_nodes[i]], collider.GetCreatedLocal(i));
            solver.ApplyDescription(_servos[i], Servo(NQuaternion.Conjugate(a.Orientation) * b.Orientation));
        }

        if (_upright.Count > 0)
        {
            UpdateStandingStrength();

            // Heights in the animated pose are measured from the feet, the origin of the entity
            float feet = _animated[0].TranslationVector.Y;
            var (root, _, _) = _upright[0];
            float standing = BodyPose(_animated[_nodes[root]], collider.GetCreatedLocal(root)).Position.Y - feet;
            _ground = FindGround(Body(root).Pose.Position.ToStride(), MathF.Max(0f, standing) * 1.3f + 0.1f, out _groundHeight);
        }

        foreach (var (body, linear, angular) in _upright)
        {
            var animated = BodyPose(_animated[_nodes[body]], collider.GetCreatedLocal(body));
            var current = Body(body).Pose.Position;
            float target = _groundHeight + animated.Position.Y - _animated[0].TranslationVector.Y;
            // Only lifts, and only from the ground: otherwise no force at all, which would also brake the fall and any sideways motion
            bool lift = _ground is not null && current.Y < target;
            solver.ApplyDescription(linear, Lift(new NVector3(current.X, lift ? target : current.Y, current.Z), lift ? _standingForce : 0f));
            solver.ApplyDescription(angular, Turn(animated.Orientation));
        }

        // The animation keeps moving the targets, the ragdoll must not fall asleep
        var first = Body(0);
        first.Awake = true;
    }

    /// <summary> Places the nodes driven by the ragdoll at the pose of their body </summary>
    private void WriteNodes()
    {
        var skeleton = Entity.Get<ModelComponent>()!.Skeleton;
        _drivenSkeleton = skeleton;
        foreach (var body in _drivers)
        {
            ref var node = ref skeleton.NodeTransformations[_nodes[body]];
            node.WorldMatrix = NodeMatrix(body, Body(body).Pose);
            node.Flags |= ModelNodeFlags.OverrideWorldMatrix;
        }
    }

    private void FollowNodes(float deltaTime)
    {
        var collider = (BoneCollider)Collider;
        var skeleton = Entity.Get<ModelComponent>()!.Skeleton;
        if (_blendRemaining > 0f)
            BlendNodes(skeleton, deltaTime);

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

        public void SimulationUpdate(BepuSimulation simulation, float simTimeStep)
        {
            if (owner._mode == SkeletonBodyMode.Animated)
                owner.FollowNodes(simTimeStep);
            else if (owner._mode is SkeletonBodyMode.Active or SkeletonBodyMode.Standing)
                owner.UpdateServos(owner.Entity.Get<ModelComponent>()!.Skeleton);
        }

        public void AfterSimulationUpdate(BepuSimulation simulation, float simTimeStep)
        {
            if (owner._mode != SkeletonBodyMode.Animated)
                owner.WriteNodes();
        }
    }

    /// <summary> Keeps the closest hit on a collidable which does not belong to the character </summary>
    private struct GroundHitHandler(SkeletonBodyComponent owner) : IRayHitHandler
    {
        public CollidableComponent? Ground;
        public float Distance;

        public bool AllowTest(CollidableReference collidable) => owner.BelongsToCharacter(owner.Simulation!.GetComponent(collidable)) == false;

        public bool AllowTest(CollidableReference collidable, int childIndex) => true;

        public void OnRayHit(in RayData ray, ref float maximumT, float t, NVector3 normal, CollidableReference collidable, int childIndex)
        {
            if (t >= maximumT)
                return;
            maximumT = t;
            Distance = t;
            Ground = owner.Simulation!.GetComponent(collidable);
        }
    }

    /// <summary> Reports hits on the shape of a single body under the index of that body </summary>
    private struct ChildRayHitHandler<T>(T inner, int child) : IShapeRayHitHandler where T : struct, IShapeRayHitHandler
    {
        public T Inner = inner;

        public bool AllowTest(int childIndex) => Inner.AllowTest(child);

        public void OnRayHit(in RayData ray, ref float maximumT, float t, NVector3 normal, int childIndex) => Inner.OnRayHit(ray, ref maximumT, t, normal, child);
    }
}
