// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core;
using Stride.Core.Annotations;

namespace Stride.BepuPhysics.Definitions.Colliders;

/// <summary>
/// A shape attached to a node of a skeleton, one entry of a <see cref="BoneCollider"/>.
/// </summary>
/// <remarks>
/// The shape's <see cref="ColliderBase.PositionLocal"/> and <see cref="ColliderBase.RotationLocal"/> are expressed in the space of the node,
/// <see cref="ColliderBase.Mass"/> is only used by bodies which are not kinematic.
/// </remarks>
[DataContract]
public sealed class BoneShape
{
    /// <summary>
    /// The name of the skeleton node this shape follows.
    /// </summary>
    [DataMember(10)]
    public string NodeName { get; set; } = "";

    /// <summary>
    /// The shape following the node: a <see cref="BoxCollider"/>, <see cref="SphereCollider"/>, <see cref="CapsuleCollider"/> or <see cref="CylinderCollider"/>.
    /// </summary>
    [DataMember(20)]
    [NotNull]
    public ColliderBase Shape { get; set; } = new CapsuleCollider();
}
