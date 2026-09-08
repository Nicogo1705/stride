// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.Trees;
using BepuUtilities.Memory;
using Stride.BepuPhysics.Definitions.Colliders.Voxels;
using Stride.BepuPhysics.Systems;
using Stride.Core;
using Stride.Core.Mathematics;
using NRigidPose = BepuPhysics.RigidPose;
using NVector3 = System.Numerics.Vector3;

namespace Stride.BepuPhysics.Definitions.Colliders;

/// <summary>
/// Collides against a voxel density field directly, generating contacts from the field as the narrow
/// phase asks for them instead of from a mesh built ahead of time.
/// </summary>
/// <typeparam name="TSource">How the samples are packed, see <see cref="IVoxelDensitySource"/>.</typeparam>
/// <remarks>
/// <para>
/// No mesh, readback or bounding volume tree is built; editing a voxel is a store into the field.
/// <see cref="Form"/> selects what a cell presents to the narrow phase. The triangle forms run the
/// same marching-cubes table and interpolation a renderer would on the same samples.
/// </para>
/// <para>
/// Triangle forms cannot use Bepu's MeshReduction (bound to the concrete Mesh type), so fast sliding
/// across internal edges may catch. <see cref="VoxelChildForm.TriangleSurfaceNets"/> suffers least,
/// <see cref="VoxelChildForm.Sphere"/> not at all.
/// </para>
/// </remarks>
[DataContract(Inherited = true)]
public abstract unsafe class VoxelColliderBase<TSource> : ICollider
    where TSource : unmanaged, IVoxelDensitySource
{
    private VoxelChildForm _form = VoxelChildForm.TriangleSurfaceNets;
    private float _cellSize = 1f;
    private float _isoLevel = 0.5f;
    private bool _invertWinding;
    private bool _sealBorder = true;
    private float _mass = 1f;
    private float _sphereRadiusScale = 1f;

    private CollidableComponent? _component;
    CollidableComponent? ICollider.Component { get => _component; set => _component = value; }

    /// <summary>What each occupied cell presents to the narrow phase.</summary>
    /// <remarks>Changing this reattaches the collidable, resetting some of its internal physics state.</remarks>
    public VoxelChildForm Form
    {
        get => _form;
        set
        {
            if (_form == value)
                return;
            _form = value;
            _component?.TryUpdateFeatures();
        }
    }

    /// <summary>Edge length of one cell, in world units.</summary>
    public float CellSize
    {
        get => _cellSize;
        set
        {
            value.ValidateGreaterThanZeroFinite(this);
            _cellSize = value;
            _component?.TryUpdateFeatures();
        }
    }

    /// <summary>Density at or above which a sample counts as solid.</summary>
    /// <remarks>Must match the renderer's iso level. Compared against the raw source value, so a signed distance field uses zero.</remarks>
    public float IsoLevel
    {
        get => _isoLevel;
        set
        {
            _isoLevel = value;
            _component?.TryUpdateFeatures();
        }
    }

    /// <summary>Reverses the winding of generated triangles.</summary>
    /// <remarks>Bepu triangles are one-sided; set this when bodies are pushed into the ground instead of out. No effect on the box and sphere forms.</remarks>
    public bool InvertWinding
    {
        get => _invertWinding;
        set
        {
            _invertWinding = value;
            _component?.TryUpdateFeatures();
        }
    }

    /// <summary>Reads the samples on the outer faces of the grid as air, so the volume closes on itself.</summary>
    /// <remarks>
    /// On by default; a grid with a solid edge has no surface there otherwise. Turn it off when the
    /// field continues into a neighbouring chunk, or every chunk is walled off from the next.
    /// </remarks>
    public bool SealBorder
    {
        get => _sealBorder;
        set
        {
            if (_sealBorder == value)
                return;
            _sealBorder = value;
            _component?.TryUpdateFeatures();
        }
    }

    /// <summary>Radius of one sphere child, as a multiple of half a cell. Only the sphere form reads it.</summary>
    /// <remarks>
    /// At 1 neighbouring spheres are tangent and small fast bodies can slip between them along
    /// diagonals. Around 1.4 covers the face diagonals without bulging far out of the field.
    /// </remarks>
    public float SphereRadiusScale
    {
        get => _sphereRadiusScale;
        set
        {
            value.ValidateGreaterThanZeroFinite(this);
            if (_sphereRadiusScale == value)
                return;
            _sphereRadiusScale = value;
            _component?.TryUpdateFeatures();
        }
    }

    /// <summary>Mass used for the inertia of a dynamic body carrying this collider.</summary>
    /// <remarks>Inertia is approximated from the grid's bounding box, not from the occupied cells.</remarks>
    public float Mass
    {
        get => _mass;
        set
        {
            value.ValidateGreaterThanZeroFinite(this);
            _mass = value;
            _component?.TryUpdateFeatures();
        }
    }

    /// <summary>Produces the density source describing the current field.</summary>
    /// <remarks>Called on attach; the returned view must outlive the collidable, never a temporary.</remarks>
    /// <returns>False when there is no field yet, which leaves the collidable unattached.</returns>
    protected abstract bool TryGetSource(out TSource source);

    /// <summary>Tells anything holding geometry derived from this field that the field has changed.</summary>
    /// <remarks>
    /// Physics reads the samples live and does not need it; the physics debug view, which keeps a
    /// copy of the surface, does. Cheap: the shape slot is swapped, no tree is rebuilt.
    /// </remarks>
    public void NotifyFieldChanged() => InvalidateShape();

    /// <summary>Rebuilds the collidable after something other than a sample value changed.</summary>
    /// <remarks>Only needed when the grid is resized or replaced; sample edits are read live.</remarks>
    protected void InvalidateShape() => _component?.TryUpdateFeatures();

    private bool TryBuildGrid(out VoxelGridData<TSource> grid)
    {
        if (!TryGetSource(out var source))
        {
            grid = default;
            return false;
        }
        grid = new VoxelGridData<TSource>
        {
            Source = source,
            CellSize = _cellSize,
            IsoLevel = _isoLevel,
            InvertWinding = _invertWinding,
            SealBorder = _sealBorder,
            SphereRadiusScale = _sphereRadiusScale,
        };
        return true;
    }

    public int Transforms => 1;

    public void GetLocalTransforms(CollidableComponent collidable, Span<ShapeTransform> transforms)
    {
        transforms[0].PositionLocal = Vector3.Zero;
        transforms[0].RotationLocal = Quaternion.Identity;
        // The grid's own cell size is the only scale it has; entity scale is not applied.
        transforms[0].Scale = Vector3.One;
    }

    bool ICollider.TryAttach(Shapes shapes, BufferPool pool, ShapeCacheSystem shapeCache, bool shouldCalculateInertia, out TypedIndex index, out Vector3 centerOfMass, out BodyInertia inertia)
    {
        centerOfMass = Vector3.Zero;
        inertia = default;
        index = default;
        if (!TryBuildGrid(out var grid))
            return false;

        index = _form switch
        {
            VoxelChildForm.Box => shapes.Add(new VoxelBoxShape<TSource> { GridData = grid }),
            VoxelChildForm.Sphere => shapes.Add(new VoxelSphereShape<TSource> { GridData = grid }),
            VoxelChildForm.TriangleMarchingCubes => shapes.Add(new VoxelTriangleShape<TSource> { GridData = grid, SurfaceNets = false }),
            _ => shapes.Add(new VoxelTriangleShape<TSource> { GridData = grid, SurfaceNets = true }),
        };

        if (shouldCalculateInertia)
        {
            grid.ComputeLocalBounds(out var min, out var max);
            var extent = max - min;
            inertia = new Box(extent.X, extent.Y, extent.Z).ComputeInertia(_mass);
        }
        return true;
    }

    void ICollider.Detach(Shapes shapes, BufferPool pool, TypedIndex index)
    {
        // The shape holds a view of memory the collider owns, so removing it must not free anything
        // - the field survives, ready to be attached again.
        shapes.Remove(index);
    }

    void ICollider.RayTest<TRayHitHandler>(Shapes shapes, TypedIndex shapeIndex, in NRigidPose pose, in RayData ray, ref float maximumT, ref TRayHitHandler hitHandler, BufferPool pool)
    {
        if (shapeIndex.Type == VoxelBoxShape<TSource>.TypeId)
            shapes.GetShape<VoxelBoxShape<TSource>>(shapeIndex.Index).RayTest(pose, ray, ref maximumT, pool, ref hitHandler);
        else if (shapeIndex.Type == VoxelSphereShape<TSource>.TypeId)
            shapes.GetShape<VoxelSphereShape<TSource>>(shapeIndex.Index).RayTest(pose, ray, ref maximumT, pool, ref hitHandler);
        else if (shapeIndex.Type == VoxelTriangleShape<TSource>.TypeId)
            shapes.GetShape<VoxelTriangleShape<TSource>>(shapeIndex.Index).RayTest(pose, ray, ref maximumT, pool, ref hitHandler);
    }

    void ICollider.AppendModel(List<BasicMeshBuffers> buffer, ShapeCacheSystem shapeCache, out object? cacheOut)
    {
        cacheOut = null;
        if (!TryBuildGrid(out var grid))
            return;

        // Walking the whole field to build a mesh is exactly what this collider exists to avoid - so
        // it happens here and nowhere else, only when someone turns the physics debug view on.
        var vertices = new List<VertexPosition3>();
        var indices = new List<int>();
        switch (_form)
        {
            case VoxelChildForm.Box:
                AppendCellSolids(ref grid, vertices, indices, sphere: false);
                break;
            case VoxelChildForm.Sphere:
                AppendCellSolids(ref grid, vertices, indices, sphere: true);
                break;
            default:
                AppendSurface(ref grid, vertices, indices, surfaceNets: _form == VoxelChildForm.TriangleSurfaceNets);
                break;
        }

        buffer.Add(new BasicMeshBuffers { Vertices = vertices.ToArray(), Indices = indices.ToArray() });
    }

    /// <summary>A box per exposed solid cell, or an icosahedron at the sphere form's own radius.</summary>
    /// <remarks>Only cells with an empty neighbour are drawn; interior cells are never seen and would make the mesh huge.</remarks>
    private static void AppendCellSolids(ref VoxelGridData<TSource> grid, List<VertexPosition3> vertices, List<int> indices, bool sphere)
    {
        var half = grid.CellSize * 0.5f;
        var radius = grid.SphereRadius;
        for (int x = 0; x < grid.CellsX; ++x)
        {
            for (int y = 0; y < grid.CellsY; ++y)
            {
                for (int z = 0; z < grid.CellsZ; ++z)
                {
                    if (!grid.CellIsSolid(x, y, z))
                        continue;

                    var exposed =
                        !grid.CellIsSolid(x - 1, y, z) || !grid.CellIsSolid(x + 1, y, z) ||
                        !grid.CellIsSolid(x, y - 1, z) || !grid.CellIsSolid(x, y + 1, z) ||
                        !grid.CellIsSolid(x, y, z - 1) || !grid.CellIsSolid(x, y, z + 1);
                    if (!exposed)
                        continue;
                    var centre = grid.CellCentre(x, y, z);
                    var first = vertices.Count;
                    if (sphere)
                    {
                        foreach (var offset in IcosahedronVertices)
                            vertices.Add(new VertexPosition3(ToStride(centre + offset * radius)));
                        foreach (var i in IcosahedronIndices)
                            indices.Add(first + i);
                    }
                    else
                    {
                        for (int corner = 0; corner < 8; ++corner)
                        {
                            vertices.Add(new VertexPosition3(ToStride(centre + new NVector3(
                                (corner & 1) != 0 ? half : -half,
                                (corner & 2) != 0 ? half : -half,
                                (corner & 4) != 0 ? half : -half))));
                        }
                        foreach (var i in BoxIndices)
                            indices.Add(first + i);
                    }
                }
            }
        }
    }

    /// <summary>The iso-surface triangles, the same ones the narrow phase would generate.</summary>
    private static void AppendSurface(ref VoxelGridData<TSource> grid, List<VertexPosition3> vertices, List<int> indices, bool surfaceNets)
    {
        for (int x = 0; x < grid.CellsX; ++x)
        {
            for (int y = 0; y < grid.CellsY; ++y)
            {
                for (int z = 0; z < grid.CellsZ; ++z)
                {
                    var cubeIndex = surfaceNets ? 0 : grid.CubeIndex(x, y, z);
                    for (int slot = 0; slot < VoxelGridData<TSource>.MaxSurfaceNetsTrianglesPerCell; ++slot)
                    {
                        var found = surfaceNets
                            ? grid.TryGetSurfaceNetsTriangle(x, y, z, slot, out var triangle)
                            : grid.TryGetMarchingCubesTriangle(x, y, z, cubeIndex, slot, out triangle);
                        if (!found)
                            continue;
                        indices.Add(vertices.Count);
                        indices.Add(vertices.Count + 1);
                        indices.Add(vertices.Count + 2);
                        vertices.Add(new VertexPosition3(ToStride(triangle.A)));
                        vertices.Add(new VertexPosition3(ToStride(triangle.B)));
                        vertices.Add(new VertexPosition3(ToStride(triangle.C)));
                    }
                }
            }
        }
    }

    private static Vector3 ToStride(NVector3 value) => new(value.X, value.Y, value.Z);

    /// <summary>Corner order matches the bit pattern used above: bit 0 is +X, bit 1 +Y, bit 2 +Z.</summary>
    private static ReadOnlySpan<int> BoxIndices =>
    [
        0, 2, 1, 1, 2, 3, // -Z
        4, 5, 6, 5, 7, 6, // +Z
        0, 1, 4, 1, 5, 4, // -Y
        2, 6, 3, 3, 6, 7, // +Y
        0, 4, 2, 2, 4, 6, // -X
        1, 3, 5, 3, 7, 5, // +X
    ];

    /// <summary>A unit icosahedron, standing in for a sphere child.</summary>
    /// <remarks>Its faces sit at 0.79 of the radius, so tangent spheres are drawn touching.</remarks>
    private static readonly NVector3[] IcosahedronVertices = BuildIcosahedron();

    private static NVector3[] BuildIcosahedron()
    {
        // The twelve corners are the cyclic permutations of (0, +-1, +-phi), normalised.
        const float phi = 1.618034f;
        NVector3[] vertices =
        [
            new(-1, phi, 0), new(1, phi, 0), new(-1, -phi, 0), new(1, -phi, 0),
            new(0, -1, phi), new(0, 1, phi), new(0, -1, -phi), new(0, 1, -phi),
            new(phi, 0, -1), new(phi, 0, 1), new(-phi, 0, -1), new(-phi, 0, 1),
        ];
        for (int i = 0; i < vertices.Length; ++i)
            vertices[i] = NVector3.Normalize(vertices[i]);
        return vertices;
    }

    /// <summary>Wound the same way round as the box above; the debug view culls back faces.</summary>
    private static ReadOnlySpan<int> IcosahedronIndices =>
    [
        0, 5, 11, 0, 1, 5, 0, 7, 1, 0, 10, 7, 0, 11, 10,
        1, 9, 5, 5, 4, 11, 11, 2, 10, 10, 6, 7, 7, 8, 1,
        3, 4, 9, 3, 2, 4, 3, 6, 2, 3, 8, 6, 3, 9, 8,
        4, 5, 9, 2, 11, 4, 6, 10, 2, 8, 7, 6, 9, 1, 8,
    ];
}

