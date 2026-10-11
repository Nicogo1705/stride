// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
#if STRIDE_GRAPHICS_API_VULKAN
using System;

namespace Stride.Graphics
{
    /// <summary>
    ///   The layouts one command buffer gave the subresources (mip, array layer) of a texture: the one it assumed on first
    ///   touch and the one it left. Submission corrects the assumed layouts from what earlier submissions left.
    /// </summary>
    /// <remarks>Pooled by the device: the arrays can be longer than <see cref="Count"/>.</remarks>
    internal sealed class CommandBufferImageLayouts
    {
        public BarrierLayout?[] Assumed = [];
        public BarrierLayout?[] Current = [];
        public int Count;

        public void Reset(int subresourceCount)
        {
            if (Assumed.Length < subresourceCount)
            {
                Assumed = new BarrierLayout?[subresourceCount];
                Current = new BarrierLayout?[subresourceCount];
            }
            else
            {
                Array.Clear(Assumed, 0, subresourceCount);
                Array.Clear(Current, 0, subresourceCount);
            }
            Count = subresourceCount;
        }
    }
}
#endif
