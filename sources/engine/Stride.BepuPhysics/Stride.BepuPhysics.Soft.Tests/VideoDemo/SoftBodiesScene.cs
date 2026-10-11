// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using Stride.BepuPhysics.Definitions.Colliders;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics.GeometricPrimitives;
using Stride.Input;
using Stride.Rendering;
using Stride.Rendering.Materials;
using Stride.Rendering.ProceduralModels;

namespace Stride.BepuPhysics.Soft.Tests.VideoDemo;

/// <summary>
/// A soft body garden: see-through jellies on a glass table under a chrome press, flags in gusts, a tent lifted by a kinematic sphere
/// with soft balls bouncing on it, molluscs crawling on an animated skeleton, one with a sail pinned to its head bone
/// </summary>
internal sealed class SoftBodiesScene : VideoScene
{
    private readonly List<(ModelComponent Model, Material On, Material Off)> _swappable = new();
    private readonly List<ClothComponent> _flags = new();
    private bool _on = true;

    /// <summary> The striped rope between the twister and the block </summary>
    public SoftPiece? Rope { get; private set; }

    public override string Title => "Soft bodies";
    public override string Pr => "PR #3528, #3530, #3564, FollowSkeleton / AnchorNode (upcoming)";
    public override string Description => "jellies whose opacity grows with the thickness crossed, a rope twisted by a motor (pull its block to stretch it), cloth in the wind, molluscs with flesh on a skeleton";
    public override Vector3 Eye => new(0.3f, 2.7f, 6.6f);
    public override Vector3 Target => new(0f, 0.9f, -0.7f);
    public override string SceneKeys => "O: OpacityThickness on / off  |  left click: grab a jelly, a flag, the rope's block (stretch it)  |  V: particles";


    protected override void BuildScene(VideoDemoGame game)
    {
        _swappable.Clear();
        _flags.Clear();
        _on = true;
        var device = game.GraphicsDevice;

        // The glass table: two chrome legs, a thick top of tinted glass
        var chrome = Look.Paint(device, new Color(222, 226, 234), 0.93f);
        foreach (var x in new[] { -1.15f, 1.15f })
        {
            var leg = new Entity("table leg") { new ModelComponent(Look.Model(game, new CylinderProceduralModel { Radius = 0.07f, Height = 0.76f, Tessellation = 32 }, chrome)) };
            leg.Transform.Position = new Vector3(x, 0.38f, 0f);
            game.AddToScene(leg);
        }
        var top = new Vector3(3.2f, 0.1f, 1.5f);
        var glass = AddSwappable(game, Look.Volume(device, new Color(120, 200, 225), 0.45f, 0.15f, MaterialVolumeMedium.Absorbing), Look.Volume(device, new Color(120, 200, 225), 0.45f, 0f, MaterialVolumeMedium.Absorbing),
            new CubeProceduralModel { Size = top });
        glass.Add(new StaticComponent { Collider = new CompoundCollider { Colliders = { new BoxCollider { Size = top } } } });
        glass.Transform.Position = new Vector3(0f, 0.81f, 0f);
        glass.Get<ModelComponent>()!.IsShadowCaster = false;
        glass.Name = "glass table";
        game.AddToScene(glass);

        // Five see-through jellies dropped onto the glass, each with its medium
        var colors = new[] { new Color(255, 90, 140), new Color(130, 230, 100), new Color(255, 185, 40), new Color(150, 110, 255), new Color(70, 200, 240) };
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
            var jelly = AddSwappable(game, Look.Volume(device, colors[i], 0.85f, 0.3f, medium), Look.Volume(device, colors[i], 0.85f, 0f, medium), shapes[i]);
            jelly.Name = "jelly";
            jelly.Add(new VolumetricSoftBodyComponent { Resolution = 5, Stiffness = 8f, ShearStiffness = 8f, Damping = 0.3f });
            jelly.Transform.Position = new Vector3(-1.2f + i * 0.6f, 1.35f + (i % 2) * 0.5f, (i % 2 == 0 ? 0.2f : -0.25f));
            jelly.Transform.Rotation = Quaternion.RotationYawPitchRoll(i * 0.8f, i * 0.3f, 0f);
            game.AddToScene(jelly);
        }
        var press = new BodyComponent { Kinematic = true, Collider = new CompoundCollider { Colliders = { new SphereCollider { Radius = 0.2f } } } };
        var pressEntity = new Entity("chrome press") { new ModelComponent(Look.Model(game, new SphereProceduralModel { Radius = 0.2f, Tessellation = 40 }, chrome)), press };
        pressEntity.Transform.Position = new Vector3(0f, 2.3f, 0.2f);
        game.AddToScene(pressEntity);

