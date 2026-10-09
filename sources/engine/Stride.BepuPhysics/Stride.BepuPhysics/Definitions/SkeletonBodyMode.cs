// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

namespace Stride.BepuPhysics.Definitions;

/// <summary>
/// How the bodies of a <see cref="SkeletonBodyComponent"/> and the skeleton drive each other.
/// </summary>
public enum SkeletonBodyMode
{
    /// <summary>
    /// Kinematic bodies follow the animated nodes.
    /// </summary>
    Animated,

    /// <summary>
    /// Dynamic bodies held together by joints, the nodes follow the bodies.
    /// </summary>
    Ragdoll,

    /// <summary>
    /// Like <see cref="Ragdoll"/>, with angular servos pulling each joint toward the animated pose.
    /// </summary>
    Active,

    /// <summary>
    /// Like <see cref="Active"/>, with the root bodies also pulled upright and up to their animated height, so the character stands where it is.
    /// </summary>
    Standing,
}
