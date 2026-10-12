// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using Stride.BepuPhysics.Components;
using Stride.BepuPhysics.Definitions;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Harness.Demo;

namespace Stride.BepuPhysics.Soft.Tests.VideoDemo;

/// <summary>
/// The whole showcase in one world: zones on flat pads of a voxel terrain (<see cref="VoxelCollider"/>), linked by paths over hills.
/// Each zone has its own simulation, stepped only while the camera is near it; the others stay drawn, frozen.
/// </summary>
internal sealed class WorldScene : VideoScene
{
    /// <summary> A zone simulates while the camera is within this distance of its middle, on the ground plane, or when it is the nearest </summary>
    public const float ActiveRadius = 20f;

    private readonly Zone[] _zones =
    {
        new PlaygroundZone(),
        new CharactersZone(soft: false),
        new GardenZone(),
        new CharactersZone(soft: true),
        new TortureZone(),
        new RopesZone(),
        new StressZone(),
    };

    private static readonly Vector2[] Pads = { new(0f, 0f), new(0f, -45f), new(-40f, -35f), new(40f, -35f), new(-45f, 10f), new(45f, 10f), new(0f, 45f) };

    public static readonly Vector3 OverviewEye = new(0f, 70f, 120f), OverviewTarget = new(0f, 0f, -10f);

    // The simulations made for the zones, kept from one load of the scene to the next
    private static readonly HashSet<BepuSimulation> s_created = new();
    private Zone? _building;
    private Vector3 _camera;

    public WorldTerrain? Terrain { get; private set; }

    public IReadOnlyList<Zone> Zones => _zones;

    /// <summary> Every zone simulates, wherever the camera is: for measures </summary>
    public bool ActivateAll { get; set; }

    /// <summary> No zone simulates: a frozen instant for captures </summary>
    public bool Frozen { get; set; }

    /// <summary> Only this zone simulates, wherever the camera is: to time one zone alone </summary>
    public Zone? Solo { get; set; }

    public override string Title => "Soft bodies, ragdolls and a voxel world";
    public override string Pr => "PR #3528, #3471, #3530 and upcoming";
    public override string Description => "zones on a voxel terrain: Esc lists them; the ones near the camera simulate";
    public override Vector3 Eye => _zones[0].Eye;
    public override Vector3 Target => _zones[0].Target;

    /// <summary> The zone nearest to the camera </summary>
    public Zone Current => Nearest(_camera);

    public override IReadOnlyList<SkeletonBodyMode> LegendModes => Current.LegendModes;

    public override string LegendCaption => Current.LegendCaption;

    public Zone Nearest(Vector3 position) => _zones.MinBy(zone => Flat(zone.Origin - position))!;

    private static float Flat(Vector3 offset) => new Vector2(offset.X, offset.Z).Length();

    protected override void BuildScene(VideoDemoGame game)
    {
        game.World = this;
        (ActivateAll, Frozen, Solo) = (false, false, null);
        _camera = _zones[0].Eye;

        // One simulation per zone: the configuration's first one, then copies of it made here. The test project's game settings carry two more
        // simulations for the Bepu tests (one with gravity -30 and a single substep, one without per-body attributes): no zone uses them
        var simulations = game.Services.GetOrCreate<BepuConfiguration>().BepuSimulations;
        var first = simulations[0];
        var zoneSimulations = new List<BepuSimulation> { first };
        zoneSimulations.AddRange(simulations.Where(s_created.Contains));
        while (zoneSimulations.Count < _zones.Length)
        {
            var copy = new BepuSimulation
            {
                SolverSubStep = first.SolverSubStep,
                SolverIteration = first.SolverIteration,
                FixedTimeStepSeconds = first.FixedTimeStepSeconds,
                MaxStepPerFrame = first.MaxStepPerFrame,
                PoseGravity = first.PoseGravity,
                PoseLinearDamping = first.PoseLinearDamping,
                PoseAngularDamping = first.PoseAngularDamping,
            };
            s_created.Add(copy);
            simulations.Add(copy);
            zoneSimulations.Add(copy);
        }
        foreach (var unused in simulations.Except(zoneSimulations))
            unused.Enabled = false;
        // More substeps for the first two seconds of every zone, from the load of the world: everything built at once settles before the normal solver takes over
        foreach (var simulation in zoneSimulations)
        {
            simulation.SoftStartDuration = TimeSpan.FromSeconds(2);
            simulation.ResetSoftStart();
        }

        var pads = Pads;
        Terrain = new WorldTerrain(game, pads);
        Terrain.AddTo(game);
        for (int i = 0; i < _zones.Length; i++)
        {
            var zone = _zones[i];
            zone.Simulation = zoneSimulations[i];
            zone.Place(new Vector3(pads[i].X, 0f, pads[i].Y), simulations.IndexOf(zone.Simulation));
            _building = zone;
            try
            {
                game.AddToScene(Terrain.Collider());
                zone.Build(game, Terrain);
            }
            finally
            {
                _building = null;
            }
            game.AddPlace(zone.Name, zone.Description, zone.Eye, zone.Target);
        }
        game.AddPlace("Overview", "the whole world from above", OverviewEye, OverviewTarget);

        // Edit the ground where the crosshair meets it: every simulation reads the same field, so it holds everywhere at once
        int dug = 0, added = 0;
        game.AddOption("Dig at the crosshair", () => $"{dug} dug", () => { if (EditAtCrosshair(game, false)) dug++; });
        game.AddOption("Add ground at the crosshair", () => $"{added} added", () => { if (EditAtCrosshair(game, true)) added++; });
    }

