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
/// The four <see cref="SkeletonBodyMode"/>s side by side, one lane each: a human, a soft bean on physical bones and an all-soft Fall Guy on ghost bones.
/// The Ragdoll human hangs from a gallows by the ankle; the Standing ones wander their lane under cannon fire
/// </summary>
internal sealed class CharactersZone : Zone
{
    private const float LaneWidth = 4.5f, Spacing = 1.6f;
    private readonly List<(SkeletonBodyMode Mode, Vector3 Lane)> _lanes = new();
    private readonly List<Walker> _standing = new();
    private Cannon? _cannon;

    public override string Name => "Characters: every SkeletonBody mode";
    public override string Description => "a human, a soft bean and a Fall Guy per mode: Animated, Ragdoll, Active, Standing";
    public override Vector3 Eye => Origin + new Vector3(0.5f, 4.2f, 11.5f);
    public override Vector3 Target => Origin + new Vector3(0f, 1f, 0f);
    public override IReadOnlyList<SkeletonBodyMode> LegendModes => Modes.All;
    public override string LegendCaption => "SkeletonBodyComponent.Mode (body color)";

    public override void Build(VideoDemoGame game, WorldTerrain terrain)
    {
        _lanes.Clear();
        _standing.Clear();
        var device = game.GraphicsDevice;
        for (int i = 0; i < Modes.All.Length; i++)
        {
            var mode = Modes.All[i];
            var lane = Origin + new Vector3((i - 1.5f) * LaneWidth, 0f, 0f);
            _lanes.Add((mode, lane));
            // A strip in the mode's color under the lane
            Look.StaticBox(game, new Vector3(LaneWidth - 0.4f, 0.04f, 5f), lane + new Vector3(0f, 0.02f, 0f), Look.Paint(device, Color.Lerp(Modes.Of(mode), Look.Ivory, 0.55f), 0.3f), mode + " lane");

            float yaw = 0f;
            var human = lane + new Vector3(-Spacing, 0.04f, 0f);
            var bean = lane + new Vector3(0f, 0.04f, 0f);
            var fallGuy = lane + new Vector3(Spacing, 0.04f, 0f);
            switch (mode)
            {
                case SkeletonBodyMode.Animated:
                    // Walking in place: the kinematic hitboxes follow the animation
                    Humans.Add(game, mode, human, yaw, new ProceduralCharacter.Animator { Time = 0.2f }, "Animated human");
                    Bean.Add(game, Kit.FleshColors[0], bean, yaw, mode, 0.4f);
                    FallGuy.Add(game, mode, fallGuy, yaw, 0.7f);
                    break;
                case SkeletonBodyMode.Ragdoll:
                    // The gallows takes more room: everything shifts right a little
                    Kit.AddHangingRagdoll(game, lane + new Vector3(-1.2f, 0f, 0f));
                    Bean.Add(game, Kit.FleshColors[1], bean + new Vector3(0.4f, 0f, 0.4f), yaw, mode, 0f);
                    FallGuy.Add(game, mode, fallGuy + new Vector3(0.2f, 0f, 0.4f), yaw, 0f);
                    break;
                case SkeletonBodyMode.Active:
                    // Servos toward an idle pose, nothing keeps them upright
                    Humans.Add(game, mode, human, yaw, new ProceduralCharacter.Animator { Idle = true, Time = 0.5f }, "Active human");
                    Bean.Add(game, Kit.FleshColors[2], bean, yaw, mode, 0.3f);
                    FallGuy.Add(game, mode, fallGuy, yaw, 0.6f);
                    break;
                default:
                {
                    var animator = new ProceduralCharacter.Animator { Time = 0.41f };
                    var (character, body, _) = Humans.Add(game, mode, human, yaw, animator, "Standing human");
                    _standing.Add(AddWalker(character, new Walker(character, body, animator.StanceSpeed, 2, yaw, lane, 1.2f)));
                    var (beanEntity, beanBody, beanAnimator) = Bean.Add(game, Kit.FleshColors[3], bean, yaw, mode, 0.3f);
                    _standing.Add(AddWalker(beanEntity, new Walker(beanEntity, beanBody, beanAnimator.StanceSpeed, Bean.TrunkBody, yaw + 2f, lane, 1.2f)));
                    var (guy, guyBody, flesh, guyAnimator) = FallGuy.Add(game, mode, fallGuy, yaw, 0.6f);
                    _standing.Add(AddWalker(guy, new Walker(guy, guyBody, guyAnimator.StanceSpeed, Bean.TrunkBody, yaw + 4f, lane, 1.2f, flesh)));
                    break;
                }
            }
        }

        // A cannon behind the Standing lane fires at its three characters in turn
        _cannon = Cannon.Build(game, _standing, _lanes[^1].Lane + new Vector3(3.6f, 0.75f, -4.2f), 6f);
        _cannon.Active = () => Active;
    }

    private Walker AddWalker(Entity entity, Walker walker)
    {
        // Added after the entity joined the scene: its simulation is set here
        walker.SimulationSelector = Selector;
        entity.Add(walker);
        return walker;
    }

    public override IEnumerable<SceneLabel> Labels()
    {
        foreach (var (mode, lane) in _lanes)
            yield return new SceneLabel(lane + new Vector3(0f, 3.1f, -1.2f), mode.ToString(), Modes.Of(mode));
        if (_lanes.Count > 0)
            yield return new SceneLabel(_lanes[0].Lane + new Vector3(-LaneWidth * 0.5f + 0.4f, 2.4f, 1.6f), "human  ·  soft bean  ·  Fall Guy");
    }

    public override IEnumerable<string> Status()
    {
        int up = _standing.Count(w => Kit.Upright(w.Body, w.Body == _standing[0].Body ? 2 : Bean.TrunkBody));
        yield return $"Standing: {up} / {_standing.Count} on their feet  |  cannonballs fired: {_cannon?.Shots ?? 0} (one every 6 s)";
    }
}
