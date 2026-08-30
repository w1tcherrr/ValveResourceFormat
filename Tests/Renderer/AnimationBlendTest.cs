using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using ValveResourceFormat;
using ValveResourceFormat.Renderer;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.ResourceTypes.ModelAnimation;

namespace Tests.Renderer
{
    public class AnimationBlendTest
    {

        // archer_turns is a 1D pose blend across three static "turn" poses at keys [-1, 0, 1] on the
        // "turn" pose parameter - the same authoring shape as Hoodwink's mouse_run_squirrel_turn_blend
        // (issue #1330/#1334), and small enough to ship as a committed fixture.
        private static Resource ReadModel()
        {
            var resource = new Resource();
            resource.Read(TestFixtures.Path("necro_archer.vmdl_c"));
            return resource;
        }

        [Test]
        public async Task PoseParametersAreReadFromTheSequenceGroup()
        {
            using var resource = ReadModel();
            var model = (Model)resource.DataBlock!;

            var poseParameters = model.GetPoseParameters();

            await Assert.That(poseParameters).Count().IsEqualTo(1);

            var turn = poseParameters[0];

            using (Assert.Multiple())
            {
                await Assert.That(turn.Name).IsEqualTo("turn");
                await Assert.That(turn.Min).IsEqualTo(-1f);
                await Assert.That(turn.Max).IsEqualTo(1f);
                await Assert.That(turn.Clamp(5f)).IsEqualTo(1f);
                await Assert.That(turn.Clamp(-5f)).IsEqualTo(-1f);
            }
        }

        [Test]
        public async Task BlendResolvesItsReferencesAndPoseParameterByName()
        {
            using var resource = ReadModel();
            var model = (Model)resource.DataBlock!;
            var turns = (SequenceAnimation)model.GetEmbeddedAnimations().Single(a => a.Name == "archer_turns");

            using (Assert.Multiple())
            {
                await Assert.That(turns.IsBlend).IsTrue();
                // m_nLocalPose always carries 2 slots (one per blend dimension); a 1D blend only uses
                // the first and leaves the second at -1, which resolves to an empty name.
                await Assert.That(turns.PoseParameterNames).IsEquivalentTo(["turn", ""]);
                await Assert.That(turns.BlendReferenceNames).IsEquivalentTo([
                    "@archer_turns_lookFrame_0",
                    "@archer_turns_lookFrame_1",
                    "@archer_turns_lookFrame_2",
                ]);
            }
        }

        // The core ask: bracketing pose keys split the weight linearly, and the single nearest key takes
        // it all once the value reaches or passes it.
        [Test]
        public async Task OneDBlendWeightsBracketTheLivePoseParameterValue()
        {
            using var resource = ReadModel();
            var model = (Model)resource.DataBlock!;
            var animations = model.GetEmbeddedAnimations().ToDictionary(a => a.Name, a => (Animation)a, System.StringComparer.OrdinalIgnoreCase);
            var turns = animations["archer_turns"];

            var controller = new AnimationController(model.Skeleton, model.FlexControllers)
            {
                AnimationLookup = name => animations.GetValueOrDefault(name),
            };

            foreach (var poseParameter in model.GetPoseParameters())
            {
                controller.RegisterPoseParameter(poseParameter);
            }

            controller.SetAnimation(turns);

            float WeightAt(float turnValue, int blendIndex)
            {
                controller.SetPoseParameter("turn", turnValue);
                controller.Update(0f);
                return controller.Clips[$"archer_turns$blend{blendIndex}"].Weight;
            }

            using (Assert.Multiple())
            {
                // At key -1: all weight on reference 0.
                await Assert.That(WeightAt(-1f, 0)).IsEqualTo(1f);
                await Assert.That(WeightAt(-1f, 1)).IsEqualTo(0f);
                await Assert.That(WeightAt(-1f, 2)).IsEqualTo(0f);

                // At key 0: all weight on reference 1.
                await Assert.That(WeightAt(0f, 0)).IsEqualTo(0f);
                await Assert.That(WeightAt(0f, 1)).IsEqualTo(1f);
                await Assert.That(WeightAt(0f, 2)).IsEqualTo(0f);

                // At key 1: all weight on reference 2.
                await Assert.That(WeightAt(1f, 0)).IsEqualTo(0f);
                await Assert.That(WeightAt(1f, 1)).IsEqualTo(0f);
                await Assert.That(WeightAt(1f, 2)).IsEqualTo(1f);

                // Halfway between keys -1 and 0: split evenly between references 0 and 1.
                await Assert.That(WeightAt(-0.5f, 0)).IsEqualTo(0.5f);
                await Assert.That(WeightAt(-0.5f, 1)).IsEqualTo(0.5f);
                await Assert.That(WeightAt(-0.5f, 2)).IsEqualTo(0f);
            }
        }

