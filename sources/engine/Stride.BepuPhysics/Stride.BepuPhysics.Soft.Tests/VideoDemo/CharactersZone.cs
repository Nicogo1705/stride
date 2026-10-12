// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using Stride.BepuPhysics.Definitions;
using Stride.Core.Mathematics;
using Stride.Engine;

namespace Stride.BepuPhysics.Soft.Tests.VideoDemo;

/// <summary>
/// The four <see cref="SkeletonBodyMode"/>s side by side, one lane each, in two zones mirroring each other: rigid humans in one,
/// soft characters in the other (a bean of soft flesh on physical bones, an all-soft Fall Guy on ghost bones).
/// In every lane one stands (the Standing ones wander under cannon fire), one hangs head down from a gallows by the ankle, one lies on the ground
/// </summary>
internal sealed class CharactersZone(bool soft) : Zone
{
    private const float LaneWidth = 4.5f, Spacing = 1.6f;
    // Fall Guy springs, scaled down from the default for a wobblier flesh
    private const float Softness = 0.7f;
    private static readonly Vector3 HumanFoot = new(0.11f, 0.06f, 0f), BeanFoot = new(0.12f, 0.06f, 0f);

    private readonly List<(SkeletonBodyMode Mode, Vector3 Lane)> _lanes = new();
    private readonly List<Walker> _standing = new();
    private readonly List<int> _trunks = new();
    private Cannon? _cannon;

    public bool Soft => soft;

    public override string Name => soft ? "Soft characters: every SkeletonBody mode" : "Characters: every SkeletonBody mode";
    public override string Description => soft
        ? "soft beans and Fall Guys per mode: standing, hanging by the ankle, lying down"
        : "rigid humans per mode: standing, hanging by the ankle, lying down";
    public override Vector3 Eye => Origin + new Vector3(0.5f, 4.6f, 12.5f);
    public override Vector3 Target => Origin + new Vector3(0f, 1.2f, 0f);
    public override IReadOnlyList<SkeletonBodyMode> LegendModes => Modes.All;
    public override string LegendCaption => "SkeletonBodyComponent.Mode (body color)";

    public override void Build(VideoDemoGame game, WorldTerrain terrain)
    {
        _lanes.Clear();
        _standing.Clear();
        _trunks.Clear();
        var device = game.GraphicsDevice;
        for (int i = 0; i < Modes.All.Length; i++)
        {
            var mode = Modes.All[i];
            var lane = Origin + new Vector3((i - 1.5f) * LaneWidth, 0f, 0f);
            _lanes.Add((mode, lane));
            // A strip in the mode's color under the lane
            Look.StaticBox(game, new Vector3(LaneWidth - 0.4f, 0.04f, 7f), lane + new Vector3(0f, 0.02f, 0.3f), Look.Paint(device, Color.Lerp(Modes.Of(mode), Look.Ivory, 0.55f), 0.3f), mode + " lane");
            var floor = new Vector3(0f, 0.04f, 0f);
            if (soft)
                BuildSoftLane(game, mode, lane + floor, i);
            else
                BuildRigidLane(game, mode, lane + floor);
        }

        // A cannon behind the Standing lane fires at its characters in turn
        _cannon = Cannon.Build(game, _standing, _lanes[^1].Lane + new Vector3(3.6f, 0.75f, -4.6f), 6f);
        _cannon.Active = () => Active;
    }

    private void BuildRigidLane(VideoDemoGame game, SkeletonBodyMode mode, Vector3 lane)
    {
        // Standing in the middle: walking in place, idle, or wandering for Standing
        var animator = mode switch
        {
            SkeletonBodyMode.Ragdoll => new ProceduralCharacter.Animator { Paused = true },
            SkeletonBodyMode.Active => new ProceduralCharacter.Animator { Idle = true, Time = 0.5f },
            _ => new ProceduralCharacter.Animator { Time = 0.41f },
        };
        var (human, body, _) = Humans.Add(game, mode, lane + new Vector3(0f, 0f, 0.3f), 0f, animator, mode + " human");
        if (mode == SkeletonBodyMode.Standing)
            AddWalker(human, new Walker(human, body, animator.StanceSpeed, 2, 0f, lane, 1.2f), 2);

        // Hanging head down from a gallows at the back
        var beam = Kit.AddGallows(game, lane + new Vector3(0f, 0f, -2.4f), 1.8f);
        Kit.Hang(game, beam, lane + new Vector3(0f, 0f, -2.4f), HumanFoot,
            (at, rotation) => Humans.Add(game, mode, at, 0f, new ProceduralCharacter.Animator { Paused = true }, "hanging " + mode + " human", rotation).Entity);

        // Lying on its back at the front, across the lane
        Humans.Add(game, mode, lane + new Vector3(0.9f, 0.16f, 2.6f), 0f, new ProceduralCharacter.Animator { Paused = true }, "lying " + mode + " human", Lying());
    }

