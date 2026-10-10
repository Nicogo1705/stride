// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BepuPhysics.CollisionDetection;
using Stride.BepuPhysics;
using Stride.BepuPhysics.Components;
using Stride.BepuPhysics.Definitions;
using Stride.BepuPhysics.Definitions.Colliders;
using Stride.BepuPhysics.Definitions.Contacts;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Input;
using Stride.Rendering;
using Stride.Rendering.ProceduralModels;

namespace Stride.Harness.Demo;

/// <summary> Scenes that only use rigid bodies, so they build against any version of Stride.BepuPhysics </summary>
public static class RigidScenes
{
    public static IEnumerable<DemoScene> All()
    {
        yield return new RigidPile(1500, "Bench", "Tas rigide du bench du cœur (1 500 corps)",
            "le temps par pas ; mêmes corps, même graine et mêmes handlers que ContactsBenchmark (T : 1 thread)");
        yield return new CompoundOnFloor();
        yield return new RigidPile(60, "Contrôle", "Petit tas rigide (60 corps)",
            "rien de particulier : la physique rigide se comporte comme d'habitude");
    }

    /// <summary> The menu, then the rigid pile in Bepu's wireframe view, near and from inside the pile </summary>
    public static async System.Threading.Tasks.Task Tour(BepuDemoGame game, string prefix, int first = 1)
    {
        var folder = DemoSettings.TourFolder;
        await game.Press(Keys.Down);
        await game.CaptureTo(Path.Combine(folder, $"{first:00}-{prefix}menu"));

        game.LoadScene(game.Scenes.First(s => s.Name.StartsWith("Tas rigide du bench", StringComparison.Ordinal)));
        await game.Steps(240);
        await game.Press(Keys.V);
        game.SetCamera(new Vector3(9f, 7f, 11f), new Vector3(0f, 2f, 0f));
        await game.Frames(5);
        await game.CaptureTo(Path.Combine(folder, $"{first + 1:00}-{prefix}tas-rigide-wireframe-proche"));
        // In the top layer of the pile: the nearest faces cross the camera plane
        game.SetCamera(new Vector3(0f, 4.6f, 0f), new Vector3(5f, 3.6f, 5f));
        await game.Frames(5);
        await game.CaptureTo(Path.Combine(folder, $"{first + 2:00}-{prefix}tas-rigide-wireframe-camera-dans-le-tas"));
        await game.Press(Keys.V);
    }

    /// <summary> The pile of ContactsBenchmark: a pit, boxes, spheres and crosses, half with a contact handler, layer 1 not touching itself </summary>
    private sealed class RigidPile(int count, string group, string name, string description) : BepuDemoScene
    {
        private EventCounter _events = new();

        public override string Group => group;
        public override string Name => name;
        public override string Description => description;
        public override Vector3 Eye => new(14f, 12f, 18f);
        public override Vector3 Target => new(0f, 2f, 0f);

        public override void Build(DemoGame game)
        {
            AddFloor(game, new Vector3(30f, 1f, 30f), new Color(0.55f, 0.55f, 0.52f));
            AddWall(game, new Vector3(1f, 20f, 16f), new Vector3(-8.5f, 10f, 0f));
            AddWall(game, new Vector3(1f, 20f, 16f), new Vector3(8.5f, 10f, 0f));
            AddWall(game, new Vector3(16f, 20f, 1f), new Vector3(0f, 10f, -8.5f));
            AddWall(game, new Vector3(16f, 20f, 1f), new Vector3(0f, 10f, 8.5f));

            var models = new[]
            {
                game.Shape(new CubeProceduralModel { Size = new Vector3(0.8f) }, Color.OrangeRed),
                game.Shape(new SphereProceduralModel { Radius = 0.45f, Tessellation = 16 }, Color.CornflowerBlue),
                game.Shape(new CubeProceduralModel { Size = new Vector3(0.9f, 0.3f, 0.3f) }, Color.Gold),
            };
            var cross = game.Shape(new CubeProceduralModel { Size = new Vector3(0.3f, 0.9f, 0.3f) }, Color.Gold);

            _events = new EventCounter();
            var random = new Random(1);
            for (int i = 0; i < count; i++)
            {
                var collider = new CompoundCollider();
                switch (i % 3)
                {
                    case 0: collider.Colliders.Add(new BoxCollider { Size = new Vector3(0.8f) }); break;
                    case 1: collider.Colliders.Add(new SphereCollider { Radius = 0.45f }); break;
                    default:
                        collider.Colliders.Add(new BoxCollider { Size = new Vector3(0.9f, 0.3f, 0.3f) });
                        collider.Colliders.Add(new BoxCollider { Size = new Vector3(0.3f, 0.9f, 0.3f) });
                        break;
                }

                var body = new BodyComponent
                {
                    Collider = collider,
                    SleepThreshold = -1f,
                    CollisionLayer = i % 2 == 0 ? CollisionLayer.Layer0 : CollisionLayer.Layer1,
                    ContactEventHandler = i % 2 == 0 ? _events : null,
                };
                var entity = new Entity { new ModelComponent(models[i % 3]), body };
                if (i % 3 == 2)
                    entity.AddChild(new Entity { new ModelComponent(cross) });
                entity.Transform.Position = new Vector3(-7f + (i % 15), 1f + i / 225 * 1.1f, -7f + (i / 15 % 15)) + new Vector3((float)random.NextDouble() * 0.2f, 0f, (float)random.NextDouble() * 0.2f);
                game.AddToScene(entity);
            }
            game.AddToScene(new Entity { _events });
        }

