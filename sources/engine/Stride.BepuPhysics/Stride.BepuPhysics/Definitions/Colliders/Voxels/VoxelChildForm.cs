// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core;

namespace Stride.BepuPhysics.Definitions.Colliders.Voxels;

/// <summary>
/// The shape each occupied cell of a <see cref="VoxelCollider"/> presents to the narrow phase.
/// </summary>
/// <remarks>
/// Nothing is stored per child whichever form is picked - the child is computed from the density
/// field on demand, every time the narrow phase asks for it. The choice is therefore about the
/// shape of the collision surface and the cost of a single child test, not about memory.
/// </remarks>
[DataContract]
public enum VoxelChildForm
{
    /// <summary>
    /// One box per occupied cell, filling it exactly. Blocky, and the cheapest child test.
    /// </summary>
    /// <remarks>
    /// The collision surface is the cell grid, not the rendered iso-surface, so it disagrees with
    /// a marching-cubes visual by up to half a cell. Fine when that is below the character radius.
    /// </remarks>
    Box,

    /// <summary>
    /// One sphere per occupied cell, inscribed in it. Rounds off cell corners, which stops
    /// characters catching on the lattice at the price of gaps along cell diagonals.
    /// </summary>
    Sphere,

    /// <summary>
    /// The marching-cubes triangles of the cell, computed from the same density field and the same
    /// table the renderer uses. The collision surface matches the rendered one exactly.
    /// </summary>
    /// <remarks>
    /// Up to five triangles per cell, so up to five child tests where a box needs one.
    /// </remarks>
    TriangleMarchingCubes,

    /// <summary>
    /// The surface-nets quads of the cell: one vertex per straddling cell, joined across every
    /// sign-changing edge. Matches a surface-nets renderer, and produces far fewer, larger
    /// triangles than <see cref="TriangleMarchingCubes"/> for the same field.
    /// </summary>
    /// <remarks>
    /// The preferred triangle form. Fewer triangles means fewer internal edges, which matters
    /// because Bepu's boundary smoothing is not available here - see the remarks on
    /// <see cref="VoxelCollider"/>.
    /// </remarks>
    TriangleSurfaceNets,
}
