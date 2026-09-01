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
using NBuffer = BepuUtilities.Memory.Buffer<ushort>;
using NRigidPose = BepuPhysics.RigidPose;

namespace Stride.BepuPhysics.Definitions.Colliders;

/// <summary>
/// Collides against a voxel density field directly, generating contacts from the field as the
/// narrow phase asks for them instead of from a mesh built ahead of time.
/// </summary>
/// <remarks>
/// <para>
/// This is the collider for a game whose world is already voxels. The usual arrangement - mesh the
/// chunk, read the vertices back from the GPU, keep a CPU copy so a <see cref="MeshCollider"/> can
/// build a bounding volume tree over it, and redo all three whenever the terrain is edited - exists
/// only because Bepu needs triangles. Here the field is the collidable: there is no mesh, no
/// readback, no per-chunk tree, and no rebuild. Digging is <see cref="SetVoxel"/>, one store.
/// </para>
/// <para>
/// Pick what a cell presents to the narrow phase with <see cref="Form"/>. The triangle forms
/// reproduce the rendered iso-surface exactly, because they run the same marching-cubes table and
/// the same interpolation the renderer's compute shader runs, on the same samples.
/// </para>
/// <para>
/// One caveat worth knowing before choosing a triangle form. Bepu smooths away the bumps a
/// character feels crossing the internal edges of a triangle mesh with a MeshReduction, and that
/// machinery is bound to Bepu's concrete Mesh type in this version, so a voxel shape cannot use it
/// - Bepu's own voxel sample has the same limitation. Expect some catching on edges when sliding
/// fast across a triangle surface. <see cref="VoxelChildForm.TriangleSurfaceNets"/> suffers least
/// (far fewer, larger triangles than marching cubes), and <see cref="VoxelChildForm.Sphere"/> not
/// at all, at the cost of a rounded surface.
/// </para>
/// <para>
/// The sample buffer is native memory owned by this collider, so it outlives attach and detach
/// cycles and is freed when the collider is disposed or finalized. Writes are visible to the
/// simulation immediately; make them between steps, not during one.
/// </para>
/// </remarks>
[DataContract]
public sealed unsafe class VoxelCollider : ICollider, IDisposable
{
    private VoxelChildForm _form = VoxelChildForm.TriangleSurfaceNets;
    private float _cellSize = 1f;
    private float _isoLevel = 0.5f;
    private bool _invertWinding;
    private float _mass = 1f;

    private ushort* _samples;
    private int _samplesX, _samplesY, _samplesZ;

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

    /// <summary>
    /// Density at or above which a sample counts as solid, normalized to 0-1. Must match the value
    /// the renderer meshes with, or the collision surface will sit beside the visible one.
    /// </summary>
    public float IsoLevel
    {
        get => _isoLevel;
        set
        {
            _isoLevel = MathUtil.Clamp(value, 0f, 1f);
            _component?.TryUpdateFeatures();
        }
    }

    /// <summary>
    /// Reverses the winding of generated triangles.
    /// </summary>
    /// <remarks>
    /// Bepu triangles collide on one side only, so a field whose density runs the other way - or a
    /// renderer that emits the opposite winding - produces a surface that pushes bodies into the
    /// ground instead of out of it. This is the one-line correction for that; it has no effect on
    /// the box and sphere forms.
    /// </remarks>
    public bool InvertWinding
    {
        get => _invertWinding;
        set
        {
            _invertWinding = value;
            _component?.TryUpdateFeatures();
        }
    }

    /// <summary>
    /// Mass used for the inertia of a dynamic body carrying this collider.
    /// </summary>
    /// <remarks>
    /// Approximated from the grid's bounding box rather than from the occupied cells, which would
    /// mean walking the whole field. A voxel collidable is normally static terrain, where inertia is
    /// never read at all.
    /// </remarks>
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

    /// <summary>Samples along each axis. One more than the number of cells, per axis.</summary>
    [DataMemberIgnore]
    public Int3 SampleCount => new(_samplesX, _samplesY, _samplesZ);

    /// <summary>Cells along each axis.</summary>
    [DataMemberIgnore]
    public Int3 CellCount => _samples == null ? Int3.Zero : new(_samplesX - 1, _samplesY - 1, _samplesZ - 1);

    /// <summary>Whether a field has been supplied yet.</summary>
    [DataMemberIgnore]
    public bool HasData => _samples != null;

