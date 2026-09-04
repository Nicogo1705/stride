// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Engine.Design;
using Stride.Games;
using Stride.Graphics;
using Stride.Rendering.Compositing;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;
using Stride.Shaders;
using GraphicsBuffer = Stride.Graphics.Buffer;

namespace Stride.Rendering.Voxels.Grid
{
    /// <summary>
    /// Keeps a <see cref="VoxelGridComponent"/>'s models in step with its field and its materials,
    /// and the resolve pass in the compositor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A grid is drawn by several models, all the same twelve-triangle box the size of the grid:
    /// one per material, each carrying that material wrapped so that a resolve layer runs at the
    /// front of its pixel stage and hands the material the resolved surface; and one that casts
    /// the shadows, carrying a material that walks the field only in the caster passes. The box
    /// is what the renderer culls, sorts and rasterises, and its far faces are kept so the volume
    /// is drawn from inside as well.
    /// </para>
    /// <para>
    /// The resolve pass is put at the front of the camera's renderer the first time a grid exists,
    /// from the update and never from a draw, since a compositor being drawn is a list being
    /// walked.
    /// </para>
    /// </remarks>
    public sealed class VoxelGridProcessor : EntityProcessor<VoxelGridComponent, VoxelGridProcessor.State>
    {
        private readonly VoxelGridInjectionList injection = new();
        /// <summary>One model of the field: a material of the list, or the shadow caster.</summary>
        public sealed class Draw
        {
            /// <summary>The material as the user gave it.</summary>
            public Material Source;
            /// <summary>The same material with the resolve layer in front of its own.</summary>
            public Material Wrapped;
            /// <summary>The entity that carries the proxy-box model for this draw.</summary>
            public Entity Carrier;
            /// <summary>The proxy-box model, one mesh, the wrapped material.</summary>
            public ModelComponent Model;
        }

        /// <summary>What the processor keeps per component.</summary>
        public sealed class State
        {
            /// <summary>The byte that identifies this grid in the resolve targets.</summary>
            public int GridIndex;
            /// <summary>The field's sample count on each axis.</summary>
            public Int3 SampleCount;
            /// <summary>The size of one cell in the grid's own units.</summary>
            public float CellSize;
            /// <summary>The component's extent revision the box was built for.</summary>
            public int ExtentRevision = -1;
            /// <summary>The extent the box was built for.</summary>
            public Vector3 Extent;

            /// <summary>The traversal's shader as it was when the shadow material was built.</summary>
            public ShaderSource Shader;

            /// <summary>The traversal's shader as it was when the materials were wrapped.</summary>
            public ShaderSource MaterialsShader;

            /// <summary>The box's buffers, released when it is rebuilt or the component goes.</summary>
            public GraphicsBuffer VertexBuffer;
            /// <summary>The proxy box's indices.</summary>
            public GraphicsBuffer IndexBuffer;
            /// <summary>The proxy box's vertex layout.</summary>
            public VertexDeclaration Layout;
            /// <summary>Vertices in the proxy box.</summary>
            public int VertexCount;
            /// <summary>Indices in the proxy box.</summary>
            public int IndexCount;
            /// <summary>The proxy box's bounds, for culling.</summary>
            public BoundingBox Bounds;
            /// <summary>The proxy box's bounding sphere, for culling.</summary>
            public BoundingSphere Sphere;

            /// <summary>The materials, one draw each, in the order of the component's list.</summary>
            public List<Draw> Materials = [];

            /// <summary>The shadow caster, and the feature its material walks the field with.</summary>
            public Draw Shadow;
            /// <summary>The feature behind the shadow material, kept to update its parameters.</summary>
            public MaterialVoxelSurfaceFeature ShadowSurface;

            /// <summary>The grey the field is drawn with when no material was given.</summary>
            public Material Fallback;

            /// <summary>The materials' colour and emission by id, for the GI injector.</summary>
            public VoxelGridInjectionTable Table;

            /// <summary>The single material as a list of one, for the table; kept so no list is made per frame.</summary>
            public Material[] SingleMaterial = new Material[1];