        // Drives the controller directly and checks the composed world-space pose itself, not just the
        // clip weights: at each extreme it must match composing that single reference pose's additive
        // delta onto the bind pose (what the mixer does with exactly one full-weight contributor), and it
        // must actually differ between two different parameter values.
        [Test]
        public async Task OneDBlendComposesTheExpectedPoseAtEachExtreme()
        {
            using var resource = ReadModel();
            var model = (Model)resource.DataBlock!;
            var animations = model.GetEmbeddedAnimations().ToDictionary(a => a.Name, a => (Animation)a, System.StringComparer.OrdinalIgnoreCase);
            var turns = animations["archer_turns"];
            var lookFrame0 = animations["@archer_turns_lookFrame_0"];
            var lookFrame2 = animations["@archer_turns_lookFrame_2"];

            var controller = new AnimationController(model.Skeleton, model.FlexControllers)
            {
                AnimationLookup = name => animations.GetValueOrDefault(name),
            };

            foreach (var poseParameter in model.GetPoseParameters())
            {
                controller.RegisterPoseParameter(poseParameter);
            }

            controller.SetAnimation(turns);

            controller.SetPoseParameter("turn", -1f);
            controller.Update(0f);
            var poseAtMinusOne = controller.Pose.ToArray();

            controller.SetPoseParameter("turn", 1f);
            controller.Update(0f);
            var poseAtPlusOne = controller.Pose.ToArray();

            var expectedAtMinusOne = ComposeOverBindPose(model, lookFrame0);
            var expectedAtPlusOne = ComposeOverBindPose(model, lookFrame2);

            using (Assert.Multiple())
            {
                for (var i = 0; i < model.Skeleton.Bones.Length; i++)
                {
                    await Assert.That(poseAtMinusOne[i]).IsEqualTo(expectedAtMinusOne[i])
                        .Because($"bone {model.Skeleton.Bones[i].Name} at turn=-1 should match lookFrame_0 composed onto the bind pose");
                    await Assert.That(poseAtPlusOne[i]).IsEqualTo(expectedAtPlusOne[i])
                        .Because($"bone {model.Skeleton.Bones[i].Name} at turn=1 should match lookFrame_2 composed onto the bind pose");
                }
            }

            var directlyAnimated = Enumerable.Range(0, model.Skeleton.Bones.Length)
                .Count(i => poseAtMinusOne[i] != poseAtPlusOne[i]);

            await Assert.That(directlyAnimated).IsGreaterThan(0)
                .Because("the composed pose must actually change between the two extreme parameter values");
        }

