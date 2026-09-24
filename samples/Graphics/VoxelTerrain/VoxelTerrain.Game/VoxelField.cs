// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System;
using Stride.Core.Mathematics;
using Stride.Games;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.ComputeEffect;
using Stride.Rendering.Voxels.Grid;
using GraphicsBuffer = Stride.Graphics.Buffer;

namespace VoxelTerrain;

/// <summary>
/// One ring of the field on the GPU: its samples as a 3D texture with a mip chain, the min/max
/// occupancy pyramid the traversal skips empty space with, and the column texture the generator
/// reads. Everything is produced and rebuilt by compute passes over a box of samples; the CPU
/// never writes a sample and reads them back only for the collider.
/// </summary>
public sealed class VoxelField : IDisposable
{
    /// <summary>Samples per axis.</summary>
    public int Samples { get; }
    public Int3 SampleCount => new(Samples);

    /// <summary>Density in red, material id in green; level 0 is the field, the mips are the far field.</summary>
    public Texture Texture { get; }

    /// <summary>The pyramid over <see cref="Texture"/>, rebuilt here on the GPU.</summary>
    public VoxelGridOccupancy Occupancy { get; }

    /// <summary>One column per (x, z): height, slope, tint, cave-proneness. Rebuilt whenever the ring moves.</summary>
    public Texture Heights { get; }

    /// <summary>The columns as the next coarser ring sees them, for the outer band to blend towards.</summary>
    public Texture HeightsCoarse { get; }

    private readonly IGame game;
    private readonly Texture[] fieldLevels;
    private readonly Texture[] occupancyLevels;
    private ComputeEffectShader? height, generate, mip, occupancyBase, occupancyUp;
    private RenderDrawContext? drawContext;
    private Texture? staging;

    public VoxelField(IGame game, int samples)
    {
        this.game = game;
        Samples = samples;
        var device = game.GraphicsDevice;
        Texture = Texture.New3D(device, samples, samples, samples, new MipMapCount(true), PixelFormat.R8G8_UNorm,
            TextureFlags.ShaderResource | TextureFlags.UnorderedAccess, GraphicsResourceUsage.Default);
        Occupancy = new VoxelGridOccupancy(device, SampleCount, unorderedAccess: true);
        Heights = Texture.New2D(device, samples, samples, 1, PixelFormat.R32G32B32A32_Float,
            TextureFlags.ShaderResource | TextureFlags.UnorderedAccess, 1, GraphicsResourceUsage.Default);
        HeightsCoarse = Texture.New2D(device, samples, samples, 1, PixelFormat.R32G32B32A32_Float,
            TextureFlags.ShaderResource | TextureFlags.UnorderedAccess, 1, GraphicsResourceUsage.Default);

        // One view per mip: a compute pass reads one level and writes the next, and Direct3D wants
        // the two as distinct subresources.
        fieldLevels = new Texture[Texture.MipLevelCount];
        for (int level = 0; level < fieldLevels.Length; level++)
            fieldLevels[level] = LevelView(Texture, level);
        occupancyLevels = new Texture[Occupancy.Levels];
        for (int level = 0; level < occupancyLevels.Length; level++)
            occupancyLevels[level] = LevelView(Occupancy.Texture, level);
    }

    private static Texture LevelView(Texture texture, int level) => texture.ToTextureView(new TextureViewDescription
    {
        Type = ViewType.Single,
        MipLevel = level,
        ArraySlice = 0,
        Flags = TextureFlags.ShaderResource | TextureFlags.UnorderedAccess,
        Format = texture.Format,
    });

    /// <summary>What a generation needs to know about where the ring stands and what was painted on it.</summary>
    public struct Placement
    {
        public Vector3 WorldOrigin;
        public float CellSize;
        public float Seed;
        /// <summary>The next finer ring's box, in world units, left empty here; max below min for none.</summary>
        public Vector3 HoleMin, HoleMax;
        public GraphicsBuffer Brushes;
        public int BrushCount;
    }

