// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using Stride.BepuPhysics.Definitions.Colliders;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics.GeometricPrimitives;
using Stride.Rendering;
using Stride.Rendering.Materials;
using Stride.Rendering.ProceduralModels;

namespace Stride.BepuPhysics.Soft.Tests.VideoDemo;

/// <summary>
/// A soft body garden: a huge trampoline of cloth pinned along its four edges, covered with soft objects of every shape and size
/// (donuts, teapots, balls, cubes, capsules, cylinders, cones), a new object dropped every few seconds.
/// Every 10 s the see-through ones switch OpacityThickness on or off. Low resolutions keep the cost down
/// </summary>
internal sealed class GardenZone : Zone
{
    private const float SwapPeriod = 10f, DropPeriod = 4f;
    private const float Half = 3.6f, Height = 2.4f;
    private const int MaxDropped = 10;

    private readonly List<(ModelComponent Model, Material On, Material Off)> _swappable = new();
    private readonly Queue<Entity> _dropped = new();
    private bool _on = true;
    private int _drops, _objects;

    public override string Name => "Soft garden";
    public override string Description => "a huge trampoline covered with soft donuts, teapots, balls and cubes of every size";
    public override Vector3 Eye => Origin + new Vector3(0.3f, 6.5f, 11.5f);
    public override Vector3 Target => Origin + new Vector3(0f, 1.2f, 0f);

    public override void Build(VideoDemoGame game, WorldTerrain terrain)
    {
        _swappable.Clear();
        _dropped.Clear();
        (_on, _drops, _objects) = (true, 0, 0);
        var device = game.GraphicsDevice;
        var o = Origin;
        var navy = Look.Paint(device, Look.Navy, 0.5f);

        // The frame: a post at each corner, a rail along each edge just outside the cloth
        foreach (var (x, z) in new[] { (-1f, -1f), (1f, -1f), (-1f, 1f), (1f, 1f) })
            Look.StaticBox(game, new Vector3(0.2f, Height, 0.2f), o + new Vector3(x * (Half + 0.15f), Height / 2f, z * (Half + 0.15f)), navy, "trampoline post");
        foreach (var (x, z, along) in new[] { (0f, -1f, true), (0f, 1f, true), (-1f, 0f, false), (1f, 0f, false) })
        {
            var size = along ? new Vector3(2f * Half + 0.5f, 0.14f, 0.14f) : new Vector3(0.14f, 0.14f, 2f * Half + 0.5f);
            Look.StaticBox(game, size, o + new Vector3(x * (Half + 0.15f), Height - 0.05f, z * (Half + 0.15f)), navy, "trampoline rail");
        }

        // The trampoline: stretchy, barely damped so it bounces, pinned along its four edges
        var trampoline = new ClothComponent { StretchStiffness = 25f, BendStiffness = 1f, Damping = 0.12f, Compression = 0.3f, Mass = 6f };
        foreach (var (min, max) in new[]
        {
            (new Vector3(-Half - 1f, -1f, -Half - 1f), new Vector3(Half + 1f, 1f, -Half + 0.1f)),
            (new Vector3(-Half - 1f, -1f, Half - 0.1f), new Vector3(Half + 1f, 1f, Half + 1f)),
            (new Vector3(-Half - 1f, -1f, -Half - 1f), new Vector3(-Half + 0.1f, 1f, Half + 1f)),
            (new Vector3(Half - 0.1f, -1f, -Half - 1f), new Vector3(Half + 1f, 1f, Half + 1f)),
        })
            trampoline.Pins.Add(new SoftBodyPin { Region = new BoundingBox(min, max) });
        var trampolineEntity = new Entity("trampoline")
        {
            new ModelComponent(Look.Model(game, new PlaneProceduralModel { Size = new Vector2(2f * Half), Tessellation = new Int2(36), Normal = NormalDirection.UpY, GenerateBackFace = true }, Look.Striped(device, Look.Teal, Look.Ivory, 12, 0.35f))),
            trampoline,
        };
        trampolineEntity.Transform.Position = o + new Vector3(0f, Height, 0f);
        game.AddToScene(trampolineEntity);

        // The pile: a big donut and a big ball in the middle, then a grid of shapes of every size, stacked in three layers
        AddObject(game, new TorusProceduralModel { Radius = 0.75f, Thickness = 0.3f, Tessellation = 48 }, o + new Vector3(-1.1f, Height + 1.2f, 0.4f), 6, 4f, 0);
        AddObject(game, new SphereProceduralModel { Radius = 0.7f, Tessellation = 40 }, o + new Vector3(1.2f, Height + 1.4f, -0.3f), 6, 3.5f, 1);
        AddObject(game, new TeapotProceduralModel { Size = 0.9f, Tessellation = 10 }, o + new Vector3(0f, Height + 3.2f, 0.2f), 6, 2.5f, 2);
        for (int layer = 0; layer < 3; layer++)
        {
            for (int i = 0; i < 6; i++)
            {
                float angle = i * MathUtil.TwoPi / 6f + layer * 0.5f;
                float ring = 2.2f - layer * 0.4f;
                var at = o + new Vector3(ring * MathF.Cos(angle), Height + 1.2f + layer * 1.3f, ring * MathF.Sin(angle));
                var (shape, mass) = Shape(layer * 6 + i);
                AddObject(game, shape, at, 4, mass, layer * 6 + i + 3);
            }
        }
    }

