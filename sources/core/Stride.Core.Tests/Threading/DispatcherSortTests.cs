// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core.Threading;
using Xunit;

namespace Stride.Core.Tests.Threading;

public class DispatcherSortTests
{
    [Fact]
    public void Sort_SmallArray_DoesNotAllocate()
    {
        var items = new int[64];
        void Fill()
        {
            for (int i = 0; i < items.Length; i++)
                items[i] = (i * 37) % items.Length;
        }

        // Warm up (JIT, pools)
        Fill();
        Dispatcher.Sort(items, 0, items.Length, Comparer<int>.Default);

        Fill();
        var before = GC.GetAllocatedBytesForCurrentThread();
        Dispatcher.Sort(items, 0, items.Length, Comparer<int>.Default);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        for (int i = 0; i < items.Length; i++)
            Assert.Equal(i, items[i]);
    }
}