        public override void OnSimulationReady(BepuSimulation simulation) => simulation.CollisionMatrix.Set(CollisionLayer.Layer1, CollisionLayer.Layer1, false);

        public override IEnumerable<string> Status()
        {
            yield return string.Format(CultureInfo.InvariantCulture, "{0} corps  |  appels de OnTouching par pas : {1:0.0}", count, _events.PerStep);
        }

        private static void AddWall(DemoGame game, Vector3 size, Vector3 position)
        {
            var wall = new Entity { new StaticComponent { Collider = new CompoundCollider { Colliders = { new BoxCollider { Size = size } } } } };
            wall.Transform.Position = position;
            game.AddToScene(wall);
        }
    }

    /// <summary> One compound of two children resting on the floor: how many touching events a single pair raises per step </summary>
    private sealed class CompoundOnFloor : BepuDemoScene
    {
        private EventCounter _events = new();

        public override string Group => "Cas limites";
        public override string Name => "Un compound à deux enfants posé au sol";
        public override string Description => "le nombre d'événements par pas pour UNE paire de composants (à comparer avant / après)";
        public override Vector3 Eye => new(3f, 2f, 4f);
        public override Vector3 Target => new(0f, 0.4f, 0f);

        public override void Build(DemoGame game)
        {
            AddFloor(game, new Vector3(20f, 1f, 20f), new Color(0.55f, 0.55f, 0.52f));
            _events = new EventCounter();
            var body = new BodyComponent
            {
                Collider = new CompoundCollider
                {
                    Colliders =
                    {
                        new BoxCollider { Size = new Vector3(1.2f, 0.3f, 0.3f), PositionLocal = new Vector3(0f, 0f, -0.3f) },
                        new BoxCollider { Size = new Vector3(1.2f, 0.3f, 0.3f), PositionLocal = new Vector3(0f, 0f, 0.3f) },
                    },
                },
                ContactEventHandler = _events,
            };
            var entity = new Entity { body };
            entity.AddChild(Child(game, new Vector3(0f, 0f, -0.3f)));
            entity.AddChild(Child(game, new Vector3(0f, 0f, 0.3f)));
            entity.Transform.Position = new Vector3(0f, 0.6f, 0f);
            game.AddToScene(entity);
            game.AddToScene(new Entity { _events });
        }

        public override IEnumerable<string> Status()
        {
            yield return string.Format(CultureInfo.InvariantCulture, "appels de OnTouching par pas pour cette paire : {0:0.0}", _events.PerStep);
        }

        private static Entity Child(DemoGame game, Vector3 position)
        {
            var child = new Entity { new ModelComponent(game.Shape(new CubeProceduralModel { Size = new Vector3(1.2f, 0.3f, 0.3f) }, Color.MediumSeaGreen)) };
            child.Transform.Position = position;
            return child;
        }
    }

    /// <summary> Reads every contact as a gameplay handler would, and counts the calls per step over the last second </summary>
    private sealed class EventCounter : ScriptComponent, IContactHandler, ISimulationUpdate
    {
        private int _calls, _steps;
        public float Sink;

        public double PerStep { get; private set; }

        public bool NoContactResponse => false;

        public void OnTouching<TManifold>(Contacts<TManifold> contacts) where TManifold : unmanaged, IContactManifold<TManifold>
        {
            _calls++;
            foreach (var contact in contacts)
                Sink += contact.Depth + contact.Normal.Y + contact.Point.X;
        }

        public void SimulationUpdate(BepuSimulation simulation, float simTimeStep) { }

        public void AfterSimulationUpdate(BepuSimulation simulation, float simTimeStep)
        {
            if (++_steps < 60)
                return;
            PerStep = _calls / (double)_steps;
            _calls = _steps = 0;
        }
    }
}
