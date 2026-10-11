// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.ComponentModel;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;

namespace Stride.BepuPhysics.Soft;

/// <summary>
/// Holds the particles of a <see cref="SoftBodyComponent"/> found in a region in place, or ties them to another entity.
/// </summary>
/// <remarks>
/// Pinned particles keep their mass and are pulled toward their anchor by a spring, see <see cref="AnchorKind"/>.
/// They never collide with the collidables of the <see cref="Anchor"/> entity.
/// </remarks>
[DataContract]
public sealed class SoftBodyPin
{
    private float _springFrequency = 30f;
    private float _springDampingRatio = 1f;
    private float _servoMaximumForce = float.MaxValue;

    /// <summary>
    /// The particles whose rest position is inside this box are pinned, in the space of the soft body's model.
    /// </summary>
    public BoundingBox Region { get; set; }

    /// <summary>
    /// The entity the pinned particles are tied to, keeping the offset they had when the soft body was created. Null holds them where they were created.
    /// </summary>
    public Entity? Anchor { get; set; }

    /// <summary>
    /// The name of a node of the <see cref="Anchor"/>'s model, such as a bone, the pinned particles follow instead of the entity itself.
    /// </summary>
    /// <remarks> Null or empty follows the entity; a name the model does not have is reported once and the entity is followed instead. </remarks>
    public string? AnchorNode { get; set; }

    /// <summary>
    /// Whether the pinned particles and the <see cref="Anchor"/> pull on each other, or the particles follow the anchor without acting on it.
    /// </summary>
    [DefaultValue(SoftBodyAnchorKind.Automatic)]
    public SoftBodyAnchorKind AnchorKind { get; set; }

    /// <summary>
    /// How fast the pinned particles return to their anchor, in hertz.
    /// </summary>
    /// <remarks> Values above half the rate of the solver's substeps cannot be honored. </remarks>
    [DefaultValue(30f)]
    public float SpringFrequency
    {
        get => _springFrequency;
        set => _springFrequency = MathF.Max(1e-3f, value);
    }

    /// <summary>
    /// The damping ratio of the pull toward the anchor, one stops oscillations the fastest without overshooting.
    /// </summary>
    [DefaultValue(1f)]
    public float SpringDampingRatio
    {
        get => _springDampingRatio;
        set => _springDampingRatio = MathF.Max(0f, value);
    }

    /// <summary>
    /// The largest force pulling each pinned particle toward its anchor; a stronger pull, a hit or a grab tears the particle away for as long as it lasts.
    /// </summary>
    /// <remarks> Not used for particles tied to the particles of another soft body, which are held by springs without a limit. </remarks>
    [DefaultValue(float.MaxValue)]
    public float ServoMaximumForce
    {
        get => _servoMaximumForce;
        set => _servoMaximumForce = MathF.Max(0f, value);
    }
}