        // Reproduces issue #1334's real shape end to end: a blend (archer_turns) reached only through
        // another sequence's auto layer (archer_run), not activated directly. The blend must still track
        // the live pose parameter while archer_run plays. Both samples use a zero timestep so archer_run's
        // own cycle position stays fixed and the pose parameter is the only thing that changes.
        [Test]
        public async Task BlendReachedThroughAnAutoLayerTracksItsPoseParameter()
        {
            using var resource = ReadModel();
            var model = (Model)resource.DataBlock!;
            var animations = model.GetEmbeddedAnimations().ToDictionary(a => a.Name, a => (Animation)a, System.StringComparer.OrdinalIgnoreCase);
            var run = (SequenceAnimation)animations["archer_run"];

            var controller = new AnimationController(model.Skeleton, model.FlexControllers)
            {
                AnimationLookup = name => animations.GetValueOrDefault(name),
            };

            foreach (var poseParameter in model.GetPoseParameters())
            {
                controller.RegisterPoseParameter(poseParameter);
            }

            controller.SetAnimation(run);
            controller.Update(0f);

            var layerKey = controller.Clips.Keys.Single(k =>
                k.StartsWith("archer_run$autolayer", System.StringComparison.Ordinal) && !k.Contains("$blend", System.StringComparison.Ordinal));

            controller.SetPoseParameter("turn", -1f);
            controller.Update(0f);
            var poseAtMinusOne = controller.Pose.ToArray();
            var blendWeightAtMinusOne = controller.Clips[$"{layerKey}$blend0"].Weight;

            controller.SetPoseParameter("turn", 1f);
            controller.Update(0f);
            var poseAtPlusOne = controller.Pose.ToArray();
            var blendWeightAtPlusOne = controller.Clips[$"{layerKey}$blend2"].Weight;

            using (Assert.Multiple())
            {
                await Assert.That(blendWeightAtMinusOne).IsEqualTo(1f)
                    .Because("turn=-1 is exactly the first blend key");
                await Assert.That(blendWeightAtPlusOne).IsEqualTo(1f)
                    .Because("turn=1 is exactly the last blend key");
            }

            var directlyAnimated = Enumerable.Range(0, model.Skeleton.Bones.Length)
                .Count(i => poseAtMinusOne[i] != poseAtPlusOne[i]);

            await Assert.That(directlyAnimated).IsGreaterThan(0)
                .Because("the turn layer's blend must vary the pose while archer_run keeps playing - this is issue #1334's reported defect");
        }

        // Without a lookup, a blend's references cannot resolve to any clip, so it gracefully falls back
        // to its own frame data (the first reference) exactly as it did before this feature existed.
        [Test]
        public async Task BlendWithoutALookupFallsBackToItsOwnFrameData()
        {
            using var resource = ReadModel();
            var model = (Model)resource.DataBlock!;
            var turns = model.GetEmbeddedAnimations().Single(a => a.Name == "archer_turns");

            var controller = new AnimationController(model.Skeleton, model.FlexControllers);
            controller.SetAnimation(turns);
            controller.Update(0f);

            await Assert.That(controller.Clips).DoesNotContainKey("archer_turns$blend0");
            await Assert.That(controller.IsUsingMixer).IsFalse();
        }

        // Uses a fresh cache per call - AnimationFrameCache.GetFrame keys its cache purely by frame
        // index, not by which animation asked for it, so reusing one across two different single-frame
        // animations both asking for frame 0 would silently return the first one's stale decode.
        private static Matrix4x4[] ComposeOverBindPose(Model model, Animation reference)
        {
            var frameCache = new AnimationFrameCache(model.Skeleton, model.FlexControllers);
            var decoded = frameCache.GetFrame(reference, 0);
            var expectedFrame = new Frame(model.Skeleton, model.FlexControllers);

            for (var i = 0; i < model.Skeleton.Bones.Length; i++)
            {
                var delta = reference.GetAdditiveDelta(i, decoded.Bones[i]);
                expectedFrame.Bones[i] = expectedFrame.Bones[i].BlendAdd(delta, 1f);
            }

            var pose = new Matrix4x4[model.Skeleton.Bones.Length];
            foreach (var root in model.Skeleton.Roots)
            {
                Skeleton.ComputeWorldSubtree(root, Matrix4x4.Identity, expectedFrame, pose);
            }

            return pose;
        }
    }
}
