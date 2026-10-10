// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using Stride.BepuPhysics.Definitions.Colliders;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Harness.Demo;
using Stride.Rendering;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;
using Buffer = Stride.Graphics.Buffer;

namespace Stride.BepuPhysics.Soft.Tests.VideoDemo;

/// <summary>
/// A height field turned into a voxel density field for a <see cref="VoxelCollider"/>, and a mesh of the same surface to draw it;
/// carving a crater edits both, the collider without rebuilding anything.
/// </summary>
internal sealed class VoxelTerrain
{
    private const float IsoLevel = 0.5f;
    private readonly VoxelCollider _collider;
    private readonly float[] _heights;
    private readonly int _samples, _layers;
    private readonly float _cell;
    private readonly VertexPositionNormalColor[] _vertices;
    private readonly Buffer _vertexBuffer;
    private readonly DemoGame _game;

    public Entity Entity { get; }

    /// <summary> Width of the terrain on X and Z </summary>
    public float Size => (_samples - 1) * _cell;

    public Vector3 Origin => Entity.Transform.Position;

    public int Craters { get; private set; }

    public VoxelTerrain(DemoGame game, int samples, int layers, float cell, Vector3 center, Func<float, float, float> height, VoxelCellShape shape = VoxelCellShape.SurfaceNets)
    {
        _game = game;
        _samples = samples;
        _layers = layers;
        _cell = cell;
        _heights = new float[samples * samples];
        for (int x = 0; x < samples; x++)
        {
            for (int z = 0; z < samples; z++)
                _heights[x * samples + z] = Math.Clamp(height(x * cell - Size / 2f, z * cell - Size / 2f), cell * 1.5f, (layers - 3) * cell);
        }

        _collider = new VoxelCollider { CellShape = shape, IsoLevel = IsoLevel, CellSize = cell, SealBorder = true };
        var field = new float[samples * layers * samples];
        for (int x = 0; x < samples; x++)
        {
            for (int z = 0; z < samples; z++)
                WriteColumn(field, x, z);
        }
        _collider.SetData(samples, layers, samples, field);

        _vertices = new VertexPositionNormalColor[samples * samples];
        var indices = new int[(samples - 1) * (samples - 1) * 6];
        int i = 0;
        for (int x = 0; x < samples - 1; x++)
        {
            for (int z = 0; z < samples - 1; z++)
            {
                int a = x * samples + z, b = (x + 1) * samples + z, c = (x + 1) * samples + z + 1, d = x * samples + z + 1;
                indices[i++] = a; indices[i++] = c; indices[i++] = d;
                indices[i++] = a; indices[i++] = b; indices[i++] = c;
            }
        }
        RefreshVertices(0, samples - 1, 0, samples - 1);
        _vertexBuffer = Buffer.Vertex.New(game.GraphicsDevice, _vertices, GraphicsResourceUsage.Default);
        var indexBuffer = Buffer.Index.New(game.GraphicsDevice, indices);
        var bounds = new BoundingBox(Vector3.Zero, new Vector3(Size, layers * cell, Size));
        var mesh = new Mesh
        {
            Draw = new MeshDraw
            {
                PrimitiveType = PrimitiveType.TriangleList,
                VertexBuffers = [new VertexBufferBinding(_vertexBuffer, VertexPositionNormalColor.Layout, _vertices.Length)],
                IndexBuffer = new IndexBufferBinding(indexBuffer, true, indices.Length),
                DrawCount = indices.Length,
            },
            BoundingBox = bounds,
            BoundingSphere = BoundingSphere.FromBox(bounds),
        };
        var material = Material.New(game.GraphicsDevice, new MaterialDescriptor
        {
            Attributes =
            {
                Diffuse = new MaterialDiffuseMapFeature(new ComputeVertexStreamColor()),
                DiffuseModel = new MaterialDiffuseLambertModelFeature(),
                MicroSurface = new MaterialGlossinessMapFeature(new ComputeFloat(0.25f)),
                Specular = new MaterialMetalnessMapFeature(new ComputeFloat(0f)),
                SpecularModel = new MaterialSpecularMicrofacetModelFeature { Environment = new MaterialSpecularMicrofacetEnvironmentGGXPolynomial() },
            },
        });
        var model = new Model { BoundingBox = bounds };
        model.Meshes.Add(mesh);
        model.Materials.Add(new MaterialInstance(material));

        Entity = new Entity("terrain voxel") { new StaticComponent { Collider = _collider }, new ModelComponent(model) };
        // The lowest surface the field allows sits on the ground
        Entity.Transform.Position = center - new Vector3(Size / 2f, cell * 1.5f - 0.02f, Size / 2f);
        game.AddToScene(Entity);
    }

