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

    [Fact]
    public void Sort_CustomComparer_AllocatesNoMoreThanDefaultComparer()
    {
        var items = new int[64];
        long MeasureSort(IComparer<int> comparer)
        {
            for (int i = 0; i < items.Length; i++)
                items[i] = (i * 37) % items.Length;
            var before = GC.GetAllocatedBytesForCurrentThread();
            Dispatcher.Sort(items, 0, items.Length, comparer);
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        // Warm up (JIT, pools)
        MeasureSort(Comparer<int>.Default);
        MeasureSort(KeyComparer.Instance);

        Assert.Equal(MeasureSort(Comparer<int>.Default), MeasureSort(KeyComparer.Instance));
    }

    [Fact]
    public void Sort_CustomComparerWithTies_MatchesArraySort()
    {
        // Few distinct keys: equal items keep the same relative order as Array.Sort, which is not stable
        var items = new Item[500];
        for (int i = 0; i < items.Length; i++)
            items[i] = new Item((i * 7919) % 13);
        var expected = (Item[])items.Clone();
        Array.Sort(expected, ItemComparer.Instance);

        Dispatcher.Sort(items, 0, items.Length, ItemComparer.Instance);

        for (int i = 0; i < items.Length; i++)
            Assert.Same(expected[i], items[i]);
    }

    private sealed class Item(int key)
    {
        public readonly int Key = key;
    }

    private sealed class ItemComparer : IComparer<Item>
    {
        public static readonly ItemComparer Instance = new();

        public int Compare(Item? x, Item? y) => x!.Key.CompareTo(y!.Key);
    }

    private sealed class KeyComparer : IComparer<int>
    {
        public static readonly KeyComparer Instance = new();

        public int Compare(int x, int y) => x.CompareTo(y);
    }
}
