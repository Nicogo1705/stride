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
/// A soft body garden: see-through jellies on a glass table under a chrome press, flags in gusts, a tent lifted by a kinematic sphere
/// with soft balls bouncing on it, a striped piece twisted by a motor. Every 10 s the jellies switch OpacityThickness on or off
/// </summary>
internal sealed class GardenZone : Zone
{
    private const float SwapPeriod = 10f;
    private readonly List<(ModelComponent Model, Material On, Material Off)> _swappable = new();
    private readonly List<ClothComponent> _flags = new();
    private bool _on = true;
    private SoftPiece? _piece;

    public override string Name => "Soft garden";
    public override string Description => "jellies whose opacity grows with the thickness crossed, cloth in the wind, a tent, a twisted soft piece";
    public override Vector3 Eye => Origin + new Vector3(0.3f, 3.6f, 9.5f);
    public override Vector3 Target => Origin + new Vector3(0f, 1f, -0.7f);

    public override void Build(VideoDemoGame game, WorldTerrain terrain)
    {
        _swappable.Clear();
        _flags.Clear();
        _on = true;
        var device = game.GraphicsDevice;
        var o = Origin;

        // The glass table: two chrome legs, a thick top of tinted glass
        var chrome = Look.Paint(device, new Color(222, 226, 234), 0.93f);
        foreach (var x in new[] { -1.15f, 1.15f })
        {
            var leg = new Entity("table leg") { new ModelComponent(Look.Model(game, new CylinderProceduralModel { Radius = 0.07f, Height = 0.76f, Tessellation = 32 }, chrome)) };
            leg.Transform.Position = o + new Vector3(x, 0.38f, 0f);
            game.AddToScene(leg);
        }
        var top = new Vector3(3.2f, 0.1f, 1.5f);
        var glass = Swappable(game, Look.Volume(device, new Color(120, 200, 225), 0.45f, 0.15f, MaterialVolumeMedium.Absorbing), Look.Volume(device, new Color(120, 200, 225), 0.45f, 0f, MaterialVolumeMedium.Absorbing),
            new CubeProceduralModel { Size = top });
        glass.Add(new StaticComponent { Collider = new CompoundCollider { Colliders = { new BoxCollider { Size = top } } } });
        glass.Transform.Position = o + new Vector3(0f, 0.81f, 0f);
        glass.Get<ModelComponent>()!.IsShadowCaster = false;
        glass.Name = "glass table";
        game.AddToScene(glass);

        // Five see-through jellies on the glass, each with its medium
        PrimitiveProceduralModelBase[] shapes =
        {
            new CubeProceduralModel { Size = new Vector3(0.5f), Subdivisions = 8 },
            new SphereProceduralModel { Radius = 0.3f, Tessellation = 32 },
            new TorusProceduralModel { Radius = 0.26f, Thickness = 0.13f, Tessellation = 40 },
            new CapsuleProceduralModel { Radius = 0.18f, Length = 0.36f, Tessellation = 24, LengthRings = 8 },
            new CubeProceduralModel { Size = new Vector3(0.45f, 0.6f, 0.45f), Subdivisions = 8 },
        };
        for (int i = 0; i < shapes.Length; i++)
        {
            var medium = i % 2 == 0 ? MaterialVolumeMedium.Scattering : MaterialVolumeMedium.Absorbing;
            var color = Kit.JellyColors[i];
            var jelly = Swappable(game, Look.Volume(device, color, 0.85f, 0.3f, medium), Look.Volume(device, color, 0.85f, 0f, medium), shapes[i]);
            jelly.Name = "jelly";
            jelly.Add(new VolumetricSoftBodyComponent { Resolution = 5, Stiffness = 8f, ShearStiffness = 8f, Damping = 0.3f });
            jelly.Transform.Position = o + new Vector3(-1.2f + i * 0.6f, 1.35f + (i % 2) * 0.5f, i % 2 == 0 ? 0.2f : -0.25f);
            jelly.Transform.Rotation = Quaternion.RotationYawPitchRoll(i * 0.8f, i * 0.3f, 0f);
            game.AddToScene(jelly);
        }
        var press = new BodyComponent { Kinematic = true, Collider = new CompoundCollider { Colliders = { new SphereCollider { Radius = 0.2f } } } };
        var pressEntity = new Entity("chrome press") { new ModelComponent(Look.Model(game, new SphereProceduralModel { Radius = 0.2f, Tessellation = 40 }, chrome)), press };
        pressEntity.Transform.Position = o + new Vector3(0f, 2.0f, 0.2f);
        game.AddToScene(pressEntity);

        // Three flags at the back left
        var flagColors = new[] { Look.Coral, Look.Sun, Look.Teal };
        for (int i = 0; i < 3; i++)
        {
            var foot = o + new Vector3(-5.6f + i * 1.8f, 0f, -3.8f + i * 0.3f);
            Props.Pillar(game, foot, 0.05f, 3.4f, Look.Navy, 0.7f);
            var flag = new ClothComponent { Pins = { new SoftBodyPin { Region = new BoundingBox(new Vector3(-0.82f, -1f, -1f), new Vector3(-0.78f, 1f, 1f)) } } };
            var entity = new Entity("flag") { new ModelComponent(Look.Model(game, new PlaneProceduralModel { Size = new Vector2(1.6f, 1f), Tessellation = new Int2(24, 16), Normal = NormalDirection.UpZ, GenerateBackFace = true }, flagColors[i], 0.3f)), flag };
            entity.Transform.Position = foot + new Vector3(0.85f, 2.85f, 0f);
            game.AddToScene(entity);
            _flags.Add(flag);
        }

        // A tent at the back right, lifted from below by a kinematic sphere, two soft balls bouncing on it
        var navy = Look.Paint(device, Look.Navy, 0.5f);
        var tentCenter = o + new Vector3(3.5f, 0f, -2.6f);
        foreach (var (x, z) in new[] { (-1.4f, -1.4f), (1.4f, -1.4f), (-1.4f, 1.4f), (1.4f, 1.4f) })
            Look.StaticBox(game, new Vector3(0.12f, 1.1f, 0.12f), tentCenter + new Vector3(x, 0.55f, z), navy, "tent post");
        var tent = new ClothComponent();
        foreach (var (x, z) in new[] { (-1f, -1f), (1f, -1f), (-1f, 1f), (1f, 1f) })
        {
            var corner = new Vector3(x * 1.425f, 0f, z * 1.425f);
            tent.Pins.Add(new SoftBodyPin { Region = new BoundingBox(corner - new Vector3(0.08f, 1f, 0.08f), corner + new Vector3(0.08f, 1f, 0.08f)) });
        }
        var tentEntity = new Entity("tent") { new ModelComponent(Look.Model(game, new PlaneProceduralModel { Size = new Vector2(2.9f), Tessellation = new Int2(24), Normal = NormalDirection.UpY, GenerateBackFace = true }, Look.Mint, 0.35f)), tent };
        tentEntity.Transform.Position = tentCenter + new Vector3(0f, 1.12f, 0f);
        game.AddToScene(tentEntity);
        var lifter = new BodyComponent { Kinematic = true, Collider = new CompoundCollider { Colliders = { new SphereCollider { Radius = 0.45f } } } };
        var lifterEntity = new Entity("kinematic sphere") { new ModelComponent(Look.Model(game, new SphereProceduralModel { Radius = 0.45f, Tessellation = 40 }, Look.Pink, 0.85f)), lifter };
        lifterEntity.Transform.Position = tentCenter + new Vector3(0f, 0.45f, 0f);
        game.AddToScene(lifterEntity);
        Kit.AddSoftBall(game, tentCenter + new Vector3(-0.5f, 2.2f, 0.3f), 0.26f, Look.Coral);
        Kit.AddSoftBall(game, tentCenter + new Vector3(0.55f, 2.8f, -0.3f), 0.26f, Look.Blue);

        // A striped piece pinned at both ends: a motor twists it from the left, the block on the right can be pulled to stretch it
        _piece = new SoftPiece(game, o + new Vector3(1.2f, 1.0f, 2.2f), o + new Vector3(3.4f, 1.0f, 2.2f), Look.Coral, Look.Ivory);
        var piece = _piece;

        game.AddToScene(new Entity("press, wind and lifter")
        {
            new StepDriver((time, _) =>
            {
                piece.Drive(time);
                // The press comes down slowly on the middle jelly every 8 s
                float phase = time % 8f;
                float down = phase < 3f ? MathF.Sin(phase / 3f * MathF.PI) : 0f;
                press.SetTargetPose(o + new Vector3(0f, 2.0f - down * 0.85f, 0.2f));
                // Gusts with a phase per flag, so they wave out of step
                for (int i = 0; i < _flags.Count; i++)
                {
                    float gust = 0.55f + 0.45f * MathF.Sin(time * 0.9f + i * 1.3f) * MathF.Sin(time * 0.37f + i);
                    _flags[i].Wind = new Vector3(9f * gust + 2f, 0.5f, 2.5f * MathF.Sin(time * 0.6f + i));
                }
                // The sphere rises under the tent every 5 s, moving sideways a little so the peak travels
                float lift = time % 5f < 3f ? MathF.Sin(time % 5f / 3f * MathF.PI) : 0f;
                lifter.SetTargetPose(tentCenter + new Vector3(0.5f * MathF.Sin(time * 0.4f), 0.45f + lift * 1.3f, 0.4f * MathF.Cos(time * 0.3f)));
            }),
        });
    }