    /// <summary> The height of the surface at a world point, from the height field </summary>
    public float HeightAt(float worldX, float worldZ)
    {
        int x = Math.Clamp((int)MathF.Round((worldX - Origin.X) / _cell), 0, _samples - 1);
        int z = Math.Clamp((int)MathF.Round((worldZ - Origin.Z) / _cell), 0, _samples - 1);
        return _heights[x * _samples + z] + Origin.Y;
    }

    /// <summary> Digs a round crater around a world point; the collider reads the edited samples on its next step </summary>
    public void Carve(Vector3 world, float radius, float depth)
    {
        var local = world - Origin;
        int x0 = Math.Max(0, (int)((local.X - radius) / _cell)), x1 = Math.Min(_samples - 1, (int)((local.X + radius) / _cell) + 1);
        int z0 = Math.Max(0, (int)((local.Z - radius) / _cell)), z1 = Math.Min(_samples - 1, (int)((local.Z + radius) / _cell) + 1);
        for (int x = x0; x <= x1; x++)
        {
            for (int z = z0; z <= z1; z++)
            {
                float distance = new Vector2(x * _cell - local.X, z * _cell - local.Z).Length();
                if (distance >= radius)
                    continue;
                float t = distance / radius;
                ref float h = ref _heights[x * _samples + z];
                h = MathF.Max(_cell * 1.5f, h - depth * (1f - t * t));
                for (int y = 0; y < _layers; y++)
                    _collider.SetVoxel(x, y, z, Density(h, y));
            }
        }
        _collider.NotifyFieldChanged();
        RefreshVertices(Math.Max(0, x0 - 1), Math.Min(_samples - 1, x1 + 1), Math.Max(0, z0 - 1), Math.Min(_samples - 1, z1 + 1));
        _vertexBuffer.SetData<VertexPositionNormalColor>(_game.GraphicsContext.CommandList, _vertices);
        Craters++;
    }

    // Linear in height near the surface, so the iso surface sits exactly at the height
    private float Density(float height, int layer) => Math.Clamp(IsoLevel + (height - layer * _cell) / _cell, -3f, 4f);

    private void WriteColumn(float[] field, int x, int z)
    {
        float h = _heights[x * _samples + z];
        for (int y = 0; y < _layers; y++)
            field[(x * _layers + y) * _samples + z] = Density(h, y);
    }

    private void RefreshVertices(int x0, int x1, int z0, int z1)
    {
        float top = (_layers - 3) * _cell;
        for (int x = x0; x <= x1; x++)
        {
            for (int z = z0; z <= z1; z++)
            {
                float h = _heights[x * _samples + z];
                float hx = _heights[Math.Min(_samples - 1, x + 1) * _samples + z] - _heights[Math.Max(0, x - 1) * _samples + z];
                float hz = _heights[x * _samples + Math.Min(_samples - 1, z + 1)] - _heights[x * _samples + Math.Max(0, z - 1)];
                var normal = Vector3.Normalize(new Vector3(-hx, 2f * _cell, -hz));
                // Grass on gentle slopes, earth on steep ones, a lighter stone toward the tops, darker in craters
                float slope = 1f - normal.Y;
                float altitude = h / top;
                var grass = Color4.Lerp(new Color4(0.20f, 0.42f, 0.16f, 1f), new Color4(0.36f, 0.55f, 0.20f, 1f), MathF.Min(1f, altitude * 1.6f));
                var earth = new Color4(0.42f, 0.30f, 0.20f, 1f);
                var stone = new Color4(0.62f, 0.60f, 0.56f, 1f);
                var color = Color4.Lerp(grass, earth, Math.Clamp(slope * 4f - 0.4f, 0f, 1f));
                color = Color4.Lerp(color, stone, Math.Clamp((altitude - 0.72f) * 4f, 0f, 1f));
                _vertices[x * _samples + z] = new VertexPositionNormalColor(new Vector3(x * _cell, h, z * _cell), normal, (Color)color);
            }
        }
    }
}
