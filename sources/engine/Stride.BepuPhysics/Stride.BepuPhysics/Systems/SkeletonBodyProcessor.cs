// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Engine;
using Stride.Games;
using Stride.Rendering;

namespace Stride.BepuPhysics.Systems;

/// <summary>
/// Recreates the bodies of a <see cref="SkeletonBodyComponent"/> when the model of its entity changes, or once it gets one.
/// </summary>
internal sealed class SkeletonBodyProcessor : EntityProcessor<SkeletonBodyComponent, SkeletonBodyProcessor.Data>
{
    internal sealed class Data
    {
        /// <summary> The model the bodies were last built from, or failed to be built from </summary>
        public Model? Model;
    }

    protected override Data GenerateComponentData(Entity entity, SkeletonBodyComponent component) => new() { Model = entity.Get<ModelComponent>()?.Model };

    public override void Update(GameTime time)
    {
        foreach (var (component, data) in ComponentDatas)
        {
            var model = component.Entity.Get<ModelComponent>()?.Model;
            if (model is not null && model != data.Model)
            {
                data.Model = model;
                component.Rebuild();
            }
        }
    }
}
