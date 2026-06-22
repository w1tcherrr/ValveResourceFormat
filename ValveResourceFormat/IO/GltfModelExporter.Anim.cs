using System.Diagnostics;
using System.IO;
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
        public void WriteAnimation(ModelRoot model, Node?[] joints, VAnim animation, string? animationName = null)
        {
            Debug.Assert(joints.Length == BoneCount);

            // Cleanup state
            Frame.Clear(Skeleton);

            RotationWriter.Clear();
            PositionWriter.Clear();
            ScaleWriter.Clear();

            var outputAnimation = model.UseAnimation(animationName ?? animation.Name);

            var fps = animation.Fps;

            // Some models have fps of 0.000, which will make time a NaN
            if (fps == 0)
            {
                fps = 1f;
            }

            // root motion is stored separately from bone frames, so bake it into the root bone(s) to keep
            // the skeleton from animating in place. horizontal travel and yaw only. the engine doesn't
            // apply a vertical movement track to the body.
            var applyRootMotion = animation.HasMovementData();

            // No cloth solver here, so mirror the renderer (BaseAnimationController.GetSkinningMatrices):
            // pin each cloth root to the cloth anchor bone instead of writing its raw, solver-less clip data.
            var clothAnchor = Skeleton.ClothSimulationRoot;
            var anchorInverseBindPose = Matrix4x4.Identity;
            if (clothAnchor != null)
            {
                var anchorBindPose = Matrix4x4.Identity;
                for (var b = clothAnchor; b != null; b = b.Parent)
                {
                    anchorBindPose *= b.BindPose;
                }

                if (!Matrix4x4.Invert(anchorBindPose, out anchorInverseBindPose))
                {
                    anchorInverseBindPose = Matrix4x4.Identity;
                }
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

                var rootMotion = Matrix4x4.Identity;
                if (applyRootMotion)
                {
                    var movement = animation.GetMovementOffsetData(f);
                    var movementPosition = new Vector3(movement.Position.X, movement.Position.Y, 0f);
                    rootMotion = Matrix4x4.CreateRotationZ(float.DegreesToRadians(movement.Angle))
                        * Matrix4x4.CreateTranslation(movementPosition);
                }

                // Anchor skinning matrix this frame (renderer's modelBones[clothSimRoot]).
                var clothSkinning = Matrix4x4.Identity;
                if (clothAnchor != null)
                {
                    var anchorPose = Matrix4x4.Identity;
                    for (var b = clothAnchor; b != null; b = b.Parent)
                    {
                        var anchorFrame = Frame.Bones[b.Index];
                        var anchorScale = anchorFrame.Scale;
                        if (float.IsNaN(anchorScale) || float.IsInfinity(anchorScale))
                        {
                            anchorScale = 0.0f;
                        }

                        anchorPose *= Matrix4x4.CreateScale(anchorScale)
                            * Matrix4x4.CreateFromQuaternion(anchorFrame.Angle)
                            * Matrix4x4.CreateTranslation(anchorFrame.Position);
                    }

                    clothSkinning = anchorInverseBindPose * anchorPose;
                }

                for (var boneID = 0; boneID < BoneCount; boneID++)
                {
                    var boneFrame = Frame.Bones[boneID];

                    var position = boneFrame.Position;
                    var rotation = boneFrame.Angle;
                    var scalarBoneScale = boneFrame.Scale;

                    if (float.IsNaN(scalarBoneScale) || float.IsInfinity(scalarBoneScale))
                    {
                        // See https://github.com/ValveResourceFormat/ValveResourceFormat/issues/527 (NaN)
                        // and https://github.com/ValveResourceFormat/ValveResourceFormat/issues/570 (inf)
                        scalarBoneScale = 0.0f;
                    }

                    var scale = new Vector3(scalarBoneScale);

                    var bone = Skeleton.Bones[boneID];

                    if (clothAnchor != null && bone.Parent == null && bone.IsProceduralCloth)
                    {
                        // Pin to the anchor; cloth bones are roots, so apply root motion too.
                        var local = bone.BindPose * clothSkinning;
                        if (applyRootMotion)
                        {
                            local *= rootMotion;
                        }

                        if (Matrix4x4.Decompose(local, out var clothS, out var clothR, out var clothT))
                        {
                            position = clothT;
                            rotation = clothR;
                            scale = clothS;
                        }
                    }
                    else if (applyRootMotion && bone.Parent == null)
                    {
                        var local = Matrix4x4.CreateScale(scale)
                            * Matrix4x4.CreateFromQuaternion(rotation)
                            * Matrix4x4.CreateTranslation(position);

                        if (Matrix4x4.Decompose(local * rootMotion, out var s, out var r, out var t))
                        {
                            position = t;
                            rotation = r;
                            scale = s;
                        }
                        else
                        {
                            position += rootMotion.Translation;
                        }
                    }

                    (position, rotation) = BakeConversion(position, rotation, bone.Parent == null);

                    RotationWriter.SubmitKeyframe(boneID, time, prevFrameTime, rotation);
                    PositionWriter.SubmitKeyframe(boneID, time, prevFrameTime, position);
                    ScaleWriter.SubmitKeyframe(boneID, time, prevFrameTime, scale);
                }
            }

            for (var boneID = 0; boneID < BoneCount; boneID++)
            {
                if (animation.FrameCount == 0)
                {
                    var bone = Skeleton.Bones[boneID];
                    var (bindPosition, bindRotation) = BakeConversion(bone.Position, bone.Angle, bone.Parent == null);
                    RotationWriter.Channels[boneID].Add(0f, bindRotation);
                    PositionWriter.Channels[boneID].Add(0f, bindPosition);
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

        /// <summary>
        /// Writes a clip authored on this writer's (source) skeleton onto a different, body-posed target
        /// armature, matching bones by name and applying each bone's rotation relative to its source bind
        /// pose onto the target bind pose. This lets a body-authored first-person mesh follow an
        /// incompatible viewmodel clip without being deformed (re-skinning the mesh onto the viewmodel rig
        /// warps it, since the two rigs disagree). Target bones with no source counterpart stay at bind.
        /// </summary>
        public void WriteRetargetedAnimation(ModelRoot model, Node?[] targetJoints, Skeleton targetSkeleton, VAnim animation, string? animationName = null)
        {
            Debug.Assert(targetJoints.Length == targetSkeleton.Bones.Length);

            Frame.Clear(Skeleton);

            var targetCount = targetSkeleton.Bones.Length;
            var rotationWriter = AnimationChannelWriter<Quaternion>.Create(targetCount);
            var positionWriter = AnimationChannelWriter<Vector3>.Create(targetCount);
            var scaleWriter = AnimationChannelWriter<Vector3>.Create(targetCount);

            // target bone -> source (clip skeleton) bone index by name, or -1 when the clip lacks it.
            var sourceForTarget = new int[targetCount];
            for (var t = 0; t < targetCount; t++)
            {
                sourceForTarget[t] = Skeleton.GetBoneIndex(targetSkeleton.Bones[t].Name);
            }

            var outputAnimation = model.UseAnimation(animationName ?? animation.Name);
            var fps = animation.Fps == 0 ? 1f : animation.Fps;
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

                for (var t = 0; t < targetCount; t++)
                {
                    var targetBone = targetSkeleton.Bones[t];
                    var s = sourceForTarget[t];

                    Vector3 position;
                    Quaternion rotation;
                    Vector3 scale;

                    if (s == -1)
                    {
                        position = targetBone.Position;
                        rotation = targetBone.Angle;
                        scale = Vector3.One;
                    }
                    else
                    {
                        var sourceFrame = Frame.Bones[s];
                        var sourceBone = Skeleton.Bones[s];

                        // Apply the source bone's rotation relative to its own bind, in bone-local space,
                        // onto the target bind: targetLocal = (frame * sourceBind^-1) * targetBind.
                        var frameLocal = Matrix4x4.CreateFromQuaternion(sourceFrame.Angle) * Matrix4x4.CreateTranslation(sourceFrame.Position);
                        var sourceBind = Matrix4x4.CreateFromQuaternion(sourceBone.Angle) * Matrix4x4.CreateTranslation(sourceBone.Position);
                        var targetBind = Matrix4x4.CreateFromQuaternion(targetBone.Angle) * Matrix4x4.CreateTranslation(targetBone.Position);

                        if (Matrix4x4.Invert(sourceBind, out var sourceBindInverse)
                            && Matrix4x4.Decompose(frameLocal * sourceBindInverse * targetBind, out _, out var r, out var tr))
                        {
                            rotation = r;
                            position = tr;
                        }
                        else
                        {
                            rotation = targetBone.Angle;
                            position = targetBone.Position;
                        }

                        var boneScale = sourceFrame.Scale;
                        if (float.IsNaN(boneScale) || float.IsInfinity(boneScale))
                        {
                            boneScale = 1f;
                        }

                        scale = new Vector3(boneScale);
                    }

                    (position, rotation) = BakeConversion(position, rotation, targetBone.Parent == null);

                    rotationWriter.SubmitKeyframe(t, time, prevFrameTime, rotation);
                    positionWriter.SubmitKeyframe(t, time, prevFrameTime, position);
                    scaleWriter.SubmitKeyframe(t, time, prevFrameTime, scale);
                }
            }

            for (var t = 0; t < targetCount; t++)
            {
                if (animation.FrameCount == 0)
                {
                    var targetBone = targetSkeleton.Bones[t];
                    var (bindPosition, bindRotation) = BakeConversion(targetBone.Position, targetBone.Angle, targetBone.Parent == null);
                    rotationWriter.Channels[t].Add(0f, bindRotation);
                    positionWriter.Channels[t].Add(0f, bindPosition);
                    scaleWriter.Channels[t].Add(0f, Vector3.One);
                }

                var jointNode = targetJoints[t];
                if (jointNode == null)
                {
                    continue;
                }

                outputAnimation.CreateRotationChannel(jointNode, rotationWriter.Channels[t], true);
                outputAnimation.CreateTranslationChannel(jointNode, positionWriter.Channels[t], true);
                outputAnimation.CreateScaleChannel(jointNode, scaleWriter.Channels[t], true);
            }
        }
    }

    // Animation-graph clips aren't part of GetAllAnimations; write them here. Clips authored on a
    // skeleton compatible with the model are retargeted onto its bones by name. Clips on an
    // incompatible skeleton (e.g. the first-person viewmodel rig, whose arm bones hang off weapon
    // bones the body lacks) are retargeted onto the model's armature by bone name as bind-relative
    // deltas, so the body-authored first-person mesh follows the clip without being deformed
    // (re-skinning that mesh onto the viewmodel rig instead would warp it, since the two rigs disagree).
    private void WriteAnimationGraphClips(ModelRoot exportedModel, VModel model, Node[] joints, HashSet<string> animationFilter)
    {
        var targets = new Dictionary<string, (AnimationWriter Writer, Node?[] Joints, Skeleton? RetargetTarget)?>();

        // UseAnimation is find-or-create by name, so a clip sharing a name with an already-written
        // animation (embedded, or an earlier clip) would merge its channels onto it. Keep the first, skip the rest.
        var writtenNames = exportedModel.LogicalAnimations.Select(a => a.Name).ToHashSet();

        foreach (var clipName in AnimationGraphLoader.GetClipNames(model, FileLoader))
        {
            CancellationToken.ThrowIfCancellationRequested();

            if (FileLoader.LoadFileCompiled(clipName)?.DataBlock is not VAnimationClip clip)
            {
                continue;
            }

            var animationName = ClipAnimationName(clip.Name);

            if (!IncludeAnimation(animationFilter, animationName))
            {
                continue;
            }

            if (writtenNames.Contains(animationName))
            {
                ProgressReporter?.Report($"Skipping animation graph clip '{animationName}': an animation with that name was already exported.");
                continue;
            }

            if (!targets.TryGetValue(clip.SkeletonName, out var target))
            {
                targets[clip.SkeletonName] = target = BuildClipTarget(model, joints, clip.SkeletonName);
            }

            if (target != null)
            {
                if (target.Value.RetargetTarget != null)
                {
                    target.Value.Writer.WriteRetargetedAnimation(exportedModel, target.Value.Joints, target.Value.RetargetTarget, new VAnim(clip), animationName);
                }
                else
                {
                    target.Value.Writer.WriteAnimation(exportedModel, target.Value.Joints, new VAnim(clip), animationName);
                }

                writtenNames.Add(animationName);
            }
        }
    }

    // glTF holds all animations in one flat named list, so AG2 clips are labelled by their resource
    // path with the .vnmclip extension stripped (the path keeps them unique across clip folders).
    private static string ClipAnimationName(string clipName) => Path.ChangeExtension(clipName, null)!;

    // Build the write target shared by all clips on a skeleton. Compatible skeletons map directly onto
    // the model's joints by name. Incompatible ones (the viewmodel rig) are retargeted onto the model's
    // joints as bind-relative deltas (see WriteRetargetedAnimation), so the body-authored first-person
    // mesh on that same armature follows the clip without being deformed.
    private (AnimationWriter Writer, Node?[] Joints, Skeleton? RetargetTarget)? BuildClipTarget(VModel model, Node[] joints, string clipSkeletonName)
    {
        if (FileLoader.LoadFileCompiled(clipSkeletonName)?.DataBlock is not BinaryKV3 skeletonData)
        {
            return null;
        }

        var clipSkeleton = Skeleton.FromSkeletonData(skeletonData.Data);

        if (clipSkeleton.IsCompatibleWith(model.Skeleton))
        {
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

            return matched ? (new AnimationWriter(clipSkeleton, model.FlexControllers), remappedJoints, null) : null;
        }

        // Incompatible skeleton (the viewmodel rig): retarget the clip onto the model's own armature by
        // bone name. The first-person mesh is also on that armature, so it follows the clip undeformed.
        return (new AnimationWriter(clipSkeleton, []), joints, model.Skeleton);
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
