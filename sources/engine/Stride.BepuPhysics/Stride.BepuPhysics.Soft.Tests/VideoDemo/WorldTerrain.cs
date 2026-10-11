// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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
/// The ground of the whole world: one voxel density field of 200 x 200 m drawn with the surface nets of <see cref="VoxelCollider"/>, cut into one collider per zone,
/// and a height field ring of hills and mountains around it up to the horizon. Carving a crater edits the field, the colliders and the meshes in place.
/// </summary>
internal sealed class WorldTerrain
{
    public const float Cell = 0.5f, Iso = 0.5f, Half = 100f, Bottom = -4f;
    public const int Samples = 401, Layers = 81;
    /// <summary> Half the width of a zone's collider; an object farther from its zone than this falls through the ground </summary>
    public const float ZoneHalf = 32f;
    /// <summary> Radius of the flat pad under each zone </summary>
    public const float PadRadius = 14f;
    private const int ZoneSamples = 129, ChunkCells = 50, Chunks = 8;
    private const float Far = 1200f;

    private readonly DemoGame _game;
    private readonly float[] _field = new float[Samples * Layers * Samples];
    private readonly float[] _padWeight = new float[Samples * Samples], _pathWeight = new float[Samples * Samples];
    private readonly bool[] _carved = new bool[Samples * Samples];
    private readonly Vector2[] _pads;
    private readonly (Vector2 A, Vector2 B)[] _paths;
    private readonly Chunk[] _chunks = new Chunk[Chunks * Chunks];
    private readonly List<(VoxelCollider Collider, int X, int Z, int Layers)> _colliders = new();
    private readonly Material _material;

    public int Craters { get; private set; }

    /// <summary> Vertices of the voxel meshes, for the report </summary>
    public int Vertices { get; private set; }

    /// <param name="pads"> Centers of the zones, each on a flat pad at y = 0, linked to the first one and to its neighbors by paths </param>
    public WorldTerrain(DemoGame game, IReadOnlyList<Vector2> pads)
    {
        _game = game;
        _pads = [.. pads];
        var paths = new List<(Vector2, Vector2)>();
        var ring = new List<Vector2>();
        for (int i = 1; i < _pads.Length; i++)
        {
            paths.Add((_pads[0], _pads[i]));
            ring.Add(_pads[i]);
        }
        ring.Sort((a, b) => MathF.Atan2(a.Y, a.X).CompareTo(MathF.Atan2(b.Y, b.X)));
        for (int i = 0; i < ring.Count; i++)
            paths.Add((ring[i], ring[(i + 1) % ring.Count]));
        _paths = [.. paths];

        Parallel.For(0, Samples, x =>
        {
            for (int z = 0; z < Samples; z++)
            {
                var (h, pad, path) = Surface(World(x), World(z));
                _padWeight[x * Samples + z] = pad;
                _pathWeight[x * Samples + z] = path;
                for (int y = 0; y < Layers; y++)
                    _field[Index(x, y, z)] = Math.Clamp(Iso + (h - Height(y)) / Cell, -3f, 4f);
            }
        });
        AddFeatures();

        _material = Material.New(game.GraphicsDevice, new MaterialDescriptor
        {
            Attributes =
            {
                Diffuse = new MaterialDiffuseMapFeature(new ComputeVertexStreamColor()),
                DiffuseModel = new MaterialDiffuseLambertModelFeature(),
                MicroSurface = new MaterialGlossinessMapFeature(new ComputeFloat(0.22f)),
                Specular = new MaterialMetalnessMapFeature(new ComputeFloat(0f)),
                SpecularModel = new MaterialSpecularMicrofacetModelFeature { Environment = new MaterialSpecularMicrofacetEnvironmentGGXPolynomial() },
            },
        });

        var meshes = new (VertexPositionNormalColor[], int[])[_chunks.Length];
        Parallel.For(0, _chunks.Length, i => meshes[i] = BuildMesh(i / Chunks, i % Chunks));
        for (int i = 0; i < _chunks.Length; i++)
        {
            int cx = i / Chunks, cz = i % Chunks;
            var min = new Vector3(World(cx * ChunkCells) - Cell, Bottom, World(cz * ChunkCells) - Cell);
            var bounds = new BoundingBox(min, min + new Vector3((ChunkCells + 2) * Cell, Layers * Cell, (ChunkCells + 2) * Cell));
            _chunks[i] = new Chunk(game, _material, bounds, meshes[i].Item1, meshes[i].Item2);
            Vertices += meshes[i].Item1.Length;
        }
        AddRing();
    }

