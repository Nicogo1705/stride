// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core.Mathematics;
using Stride.Rendering;

namespace Stride.BepuPhysics.Soft.Topology;

/// <summary>
/// Ties each particle to the two bones of a skeleton nearest to its rest position, so it can be carried along by their animation.
/// </summary>
/// <remarks>
/// A bone is the segment from a node to each of its children, or the node itself for a leaf; the root node, the model's own space, is not a bone.
/// </remarks>
internal sealed class SkeletonBinding
{
    public const int BonesPerParticle = 2;

    /// <summary> <see cref="BonesPerParticle"/> node indices per particle </summary>
    public required int[] Nodes { get; init; }

    /// <summary> <see cref="BonesPerParticle"/> weights per particle, summing to one </summary>
    public required float[] Weights { get; init; }

    /// <summary> The rest position of each particle in the space of each of its nodes, in the bind pose </summary>
    public required Vector3[] Offsets { get; init; }

    /// <summary> Null when the skeleton has no bone </summary>
    public static SkeletonBinding? Build(Skeleton skeleton, ReadOnlySpan<Vector3> restPositions)
    {
        var bind = ModelNodes.BindPose(skeleton);
        var segments = new List<(int Node, Vector3 Start, Vector3 End)>();
        var nodes = skeleton.Nodes;
        for (int i = 1; i < nodes.Length; i++)
        {
            var start = bind[i].TranslationVector;
            bool leaf = true;
            for (int child = i + 1; child < nodes.Length; child++)
            {
                if (nodes[child].ParentIndex == i)
                {
                    segments.Add((i, start, bind[child].TranslationVector));
                    leaf = false;
                }
            }
            if (leaf)
                segments.Add((i, start, start));
        }
        if (segments.Count == 0)
            return null;

        var inverseBind = new Matrix[bind.Length];
        for (int i = 0; i < bind.Length; i++)
            inverseBind[i] = Matrix.Invert(bind[i]);

        int count = restPositions.Length;
        var binding = new SkeletonBinding
        {
            Nodes = new int[count * BonesPerParticle],
            Weights = new float[count * BonesPerParticle],
            Offsets = new Vector3[count * BonesPerParticle],
        };

        for (int p = 0; p < count; p++)
        {
            var rest = restPositions[p];
            // The nearest segment, then the nearest one belonging to another node, so a particle at a joint blends both sides
            int first = -1, second = -1;
            float firstDistance = float.MaxValue, secondDistance = float.MaxValue;
            for (int s = 0; s < segments.Count; s++)
            {
                var distance = DistanceToSegment(rest, segments[s].Start, segments[s].End);
                if (distance < firstDistance)
                {
                    if (first >= 0 && segments[first].Node != segments[s].Node)
                        (second, secondDistance) = (first, firstDistance);
                    (first, firstDistance) = (s, distance);
                }
                else if (distance < secondDistance && segments[s].Node != segments[first].Node)
                {
                    (second, secondDistance) = (s, distance);
                }
            }

            // Inverse square of the distance, so the nearest bone dominates away from the joints
            float firstWeight = 1f / (firstDistance * firstDistance + 1e-8f);
            float secondWeight = second >= 0 ? 1f / (secondDistance * secondDistance + 1e-8f) : 0f;
            float total = firstWeight + secondWeight;

            int b = p * BonesPerParticle;
            var firstNode = segments[first].Node;
            var secondNode = second >= 0 ? segments[second].Node : firstNode;
            binding.Nodes[b] = firstNode;
            binding.Nodes[b + 1] = secondNode;
            binding.Weights[b] = firstWeight / total;
            binding.Weights[b + 1] = secondWeight / total;
            binding.Offsets[b] = Vector3.TransformCoordinate(rest, inverseBind[firstNode]);
            binding.Offsets[b + 1] = Vector3.TransformCoordinate(rest, inverseBind[secondNode]);
        }
        return binding;
    }

    /// <summary> Where the particle goes given the current world matrix of every node </summary>
    public Vector3 Target(int particle, ModelNodeTransformation[] nodes)
    {
        int b = particle * BonesPerParticle;
        var position = Vector3.Zero;
        for (int i = 0; i < BonesPerParticle; i++)
            position += Vector3.TransformCoordinate(Offsets[b + i], nodes[Nodes[b + i]].WorldMatrix) * Weights[b + i];
        return position;
    }

    private static float DistanceToSegment(Vector3 point, Vector3 start, Vector3 end)
    {
        var direction = end - start;
        var lengthSquared = direction.LengthSquared();
        var t = lengthSquared > 1e-12f ? Math.Clamp(Vector3.Dot(point - start, direction) / lengthSquared, 0f, 1f) : 0f;
        return Vector3.Distance(point, start + direction * t);
    }
}
