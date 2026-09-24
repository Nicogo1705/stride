// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System;
using System.Collections.Generic;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.ComputeEffect;

namespace VoxelTerrain;

/// <summary>
/// Water as a height field over the ground, simulated on the GPU with virtual pipes between
/// columns: waves, settling, flow down slopes and into holes, stable at a fixed small step. The
/// domain is a square around the camera that moves with it; outside it the sea is still, at sea
/// level. The ground under the water is read from the terrain rings, so a dug hole fills.
/// </summary>
public sealed class WaterSim : IDisposable
{
    /// <summary>Columns along each side of the domain.</summary>
    public const int Size = 256;
    /// <summary>World size of a column.</summary>
    public const float Cell = 0.5f;
    public const float Extent = Size * Cell;

    public const float SeaLevel = 0f;
    public float Gravity { get; set; } = 9.81f;
    public float Damping { get; set; } = 0.995f;
    /// <summary>The fixed step. Waves travel at most a cell per step at any depth the terrain offers.</summary>
    public const float Dt = 1f / 120f;
    public int MaxStepsPerFrame { get; set; } = 4;

    /// <summary>World position of column (0, 0); y unused.</summary>
    public Vector3 Origin { get; private set; }
    public bool Placed { get; private set; }

    /// <summary>The ground moved under the water: the bed is read from the rings again before the next step.</summary>
    public bool BedDirty { get; set; } = true;

    public Texture Height => heights[current];
    public Texture Velocity { get; }
    public Texture Bed { get; }
    public int Steps { get; private set; }

    /// <summary>Springs: a point over XZ, a radius, and a height per second, poured every step.</summary>
    public List<(Vector2 Centre, float Radius, float Rate)> Springs { get; } = [];

    private readonly Game game;
    private readonly TerrainClipmap clipmap;
    private readonly Texture[] heights = new Texture[2];
    private readonly Texture[] fluxes = new Texture[2];
    private int current;
    private float accumulator;
    private readonly Queue<(Vector2 Centre, float Radius, float Rate)> pours = new();
    private ComputeEffectShader? bed, flux, height, shift;
    private RenderDrawContext? drawContext;
    private Texture? staging, bedStaging;
    private float[]? readback, bedReadback;
    private int framesSinceReadback;

    public WaterSim(Game game, TerrainClipmap clipmap)
    {
        this.game = game;
        this.clipmap = clipmap;
        var device = game.GraphicsDevice;
        const TextureFlags flags = TextureFlags.ShaderResource | TextureFlags.UnorderedAccess;
        for (int i = 0; i < 2; i++)
        {
            heights[i] = Texture.New2D(device, Size, Size, 1, PixelFormat.R32_Float, flags, 1, GraphicsResourceUsage.Default);
            fluxes[i] = Texture.New2D(device, Size, Size, 1, PixelFormat.R32G32B32A32_Float, flags, 1, GraphicsResourceUsage.Default);
        }
        Bed = Texture.New2D(device, Size, Size, 1, PixelFormat.R32_Float, flags, 1, GraphicsResourceUsage.Default);
        Velocity = Texture.New2D(device, Size, Size, 1, PixelFormat.R32G32_Float, flags, 1, GraphicsResourceUsage.Default);
    }

    /// <summary>Adds a ball of water over a point on the next step.</summary>
    public void Pour(Vector2 centre, float radius, float rate) => pours.Enqueue((centre, radius, rate));