    /// <summary>The whole ring: the columns, then every sample, then the mips and the pyramid.</summary>
    public void Generate(in Placement placement)
    {
        Prepare();
        height!.Parameters.Set(TerrainNoiseKeys.Seed, placement.Seed);
        height.Parameters.Set(TerrainHeightKeys.HeightsOut, Heights);
        height.Parameters.Set(TerrainHeightKeys.HeightsCoarseOut, HeightsCoarse);
        height.Parameters.Set(TerrainHeightKeys.HeightSize, new Int2(Samples));
        height.Parameters.Set(TerrainHeightKeys.WorldOriginXZ, new Vector2(placement.WorldOrigin.X, placement.WorldOrigin.Z));
        height.Parameters.Set(TerrainHeightKeys.CellSize, placement.CellSize);
        Dispatch(height, new Int3(8, 8, 1), new Int3(Samples, Samples, 1));

        GenerateBox(placement, Int3.Zero, SampleCount - Int3.One);
    }

    /// <summary>The samples over a box, inclusive, from the columns already generated; then what derives from them over that box.</summary>
    public void GenerateBox(in Placement placement, Int3 lo, Int3 hi)
    {
        Prepare();
        lo = Int3.Max(lo, Int3.Zero);
        hi = Int3.Min(hi, SampleCount - Int3.One);
        var extent = hi - lo + Int3.One;
        if (extent.X <= 0 || extent.Y <= 0 || extent.Z <= 0)
            return;

        var parameters = generate!.Parameters;
        parameters.Set(TerrainNoiseKeys.Seed, placement.Seed);
        parameters.Set(VoxelFieldBoxKeys.Origin, lo);
        parameters.Set(VoxelFieldBoxKeys.TargetSize, SampleCount);
        parameters.Set(TerrainGenerateKeys.Heights, Heights);
        parameters.Set(TerrainGenerateKeys.HeightsCoarse, HeightsCoarse);
        parameters.Set(TerrainGenerateKeys.Target, fieldLevels[0]);
        parameters.Set(TerrainGenerateKeys.WorldOrigin, placement.WorldOrigin);
        parameters.Set(TerrainGenerateKeys.CellSize, placement.CellSize);
        parameters.Set(TerrainGenerateKeys.HoleMin, placement.HoleMin);
        parameters.Set(TerrainGenerateKeys.HoleMax, placement.HoleMax);
        parameters.Set(TerrainGenerateKeys.Brushes, placement.Brushes);
        parameters.Set(TerrainGenerateKeys.BrushCount, placement.BrushCount);
        Dispatch(generate, new Int3(8), extent);

        Rebuild(lo, hi);
    }

    /// <summary>
    /// The samples, back on the CPU, two bytes each in the texture's order (x varying fastest):
    /// density then material, which read as a little-endian ushort is the collider's packing.
    /// Waits for the GPU; a few milliseconds for a 129³ ring.
    /// </summary>
    public void ReadBack(byte[] into)
    {
        staging ??= Texture.ToStaging();
        Texture.GetData(game.GraphicsContext.CommandList, staging, into, 0, 0);
    }

    private void Prepare()
    {
        if (drawContext is not null)
            return;
        var services = game.Services;
        var renderContext = RenderContext.GetShared(services);
        drawContext = new RenderDrawContext(services, renderContext, game.GraphicsContext);
        height = new ComputeEffectShader(renderContext) { ShaderSourceName = "TerrainHeight" };
        generate = new ComputeEffectShader(renderContext) { ShaderSourceName = "TerrainGenerate" };
        mip = new ComputeEffectShader(renderContext) { ShaderSourceName = "VoxelFieldMip" };
        occupancyBase = new ComputeEffectShader(renderContext) { ShaderSourceName = "VoxelFieldOccupancyBase" };
        occupancyUp = new ComputeEffectShader(renderContext) { ShaderSourceName = "VoxelFieldOccupancyUp" };
    }