    /// <summary> Adds the meshes to the scene; the zones add their own colliders </summary>
    public void AddTo(DemoGame game)
    {
        foreach (var chunk in _chunks)
            game.AddToScene(chunk.Entity);
        game.AddToScene(_ring!);
    }

    private Entity? _ring;

    public static float World(int sample) => -Half + sample * Cell;

    public static float Height(int layer) => Bottom + layer * Cell;

    private static int Index(int x, int y, int z) => (x * Layers + y) * Samples + z;

    private static float Smooth(float a, float b, float x)
    {
        float t = Math.Clamp((x - a) / (b - a), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    // Value noise in [-1, 1] on a hashed lattice
    private static float Hash(int x, int z)
    {
        uint h = (uint)x * 374761393u + (uint)z * 668265263u;
        h = (h ^ (h >> 13)) * 1274126177u;
        return (h ^ (h >> 16)) / (float)uint.MaxValue * 2f - 1f;
    }

    private static float Noise(float x, float z)
    {
        int ix = (int)MathF.Floor(x), iz = (int)MathF.Floor(z);
        float fx = x - ix, fz = z - iz;
        fx = fx * fx * (3f - 2f * fx);
        fz = fz * fz * (3f - 2f * fz);
        float a = MathUtil.Lerp(Hash(ix, iz), Hash(ix + 1, iz), fx);
        float b = MathUtil.Lerp(Hash(ix, iz + 1), Hash(ix + 1, iz + 1), fx);
        return MathUtil.Lerp(a, b, fz);
    }

    private static float Fbm(float x, float z)
    {
        float sum = 0f, amplitude = 0.5f;
        for (int i = 0; i < 4; i++)
        {
            sum += amplitude * Noise(x, z);
            x = x * 2.03f + 17.1f;
            z = z * 2.03f - 9.7f;
            amplitude *= 0.5f;
        }
        return sum / 0.9375f;
    }

    // Sharp crests in [0, 1]
    private static float Ridged(float x, float z)
    {
        float sum = 0f, amplitude = 0.5f;
        for (int i = 0; i < 4; i++)
        {
            float n = 1f - MathF.Abs(Noise(x, z));
            sum += amplitude * n * n;
            x = x * 2.1f + 5.3f;
            z = z * 2.1f + 11.9f;
            amplitude *= 0.5f;
        }
        return sum / 0.9375f;
    }

    /// <summary> The height of the ground before carving, and how much of a pad and a path is there </summary>
    public (float Height, float Pad, float Path) Surface(float x, float z)
    {
        float r = MathF.Sqrt(x * x + z * z);
        float h = 0.6f + 4f * (0.5f + 0.5f * Fbm(x / 22f, z / 22f)) + 0.8f * Fbm(x / 9f + 31.7f, z / 9f - 12.3f);
        float mountains = 18f * Smooth(62f, 100f, r) + 55f * Smooth(100f, 260f, r) - 45f * Smooth(330f, 440f, r);
        h += mountains * (0.75f + 0.5f * Ridged(x / 40f, z / 40f));
        // The voxel field ends at 36 m: flatten the crests inside it, smoothly
        float clamped = h > 28f ? 28f + 6f * MathF.Tanh((h - 28f) / 6f) : h;
        h = MathUtil.Lerp(clamped, h, Smooth(Half, Half + 12f, MathF.Max(MathF.Abs(x), MathF.Abs(z))));

        float path = 0f;
        var p = new Vector2(x, z);
        foreach (var (a, b) in _paths)
        {
            var ab = b - a;
            float t = Math.Clamp(Vector2.Dot(p - a, ab) / ab.LengthSquared(), 0f, 1f);
            path = MathF.Max(path, 1f - Smooth(1.5f, 3.5f, (a + ab * t - p).Length()));
        }
        h = MathUtil.Lerp(h, h * 0.2f, path);

        float pad = 0f;
        foreach (var center in _pads)
            pad = MathF.Max(pad, 1f - Smooth(PadRadius, PadRadius + 7f, (p - center).Length()));
        h = MathUtil.Lerp(h, 0f, pad);

        // A mound to dig into at the playground
        var mound = p - (_pads[0] + new Vector2(6f, 9f));
        h += 2.5f * MathF.Exp(-mound.LengthSquared() / 12f);
        return (h, pad, path);
    }

    // The playground's cliff, its overhang and an arch, added as solids to the field
    private void AddFeatures()
    {
        var o = new Vector3(_pads[0].X, 0f, _pads[0].Y);
        Union(o + new Vector3(2f, -4f, -10f), o + new Vector3(16f, 6f, 2f), p =>
        {
            var q = p - (o + new Vector3(11f, 0f, -3f));
            float plateau = MathF.Max(new Vector2(q.X, q.Z).Length() - 4f, q.Y - 5f);
            var d = p - (o + new Vector3(6.5f, 4.5f, -3f));
            var slab = new Vector3(MathF.Abs(d.X) - 3.5f, MathF.Abs(d.Y) - 0.5f, MathF.Abs(d.Z) - 3f);
            float box = Vector3.Max(slab, Vector3.Zero).Length() + MathF.Min(MathF.Max(slab.X, MathF.Max(slab.Y, slab.Z)), 0f);
            return MathF.Min(plateau, box - 0.15f);
        });
        Union(o + new Vector3(-12f, -1f, 4f), o + new Vector3(-4f, 4f, 8f), p =>
        {
            var q = p - (o + new Vector3(-8f, 0f, 6f));
            return new Vector2(new Vector2(q.X, q.Y).Length() - 3f, q.Z).Length() - 0.8f;
        });
    }

    private void Union(Vector3 min, Vector3 max, Func<Vector3, float> sdf)
    {
        int x0 = SampleOf(min.X), x1 = SampleOf(max.X), z0 = SampleOf(min.Z), z1 = SampleOf(max.Z);
        int y0 = Math.Max(0, (int)((min.Y - Bottom) / Cell)), y1 = Math.Min(Layers - 1, (int)((max.Y - Bottom) / Cell) + 1);
        for (int x = x0; x <= x1; x++)
        {
            for (int y = y0; y <= y1; y++)
            {
                for (int z = z0; z <= z1; z++)
                {
                    ref float d = ref _field[Index(x, y, z)];
                    d = MathF.Max(d, Math.Clamp(Iso - sdf(new Vector3(World(x), Height(y), World(z))) / Cell, -3f, 4f));
                }
            }
        }
    }

    private static int SampleOf(float world) => Math.Clamp((int)MathF.Round((world + Half) / Cell), 0, Samples - 1);

    /// <summary> The collider of a zone: the field around its center, in a <see cref="StaticComponent"/> to add while the zone builds </summary>
    public Entity Collider(Vector2 center)
    {
        int x0 = SampleOf(center.X - ZoneHalf), z0 = SampleOf(center.Y - ZoneHalf);
        x0 = Math.Min(x0, Samples - ZoneSamples);
        z0 = Math.Min(z0, Samples - ZoneSamples);
        int top = 0;
        for (int x = x0; x < x0 + ZoneSamples; x++)
        {
            for (int z = z0; z < z0 + ZoneSamples; z++)
            {
                for (int y = Layers - 1; y > top; y--)
                {
                    if (_field[Index(x, y, z)] >= Iso)
                    {
                        top = y;
                        break;
                    }
                }
            }
        }
        // Room above the highest ground, and for whatever is stacked on it
        int layers = Math.Min(Layers, top + 5);
        var data = new float[ZoneSamples * layers * ZoneSamples];
        for (int x = 0; x < ZoneSamples; x++)
        {
            for (int y = 0; y < layers; y++)
                Array.Copy(_field, Index(x0 + x, y, z0), data, (x * layers + y) * ZoneSamples, ZoneSamples);
        }
        var collider = new VoxelCollider { CellShape = VoxelCellShape.SurfaceNets, CellSize = Cell, IsoLevel = Iso, SealBorder = true };
        collider.SetData(ZoneSamples, layers, ZoneSamples, data);
        _colliders.Add((collider, x0, z0, layers));
        var entity = new Entity("zone ground") { new StaticComponent { Collider = collider } };
        entity.Transform.Position = new Vector3(World(x0), Bottom, World(z0));
        return entity;
    }

    /// <summary> The highest ground at a point, carving included </summary>
    public float SurfaceY(float x, float z)
    {
        if (MathF.Abs(x) >= Half || MathF.Abs(z) >= Half)
            return Surface(x, z).Height;
        int sx = SampleOf(x), sz = SampleOf(z);
        for (int y = Layers - 2; y >= 0; y--)
        {
            float below = _field[Index(sx, y, sz)];
            if (below < Iso)
                continue;
            float above = _field[Index(sx, y + 1, sz)];
            return Height(y) + Cell * Math.Clamp((Iso - below) / (above - below), 0f, 1f);
        }
        return Bottom;
    }

    /// <summary> Digs a round hole: the colliders read the new samples on their next step, the meshes around it are rebuilt </summary>
    public void Carve(Vector3 center, float radius)
    {
        int x0 = SampleOf(center.X - radius - Cell), x1 = SampleOf(center.X + radius + Cell);
        int z0 = SampleOf(center.Z - radius - Cell), z1 = SampleOf(center.Z + radius + Cell);
        int y0 = Math.Max(1, (int)((center.Y - radius - Bottom) / Cell)), y1 = Math.Min(Layers - 1, (int)((center.Y + radius - Bottom) / Cell) + 2);
        var touched = new bool[_colliders.Count];
        for (int x = x0; x <= x1; x++)
        {
            for (int z = z0; z <= z1; z++)
            {
                for (int y = y0; y <= y1; y++)
                {
                    float distance = (new Vector3(World(x), Height(y), World(z)) - center).Length();
                    float density = Math.Clamp(Iso + (distance - radius) / Cell, -3f, 4f);
                    ref float d = ref _field[Index(x, y, z)];
                    if (density >= d)
                        continue;
                    d = density;
                    _carved[x * Samples + z] = true;
                    for (int i = 0; i < _colliders.Count; i++)
                    {
                        var (collider, cx, cz, layers) = _colliders[i];
                        int lx = x - cx, lz = z - cz;
                        if ((uint)lx < ZoneSamples && (uint)lz < ZoneSamples && y < layers)
                        {
                            collider.SetVoxel(lx, y, lz, density);
                            touched[i] = true;
                        }
                    }
                }
            }
        }
        for (int i = 0; i < _colliders.Count; i++)
        {
            if (touched[i])
                _colliders[i].Collider.NotifyFieldChanged();
        }
        for (int cx = Math.Max(0, (x0 - 1) / ChunkCells); cx <= Math.Min(Chunks - 1, (x1 + 1) / ChunkCells); cx++)
        {
            for (int cz = Math.Max(0, (z0 - 1) / ChunkCells); cz <= Math.Min(Chunks - 1, (z1 + 1) / ChunkCells); cz++)
            {
                var (vertices, indices) = BuildMesh(cx, cz);
                _chunks[cx * Chunks + cz].Replace(_game, vertices, indices);
            }
        }
        Craters++;
    }

    // Surface nets of one chunk, with the collider's rule: a vertex per cell at the mean of its edge crossings, a quad per edge that changes sign
    private (VertexPositionNormalColor[], int[]) BuildMesh(int chunkX, int chunkZ)
    {
        int x0 = chunkX * ChunkCells, z0 = chunkZ * ChunkCells;
        const int span = ChunkCells + 1;
        var cache = new int[span * Layers * span];
        Array.Fill(cache, -1);
        var vertices = new List<VertexPositionNormalColor>();
        var indices = new List<int>();
        const int cells = Samples - 1, cellLayers = Layers - 1;

        int Vertex(int cx, int cy, int cz)
        {
            ref int slot = ref cache[((cx - x0 + 1) * Layers + cy) * span + cz - z0 + 1];
            if (slot >= 0)
                return slot;
            var sum = Vector3.Zero;
            int count = 0;
            for (int edge = 0; edge < 12; edge++)
            {
                // Edges 0-3 along X, 4-7 along Y, 8-11 along Z
                int axis = edge / 4, a = edge & 1, b = (edge >> 1) & 1;
                var start = axis switch { 0 => new Int3(0, a, b), 1 => new Int3(a, 0, b), _ => new Int3(a, b, 0) };
                var end = start;
                if (axis == 0) end.X = 1; else if (axis == 1) end.Y = 1; else end.Z = 1;
                float da = _field[Index(cx + start.X, cy + start.Y, cz + start.Z)], db = _field[Index(cx + end.X, cy + end.Y, cz + end.Z)];
                if (da >= Iso == db >= Iso)
                    continue;
                float delta = db - da;
                float t = MathUtil.IsZero(delta) ? 0.5f : Math.Clamp((Iso - da) / delta, 0f, 1f);
                sum += Vector3.Lerp(new Vector3(start.X, start.Y, start.Z), new Vector3(end.X, end.Y, end.Z), t);
                count++;
            }
            var position = new Vector3(World(cx), Height(cy), World(cz)) + sum / count * Cell;
            var normal = -Gradient(position);
            normal = normal.LengthSquared() > 1e-8f ? Vector3.Normalize(normal) : Vector3.UnitY;
            slot = vertices.Count;
            vertices.Add(new VertexPositionNormalColor(position, normal, (Color)Shade(position, normal, SampleOf(position.X), SampleOf(position.Z))));
            return slot;
        }

        Span<int> quad = stackalloc int[4];
        for (int x = x0; x < x0 + ChunkCells; x++)
        {
            for (int z = z0; z < z0 + ChunkCells; z++)
            {
                for (int y = 0; y < Layers; y++)
                {
                    bool solid = _field[Index(x, y, z)] >= Iso;
                    for (int axis = 0; axis < 3; axis++)
                    {
                        int nx = x + (axis == 0 ? 1 : 0), ny = y + (axis == 1 ? 1 : 0), nz = z + (axis == 2 ? 1 : 0);
                        if (nx >= Samples || ny >= Layers || nz >= Samples || solid == _field[Index(nx, ny, nz)] >= Iso)
                            continue;
                        // The four cells around the edge, as the collider orders them
                        bool exists = true;
                        for (int i = 0; i < 4 && exists; i++)
                        {
                            int stepA = i >= 2 ? -1 : 0, stepB = i is 1 or 2 ? -1 : 0;
                            int qx = x, qy = y, qz = z;
                            switch (axis)
                            {
                                case 0: qy += stepA; qz += stepB; break;
                                case 1: qz += stepA; qx += stepB; break;
                                default: qx += stepA; qy += stepB; break;
                            }
                            exists = qx >= 0 && qy >= 0 && qz >= 0 && qx < cells && qy < cellLayers && qz < cells;
                            if (exists)
                                quad[i] = Vertex(qx, qy, qz);
                        }
                        if (!exists)
                            continue;
                        var outward = Vector3.Zero;
                        outward[axis] = solid ? 1f : -1f;
                        AddTriangle(quad[0], quad[1], quad[2], outward);
                        AddTriangle(quad[0], quad[2], quad[3], outward);
                    }
                }
            }
        }

        void AddTriangle(int a, int b, int c, Vector3 outward)
        {
            var pa = vertices[a].Position;
            var cross = Vector3.Cross(vertices[b].Position - pa, vertices[c].Position - pa);
            // Front faces wind so that this cross product points into the ground
            if (Vector3.Dot(cross, outward) > 0f)
                (b, c) = (c, b);
            indices.Add(a);
            indices.Add(b);
            indices.Add(c);
        }

        return (vertices.ToArray(), indices.ToArray());
    }

    private float Density(Vector3 world)
    {
        float fx = Math.Clamp((world.X + Half) / Cell, 0f, Samples - 1.001f);
        float fy = Math.Clamp((world.Y - Bottom) / Cell, 0f, Layers - 1.001f);
        float fz = Math.Clamp((world.Z + Half) / Cell, 0f, Samples - 1.001f);
        int x = (int)fx, y = (int)fy, z = (int)fz;
        float tx = fx - x, ty = fy - y, tz = fz - z;
        float Lerp2(int ox, int oy) => MathUtil.Lerp(_field[Index(x + ox, y + oy, z)], _field[Index(x + ox, y + oy, z + 1)], tz);
        float bottom = MathUtil.Lerp(Lerp2(0, 0), Lerp2(1, 0), tx), top = MathUtil.Lerp(Lerp2(0, 1), Lerp2(1, 1), tx);
        return MathUtil.Lerp(bottom, top, ty);
    }

    private Vector3 Gradient(Vector3 p)
    {
        const float h = Cell * 0.75f;
        return new Vector3(
            Density(p + new Vector3(h, 0f, 0f)) - Density(p - new Vector3(h, 0f, 0f)),
            Density(p + new Vector3(0f, h, 0f)) - Density(p - new Vector3(0f, h, 0f)),
            Density(p + new Vector3(0f, 0f, h)) - Density(p - new Vector3(0f, 0f, h)));
    }

    private Color4 Shade(Vector3 p, Vector3 normal, int sx, int sz)
    {
        int column = sx * Samples + sz;
        return Shade(p, normal, _padWeight[column], _pathWeight[column], _carved[column]);
    }

    private static Color4 Shade(Vector3 p, Vector3 normal, float pad, float path, bool carved)
    {
        float tone = 0.5f + 0.5f * Fbm(p.X / 7f + 3.1f, p.Z / 7f - 8.4f);
        var color = Color4.Lerp(new Color4(0.21f, 0.40f, 0.15f, 1f), new Color4(0.36f, 0.53f, 0.20f, 1f), tone);
        color = Color4.Lerp(color, new Color4(0.55f, 0.45f, 0.32f, 1f), Smooth(0.2f, 0.7f, path));
        color = Color4.Lerp(color, new Color4(0.72f, 0.70f, 0.64f, 1f), Smooth(0.45f, 0.75f, pad) * (p.Y < 0.3f && normal.Y > 0.9f ? 1f : 0f));
        var rock = Color4.Lerp(new Color4(0.46f, 0.44f, 0.41f, 1f), new Color4(0.58f, 0.55f, 0.50f, 1f), tone);
        color = Color4.Lerp(color, rock, Smooth(0.78f, 0.55f, normal.Y));
        if (carved && p.Y < 0.1f)
            color = Color4.Lerp(color, new Color4(0.42f, 0.30f, 0.20f, 1f), Smooth(0.95f, 0.75f, normal.Y) * 0.6f + 0.4f);
        return Color4.Lerp(color, new Color4(0.93f, 0.94f, 0.96f, 1f), Smooth(42f, 50f, p.Y) * Smooth(0.55f, 0.75f, normal.Y));
    }

    // Hills and mountains around the voxel field, on a grid whose spacing grows with distance; it slips under the voxel mesh where both exist
    private void AddRing()
    {
        var coordinates = new List<float>();
        for (float c = -Half; c <= Half + 0.01f; c += 2f)
            coordinates.Add(c);
        float step = 2f;
        for (float c = Half; c < Far;)
        {
            step *= 1.15f;
            c += step;
            coordinates.Add(c);
            coordinates.Insert(0, -c);
        }
        int n = coordinates.Count;
        var vertices = new VertexPositionNormalColor[n * n];
        Parallel.For(0, n, i =>
        {
            for (int j = 0; j < n; j++)
            {
                float x = coordinates[i], z = coordinates[j];
                float h = Surface(x, z).Height;
                const float e = 1f;
                var normal = Vector3.Normalize(new Vector3(Surface(x - e, z).Height - Surface(x + e, z).Height, 2f * e, Surface(x, z - e).Height - Surface(x, z + e).Height));
                if (MathF.Abs(x) <= Half && MathF.Abs(z) <= Half)
                    h -= 0.3f;
                var p = new Vector3(x, h, z);
                vertices[i * n + j] = new VertexPositionNormalColor(p, normal, (Color)Shade(p, normal, 0f, 0f, false));
            }
        });
        var indices = new List<int>();
        const float inner = Half - 4f;
        for (int i = 0; i < n - 1; i++)
        {
            for (int j = 0; j < n - 1; j++)
            {
                if (MathF.Abs(coordinates[i]) < inner && MathF.Abs(coordinates[i + 1]) < inner && MathF.Abs(coordinates[j]) < inner && MathF.Abs(coordinates[j + 1]) < inner)
                    continue;
                int a = i * n + j, b = (i + 1) * n + j, c = (i + 1) * n + j + 1, d = i * n + j + 1;
                indices.AddRange([a, c, d, a, b, c]);
            }
        }
        var bounds = new BoundingBox(new Vector3(-Far - 300f, Bottom, -Far - 300f), new Vector3(Far + 300f, 120f, Far + 300f));
        var chunk = new Chunk(_game, _material, bounds, vertices, [.. indices]);
        _ring = chunk.Entity;
        _ring.Name = "far hills";
        _ring.Get<ModelComponent>().IsShadowCaster = false;
    }

    private sealed class Chunk
    {
        private readonly Mesh _mesh;
        private Buffer _vertices, _indices;

        public Entity Entity { get; }

        public Chunk(DemoGame game, Material material, BoundingBox bounds, VertexPositionNormalColor[] vertices, int[] indices)
        {
            _mesh = new Mesh { Draw = new MeshDraw { PrimitiveType = PrimitiveType.TriangleList }, BoundingBox = bounds, BoundingSphere = BoundingSphere.FromBox(bounds) };
            (_vertices, _indices) = Upload(game, vertices, indices);
            var model = new Model { BoundingBox = bounds };
            model.Meshes.Add(_mesh);
            model.Materials.Add(new MaterialInstance(material));
            Entity = new Entity("ground") { new ModelComponent(model) };
        }

        public void Replace(DemoGame game, VertexPositionNormalColor[] vertices, int[] indices)
        {
            var (oldVertices, oldIndices) = (_vertices, _indices);
            (_vertices, _indices) = Upload(game, vertices, indices);
            oldVertices.Dispose();
            oldIndices.Dispose();
        }

        private (Buffer, Buffer) Upload(DemoGame game, VertexPositionNormalColor[] vertices, int[] indices)
        {
            // An empty chunk still needs buffers: one degenerate triangle
            if (indices.Length == 0)
            {
                vertices = [default];
                indices = [0, 0, 0];
            }
            var vertexBuffer = Buffer.Vertex.New(game.GraphicsDevice, vertices, GraphicsResourceUsage.Default);
            var indexBuffer = Buffer.Index.New(game.GraphicsDevice, indices);
            _mesh.Draw.VertexBuffers = [new VertexBufferBinding(vertexBuffer, VertexPositionNormalColor.Layout, vertices.Length)];
            _mesh.Draw.IndexBuffer = new IndexBufferBinding(indexBuffer, true, indices.Length);
            _mesh.Draw.DrawCount = indices.Length;
            return (vertexBuffer, indexBuffer);
        }
    }
}