    private bool EditAtCrosshair(VideoDemoGame game, bool add)
    {
        if (Terrain is null || Current.Simulation is not { } simulation)
            return false;
        var (origin, forward) = game.CrosshairRay;
        if (!simulation.RayCast(origin, forward, 200f, out var hit) || !Terrain.IsGround(hit.Collidable))
            return false;
        if (add)
            Terrain.Fill(hit.Point + hit.Normal * 0.4f, 1.3f);
        else
            Terrain.Carve(hit.Point, 1.4f);
        return true;
    }

    /// <summary> Puts an entity in the simulation of the zone being built, or of the zone it appears in </summary>
    public void Assign(Entity entity)
    {
        if (HasSelector(entity))
            return;
        var zone = _building ?? Nearest(entity.Transform.Position);
        BepuDemoGame.AssignSimulation(entity, zone.Selector);
    }

    private static bool HasSelector(Entity entity)
    {
        foreach (var component in entity.Components)
        {
            if (component is CollidableComponent { SimulationSelector: not SceneBasedSimulationSelector })
                return true;
            if (component is ISimulationUpdate { SimulationSelector: not SceneBasedSimulationSelector })
                return true;
        }
        return entity.Transform.Children.Any(child => HasSelector(child.Entity));
    }

    /// <summary> Steps the zones near the camera, freezes the others, and runs the timers of the active ones </summary>
    public void UpdateZones(VideoDemoGame game, float seconds)
    {
        _camera = game.CameraPosition;
        var nearest = Current;
        foreach (var zone in _zones)
        {
            if (zone.Simulation is not { } simulation)
                continue;
            simulation.Enabled = !Frozen && (Solo is not null ? zone == Solo : ActivateAll || zone == nearest || Flat(zone.Origin - _camera) < ActiveRadius);
            if (simulation.Enabled)
                zone.Update(game, seconds);
        }
    }

    public int ActiveZones => _zones.Count(z => z.Active);

    public override IEnumerable<SceneLabel> Labels()
    {
        foreach (var zone in _zones)
        {
            float distance = Flat(zone.Origin - _camera);
            if (distance > ActiveRadius)
            {
                yield return new SceneLabel(zone.Origin + new Vector3(0f, 7f, 0f), zone.Name);
                continue;
            }
            foreach (var label in zone.Labels())
            {
                if (Vector3.Distance(label.World, _camera) < 30f)
                    yield return label;
            }
        }
    }

    public override IEnumerable<string> Status()
    {
        var zone = Current;
        yield return $"{zone.Name}  |  zones simulating: {ActiveZones} / {_zones.Length}{(ActivateAll ? " (all)" : string.Empty)}  |  {Terrain?.Vertices ?? 0} terrain vertices, {Terrain?.Craters ?? 0} craters";
        foreach (var line in zone.Status())
            yield return line;
    }
}
