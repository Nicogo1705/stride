// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.ComponentModel;
using System.Diagnostics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.Trees;
using BepuUtilities.Memory;
using Stride.BepuPhysics.Systems;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Diagnostics;
using Stride.Core.Mathematics;
using Stride.Rendering;
using NRigidPose = BepuPhysics.RigidPose;

namespace Stride.BepuPhysics.Definitions.Colliders;

/// <summary>
/// The shapes of a <see cref="SkeletonBodyComponent"/>, each one following a node of the skeleton of the <see cref="Engine.ModelComponent"/> on the same entity.
/// </summary>
/// <remarks> Created by the <see cref="SkeletonBodyComponent"/>, it cannot be used by other collidables. </remarks>
[DataContract]
[NonInstantiable]
public sealed class BoneCollider : ICollider
{
    private static readonly Logger Logger = GlobalLogger.GetLogger(nameof(BoneCollider));

    private readonly List<(BoneShape Bone, float Scale, Vector3 Position, Quaternion Rotation, TypedIndex Index, BodyInertia Inertia)> _created = new();
    private CollidableComponent? _component;

    /// <summary>
    /// The shapes and the nodes they follow, several shapes may follow the same node.
    /// </summary>
    /// <remarks> Changes are applied when the component is added to the scene, or by <see cref="SkeletonBodyComponent.Rebuild"/> </remarks>
    [MemberCollection(NotNullItems = true)]
    [DataMember(10)]
    public List<BoneShape> Bones { get; } = new();

    /// <summary>
    /// When <see cref="Bones"/> is empty, the radius of the capsules generated between every node and each of its children, zero to generate none.
    /// </summary>
    /// <remarks> See <see cref="Generate(Model, float)"/>; changes are applied when the component is added to the scene, or by <see cref="SkeletonBodyComponent.Rebuild"/>. </remarks>
    [DataMember(20)]
    [DataMemberRange(0, 3)]
    [DefaultValue(0f)]
    public float GeneratedRadius { get; set; }

    /// <summary> The amount of shapes created, one per node found in the skeleton </summary>
    [DataMemberIgnore]
    public int Transforms => _created.Count;

    CollidableComponent? ICollider.Component { get => _component; set => _component = value; }

    /// <summary>
    /// Creates a capsule along every bone of a model's skeleton, from a node to each of its children, in the rest pose.
    /// </summary>
    /// <remarks>
    /// When the meshes of the model are skinned, only the nodes which deform them get capsules, so the root, an armature node or helpers
    /// such as footsteps and IK targets get none, and their segments only reach a non-deforming child when no other child deforms,
    /// like an end bone rather than a weapon slot. A child with no child of its own next to other children, like a twist or end bone,
    /// gets no capsule either, nor does a segment shorter than <paramref name="radius"/>.
    /// Capsules weigh as much as water of their volume, once scaled by their node.
    /// </remarks>
    /// <param name="model"> The model whose skeleton the shapes are built from </param>
    /// <param name="radius"> The radius of each capsule, in the space of the nodes </param>
    /// <returns> One shape per segment, in node order; none without a skeleton </returns>
    public static List<BoneShape> Generate(Model model, float radius)
    {
        var shapes = new List<BoneShape>();
        if (model.Skeleton?.Nodes is not { } nodes)
            return shapes;

        var deforming = new HashSet<int>();
        foreach (var mesh in model.Meshes)
        {
            if (mesh.Skinning?.Bones is { } bones)
            {
                foreach (var bone in bones)
                    deforming.Add(bone.NodeIndex);
            }
        }

        var children = new int[nodes.Length];
        var deformingChildren = new int[nodes.Length];
        for (int i = 0; i < nodes.Length; i++)
        {
            if (nodes[i].ParentIndex >= 0)
            {
                children[nodes[i].ParentIndex]++;
                if (deforming.Contains(i))
                    deformingChildren[nodes[i].ParentIndex]++;
            }
        }

        for (int child = 0; child < nodes.Length; child++)
        {
            int parent = nodes[child].ParentIndex;
            if (parent <= 0 || (deforming.Count > 0 && deforming.Contains(parent) == false) || (children[child] == 0 && children[parent] > 1))
                continue;
            // A helper hanging from a deforming bone, like a weapon slot, next to the bones carrying the chain
            if (deforming.Count > 0 && deforming.Contains(child) == false && deformingChildren[parent] > 0)
                continue;

            var segment = nodes[child].Transform.Position;
            float length = segment.Length();
            if (length < radius)
                continue;

            shapes.Add(new BoneShape
            {
                NodeName = nodes[parent].Name,
                AlongBone = false,
                Shape = new CapsuleCollider
                {
                    Radius = radius,
                    // The capsule spans the segment, its caps included
                    Length = MathF.Max(length - 2f * radius, 1e-3f),
                    PositionLocal = segment * 0.5f,
                    RotationLocal = Quaternion.BetweenDirections(Vector3.UnitY, segment / length),
                },
            });
        }

        return shapes;
    }

    /// <summary> The entry and the scale of a created shape </summary>
    internal (BoneShape Bone, float Scale) GetCreated(int index) => (_created[index].Bone, _created[index].Scale);

    /// <summary> The pose of a created shape in the space of its node, along the bone or not </summary>
    internal (Vector3 Position, Quaternion Rotation) GetCreatedLocal(int index) => (_created[index].Position, _created[index].Rotation);

    internal TypedIndex GetCreatedIndex(int index) => _created[index].Index;

    /// <summary> The inertia of a created shape from its <see cref="ColliderBase.Mass"/>, used when its body is not kinematic </summary>
    internal BodyInertia GetCreatedInertia(int index) => _created[index].Inertia;

