using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.IO;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.ResourceTypes.ModelAnimation;
using ValveResourceFormat.Serialization.KeyValues;

namespace Tests
{
    public class ModelExtractBlendTest
    {
        private static Resource ReadModel()
        {
            var resource = new Resource();
            resource.Read(Path.Combine(TestContext.TestDirectory!, "Files", "necro_archer.vmdl_c"));
            return resource;
        }

        [Test]
        public async Task BlendSequenceKeepsEveryAnimationItPlays()
        {
            using var resource = ReadModel();
            var model = (Model)resource.DataBlock!;

            var blend = model.GetAllAnimations(new NullFileLoader())
                .OfType<SequenceAnimation>()
                .Single(anim => anim.Name == "archer_turns");

            using (Assert.Multiple())
            {
                await Assert.That(blend.IsBlend).IsTrue();
                await Assert.That(blend.Fetch!.Value.LocalReferenceArray).Count().IsEqualTo(3);
                await Assert.That(blend.Fetch!.Value.PoseKeyArray).IsEquivalentTo([-1f, 0f, 1f]);
            }
        }

        [Test]
        public async Task BlendSequenceIsWrittenAsABlendNode()
        {
            using var resource = ReadModel();
            var vmdl = new ModelExtract(resource, new NullFileLoader()).ToValveModel();

            using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(vmdl));
            var animationList = FindNode(KVDocumentExtensions.ParseKV3(ms).Root, "AnimationList");

            var blend = FindNode(animationList!, "1DBlend");
            await Assert.That(blend).IsNotNull();

            var proxies = blend!.GetArray("blendList");

            using (Assert.Multiple())
            {
                await Assert.That(blend.GetStringProperty("name")).IsEqualTo("archer_turns");
                await Assert.That(blend.GetStringProperty("poseParam")).IsEqualTo("turn");
                await Assert.That(proxies.Select(proxy => proxy.GetStringProperty("name")))
                    .IsEquivalentTo(["@archer_turns_lookFrame_0", "@archer_turns_lookFrame_1", "@archer_turns_lookFrame_2"]);
                await Assert.That(proxies.Select(proxy => proxy.GetFloatProperty("weight")))
                    .IsEquivalentTo([-1f, 0f, 1f]);
            }
        }

        [Test]
        public async Task SequenceThatPlaysNoAnimationBecomesABindPose()
        {
            using var resource = ReadModel();
            var vmdl = new ModelExtract(resource, new NullFileLoader()).ToValveModel();

            using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(vmdl));
            var animationList = FindNode(KVDocumentExtensions.ParseKV3(ms).Root, "AnimationList");

            await Assert.That(FindNode(animationList!, "AnimBindPose")?.GetStringProperty("name")).IsEqualTo("bindPose");
        }

        [Test]
        public async Task AnimationsKeepTheirWeightListAndActivityModifiers()
        {
            var file = Path.Combine(TestContext.TestDirectory!, "Files", "alyx_hand_left.vmdl_c");
            using var resource = new Resource
            {
                FileName = file,
            };
            resource.Read(file);

            var vmdl = new ModelExtract(resource, new NullFileLoader()).ToValveModel();
            using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(vmdl));
            var animationList = FindNode(KVDocumentExtensions.ParseKV3(ms).Root, "AnimationList")!;

            var thumb = FindNamed(animationList, "@grab_thumb");
            var cylinder = FindNamed(animationList, "cylinder_ik_pose");

            using (Assert.Multiple())
            {
                await Assert.That(thumb?.GetStringProperty("weight_list_name")).IsEqualTo("wl_thumb");
                await Assert.That(cylinder?.GetStringProperty("activity_name")).IsEqualTo("ACT_CYLINDER");
                await Assert.That(FindNode(cylinder!, "ActivityModifier")?.GetStringProperty("activity_name"))
                    .IsEqualTo("ACT_NEUTRAL_REF_POSE");
            }
        }

        [Test]
        public async Task TwoDimensionalBlendKeepsItsGrid()
        {
            var file = Path.Combine(TestContext.TestDirectory!, "Files", "gem_lich.vmdl_c");
            using var resource = new Resource
            {
                FileName = file,
            };
            resource.Read(file);

            var vmdl = new ModelExtract(resource, new NullFileLoader()).ToValveModel();
            using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(vmdl));
            var blend = FindNode(KVDocumentExtensions.ParseKV3(ms).Root, "2DBlend")!;

            var rows = blend.GetArray("blend_anim_list");

            using (Assert.Multiple())
            {
                await Assert.That(blend.GetStringProperty("row_pose_param_name")).IsEqualTo("up_down");
                await Assert.That(blend.GetStringProperty("col_pose_param_name")).IsEqualTo("left_right");
                await Assert.That(blend.GetFloatArray("row_weight_list")).IsEquivalentTo([-1f, 0f, 1f]);
                await Assert.That(blend.GetFloatArray("col_weight_list")).IsEquivalentTo([-1f, 0f, 1f]);
                await Assert.That(rows).Count().IsEqualTo(3);
                await Assert.That(string.Join(',', rows[0].Select(cell => cell.Value)))
                    .IsEqualTo("@gem_lina_coordinates_right_up,@gem_lina_coordinates_up,@gem_lina_coordinates_left_up");
            }
        }

        private static KVObject? FindNamed(KVObject node, string name)
        {
            if (node.GetStringProperty("name") == name && node.GetStringProperty("_class").Length > 0)
            {
                return node;
            }

            foreach (var (_, child) in node)
            {
                if (child is KVObject childNode && FindNamed(childNode, name) is { } found)
                {
                    return found;
                }
            }

            return null;
        }

        private static KVObject? FindNode(KVObject node, string className)
        {
            if (node.GetStringProperty("_class") == className)
            {
                return node;
            }

            foreach (var (_, child) in node)
            {
                if (child is KVObject childNode && FindNode(childNode, className) is { } found)
                {
                    return found;
                }
            }

            return null;
        }
    }
}