    /// <summary>
    /// Supplies the density field. Samples are packed one per <see cref="ushort"/>, density in bits
    /// 0-7 and material in bits 8-15, laid out x-major with z varying fastest - the layout a voxel
    /// game already uses to upload a chunk, so the same array serves both.
    /// </summary>
    /// <remarks>
    /// A grid of n cells per axis needs n+1 samples per axis: a cell reads the eight samples at its
    /// corners. The data is copied into native memory this collider owns, so the caller's array is
    /// free to move or be reused afterwards.
    /// </remarks>
    public void SetData(int samplesX, int samplesY, int samplesZ, ReadOnlySpan<ushort> samples)
    {
        if (samplesX < 2 || samplesY < 2 || samplesZ < 2)
            throw new ArgumentException("A voxel collider needs at least two samples per axis, which is one cell.");
        var count = samplesX * samplesY * samplesZ;
        if (samples.Length < count)
            throw new ArgumentException($"Expected at least {count} samples for a {samplesX}x{samplesY}x{samplesZ} grid, got {samples.Length}.", nameof(samples));

        if (_samples == null || count != _samplesX * _samplesY * _samplesZ)
        {
            ReleaseData();
            _samples = (ushort*)NativeMemory.Alloc((nuint)count, sizeof(ushort));
        }
        _samplesX = samplesX;
        _samplesY = samplesY;
        _samplesZ = samplesZ;
        samples[..count].CopyTo(new Span<ushort>(_samples, count));
        _component?.TryUpdateFeatures();
    }

    /// <summary>
    /// Overwrites one sample. This is the whole cost of a terrain edit: the narrow phase reads the
    /// field on demand, so nothing is rebuilt and the collidable's bounds do not change.
    /// </summary>
    /// <remarks>
    /// Deliberately does not reattach the collidable - that would defeat the purpose. Call it
    /// between simulation steps; a write racing the narrow phase is a data race like any other.
    /// </remarks>
    public void SetVoxel(int x, int y, int z, ushort packedDensityAndMaterial)
    {
        CheckHasData();
        _samples[SampleIndex(x, y, z)] = packedDensityAndMaterial;
    }

    /// <summary>Reads one sample back, packed as <see cref="SetVoxel"/> takes it.</summary>
    public ushort GetVoxel(int x, int y, int z)
    {
        CheckHasData();
        return _samples[SampleIndex(x, y, z)];
    }

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

    private int SampleIndex(int x, int y, int z)
    {
        if ((uint)x >= (uint)_samplesX || (uint)y >= (uint)_samplesY || (uint)z >= (uint)_samplesZ)
            throw new ArgumentOutOfRangeException(nameof(x), $"({x}, {y}, {z}) is outside a {_samplesX}x{_samplesY}x{_samplesZ} sample grid.");
        return (x * _samplesY + y) * _samplesZ + z;
    }

    private void CheckHasData()
    {
        if (_samples == null)
            throw new InvalidOperationException($"This {nameof(VoxelCollider)} has no data yet; call {nameof(SetData)} first.");
    }

    private VoxelGridData BuildGrid() => new()
    {
        Samples = new NBuffer(_samples, _samplesX * _samplesY * _samplesZ),
        SamplesX = _samplesX,
        SamplesY = _samplesY,
        SamplesZ = _samplesZ,
        CellSize = _cellSize,
        IsoLevel = _isoLevel,
        InvertWinding = _invertWinding,
    };

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
        if (_samples == null)
            return false;

        var grid = BuildGrid();
        index = _form switch
        {
            VoxelChildForm.Box => shapes.Add(new VoxelBoxShape { GridData = grid }),
            VoxelChildForm.Sphere => shapes.Add(new VoxelSphereShape { GridData = grid }),
            VoxelChildForm.TriangleMarchingCubes => shapes.Add(new VoxelTriangleShape { GridData = grid, SurfaceNets = false }),
            _ => shapes.Add(new VoxelTriangleShape { GridData = grid, SurfaceNets = true }),
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
        // The shape holds a view of memory this collider owns, so removing it must not free
        // anything - the field survives, ready to be attached again.
        shapes.Remove(index);
    }

    void ICollider.AppendModel(List<BasicMeshBuffers> buffer, ShapeCacheSystem shapeCache, out object? cacheOut)
    {
        // Nothing to hand the debug renderer: there is no mesh to draw, and generating one here
        // would rebuild exactly the thing this collider exists to avoid.
        cacheOut = null;
    }

    void ICollider.RayTest<TRayHitHandler>(Shapes shapes, TypedIndex shapeIndex, in NRigidPose pose, in RayData ray, ref float maximumT, ref TRayHitHandler hitHandler, BufferPool pool)
    {
        switch (shapeIndex.Type)
        {
            case VoxelBoxShape.Id:
                shapes.GetShape<VoxelBoxShape>(shapeIndex.Index).RayTest(pose, ray, ref maximumT, pool, ref hitHandler);
                break;
            case VoxelSphereShape.Id:
                shapes.GetShape<VoxelSphereShape>(shapeIndex.Index).RayTest(pose, ray, ref maximumT, pool, ref hitHandler);
                break;
            case VoxelTriangleShape.Id:
                shapes.GetShape<VoxelTriangleShape>(shapeIndex.Index).RayTest(pose, ray, ref maximumT, pool, ref hitHandler);
                break;
        }
    }
}