    /// <summary>Moves the domain with the camera, refreshes the bed, and runs the steps the frame is due.</summary>
    public void Update(Vector3 camera, float frameSeconds)
    {
        Prepare();

        // The domain follows the camera by whole groups of columns, so a move is a copy.
        const float align = Cell * 8f;
        var half = Extent * 0.5f;
        var drift = new Vector2(camera.X - (Origin.X + half), camera.Z - (Origin.Z + half));
        if (!Placed || MathF.Abs(drift.X) > Extent / 8f || MathF.Abs(drift.Y) > Extent / 8f)
        {
            var desired = new Vector3(
                MathF.Round((camera.X - half) / align) * align, 0f,
                MathF.Round((camera.Z - half) / align) * align);
            if (!Placed || desired != Origin)
            {
                var offset = Placed
                    ? new Int2((int)MathF.Round((Origin.X - desired.X) / Cell), (int)MathF.Round((Origin.Z - desired.Z) / Cell))
                    : new Int2(Size * 4);
                Origin = desired;
                Placed = true;
                RebuildBed();
                Shift(offset);
            }
        }
        else if (BedDirty)
        {
            RebuildBed();
        }

        accumulator = MathF.Min(accumulator + frameSeconds, Dt * MaxStepsPerFrame);
        var steps = 0;
        while (accumulator >= Dt && steps < MaxStepsPerFrame)
        {
            Step();
            accumulator -= Dt;
            steps++;
        }

        // The heights back on the CPU now and then, for whoever asks where the surface is.
        if (++framesSinceReadback >= 8)
        {
            framesSinceReadback = 0;
            staging ??= Height.ToStaging();
            readback ??= new float[Size * Size];
            Height.GetData(game.GraphicsContext.CommandList, staging, readback, 0, 0);
            bedStaging ??= Bed.ToStaging();
            bedReadback ??= new float[Size * Size];
            Bed.GetData(game.GraphicsContext.CommandList, bedStaging, bedReadback, 0, 0);
        }
    }

    /// <summary>Height of the water surface over a point, from the last read back; sea level outside the domain; null where the column is dry.</summary>
    public float? SurfaceAt(Vector3 world)
    {
        if (readback is null || bedReadback is null || !Placed)
            return SeaLevel;
        var x = (int)MathF.Floor((world.X - Origin.X) / Cell);
        var z = (int)MathF.Floor((world.Z - Origin.Z) / Cell);
        if (x < 0 || z < 0 || x >= Size || z >= Size)
            return SeaLevel;
        var h = readback[z * Size + x];
        return h > 0.02f ? bedReadback[z * Size + x] + h : null;
    }

    private void Prepare()
    {
        if (drawContext is not null)
            return;
        var services = game.Services;
        var renderContext = RenderContext.GetShared(services);
        drawContext = new RenderDrawContext(services, renderContext, game.GraphicsContext);
        bed = new ComputeEffectShader(renderContext) { ShaderSourceName = "WaterBed" };
        flux = new ComputeEffectShader(renderContext) { ShaderSourceName = "WaterFlux" };
        height = new ComputeEffectShader(renderContext) { ShaderSourceName = "WaterHeight" };
        shift = new ComputeEffectShader(renderContext) { ShaderSourceName = "WaterShift" };
    }

    private void RebuildBed()
    {
        BedDirty = false;
        var rings = clipmap.Rings;
        var p = bed!.Parameters;
        p.Set(WaterBedKeys.BedOut, Bed);
        p.Set(WaterBedKeys.SimSize, new Int2(Size));
        p.Set(WaterBedKeys.SimOriginXZ, new Vector2(Origin.X, Origin.Z));
        p.Set(WaterBedKeys.SimCell, Cell);
        p.Set(WaterBedKeys.Samples, clipmap.Samples);
        Ring(p, rings, 0, WaterBedKeys.Field0, WaterBedKeys.Origin0, WaterBedKeys.Cell0);
        Ring(p, rings, 1, WaterBedKeys.Field1, WaterBedKeys.Origin1, WaterBedKeys.Cell1);
        Ring(p, rings, 2, WaterBedKeys.Field2, WaterBedKeys.Origin2, WaterBedKeys.Cell2);
        Dispatch(bed, new Int3(8, 8, 1), new Int3(Size, Size, 1));
    }

    private static void Ring(ParameterCollection p, IReadOnlyList<TerrainClipmap.Ring> rings, int level,
        ObjectParameterKey<Texture> field, ValueParameterKey<Vector3> origin, ValueParameterKey<float> cell)
    {
        // A missing ring is the last one again, which the finer ones already answered for.
        var ring = rings[Math.Min(level, rings.Count - 1)];
        p.Set(field, ring.Field.Texture);
        p.Set(origin, ring.Origin);
        p.Set(cell, ring.CellSize);
    }

