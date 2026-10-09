// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using Xunit;
using Stride.Core.IO;
using Stride.Core.MostRecentlyUsedFiles;
using Stride.Core.Settings;

namespace Stride.Core.Assets.Editor.Tests
{
    public class TestMostRecentlyUsedFiles
    {
        private static MostRecentlyUsedFileCollection CreateCollection()
        {
            var container = new SettingsContainer();
            var key = new SettingsKey<MRUDictionary>("Internal/MostRecentlyUsedSessions", container, () => new MRUDictionary());
            return new MostRecentlyUsedFileCollection(() => container.CurrentProfile, key, null);
        }

        [Fact]
        public void TestEmptyPathIsNotRecorded()
        {
            var collection = CreateCollection();
            collection.AddFile(new UFile(""), "4.5");
            collection.AddFile(null, "4.5");
            Assert.Empty(collection.MostRecentlyUsedFiles);
        }

        [Fact]
        public void TestPathIsRecorded()
        {
            var collection = CreateCollection();
            collection.AddFile(new UFile("C:/Projects/Game/Game.sln"), "4.5");
            Assert.Single(collection.MostRecentlyUsedFiles);
        }
    }
}
