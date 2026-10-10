// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Graphics.GeometricPrimitives;
using Stride.Rendering.ProceduralModels;

namespace Stride.Harness.Demo;

/// <summary>
/// A cube whose faces are grids of <see cref="Subdivisions"/> × <see cref="Subdivisions"/> quads, with the faces, winding
/// and texture coordinates of Stride's cube; a deformed mesh needs vertices inside the faces to bend.
/// </summary>
public class SubdividedCubeProceduralModel : PrimitiveProceduralModelBase
{
    private static readonly Vector3[] FaceNormals =
    [
        new(0, 0, 1), new(0, 0, -1), new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0),
    ];

    public Vector3 Size { get; set; } = Vector3.One;

    /// <summary> Quads along each edge of a face </summary>
    public int Subdivisions { get; set; } = 16;

    /// <summary> Triangles of the generated mesh </summary>
    public int TriangleCount => 6 * Subdivisions * Subdivisions * 2;

    protected override GeometricMeshData<VertexPositionNormalTexture> CreatePrimitiveMeshData()
    {
        int n = Subdivisions, side = n + 1;
        var vertices = new VertexPositionNormalTexture[6 * side * side];
        var indices = new int[6 * n * n * 6];
        var half = Size / 2f;
        int vertex = 0, index = 0;

        for (int face = 0; face < 6; face++)
        {
            var normal = FaceNormals[face];
            var basis = face >= 4 ? Vector3.UnitZ : Vector3.UnitY;
            var side1 = Vector3.Cross(normal, basis);
            var side2 = Vector3.Cross(normal, side1);

            // Corner (a, b) of a face: a goes along side1, b along side2, as the four corners of Stride's cube
            int first = vertex;
            for (int i = 0; i <= n; i++)
            {
                for (int j = 0; j <= n; j++)
                {
                    float a = (float)i / n, b = (float)j / n;
                    var position = (normal + side1 * (2f * a - 1f) + side2 * (2f * b - 1f)) * half;
                    vertices[vertex++] = new VertexPositionNormalTexture(position, normal, new Vector2((1f - a) * UvScale.X, b * UvScale.Y));
                }
            }

            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++)
                {
                    int v00 = first + i * side + j, v01 = v00 + 1, v10 = v00 + side, v11 = v10 + 1;
                    indices[index++] = v00;
                    indices[index++] = v01;
                    indices[index++] = v11;
                    indices[index++] = v00;
                    indices[index++] = v11;
                    indices[index++] = v10;
                }
            }
        }

        return new GeometricMeshData<VertexPositionNormalTexture>(vertices, indices, isLeftHanded: false) { Name = "SubdividedCube" };
    }
}