    /// <summary>The mips and the pyramid over a box of level-0 samples, inclusive.</summary>
    private void Rebuild(Int3 lo, Int3 hi)
    {
        // Each mip from the one above it. A texel at level L filters the finer level one texel each
        // way, so the box widens by one at every level.
        for (int level = 1; level < fieldLevels.Length; level++)
        {
            var sourceSize = new Int3(Texture.CalculateMipSize(Samples, level - 1));
            var targetSize = new Int3(Texture.CalculateMipSize(Samples, level));
            var (origin, extent) = Box(lo, hi, level, 1, targetSize);
            mip!.Parameters.Set(VoxelFieldBoxKeys.Origin, origin);
            mip.Parameters.Set(VoxelFieldBoxKeys.TargetSize, targetSize);
            mip.Parameters.Set(VoxelFieldMipKeys.Source, fieldLevels[level - 1]);
            mip.Parameters.Set(VoxelFieldMipKeys.Target, fieldLevels[level]);
            mip.Parameters.Set(VoxelFieldMipKeys.SourceSize, sourceSize);
            Dispatch(mip, new Int3(8), extent);
        }

        // The pyramid: bricks of two cells at the base from the field, each level from the one under
        // it. Bricks overlap by one sample, so the box widens by a brick each way.
        var baseSize = new Int3(Occupancy.Texture.Width, Occupancy.Texture.Height, Occupancy.Texture.Depth);
        {
            var (origin, extent) = Box(lo, hi, 1, 1, baseSize);
            occupancyBase!.Parameters.Set(VoxelFieldBoxKeys.Origin, origin);
            occupancyBase.Parameters.Set(VoxelFieldBoxKeys.TargetSize, baseSize);
            occupancyBase.Parameters.Set(VoxelFieldOccupancyBaseKeys.Field, fieldLevels[0]);
            occupancyBase.Parameters.Set(VoxelFieldOccupancyBaseKeys.Target, occupancyLevels[0]);
            occupancyBase.Parameters.Set(VoxelFieldOccupancyBaseKeys.SampleCount, SampleCount);
            Dispatch(occupancyBase, new Int3(4), extent);
        }
        for (int level = 1; level < occupancyLevels.Length; level++)
        {
            var sourceSize = MipSize(baseSize, level - 1);
            var targetSize = MipSize(baseSize, level);
            var (origin, extent) = Box(lo, hi, level + 1, 1, targetSize);
            occupancyUp!.Parameters.Set(VoxelFieldBoxKeys.Origin, origin);
            occupancyUp.Parameters.Set(VoxelFieldBoxKeys.TargetSize, targetSize);
            occupancyUp.Parameters.Set(VoxelFieldOccupancyUpKeys.Source, occupancyLevels[level - 1]);
            occupancyUp.Parameters.Set(VoxelFieldOccupancyUpKeys.Target, occupancyLevels[level]);
            occupancyUp.Parameters.Set(VoxelFieldOccupancyUpKeys.SourceSize, sourceSize);
            Dispatch(occupancyUp, new Int3(4), extent);
        }
    }

    /// <summary>A level-0 box shifted down to a level and widened by a margin, clamped to that level's size: its origin and extent.</summary>
    private static (Int3 Origin, Int3 Extent) Box(Int3 lo, Int3 hi, int shift, int margin, Int3 size)
    {
        var origin = Int3.Max(Shift(lo, shift) - new Int3(margin), Int3.Zero);
        var last = Int3.Min(Shift(hi, shift) + new Int3(margin), size - Int3.One);
        return (origin, Int3.Max(last - origin + Int3.One, Int3.Zero));
    }

    private static Int3 Shift(Int3 value, int shift) => new(value.X >> shift, value.Y >> shift, value.Z >> shift);

    private static Int3 MipSize(Int3 size, int level) => new(
        Texture.CalculateMipSize(size.X, level),
        Texture.CalculateMipSize(size.Y, level),
        Texture.CalculateMipSize(size.Z, level));

    private void Dispatch(ComputeEffectShader shader, Int3 threads, Int3 cells)
    {
        if (cells.X <= 0 || cells.Y <= 0 || cells.Z <= 0)
            return;
        shader.ThreadNumbers = threads;
        shader.ThreadGroupCounts = new Int3(
            (cells.X + threads.X - 1) / threads.X,
            (cells.Y + threads.Y - 1) / threads.Y,
            (cells.Z + threads.Z - 1) / threads.Z);
        shader.Draw(drawContext!);
    }

    public void Dispose()
    {
        height?.Dispose();
        generate?.Dispose();
        mip?.Dispose();
        occupancyBase?.Dispose();
        occupancyUp?.Dispose();
        staging?.Dispose();
        foreach (var view in fieldLevels)
            view.Dispose();
        foreach (var view in occupancyLevels)
            view.Dispose();
        Occupancy.Dispose();
        Heights.Dispose();
        HeightsCoarse.Dispose();
        Texture.Dispose();
    }
}
