// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Stride.BepuPhysics.Definitions;
using Stride.BepuPhysics.Definitions.Colliders;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Materials;
using Buffer = Stride.Graphics.Buffer;

namespace Stride.BepuPhysics.Soft.Tests.VideoDemo;

/// <summary>
/// A humanoid built in code: a skeleton, a mesh skinned to it (one box per hitbox, rigidly bound to its node),
/// its table of hitboxes, and a script animating the nodes.
/// </summary>
internal static class ProceduralCharacter
{
    public const int Root = 0, Hips = 1, Spine = 2, Chest = 3, Head = 4;
    public const int UpperArmL = 5, ForeArmL = 6, HandL = 7, UpperArmR = 8, ForeArmR = 9, HandR = 10;
    public const int ThighL = 11, ShinL = 12, FootL = 13, ThighR = 14, ShinR = 15, FootR = 16;

    /// <summary> Standing, facing +Z, feet on y = 0 </summary>
    public static ModelNodeDefinition[] Nodes() =>
    [
        Node("Root", -1, Vector3.Zero),
        Node("Hips", Root, new Vector3(0f, 0.95f, 0f)),
        Node("Spine", Hips, new Vector3(0f, 0.2f, 0f)),
        Node("Chest", Spine, new Vector3(0f, 0.25f, 0f)),
        Node("Head", Chest, new Vector3(0f, 0.3f, 0f)),
        Node("UpperArm.L", Chest, new Vector3(0.22f, 0.22f, 0f)),
        Node("ForeArm.L", UpperArmL, new Vector3(0f, -0.3f, 0f)),
        Node("Hand.L", ForeArmL, new Vector3(0f, -0.28f, 0f)),
        Node("UpperArm.R", Chest, new Vector3(-0.22f, 0.22f, 0f)),
        Node("ForeArm.R", UpperArmR, new Vector3(0f, -0.3f, 0f)),
        Node("Hand.R", ForeArmR, new Vector3(0f, -0.28f, 0f)),
        Node("Thigh.L", Hips, new Vector3(0.11f, -0.05f, 0f)),
        Node("Shin.L", ThighL, new Vector3(0f, -0.42f, 0f)),
        Node("Foot.L", ShinL, new Vector3(0f, -0.42f, 0f)),
        Node("Thigh.R", Hips, new Vector3(-0.11f, -0.05f, 0f)),
        Node("Shin.R", ThighR, new Vector3(0f, -0.42f, 0f)),
        Node("Foot.R", ShinR, new Vector3(0f, -0.42f, 0f)),
    ];

    /// <summary> The table of hitboxes: boxes on the trunk and the feet, a sphere for the head, capsules on the limbs </summary>
    public static List<BoneShape> Hitboxes()
    {
        var shapes = new List<BoneShape>
        {
            Box("Hips", new Vector3(0.34f, 0.22f, 0.2f), new Vector3(0f, 0.04f, 0f), 11f),
            Capsule("Spine", 0.13f, 0.06f, new Vector3(0f, 0.12f, 0f), 8f),
            Box("Chest", new Vector3(0.4f, 0.32f, 0.22f), new Vector3(0f, 0.14f, 0f), 14f),
            new() { NodeName = "Head", Shape = new SphereCollider { Radius = 0.13f, PositionLocal = new Vector3(0f, 0.13f, 0.01f), Mass = 5f } },
        };
        foreach (var side in new[] { "L", "R" })
        {
            shapes.Add(Capsule($"UpperArm.{side}", 0.065f, 0.17f, new Vector3(0f, -0.15f, 0f), 2.5f));
            shapes.Add(Capsule($"ForeArm.{side}", 0.055f, 0.2f, new Vector3(0f, -0.15f, 0f), 2f));
            shapes.Add(Capsule($"Thigh.{side}", 0.085f, 0.26f, new Vector3(0f, -0.21f, 0f), 7f));
            shapes.Add(Capsule($"Shin.{side}", 0.07f, 0.28f, new Vector3(0f, -0.21f, 0f), 4f));
            shapes.Add(Box($"Foot.{side}", new Vector3(0.11f, 0.08f, 0.26f), new Vector3(0f, -0.02f, 0.06f), 1.2f));
        }
        return shapes;
    }

