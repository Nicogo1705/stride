// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Stride.BepuPhysics.Soft.Topology;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;
using Buffer = Stride.Graphics.Buffer;

namespace Stride.BepuPhysics.Soft.Rendering;

/// <summary>
/// A copy of a soft body's model whose vertices follow its particles: rewritten every frame on the CPU, or moved by the vertex shader.
/// </summary>
internal sealed class DeformableModel : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Vertex
    {
        public static readonly VertexDeclaration Layout = new(
            VertexElement.Position<Vector3>(),
            VertexElement.Normal<Vector3>(),
            VertexElement.Tangent<Vector4>(),
            VertexElement.TextureCoordinate<Vector2>());

        public Vector3 Position;
        public Vector3 Normal;
        public Vector4 Tangent;
        public Vector2 TexCoord;
    }

    private static readonly ConditionalWeakTable<Model, Model> SourceOfModel = new();

    private readonly Buffer _indexBuffer;
    private readonly Vector3[] _particles;
    private Buffer? _vertexBuffer; // Deformation on the CPU, the GPU deformation has its own

    // Deformation on the CPU
    private Vertex[] _vertices = [];
    private Vector3[] _positions = [];
    private Vector3[] _normals = [];
    private Vector4[] _tangents = [];
    private Vector3[] _gradients = [];

    private GpuDeformation? _gpu;

    public Model Model { get; }

    public SoftBodyTopology Topology { get; }

    public SoftBodyDeformationMode Mode => _gpu is null ? SoftBodyDeformationMode.Cpu : SoftBodyDeformationMode.Gpu;

    public DeformableModel(GraphicsDevice device, Model source, SoftBodyTopology topology, SoftBodyDeformationMode mode)
    {
        Topology = topology;
        var mesh = topology.Mesh;
        _particles = new Vector3[topology.ParticleCount];
        _indexBuffer = Buffer.Index.New(device, mesh.Indices);

        // The skeleton stays, so an animation of the source model still plays and the soft body can follow it
        Model = new Model { Skeleton = source.Skeleton };
        SourceOfModel.AddOrUpdate(Model, source);
        foreach (var material in source.Materials)
            Model.Materials.Add(material);
        foreach (var part in mesh.Parts)
            Model.Meshes.Add(new Mesh { MaterialIndex = part.MaterialIndex });

        SetMode(device, mode, retired: []);
    }

    /// <summary>
    /// Switches the buffers and draws of the meshes to <paramref name="mode"/>, in place: the model and its meshes stay, so does whatever follows them.
    /// </summary>
    /// <param name="retired"> Receives the buffers of the previous mode, to dispose of once the renderer no longer uses them </param>
    public void SetMode(GraphicsDevice device, SoftBodyDeformationMode mode, ICollection<IDisposable> retired)
    {
        bool built = _gpu is not null || _vertexBuffer is not null;
        if (built && mode == Mode)
            return;

        if (_gpu is not null)
            retired.Add(_gpu);
        if (_vertexBuffer is not null)
            retired.Add(_vertexBuffer);
        _gpu = null;
        _vertexBuffer = null;

        var mesh = Topology.Mesh;
        int vertexCount = mesh.Positions.Length;
        Buffer vertexBuffer;
        VertexDeclaration layout;
        if (mode == SoftBodyDeformationMode.Gpu)
        {
            _gpu = new GpuDeformation(device, Topology);
            vertexBuffer = _gpu.VertexBuffer;
            layout = _gpu.Layout;
            _vertices = [];
            _positions = [];
            _normals = [];
            _tangents = [];
            _gradients = [];
        }
        else
        {
            _vertices = new Vertex[vertexCount];
            for (int i = 0; i < vertexCount; i++)
                _vertices[i].TexCoord = mesh.TexCoords[i];
            _positions = new Vector3[vertexCount];
            _normals = new Vector3[vertexCount];
            _tangents = new Vector4[vertexCount];
            _gradients = Topology.LatticeCoordinates is null ? [] : new Vector3[Topology.ParticleCount * 3];
            vertexBuffer = _vertexBuffer = Buffer.Vertex.New(device, bufferSize: vertexCount * Vertex.Layout.VertexStride, usage: GraphicsResourceUsage.Dynamic);
            layout = Vertex.Layout;
        }

        var vertexBinding = new VertexBufferBinding(vertexBuffer, layout, vertexCount);
        var indexBinding = new IndexBufferBinding(_indexBuffer, true, mesh.Indices.Length);
        for (int i = 0; i < mesh.Parts.Length; i++)
        {
            var part = mesh.Parts[i];
            var renderMesh = Model.Meshes[i];
            renderMesh.Draw = new MeshDraw
            {
                PrimitiveType = PrimitiveType.TriangleList,
                StartLocation = part.FirstIndex,
                DrawCount = part.IndexCount,
                VertexBuffers = [vertexBinding],
                IndexBuffer = indexBinding,
            };
            if (_gpu is not null)
                _gpu.Bind(renderMesh);
            else
                GpuDeformation.Unbind(renderMesh);
        }
    }

    /// <summary> The model a deformable model was copied from, or <paramref name="model"/> itself when it is not one, such as on a cloned entity </summary>
    public static Model SourceOf(Model model) => SourceOfModel.TryGetValue(model, out var source) ? source : model;

    /// <summary> Computes the vertices from the current particles, in the space of <paramref name="world"/>; safe to run for several models at once </summary>
    public void Deform(SoftBodyComponent body, Matrix world)
    {
        body.CopyParticlePositions(_particles);
        if (_gpu is not null)
        {
            DeformOnGpu(body, world);
            return;
        }

        Topology.Deform(_particles, _positions);

        // Normals and tangents are first found in world space
        if (Topology.LatticeCoordinates is { } coordinates)
        {
            DeformFrames(coordinates);
        }
        else
        {
            RebuildFrames();

            // Pushes the two faces of a double sided model apart, just enough for them not to fight over the same depth
            var offset = SurfaceOffset(body);
            for (int i = 0; i < _positions.Length; i++)
                _positions[i] += _normals[i] * offset;
        }

        var worldToLocal = Matrix.Invert(world);
        world.Decompose(out var scale, out Quaternion rotation, out _);
        var toLocal = Quaternion.Conjugate(rotation);
        var bounds = new BoundingBox(new Vector3(float.MaxValue), new Vector3(float.MinValue));
        for (int i = 0; i < _vertices.Length; i++)
        {
            ref var vertex = ref _vertices[i];
            vertex.Position = Vector3.TransformCoordinate(_positions[i], worldToLocal);
            // Undoing the scale stretches normals the other way than directions, as the inverse transpose would
            vertex.Normal = SafeNormalize(Vector3.Transform(_normals[i], toLocal) * scale, Topology.Mesh.Normals[i]);
            var tangent = Vector3.Transform((Vector3)_tangents[i], toLocal) / scale;
            tangent -= vertex.Normal * Vector3.Dot(vertex.Normal, tangent);
            vertex.Tangent = new Vector4(SafeNormalize(tangent, (Vector3)Topology.Mesh.Tangents[i]), _tangents[i].W);
            BoundingBox.Merge(ref bounds, ref vertex.Position, out bounds);
        }

        SetBounds(bounds);
    }

    /// <summary> Takes the particles and finds the bounds, the vertex shader does the rest </summary>
    private void DeformOnGpu(SoftBodyComponent body, Matrix world)
    {
        // The shader pushes along normals in world space, the bounds are in local space
        var offset = Topology.LatticeCoordinates is null ? SurfaceOffset(body) : 0f;
        world.Decompose(out var scale, out Quaternion _, out _);
        var smallestScale = MathF.Min(MathF.Abs(scale.X), MathF.Min(MathF.Abs(scale.Y), MathF.Abs(scale.Z)));
        var localOffset = MathUtil.IsZero(smallestScale) ? 0f : offset / smallestScale;
        SetBounds(_gpu!.Update(_particles, Matrix.Invert(world), offset, localOffset));
    }

    /// <summary> How far the vertices of a surface are pushed along their normal, so the two faces of a double sided model do not fight over the same depth </summary>
    private static float SurfaceOffset(SoftBodyComponent body) => body.Collider is Colliders.ParticleCollider particle ? particle.ActualRadius * 0.1f : 0f;

    private void SetBounds(BoundingBox bounds)
    {
        var sphere = BoundingSphere.FromBox(bounds);
        foreach (var mesh in Model.Meshes)
        {
            mesh.BoundingBox = bounds;
            mesh.BoundingSphere = sphere;
        }
        Model.BoundingBox = bounds;
        Model.BoundingSphere = sphere;
    }

    /// <summary> Sends what <see cref="Deform"/> computed to the GPU: the vertices, or only the particles </summary>
    public void Upload(CommandList commandList)
    {
        if (_gpu is not null)
            _gpu.Upload(commandList);
        else
            _vertexBuffer!.SetData<Vertex>(commandList, _vertices.AsSpan());
    }

    /// <summary> Carries the authored normals and tangents along the deformation of the lattice around each vertex </summary>
    private void DeformFrames(Vector3[] coordinates)
    {
        Topology.ComputeLatticeGradients(_particles, _gradients);
        for (int v = 0; v < coordinates.Length; v++)
            Topology.DeformFrame(v, _gradients, out _normals[v], out _tangents[v]);
    }

    /// <summary> Recomputes normals and tangents from the deformed triangles, for surfaces whose vertices are the particles themselves </summary>
    private void RebuildFrames()
    {
        var mesh = Topology.Mesh;
        Array.Clear(_tangents);
        foreach (var part in mesh.Parts)
        {
            var indices = mesh.Indices.AsSpan(part.FirstIndex, part.IndexCount);
            SourceMesh.ComputeNormals(_positions, indices, _normals, mesh.Clockwise);
            SourceMesh.ComputeTangents(_positions, _normals, mesh.TexCoords, indices, _tangents);
        }
    }

    private static Vector3 SafeNormalize(Vector3 value, Vector3 fallback)
    {
        var length = value.Length();
        return length > 1e-12f ? value / length : fallback;
    }

    public void Dispose()
    {
        _gpu?.Dispose();
        _vertexBuffer?.Dispose();
        _indexBuffer.Dispose();
    }
}
