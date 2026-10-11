// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;

namespace Stride.BepuPhysics.Debug.Effects.RenderFeatures;

/// <summary>
/// Draws <see cref="LineRenderObject"/>s as screen-space lines, or sphere outlines, of constant width, on top of the scene or faded where something hides them.
/// </summary>
/// <remarks> Lines are only drawn in the render stages picked by <see cref="RootRenderFeature.RenderStageSelectors"/>. </remarks>
public class LineRenderFeature : RootRenderFeature
{
    // Opacity of depth tested segments where something hides them, the default of the wireframes' hidden lines
    private const float HiddenOpacity = 0.5f;

    private DynamicEffectInstance _shader = null!;
    private DynamicEffectInstance _sphereShader = null!;
    private MutablePipelineState _overlayPipelineState = null!;
    private MutablePipelineState _visiblePipelineState = null!;
    private MutablePipelineState _hiddenPipelineState = null!;

    /// <summary>
    /// The width of the lines, in pixels.
    /// </summary>
    [DataMember(10)]
    [DataMemberRange(0.0f, 10.0f, 0.001f, 0.002f, 4)]
    public float LineWidth = 3f;

    /// <inheritdoc/>
    public override Type SupportedRenderObjectType => typeof(LineRenderObject);

    public LineRenderFeature()
    {
        SortKey = 255;
    }

    protected override void InitializeCore()
    {
        base.InitializeCore();

        _shader = new DynamicEffectInstance("StrideDebugLineShader");
        _shader.Initialize(Context.Services);
        _sphereShader = new DynamicEffectInstance("StrideDebugSphereShader");
        _sphereShader.Initialize(Context.Services);

        // A debug overlay: normals start inside body A and markers sit on surfaces, depth testing would hide them
        _overlayPipelineState = CreatePipelineState(DepthStencilStates.None);
        // Depth tested batches: the two depth tests split every pixel of a segment between them
        _visiblePipelineState = CreatePipelineState(DepthStencilStates.DepthRead);
        _hiddenPipelineState = CreatePipelineState(new DepthStencilStateDescription(depthEnable: true, depthWriteEnable: false) { DepthBufferFunction = CompareFunction.Greater });
    }

    private MutablePipelineState CreatePipelineState(DepthStencilStateDescription depthStencilState)
    {
        var pipelineState = new MutablePipelineState(Context.GraphicsDevice);
        pipelineState.State.SetDefaults();
        pipelineState.State.InputElements = LineRenderObject.LineVertex.Layout.CreateInputElements();
        pipelineState.State.PrimitiveType = PrimitiveType.LineList;
        pipelineState.State.RasterizerState.CullMode = CullMode.None;
        pipelineState.State.BlendState = BlendStates.AlphaBlend;
        pipelineState.State.DepthStencilState = depthStencilState;
        return pipelineState;
    }

    public override void Draw(RenderDrawContext context, RenderView renderView, RenderViewStage renderViewStage, int startIndex, int endIndex)
    {
        if (!HasLines(renderViewStage, startIndex, endIndex))
            return;

        _shader.UpdateEffect(context.GraphicsDevice);
        _shader.Parameters.Set(TransformationKeys.WorldViewProjection, renderView.ViewProjection);
        _shader.Parameters.Set(DebugLineShaderKeys.ViewportSize, renderView.ViewSize);
        _shader.Parameters.Set(DebugLineShaderKeys.LineWidth, LineWidth);
        _sphereShader.UpdateEffect(context.GraphicsDevice);
        _sphereShader.Parameters.Set(TransformationKeys.View, renderView.View);
        _sphereShader.Parameters.Set(TransformationKeys.Projection, renderView.Projection);
        _sphereShader.Parameters.Set(TransformationKeys.ViewProjection, renderView.ViewProjection);
        _sphereShader.Parameters.Set(DebugSphereShaderKeys.ViewportSize, renderView.ViewSize);
        _sphereShader.Parameters.Set(DebugSphereShaderKeys.LineWidth, LineWidth);

        for (int index = startIndex; index < endIndex; index++)
        {
            var lines = GetLines(renderViewStage, index);
            if (!lines.Enabled || lines.VertexCount == 0)
                continue;

            var vertexBuffer = lines.Upload(context.CommandList);
            context.CommandList.SetVertexBuffer(0, vertexBuffer, 0, LineRenderObject.LineVertex.Size);
            if (lines.DepthTested)
            {
                // Hidden parts first, so the visible ones stay on top where they cross
                DrawPass(context, lines, _hiddenPipelineState, HiddenOpacity);
                DrawPass(context, lines, _visiblePipelineState, 1f);
            }
            else
            {
                DrawPass(context, lines, _overlayPipelineState, 1f);
            }
        }
    }

    private void DrawPass(RenderDrawContext context, LineRenderObject lines, MutablePipelineState pipelineState, float opacity)
    {
        var shader = lines.Spheres ? _sphereShader : _shader;
        pipelineState.State.RootSignature = shader.RootSignature;
        pipelineState.State.EffectBytecode = shader.Effect.Bytecode;
        pipelineState.State.Output.CaptureState(context.CommandList);
        pipelineState.Update();

        shader.Parameters.Set(lines.Spheres ? DebugSphereShaderKeys.Opacity : DebugLineShaderKeys.Opacity, opacity);
        context.CommandList.SetPipelineState(pipelineState.CurrentState);
        shader.Apply(context.GraphicsContext);
        context.CommandList.Draw(lines.VertexCount);
    }

    private LineRenderObject GetLines(RenderViewStage renderViewStage, int index)
        => (LineRenderObject)GetRenderNode(renderViewStage.SortedRenderNodes[index].RenderNode).RenderObject;

    private bool HasLines(RenderViewStage renderViewStage, int startIndex, int endIndex)
    {
        for (int index = startIndex; index < endIndex; index++)
        {
            var lines = GetLines(renderViewStage, index);
            if (lines.Enabled && lines.VertexCount > 0)
                return true;
        }
        return false;
    }
}
