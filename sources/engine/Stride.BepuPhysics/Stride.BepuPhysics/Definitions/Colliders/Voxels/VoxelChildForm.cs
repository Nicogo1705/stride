// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core;

namespace Stride.BepuPhysics.Definitions.Colliders.Voxels;

/// <summary>
/// The shape each occupied cell of a <see cref="VoxelCollider"/> presents to the narrow phase.
/// </summary>
/// <remarks>Children are computed from the density field on demand; the choice affects the collision surface and the cost of a child test, not memory.</remarks>
[DataContract]
public enum VoxelChildForm
{
    /// <summary>One box per occupied cell, filling it exactly. Blocky, and the cheapest child test.</summary>
    /// <remarks>The collision surface is the cell grid and may sit up to half a cell off a marching-cubes visual.</remarks>
    Box,

    /// <summary>One sphere per occupied cell, inscribed in it. Rounds off corners, leaving gaps along cell diagonals.</summary>
    Sphere,

    /// <summary>The marching-cubes triangles of the cell, matching a marching-cubes renderer exactly.</summary>
    /// <remarks>Up to five triangles per cell, so up to five child tests where a box needs one.</remarks>
    TriangleMarchingCubes,

    /// <summary>The surface-nets quads of the cell, matching a surface-nets renderer.</summary>
    /// <remarks>
    /// The preferred triangle form: far fewer, larger triangles than <see cref="TriangleMarchingCubes"/>,
    /// so fewer internal edges to catch on (see <see cref="VoxelCollider"/>).
    /// </remarks>
    TriangleSurfaceNets,
}