        // Three flags at the back left
        var flagColors = new[] { Look.Coral, Look.Sun, Look.Teal };
        for (int i = 0; i < 3; i++)
        {
            var foot = new Vector3(-5.6f + i * 1.8f, 0f, -3.8f + i * 0.3f);
            Props.Pillar(game, foot, 0.05f, 3.4f, Look.Navy, 0.7f);
            var flag = new ClothComponent { Pins = { new SoftBodyPin { Region = new BoundingBox(new Vector3(-0.82f, -1f, -1f), new Vector3(-0.78f, 1f, 1f)) } } };
            var entity = new Entity("flag") { new ModelComponent(Look.Model(game, new PlaneProceduralModel { Size = new Vector2(1.6f, 1f), Tessellation = new Int2(24, 16), Normal = NormalDirection.UpZ, GenerateBackFace = true }, flagColors[i], 0.3f)), flag };
            entity.Transform.Position = foot + new Vector3(0.85f, 2.85f, 0f);
            game.AddToScene(entity);
            _flags.Add(flag);
        }

        // A tent at the back right, lifted from below by a kinematic sphere, two soft balls bouncing on it
        var navy = Look.Paint(device, Look.Navy, 0.5f);
        var tentCenter = new Vector3(3.5f, 0f, -2.6f);
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
        foreach (var (offset, color) in new[] { (new Vector3(-0.5f, 2.2f, 0.3f), Look.Coral), (new Vector3(0.55f, 2.8f, -0.3f), Look.Blue) })
        {
            var ball = new Entity("soft ball")
            {
                new ModelComponent(Look.Model(game, new SphereProceduralModel { Radius = 0.26f, Tessellation = 32 }, color, 0.85f)),
                new VolumetricSoftBodyComponent { Resolution = 5, Stiffness = 14f, ShearStiffness = 14f, VolumeStiffness = 12f, Damping = 0.15f, Mass = 0.6f },
            };
            ball.Transform.Position = tentCenter + offset;
            game.AddToScene(ball);
        }

        // Two molluscs crawling around a rock at the front left, the violet one under a sail pinned to its head bone
        var rock = new Vector3(-2.3f, 0f, 1.3f);
        Props.Pillar(game, rock, 0.35f, 0.6f, Look.Stone, 0.3f);
        var driver = new MolluscDriver();
        Mollusc.Add(game, Look.Coral, driver, Mollusc.Circle(rock, 1.05f, 0.5f, MathF.PI));
        var path = Mollusc.Circle(rock, 1.05f, 0.5f, 0f);
        var sailed = Mollusc.Add(game, Look.Violet, driver, path, PhysicalWorm.FleshLayer);
        Mollusc.AddSail(game, sailed, path(0f).Yaw, Look.Sun);
        game.AddToScene(new Entity("mollusc driver") { driver });

        // A striped rope pinned at both ends: a motor twists it from the left, the block on the right stretches it when pulled
        Rope = new SoftPiece(game, new Vector3(1.2f, 1.0f, 2.0f), new Vector3(3.4f, 1.0f, 2.0f), Look.Coral, Look.Ivory);
        var rope = Rope;

        game.AddToScene(new Entity("press, wind and lifter")
        {
            new StepDriver((time, _) =>
            {
                rope.Drive(time);
                // The press comes down on the middle jelly every 3.5 s
                float phase = time % 3.5f;
                float down = phase < 1.4f ? MathF.Sin(phase / 1.4f * MathF.PI) : 0f;
                press.SetTargetPose(new Vector3(0f, 2.0f - down * 0.85f, 0.2f));
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

    /// <summary> An entity whose material O switches between <paramref name="on"/> and <paramref name="off"/>, not yet in the scene </summary>
    private Entity AddSwappable(VideoDemoGame game, Material on, Material off, PrimitiveProceduralModelBase shape)
    {
        var model = new ModelComponent(Look.Model(game, shape, on));
        var entity = new Entity("volume") { model };
        _swappable.Add((model, on, off));
        return entity;
    }

    public override void UpdateInput(VideoDemoGame game)
    {
        if (!game.Input.IsKeyPressed(Keys.O))
            return;
        _on = !_on;
        foreach (var (model, on, off) in _swappable)
            model.Materials[0] = _on ? on : off;
    }

    public override IEnumerable<(Vector3 World, string Text)> Labels()
    {
        yield return (new Vector3(0f, 2.75f, 0f), _on ? "OpacityThickness jellies on glass" : "constant opacity (OpacityThickness off)");
        yield return (new Vector3(-2.7f, 3.55f, -3.5f), "ClothComponent.Wind");
        yield return (new Vector3(3.5f, 3.2f, -2.6f), "cloth + kinematic sphere");
        yield return (new Vector3(-2.3f, 1.4f, 1.3f), "FollowSkeleton flesh, AnchorNode sail");
        yield return (new Vector3(2.3f, 1.65f, 2.0f), "two pins, each anchored to a rigid handle");
    }

    public override IEnumerable<string> Status()
    {
        yield return $"OpacityThickness: {(_on ? "on" : "off")} (O)  |  5 jellies, 3 flags, 1 tent, 2 soft balls, 2 molluscs + sail, 1 rope (twister at {Rope?.Angle / MathUtil.TwoPi ?? 0f:0.0} turns)";
    }
}