    void ICollider.GetLocalTransforms(CollidableComponent collidable, Span<ShapeTransform> transforms)
    {
        for (int i = 0; i < _created.Count; i++)
        {
            var (bone, scale, _, _, _, _) = _created[i];
            // The node and the local offset are part of the pose of each body
            transforms[i].PositionLocal = Vector3.Zero;
            transforms[i].RotationLocal = Quaternion.Identity;
            transforms[i].Scale = bone.Shape switch
            {
                BoxCollider box => box.Size * scale,
                CylinderCollider cyl => new Vector3(cyl.Radius, cyl.Length, cyl.Radius) * scale,
                SphereCollider sph => new Vector3(sph.Radius * scale),
                _ => new Vector3(scale),
            };
        }
    }

    bool ICollider.TryAttach(Shapes shapes, BufferPool pool, ShapeCacheSystem shapeCache, bool shouldCalculateInertia, out TypedIndex index, out Vector3 centerOfMass, out BodyInertia inertia)
    {
        centerOfMass = Vector3.Zero;
        inertia = default;
        index = default;
        _created.Clear();

        if (_component is not SkeletonBodyComponent owner)
        {
            Logger.Error($"{nameof(BoneCollider)} only works on a {nameof(SkeletonBodyComponent)}, the {_component?.GetType().Name} on '{_component?.Entity?.Name}' is left out of the simulation");
            return false;
        }
        if (owner.TryResolveBones(out var resolved) == false)
            return false;

        bool generated = Bones.Count == 0;
        foreach (var (bone, scale, position, rotation) in resolved)
        {
            var shape = bone.Shape;
            var created = shape switch
            {
                BoxCollider box => Add(shapes, new Box(box.Size.X * scale, box.Size.Y * scale, box.Size.Z * scale), shape.Mass, generated),
                SphereCollider sph => Add(shapes, new Sphere(sph.Radius * scale), shape.Mass, generated),
                CapsuleCollider cap => Add(shapes, new Capsule(cap.Radius * scale, cap.Length * scale), shape.Mass, generated),
                CylinderCollider cyl => Add(shapes, new Cylinder(cyl.Radius * scale, cyl.Length * scale), shape.Mass, generated),
                _ => throw new UnreachableException(), // TryResolveBones skips the other shapes
            };
            _created.Add((bone, scale, position, rotation, created.Index, created.Inertia));
        }

        BoundInertia();

        index = _created[0].Index;
        return true;
    }

    /// <summary> Bodies rotating thousands of times more easily than the rest of the skeleton, like short and thin bones, make the joints blow up: their inertia is raised to a bounded ratio </summary>
    private void BoundInertia()
    {
        const float maximumRatio = 100f;
        float hardest = float.MaxValue;
        foreach (var created in _created)
        {
            ref readonly var tensor = ref created.Inertia.InverseInertiaTensor;
            hardest = MathF.Min(hardest, MathF.Min(tensor.XX, MathF.Min(tensor.YY, tensor.ZZ)));
        }

        if (hardest <= 0f)
            return;
        float limit = hardest * maximumRatio;
        for (int i = 0; i < _created.Count; i++)
        {
            var entry = _created[i];
            ref var tensor = ref entry.Inertia.InverseInertiaTensor;
            // The shapes are not rotated in their body, their tensors are diagonal
            tensor.XX = MathF.Min(tensor.XX, limit);
            tensor.YY = MathF.Min(tensor.YY, limit);
            tensor.ZZ = MathF.Min(tensor.ZZ, limit);
            _created[i] = entry;
        }
    }

    private static (TypedIndex Index, BodyInertia Inertia) Add<TShape>(Shapes shapes, TShape shape, float mass, bool fromVolume) where TShape : unmanaged, IConvexShape
    {
        if (fromVolume)
        {
            // The density of water, close to that of a body
            const float density = 1000f;
            mass = density * Volume(shape);
        }
        return (shapes.Add(shape), shape.ComputeInertia(mass));
    }

    private static float Volume<TShape>(TShape shape) where TShape : unmanaged, IConvexShape => shape switch
    {
        Box box => box.Width * box.Height * box.Length,
        Sphere sphere => 4f / 3f * MathF.PI * sphere.Radius * sphere.Radius * sphere.Radius,
        Capsule capsule => MathF.PI * capsule.Radius * capsule.Radius * (capsule.Length + 4f / 3f * capsule.Radius),
        Cylinder cylinder => MathF.PI * cylinder.Radius * cylinder.Radius * cylinder.Length,
        _ => 1f,
    };

    void ICollider.Detach(Shapes shapes, BufferPool pool, TypedIndex index)
    {
        foreach (var (_, _, _, _, typedIndex, _) in _created)
            shapes.Remove(typedIndex);
        _created.Clear();
    }

    void ICollider.AppendModel(List<BasicMeshBuffers> buffer, ShapeCacheSystem shapeCache, out object? cache)
    {
        cache = null;
        foreach (var (bone, _, _, _, _, _) in _created)
        {
            buffer.Add(bone.Shape switch
            {
                BoxCollider => shapeCache._boxShapeData,
                CapsuleCollider cap => shapeCache.BuildCapsule(cap),
                CylinderCollider => shapeCache._cylinderShapeData,
                _ => shapeCache._sphereShapeData,
            });
        }
    }

    void ICollider.RayTest<TRayHitHandler>(Shapes shapes, TypedIndex shapeIndex, in NRigidPose pose, in RayData ray, ref float maximumT, ref TRayHitHandler hitHandler, BufferPool pool)
    {
        shapes[shapeIndex.Type].RayTest(shapeIndex.Index, pose, ray, ref maximumT, pool, ref hitHandler);
    }
}