    /// <summary>
    /// A model with the skeleton of <see cref="Nodes"/> and two meshes skinned to it, slightly inside the hitboxes so their wireframe shows:
    /// the clothes (material 0, the color of the mode) and the skin of the head and hands (material 1)
    /// </summary>
    public static Model CreateModel(GraphicsDevice device, Material clothes, Material skin)
    {
        var nodes = Nodes();
        var rest = RestWorld(nodes);
        var parts = new[] { (Vertices: new List<SkinnedVertex>(), Indices: new List<int>()), (Vertices: new List<SkinnedVertex>(), Indices: new List<int>()) };
        foreach (var bone in Hitboxes())
        {
            int node = Array.FindIndex(nodes, n => n.Name == bone.NodeName);
            var shape = bone.Shape;
            var size = shape switch
            {
                BoxCollider box => box.Size * new Vector3(0.75f, 0.9f, 0.75f),
                CapsuleCollider capsule => new Vector3(capsule.Radius * 1.1f, capsule.Length + capsule.Radius * 1.6f, capsule.Radius * 1.1f),
                SphereCollider sphere => new Vector3(sphere.Radius * 1.1f),
                _ => throw new NotSupportedException(),
            };
            var local = Matrix.RotationQuaternion(shape.RotationLocal) * Matrix.Translation(shape.PositionLocal);
            var part = parts[node == Head ? 1 : 0];
            AddBox(part.Vertices, part.Indices, size, local * rest[node], (ushort)node);
        }
        // Hands have no hitbox: a small block of skin each
        foreach (var hand in new[] { HandL, HandR })
            AddBox(parts[1].Vertices, parts[1].Indices, new Vector3(0.07f, 0.12f, 0.09f), Matrix.Translation(0f, -0.05f, 0f) * rest[hand], (ushort)hand);

        var bones = new MeshBoneDefinition[nodes.Length];
        for (int i = 0; i < nodes.Length; i++)
            bones[i] = new MeshBoneDefinition { NodeIndex = i, LinkToMeshMatrix = Matrix.Invert(rest[i]) };

        var model = new Model { Skeleton = new Skeleton { Nodes = nodes } };
        for (int p = 0; p < parts.Length; p++)
        {
            var (vertices, indices) = parts[p];
            var vertexBuffer = Buffer.Vertex.New(device, vertices.ToArray(), GraphicsResourceUsage.Immutable);
            var indexBuffer = Buffer.Index.New(device, indices.ToArray(), GraphicsResourceUsage.Immutable);
            var mesh = new Mesh
            {
                Draw = new MeshDraw
                {
                    PrimitiveType = PrimitiveType.TriangleList,
                    VertexBuffers = [new VertexBufferBinding(vertexBuffer, SkinnedVertex.Layout, vertices.Count)],
                    IndexBuffer = new IndexBufferBinding(indexBuffer, true, indices.Count),
                    DrawCount = indices.Count,
                },
                MaterialIndex = p,
                Skinning = new MeshSkinningDefinition { Bones = bones },
                BoundingBox = new BoundingBox(new Vector3(-0.6f, 0f, -0.4f), new Vector3(0.6f, 2f, 0.4f)),
                BoundingSphere = new BoundingSphere(new Vector3(0f, 1f, 0f), 1.2f),
            };
            mesh.Parameters.Set(MaterialKeys.HasSkinningPosition, true);
            mesh.Parameters.Set(MaterialKeys.HasSkinningNormal, true);
            model.Meshes.Add(mesh);
        }
        model.Materials.Add(new MaterialInstance(clothes));
        model.Materials.Add(new MaterialInstance(skin));
        return model;
    }

    /// <summary> An entity with the model, the animation and, when <paramref name="hitboxes"/> is set, a <see cref="SkeletonBodyComponent"/> using it </summary>
    public static Entity Create(Model model, Animator animator, SkeletonBodyComponent? hitboxes)
    {
        var entity = new Entity { new ModelComponent(model), animator };
        if (hitboxes is not null)
            entity.Add(hitboxes);
        return entity;
    }

    /// <summary> A <see cref="SkeletonBodyComponent"/> using <see cref="Hitboxes"/> </summary>
    public static SkeletonBodyComponent CreateHitboxes()
    {
        var body = new SkeletonBodyComponent();
        ((BoneCollider)body.Collider).Bones.AddRange(Hitboxes());
        return body;
    }

    // Masses in kilograms, about 76 kg in total, only used once the bodies form a ragdoll
    private static BoneShape Box(string node, Vector3 size, Vector3 offset, float mass) => new() { NodeName = node, Shape = new BoxCollider { Size = size, PositionLocal = offset, Mass = mass } };

