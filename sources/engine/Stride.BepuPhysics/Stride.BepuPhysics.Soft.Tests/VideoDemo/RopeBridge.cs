// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using Stride.BepuPhysics.Definitions.Colliders;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics.GeometricPrimitives;
using Stride.Rendering.ProceduralModels;

namespace Stride.BepuPhysics.Soft.Tests.VideoDemo;

/// <summary>
/// A rope bridge between two landings: a cloth deck pinned at both ends, a wooden plank pinned across it every few decimeters
/// (each plank a body anchor of a band of the cloth), two hand ropes. Something drops on the middle of the deck every few seconds,
/// and a wrecking ball hanging from a gantry above the middle is swung across it
/// </summary>
internal sealed class RopeBridge
{
    public const float Span = 12f, Height = 3f, Width = 1.3f;
    private const int Planks = 21, MaxTraffic = 6;
    private const float GantryHeight = 8.5f, BallRadius = 0.45f, RopeLength = 4.2f;
    private const float SendPeriod = 2.5f;
    // The deck and its planks settle before anything is sent onto them
    private const float Settle = 8f;
    // Off for now: swung across the loaded deck, it tears it
    private static readonly bool WithWreckingBall = false;

    private readonly Vector3 _center;
    private readonly Queue<Entity> _traffic = new();
    private readonly BodyComponent? _ball;
    private readonly List<BodyComponent> _planks = new();
    private int _sent, _swings;

    /// <summary> Middle of the deck </summary>
    public Vector3 Deck => _center + new Vector3(0f, Height, 0f);

    public int Sent => _sent;

    public int Swings => _swings;

    public bool HasWreckingBall => _ball is not null;

    /// <summary> How far the lowest plank hangs under the landings </summary>
    public float Sag => _planks.Count == 0 ? 0f : Deck.Y - _planks.Min(p => p.Position.Y);

    public RopeBridge(VideoDemoGame game, Vector3 center)
    {
        _center = center;
        var device = game.GraphicsDevice;
        var navy = Look.Paint(device, Look.Navy, 0.5f);
        var wood = Look.Paint(device, Look.Wood, 0.45f);
        float half = Span / 2f;

        // A landing and a tower of two posts at each end
        foreach (var side in new[] { -1f, 1f })
        {
            Look.StaticBox(game, new Vector3(2.4f, Height, 2.2f), center + new Vector3(side * (half + 1.15f), Height / 2f, 0f), Look.Paint(device, Look.Stone, 0.3f), "bridge landing");
            foreach (var z in new[] { -1f, 1f })
                Look.StaticBox(game, new Vector3(0.22f, Height + 1.5f, 0.22f), center + new Vector3(side * (half + 0.1f), (Height + 1.5f) / 2f, z * (Width / 2f + 0.15f)), navy, "bridge tower");
            Look.StaticBox(game, new Vector3(0.22f, 0.22f, Width + 0.52f), center + new Vector3(side * (half + 0.1f), Height + 1.5f, 0f), navy, "bridge tower beam");
        }

        // The deck: a strip of cloth held at both ends, a plank pinned across it at each band. Heavy particles, few of them along the span:
        // springs in series give way, and particles much lighter than the planks they carry let them sink
        var deck = new ClothComponent { StretchStiffness = 60f, BendStiffness = 6f, Compression = 0.2f, Damping = 1f, Mass = 25f, AirDrag = 0f };
        deck.Pins.Add(new SoftBodyPin { Region = new BoundingBox(new Vector3(-half - 1f, -1f, -2f), new Vector3(-half + 0.08f, 1f, 2f)) });
        deck.Pins.Add(new SoftBodyPin { Region = new BoundingBox(new Vector3(half - 0.08f, -1f, -2f), new Vector3(half + 1f, 1f, 2f)) });
        var plankSize = new Vector3(0.24f, 0.05f, Width);
        for (int i = 0; i < Planks; i++)
        {
            float x = -half + (i + 1) * Span / (Planks + 1);
            var plank = new BodyComponent { Collider = new CompoundCollider { Colliders = { new BoxCollider { Size = plankSize, Mass = 0.2f } } } };
            var plankEntity = new Entity("bridge plank") { new ModelComponent(Look.Model(game, new CubeProceduralModel { Size = plankSize }, wood)), plank };
            plankEntity.Transform.Position = center + new Vector3(x, Height + 0.03f, 0f);
            game.AddToScene(plankEntity);
            _planks.Add(plank);
            // Wider than the spacing of the particles: two rows at least, so the plank cannot turn about a single one
            deck.Pins.Add(new SoftBodyPin { Region = new BoundingBox(new Vector3(x - 0.26f, -1f, -2f), new Vector3(x + 0.26f, 1f, 2f)), Anchor = new BodyAnchor { Body = plank } });
        }
        var deckEntity = new Entity("bridge deck")
        {
            new ModelComponent(Look.Model(game, new PlaneProceduralModel { Size = new Vector2(Span, Width), Tessellation = new Int2(48, 4), Normal = NormalDirection.UpY, GenerateBackFace = true }, Look.Ivory, 0.3f)),
            deck,
        };
        deckEntity.Transform.Position = center + new Vector3(0f, Height, 0f);
        game.AddToScene(deckEntity);

        // Two hand ropes from tower to tower
        foreach (var z in new[] { -1f, 1f })
        {
            float rz = z * (Width / 2f + 0.15f);
            Ropes.Add(game, center + new Vector3(-half + 0.1f, Height + 1.1f, rz), center + new Vector3(half - 0.1f, Height + 1.1f, rz), 0.04f, Look.Wood, null, null, mass: 2f, stiffness: 80f, name: "hand rope");
        }

        if (!WithWreckingBall)
            return;

        // The wrecking ball: a heavy ball on a long rope from a gantry above the middle of the bridge
        foreach (var z in new[] { -1f, 1f })
            Look.StaticBox(game, new Vector3(0.3f, GantryHeight, 0.3f), center + new Vector3(0f, GantryHeight / 2f, z * 4.5f), navy, "gantry post");
        var beam = Look.StaticBox(game, new Vector3(0.3f, 0.3f, 9.3f), center + new Vector3(0f, GantryHeight + 0.15f, 0f), navy, "gantry beam");
        var hook = center + new Vector3(0f, GantryHeight, 0f);
        _ball = new BodyComponent { Collider = new CompoundCollider { Colliders = { new SphereCollider { Radius = BallRadius, Mass = 30f } } } };
        var ball = new Entity("wrecking ball") { new ModelComponent(Look.Model(game, new SphereProceduralModel { Radius = BallRadius, Tessellation = 40 }, Look.Paint(device, new Color(60, 62, 70), 0.85f, 0.8f))), _ball };
        ball.Transform.Position = hook - new Vector3(0f, RopeLength + BallRadius - Ropes.PinLength * 1.5f, 0f);
        game.AddToScene(ball);
        Ropes.Add(game, hook, hook - new Vector3(0f, RopeLength, 0f), 0.06f, Look.Ivory, beam, ball, mass: 6f, stiffness: 80f, name: "wrecking ball rope");
    }

