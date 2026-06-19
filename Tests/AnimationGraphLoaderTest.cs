using System.IO;
using System.Linq;
using NUnit.Framework;
using SteamDatabase.ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.IO;
using ValveResourceFormat.ResourceTypes;

namespace Tests
{
    [TestFixture]
    public class AnimationGraphLoaderTest
    {
        private const string VmatName = "materials/cs_italy/ground/tile_floor_diamond_1.vmat";

        private static (Package Package, GameFileLoader Loader) OpenSmallMap()
        {
            var vpkPath = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "small_map_with_material.vpk");
            var package = new Package();
            package.Read(vpkPath);
            return (package, new GameFileLoader(package, null));
        }

        [Test]
        public void FindFilesEnumeratesCompiledResourceNames()
        {
            var (package, loader) = OpenSmallMap();
            using (package)
            using (loader)
            {
                var materials = loader.FindFiles("vmat").ToList();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(materials, Does.Contain(VmatName));
                    Assert.That(materials, Has.All.Not.EndsWith("_c"), "FindFiles should return resource names without the _c suffix");
                }
            }
        }

        [Test]
        public void LoadFileExternalRefsMatchesFullLoad()
        {
            var (package, loader) = OpenSmallMap();
            using (package)
            using (loader)
            {
                var lightweight = loader.LoadFileExternalRefs(VmatName);
                Assert.That(lightweight, Is.Not.Null);

                using var full = loader.LoadFileCompiled(VmatName);
                Assert.That(full, Is.Not.Null);

                var lightweightNames = lightweight.ResourceRefInfoList.Select(r => r.Name).ToList();
                var fullNames = full.ExternalReferences!.ResourceRefInfoList.Select(r => r.Name).ToList();

                using (Assert.EnterMultipleScope())
                {
                    // The material references its textures; the lightweight read must see the same refs.
                    Assert.That(lightweightNames, Is.Not.Empty);
                    Assert.That(lightweightNames, Is.EquivalentTo(fullNames));
                }
            }
        }

        [Test]
        public void ClipDiscoveryIsEmptyWithoutAnEnumeratingLoader()
        {
            using var resource = new Resource();
            resource.Read(Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "box_creature_ik_model.vmdl_c"));
            var model = (Model)resource.DataBlock!;

            var loader = new NullFileLoader();

            Assert.That(AnimationGraphLoader.GetClipNames(model, loader), Is.Empty);
        }
    }
}
