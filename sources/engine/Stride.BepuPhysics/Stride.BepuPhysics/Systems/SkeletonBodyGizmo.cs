// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.BepuPhysics.Definitions.Colliders;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Engine.Gizmos;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;
using Buffer = Stride.Graphics.Buffer;

namespace Stride.BepuPhysics.Systems;

/// <summary>
/// Draws the bone shapes of a <see cref="SkeletonBodyComponent"/> on the nodes of its model in the editor, generated ones included.
/// </summary>
/// <remarks> The bodies only exist at runtime, so the shapes are placed from the skeleton and rebuilt whenever their settings change. </remarks>
[GizmoComponent(typeof(SkeletonBodyComponent), false)]
public sealed class SkeletonBodyGizmo : IEntityGizmo
{
    private readonly SkeletonBodyComponent _component;
    private readonly List<(BoneShape Bone, float Scale, Vector3 Position, Quaternion Rotation)> _resolved = new();
    private readonly List<int> _nodes = new();
    private readonly List<Vector3> _scales = new();
    private readonly List<(ModelComponent Model, Vector3 Size)> _shapes = new();
    private IServiceRegistry _services = null!;
    private Scene _editorScene = null!;
    private Material? _material, _materialOnSelect;
    private int _signature;
    private bool _selected, _enabled;

    /// <summary> Creates the gizmo of <paramref name="component"/> </summary>
    public SkeletonBodyGizmo(SkeletonBodyComponent component)
    {
        _component = component;
    }

