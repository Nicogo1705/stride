// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.ComponentModel;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Mathematics;

namespace Stride.BepuPhysics.Soft;

/// <summary>
/// Holds the particles of a <see cref="SoftBodyComponent"/> found in a region in place, or ties them to another entity.
/// </summary>
/// <remarks>
/// Pinned particles keep their mass and are pulled toward their anchor by a spring.
/// They never collide with the collidables of the anchor's entity.
/// </remarks>
[DataContract]
public sealed class SoftBodyPin
{
    private float _springFrequency = 30f;
    private float _springDampingRatio = 1f;
    private float _servoMaximumForce;

    /// <summary>
    /// The particles whose rest position is inside this box are pinned, in the space of the soft body's model.
    /// </summary>
    /// <remarks>
    /// The particles of a volumetric body sit a fraction of a cell inside its surface, so a thin box along a face may hold none.
    /// A box reduced to a point, like the default one, pins the particles closest to it instead;
    /// at the origin with an anchor, the particles closest to the anchor's entity, or to its node.
    /// </remarks>
    [DataMember(10)]
    public BoundingBox Region { get; set; }

    /// <summary>
    /// What the pinned particles are tied to, keeping the offset they had when the soft body was created; null holds them where they were created.
    /// </summary>
    [DataMember(20)]
    public SoftBodyAnchor? Anchor { get; set; }

    /// <summary>
    /// How fast the pinned particles return to their anchor, in hertz.
    /// </summary>
    /// <remarks> Values above half the rate of the solver's substeps cannot be honored. </remarks>
    [DataMember(30)]
    [DataMemberRange(0.001, 3)]
    [DefaultValue(30f)]
    public float SpringFrequency
    {
        get => _springFrequency;
        set => _springFrequency = MathF.Max(1e-3f, value);
    }

    /// <summary>
    /// The damping ratio of the pull toward the anchor, one stops oscillations the fastest without overshooting.
    /// </summary>
    [DataMember(40)]
    [DataMemberRange(0, 3)]
    [DefaultValue(1f)]
    public float SpringDampingRatio
    {
        get => _springDampingRatio;
        set => _springDampingRatio = MathF.Max(0f, value);
    }

    /// <summary>
    /// The largest force pulling each pinned particle toward its anchor; a stronger pull, a hit or a grab tears the particle away for as long as it lasts.
    /// Zero removes the limit.
    /// </summary>
    /// <remarks> Not used for particles tied to the particles of another soft body, which are held by springs without a limit. </remarks>
    [DataMember(50)]
    [DataMemberRange(0, 3)]
    [DefaultValue(0f)]
    public float ServoMaximumForce
    {
        get => _servoMaximumForce;
        set => _servoMaximumForce = MathF.Max(0f, value);
    }

    /// <summary> The servo force limit given to the solver </summary>
    internal float SolverMaximumForce => _servoMaximumForce > 0f ? _servoMaximumForce : float.MaxValue;
}