    /// <summary> An entity whose material switches between <paramref name="on"/> and <paramref name="off"/>, not yet in the scene </summary>
    private Entity Swappable(VideoDemoGame game, Material on, Material off, PrimitiveProceduralModelBase shape)
    {
        var model = new ModelComponent(Look.Model(game, shape, on));
        _swappable.Add((model, on, off));
        return new Entity("volume") { model };
    }

    protected override void Tick(VideoDemoGame game, float time, float seconds)
    {
        if (!Every(time, seconds, SwapPeriod, SwapPeriod))
            return;
        _on = !_on;
        foreach (var (model, on, off) in _swappable)
            model.Materials[0] = _on ? on : off;
    }

    public override IEnumerable<SceneLabel> Labels()
    {
        var o = Origin;
        yield return new(o + new Vector3(0f, 2.75f, 0f), _on ? "OpacityThickness on" : "OpacityThickness off");
        yield return new(o + new Vector3(-3.8f, 3.55f, -3.5f), "ClothComponent.Wind");
        yield return new(o + new Vector3(3.5f, 3.2f, -2.6f), "cloth + kinematic sphere");
        yield return new(o + new Vector3(2.3f, 1.65f, 2.2f), "two pins, each anchored to a rigid handle");
    }

    public override IEnumerable<string> Status()
    {
        yield return $"OpacityThickness: {(_on ? "on" : "off")} (switches every {SwapPeriod:0} s)  |  5 jellies, 3 flags, 1 tent, 2 soft balls, twisted piece at {(_piece?.Angle ?? 0f) / MathUtil.TwoPi:0.0} turns";
    }
}
