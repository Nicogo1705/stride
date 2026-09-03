// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Games;
using Stride.Graphics;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;
using Stride.Shaders;
using GraphicsBuffer = Stride.Graphics.Buffer;

namespace Stride.Rendering.Voxels.Grid
{
    /// <summary>
    /// Keeps a <see cref="VoxelGridComponent"/>'s model and material in step with its field.
    /// </summary>
    public sealed class VoxelGridProcessor : EntityProcessor<VoxelGridComponent, VoxelGridProcessor.State>
    {
        /// <summary>What the processor keeps per component: the model standing in for the grid, and
        /// what it was built from, so it is rebuilt only when that changes.</summary>
        public sealed class State
        {
            public ModelComponent Model;
            public Material Material;
            public MaterialVoxelSurfaceFeature Surface;
            public Int3 SampleCount;
            public float CellSize;
            public int ExtentRevision = -1;

            /// <summary>The traversal's shader as it was when the material was built.</summary>
            public ShaderSource Shader;

            /// <summary>The emissive the default material was built with.</summary>
            public IComputeColor Emissive;

            /// <summary>The box's buffers, released when it is rebuilt or the component goes.</summary>
            public GraphicsBuffer VertexBuffer;
            public GraphicsBuffer IndexBuffer;

            public void ReleaseBuffers()
            {
                VertexBuffer?.Dispose();
                IndexBuffer?.Dispose();
                VertexBuffer = null;
                IndexBuffer = null;
            }
        }

        private IGraphicsDeviceService graphicsDeviceService;

        protected override void OnSystemAdd()
        {
            base.OnSystemAdd();
            graphicsDeviceService = Services.GetService<IGraphicsDeviceService>();
        }

        protected override State GenerateComponentData(Entity entity, VoxelGridComponent component) => new();

        protected override void OnEntityComponentRemoved(Entity entity, VoxelGridComponent component, State state)
        {
            if (state.Model?.Entity is { } carrier && carrier != entity)
                entity.RemoveChild(carrier);
            state.Model = null;
            state.ReleaseBuffers();
        }

        public override void Update(GameTime time)
        {
            var device = graphicsDeviceService?.GraphicsDevice;
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

                // The pass parameters are what the mesh render feature copies from, every frame.
                if (state.Surface != null)
                {
                    state.Surface.Debug = component.DebugView;
                    state.Surface.ApplyParameters(state.Material.Passes[0].Parameters);
                }
            }
        }

        private static void EnsureModel(GraphicsDevice device, VoxelGridComponent component, State state)
        {
            var samples = component.Traversal.Source.SampleCount;
            var cellSize = component.Traversal.CellSize;

            // The traversal's shader is a permutation: change the surface form and it is a different
            // shader, so the default material is built again. A material the user supplied is theirs
            // to regenerate. Shader sources compare structurally, so no string is built for it.
            var shader = component.Traversal.GetShaderSource();
            var wanted = component.Material;
            var rebuild = state.Material == null
                          || (wanted != null && state.Material != wanted)
                          || (wanted == null && (!Equals(state.Shader, shader) || !ReferenceEquals(state.Emissive, component.Emissive)));
            if (rebuild)
            {
                if (wanted != null)
                {
                    state.Material = wanted;
                    state.Surface = wanted.Descriptor?.Attributes?.Surface as MaterialVoxelSurfaceFeature;
                }
                else
                {
                    // Held from construction: Material.New does not keep the descriptor it was given,
                    // so the feature cannot be looked up on the finished material.
                    state.Material = BuildDefaultMaterial(device, component, out var built);
                    state.Surface = built;
                }

                state.Shader = shader;
                state.Emissive = component.Emissive;
                state.Model = null;

                // The far faces are kept, so the volume is drawn from inside as well as from outside
                // for one walk per pixel. The ray through a far face is the same ray as through the
                // near one; only the far one is still there when the camera stands in the volume.
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

            state.ReleaseBuffers();
            var model = new Model { state.Material };
            model.Add(BuildBoxMesh(device, extent, state, out var bounds, out var sphere));
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

            // A ray that crosses the volume corner to corner has gone as far as it can.
            if (state.Surface != null && state.Surface.MaxDistance <= 0)
                state.Surface.MaxDistance = extent.Length();
        }

        /// <summary>
        /// The volume's bounds, as twelve triangles for a ray to be found through.
        /// </summary>
        /// <remarks>
        /// Built the way a procedural model is built - tangents generated, bounding sphere computed -
        /// so the mesh path treats it like any other mesh.
        /// </remarks>
        private static Mesh BuildBoxMesh(GraphicsDevice device, Vector3 extent, State state, out BoundingBox bounds, out BoundingSphere sphere)
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

            // Corner order matches the bit pattern above: bit 0 is +X, bit 1 +Y, bit 2 +Z. One
            // winding; the material pass keeps the far faces.
            int[] indices =
            [
                0, 1, 2, 1, 3, 2, // -Z
                4, 6, 5, 5, 6, 7, // +Z
                0, 4, 1, 1, 4, 5, // -Y
                2, 3, 6, 3, 7, 6, // +Y
                0, 2, 4, 2, 6, 4, // -X
                1, 5, 3, 3, 5, 7, // +X
            ];

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

            var vertexBuffer = state.VertexBuffer = GraphicsBuffer.New(device, complete.VertexBuffer, BufferFlags.VertexBuffer, GraphicsResourceUsage.Default);
            var indexBuffer = state.IndexBuffer = GraphicsBuffer.Index.New(device, indicesShort);

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

                    // The diffuse slot reads the field's colour. A material needs a diffuse feature
                    // to shade at all; a user replaces the palette by putting a texture here instead.
                    Diffuse = new MaterialDiffuseMapFeature(new ComputeShaderClassColor { MixinReference = "ComputeColorVoxelAlbedo" }),
                    DiffuseModel = new MaterialDiffuseLambertModelFeature(),
                    Specular = new MaterialMetalnessMapFeature(new ComputeFloat(0f)),
                    SpecularModel = new MaterialSpecularMicrofacetModelFeature
                    {
                        Environment = new MaterialSpecularMicrofacetEnvironmentGGXPolynomial(),
                    },
                    MicroSurface = new MaterialGlossinessMapFeature(new ComputeFloat(0.35f)),

                    // Whatever the component says the surface emits. The slot runs after the
                    // surface feature, so a shader put here reads the traced albedo and position.
                    Emissive = component.Emissive is null ? null : new MaterialEmissiveMapFeature(component.Emissive),
                },
            };

            return Material.New(device, descriptor);
        }
    }
}
