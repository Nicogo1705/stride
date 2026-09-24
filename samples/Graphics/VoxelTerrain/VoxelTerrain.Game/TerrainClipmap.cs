// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System;
using System.Collections.Generic;
using Stride.BepuPhysics;
using Stride.BepuPhysics.Definitions.Colliders;
using Stride.BepuPhysics.Definitions.Colliders.Voxels;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Voxels.Grid;
using GraphicsBuffer = Stride.Graphics.Buffer;

namespace VoxelTerrain;

/// <summary>
/// The world as nested rings of voxel grids around the camera: the finest ring holds a few tens of
/// metres at the finest cell, each ring out covers twice the distance at twice the cell, and the
/// outermost reaches the horizon. Every ring is one <see cref="VoxelGridComponent"/> - traced, not
/// meshed - and each coarser ring leaves a hole where the finer one draws, so the surface is drawn
/// once at the finest level of detail the distance allows.
/// </summary>
/// <remarks>
/// <para>
/// A ring is regenerated on the GPU, whole, whenever the camera has moved far enough from its
/// centre: the landscape is a function evaluated in a compute pass, so there is nothing to stream
/// and nothing to keep but the brushes the player painted, which the generator applies again. A
/// ring's origin snaps to twice its own cell, which puts the finer ring's box on the coarser ring's
/// sample lattice and keeps the hole's edges on whole cells.
/// </para>
/// <para>
/// The finest ring is also read back to the CPU for the voxel collider, so the ground under the
/// player is solid exactly where it is drawn.
/// </para>
/// </remarks>
public sealed class TerrainClipmap : IDisposable
{
    public sealed class Ring
    {
        public required int Level;
        public required float CellSize;
        public required VoxelField Field;
        public required Entity Entity;
        public required VoxelGridComponent Grid;
        /// <summary>World position of sample (0, 0, 0).</summary>
        public Vector3 Origin;
        public bool Placed;
        public bool Dirty;
        public int Generations;
    }

    public IReadOnlyList<Ring> Rings => rings;
    public int Samples { get; }
    public int Seed { get; }

    /// <summary>Rings regenerated per frame after the first: one keeps the frame smooth while the camera flies.</summary>
    public int RegenerationsPerFrame { get; set; } = 1;

    /// <summary>The finest ring's box, in world units.</summary>
    public BoundingBox FinestBox => new(rings[0].Origin, rings[0].Origin + new Vector3(Extent(rings[0])));

    public int BrushCount => brushes.Count / 2;

    private readonly Game game;
    private readonly Scene scene;
    private readonly List<Ring> rings = [];
    private readonly List<Vector4> brushes = [];
    private GraphicsBuffer brushBuffer;
    private bool brushesDirty;
    private bool firstUpdate = true;

    // The collider over the finest ring.
    private VoxelCollider? collider;
    private readonly byte[] readback;
    private readonly ushort[] colliderSamples;
    private Entity? colliderEntity;

    /// <param name="ringCount">Rings, finest first. Seven at 129 samples and a 0.25 m cell reach 1 km out.</param>
    /// <param name="samples">Samples along each axis of every ring; cells are one fewer.</param>
    /// <param name="finestCell">World size of the finest ring's cell.</param>
    /// <param name="shadowRings">How many of the finest rings write the shadow maps; the GI lights the rest.</param>
    public TerrainClipmap(Game game, Scene scene, int ringCount, int samples, float finestCell, int seed, Material material, int shadowRings, bool levelOfDetail, int beamBlockSize)
    {
        this.game = game;
        this.scene = scene;
        Samples = samples;
        Seed = seed;

        brushBuffer = GraphicsBuffer.Structured.New(game.GraphicsDevice, 2, 16, false);

        for (int level = 0; level < ringCount; level++)
        {
            var cellSize = finestCell * (1 << level);
            var field = new VoxelField(game, samples);
            var grid = new VoxelGridComponent
            {
                Traversal = new VoxelGridTraversalDDA
                {
                    Occupancy = field.Occupancy,
                    Source = new VoxelGridSourceTexture3D { Texture = field.Texture, SampleCount = field.SampleCount },
                    CellSize = cellSize,
                    IsoLevel = 0.5f,
                    MaxSteps = samples * 3 + 64,
                    Surface = VoxelSurfaceForm.MarchingCubes,
                },
                CastShadows = level < shadowRings,
                InjectIntoGI = true,
                LevelOfDetail = levelOfDetail,
                BeamBlockSize = beamBlockSize,
            };
            grid.Material = material;
            var entity = new Entity($"Terrain ring {level}") { grid };
            scene.Entities.Add(entity);
            rings.Add(new Ring { Level = level, CellSize = cellSize, Field = field, Entity = entity, Grid = grid });
        }

        readback = new byte[samples * samples * samples * 2];
        colliderSamples = new ushort[samples * samples * samples];
    }

