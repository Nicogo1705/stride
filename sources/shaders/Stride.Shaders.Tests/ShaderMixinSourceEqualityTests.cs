// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

namespace Stride.Shaders.Tests;

/// <summary>
/// <see cref="ShaderMixinSource"/> equality, used by the effect system on every effect lookup.
/// </summary>
public class ShaderMixinSourceEqualityTests
{
    private static ShaderMixinSource CreateMixin(string compositionKey = "lighting", string childClass = "LightDirectional")
    {
        var child = new ShaderMixinSource();
        child.Mixins.Add(new ShaderClassSource(childClass));

        var mixin = new ShaderMixinSource { Name = "Effect" };
        mixin.Mixins.Add(new ShaderClassSource("ShaderBase"));
        mixin.AddMacro("MAX_LIGHTS", 4);
        mixin.AddComposition(compositionKey, child);
        mixin.AddComposition("empty", null);
        return mixin;
    }

    [Fact]
    public void ComparesCompositionKeysAndValues()
    {
        Assert.True(CreateMixin().Equals(CreateMixin()));
        Assert.False(CreateMixin().Equals(CreateMixin(compositionKey: "shading")));
        Assert.False(CreateMixin().Equals(CreateMixin(childClass: "LightPoint")));

        var withoutNull = CreateMixin();
        withoutNull.AddComposition("empty", new ShaderClassSource("Item"));
        Assert.False(CreateMixin().Equals(withoutNull));
        Assert.False(withoutNull.Equals(CreateMixin()));

        var extra = CreateMixin();
        extra.AddComposition("extra", new ShaderClassSource("Item"));
        Assert.False(CreateMixin().Equals(extra));
    }

    [Fact]
    public void EqualsDoesNotAllocate()
    {
        var first = CreateMixin();
        var second = CreateMixin();
        Assert.True(first.Equals(second));

        var before = GC.GetAllocatedBytesForCurrentThread();
        var equal = first.Equals(second);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(equal);
        Assert.Equal(0, allocated);
    }
}
