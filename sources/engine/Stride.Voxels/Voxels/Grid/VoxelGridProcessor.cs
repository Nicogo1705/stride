// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Games;
using Stride.Graphics;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;
using GraphicsBuffer = Stride.Graphics.Buffer;

namespace Stride.Rendering.Voxels.Grid
{
    /// <summary>
    /// Keeps a <see cref="VoxelGridComponent"/>'s model and material in step with its field.
    /// </summary>
    public sealed class VoxelGridProcessor : EntityProcessor<VoxelGridComponent, VoxelGridProcessor.State>
    {
        public class State
        {
            public ModelComponent Model;
            public Material Material;
            public MaterialVoxelSurfaceFeature Surface;
            public Int3 SampleCount;
            public float CellSize;
            public int ExtentRevision = -1;
        }

        protected override State GenerateComponentData(Entity entity, VoxelGridComponent component) => new();

        protected override void OnEntityComponentRemoved(Entity entity, VoxelGridComponent component, State state)
        {
            if (state.Model?.Entity is { } carrier && carrier != entity)
                entity.RemoveChild(carrier);
            state.Model = null;
        }

        public override void Update(GameTime time)
        {
            var device = Services.GetService<IGraphicsDeviceService>()?.GraphicsDevice;
            if (device == null)
                return;

            foreach (var pair in ComponentDatas)
            {
                var component = pair.Key;
                var state = pair.Value;

                if (!component.Enabled || component.Traversal?.Source == null)
                {
                    if (state.Model != null)
                        state.Model.Enabled = false;
                    continue;
                }

                EnsureModel(device, component, state);
                state.Model.Enabled = true;
                state.Model.IsShadowCaster = component.CastShadows;

                // On the material's own pass, every frame, under the fixed names the shaders link
                // to. This is the collection the mesh render feature reads; what was set on the
                // generator's parameters at build time is not guaranteed to have been carried over,
                // and a value that is not here is not bound.
                state.Surface?.ApplyParameters(state.Material.Passes[0].Parameters);

            }
        }

        private static void EnsureModel(GraphicsDevice device, VoxelGridComponent component, State state)
        {
            var samples = component.Traversal.Source.SampleCount;
            var cellSize = component.Traversal.CellSize;

            var wanted = component.Material;
            if (state.Material == null || (wanted != null && state.Material != wanted))
            {
                // Held, not looked up afterwards. A material built here keeps no descriptor - the
                // one handed to Material.New is consumed and dropped - so asking the finished
                // material what features it was made from silently answers nothing, the parameters
                // are never applied, and the shader traces a field whose dimensions are zero. It
                // then reports a hit at no distance at all, writes depth at the eye across the whole
                // box, and every other thing in the scene fails the depth test behind it. A grey
                // screen, from a null reference nobody dereferenced.
                if (wanted != null)
                {
                    state.Material = wanted;
                    state.Surface = wanted.Descriptor?.Attributes?.Surface as MaterialVoxelSurfaceFeature;
                }
                else
                {
                    state.Material = BuildDefaultMaterial(device, component, out var built);
                    state.Surface = built;
                }



                state.Model = null;

                // The far faces are the ones kept, not the near ones.
                //
                // The ray from the eye through a fragment is the same ray whichever face carries the
                // fragment, so either set of faces serves - but only the far set is still there when
                // the camera stands inside the volume, which for a voxel world is most of the time.
                // Set on the pass rather than by winding the mesh inside out, so a material the user
                // supplies behaves the same as the one built here.
                foreach (var pass in state.Material.Passes)
                    pass.CullMode = CullMode.Front;
            }

            // The box stands in for the volume, so it is rebuilt only when the volume's extent moves.
            var stale = state.Model == null
                        || state.SampleCount != samples
                        || state.CellSize != cellSize
                        || state.ExtentRevision != component.ExtentRevision;
            if (!stale)
                return;

            state.SampleCount = samples;
            state.CellSize = cellSize;
            state.ExtentRevision = component.ExtentRevision;

            var extent = new Vector3(samples.X - 1, samples.Y - 1, samples.Z - 1) * cellSize;

            var model = new Model { state.Material };
            model.Add(BuildBoxMesh(device, extent, out var bounds, out var sphere));
            model.BoundingBox = bounds;
            model.BoundingSphere = sphere;

            if (state.Model == null)
            {
                // A child entity of its own, rather than a ModelComponent on the grid's entity. That
                // entity is the user's - it commonly already carries a collider, and may carry a
                // model the user put there - and quietly taking over its ModelComponent is both a
                // collision and a thing that is hard to see from the outside.
                var carrier = new Entity("VoxelGridModel");
                state.Model = carrier.GetOrCreate<ModelComponent>();
                component.Entity.AddChild(carrier);
            }

            state.Model.Model = model;

            // A ray that crosses the volume corner to corner has gone as far as it can, so the
            // diagonal is the honest ceiling. Left unset the feature would ask for no limit, and
            // "no limit" reaches the traversal as a number large enough to lose precision in its
            // own arithmetic - the surface is then found only where it nearly touches the camera,
            // and the grid appears to exist only while you stand inside it.
            if (state.Surface != null && state.Surface.MaxDistance <= 0)
                state.Surface.MaxDistance = extent.Length();

        }

