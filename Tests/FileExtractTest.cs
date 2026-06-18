using System.IO;
using NUnit.Framework;
using ValveResourceFormat.IO;

namespace Tests
{
    [TestFixture]
    public class FileExtractTest
    {
        private static string Root => Path.Combine(TestContext.CurrentContext.WorkDirectory, "alpinetest");

        [Test]
        public void ResolveContentRoot_SelectingRoot_KeepsRoot()
        {
            Assert.That(FileExtract.ResolveContentRoot(Root, "maps/cs_alpine_d.vmap"), Is.EqualTo(Root));
        }

        [Test]
        public void ResolveContentRoot_SelectingMapsFolder_LiftsToRoot()
        {
            var maps = Path.Combine(Root, "maps");
            Assert.That(FileExtract.ResolveContentRoot(maps, "maps/cs_alpine_d.vmap"), Is.EqualTo(Root));
        }

        [Test]
        public void ResolveContentRoot_TrailingSeparator_LiftsToRoot()
        {
            var maps = Path.Combine(Root, "maps") + Path.DirectorySeparatorChar;
            Assert.That(FileExtract.ResolveContentRoot(maps, "maps/cs_alpine_d.vmap"), Is.EqualTo(Root));
        }

        [Test]
        public void ResolveContentRoot_NoOverlap_KeepsSelectedFolder()
        {
            var other = Path.Combine(Root, "models");
            Assert.That(FileExtract.ResolveContentRoot(other, "maps/cs_alpine_d.vmap"), Is.EqualTo(other));
        }

        [Test]
        public void ResolveContentRoot_MultiSegmentAssetDir_LiftsWholeOverlap()
        {
            var modelsProps = Path.Combine(Root, "models", "props");
            Assert.That(FileExtract.ResolveContentRoot(modelsProps, "models/props/box.vmdl"), Is.EqualTo(Root));

            var models = Path.Combine(Root, "models");
            Assert.That(FileExtract.ResolveContentRoot(models, "models/props/box.vmdl"), Is.EqualTo(Root));
        }

        [Test]
        public void ResolveContentRoot_PrimaryWithoutDirectory_KeepsSelectedFolder()
        {
            Assert.That(FileExtract.ResolveContentRoot(Root, "cs_alpine_d.vmap"), Is.EqualTo(Root));
        }
    }
}
