using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ValveResourceFormat;
using ValveResourceFormat.IO;
using ValveResourceFormat.ResourceTypes;

namespace Tests
{
    public class ModelExtractTest
    {
        /// <summary>
        /// A vmesh has no model to read mesh groups, LODs, materials or a skeleton from, and map
        /// extraction reaches this path for a scene object that carries a bare renderable.
        /// </summary>
        [Test]
        public async Task ExtractsAMeshWithNoModelBehindIt()
        {
            using var resource = new Resource();
            resource.Read(Path.Combine(TestContext.TestDirectory!, "Files", "chen_weapon.vmesh_c"));

            using var content = new ModelExtract((Mesh)resource.DataBlock!, "models/heroes/chen/chen_weapon.vmesh").ToContentFile();
            var vmdl = Encoding.UTF8.GetString(content.Data!);

            using (Assert.Multiple())
            {
                await Assert.That(content.FileName).IsEqualTo("models/heroes/chen/chen_weapon.vmdl");
                await Assert.That(vmdl).Contains("_class = \"RenderMeshFile\"");
                await Assert.That(vmdl).Contains("filename = \"models/heroes/chen/chen_weapon.dmx\"");

                // Nothing a model would have contributed may be invented for a lone mesh.
                await Assert.That(vmdl).DoesNotContain("BodyGroupList");
                await Assert.That(vmdl).DoesNotContain("LODGroupList");
                await Assert.That(vmdl).DoesNotContain("MaterialGroupList");

                await Assert.That(content.SubFiles.Select(subFile => subFile.FileName))
                    .IsEquivalentTo(["chen_weapon.dmx"]);
                await Assert.That(content.SubFiles.Single().Extract!.Invoke()).IsNotEmpty();
            }
        }
    }
}