            /// <summary>The materials wanted this frame, compared against the draws; kept so no list is made per frame.</summary>
            public List<(Material material, int id)> Wanted = [];

            /// <summary>The resolve targets' version the materials were last bound to.</summary>
            public int TargetsVersion = -1;

            /// <summary>Releases the proxy box's buffers.</summary>
            public void ReleaseBuffers()
            {
                VertexBuffer?.Dispose();
                IndexBuffer?.Dispose();
                VertexBuffer = null;
                IndexBuffer = null;
                Table?.Dispose();
                Table = null;
            }
        }

        private IGraphicsDeviceService graphicsDeviceService;
        private SceneSystem sceneSystem;
        private IGame game;
        private VoxelGridResolvePass resolvePass;

        // A grid's index is a byte in the resolve targets; one given back is given out again
        // before a new one is minted, so two live grids never share one.
        private readonly Stack<int> freeGridIndices = new();
        private int nextGridIndex;

        protected override void OnSystemAdd()
        {
            base.OnSystemAdd();
            graphicsDeviceService = Services.GetService<IGraphicsDeviceService>();
            sceneSystem = Services.GetService<SceneSystem>();
            game = Services.GetService<IGame>();
        }

        protected override State GenerateComponentData(Entity entity, VoxelGridComponent component)
            => new() { GridIndex = freeGridIndices.Count > 0 ? freeGridIndices.Pop() : nextGridIndex++ % 256 };

        protected override void OnEntityComponentRemoved(Entity entity, VoxelGridComponent component, State state)
        {
            freeGridIndices.Push(state.GridIndex);
            foreach (var draw in state.Materials)
                Detach(entity, draw);
            state.Materials.Clear();
            if (state.Shadow != null)
                Detach(entity, state.Shadow);
            state.Shadow = null;
            state.ReleaseBuffers();
        }

        public override void Update(GameTime time)
        {
            var device = graphicsDeviceService?.GraphicsDevice;
            if (device == null)
                return;

            EnsureResolvePass();
            var renderer = resolvePass?.Renderer;
            renderer?.Grids.Clear();
            injection.Entries.Clear();

            // The list the GI's injector reads, on every visibility group of the scene: the
            // renderer finds it through the one its view draws with. (A processor only gets a
            // visibility group of its own when its component names it as a renderer.)
            var groups = sceneSystem?.SceneInstance?.VisibilityGroups;
            if (groups != null)
                foreach (var group in groups)
                    if (group.Tags.Get(VoxelGridInjector.CurrentEntries) != injection)
                        group.Tags.Set(VoxelGridInjector.CurrentEntries, injection);

            foreach (var pair in ComponentDatas)
            {
                var component = pair.Key;
                var state = pair.Value;

                var live = component.Enabled && component.Traversal?.Source != null;
                if (!live)
                {
                    SetEnabled(state, false);
                    continue;
                }

                EnsureBox(device, component, state);
                EnsureShadow(device, component, state);
                EnsureMaterials(device, component, state);
                SetEnabled(state, true);

                if (state.Shadow != null)
                {
                    state.Shadow.Model.IsShadowCaster = component.CastShadows;
                    state.Shadow.Model.Enabled = component.CastShadows;
                    state.ShadowSurface.ApplyParameters(state.Shadow.Wrapped.Passes[0].Parameters);
                }

                // The resolve targets, bound again when the renderer remade them; the field's
                // parameters, for the voxelizer's view, where the layer walks the field itself;
                // and the diagnostic view. Every frame.
                foreach (var draw in state.Materials)
                {
                    foreach (var pass in draw.Wrapped.Passes)
                    {
                        component.Traversal.ApplyParameters(pass.Parameters);
                        pass.Parameters.Set(VoxelGridFieldKeys.MaxDistance, state.Extent.Length());
                        pass.Parameters.Set(VoxelGridFieldKeys.Debug, component.DebugView);
                        pass.Parameters.Set(VoxelGridFieldKeys.Injected, component.InjectIntoGI ? 1f : 0f);
                        if (renderer != null && state.TargetsVersion != renderer.TargetsVersion)
                        {
                            pass.Parameters.Set(VoxelGridFieldKeys.ResolveNormal, renderer.Normal);
                            pass.Parameters.Set(VoxelGridFieldKeys.ResolveMaterial, renderer.Material);
                            pass.Parameters.Set(VoxelGridFieldKeys.ResolvePosition, renderer.Position);
                        }
                    }
                }
                if (renderer != null)
                    state.TargetsVersion = renderer.TargetsVersion;

                component.Entity.Transform.UpdateWorldMatrix();

                // What the GI injector needs, when the field goes to the GI from its samples.
                if (component.InjectIntoGI && game?.GraphicsContext != null)
                {
                    EnsureTable(device, component, state);
                    injection.Entries.Add(new VoxelGridInjectionEntry
                    {
                        Traversal = component.Traversal,
                        World = component.Entity.Transform.WorldMatrix,
                        Extent = state.Extent,
                        Table = state.Table.Buffer,
                    });
                }

                if (renderer != null)
                {
                    renderer.Grids.Add(new VoxelGridResolveEntry
                    {
                        Traversal = component.Traversal,
                        World = component.Entity.Transform.WorldMatrix,
                        MaxDistance = state.Extent.Length(),
                        Dither = component.Dither,
                        GridIndex = state.GridIndex,
                    });
                }
            }
        }

