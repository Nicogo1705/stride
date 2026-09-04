// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Collections.Generic;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Engine;
using Stride.Engine.Design;
using Stride.Rendering.Materials;

namespace Stride.Rendering.Voxels.Grid
{
    /// <summary>How a boundary between two materials is shared out between pixels.</summary>
    public enum VoxelMaterialDither
    {
        /// <summary>Every pixel goes to the material that weighs most at the point: a hard line that follows the cells.</summary>
        Sharp = 0,

        /// <summary>A 4x4 ordered dither: a blend at a distance, a visible 4 pixel tile up close.</summary>
        Bayer4x4 = 1,

        /// <summary>An 8x8 ordered dither: finer steps, an 8 pixel tile up close.</summary>
        Bayer8x8 = 2,

        /// <summary>Interleaved gradient noise: no tile, a grain that temporal antialiasing takes off best.</summary>
        InterleavedGradientNoise = 3,
    }

    /// <summary>
    /// Draws a voxel field as a body of the scene, with the materials of the scene.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The field is walked once per pixel by a resolve pass, which leaves where the surface is,
    /// which way it faces and which material it carries. Each material in <see cref="Materials"/>
    /// is then drawn as itself: an ordinary material, taken as compiled, put over the pixels that
    /// are its own. Lights, shadows, fog, screen space effects and the depth test against other
    /// geometry are not reimplemented for the field; the field simply goes down the same path.
    /// </para>
    /// <para>
    /// The samples come from <see cref="Traversal"/>'s source; a sample's material byte is an index
    /// into <see cref="Materials"/>.
    /// </para>
    /// </remarks>
    [DataContract("VoxelGridComponent")]
    [Display("Voxel Grid", Expand = ExpandRule.Once)]
    [DefaultEntityComponentProcessor(typeof(VoxelGridProcessor), ExecutionMode = ExecutionMode.All)]
    [ComponentCategory("Model")]
    public sealed class VoxelGridComponent : ActivableEntityComponent
    {
        /// <summary>How a ray finds the surface, and where the samples come from.</summary>
        /// <userdoc>How the surface is found in the field, and where the field's samples come from.</userdoc>
        [DataMember(10)]
        public IVoxelGridTraversal Traversal { get; set; } = new VoxelGridTraversalDDA();

        /// <summary>
        /// One material for the whole field, whatever the samples say. Leave empty to draw each
        /// id with the material of the same index in <see cref="Materials"/>.
        /// </summary>
        /// <userdoc>One material for the whole field. Leave empty to use the list of materials by id.</userdoc>
        [DataMember(15)]
        public Material Material { get; set; }

        /// <summary>
        /// The materials the samples point at, by id: a sample whose material byte is 3 is drawn
        /// with the fourth material here. Up to 256, authored like any other material and drawn as
        /// themselves - every feature of the material, not a copy of its numbers.
        /// </summary>
        /// <remarks>
        /// Each material is one draw of the field's proxy box over the pixels the resolve pass gave
        /// to its id, so the count of materials is the count of draws; each is cheap, a read and a
        /// compare per pixel for the pixels that are not its own. Texture coordinates are the proxy
        /// box's, which a texture in a material will show; a material meant for a field maps by
        /// world position.
        /// </remarks>
        /// <userdoc>The materials the samples point at, by id. A sample's material byte is an index into this list.</userdoc>
        [DataMember(17)]
        public List<Material> Materials { get; } = [];

        /// <summary>How a boundary between two materials is shared out between pixels.</summary>
        /// <userdoc>How the boundary between two materials is drawn: a hard line, or a dither that reads as a blend.</userdoc>
        [DataMember(18)]
        public VoxelMaterialDither Dither { get; set; } = VoxelMaterialDither.InterleavedGradientNoise;

        /// <summary>
        /// Whether a voxel GI volume takes the field straight from its samples, instead of
        /// rasterising the field's proxy box through the voxelizer, walking a ray per fragment.
        /// </summary>
        /// <remarks>
        /// What reaches the GI this way is what the field's materials emit, and its occlusion; the
        /// voxelizer would also carry the sun's light off the field. A world lit by what emits loses
        /// nothing and saves the walks.
        /// </remarks>
        /// <userdoc>Let a voxel GI volume read the field's samples directly, rather than voxelizing the field like a mesh. Carries emission and occlusion; not direct light.</userdoc>
        [DataMember(19)]
        public bool InjectIntoGI { get; set; } = true;

        /// <summary>
        /// With <see cref="InjectIntoGI"/>, how much of the previous frame's indirect light the
        /// field's colour sends back into the GI; 0 injects emission alone. The GI reads this back
        /// next frame, so with the volume's own bounce the product must stay under one or the
        /// field lights itself up without end.
        /// </summary>
        /// <userdoc>How much indirect light the field bounces back into the GI when injected. 0 carries only what it emits.</userdoc>
        [DataMember(20)]
        [DataMemberRange(0.0, 4.0, 0.05, 0.25, 2)]
        public float InjectBounce { get; set; } = 0.5f;

        /// <summary>
        /// Whether the camera walks a coarser level of the field where a pixel covers more than
        /// one cell. Needs a source with coarser levels, such as a 3D texture with mips.
        /// </summary>
        /// <userdoc>Walk a coarser level of the field far from the camera, where its source holds one.</userdoc>
        [DataMember(21)]
        public bool LevelOfDetail { get; set; } = true;

        /// <summary>Bias on the level of detail, in levels: positive is coarser, negative finer.</summary>
        /// <userdoc>Level-of-detail bias in levels. Positive is coarser.</userdoc>
        [DataMember(22)]
        [DataMemberRange(-2.0, 4.0, 0.1, 0.5, 1)]
        public float LodBias { get; set; } = 0f;

        /// <summary>Whether the field writes the shadow maps. It receives shadows either way.</summary>
        /// <userdoc>Whether the field casts shadows.</userdoc>
        [DataMember(20)]
        public bool CastShadows { get; set; } = true;

        /// <summary>See <see cref="VoxelGridFieldKeys.Debug"/>.</summary>
        [DataMemberIgnore]
        public float DebugView { get; set; }

        /// <summary>
        /// Counts the times the field's extent changed, so the model can be rebuilt on an extent
        /// change that the sample count alone would not show.
        /// </summary>
        [DataMemberIgnore]
        public int ExtentRevision { get; private set; }

        /// <summary>Tells the processor the field's extent changed and the proxy box must follow.</summary>
        public void InvalidateExtent() => ExtentRevision++;
    }
}
