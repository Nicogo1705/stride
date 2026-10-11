// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core;
using Stride.Core.Annotations;
using Stride.Engine;
using Stride.Engine.Design;
using Stride.Input;

namespace Stride.BepuPhysics.Debug;

/// <summary>
/// Draws the colliders and the solver contacts of the Bepu simulations, toggled at runtime with <see cref="Key"/>.
/// </summary>
/// <remarks> Wireframes and contact lines are expanded by geometry shaders, so this needs <see cref="Stride.Graphics.GraphicsProfile.Level_10_0"/> or higher. </remarks>
[DataContract]
[DefaultEntityComponentProcessor(typeof(DebugRenderProcessor), ExecutionMode = ExecutionMode.Runtime)]
[ComponentCategory("Bepu - Debug")]
public class DebugRenderComponent : SyncScript
{
    /// <summary> The category of the properties choosing which collidables are drawn </summary>
    public const string CategoryFilters = "Filters";

    internal DebugRenderProcessor? _processor;
    bool _state = true;

    public Keys Key { get; set; } = Keys.F11;

    [DataMember]
    public bool Visible
    {
        get => _processor?.Visible ?? _state;
        set
        {
            _state = value;
            if (_processor is not null)
                _processor.Visible = value;
        }
    }

    /// <summary>
    /// Whether to draw the wireframe of each collider.
    /// </summary>
    [DataMember]
    public bool ShowShapes { get; set; } = true;

    /// <summary>
    /// Whether to draw the colliders of <see cref="StaticComponent"/>s.
    /// </summary>
    [DataMember]
    [Display(category: CategoryFilters)]
    public bool ShowStatics { get; set; } = true;

    /// <summary>
    /// Whether to draw the colliders of the bodies moved by the simulation.
    /// </summary>
    [DataMember]
    [Display(category: CategoryFilters)]
    public bool ShowDynamicBodies { get; set; } = true;

    /// <summary>
    /// Whether to draw the colliders of the bodies whose <see cref="BodyComponent.Kinematic"/> is set.
    /// </summary>
    [DataMember]
    [Display(category: CategoryFilters)]
    public bool ShowKinematicBodies { get; set; } = true;

    /// <summary>
    /// Whether to draw the colliders of characters.
    /// </summary>
    [DataMember]
    [Display(category: CategoryFilters)]
    public bool ShowCharacters { get; set; } = true;

    /// <summary>
    /// Whether to draw the particles of soft bodies.
    /// </summary>
    [DataMember]
    [Display(category: CategoryFilters)]
    public bool ShowSoftBodies { get; set; } = true;

    /// <summary>
    /// Whether to draw the colliders of the bodies the simulation put to sleep, on top of the filters above.
    /// </summary>
    [DataMember]
    [Display(category: CategoryFilters)]
    public bool ShowSleepingBodies { get; set; } = true;

    /// <summary>
    /// Whether to draw a marker at each contact point the solver is currently resolving.
    /// </summary>
    [DataMember]
    public bool ShowContactPoints { get; set; }

    /// <summary>
    /// Whether to draw the normal of each contact the solver is currently resolving.
    /// </summary>
    [DataMember]
    public bool ShowContactNormals { get; set; }

    /// <summary>
    /// The length of the contact normals, in world units; the point markers are scaled from it.
    /// </summary>
    [DataMember]
    public float ContactSize { get; set; } = 0.25f;

    /// <summary>
    /// The width of the wireframes and contact lines, in pixels.
    /// </summary>
    /// <remarks> Given to the graphics compositor's line and wireframe render features when the debug view starts and whenever it changes; a <c>LineWidth</c> set on them afterwards holds until then. </remarks>
    [DataMember]
    [DataMemberRange(0.5, 10, 0.5, 1, 1)]
    public float LineWidth { get; set; } = 3f;

    public override void Update()
    {
        if (Input.IsKeyPressed(Key))
        {
            Visible = !Visible;
        }
    }
}