    /// <inheritdoc/>
    public bool IsEnabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            UpdateVisibility();
        }
    }

    /// <inheritdoc/>
    public float SizeFactor { get; set; }

    /// <inheritdoc/>
    public bool IsSelected
    {
        get => _selected;
        set
        {
            _selected = value;
            UpdateVisibility();
        }
    }

    /// <inheritdoc/>
    public void Initialize(IServiceRegistry services, Scene editorScene)
    {
        _services = services;
        _editorScene = editorScene;
    }

    /// <inheritdoc/>
    public bool HandlesComponentId(OpaqueComponentId pickedComponentId, out Entity? selection)
    {
        foreach (var (model, _) in _shapes)
        {
            if (pickedComponentId.Match(model))
            {
                selection = _component.Entity;
                return true;
            }
        }
        selection = null;
        return false;
    }

    /// <inheritdoc/>
    public void Update()
    {
        if ((_selected || _enabled) == false)
            return;

        if (_component.Entity?.Get<ModelComponent>() is not { Model: not null, Skeleton: not null } model || _component.Collider is not BoneCollider collider)
        {
            ClearShapes();
            _signature = 0;
            return;
        }

        var signature = Signature(model, collider);
        if (signature != _signature)
        {
            _signature = signature;
            Rebuild(model, collider);
        }

        var nodes = model.Skeleton.NodeTransformations;
        for (int i = 0; i < _shapes.Count; i++)
        {
            // Placed as the body would be: see SkeletonBodyComponent.BodyPose
            var (_, _, position, rotation) = _resolved[i];
            ref var node = ref nodes[_nodes[i]].WorldMatrix;
            node.Decompose(out var nodeScale, out Quaternion nodeRotation, out _);
            var size = _shapes[i].Size * (MathF.Abs(nodeScale.X + nodeScale.Y + nodeScale.Z) / 3f);
            var worldRotation = rotation * nodeRotation;
            var worldPosition = Vector3.TransformCoordinate(position, node);
            Matrix.Transformation(ref size, ref worldRotation, ref worldPosition, out var world);
            _shapes[i].Model.Entity.Transform.LocalMatrix = world;
        }
    }

    /// <inheritdoc/>
    public void Dispose() => ClearShapes();

    private void Rebuild(ModelComponent model, BoneCollider collider)
    {
        ClearShapes();
        _resolved.Clear();
        _nodes.Clear();
        _scales.Clear();
        SkeletonBodyComponent.ResolveBones(model, collider, _resolved, _nodes, _scales, warnFor: null);

        var shapeCache = _services.GetOrCreate<ShapeCacheSystem>();
        var graphicsDevice = _services.GetSafeServiceAs<IGraphicsDeviceService>().GraphicsDevice;
        _material ??= CreateMaterial(graphicsDevice, 0.05f);
        _materialOnSelect ??= CreateMaterial(graphicsDevice, 0.5f);
        foreach (var (bone, _, _, _) in _resolved)
        {
            // The unit meshes and the scales of BoneCollider.AppendModel and GetLocalTransforms
            var (mesh, size) = bone.Shape switch
            {
                BoxCollider box => (shapeCache._boxShapeData, box.Size),
                CapsuleCollider capsule => (shapeCache.BuildCapsule(capsule), Vector3.One),
                CylinderCollider cylinder => (shapeCache._cylinderShapeData, new Vector3(cylinder.Radius, cylinder.Length, cylinder.Radius)),
                SphereCollider sphere => (shapeCache._sphereShapeData, new Vector3(sphere.Radius)),
                _ => (shapeCache._sphereShapeData, Vector3.Zero),
            };

            var vertexBuffer = Buffer.Vertex.New(graphicsDevice, mesh.Vertices);
            var indexBuffer = Buffer.Index.New(graphicsDevice, mesh.Indices);
            var shape = new ModelComponent
            {
                Model = new Model
                {
                    _selected ? _materialOnSelect : _material,
                    new Mesh
                    {
                        Draw = new MeshDraw
                        {
                            PrimitiveType = PrimitiveType.TriangleList,
                            VertexBuffers = [new VertexBufferBinding(vertexBuffer, mesh.Vertices[0].GetLayout(), vertexBuffer.ElementCount)],
                            IndexBuffer = new IndexBufferBinding(indexBuffer, true, indexBuffer.ElementCount),
                            DrawCount = indexBuffer.ElementCount,
                        },
                    },
                },
                RenderGroup = IEntityGizmo.PickingRenderGroup,
                Enabled = _selected || _enabled,
            };

            var entity = new Entity($"{nameof(SkeletonBodyGizmo)} for {_component.Entity.Name}") { shape };
            entity.Transform.UseTRS = false;
            entity.Scene = _editorScene;
            vertexBuffer.DisposeBy(entity);
            indexBuffer.DisposeBy(entity);
            _shapes.Add((shape, size));
        }
    }

    private void ClearShapes()
    {
        foreach (var (model, _) in _shapes)
        {
            model.Entity.Scene = null;
            model.Entity.Dispose();
        }
        _shapes.Clear();
    }

    private void UpdateVisibility()
    {
        foreach (var (model, _) in _shapes)
        {
            model.Materials[0] = _selected ? _materialOnSelect : _material;
            model.Enabled = _selected || _enabled;
        }
    }

    /// <summary> Changes whenever a setting the shapes depend on changes, the property grid edits them in place </summary>
    private static int Signature(ModelComponent model, BoneCollider collider)
    {
        var hash = new HashCode();
        hash.Add(model.Model);
        hash.Add(collider.GeneratedRadius);
        foreach (var bone in collider.Bones)
        {
            hash.Add(bone.NodeName);
            hash.Add(bone.AlongBone);
            hash.Add(bone.Shape.GetType());
            hash.Add(bone.Shape.PositionLocal);
            hash.Add(bone.Shape.RotationLocal);
            switch (bone.Shape)
            {
                case BoxCollider box:
                    hash.Add(box.Size);
                    break;
                case CapsuleCollider capsule:
                    hash.Add(capsule.Radius);
                    hash.Add(capsule.Length);
                    break;
                case CylinderCollider cylinder:
                    hash.Add(cylinder.Radius);
                    hash.Add(cylinder.Length);
                    break;
                case SphereCollider sphere:
                    hash.Add(sphere.Radius);
                    break;
            }
        }
        // Never 0, which stands for no shapes built yet
        return hash.ToHashCode() | 1;
    }

    private static Material CreateMaterial(GraphicsDevice graphicsDevice, float alpha) => Material.New(graphicsDevice, new MaterialDescriptor
    {
        Attributes =
        {
            Emissive = new MaterialEmissiveMapFeature(new ComputeColor(new Color4(0.25f, 0.75f, 0.25f, alpha).ToColorSpace(graphicsDevice.ColorSpace))) { UseAlpha = true },
            Transparency = new MaterialTransparencyBlendFeature(),
        },
    });
}