/// <summary>
/// A <see cref="VoxelColliderBase{TSource}"/> over samples packed one per <see cref="ushort"/>,
/// density in bits 0-7 and material in bits 8-15.
/// </summary>
/// <remarks>
/// Owns a native copy of its samples, freed on <see cref="Dispose"/> or finalization. A game keeping
/// its field in unmanaged memory can derive from <see cref="VoxelColliderBase{TSource}"/> instead.
/// </remarks>
[DataContract]
public sealed unsafe class VoxelCollider : VoxelColliderBase<PackedVoxelSource>, IDisposable
{
    private ushort* _samples;
    private int _samplesX, _samplesY, _samplesZ;

    /// <summary>Samples along each axis. One more than the number of cells, per axis.</summary>
    [DataMemberIgnore]
    public Int3 SampleCount => new(_samplesX, _samplesY, _samplesZ);

    /// <summary>Cells along each axis.</summary>
    [DataMemberIgnore]
    public Int3 CellCount => _samples == null ? Int3.Zero : new(_samplesX - 1, _samplesY - 1, _samplesZ - 1);

    /// <summary>Whether a field has been supplied yet.</summary>
    [DataMemberIgnore]
    public bool HasData => _samples != null;

    /// <summary>Supplies the density field, laid out x-major with z varying fastest.</summary>
    /// <remarks>
    /// A grid of n cells per axis needs n+1 samples per axis. The data is copied into native memory
    /// owned by this collider, so the caller's array can be reused afterwards.
    /// </remarks>
    public void SetData(int samplesX, int samplesY, int samplesZ, ReadOnlySpan<ushort> samples)
    {
        if (samplesX < 2 || samplesY < 2 || samplesZ < 2)
            throw new ArgumentException("A voxel collider needs at least two samples per axis, which is one cell.");
        var count = samplesX * samplesY * samplesZ;
        if (samples.Length < count)
            throw new ArgumentException($"Expected at least {count} samples for a {samplesX}x{samplesY}x{samplesZ} grid, got {samples.Length}.", nameof(samples));

        var resized = _samples == null || count != _samplesX * _samplesY * _samplesZ;
        if (resized)
        {
            ReleaseData();
            _samples = (ushort*)NativeMemory.Alloc((nuint)count, sizeof(ushort));
        }
        _samplesX = samplesX;
        _samplesY = samplesY;
        _samplesZ = samplesZ;
        samples[..count].CopyTo(new Span<ushort>(_samples, count));
        // Only the layout can force a rebuild; sample values are read live.
        if (resized)
            InvalidateShape();
    }

