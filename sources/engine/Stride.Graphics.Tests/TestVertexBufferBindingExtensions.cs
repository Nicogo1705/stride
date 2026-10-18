// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Runtime.CompilerServices;
using Xunit;

namespace Stride.Graphics.Tests
{
    public class TestVertexBufferBindingExtensions
    {
        // An explicit offset in the middle: the following elements resume from it.
        private static readonly VertexDeclaration Declaration = new(
            [
                new VertexElement("POSITION", 0, PixelFormat.R32G32B32_Float),
                new VertexElement("NORMAL", 0, PixelFormat.R32G32B32_Float, 20),
                new VertexElement("TEXCOORD", 0, PixelFormat.R32G32_Float),
                new VertexElement("COLOR", 1, PixelFormat.R8G8B8A8_UNorm),
            ],
            instanceCount: 0,
            vertexStride: 48);

        [Fact]
        public void DeclarationMatchesEnumerateWithOffsets()
        {
            var inputElements = Declaration.CreateInputElements();

            AssertMatches(Declaration, inputSlot: 0, inputElements);
        }

        [Fact]
        public void BindingsMatchEnumerateWithOffsetsPerSlot()
        {
            var secondDeclaration = new VertexDeclaration(new VertexElement("TANGENT", 0, PixelFormat.R32G32B32A32_Float));
            VertexBufferBinding[] bindings = [CreateBinding(Declaration), CreateBinding(secondDeclaration)];

            var inputElements = bindings.CreateInputElements();

            Assert.Equal(Declaration.VertexElements.Length + 1, inputElements.Length);
            AssertMatches(Declaration, inputSlot: 0, inputElements.AsSpan(0, Declaration.VertexElements.Length));
            AssertMatches(secondDeclaration, inputSlot: 1, inputElements.AsSpan(Declaration.VertexElements.Length));
        }

        [Fact]
        public void BindingsAllocateOnlyTheResult()
        {
            VertexBufferBinding[] bindings = [CreateBinding(Declaration)];
            bindings.CreateInputElements();

            var before = GC.GetAllocatedBytesForCurrentThread();
            var inputElements = bindings.CreateInputElements();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            // Array header + elements: no iterator or other temporary on the way.
            var expected = IntPtr.Size * 3 + inputElements.Length * Unsafe.SizeOf<InputElementDescription>();
            Assert.True(allocated <= expected, $"Allocated {allocated} bytes, expected at most {expected}.");
        }

        private static VertexBufferBinding CreateBinding(VertexDeclaration declaration)
        {
            // The conversion only reads the declaration; the buffer is never touched.
            var buffer = (Buffer)RuntimeHelpers.GetUninitializedObject(typeof(Buffer));
            return new VertexBufferBinding(buffer, declaration, vertexCount: 1);
        }

        private static void AssertMatches(VertexDeclaration declaration, int inputSlot, ReadOnlySpan<InputElementDescription> inputElements)
        {
            var i = 0;
            foreach (var element in declaration.EnumerateWithOffsets())
            {
                var expected = new InputElementDescription
                {
                    SemanticName = element.VertexElement.SemanticName,
                    SemanticIndex = element.VertexElement.SemanticIndex,
                    Format = element.VertexElement.Format,
                    InputSlot = inputSlot,
                    AlignedByteOffset = element.Offset
                };
                Assert.Equal(expected, inputElements[i++]);
            }
            Assert.Equal(i, inputElements.Length);
        }
    }
}
