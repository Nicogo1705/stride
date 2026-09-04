// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Collections.Generic;
using Stride.Core;
using Stride.Engine;
using Stride.Engine.Design;
using Stride.Rendering.Materials;

namespace Stride.Rendering.Voxels.Grid
{
    /// <summary>
    /// A voxel grid that draws as a model: same materials, same lights, same shadows, same depth.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The entity gets an ordinary <see cref="ModelComponent"/> holding a box the size of the grid,
    /// with a material whose surface is resolved from the field rather than from the box. From the
    /// renderer's side there is nothing unusual about it: it is culled by its bounds, sorted with
    /// everything else, drawn by the mesh render feature, lit by the lights in the scene and written
    /// into the depth buffer at the depth its surface is actually at.
    /// </para>
    /// <para>
    /// So the things a user would otherwise notice as missing are not implemented here at all. Lights
    /// and shadow maps apply because the material is a material; a mesh standing inside the volume is
    /// resolved correctly because the depth is the surface's; screen space effects work because they
    /// read the same depth buffer. A pass of its own would have had to earn each of those separately.
    /// </para>
    /// <para>
    /// Give it a <see cref="Traversal"/> with a source, and set <see cref="Material"/> if the field's
    /// own palette is not the wanted look: a material of your own needs a
    /// <see cref="MaterialVoxelSurfaceFeature"/> in its surface slot, and whatever its diffuse slot
    /// holds is what colours the surface.
    /// </para>
    /// </remarks>
    [DataContract("VoxelGridComponent")]
    [Display("Voxel Grid", Expand = ExpandRule.Once)]
    [ComponentCategory("Voxels")]
    [DefaultEntityComponentProcessor(typeof(VoxelGridProcessor), ExecutionMode = ExecutionMode.All)]
    public sealed class VoxelGridComponent : ActivableEntityComponent
    {
        /// <summary>How a ray finds the surface, and where the samples come from.</summary>
        /// <remarks>
        /// Serialized, so a grid can be set up in the editor rather than only from code: the
        /// traversal's cell size, iso level and surface form are ordinary properties, and a source
        /// backed by a 3D texture is an asset like any other. A source backed by a buffer the game
        /// fills is not, and stays something code hands over at load.
        /// </remarks>
        /// <userdoc>How rays find the surface, and where the samples come from.</userdoc>
        [DataMember(10)]
        public IVoxelGridTraversal Traversal { get; set; } = new VoxelGridTraversalDDA();

        /// <summary>
        /// The material to draw with. Left null, one is built carrying nothing but the field's own
        /// colours, which is enough to see the grid but is not a material a game would ship.
        /// </summary>
        /// <userdoc>The material to draw with. Leave empty for one that shows the field's own colours.</userdoc>
        [DataMember(15)]
        public Material Material { get; set; }

        /// <summary>
        /// Whether the grid casts and receives shadows, as a model does.
        /// </summary>
        /// <userdoc>Whether the grid casts shadows, as a model does.</userdoc>
        [DataMember(20)]
        public bool CastShadows { get; set; } = true;

        /// <summary>
        /// The materials the samples point at, by id: a sample whose material byte is 3 is drawn
        /// with the fourth material here. Up to 256, authored like any other material.
        /// </summary>
        /// <remarks>
        /// What the field's shaders take from each is its colour, glossiness, metalness and
        /// emission, read off the material's constant parameters; a material fed a texture in one
        /// of those slots falls back to a default there, since a byte per sample carries nothing a
        /// texture could be looked up with.
        /// </remarks>
        /// <userdoc>The materials the samples point at, by id. A sample's material byte is an index into this list.</userdoc>
        [DataMember(25)]
        public List<Material> Materials { get; } = [];

        /// <summary>
        /// Diagnostic view of the drawn surface, see <see cref="VoxelGridFieldKeys.Debug"/>. Zero
        /// draws normally. Not saved with the scene.
        /// </summary>
        [DataMemberIgnore]
        public float DebugView { get; set; }

        /// <summary>
        /// Tells the component the field's extent changed, so the box standing in for it is rebuilt.
        /// </summary>
        /// <remarks>
        /// Not needed when samples change. The material reads the field as it draws, so digging a
        /// hole shows up in the next frame with nothing rebuilt and no material recompiled - only a
        /// grid that grew or shrank moves the box.
        /// </remarks>
        public void InvalidateExtent() => ExtentRevision++;

        [DataMemberIgnore]
        internal int ExtentRevision { get; private set; }
    }
}
