using System.Collections.Generic;
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
    public class AnimationAutoLayerTest
    {
        private static string FilePath(string name)
            => Path.Combine(TestContext.TestDirectory!, "Files", name);

        private static void CollectSubtree(Bone bone, HashSet<int> indices)
        {
            indices.Add(bone.Index);

            foreach (var child in bone.Children)
            {
                CollectSubtree(child, indices);
            }
        }

        // archer_run's one auto layer references archer_turns, a legacy-delta (additive) 1D pose blend
        // whose first reference is a static "turn" lean pose - the same authoring shape as Hoodwink's
        // mouse_run_squirrel_evade layering onto mouse_run_squirrel_turn_blend (issue #1330/#1334).
        [Test]
        public async Task RunSequenceComposesItsAutoLayerAdditively()
        {
            using var resource = new Resource();
            resource.Read(FilePath("necro_archer.vmdl_c"));
            var model = (Model)resource.DataBlock!;

            var animations = model.GetEmbeddedAnimations().ToDictionary(a => a.Name, a => (Animation)a, System.StringComparer.OrdinalIgnoreCase);
            var run = (SequenceAnimation)animations["archer_run"];
            var turns = animations["archer_turns"];

            using (Assert.Multiple())
            {
                await Assert.That(run.AutoLayers).Count().IsEqualTo(1);
                await Assert.That(run.AutoLayers[0].ReferencedAnimationName).IsEqualTo("archer_turns");
                await Assert.That(turns.IsAdditive).IsTrue().Because("archer_turns is authored as a legacy delta sequence");
            }

            var withLayer = new AnimationController(model.Skeleton, model.FlexControllers)
            {
                AnimationLookup = name => animations.GetValueOrDefault(name),
            };

            // archer_turns blends across the "turn" pose parameter; pin it to -1 so the layer plays its
            // first reference, matching turnFrame below (decoded from archer_turns' own frame data, which
            // is that same first reference).
            foreach (var poseParameter in model.GetPoseParameters())
            {
                withLayer.RegisterPoseParameter(poseParameter);
            }
            withLayer.SetPoseParameter("turn", -1f);

            withLayer.SetAnimation(run);
            withLayer.Update(0.05f);

            var withoutLayer = new AnimationController(model.Skeleton, model.FlexControllers);
            withoutLayer.SetAnimation(run);
            withoutLayer.Update(0.05f);

            // archer_turns is itself a 1D blend, so its layer clip now also owns blend reference clips
            // keyed with a further "$blend" suffix - exclude those to find the layer clip itself.
            var layerEntry = withLayer.Clips.Single(kv =>
                kv.Key.StartsWith("archer_run$autolayer", System.StringComparison.Ordinal) && !kv.Key.Contains("$blend", System.StringComparison.Ordinal));

            using (Assert.Multiple())
            {
                await Assert.That(layerEntry.Value.Animation).IsEqualTo(turns);
                await Assert.That(layerEntry.Value.IsAdditive).IsTrue();
                await Assert.That(layerEntry.Value.Weight).IsEqualTo(1f).Because("start == end means an always-on \"add\" layer, not a ramped one");
                await Assert.That(withoutLayer.Clips).DoesNotContainKey(layerEntry.Key).Because("no lookup means the layer target cannot resolve");
            }

            var frameCache = new AnimationFrameCache(model.Skeleton, model.FlexControllers);
            var turnFrame = frameCache.GetFrame(turns, 0);

            var directlyAnimated = new List<int>();
            for (var i = 0; i < model.Skeleton.Bones.Length; i++)
            {
                var delta = turns.GetAdditiveDelta(i, turnFrame.Bones[i]);

                if (delta.Position != Vector3.Zero || delta.Angle != Quaternion.Identity)
                {
                    directlyAnimated.Add(i);
                }
            }

            await Assert.That(directlyAnimated).IsNotEmpty().Because("the fixture would be useless if the turn layer animated no bone");

            var affectedSubtree = new HashSet<int>();
            foreach (var index in directlyAnimated)
            {
                CollectSubtree(model.Skeleton.Bones[index], affectedSubtree);
            }

            foreach (var index in directlyAnimated)
            {
                await Assert.That(withLayer.Pose[index]).IsNotEqualTo(withoutLayer.Pose[index])
                    .Because($"bone {model.Skeleton.Bones[index].Name} has a channel in the turn layer");
            }

            for (var i = 0; i < model.Skeleton.Bones.Length; i++)
            {
                if (affectedSubtree.Contains(i))
                {
                    continue;
                }

                await Assert.That(withLayer.Pose[i]).IsEqualTo(withoutLayer.Pose[i])
                    .Because($"bone {model.Skeleton.Bones[i].Name} has no animated ancestor in the turn layer");
            }
        }

        // The mixer zeroes every clip but the active/previous pair while a time-based crossfade is in
        // progress (AnimationPlayer.Mixer.cs, UpdateClips); an auto layer clip must survive that.
        [Test]
        public async Task AutoLayerWeightSurvivesACrossfade()
        {
            using var resource = new Resource();
            resource.Read(FilePath("necro_archer.vmdl_c"));
            var model = (Model)resource.DataBlock!;

            var animations = model.GetEmbeddedAnimations().ToDictionary(a => a.Name, a => (Animation)a, System.StringComparer.OrdinalIgnoreCase);
            var idle = animations["archer_idle"];
            var run = animations["archer_run"];

            var controller = new AnimationController(model.Skeleton, model.FlexControllers)
            {
                AnimationLookup = name => animations.GetValueOrDefault(name),
            };

            controller.SetAnimation(idle, 0f);
            controller.Update(0f);

            controller.SetAnimation(run, 0.2f);
            controller.Update(0.1f);

            var runClip = controller.Clips["archer_run"];
            var layerClip = controller.Clips.Single(kv =>
                kv.Key.StartsWith("archer_run$autolayer", System.StringComparison.Ordinal) && !kv.Key.Contains("$blend", System.StringComparison.Ordinal)).Value;

            using (Assert.Multiple())
            {
                await Assert.That(runClip.Weight).IsGreaterThan(0f).Because("half of a 0.2s crossfade has elapsed");
                await Assert.That(runClip.Weight).IsLessThan(1f).Because("the crossfade is not finished yet");
                await Assert.That(layerClip.Weight).IsGreaterThan(0f)
                    .Because("the crossfade's per-tick zero-out of every other clip must not survive past the auto layer update");
                await Assert.That(layerClip.Weight).IsEqualTo(runClip.Weight).Within(0.001f)
                    .Because("start == end gives the layer full curve weight, scaled only by its owner clip's own blend weight");
            }
        }
    }
}
