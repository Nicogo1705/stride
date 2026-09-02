// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core;
using Stride.Shaders;

namespace Stride.Rendering.Voxels.Grid
{
    /// <summary>
    /// How a ray finds the surface of a voxel grid.
    /// </summary>
    /// <remarks>
    /// An interface rather than a function because of what comes after it: a hardware ray tracing
    /// path implements the same shader and everything above stays as it is, and so does a distance
    /// field tracer taking larger steps through empty space. They differ only in how they skip
    /// nothing, which is exactly what an interface should hide.
    /// </remarks>
    public interface IVoxelGridTraversal
    {
        /// <summary>Where the samples come from. Composed into the traversal shader.</summary>
        IVoxelGridSource Source { get; set; }

        /// <summary>The shader implementing <c>IVoxelGridTraversal</c>, source already composed in.</summary>
        ShaderSource GetShaderSource();

        /// <summary>
        /// Recomputes the parameter keys for this traversal and its source at the given composition
        /// path. Call before <see cref="ApplyParameters"/> whenever the path changes.
        /// </summary>
        void UpdateLayout(string compositionName);

        /// <summary>Writes this traversal's parameters, and its source's, into a collection.</summary>
        void ApplyParameters(ParameterCollection parameters);
    }

    /// <summary>
    /// Walks the cells a ray crosses, in order, and stops at the first solid one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It needs nothing but the occupancy - no acceleration structure to build, no distance field to
    /// precompute, nothing to rebuild when a voxel changes - and it is exact, because a regular grid
    /// already is the structure a ray wants. It also runs on every graphics API, which a hardware
    /// traced path cannot claim.
    /// </para>
    /// <para>
    /// Not sphere tracing, deliberately: that wants a distance correct everywhere, and a marching
    /// cubes density field only guarantees the sign. When empty space becomes the cost, the answer
    /// is a coarse occupancy pyramid to skip whole bricks, not a change of algorithm.
    /// </para>
    /// </remarks>
    [DataContract(DefaultMemberMode = DataMemberMode.Default)]
    [Display("DDA")]
    public class VoxelGridTraversalDDA : IVoxelGridTraversal
    {
        /// <summary>Where the samples come from.</summary>
        public IVoxelGridSource Source { get; set; } = new VoxelGridSourceTexture3D();

        /// <summary>Edge length of one cell, in the grid's local space.</summary>
        public float CellSize { get; set; } = 1.0f;

        /// <summary>
        /// Density at or above which a sample counts as solid. Match the value the rest of the game
        /// meshes and collides with, or the surfaces will not agree.
        /// </summary>
        public float IsoLevel { get; set; } = 0.5f;

        /// <summary>
        /// Whether the crossing is solved inside the cell that contains it.
        /// </summary>
        /// <remarks>
        /// On, the surface matches the one a renderer or a collider meshes from the same samples,
        /// because it is the same trilinear reconstruction. Off, the ray stops at the cell and the
        /// world is made of cubes - not a lesser answer, a different one, and the right one for a
        /// game that means its blocks. Both walk the same cells.
        /// </remarks>
        public bool Smooth { get; set; } = true;

        /// <summary>
        /// Ceiling on the cells one ray may visit. A ray crossing a 256 cell grid corner to corner
        /// touches on the order of 768, so this bounds the worst case rather than the common one.
        /// </summary>
        public int MaxSteps { get; set; } = 512;

        private ValueParameterKey<float> cellSizeKey;
        private ValueParameterKey<float> isoLevelKey;
        private ValueParameterKey<float> smoothKey;
        private ValueParameterKey<int> maxStepsKey;

        public ShaderSource GetShaderSource()
        {
            var mixin = new ShaderMixinSource();
            mixin.Mixins.Add(new ShaderClassSource("VoxelGridTraversalDDA"));
            mixin.AddComposition("Source", Source.GetShaderSource());
            return mixin;
        }

        public void UpdateLayout(string compositionName)
        {
            cellSizeKey = VoxelGridTraversalDDAKeys.VoxelGridCellSize.ComposeWith(compositionName);
            isoLevelKey = VoxelGridTraversalDDAKeys.VoxelGridIsoLevel.ComposeWith(compositionName);
            smoothKey = VoxelGridTraversalDDAKeys.VoxelGridSmoothSurface.ComposeWith(compositionName);
            maxStepsKey = VoxelGridTraversalDDAKeys.VoxelGridMaxSteps.ComposeWith(compositionName);
            Source.UpdateLayout("Source." + compositionName);
        }

        public void ApplyParameters(ParameterCollection parameters)
        {
            parameters.Set(cellSizeKey, CellSize);
            parameters.Set(isoLevelKey, IsoLevel);
            parameters.Set(smoothKey, Smooth ? 1f : 0f);
            parameters.Set(maxStepsKey, MaxSteps);
            Source.ApplyParameters(parameters);
        }
    }
}
