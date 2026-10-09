// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.ComponentModel;
using Stride.Core;

namespace Stride.BepuPhysics.Definitions;

/// <summary>
/// The limits of the joint between the shapes of a node and those of its closest ancestor with shapes, used by <see cref="SkeletonBodyComponent"/> outside of <see cref="SkeletonBodyMode.Animated"/>.
/// </summary>
/// <remarks> Angles are in radians, measured from the rest pose of the skeleton around the axis going from the node to the center of its first shape. </remarks>
[DataContract]
public sealed class BoneJoint
{
    /// <summary>
    /// The name of the child node of the joint, a node with at least one bone shape.
    /// </summary>
    [DataMember(10)]
    public string NodeName { get; set; } = "";

    /// <summary>
    /// The largest angle the axis of the child may swing away from its rest direction.
    /// </summary>
    /// <userdoc> In radians </userdoc>
    [DataMember(20)]
    [DefaultValue(MathF.PI / 4f)]
    public float MaximumSwingAngle { get; set; } = MathF.PI / 4f;

    /// <summary>
    /// The smallest twist angle around the axis of the child.
    /// </summary>
    /// <userdoc> In radians </userdoc>
    [DataMember(30)]
    [DefaultValue(-MathF.PI / 6f)]
    public float MinimumTwistAngle { get; set; } = -MathF.PI / 6f;

    /// <summary>
    /// The largest twist angle around the axis of the child.
    /// </summary>
    /// <userdoc> In radians </userdoc>
    [DataMember(40)]
    [DefaultValue(MathF.PI / 6f)]
    public float MaximumTwistAngle { get; set; } = MathF.PI / 6f;
}