        /// <summary>
        /// The volume's bounds, as geometry for a ray to be found through.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Built the way Stride builds a procedural model, rather than by hand. A mesh assembled with
        /// a bare position-normal-texture layout has no tangent, no generated bounding sphere and no
        /// second texture coordinate, and the mesh render feature simply does not draw it - no error,
        /// no warning, nothing on screen. That is indistinguishable from a face wound the wrong way
        /// or from a ray that finds nothing, and all three were suspected in turn before the vertex
        /// layout was.
        /// </para>
        /// <para>
        /// </para>
        /// </remarks>
        private static Mesh BuildBoxMesh(GraphicsDevice device, Vector3 extent, out BoundingBox bounds, out BoundingSphere sphere)
        {
            var vertices = new VertexPositionNormalTexture[8];
            for (int corner = 0; corner < 8; ++corner)
            {
                var position = new Vector3(
                    (corner & 1) != 0 ? extent.X : 0,
                    (corner & 2) != 0 ? extent.Y : 0,
                    (corner & 4) != 0 ? extent.Z : 0);

                // Normals and texture coordinates that are merely not degenerate: the surface shader
                // replaces the frame with the traced one, but tangent generation divides by the
                // spread of the texture coordinates and hands back NaN when every vertex shares one.
                vertices[corner] = new VertexPositionNormalTexture(
                    position,
                    Vector3.Normalize(position - extent * 0.5f),
                    new Vector2((corner & 1) != 0 ? 1 : 0, (corner & 2) != 0 ? 1 : 0));
            }

            // Corner order matches the bit pattern above: bit 0 is +X, bit 1 +Y, bit 2 +Z.
            int[] faces =
            [
                0, 1, 2, 1, 3, 2, // -Z
                4, 6, 5, 5, 6, 7, // +Z
                0, 4, 1, 1, 4, 5, // -Y
                2, 3, 6, 3, 7, 6, // +Y
                0, 2, 4, 2, 6, 4, // -X
                1, 5, 3, 3, 5, 7, // +X
            ];

            // One winding. Which faces survive is the material pass's cull mode, set where the
            // material is built: the far ones, so the volume is drawn from inside as well as from
            // outside at the cost of one walk per pixel rather than two.
            var indices = faces;

            bounds = new BoundingBox(Vector3.Zero, extent);
            unsafe
            {
                fixed (void* positions = vertices)
                    BoundingSphere.FromPoints((IntPtr)positions, 0, vertices.Length, VertexPositionNormalTexture.Size, out sphere);
            }

            var withTangents = VertexHelper.GenerateTangentBinormal(vertices[0].GetLayout(), vertices, indices);
            var complete = VertexHelper.GenerateMultiTextureCoordinates(withTangents, 0, 0);

            var indicesShort = new ushort[indices.Length];
            for (int i = 0; i < indicesShort.Length; ++i)
                indicesShort[i] = (ushort)indices[i];

            var vertexBuffer = GraphicsBuffer.New(device, complete.VertexBuffer, BufferFlags.VertexBuffer, GraphicsResourceUsage.Default);
            var indexBuffer = GraphicsBuffer.Index.New(device, indicesShort);

            return new Mesh
            {
                MaterialIndex = 0,
                BoundingBox = bounds,
                BoundingSphere = sphere,
                Draw = new MeshDraw
                {
                    PrimitiveType = PrimitiveType.TriangleList,
                    DrawCount = indices.Length,
                    IndexBuffer = new IndexBufferBinding(indexBuffer, false, indices.Length),
                    VertexBuffers = [new VertexBufferBinding(vertexBuffer, complete.Layout, vertices.Length)],
                },
            };
        }

        /// <summary>
        /// Enough material to see the field by, carrying its own colours and nothing else.
        /// </summary>
        /// <remarks>
        /// The environment function is the polynomial approximation rather than the lookup table: the
        /// table is a texture the pipeline binds for a material that came from an asset, and a
        /// material built here at runtime has none - which leaves environment specular at zero and
        /// metal reading as black.
        /// </remarks>
        private static Material BuildDefaultMaterial(GraphicsDevice device, VoxelGridComponent component, out MaterialVoxelSurfaceFeature surface)
        {
            surface = new MaterialVoxelSurfaceFeature { Traversal = component.Traversal };
            var descriptor = new MaterialDescriptor
            {
                Attributes =
                {
                    Surface = surface,

                    // The diffuse slot asks the field for its colour, rather than being handed a
                    // constant. A material needs a diffuse feature at all - without one it generates
                    // no valid shading - and whatever is in that slot runs after the surface feature
                    // and wins, so a constant there is a constant everywhere and the palette never
                    // survives. Asking is also how a user replaces it: put a texture in this slot
                    // instead and the field's colours are simply not consulted.
                    Diffuse = new MaterialDiffuseMapFeature(new ComputeShaderClassColor { MixinReference = "ComputeColorVoxelAlbedo" }),
                    DiffuseModel = new MaterialDiffuseLambertModelFeature(),
                    Specular = new MaterialMetalnessMapFeature(new ComputeFloat(0f)),
                    SpecularModel = new MaterialSpecularMicrofacetModelFeature
                    {
                        Environment = new MaterialSpecularMicrofacetEnvironmentGGXPolynomial(),
                    },
                    MicroSurface = new MaterialGlossinessMapFeature(new ComputeFloat(0.35f)),
                },
            };

            return Material.New(device, descriptor);
        }
    }
}