    private void BuildSoftLane(VideoDemoGame game, SkeletonBodyMode mode, Vector3 lane, int index)
    {
        float time = mode == SkeletonBodyMode.Ragdoll ? 0f : 0.3f + 0.1f * index;
        // Mode colors: the bean (soft flesh on rigid bones) lighter, the all-soft Fall Guy darker
        var color = Modes.Light(mode);

        // Standing side by side: a bean and a Fall Guy, wandering for Standing
        var (bean, beanBody, beanAnimator) = Bean.Add(game, color, lane + new Vector3(-Spacing / 2f, 0f, 0.3f), 0f, mode, time);
        var (guy, guyBody, flesh, guyAnimator) = FallGuy.Add(game, mode, lane + new Vector3(Spacing / 2f, 0f, 0.3f), 0f, time, 7, Softness);
        if (mode == SkeletonBodyMode.Standing)
        {
            AddWalker(bean, new Walker(bean, beanBody, beanAnimator.StanceSpeed, Bean.TrunkBody, 0f, lane, 1.2f), Bean.TrunkBody);
            AddWalker(guy, new Walker(guy, guyBody, guyAnimator.StanceSpeed, Bean.TrunkBody, 3f, lane, 1.2f, flesh), Bean.TrunkBody);
        }

        // Both hanging head down from one gallows at the back, coarser flesh to keep the cost down
        var gallows = lane + new Vector3(0f, 0f, -2.4f);
        var beam = Kit.AddGallows(game, gallows, 2.4f);
        Kit.Hang(game, beam, gallows + new Vector3(-0.65f, 0f, 0f), BeanFoot, (at, rotation) => Bean.Add(game, color, at, 0f, mode, time, 5, rotation).Entity);
        Kit.Hang(game, beam, gallows + new Vector3(0.65f, 0f, 0f), BeanFoot, (at, rotation) => FallGuy.Add(game, mode, at, 0f, time, 5, Softness, rotation).Entity);

        // Both lying on their back at the front
        Bean.Add(game, color, lane + new Vector3(-0.4f, 0.36f, 2.6f), 0f, mode, time, 5, Lying());
        FallGuy.Add(game, mode, lane + new Vector3(1.6f, 0.36f, 2.6f), 0f, time, 5, Softness, Lying());
    }

    /// <summary> On its back, head toward -X: a quarter turn about X turns up into -Z, then a quarter turn about Y turns -Z into -X </summary>
    private static Quaternion Lying() => Quaternion.RotationX(-MathUtil.PiOverTwo) * Quaternion.RotationY(MathUtil.PiOverTwo);

    private void AddWalker(Entity entity, Walker walker, int trunk)
    {
        // Added after the entity joined the scene: its simulation is set here
        walker.SimulationSelector = Selector;
        entity.Add(walker);
        _standing.Add(walker);
        _trunks.Add(trunk);
    }

    public override IEnumerable<SceneLabel> Labels()
    {
        foreach (var (mode, lane) in _lanes)
            yield return new SceneLabel(lane + new Vector3(0f, 4.3f, -2.4f), mode.ToString(), Modes.Of(mode));
        if (_lanes.Count > 0)
            yield return new SceneLabel(_lanes[0].Lane + new Vector3(-LaneWidth * 0.5f + 0.4f, 2.4f, 1.6f), soft ? "bean  ·  Fall Guy" : "rigid humans");
    }

    public override IEnumerable<string> Status()
    {
        int up = _standing.Select((w, i) => Kit.Upright(w.Body, _trunks[i])).Count(standing => standing);
        yield return $"Standing: {up} / {_standing.Count} on their feet  |  per lane: standing, hanging by the ankle, lying  |  cannonballs fired: {_cannon?.Shots ?? 0} (one every 6 s)";
    }
}
