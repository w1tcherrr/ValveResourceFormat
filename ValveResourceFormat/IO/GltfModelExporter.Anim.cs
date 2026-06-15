using System.Diagnostics;
using System.Linq;
using SharpGLTF.Schema2;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.ResourceTypes.ModelAnimation;
using ValveResourceFormat.ResourceTypes.ModelFlex;
using VAnim = ValveResourceFormat.ResourceTypes.ModelAnimation.Animation;
using VAnimationClip = ValveResourceFormat.ResourceTypes.ModelAnimation2.AnimationClip;
using VModel = ValveResourceFormat.ResourceTypes.Model;

namespace ValveResourceFormat.IO;

public partial class GltfModelExporter
{
    /// <summary>
    /// Manages the writing of skeletal animation data to glTF format.
    /// </summary>
    public class AnimationWriter
    {
        Skeleton Skeleton { get; init; }
        Frame Frame { get; init; }
        int BoneCount => Frame.Bones.Length;

        AnimationChannelWriter<Quaternion> RotationWriter;
        AnimationChannelWriter<Vector3> PositionWriter;
        AnimationChannelWriter<Vector3> ScaleWriter;

        /// <summary>
        /// Initializes a new instance of the <see cref="AnimationWriter"/> class.
        /// </summary>
        public AnimationWriter(Skeleton skeleton, FlexController[] flexControllers)
        {
            Skeleton = skeleton;
            Frame = new(skeleton, flexControllers);

            RotationWriter = AnimationChannelWriter<Quaternion>.Create(BoneCount);
            PositionWriter = AnimationChannelWriter<Vector3>.Create(BoneCount);
            ScaleWriter = AnimationChannelWriter<Vector3>.Create(BoneCount);
        }

        /// <summary>
        /// Writes a skeletal animation to the glTF model. Entries in <paramref name="joints"/> may be
        /// null when an animation targets a skeleton with bones the exported model does not have
        /// (e.g. animation graph clips retargeted by bone name); those bones are skipped.
        /// </summary>
        public void WriteAnimation(ModelRoot model, Node?[] joints, VAnim animation)
        {
            Debug.Assert(joints.Length == BoneCount);

            // Cleanup state
            Frame.Clear(Skeleton);

            RotationWriter.Clear();
            PositionWriter.Clear();
            ScaleWriter.Clear();

            var outputAnimation = model.UseAnimation(animation.Name);

            var fps = animation.Fps;

            // Some models have fps of 0.000, which will make time a NaN
            if (fps == 0)
            {
                fps = 1f;
            }

            // bake additive clips over the bind pose, same as the renderer
            var additive = animation.Clip?.IsAdditive ?? false;

            for (var f = 0; f < animation.FrameCount; f++)
            {
                Frame.FrameIndex = f;
                animation.DecodeFrame(Frame);

                if (additive)
                {
                    for (var boneID = 0; boneID < BoneCount; boneID++)
                    {
                        var bind = new FrameBone(Skeleton.Bones[boneID].Position, 1f, Skeleton.Bones[boneID].Angle);
                        Frame.Bones[boneID] = Frame.Bones[boneID].BlendAdd(bind, 1f);
                    }
                }

                var time = f / fps;
                var prevFrameTime = (f - 1) / fps;

                for (var boneID = 0; boneID < BoneCount; boneID++)
                {
                    var boneFrame = Frame.Bones[boneID];

                    RotationWriter.SubmitKeyframe(boneID, time, prevFrameTime, boneFrame.Angle);
                    PositionWriter.SubmitKeyframe(boneID, time, prevFrameTime, boneFrame.Position);

                    var scalarBoneScale = boneFrame.Scale;

                    if (float.IsNaN(scalarBoneScale) || float.IsInfinity(scalarBoneScale))
                    {
                        // See https://github.com/ValveResourceFormat/ValveResourceFormat/issues/527 (NaN)
                        // and https://github.com/ValveResourceFormat/ValveResourceFormat/issues/570 (inf)
                        scalarBoneScale = 0.0f;
                    }

                    ScaleWriter.SubmitKeyframe(boneID, time, prevFrameTime, new Vector3(scalarBoneScale));
                }
            }

            for (var boneID = 0; boneID < BoneCount; boneID++)
            {
                if (animation.FrameCount == 0)
                {
                    RotationWriter.Channels[boneID].Add(0f, Skeleton.Bones[boneID].Angle);
                    PositionWriter.Channels[boneID].Add(0f, Skeleton.Bones[boneID].Position);
                    ScaleWriter.Channels[boneID].Add(0f, Vector3.One);
                }

                var jointNode = joints[boneID];
                if (jointNode == null)
                {
                    continue;
                }

                outputAnimation.CreateRotationChannel(jointNode, RotationWriter.Channels[boneID], true);
                outputAnimation.CreateTranslationChannel(jointNode, PositionWriter.Channels[boneID], true);
                outputAnimation.CreateScaleChannel(jointNode, ScaleWriter.Channels[boneID], true);
            }
        }
    }