        /// <summary>The injector's table of colours and emissions, kept in step with the material list.</summary>
        private void EnsureTable(GraphicsDevice device, VoxelGridComponent component, State state)
        {
            state.Table ??= new VoxelGridInjectionTable(device);
            IReadOnlyList<Material> materials = component.Materials;
            if (component.Material != null)
            {
                state.SingleMaterial[0] = component.Material;
                materials = state.SingleMaterial;
            }
            state.Table.Refresh(game.GraphicsContext.CommandList, materials);
        }

        /// <summary>
        /// Puts the resolve pass at the front of the camera's renderer, once. Inside the camera
        /// renderer, not beside it: a renderer beside it runs with no view and resolves nothing.
        /// </summary>
        private void EnsureResolvePass()
        {
            if (resolvePass != null)
                return;

            var compositor = sceneSystem?.GraphicsCompositor;
            if (compositor?.Game == null)
                return;

            var pass = new VoxelGridResolvePass();
            if (InsertFirst(compositor.Game, pass))
                resolvePass = pass;
        }

        private static bool InsertFirst(ISceneRenderer renderer, ISceneRenderer pass)
        {
            switch (renderer)
            {
                case SceneCameraRenderer camera:
                    if (camera.Child is SceneRendererCollection inner)
                    {
                        inner.Children.Insert(0, pass);
                    }
                    else
                    {
                        var wrapper = new SceneRendererCollection();
                        wrapper.Children.Add(pass);
                        wrapper.Children.Add(camera.Child);
                        camera.Child = wrapper;
                    }
                    return true;

                case SceneRendererCollection collection:
                    foreach (var child in collection.Children)
                    {
                        if (InsertFirst(child, pass))
                            return true;
                    }
                    return false;

                default:
                    return false;
            }
        }

        private static void SetEnabled(State state, bool enabled)
        {
            foreach (var draw in state.Materials)
                draw.Model.Enabled = enabled;
            if (state.Shadow != null && !enabled)
                state.Shadow.Model.Enabled = false;
        }

        /// <summary>The proxy box, rebuilt only when the field's extent moves.</summary>
        private static void EnsureBox(GraphicsDevice device, VoxelGridComponent component, State state)
        {
            var samples = component.Traversal.Source.SampleCount;
            var cellSize = component.Traversal.CellSize;

            var stale = state.VertexBuffer == null
                        || state.SampleCount != samples
                        || state.CellSize != cellSize
                        || state.ExtentRevision != component.ExtentRevision;
            if (!stale)
                return;

            state.SampleCount = samples;
            state.CellSize = cellSize;
            state.ExtentRevision = component.ExtentRevision;
            state.Extent = new Vector3(samples.X - 1, samples.Y - 1, samples.Z - 1) * cellSize;

            state.ReleaseBuffers();
            BuildBox(device, state);

            // Every model carries the box; a new box means new meshes.
            foreach (var draw in state.Materials)
                draw.Model.Model = BuildModel(state, draw.Wrapped);
            if (state.Shadow != null)
                state.Shadow.Model.Model = BuildModel(state, state.Shadow.Wrapped);
        }