    private static BoneShape Capsule(string node, float radius, float length, Vector3 offset, float mass) => new() { NodeName = node, Shape = new CapsuleCollider { Radius = radius, Length = length, PositionLocal = offset, Mass = mass } };

    private static ModelNodeDefinition Node(string name, int parent, Vector3 position) => new()
    {
        Name = name,
        ParentIndex = parent,
        Transform = { Position = position, Rotation = Quaternion.Identity, Scale = Vector3.One },
        Flags = ModelNodeFlags.Default,
    };

    private static Matrix[] RestWorld(ModelNodeDefinition[] nodes)
    {
        var world = new Matrix[nodes.Length];
        for (int i = 0; i < nodes.Length; i++)
        {
            var local = Matrix.Translation(nodes[i].Transform.Position);
            world[i] = nodes[i].ParentIndex < 0 ? local : local * world[nodes[i].ParentIndex];
        }
        return world;
    }

    private static void AddBox(List<SkinnedVertex> vertices, List<int> indices, Vector3 size, Matrix transform, ushort bone)
    {
        var half = size / 2f;
        Span<Vector3> normals = [Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ];
        foreach (var normal in normals)
        {
            // Two axes spanning the face, ordered so the triangles face outward
            var u = MathF.Abs(normal.Y) > 0.5f ? Vector3.UnitZ : Vector3.UnitY;
            var v = Vector3.Cross(normal, u);
            int first = vertices.Count;
            foreach (var (a, b) in new[] { (-1f, -1f), (1f, -1f), (1f, 1f), (-1f, 1f) })
            {
                var corner = (normal + u * a + v * b) * half;
                vertices.Add(new SkinnedVertex
                {
                    Position = Vector3.TransformCoordinate(corner, transform),
                    Normal = Vector3.Normalize(Vector3.TransformNormal(normal, transform)),
                    Bone = bone,
                    Weights = new Vector4(1f, 0f, 0f, 0f),
                });
            }
            indices.AddRange([first, first + 2, first + 1, first, first + 3, first + 2]);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SkinnedVertex
    {
        public static readonly VertexDeclaration Layout = new(
            VertexElement.Position<Vector3>(),
            VertexElement.Normal<Vector3>(),
            new VertexElement("BLENDINDICES", 0, PixelFormat.R16G16B16A16_UInt),
            new VertexElement("BLENDWEIGHT", 0, PixelFormat.R32G32B32A32_Float));

        public Vector3 Position;
        public Vector3 Normal;
        public ushort Bone, Unused1, Unused2, Unused3;
        public Vector4 Weights;
    }

    /// <summary> Animates the nodes from code: a walk cycle in place, or a kick of the right leg </summary>
    public sealed class Animator : SyncScript
    {
        /// <summary> Seconds into the animation </summary>
        public float Time { get; set; }

        /// <summary> Cycles per second of the walk </summary>
        public float Speed { get; set; } = 0.8f;

        /// <summary> Kicks with the right leg every <see cref="KickPeriod"/> seconds instead of walking </summary>
        public bool Kick { get; set; }

        public float KickPeriod { get; set; } = 2.5f;

        /// <summary> Stops the clock, the pose stays </summary>
        public bool Paused { get; set; }

        /// <summary> Stands still, breathing and looking around, instead of walking </summary>
        public bool Idle { get; set; }

        public override void Update()
        {
            if (!Paused)
                Time += (float)Game.UpdateTime.Elapsed.TotalSeconds;
            Apply(Entity.Get<ModelComponent>()!.Skeleton.NodeTransformations, Time);
        }

        /// <summary> The forward speed of the hips over the planted foot: the straight leg's foot moves back under the hips at this speed </summary>
        public float StanceSpeed()
        {
            float omega = Speed * MathF.Tau;
            float phase = Time * omega;
            float thigh = 0.55f * MathF.Sin(phase);
            // Foot of a straight leg: z = -0.84 sin(thigh), the planted leg is the one whose thigh angle grows
            return 0.84f * MathF.Cos(thigh) * 0.55f * omega * MathF.Abs(MathF.Cos(phase));
        }

        /// <summary> Writes the pose at <paramref name="time"/> into the node transforms </summary>
        public void Apply(ModelNodeTransformation[] nodes, float time)
        {
            if (Idle)
            {
                ApplyIdle(nodes, time);
                return;
            }
            if (Kick)
            {
                ApplyKick(nodes, time % KickPeriod);
                return;
            }

            // Positive angles around X swing a hanging limb backward, the character faces +Z
            float phase = time * Speed * MathF.Tau;
            float swing = MathF.Sin(phase);
            nodes[Hips].Transform.Position = new Vector3(0f, 0.95f + 0.03f * MathF.Cos(2f * phase), 0f);
            nodes[Spine].Transform.Rotation = Quaternion.RotationY(0.15f * swing);
            nodes[ThighL].Transform.Rotation = Quaternion.RotationX(0.55f * swing);
            nodes[ThighR].Transform.Rotation = Quaternion.RotationX(-0.55f * swing);
            // The knee bends while its leg swings forward, the other leg stays straight on the ground and moves back (a walk, not a moonwalk)
            nodes[ShinL].Transform.Rotation = Quaternion.RotationX(0.9f * MathF.Max(0f, -MathF.Cos(phase)));
            nodes[ShinR].Transform.Rotation = Quaternion.RotationX(0.9f * MathF.Max(0f, MathF.Cos(phase)));
            nodes[UpperArmL].Transform.Rotation = Quaternion.RotationX(-0.6f * swing) * Quaternion.RotationZ(0.12f);
            nodes[UpperArmR].Transform.Rotation = Quaternion.RotationX(0.6f * swing) * Quaternion.RotationZ(-0.12f);
            nodes[ForeArmL].Transform.Rotation = Quaternion.RotationX(-0.5f - 0.3f * MathF.Max(0f, -swing));
            nodes[ForeArmR].Transform.Rotation = Quaternion.RotationX(-0.5f - 0.3f * MathF.Max(0f, swing));
        }

        private static void ApplyIdle(ModelNodeTransformation[] nodes, float t)
        {
            float breath = MathF.Sin(t * 1.7f);
            nodes[Hips].Transform.Position = new Vector3(0f, 0.95f, 0f);
            nodes[Spine].Transform.Rotation = Quaternion.RotationX(0.02f * breath);
            nodes[Head].Transform.Rotation = Quaternion.RotationY(0.5f * MathF.Sin(t * 0.45f));
            foreach (var node in new[] { ThighL, ThighR, ShinL, ShinR, ForeArmL, ForeArmR })
                nodes[node].Transform.Rotation = node is ForeArmL or ForeArmR ? Quaternion.RotationX(-0.25f) : Quaternion.Identity;
            nodes[UpperArmL].Transform.Rotation = Quaternion.RotationZ(0.12f + 0.03f * breath);
            nodes[UpperArmR].Transform.Rotation = Quaternion.RotationZ(-0.12f - 0.03f * breath);
        }

        private static void ApplyKick(ModelNodeTransformation[] nodes, float t)
        {
            // Wind up for 0.4 s, swing through in 0.5 s, hold, then come back
            float thigh = t switch
            {
                < 0.4f => 0.7f * Smooth(t / 0.4f),
                < 0.9f => 0.7f - 2.1f * Smooth((t - 0.4f) / 0.5f),
                < 1.2f => -1.4f,
                < 1.7f => -1.4f * (1f - Smooth((t - 1.2f) / 0.5f)),
                _ => 0f,
            };
            float shin = t switch
            {
                < 0.4f => 1.1f * Smooth(t / 0.4f),
                < 0.9f => 1.1f * (1f - Smooth((t - 0.4f) / 0.5f)),
                _ => 0f,
            };
            nodes[Hips].Transform.Position = new Vector3(0f, 0.95f, 0f);
            nodes[Spine].Transform.Rotation = Quaternion.RotationX(0.25f * MathF.Max(0f, -thigh) / 1.4f);
            nodes[ThighR].Transform.Rotation = Quaternion.RotationX(thigh);
            nodes[ShinR].Transform.Rotation = Quaternion.RotationX(shin);
            nodes[ThighL].Transform.Rotation = Quaternion.Identity;
            nodes[ShinL].Transform.Rotation = Quaternion.Identity;
            nodes[UpperArmL].Transform.Rotation = Quaternion.RotationX(0.5f * MathF.Max(0f, -thigh) / 1.4f) * Quaternion.RotationZ(0.5f);
            nodes[UpperArmR].Transform.Rotation = Quaternion.RotationX(-0.5f * MathF.Max(0f, -thigh) / 1.4f) * Quaternion.RotationZ(-0.5f);
            nodes[ForeArmL].Transform.Rotation = Quaternion.RotationX(-0.3f);
            nodes[ForeArmR].Transform.Rotation = Quaternion.RotationX(-0.3f);
        }

        private static float Smooth(float x) => x * x * (3f - 2f * x);
    }
}