    private void Shift(Int2 offset)
    {
        var next = 1 - current;
        var p = shift!.Parameters;
        p.Set(WaterShiftKeys.Height, heights[current]);
        p.Set(WaterShiftKeys.Flux, fluxes[current]);
        p.Set(WaterShiftKeys.Bed, Bed);
        p.Set(WaterShiftKeys.HeightOut, heights[next]);
        p.Set(WaterShiftKeys.FluxOut, fluxes[next]);
        p.Set(WaterShiftKeys.SimSize, new Int2(Size));
        p.Set(WaterShiftKeys.Offset, offset);
        p.Set(WaterShiftKeys.SeaLevel, SeaLevel);
        Dispatch(shift, new Int3(8, 8, 1), new Int3(Size, Size, 1));
        current = next;
    }

    private void Step()
    {
        var next = 1 - current;

        var f = flux!.Parameters;
        f.Set(WaterFluxKeys.Height, heights[current]);
        f.Set(WaterFluxKeys.Bed, Bed);
        f.Set(WaterFluxKeys.Flux, fluxes[current]);
        f.Set(WaterFluxKeys.FluxOut, fluxes[next]);
        f.Set(WaterFluxKeys.SimSize, new Int2(Size));
        f.Set(WaterFluxKeys.Cell, Cell);
        f.Set(WaterFluxKeys.Dt, Dt);
        f.Set(WaterFluxKeys.Gravity, Gravity);
        f.Set(WaterFluxKeys.Damping, Damping);
        Dispatch(flux, new Int3(8, 8, 1), new Int3(Size, Size, 1));

        // One pour per step: what the player asked for first, else a spring in turn.
        var pour = (Centre: Vector2.Zero, Radius: 0f, Rate: 0f);
        if (pours.Count > 0)
            pour = pours.Dequeue();
        else if (Springs.Count > 0)
            pour = Springs[Steps % Springs.Count];

        var h = height!.Parameters;
        h.Set(WaterHeightKeys.Height, heights[current]);
        h.Set(WaterHeightKeys.Bed, Bed);
        h.Set(WaterHeightKeys.Flux, fluxes[next]);
        h.Set(WaterHeightKeys.HeightOut, heights[next]);
        h.Set(WaterHeightKeys.VelocityOut, Velocity);
        h.Set(WaterHeightKeys.SimSize, new Int2(Size));
        h.Set(WaterHeightKeys.SimOriginXZ, new Vector2(Origin.X, Origin.Z));
        h.Set(WaterHeightKeys.Cell, Cell);
        h.Set(WaterHeightKeys.Dt, Dt);
        h.Set(WaterHeightKeys.SeaLevel, SeaLevel);
        h.Set(WaterHeightKeys.Pour, new Vector3(pour.Centre.X, pour.Centre.Y, pour.Radius));
        h.Set(WaterHeightKeys.PourRate, pour.Rate * Springs.Count);
        Dispatch(height, new Int3(8, 8, 1), new Int3(Size, Size, 1));

        current = next;
        Steps++;
    }

    private void Dispatch(ComputeEffectShader shader, Int3 threads, Int3 cells)
    {
        shader.ThreadNumbers = threads;
        shader.ThreadGroupCounts = new Int3(
            (cells.X + threads.X - 1) / threads.X,
            (cells.Y + threads.Y - 1) / threads.Y,
            (cells.Z + threads.Z - 1) / threads.Z);
        shader.Draw(drawContext!);
    }

    public void Dispose()
    {
        bed?.Dispose();
        flux?.Dispose();
        height?.Dispose();
        shift?.Dispose();
        staging?.Dispose();
        bedStaging?.Dispose();
        foreach (var t in heights) t.Dispose();
        foreach (var t in fluxes) t.Dispose();
        Bed.Dispose();
        Velocity.Dispose();
    }
}