        /// <summary>The shadow caster: a box whose material walks the field in the caster passes only.</summary>
        private static void EnsureShadow(GraphicsDevice device, VoxelGridComponent component, State state)
        {
            var shader = component.Traversal.GetShaderSource();
            if (state.Shadow != null && Equals(state.Shader, shader))
                return;

            if (state.Shadow != null)
                Detach(component.Entity, state.Shadow);

            state.Shader = shader;
            state.ShadowSurface = new MaterialVoxelSurfaceFeature { Traversal = component.Traversal, MaxDistance = state.Extent.Length() };
            var material = Material.New(device, new MaterialDescriptor
            {
                Attributes =
                {
                    Surface = state.ShadowSurface,
                    // A diffuse feature, or the generator adds no pixel stage worth running.
                    Diffuse = new MaterialDiffuseMapFeature(new ComputeColor(Color4.Black)),
                    DiffuseModel = new MaterialDiffuseLambertModelFeature(),
                },
            });
            foreach (var pass in material.Passes)
                pass.CullMode = CullMode.Front;

            state.Shadow = Attach(component.Entity, "VoxelGridShadow", state, null, material);
            state.Shadow.Model.IsShadowCaster = component.CastShadows;
        }

        /// <summary>
        /// One model per material, rebuilt when the list changes. A single <see cref="VoxelGridComponent.Material"/>
        /// draws every id; an empty list draws every id in grey.
        /// </summary>
        private static void EnsureMaterials(GraphicsDevice device, VoxelGridComponent component, State state)
        {
            var wanted = state.Wanted;
            wanted.Clear();
            if (component.Material != null)
            {
                wanted.Add((component.Material, -1));
            }
            else if (component.Materials.Count > 0)
            {
                for (int id = 0; id < component.Materials.Count && id < 256; id++)
                    if (component.Materials[id] != null)
                        wanted.Add((component.Materials[id], id));
            }
            else
            {
                state.Fallback ??= Material.New(device, new MaterialDescriptor
                {
                    Attributes =
                    {
                        Diffuse = new MaterialDiffuseMapFeature(new ComputeColor(new Color4(0.6f, 0.6f, 0.6f, 1f))),
                        DiffuseModel = new MaterialDiffuseLambertModelFeature(),
                        MicroSurface = new MaterialGlossinessMapFeature(new ComputeFloat(0.3f)),
                        Specular = new MaterialMetalnessMapFeature(new ComputeFloat(0f)),
                        SpecularModel = new MaterialSpecularMicrofacetModelFeature { Environment = new MaterialSpecularMicrofacetEnvironmentGGXPolynomial() },
                    },
                });
                wanted.Add((state.Fallback, -1));
            }

            // Rebuilt when the list changes, and when the traversal's shader does: the layer mixes
            // the traversal in for the voxelizer's view.
            var shader = component.Traversal.GetShaderSource();
            var same = wanted.Count == state.Materials.Count && Equals(state.MaterialsShader, shader);
            for (int i = 0; same && i < wanted.Count; i++)
                same = ReferenceEquals(state.Materials[i].Source, wanted[i].material);
            if (same)
                return;
            state.MaterialsShader = shader;

            foreach (var draw in state.Materials)
                Detach(component.Entity, draw);
            state.Materials.Clear();

            foreach (var (material, id) in wanted)
            {
                var wrapped = Wrap(material, id, state.GridIndex, shader);
                var draw = Attach(component.Entity, $"VoxelGridMaterial{id}", state, material, wrapped);
                draw.Model.IsShadowCaster = false;
                state.Materials.Add(draw);
            }

            // Bound on the next update, whatever version the targets are at.
            state.TargetsVersion = -1;
        }

