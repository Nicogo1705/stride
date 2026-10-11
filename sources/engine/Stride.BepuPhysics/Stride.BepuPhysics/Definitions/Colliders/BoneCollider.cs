// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.ComponentModel;
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

    private readonly List<(BoneShape Bone, float Scale, TypedIndex Index, BodyInertia Inertia)> _created = new();
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
    /// <remarks> See <see cref="Generate(Skeleton, float)"/> </remarks>
    [DataMember(20)]
    [DefaultValue(0f)]
    public float GeneratedRadius { get; set; }

    /// <summary> The amount of shapes created, one per node found in the skeleton </summary>
    [DataMemberIgnore]
    public int Transforms => _created.Count;

    CollidableComponent? ICollider.Component { get => _component; set => _component = value; }

    /// <summary>
    /// Creates a capsule between every node of a skeleton and each of its children, in the rest pose.
    /// </summary>
    /// <remarks> Segments starting from the root node, or shorter than <paramref name="radius"/>, are skipped. </remarks>
    /// <param name="skeleton"> The skeleton to build the shapes from </param>
    /// <param name="radius"> The radius of each capsule, in the space of the nodes </param>
    /// <returns> One shape per segment, in node order </returns>
    public static List<BoneShape> Generate(Skeleton skeleton, float radius) => Generate(skeleton.Nodes, radius);

    internal static List<BoneShape> Generate(ModelNodeDefinition[] nodes, float radius)
    {
        var shapes = new List<BoneShape>();
        for (int child = 0; child < nodes.Length; child++)
        {
            int parent = nodes[child].ParentIndex;
            if (parent <= 0)
                continue;

            var segment = nodes[child].Transform.Position;
            float length = segment.Length();
            if (length < radius)
                continue;

            shapes.Add(new BoneShape
            {
                NodeName = nodes[parent].Name,
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

    internal TypedIndex GetCreatedIndex(int index) => _created[index].Index;

    /// <summary> The inertia of a created shape from its <see cref="ColliderBase.Mass"/>, used when its body is not kinematic </summary>
    internal BodyInertia GetCreatedInertia(int index) => _created[index].Inertia;

    void ICollider.GetLocalTransforms(CollidableComponent collidable, Span<ShapeTransform> transforms)
    {
        for (int i = 0; i < _created.Count; i++)
        {
            var (bone, scale, _, _) = _created[i];
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

        foreach (var (bone, scale) in resolved)
        {
            var shape = bone.Shape;
            var created = shape switch
            {
                BoxCollider box => Add(shapes, new Box(box.Size.X * scale, box.Size.Y * scale, box.Size.Z * scale), shape.Mass),
                SphereCollider sph => Add(shapes, new Sphere(sph.Radius * scale), shape.Mass),
                CapsuleCollider cap => Add(shapes, new Capsule(cap.Radius * scale, cap.Length * scale), shape.Mass),
                CylinderCollider cyl => Add(shapes, new Cylinder(cyl.Radius * scale, cyl.Length * scale), shape.Mass),
                _ => throw new NotSupportedException($"{nameof(BoneCollider)} does not support {shape.GetType().Name}, use a box, sphere, capsule or cylinder"),
            };
            _created.Add((bone, scale, created.Index, created.Inertia));
        }

        index = _created[0].Index;
        return true;
    }

    private static (TypedIndex Index, BodyInertia Inertia) Add<TShape>(Shapes shapes, TShape shape, float mass) where TShape : unmanaged, IConvexShape
    {
        return (shapes.Add(shape), shape.ComputeInertia(mass));
    }

    void ICollider.Detach(Shapes shapes, BufferPool pool, TypedIndex index)
    {
        foreach (var (_, _, typedIndex, _) in _created)
            shapes.Remove(typedIndex);
        _created.Clear();
    }

    void ICollider.AppendModel(List<BasicMeshBuffers> buffer, ShapeCacheSystem shapeCache, out object? cache)
    {
        cache = null;
        foreach (var (bone, _, _, _) in _created)
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
