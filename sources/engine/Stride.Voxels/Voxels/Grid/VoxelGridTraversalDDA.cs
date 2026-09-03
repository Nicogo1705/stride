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

        /// <summary>Edge length of one cell, in the grid's own space.</summary>
        /// <remarks>
        /// On the interface because it is the field's extent rather than a detail of how a ray finds
        /// the surface: whatever draws or bounds the grid needs it, and every traversal has one.
        /// </remarks>
        float CellSize { get; }

        /// <summary>
        /// The shader implementing <c>IVoxelGridTraversal</c> together with its source, as mixins to
        /// add beside whatever consumes them. It changes with anything that is a permutation rather
        /// than a parameter - the surface form is one - so a consumer compares it frame to frame.
        /// </summary>
        ShaderSource GetShaderSource();

        /// <summary>Writes this traversal's parameters, and its source's, under <see cref="VoxelGridFieldKeys"/>.</summary>
        void ApplyParameters(ParameterCollection parameters);
    }

    /// <summary>Which surface a traversal stops on.</summary>
    public enum VoxelSurfaceForm
    {
        /// <summary>The cells themselves. A world that is meant to look like cubes.</summary>
        Cubes,

        /// <summary>
        /// The crossing on the trilinear field, which is where marching cubes puts its vertices - so
        /// this is the surface a marching-cubes mesh or collider has.
        /// </summary>
        MarchingCubes,

        /// <summary>
        /// The facet about the cell's surface-nets vertex, which is what surface nets meshes. It
        /// smooths the crossings rather than sitting on them, so it is a different surface from
        /// <see cref="MarchingCubes"/> - which is exactly why it is worth being able to ask for it.
        /// </summary>
        SurfaceNets,
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
        /// Reads the samples on the outer faces of the grid as air, so the volume closes on itself
        /// with real surface rather than with the faces of its own bounding box.
        /// </summary>
        /// <remarks>
        /// Match it to the collider's setting of the same name, or the drawn body and the solid one
        /// disagree at their edges. Turn both off where the field continues into a neighbour.
        /// </remarks>
        public bool SealBorder { get; set; } = true;

        /// <summary>
        /// Which surface the walk stops on. All three walk the same cells.
        /// </summary>
        /// <remarks>
        /// Worth matching to the collider's form: cubes with a box collider, and marching cubes with
        /// a marching-cubes collider, are the same surface. Surface nets is a different one, so
        /// pairing it with either of the others draws one body and collides with another.
        /// </remarks>
        public VoxelSurfaceForm Surface { get; set; } = VoxelSurfaceForm.MarchingCubes;

        /// <summary>
        /// Ceiling on the cells one ray may visit. A ray crossing a 256 cell grid corner to corner
        /// touches on the order of 768, so this bounds the worst case rather than the common one.
        /// </summary>
        public int MaxSteps { get; set; } = 512;

        public ShaderSource GetShaderSource()
        {
            // Mixed beside its source rather than composing it, so both share one scope and the
            // field's resource is declared once. The surface form is a generic argument, not a
            // parameter: only the branch asked for is compiled, and the other two - surface nets
            // alone is several hundred reads - never reach the shader at all.
            var mixin = new ShaderMixinSource();
            mixin.Mixins.Add(new ShaderClassSource("VoxelGridTraversalDDA", (int)Surface));
            if (Source.GetShaderSource() is ShaderClassSource sourceClass)
                mixin.Mixins.Add(sourceClass);
            return mixin;
        }

        public void ApplyParameters(ParameterCollection parameters)
        {
            parameters.Set(VoxelGridFieldKeys.CellSize, CellSize);
            parameters.Set(VoxelGridFieldKeys.IsoLevel, IsoLevel);
            parameters.Set(VoxelGridFieldKeys.SealBorder, SealBorder ? 1f : 0f);
            parameters.Set(VoxelGridFieldKeys.MaxSteps, MaxSteps);
            Source.ApplyParameters(parameters);
        }
    }
}
