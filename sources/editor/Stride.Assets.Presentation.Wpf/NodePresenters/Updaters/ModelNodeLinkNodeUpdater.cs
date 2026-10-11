// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System;
using System.Collections.Generic;
using System.Linq;
using Stride.Core.Assets.Editor.Quantum.NodePresenters;
using Stride.Core.Assets.Editor.ViewModel;
using Stride.Assets.Models;
using Stride.Assets.Presentation.NodePresenters.Keys;
using Stride.Engine;
using Stride.Rendering;
using Stride.SpriteStudio.Offline;
using Stride.SpriteStudio.Runtime;

namespace Stride.Assets.Presentation.NodePresenters.Updaters
{
    internal sealed class ModelNodeLinkNodeUpdater : AssetNodePresenterUpdaterBase
    {
        protected override void UpdateNode(IAssetNodePresenter node)
        {
            var entity = node.Root.Value as Entity;
            var asset = node.Asset;
            if (asset == null || entity == null)
                return;

            if (node.Name == nameof(ModelNodeLinkComponent.Target) && node.Parent?.Value is ModelNodeLinkComponent)
            {
                var parent = (IAssetNodePresenter)node.Parent;
                parent.AttachedProperties.Set(ModelNodeLinkData.Key, GetAvailableNodesForLink(asset, (ModelNodeLinkComponent)parent?.Value));
            }

            if (node.Name == nameof(SpriteStudioNodeLinkComponent.Target) && node.Parent?.Value is SpriteStudioNodeLinkComponent)
            {
                var parent = (IAssetNodePresenter)node.Parent;
                parent.AttachedProperties.Set(ModelNodeLinkData.Key, GetAvailableNodesForLink(asset, (SpriteStudioNodeLinkComponent)parent?.Value));
            }
            var physicsComponent = node.Value as PhysicsComponent;
            if (physicsComponent != null)
            {
                node.AttachedProperties.Set(ModelNodeLinkData.Key, GetAvailableNodesForLink(asset, physicsComponent));
            }

            // Any other string member named NodeName lists the nodes of the model of the entity its owner refers to, or of the edited entity.
            // Updated from the reference too, so changing it refreshes the list.
            if (node.Parent is IAssetNodePresenter owner && owner.Value is not (null or ModelNodeLinkComponent or SpriteStudioNodeLinkComponent or PhysicsComponent)
                && (node.Name == NodeNameMember || IsEntityReference(node.Type)) && owner.TryGetChild(NodeNameMember)?.Type == typeof(string))
            {
                owner.AttachedProperties.Set(ModelNodeLinkData.Key, GetAvailableNodesForLink(asset, GetReferencedModel(owner, entity)));
            }
        }

        private const string NodeNameMember = nameof(ModelNodeLinkComponent.NodeName);

        private static bool IsEntityReference(Type type) => typeof(Entity).IsAssignableFrom(type) || typeof(EntityComponent).IsAssignableFrom(type);

        private static Model GetReferencedModel(IAssetNodePresenter owner, Entity entity)
        {
            foreach (var member in owner.Children)
            {
                if (IsEntityReference(member.Type) && (member.Value as Entity ?? (member.Value as EntityComponent)?.Entity)?.Get<ModelComponent>()?.Model is { } model)
                    return model;
            }
            return entity.Get<ModelComponent>()?.Model;
        }

        private static IEnumerable<NodeInformation> GetAvailableNodesForLink(AssetViewModel viewModel, ModelNodeLinkComponent modelNodeLinkComponent)
        {
            return GetAvailableNodesForLink(viewModel, modelNodeLinkComponent?.Target?.Model ?? modelNodeLinkComponent?.Entity?.Transform.Parent?.Entity?.Get<ModelComponent>()?.Model);
        }

        private static IEnumerable<NodeInformation> GetAvailableNodesForLink(AssetViewModel viewModel, SpriteStudioNodeLinkComponent spriteStudioNodeLinkComponent)
        {
            return GetAvailableNodesForLink(viewModel, spriteStudioNodeLinkComponent?.Target?.Sheet ?? spriteStudioNodeLinkComponent?.Entity?.Transform.Parent?.Entity?.Get<SpriteStudioComponent>()?.Sheet);
        }

        private static IEnumerable<NodeInformation> GetAvailableNodesForLink(AssetViewModel viewModel, PhysicsComponent physicsComponent)
        {
            return GetAvailableNodesForLink(viewModel, physicsComponent?.Entity?.Get<ModelComponent>()?.Model);
            //todo physics is kinda independant from the rest so i don't wanna reference sprite studio stuff in it... for now physics can be achieved by sprite studio node link
            //var spriteStudioSheet = physicsComponent?.Entity?.Get(SpriteStudioComponent.Key)?.Sheet;
            //if (spriteStudioSheet != null)
            //{
            //    UpdateAvailableChoices(viewModel, spriteStudioSheet, availableChoices);
            //}
        }

        private static IEnumerable<NodeInformation> GetAvailableNodesForLink(AssetViewModel viewModel, Model model)
        {
            var parentModelAsset = viewModel?.AssetItem.Package.Session.FindAssetFromProxyObject(model);
            var modelAsset = parentModelAsset?.Asset as ModelAsset;
            if (modelAsset != null)
            {
                var skeletonAsset = parentModelAsset.Package.FindAssetFromProxyObject(modelAsset.Skeleton);
                if (skeletonAsset != null)
                {
                    return ((SkeletonAsset)skeletonAsset.Asset).Nodes;
                }
            }
            return Enumerable.Empty<NodeInformation>();
        }

        private static IEnumerable<NodeInformation> GetAvailableNodesForLink(AssetViewModel viewModel, SpriteStudioSheet sheet)
        {
            var parentModelAsset = viewModel.AssetItem.Package.Session.FindAssetFromProxyObject(sheet);
            var modelAsset = parentModelAsset?.Asset as SpriteStudioModelAsset;
            if (modelAsset != null)
            {
                return modelAsset.NodeNames.Select(nodeName => new NodeInformation(nodeName, 0, true));
            }
            return Enumerable.Empty<NodeInformation>();
        }
    }
}
