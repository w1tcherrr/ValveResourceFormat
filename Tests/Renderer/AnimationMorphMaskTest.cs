using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using ValveResourceFormat;
using ValveResourceFormat.Renderer;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.ResourceTypes.ModelAnimation;

namespace Tests.Renderer
{
    public class AnimationMorphMaskTest
    {
        private static string FilePath(string name)
            => Path.Combine(TestContext.TestDirectory!, "Files", name);

        // A minimal animation whose flex controller values are fixed regardless of playback time, so the
        // test isolates mask scoping (AnimationPlayer.Mixer.cs's GetBlendedFrame) from frame decoding and
        // interpolation, neither of which this feature touches.
        private sealed class FixedFlexAnimation : Animation
        {
            private readonly float[] values;

            public FixedFlexAnimation(string name, float[] values)
            {
                Name = name;
                Fps = 30f;
                FrameCount = 1;
                this.values = values;
            }

            public override void DecodeFrame(Frame outFrame) => values.CopyTo(outFrame.Datas, 0);

            public override FrameBone GetAdditiveDelta(int boneIndex, FrameBone bone) => bone;

            public override bool HasMovementData() => false;

            public override AnimationMovement.MovementData GetMovementOffsetData(float time) => default;

            public override AnimationMovement.MovementData GetMovementOffsetData(int frame) => default;
        }

        // Reuses necro_archer's committed skeleton purely to get a working AnimationController - this
        // test does not exercise bone masking or care about the skeleton's shape. Its own model has no
        // MRPH block (m_nMorphBlock == -1), so the flex controllers here are synthetic rather than the
        // model's own (empty) set - a mask scopes flex controllers by name, independent of the skeleton
        // it happens to be registered against.
        private static Skeleton LoadSkeleton()
        {
            using var resource = new Resource();
            resource.Read(FilePath("necro_archer.vmdl_c"));
            return ((Model)resource.DataBlock!).Skeleton;
        }

        [Test]
        public async Task MaskedLayerOnlyMovesThePermittedFlexControllers()
        {
            var flexControllers = new[]
            {
                new ValveResourceFormat.ResourceTypes.ModelFlex.FlexController("browLowerer", "default", 0f, 1f),
                new ValveResourceFormat.ResourceTypes.ModelFlex.FlexController("jawClencher", "default", 0f, 1f),
                new ValveResourceFormat.ResourceTypes.ModelFlex.FlexController("cheekPuff", "default", 0f, 1f),
            };

            var controller = new AnimationController(LoadSkeleton(), flexControllers);

            var baseAnim = new FixedFlexAnimation("base", [0.2f, 0.2f, 0.2f]);
            var layerAnim = new FixedFlexAnimation("layer", [1f, 1f, 1f]);

            // "less_brow" permits browLowerer explicitly, excludes jawClencher explicitly, and leaves
            // cheekPuff unlisted - which must default to 1 (unrestricted), matching the compiled data's
            // own m_flDefaultMorphCtrlWeight default of 1 when the field is absent. Deliberately cased
            // differently than the controller's own name to prove the lookup is case-insensitive, the
            // same convention AnimationDataChannel already uses to resolve channel names to indices.
            controller.RegisterMorphMask("less_brow", new Dictionary<string, float>
            {
                ["BROWLOWERER"] = 1f,
                ["jawClencher"] = 0f,
            });

            controller.SetAnimation(baseAnim);
            controller.Clips["layer"] = new AnimationPlayer.PlaybackClip(layerAnim)
            {
                IsAdditive = true,
                Weight = 1f,
                BoneMask = "less_brow",
            };

            controller.Update(0f);

            var datas = controller.AnimationFrame!.Datas;

            using (Assert.Multiple())
            {
                await Assert.That(datas[0]).IsEqualTo(1.2f).Within(0.0001f)
                    .Because("browLowerer is explicitly permitted (weight 1) - the layer's additive delta applies in full");
                await Assert.That(datas[1]).IsEqualTo(0.2f).Within(0.0001f)
                    .Because("jawClencher is explicitly excluded (weight 0) - only the base clip's value survives");
                await Assert.That(datas[2]).IsEqualTo(1.2f).Within(0.0001f)
                    .Because("cheekPuff is unlisted in the mask and must default to 1 (unrestricted), not 0");
            }
        }

        [Test]
        public async Task UnmaskedLayerMovesEveryFlexController()
        {
            var flexControllers = new[]
            {
                new ValveResourceFormat.ResourceTypes.ModelFlex.FlexController("browLowerer", "default", 0f, 1f),
                new ValveResourceFormat.ResourceTypes.ModelFlex.FlexController("jawClencher", "default", 0f, 1f),
            };

            var controller = new AnimationController(LoadSkeleton(), flexControllers);

            var baseAnim = new FixedFlexAnimation("base", [0.2f, 0.2f]);
            var layerAnim = new FixedFlexAnimation("layer", [1f, 1f]);

            controller.SetAnimation(baseAnim);
            controller.Clips["layer"] = new AnimationPlayer.PlaybackClip(layerAnim)
            {
                IsAdditive = true,
                Weight = 1f,
            };

            controller.Update(0f);

            var datas = controller.AnimationFrame!.Datas;

            using (Assert.Multiple())
            {
                await Assert.That(datas[0]).IsEqualTo(1.2f).Within(0.0001f);
                await Assert.That(datas[1]).IsEqualTo(1.2f).Within(0.0001f);
            }
        }
    }
}