        /// <summary>
        /// A material, taken as compiled, with the resolve layer at the front of its pixel stage.
        /// </summary>
        /// <remarks>
        /// No descriptor is needed and none is used: a compiled material's pixel stage is a shader
        /// source in its parameters, an array of layers, and a layer put in front of the others is
        /// run before them. The parameters are copied, so the material the user holds is untouched
        /// and each wrapped copy carries its own id.
        /// </remarks>
        private static Material Wrap(Material source, int id, int gridIndex, ShaderSource traversal)
        {
            var wrapped = new Material();
            foreach (var sourcePass in source.Passes)
            {
                var parameters = new ParameterCollection(sourcePass.Parameters);

                // The resolve layer, with the traversal and its source mixed in beside it for the
                // voxelizer's view, where the layer walks the field itself.
                var resolve = new ShaderMixinSource();
                resolve.Mixins.Add(new ShaderClassSource("MaterialSurfaceVoxelGridResolve"));
                if (traversal is ShaderMixinSource traversalMixin)
                    foreach (var part in traversalMixin.Mixins)
                        resolve.Mixins.Add(part);

                var layers = new ShaderMixinSource();
                layers.Mixins.Add(new ShaderClassSource("MaterialSurfaceArray"));
                layers.AddCompositionToArray("layers", resolve);

                var original = parameters.Get(MaterialKeys.PixelStageSurfaceShaders);
                if (original is ShaderMixinSource mixin && mixin.Compositions.TryGetValue("layers", out var array) && array is ShaderArraySource arraySource)
                {
                    foreach (var layer in arraySource.Values)
                        layers.AddCompositionToArray("layers", layer);
                }
                else if (original != null)
                {
                    layers.AddCompositionToArray("layers", original);
                }

                parameters.Set(MaterialKeys.PixelStageSurfaceShaders, layers);

                // The vertex half defines the pass streams the layer reads, in every pass.
                var vertexLayers = new ShaderMixinSource();
                vertexLayers.Mixins.Add(new ShaderClassSource("MaterialSurfaceArray"));
                var originalVertex = parameters.Get(MaterialKeys.VertexStageSurfaceShaders);
                if (originalVertex is ShaderMixinSource vertexMixin && vertexMixin.Compositions.TryGetValue("layers", out var vertexArray) && vertexArray is ShaderArraySource vertexSource)
                {
                    foreach (var layer in vertexSource.Values)
                        vertexLayers.AddCompositionToArray("layers", layer);
                }
                else if (originalVertex != null)
                {
                    vertexLayers.AddCompositionToArray("layers", originalVertex);
                }
                vertexLayers.AddCompositionToArray("layers", new ShaderClassSource("MaterialSurfaceVoxelGridVertex"));
                parameters.Set(MaterialKeys.VertexStageSurfaceShaders, vertexLayers);
                // The depth-only passes rasterise with the vertex stage alone unless told otherwise,
                // and for this material the mesh is the proxy box.
                parameters.Set(MaterialKeys.UsePixelShaderWithDepthPass, true);
                parameters.Set(VoxelGridFieldKeys.MaterialId, id);
                parameters.Set(VoxelGridFieldKeys.GridIndex, gridIndex);

                wrapped.Passes.Add(new MaterialPass(parameters)
                {
                    // The far faces are kept, so the volume is drawn from inside as well as from
                    // outside; the layer discards what the resolve pass did not give this material.
                    CullMode = CullMode.Front,
                    DepthFunction = sourcePass.DepthFunction,
                    BlendState = sourcePass.BlendState,
                    TessellationMethod = sourcePass.TessellationMethod,
                    HasTransparency = sourcePass.HasTransparency,
                    AlphaToCoverage = sourcePass.AlphaToCoverage,
                    IsLightDependent = sourcePass.IsLightDependent,
                    PassIndex = sourcePass.PassIndex,
                });
            }
            return wrapped;
        }

