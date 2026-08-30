using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TUnit.Assertions.Enums;
using ValveResourceFormat;
using ValveResourceFormat.IO;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;

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

        /// <summary>
        /// A model doc list node is created the first time a section writes into it, so the order the
        /// sections run in is the order the reader sees, and a list nothing wrote is absent rather than
        /// empty. Pinned per model because which sections run at all depends on what the model carries.
        /// </summary>
        [Test]
        public async Task WritesOnlyTheDocSectionsTheModelFills()
        {
            using (Assert.Multiple())
            {
                await Assert.That(RootSections("necro_archer.vmdl_c")).IsEquivalentTo(
                    ["BoneMarkupList", "RenderMeshList", "BodyGroupList", "AttachmentList", "PoseParamList", "AnimationList", "HitboxSetList", "Skeleton"],
                    CollectionOrdering.Matching);

                await Assert.That(RootSections("box_creature_ik_model.vmdl_c")).IsEquivalentTo(
                    ["BoneMarkupList", "RenderMeshList", "AttachmentList", "WeightListList", "AnimationList", "IKData", "GameDataList", "Skeleton"],
                    CollectionOrdering.Matching);

                await Assert.That(RootSections("alyx_hand_left.vmdl_c")).IsEquivalentTo(
                    ["BoneMarkupList", "RenderMeshList", "AttachmentList", "WeightListList", "PoseParamList", "AnimationList", "IKData", "HitboxSetList", "Skeleton", "PhysicsBodyMarkupList", "PhysicsShapeList"],
                    CollectionOrdering.Matching);

                // Only the LOD fixture reaches LODGroupList, and it has no skeleton to write.
                await Assert.That(RootSections("lod_test.vmdl_c")).IsEquivalentTo(
                    ["BoneMarkupList", "RenderMeshList", "LODGroupList"],
                    CollectionOrdering.Matching);

                // Every mesh of this one is an external reference the null loader cannot resolve, so
                // nothing downstream of the meshes has anything to write.
                await Assert.That(RootSections("alchemist.vmdl_c")).IsEquivalentTo(
                    ["BoneMarkupList", "Skeleton"],
                    CollectionOrdering.Matching);
            }
        }

        private static string[] RootSections(string fileName)
        {
            using var resource = new Resource();
            resource.Read(Path.Combine(TestContext.TestDirectory!, "Files", fileName));

            var vmdl = new ModelExtract(resource, new NullFileLoader()).ToValveModel();
            using var ms = new MemoryStream(Encoding.UTF8.GetBytes(vmdl));

            return [.. KVDocumentExtensions.ParseKV3(ms).Root
                .GetSubCollection("rootNode")
                .GetArray("children")
                .Select(child => child.GetStringProperty("_class"))];
        }
    }
}