    /// <summary>World size of a ring along each axis.</summary>
    public float Extent(Ring ring) => (Samples - 1) * ring.CellSize;

    /// <summary>Snaps the rings to the camera and regenerates the ones that moved.</summary>
    public void Update(Vector3 camera)
    {
        for (int level = 0; level < rings.Count; level++)
        {
            var ring = rings[level];
            var extent = Extent(ring);
            var half = new Vector3(extent * 0.5f);
            var centre = ring.Origin + half;
            var drift = camera - centre;
            // Re-centred once the camera is an eighth of the ring away from its centre, so a ring
            // is not regenerated at every step but the camera never nears its edge.
            var slack = extent / 8f;
            if (ring.Placed && MathF.Abs(drift.X) < slack && MathF.Abs(drift.Y) < slack && MathF.Abs(drift.Z) < slack)
                continue;

            var align = ring.CellSize * 2f;
            var desired = (camera - half) / align;
            desired = new Vector3(MathF.Round(desired.X), MathF.Round(desired.Y), MathF.Round(desired.Z)) * align;
            if (ring.Placed && desired == ring.Origin)
                continue;

            ring.Origin = desired;
            ring.Placed = true;
            ring.Dirty = true;
            // The coarser ring's hole is this ring's box, so it moves too.
            if (level + 1 < rings.Count)
                rings[level + 1].Dirty = true;
        }

        if (brushesDirty)
        {
            UploadBrushes();
            brushesDirty = false;
        }

        // Everything at once on the first frame; afterwards a budget, finest first, since that is
        // the ground under the player and the collider.
        var budget = firstUpdate ? rings.Count : RegenerationsPerFrame;
        firstUpdate = false;
        for (int level = 0; level < rings.Count && budget > 0; level++)
        {
            if (!rings[level].Dirty)
                continue;
            Regenerate(rings[level]);
            budget--;
        }
    }

    private VoxelField.Placement PlacementOf(Ring ring)
    {
        var placement = new VoxelField.Placement
        {
            WorldOrigin = ring.Origin,
            CellSize = ring.CellSize,
            Seed = Seed * 1000f,
            HoleMin = new Vector3(1f),
            HoleMax = new Vector3(-1f),
            Brushes = brushBuffer,
            BrushCount = brushes.Count / 2,
        };
        if (ring.Level > 0)
        {
            // The finer ring's box, shrunk by one of this ring's cells: the two overlap by that
            // much and the depth test picks the nearer, so a seam shows no gap.
            var finer = rings[ring.Level - 1];
            placement.HoleMin = finer.Origin + new Vector3(ring.CellSize);
            placement.HoleMax = finer.Origin + new Vector3(Extent(finer) - ring.CellSize);
        }
        return placement;
    }

    private void Regenerate(Ring ring)
    {
        ring.Dirty = false;
        ring.Generations++;
        ring.Entity.Transform.Position = ring.Origin;
        ring.Field.Generate(PlacementOf(ring));
        if (ring.Level == 0)
            SyncCollider();
    }

    /// <summary>The finest ring back on the CPU, into the collider, on a fresh static at the ring's origin.</summary>
    private void SyncCollider()
    {
        var ring = rings[0];
        Console.WriteLine($"[VoxelTerrain] collider sync #{ring.Generations} at {ring.Origin}");
        ring.Field.ReadBack(readback);
        // The texture is laid out with x varying fastest; the collider reads z fastest.
        var n = Samples;
        for (int z = 0; z < n; z++)
            for (int y = 0; y < n; y++)
            {
                var row = (z * n + y) * n;
                for (int x = 0; x < n; x++)
                    colliderSamples[(x * n + y) * n + z] = (ushort)(readback[(row + x) * 2] | (readback[(row + x) * 2 + 1] << 8));
            }
        // A static's pose is taken when it is attached, and a collider stays bound to its component
        // until that is torn down, so the terrain stands on a new pair at each move.
        if (colliderEntity is not null)
        {
            scene.Entities.Remove(colliderEntity);
            collider?.Dispose();
        }
        collider = new VoxelCollider
        {
            Form = VoxelChildForm.TriangleMarchingCubes,
            CellSize = ring.CellSize,
            IsoLevel = 0.5f,
        };
        collider.SetData(n, n, n, colliderSamples);
        colliderEntity = new Entity("Terrain collider") { new StaticComponent { Collider = collider } };
        colliderEntity.Transform.Position = ring.Origin;
        scene.Entities.Add(colliderEntity);
    }