    /// <summary>Overwrites one sample. Nothing is rebuilt; the narrow phase reads the field on demand.</summary>
    /// <remarks>Does not reattach the collidable. Call between simulation steps; a write racing the narrow phase is a data race.</remarks>
    public void SetVoxel(int x, int y, int z, ushort packedDensityAndMaterial)
        => _samples[SampleIndex(x, y, z)] = packedDensityAndMaterial;

    /// <summary>Reads one sample back, packed as <see cref="SetVoxel"/> takes it.</summary>
    public ushort GetVoxel(int x, int y, int z) => _samples[SampleIndex(x, y, z)];

    /// <summary>Frees the native sample buffer. Safe to call more than once.</summary>
    public void ReleaseData()
    {
        if (_samples == null)
            return;
        NativeMemory.Free(_samples);
        _samples = null;
        _samplesX = _samplesY = _samplesZ = 0;
    }

    public void Dispose()
    {
        ReleaseData();
        GC.SuppressFinalize(this);
    }

    ~VoxelCollider() => ReleaseData();

    protected override bool TryGetSource(out PackedVoxelSource source)
    {
        if (_samples == null)
        {
            source = default;
            return false;
        }
        source = new PackedVoxelSource
        {
            Samples = new Buffer<ushort>(_samples, _samplesX * _samplesY * _samplesZ),
            SamplesX = _samplesX,
            SamplesY = _samplesY,
            SamplesZ = _samplesZ,
        };
        return true;
    }

    private int SampleIndex(int x, int y, int z)
    {
        if (_samples == null)
            throw new InvalidOperationException($"This {nameof(VoxelCollider)} has no data yet; call {nameof(SetData)} first.");
        if ((uint)x >= (uint)_samplesX || (uint)y >= (uint)_samplesY || (uint)z >= (uint)_samplesZ)
            throw new ArgumentOutOfRangeException(nameof(x), $"({x}, {y}, {z}) is outside a {_samplesX}x{_samplesY}x{_samplesZ} sample grid.");
        return (x * _samplesY + y) * _samplesZ + z;
    }
}
