// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.BepuPhysics.Definitions;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;

namespace Stride.BepuPhysics.Debug.Effects.RenderFeatures;

/// <summary>
/// Draws <see cref="WireFrameRenderObject"/>s as wireframes, in the render stages picked by <see cref="RootRenderFeature.RenderStageSelectors"/>.
/// </summary>
/// <remarks>
/// Lines are drawn at full opacity where nothing hides them, and at half opacity where something does, such as the
/// collider's own visual model: a collider inside its model stays visible, and the parts sticking out of it stand out.
/// </remarks>
public class SinglePassWireframeRenderFeature : RootRenderFeature
{
    // Opacity of the lines something hides; only their front faces are drawn there, so a hidden collider reads as a surface
    private const float HiddenOpacity = 0.5f;

    private DynamicEffectInstance _shader = null!;
    private MutablePipelineState _visiblePipelineState = null!;
    private MutablePipelineState _hiddenPipelineState = null!;

    [DataMember(0)]
    public bool Enable = true;

    [DataMember(10)]
    [DataMemberRange(0.0f, 10.0f, 0.001f, 0.002f, 4)]
    public float LineWidth = 3f;

    public override Type SupportedRenderObjectType => typeof(WireFrameRenderObject);

    public SinglePassWireframeRenderFeature()
    {
        SortKey = 254; // Below LineRenderFeature, so contacts are drawn over the wireframes
    }

    protected override void InitializeCore()
    {
        base.InitializeCore();

        // initialize shader
        _shader = new DynamicEffectInstance("StrideSinglePassWireframeShader");
        _shader.Initialize(Context.Services);

        // create the pipeline states and set properties that won't change; the two depth tests split every line pixel between them
        _visiblePipelineState = CreatePipelineState(DepthStencilStates.DepthRead, CullMode.None);
        _hiddenPipelineState = CreatePipelineState(new DepthStencilStateDescription(depthEnable: true, depthWriteEnable: false) { DepthBufferFunction = CompareFunction.Greater }, CullMode.Back);
    }

    private MutablePipelineState CreatePipelineState(DepthStencilStateDescription depthStencilState, CullMode cullMode)
    {
        var pipelineState = new MutablePipelineState(Context.GraphicsDevice);
        pipelineState.State.SetDefaults();
        pipelineState.State.InputElements = VertexPosition3.Layout.CreateInputElements();
        pipelineState.State.BlendState = BlendStates.AlphaBlend;
        pipelineState.State.RasterizerState.CullMode = cullMode;
        pipelineState.State.DepthStencilState = depthStencilState;
        return pipelineState;
    }

    public override void Prepare(RenderDrawContext context)
    {
        base.Prepare(context);
    }

    public void IsEnabled(bool enable)
    {
        Enable = enable;
    }

    public override void Draw(RenderDrawContext context, RenderView renderView, RenderViewStage renderViewStage, int startIndex, int endIndex)
    {
        if (!Enable || startIndex == endIndex) return;

        _shader.UpdateEffect(context.GraphicsDevice);
        _shader.Parameters.Set(TransformationKeys.Projection, renderView.Projection);
        // The bound viewport rather than the view size, so edges line up with SV_Position when the view is offset (VR eye, letterboxing)
        var viewport = context.CommandList.Viewport;
        _shader.Parameters.Set(SinglePassWireframeShaderKeys.Viewport, new Vector4(viewport.Width, viewport.Height, viewport.X, viewport.Y));
        _shader.Parameters.Set(SinglePassWireframeShaderKeys.LineWidth, LineWidth);

        // Hidden lines first, so the visible ones stay on top where they cross
        DrawPass(context, renderView, renderViewStage, startIndex, endIndex, _hiddenPipelineState, HiddenOpacity);
        DrawPass(context, renderView, renderViewStage, startIndex, endIndex, _visiblePipelineState, 1f);
    }

    private void DrawPass(RenderDrawContext context, RenderView renderView, RenderViewStage renderViewStage, int startIndex, int endIndex, MutablePipelineState pipelineState, float opacity)
    {
        var commandList = context.CommandList;
        pipelineState.State.RootSignature = _shader.RootSignature;
        pipelineState.State.EffectBytecode = _shader.Effect.Bytecode;
        pipelineState.State.Output.CaptureState(commandList);
        pipelineState.State.PrimitiveType = PrimitiveType.TriangleList;
        pipelineState.Update();
        _shader.Parameters.Set(SinglePassWireframeShaderKeys.Opacity, opacity);

        for (int index = startIndex; index < endIndex; index++)
        {
            var wireframe = (WireFrameRenderObject)GetRenderNode(renderViewStage.SortedRenderNodes[index].RenderNode).RenderObject;

            _shader.Parameters.Set(TransformationKeys.WorldViewProjection, wireframe.WorldMatrix * renderView.ViewProjection);
            _shader.Parameters.Set(TransformationKeys.WorldView, wireframe.WorldMatrix * renderView.View);
            _shader.Parameters.Set(SinglePassWireframeShaderKeys.LineColor, (Vector3)wireframe.Color);
            _shader.Parameters.Set(SinglePassWireframeShaderKeys.DepthBias, DepthBias(wireframe));

            if (pipelineState.State.PrimitiveType != wireframe.PrimitiveType)
            {
                pipelineState.State.PrimitiveType = wireframe.PrimitiveType;
                pipelineState.Update();
            }

            commandList.SetVertexBuffer(0, wireframe.VertexBuffer, 0, wireframe.VertexStride);
            commandList.SetIndexBuffer(wireframe.IndexBuffer, 0, true);
            commandList.SetPipelineState(pipelineState.CurrentState);
            _shader.Apply(context.GraphicsContext);
            commandList.DrawIndexed(wireframe.IndexBuffer.ElementCount);
        }
    }

    /// <summary> The debug mesh's distance to the exact shape, in world units </summary>
    private static float DepthBias(WireFrameRenderObject wireframe)
    {
        // Curved debug meshes only stray from their shape across its Y axis: the sphere, the cylinder's side, the capsule's radius
        var world = wireframe.WorldMatrix;
        return wireframe.MaxDeviation * MathF.Max(world.Row1.XYZ().Length(), world.Row3.XYZ().Length());
    }
}
