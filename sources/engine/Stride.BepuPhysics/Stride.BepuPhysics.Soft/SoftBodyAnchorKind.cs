// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

namespace Stride.BepuPhysics.Soft;

/// <summary>
/// How the particles of a <see cref="SoftBodyPin"/> are tied to its <see cref="SoftBodyPin.Anchor"/>.
/// </summary>
public enum SoftBodyAnchorKind
{
    /// <summary>
    /// <see cref="Body"/> when the anchor entity carries a body, a soft body or a skeleton with bodies, <see cref="World"/> otherwise.
    /// </summary>
    Automatic,

    /// <summary>
    /// Each particle is joined to the nearest body of the anchor, or to the nearest particles of a soft body: they pull on each other.
    /// </summary>
    /// <remarks> Falls back to <see cref="World"/> when the anchor carries no body. </remarks>
    Body,

    /// <summary>
    /// Each particle is pulled toward a point following the anchor entity, or its node, without acting on it; without an anchor, the point stays where the particle was created.
    /// </summary>
    World,
}