    /// <summary> Called every frame while the zone simulates </summary>
    public void Tick(VideoDemoGame game, float time, float seconds)
    {
        if (_ball is not null && Zone.Every(time, seconds, 9f, 5f))
        {
            // Across the bridge, one way then the other
            _ball.LinearVelocity += new Vector3(0f, 0f, _swings++ % 2 == 0 ? 7f : -7f);
            _ball.Awake = true;
        }
        if (time >= Settle && Zone.Every(time - Settle, seconds, SendPeriod))
            Send(game);
    }

    /// <summary> Something drops on the middle of the deck: a soft ball, a barrel, a jelly, a crate, a big soft ball, in turn </summary>
    private void Send(VideoDemoGame game)
    {
        var start = Deck + new Vector3(1.8f * MathF.Sin(_sent * 2.3f), 2f, 0.15f * MathF.Sin(_sent * 1.7f));
        Entity entity;
        switch (_sent % 5)
        {
            case 0:
                entity = Kit.AddSoftBall(game, start, 0.3f, Look.Accents[_sent % Look.Accents.Length], 5, 1f);
                break;
            case 1:
            {
                var barrel = new BodyComponent { Collider = new CompoundCollider { Colliders = { new CylinderCollider { Radius = 0.25f, Length = 0.6f, Mass = 4f } } } };
                entity = new Entity("barrel") { new ModelComponent(Look.Model(game, new CylinderProceduralModel { Radius = 0.25f, Height = 0.6f, Tessellation = 28 }, Look.Coral, 0.6f)), barrel };
                entity.Transform.Position = start;
                entity.Transform.Rotation = Quaternion.RotationX(MathUtil.PiOverTwo);
                game.AddToScene(entity);
                break;
            }
            case 2:
                entity = Kit.AddJelly(game, new CubeProceduralModel { Size = new Vector3(0.5f), Subdivisions = 6 }, start, _sent, 4);
                break;
            case 3:
            {
                var size = new Vector3(0.6f);
                var crate = new BodyComponent { Collider = new CompoundCollider { Colliders = { new BoxCollider { Size = size, Mass = 4f } } } };
                entity = new Entity("crate") { new ModelComponent(Look.Model(game, new CubeProceduralModel { Size = size }, Look.Wood, 0.4f)), crate };
                entity.Transform.Position = start;
                game.AddToScene(entity);
                break;
            }
            default:
                entity = Kit.AddSoftBall(game, start, 0.45f, Look.Teal, 6, 2.5f);
                break;
        }
        _sent++;
        _traffic.Enqueue(entity);
        if (_traffic.Count > MaxTraffic)
            Zone.Remove(game, _traffic.Dequeue());
    }
}
