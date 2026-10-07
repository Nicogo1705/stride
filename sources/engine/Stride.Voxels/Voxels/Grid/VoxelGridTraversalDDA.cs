// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core;
using Stride.Rendering;
using Stride.Shaders;

namespace Stride.Rendering.Voxels.Grid
{
    /// <summary>
    /// How a ray finds the surface of a voxel grid.
    /// </summary>
    /// <remarks>An interface so that other tracers (a distance field, hardware ray tracing) can implement the same shader with the callers unchanged.</remarks>
    public interface IVoxelGridTraversal
    {
        /// <summary>Where the samples come from. Its shader is mixed in beside the traversal's.</summary>
        IVoxelGridSource Source { get; set; }

        /// <summary>Edge length of one cell, in the grid's own space.</summary>
        float CellSize { get; }

        /// <summary>The shader implementing <c>IVoxelGridTraversal</c> together with its source, as mixins. It changes with any permutation, such as the surface form, so a consumer compares it frame to frame.</summary>
        ShaderSource GetShaderSource();

        /// <summary>Writes this traversal's parameters, and its source's, under <see cref="VoxelGridFieldKeys"/>.</summary>
        void ApplyParameters(ParameterCollection parameters);
    }

    /// <summary>Which surface a traversal stops on.</summary>
    public enum VoxelSurfaceForm
    {
        /// <summary>The cells themselves. A world that is meant to look like cubes.</summary>
        Cubes,

        /// <summary>The crossing on the trilinear field, where marching cubes puts its vertices: the surface a marching-cubes mesh or collider has.</summary>
        MarchingCubes,

        /// <summary>The facet about the cell's surface-nets vertex, as surface nets meshes it. A different surface from <see cref="MarchingCubes"/>.</summary>
        SurfaceNets,
    }

    /// <summary>Walks the cells a ray crosses, in order, and stops at the first solid one.</summary>
    /// <remarks>
    /// Needs nothing but the samples and runs on every graphics API. Not sphere tracing: a density field only
    /// guarantees the sign. Empty space is skipped by the min/max pyramid in <see cref="Occupancy"/>, brick by brick.
    /// </remarks>
    [DataContract(DefaultMemberMode = DataMemberMode.Default)]
    [Display("DDA")]
    public class VoxelGridTraversalDDA : IVoxelGridTraversal
    {
        /// <summary>Where the samples come from.</summary>
        public IVoxelGridSource Source { get; set; } = new VoxelGridSourceTexture3D();

        /// <summary>Edge length of one cell, in the grid's local space.</summary>
        public float CellSize { get; set; } = 1.0f;

        /// <summary>Density at or above which a sample counts as solid. Match the value the game meshes and collides with.</summary>
        public float IsoLevel { get; set; } = 0.5f;

        /// <summary>Reads the samples on the outer faces of the grid as air, so the volume closes with real surface rather than the faces of its box.</summary>
        /// <remarks>Match the collider's setting of the same name. Turn both off where the field continues into a neighbour.</remarks>
        public bool SealBorder { get; set; } = true;

        /// <summary>Which surface the walk stops on. All three walk the same cells.</summary>
        /// <remarks>Match the collider's form: surface nets is a different surface from cubes and marching cubes.</remarks>
        public VoxelSurfaceForm Surface { get; set; } = VoxelSurfaceForm.MarchingCubes;

        /// <summary>Ceiling on the cells one ray may visit; bounds the worst case, not the common one.</summary>
        public int MaxSteps { get; set; } = 512;

        /// <summary>The min/max pyramid over the field, letting a ray leap over bricks that hold no surface. Optional: without it every cell on the ray is visited.</summary>
        /// <remarks>Owned by whoever owns the samples, since it is rebuilt from them for the region an edit touched.</remarks>
        [DataMemberIgnore]
        public VoxelGridOccupancy Occupancy { get; set; }


        /// <summary>The traversal shader with its source mixed in, generic on the surface mode.</summary>
        public ShaderSource GetShaderSource()
        {
            // Mixed beside its source rather than composing it, so both share one scope and the field's resource is
            // declared once. The surface form is a generic argument, so only the branch asked for is compiled.
            var mixin = new ShaderMixinSource();
            mixin.Mixins.Add(new ShaderClassSource("VoxelGridTraversalDDA", (int)Surface));
            if (Source.GetShaderSource() is ShaderClassSource sourceClass)
                mixin.Mixins.Add(sourceClass);
            return mixin;
        }

        /// <summary>Binds the source, the occupancy pyramid and the walk's limits.</summary>
        public void ApplyParameters(ParameterCollection parameters)
        {
            parameters.Set(VoxelGridFieldKeys.CellSize, CellSize);
            parameters.Set(VoxelGridFieldKeys.IsoLevel, IsoLevel);
            parameters.Set(VoxelGridFieldKeys.SealBorder, SealBorder ? 1f : 0f);
            parameters.Set(VoxelGridFieldKeys.MaxSteps, MaxSteps);
            parameters.Set(VoxelGridFieldKeys.Occupancy, Occupancy?.Texture);
            parameters.Set(VoxelGridFieldKeys.OccupancyLevels, Occupancy?.Levels ?? 0);
            Source.ApplyParameters(parameters);
        }
    }
}