        /// <summary>
        /// A child entity of its own, rather than a ModelComponent on the grid's entity. That
        /// entity is the user's - it commonly already carries a collider, and may carry a model the
        /// user put there - and quietly taking over its ModelComponent is both a collision and a
        /// thing that is hard to see from the outside.
        /// </summary>
        private static Draw Attach(Entity entity, string name, State state, Material source, Material wrapped)
        {
            var carrier = new Entity(name);
            var model = carrier.GetOrCreate<ModelComponent>();
            model.Model = BuildModel(state, wrapped);
            entity.AddChild(carrier);
            return new Draw { Source = source, Wrapped = wrapped, Carrier = carrier, Model = model };
        }

        private static void Detach(Entity entity, Draw draw)
        {
            if (draw.Carrier.GetParent() == entity)
                entity.RemoveChild(draw.Carrier);
        }

        private static Model BuildModel(State state, Material material)
        {
            var model = new Model { material };
            model.Add(new Mesh
            {
                MaterialIndex = 0,
                BoundingBox = state.Bounds,
                BoundingSphere = state.Sphere,
                Draw = new MeshDraw
                {
                    PrimitiveType = PrimitiveType.TriangleList,
                    DrawCount = state.IndexCount,
                    IndexBuffer = new IndexBufferBinding(state.IndexBuffer, false, state.IndexCount),
                    VertexBuffers = [new VertexBufferBinding(state.VertexBuffer, state.Layout, state.VertexCount)],
                },
            });
            model.BoundingBox = state.Bounds;
            model.BoundingSphere = state.Sphere;
            return model;
        }

        /// <summary>
        /// The volume's bounds, as twelve triangles for the resolve to be read through. Built the
        /// way a procedural model is built - tangents generated, bounding sphere computed - so the
        /// mesh path treats it like any other mesh.
        /// </summary>
        private static void BuildBox(GraphicsDevice device, State state)
        {
            var extent = state.Extent;
            var vertices = new VertexPositionNormalTexture[8];
            for (int corner = 0; corner < 8; ++corner)
            {
                var position = new Vector3(
                    (corner & 1) != 0 ? extent.X : 0,
                    (corner & 2) != 0 ? extent.Y : 0,
                    (corner & 4) != 0 ? extent.Z : 0);

                // Normals and texture coordinates that are merely not degenerate: the resolve layer
                // replaces the frame, but tangent generation divides by the spread of the texture
                // coordinates and hands back NaN when every vertex shares one.
                vertices[corner] = new VertexPositionNormalTexture(
                    position,
                    Vector3.Normalize(position - extent * 0.5f),
                    new Vector2((corner & 1) != 0 ? 1 : 0, (corner & 2) != 0 ? 1 : 0));
            }

            // Corner order matches the bit pattern above: bit 0 is +X, bit 1 +Y, bit 2 +Z. One
            // winding; the material passes keep the far faces.
            int[] indices =
            [
                0, 1, 2, 1, 3, 2, // -Z
                4, 6, 5, 5, 6, 7, // +Z
                0, 4, 1, 1, 4, 5, // -Y
                2, 3, 6, 3, 7, 6, // +Y
                0, 2, 4, 2, 6, 4, // -X
                1, 5, 3, 3, 5, 7, // +X
            ];

            state.Bounds = new BoundingBox(Vector3.Zero, extent);
            unsafe
            {
                fixed (void* positions = vertices)
                    BoundingSphere.FromPoints((IntPtr)positions, 0, vertices.Length, VertexPositionNormalTexture.Size, out state.Sphere);
            }

            var withTangents = VertexHelper.GenerateTangentBinormal(vertices[0].GetLayout(), vertices, indices);
            var complete = VertexHelper.GenerateMultiTextureCoordinates(withTangents, 0, 0);

            var indicesShort = new ushort[indices.Length];
            for (int i = 0; i < indicesShort.Length; ++i)
                indicesShort[i] = (ushort)indices[i];

            state.VertexBuffer = GraphicsBuffer.New(device, complete.VertexBuffer, BufferFlags.VertexBuffer, GraphicsResourceUsage.Default);
            state.IndexBuffer = GraphicsBuffer.Index.New(device, indicesShort);
            state.Layout = complete.Layout;
            state.VertexCount = vertices.Length;
            state.IndexCount = indices.Length;
        }
    }
}
