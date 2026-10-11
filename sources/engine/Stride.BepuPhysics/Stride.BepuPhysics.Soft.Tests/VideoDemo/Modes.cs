// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using Stride.BepuPhysics.Definitions;
using Stride.Core.Mathematics;
using Stride.Engine;

namespace Stride.BepuPhysics.Soft.Tests.VideoDemo;

/// <summary> The one color table of the <see cref="SkeletonBodyMode"/>s, used by every scene and its legend </summary>
internal static class Modes
{
    public static readonly SkeletonBodyMode[] All = { SkeletonBodyMode.Animated, SkeletonBodyMode.Ragdoll, SkeletonBodyMode.Active, SkeletonBodyMode.Standing };

    public static Color Of(SkeletonBodyMode mode) => mode switch
    {
        SkeletonBodyMode.Animated => new Color(61, 125, 216),
        SkeletonBodyMode.Ragdoll => new Color(232, 72, 72),
        SkeletonBodyMode.Active => new Color(40, 190, 150),
        _ => new Color(255, 178, 36),
    };

    public static string Describe(SkeletonBodyMode mode) => mode switch
    {
        SkeletonBodyMode.Animated => "Animated: kinematic hitboxes follow the animation",
        SkeletonBodyMode.Ragdoll => "Ragdoll: limp, jointed bodies",
        SkeletonBodyMode.Active => "Active: servos toward the animated pose",
        _ => "Standing: Active + keeps itself upright",
    };

    /// <summary> The legend at the top right: one swatch and one line per mode </summary>
    public static void DrawLegend(VideoDemoGame game, IReadOnlyList<SkeletonBodyMode> modes, string caption)
    {
        const int size = 16;
        const float row = 24f;
        float width = game.Measure(caption, size, true).X;
        foreach (var mode in modes)
            width = MathF.Max(width, game.Measure(Describe(mode), size).X + 30f);
        float x = game.Screen.X - width - 36f, y = 20f;
        game.Panel(new RectangleF(x - 14f, y - 10f, width + 28f, 32f + modes.Count * row), new Color(18, 20, 32, 190));
        game.Write(caption, new Vector2(x, y - 2f), Color.White, size, true);
        y += 26f;
        foreach (var mode in modes)
        {
            game.Panel(new RectangleF(x, y + 2f, 18f, 16f), Of(mode));
            game.Write(Describe(mode), new Vector2(x + 28f, y), Color.White, size);
            y += row;
        }
    }
}

/// <summary> Paints models in the color of the current mode of a skeleton, so a switch (a hit, a grab) shows at once </summary>
internal sealed class ModeTint(SkeletonBodyComponent body, IReadOnlyList<ModelComponent> models, float gloss = 0.5f) : SyncScript
{
    private SkeletonBodyMode? _shown;

    public override void Update()
    {
        if (_shown == body.Mode)
            return;
        _shown = body.Mode;
        var material = Look.Paint(Game.GraphicsDevice, Modes.Of(body.Mode), gloss);
        foreach (var model in models)
            model.Materials[0] = material;
    }
}
