// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Rendering;

namespace Stride.BepuPhysics.Soft.Topology;

/// <summary>
/// Reads the world matrices of the nodes of an entity's model, such as the bones of an animated skeleton.
/// </summary>
internal static class ModelNodes
{
    /// <summary> The skeleton of the entity's model with its world matrices recomputed from the current transform and node poses, null without one </summary>
    /// <remarks> The model only does it before drawing, the physics step would otherwise read the poses of the previous frame </remarks>
    public static SkeletonUpdater? Refresh(Entity entity)
    {
        if (entity.Get<ModelComponent>()?.Skeleton is not { } skeleton || skeleton.NodeTransformations.Length == 0)
            return null;

        entity.Transform.UpdateWorldMatrix();
        skeleton.NodeTransformations[0].LocalMatrix = entity.Transform.WorldMatrix;
        skeleton.UpdateMatrices();
        return skeleton;
    }

    /// <summary> The index of the node called <paramref name="name"/>, or -1 </summary>
    public static int Find(SkeletonUpdater skeleton, string name)
    {
        var nodes = skeleton.Nodes;
        for (int i = 0; i < nodes.Length; i++)
        {
            if (nodes[i].Name == name)
                return i;
        }
        return -1;
    }

    /// <summary> The bind pose of every node in the space of the model, the root node being the model's own space as for its meshes </summary>
    public static Matrix[] BindPose(Skeleton skeleton)
    {
        var nodes = skeleton.Nodes;
        var transforms = new Matrix[nodes.Length];
        if (nodes.Length == 0)
            return transforms;

        transforms[0] = Matrix.Identity;
        for (int i = 1; i < nodes.Length; i++)
        {
            var node = nodes[i];
            Matrix.Transformation(ref node.Transform.Scale, ref node.Transform.Rotation, ref node.Transform.Position, out var local);
            transforms[i] = node.ParentIndex >= 0 ? local * transforms[node.ParentIndex] : local;
        }
        return transforms;
    }
}
