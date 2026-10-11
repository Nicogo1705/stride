// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

#if STRIDE_GRAPHICS_API_DIRECT3D11
using Silk.NET.Direct3D11;
using Stride.Shaders;
using Xunit;

namespace Stride.Graphics.Tests;

public class TestUnorderedAccessViewUnbinding : GraphicTestGameBase
{
    /// <summary>
    /// Binding a texture as a shader resource unbinds a UAV of one of its views,
    /// otherwise Direct3D 11 nulls the shader resource view.
    /// </summary>
    [Theory]
    [InlineData(ShaderStage.Compute)]
    [InlineData(ShaderStage.Pixel)]
    public void UavOfAViewIsUnboundWhenItsTextureIsReadAsSrv(ShaderStage stage)
    {
        // Above the back buffer, which takes the first output slot
        const int slot = 1;

        PerformTest(game =>
        {
            var commandList = game.GraphicsContext.CommandList;
            var texture = Texture.New2D(game.GraphicsDevice, 16, 16, mipCount: 2, PixelFormat.R8G8B8A8_UNorm,
                TextureFlags.ShaderResource | TextureFlags.UnorderedAccess);
            var mipView = texture.ToTextureView(new TextureViewDescription
            {
                Type = ViewType.Single,
                MipLevel = 1,
                Format = texture.Format,
                Flags = TextureFlags.UnorderedAccess,
            });

            commandList.SetUnorderedAccessView(stage, slot, mipView, -1);
            Assert.True(GetBoundUav(commandList, stage, slot) != 0, "The UAV was not bound.");

            // Same order as ResourceBinder before it binds a shader resource view
            commandList.UnsetUnorderedAccessView(texture);
            commandList.SetShaderResourceView(ShaderStage.Pixel, 0, texture);

            Assert.True(GetBoundUav(commandList, stage, slot) == 0, "The UAV of the texture's view is still bound.");

            commandList.SetShaderResourceView(ShaderStage.Pixel, 0, null);
            mipView.Dispose();
            texture.Dispose();
        });
    }

    private static unsafe nint GetBoundUav(CommandList commandList, ShaderStage stage, uint slot)
    {
        ID3D11UnorderedAccessView* uav = null;
        if (stage == ShaderStage.Compute)
            commandList.NativeDeviceContext.CSGetUnorderedAccessViews(slot, 1, &uav);
        else
            commandList.NativeDeviceContext.OMGetRenderTargetsAndUnorderedAccessViews(0, null, null, slot, 1, &uav);
        if (uav != null)
            uav->Release();
        return (nint) uav;
    }
}
#endif