    /// <summary>
    /// Adds or removes a ball of material. The brush is kept, so a ring regenerated later still
    /// shows it; the rings it touches now are regenerated over the touched box only, and the
    /// collider's samples are edited in place.
    /// </summary>
    public void Dig(Vector3 centre, float radius, bool fill, byte material)
    {
        brushes.Add(new Vector4(centre, radius));
        brushes.Add(new Vector4(fill ? 1f : 0f, material, 0f, 0f));
        UploadBrushes();
        brushesDirty = false;

        var lo = centre - new Vector3(radius);
        var hi = centre + new Vector3(radius);
        foreach (var ring in rings)
        {
            if (!ring.Placed)
                continue;
            var inverse = 1f / ring.CellSize;
            var a = (lo - ring.Origin) * inverse;
            var b = (hi - ring.Origin) * inverse;
            var boxLo = new Int3((int)MathF.Floor(a.X), (int)MathF.Floor(a.Y), (int)MathF.Floor(a.Z));
            var boxHi = new Int3((int)MathF.Ceiling(b.X), (int)MathF.Ceiling(b.Y), (int)MathF.Ceiling(b.Z));
            if (boxHi.X < 0 || boxHi.Y < 0 || boxHi.Z < 0 || boxLo.X >= Samples || boxLo.Y >= Samples || boxLo.Z >= Samples)
                continue;
            ring.Field.GenerateBox(PlacementOf(ring), boxLo, boxHi);

            if (ring.Level == 0)
                EditCollider(ring, centre, radius, fill, material, Int3.Max(boxLo, Int3.Zero), Int3.Min(boxHi, new Int3(Samples - 1)));
        }
    }

    /// <summary>The same brush the generator applies, on the collider's samples, so the ground stays solid where it is drawn.</summary>
    private void EditCollider(Ring ring, Vector3 centre, float radius, bool fill, byte material, Int3 lo, Int3 hi)
    {
        if (colliderEntity is null)
            return;
        var touched = false;
        for (int x = lo.X; x <= hi.X; ++x)
            for (int y = lo.Y; y <= hi.Y; ++y)
                for (int z = lo.Z; z <= hi.Z; ++z)
                {
                    var p = ring.Origin + new Vector3(x, y, z) * ring.CellSize;
                    var distance = (p - centre).Length();
                    if (distance >= radius)
                        continue;
                    var index = (x * Samples + y) * Samples + z;
                    var previous = colliderSamples[index];
                    var density = previous & 0xFF;
                    var id = previous >> 8;
                    var edge = MathUtil.Clamp((distance - radius * 0.55f) / (radius * 0.45f), 0f, 1f);
                    var limit = (int)MathF.Round(255f * edge);
                    var target = fill ? Math.Max(density, 255 - limit) : Math.Min(density, limit);
                    if (fill && id == 0)
                        id = material;
                    var packed = (ushort)(target | (id << 8));
                    if (packed == previous)
                        continue;
                    colliderSamples[index] = packed;
                    collider!.SetVoxel(x, y, z, packed);
                    touched = true;
                }
        if (touched)
            collider!.NotifyFieldChanged();
    }

    private void UploadBrushes()
    {
        var count = Math.Max(brushes.Count, 2);
        if (brushBuffer.ElementCount < count)
        {
            brushBuffer.Dispose();
            brushBuffer = GraphicsBuffer.Structured.New(game.GraphicsDevice, Math.Max(count * 2, 64), 16, false);
        }
        if (brushes.Count > 0)
            brushBuffer.SetData(game.GraphicsContext.CommandList, brushes.ToArray());
    }

    public void Dispose()
    {
        foreach (var ring in rings)
        {
            scene.Entities.Remove(ring.Entity);
            ring.Field.Dispose();
        }
        rings.Clear();
        if (colliderEntity is not null)
            scene.Entities.Remove(colliderEntity);
        collider?.Dispose();
        brushBuffer.Dispose();
    }
}
