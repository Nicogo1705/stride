// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Globalization;
using Stride.BepuPhysics.Definitions.Colliders;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics.GeometricPrimitives;
using Stride.Rendering.ProceduralModels;

namespace Stride.BepuPhysics.Soft.Tests.VideoDemo;

/// <summary>
/// Many vertices on few particles, for the GPU deformation: dense spheres on a coarse lattice stirred by a slow paddle, two dense cloths in gusts
/// </summary>
internal sealed class StressZone : Zone
{
    public const int Rows = 3, Columns = 4, SphereTessellation = 192, LatticeResolution = 3, ClothTessellation = 40;

    // As SphereProceduralModel builds it: rings from pole to pole, twice as many segments around
    public const int SphereVertices = (SphereTessellation + 1) * (2 * SphereTessellation + 1);

    public override string Name => "Deformation stress";
    public override string Description => $"{Rows * Columns} spheres of {SphereVertices} vertices on a {LatticeResolution}-cell lattice, two {ClothTessellation}x{ClothTessellation} cloths";
    public override Vector3 Eye => Origin + new Vector3(0f, 4.6f, 9.4f);
    public override Vector3 Target => Origin + new Vector3(0f, 0.6f, 0f);

    public override void Build(VideoDemoGame game, WorldTerrain terrain)
    {
        var o = Origin;
        for (int row = 0; row < Rows; row++)
        for (int column = 0; column < Columns; column++)
        {
            var sphere = new Entity("dense sphere")
            {
                new ModelComponent(Look.Model(game, new SphereProceduralModel { Radius = 0.45f, Tessellation = SphereTessellation }, Look.Accents[(row * Columns + column) % Look.Accents.Length], 0.6f)),
                new VolumetricSoftBodyComponent { Resolution = LatticeResolution, Stiffness = 6f, ShearStiffness = 6f, Damping = 0.2f },
            };
            sphere.Transform.Position = o + new Vector3((column - (Columns - 1) / 2f) * 1.3f, 0.8f + row * 1.1f, (row - 1) * 1.2f);
            game.AddToScene(sphere);
        }

        var cloths = new ClothComponent[2];
        for (int i = 0; i < 2; i++)
        {
            float x = i == 0 ? -3.8f : 3.8f;
            Look.StaticBox(game, new Vector3(1.9f, 0.08f, 0.08f), o + new Vector3(x, 3.04f, -1.6f), Look.Paint(game.GraphicsDevice, Look.Navy, 0.5f), "cloth bar");
            var plane = new PlaneProceduralModel { Size = new Vector2(1.6f), Tessellation = new Int2(ClothTessellation), Normal = NormalDirection.UpZ, GenerateBackFace = true };
            cloths[i] = new ClothComponent { Pins = { new SoftBodyPin { Region = new BoundingBox(new Vector3(-1f, 0.79f, -1f), new Vector3(1f, 0.81f, 1f)) } } };
            var cloth = new Entity("dense cloth") { new ModelComponent(Look.Model(game, plane, i == 0 ? Look.Teal : Look.Coral, 0.4f)), cloths[i] };
            cloth.Transform.Position = o + new Vector3(x, 2.2f, -1.6f);
            game.AddToScene(cloth);
        }

        // A long paddle sweeping slowly at ground level, so the spheres keep rolling and deforming
        var size = new Vector3(5.5f, 0.3f, 0.2f);
        var paddle = Kit.Kinematic(game, "paddle", new CubeProceduralModel { Size = size }, new BoxCollider { Size = size }, Look.Paint(game.GraphicsDevice, Look.Wood, 0.6f), o + new Vector3(0f, 0.25f, 0f)).Get<BodyComponent>()!;
        game.AddToScene(new Entity("paddle and gusts")
        {
            new StepDriver((time, _) =>
            {
                paddle.SetTargetPose(o + new Vector3(0f, 0.25f, 0f), Quaternion.RotationY(time * 0.4f));
                for (int i = 0; i < cloths.Length; i++)
                    cloths[i].Wind = new Vector3(2f * MathF.Sin(time * 0.5f + i), 0.5f, 6f + 4f * MathF.Sin(time * 0.9f + i * 2f));
            }),
        });
    }

    public override IEnumerable<SceneLabel> Labels()
    {
        yield return new(Origin + new Vector3(0f, 4.4f, 0f), "GPU deformation: many vertices, few particles");
    }

    public override IEnumerable<string> Status()
    {
        yield return string.Format(CultureInfo.InvariantCulture, "{0} spheres x {1} vertices, lattice {2}  |  two {3}x{3} cloths", Rows * Columns, SphereVertices, LatticeResolution, ClothTessellation);
    }
}
