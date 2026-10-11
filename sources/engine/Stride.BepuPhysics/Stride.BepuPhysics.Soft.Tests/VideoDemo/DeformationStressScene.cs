// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Collections.Generic;
using System.Globalization;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Rendering.ProceduralModels;

namespace Stride.BepuPhysics.Soft.Tests.VideoDemo;

/// <summary>
/// Many vertices for few particles: high-poly spheres over a coarse lattice, the case where deforming on the GPU pays the most,
/// and two dense cloths whose vertices are their particles
/// </summary>
internal sealed class DeformationStressScene : VideoScene
{
    public const int Rows = 3, Columns = 4, SphereTessellation = 192, LatticeResolution = 3, ClothTessellation = 40;

    // As SphereProceduralModel builds it: rings from pole to pole, twice as many segments around
    private const int SphereVertices = (SphereTessellation + 1) * (2 * SphereTessellation + 1);

    public override string Group => "Bench";
    public override string Title => "Deformation stress";
    public override string Pr => "GPU deformation (upcoming PR, on #3528)";
    public override string Description => $"{Rows * Columns} spheres of {SphereVertices} vertices on a {LatticeResolution}-cell lattice, two {ClothTessellation}x{ClothTessellation} cloths: F4 switches GPU / CPU deformation";
    public override Vector3 Eye => new(0f, 4.2f, 8.4f);
    public override Vector3 Target => new(0f, 0.6f, 0f);

    protected override void BuildScene(VideoDemoGame game)
    {
        for (int row = 0; row < Rows; row++)
        for (int column = 0; column < Columns; column++)
        {
            var sphere = new Entity("dense sphere")
            {
                new ModelComponent(Look.Model(game, new SphereProceduralModel { Radius = 0.45f, Tessellation = SphereTessellation }, Look.Accents[(row * Columns + column) % Look.Accents.Length], 0.6f)),
                new VolumetricSoftBodyComponent { Resolution = LatticeResolution, Stiffness = 6f, ShearStiffness = 6f, Damping = 0.2f },
            };
            sphere.Transform.Position = new Vector3((column - (Columns - 1) / 2f) * 1.3f, 0.8f + row * 1.1f, (row - 1) * 1.2f);
            game.AddToScene(sphere);
        }

        for (int i = 0; i < 2; i++)
        {
            var plane = new PlaneProceduralModel { Size = new Vector2(1.6f), Tessellation = new Int2(ClothTessellation), Normal = Graphics.GeometricPrimitives.NormalDirection.UpZ, GenerateBackFace = true };
            var cloth = new Entity("dense cloth")
            {
                new ModelComponent(Look.Model(game, plane, i == 0 ? Look.Teal : Look.Coral, 0.4f)),
                new ClothComponent { Pins = { new SoftBodyPin { Region = new BoundingBox(new Vector3(-1f, 0.79f, -1f), new Vector3(1f, 0.81f, 1f)) } } },
            };
            cloth.Transform.Position = new Vector3(i == 0 ? -3.8f : 3.8f, 2.2f, -1.6f);
            game.AddToScene(cloth);
        }
    }

    public override IEnumerable<string> Status()
    {
        yield return string.Format(CultureInfo.InvariantCulture, "{0} spheres x {1} vertices, lattice {2}", Rows * Columns, SphereVertices, LatticeResolution);
    }
}