    /// <summary> A shape from the catalog, its size varying along the index, and a mass growing with it </summary>
    private static (PrimitiveProceduralModelBase Shape, float Mass) Shape(int index)
    {
        float scale = 0.6f + 0.25f * (index * 7 % 5);
        return (index % 7) switch
        {
            0 => (new TorusProceduralModel { Radius = 0.3f * scale, Thickness = 0.13f * scale, Tessellation = 32 }, 0.8f * scale),
            1 => (new TeapotProceduralModel { Size = 0.5f * scale, Tessellation = 8 }, 1f * scale),
            2 => (new SphereProceduralModel { Radius = 0.28f * scale, Tessellation = 24 }, 0.7f * scale),
            3 => (new CubeProceduralModel { Size = new Vector3(0.45f * scale), Subdivisions = 6 }, 1f * scale),
            4 => (new CapsuleProceduralModel { Radius = 0.16f * scale, Length = 0.4f * scale, Tessellation = 20, LengthRings = 6 }, 0.6f * scale),
            5 => (new CylinderProceduralModel { Radius = 0.25f * scale, Height = 0.45f * scale, Tessellation = 24 }, 0.8f * scale),
            _ => (new ConeProceduralModel { Radius = 0.3f * scale, Height = 0.55f * scale, Tessellation = 24 }, 0.6f * scale),
        };
    }

    /// <summary> A soft object, one in three see-through (its material switching with OpacityThickness), the others painted </summary>
    private Entity AddObject(VideoDemoGame game, PrimitiveProceduralModelBase shape, Vector3 position, int resolution, float mass, int index)
    {
        var device = game.GraphicsDevice;
        var color = Kit.JellyColors[index % Kit.JellyColors.Length];
        ModelComponent model;
        if (index % 3 == 0)
        {
            var medium = index % 2 == 0 ? MaterialVolumeMedium.Scattering : MaterialVolumeMedium.Absorbing;
            var (on, off) = (Look.Volume(device, color, 0.85f, 0.3f, medium), Look.Volume(device, color, 0.85f, 0f, medium));
            model = new ModelComponent(Look.Model(game, shape, _on ? on : off));
            _swappable.Add((model, on, off));
        }
        else
        {
            var accent = Look.Accents[index % Look.Accents.Length];
            model = new ModelComponent(Look.Model(game, shape, index % 3 == 1 ? Look.Paint(device, accent, 0.8f) : Look.Striped(device, accent, Look.Ivory, 5)));
        }
        var entity = new Entity("soft object")
        {
            model,
            new VolumetricSoftBodyComponent { Resolution = resolution, Stiffness = 10f, ShearStiffness = 9f, VolumeStiffness = 12f, Damping = 0.25f, Mass = mass },
        };
        entity.Transform.Position = position;
        entity.Transform.Rotation = Quaternion.RotationYawPitchRoll(index * 0.9f, index * 0.4f, index * 0.25f);
        game.AddToScene(entity);
        _objects++;
        return entity;
    }

    protected override void Tick(VideoDemoGame game, float time, float seconds)
    {
        if (Every(time, seconds, DropPeriod))
        {
            // A new object from above, somewhere over the cloth; the oldest dropped one goes once there are too many
            int index = 100 + _drops;
            var spot = new Vector3(2.2f * MathF.Sin(_drops * 2.4f), 6f, 2.2f * MathF.Cos(_drops * 1.7f));
            var (shape, mass) = Shape(index);
            _dropped.Enqueue(AddObject(game, shape, Origin + spot, 4, mass, index));
            _drops++;
            if (_dropped.Count > MaxDropped)
            {
                var gone = _dropped.Dequeue();
                _swappable.RemoveAll(s => s.Model == gone.Get<ModelComponent>());
                Remove(game, gone);
                _objects--;
            }
        }
        if (!Every(time, seconds, SwapPeriod, SwapPeriod))
            return;
        _on = !_on;
        foreach (var (model, on, off) in _swappable)
            model.Materials[0] = _on ? on : off;
    }

    public override IEnumerable<SceneLabel> Labels()
    {
        var o = Origin;
        yield return new(o + new Vector3(0f, 4.2f, -Half), "trampoline: ClothComponent pinned on its four edges");
        yield return new(o + new Vector3(0f, 3.6f, -Half), _on ? "OpacityThickness on" : "OpacityThickness off");
    }

    public override IEnumerable<string> Status()
    {
        yield return $"OpacityThickness: {(_on ? "on" : "off")} (switches every {SwapPeriod:0} s)  |  {_objects} soft objects on the trampoline, one dropped every {DropPeriod:0} s";
    }
}
