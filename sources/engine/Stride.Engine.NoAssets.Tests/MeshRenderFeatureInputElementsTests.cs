// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Shaders;
using Xunit;
using Buffer = Stride.Graphics.Buffer;

namespace Stride.Engine.Tests
{
    public class MeshRenderFeatureInputElementsTests
    {
        private static readonly VertexDeclaration Declaration = new(
            new VertexElement("POSITION", 0, PixelFormat.R32G32B32_Float),
            new VertexElement("TEXCOORD", 0, PixelFormat.R32G32_Float));

        [Fact]
        public void MissingAttributesAreAppendedOnAnExtraSlot()
        {
            var prepare = CreatePrepareInputElements();
            var drawData = CreateDrawData();
            var pipelineState = CreatePipelineState(("TEXCOORD", 0), ("NORMAL", 0), ("POSITION", 0), ("TEXCOORD", 1));

            var inputElements = prepare(pipelineState, drawData);

            var expected = drawData.VertexBuffers.CreateInputElements();
            Assert.Equal(4, inputElements.Length);
            Assert.Equal(expected[0], inputElements[0]);
            Assert.Equal(expected[1], inputElements[1]);
            Assert.Equal(CreateMissingElement("NORMAL", 0), inputElements[2]);
            Assert.Equal(CreateMissingElement("TEXCOORD", 1), inputElements[3]);
        }

        [Fact]
        public void ProvidedAttributesKeepTheVertexBufferLayout()
        {
            var prepare = CreatePrepareInputElements();
            var drawData = CreateDrawData();
            var pipelineState = CreatePipelineState(("POSITION", 0), ("TEXCOORD", 0));

            var inputElements = prepare(pipelineState, drawData);

            Assert.Equal(drawData.VertexBuffers.CreateInputElements(), inputElements);
        }

        [Fact]
        public void MissingAttributesAllocateOnlyTheVertexBufferElementsAndTheResult()
        {
            var prepare = CreatePrepareInputElements();
            var drawData = CreateDrawData();
            var pipelineState = CreatePipelineState(("POSITION", 0), ("NORMAL", 0), ("TANGENT", 0), ("COLOR", 0));
            prepare(pipelineState, drawData);
            drawData.VertexBuffers.CreateInputElements();

            var before = GC.GetAllocatedBytesForCurrentThread();
            drawData.VertexBuffers.CreateInputElements();
            var vertexBufferElementsBytes = GC.GetAllocatedBytesForCurrentThread() - before;

            before = GC.GetAllocatedBytesForCurrentThread();
            var inputElements = prepare(pipelineState, drawData);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            // The vertex buffer elements and the result: no list or copy in between.
            var expected = vertexBufferElementsBytes + IntPtr.Size * 3 + inputElements.Length * Unsafe.SizeOf<InputElementDescription>();
            Assert.True(allocated <= expected, $"Allocated {allocated} bytes, expected at most {expected}.");
        }

        private static Func<PipelineStateDescription, MeshDraw, InputElementDescription[]> CreatePrepareInputElements()
        {
            var method = typeof(MeshRenderFeature).GetMethod("PrepareInputElements", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            return method.CreateDelegate<Func<PipelineStateDescription, MeshDraw, InputElementDescription[]>>(new MeshRenderFeature());
        }

        private static MeshDraw CreateDrawData()
        {
            // Only the vertex declaration is read; the buffer is never touched.
            var buffer = (Buffer)RuntimeHelpers.GetUninitializedObject(typeof(Buffer));
            return new MeshDraw { VertexBuffers = [new VertexBufferBinding(buffer, Declaration, vertexCount: 1)] };
        }

        private static PipelineStateDescription CreatePipelineState(params (string Name, int Index)[] attributes)
        {
            var reflection = new EffectReflection();
            foreach (var (name, index) in attributes)
                reflection.InputAttributes.Add(new ShaderInputAttributeDescription { SemanticName = name, SemanticIndex = index });

            return new PipelineStateDescription { EffectBytecode = new EffectBytecode { Reflection = reflection } };
        }

        private static InputElementDescription CreateMissingElement(string semanticName, int semanticIndex) => new()
        {
            SemanticName = semanticName,
            SemanticIndex = semanticIndex,
            Format = PixelFormat.R32G32B32A32_Float,
            InputSlot = 1,
            InputSlotClass = InputClassification.Vertex,
        };
    }
}