    // Animation-graph clips aren't part of GetAllAnimations; write them here, retargeted by bone name.
    private void WriteAnimationGraphClips(ModelRoot exportedModel, VModel model, Node[] joints, HashSet<string> animationFilter)
    {
        var retargets = new Dictionary<string, (AnimationWriter Writer, Node?[] Joints)?>();

        foreach (var clipName in AnimationGraphLoader.GetClipNames(model, FileLoader))
        {
            CancellationToken.ThrowIfCancellationRequested();

            if (FileLoader.LoadFileCompiled(clipName)?.DataBlock is not VAnimationClip clip
                || (animationFilter.Count > 0 && !animationFilter.Contains(clip.Name)))
            {
                continue;
            }

            if (!retargets.TryGetValue(clip.SkeletonName, out var retarget))
            {
                retargets[clip.SkeletonName] = retarget = BuildClipRetarget(model, joints, clip.SkeletonName);
            }

            if (retarget != null)
            {
                retarget.Value.Writer.WriteAnimation(exportedModel, retarget.Value.Joints, new VAnim(clip));
            }
        }
    }

    // Loads a clip's skeleton and maps its bones onto the model's joints by name. Null if none match.
    private (AnimationWriter Writer, Node?[] Joints)? BuildClipRetarget(VModel model, Node[] joints, string clipSkeletonName)
    {
        if (FileLoader.LoadFileCompiled(clipSkeletonName)?.DataBlock is not BinaryKV3 skeletonData)
        {
            return null;
        }

        var clipSkeleton = Skeleton.FromSkeletonData(skeletonData.Data);
        var remappedJoints = new Node?[clipSkeleton.Bones.Length];
        var matched = false;

        for (var i = 0; i < clipSkeleton.Bones.Length; i++)
        {
            var modelBone = model.Skeleton[clipSkeleton.Bones[i].Name];
            if (modelBone != null)
            {
                remappedJoints[i] = joints[modelBone.Index];
                matched = true;
            }
        }

        return matched ? (new AnimationWriter(clipSkeleton, model.FlexControllers), remappedJoints) : null;
    }

    record struct AnimationChannelWriter<T>(Dictionary<float, T>[] Channels, T?[] LastValue, bool[] ValueOmmited) where T : struct
    {
        public static AnimationChannelWriter<T> Create(int boneCount) => new()
        {
            Channels = [.. Enumerable.Range(0, boneCount).Select(_ => new Dictionary<float, T>())],
            LastValue = new T?[boneCount],
            ValueOmmited = new bool[boneCount],
        };

        public readonly void SubmitKeyframe(int boneID, float time, float prevTime, T value)
        {
            var lastValue = LastValue[boneID];

            if (lastValue != null && lastValue.Value.Equals(value))
            {
                ValueOmmited[boneID] = true;
                return;
            }

            if (lastValue != null && ValueOmmited[boneID])
            {
                ValueOmmited[boneID] = false;

                // Restore keyframe before current frame, as otherwise interpolation will
                // begin from the first instance of identical frame, and not from previous frame
                Channels[boneID].Add(prevTime, lastValue.Value);
            }

            Channels[boneID].Add(time, value);
            LastValue[boneID] = value;
        }

        public readonly void Clear()
        {
            for (var i = 0; i < Channels.Length; i++)
            {
                Channels[i].Clear();
                LastValue[i] = default; // null
                ValueOmmited[i] = false;
            }
        }
    }
}
