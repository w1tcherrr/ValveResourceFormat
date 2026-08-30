using System.Linq;
using ValveKeyValue;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.ResourceTypes.ModelAnimation;
using ValveResourceFormat.ResourceTypes.ModelFlex;
using ValveResourceFormat.Serialization.KeyValues;
using static ValveResourceFormat.IO.KVHelpers;

namespace ValveResourceFormat.IO;

/// <summary>
/// Rebuilds the model doc nodes an animation is authored as: the sequence itself, the blends that
/// position several of them along a pose parameter, and the layers composed on top.
/// </summary>
partial class ModelExtract
{
    /// <summary>
    /// Returns the weight list a sequence applies, or <see langword="null"/> when it uses the default
    /// one every animation gets.
    /// </summary>
    static string? GetWeightListName(string sequenceName, Dictionary<string, KVObject> sequenceData, string[]? boneMaskNames)
    {
        if (boneMaskNames == null || !sequenceData.TryGetValue(sequenceName, out var seqDesc))
        {
            return null;
        }

        var index = seqDesc.GetInt32Property("m_nLocalWeightlist");

        if (index <= 0 || index >= boneMaskNames.Length)
        {
            return null;
        }

        return boneMaskNames[index];
    }

    /// <summary>
    /// Rebuilds the bone scale markup an animation was authored with. A DMX animation carries only
    /// position and orientation, so a resized bone has to come back as a node the compiler applies on
    /// top of it.
    /// </summary>
    static IEnumerable<KVObject> ProcessBoneScales(Skeleton skeleton, FlexController[] flexControllers, SequenceAnimation animation)
    {
        // Quantization leaves a bone the animation does not resize a hair off one.
        const float RestScaleTolerance = 1e-3f;

        var scaledBones = animation.GetScaledBones();

        if (scaledBones.Length == 0)
        {
            yield break;
        }

        for (var frameIndex = 0; frameIndex < animation.FrameCount; frameIndex++)
        {
            var frame = new Frame(skeleton, flexControllers)
            {
                FrameIndex = frameIndex
            };

            animation.DecodeFrame(frame);

            foreach (var boneIndex in scaledBones)
            {
                var scale = frame.Bones[boneIndex].Scale;

                if (MathF.Abs(scale - 1f) <= RestScaleTolerance)
                {
                    continue;
                }

                yield return MakeNode("AnimForceBoneScale",
                    ("bone", GetExportBoneName(skeleton.Bones[boneIndex])),
                    ("scale", scale),
                    ("frame", frameIndex)
                );
            }
        }
    }

    /// <summary>
    /// Rebuilds a blend that spreads its animations over a grid of two pose parameters. The compiler
    /// takes each dimension's size from the length of its weight list, and walks the grid row first.
    /// </summary>
    static KVObject Process2DBlendSequence(SequenceAnimation animation, string[] localSequenceNameArray, string[] poseParamNames,
        HashSet<string> nodeNames, bool blendAnimEvents)
    {
        var fetch = animation.Fetch!.Value;
        var rows = fetch.GroupSize.Length > 0 ? (int)fetch.GroupSize[0] : 0;
        var columns = fetch.GroupSize.Length > 1 ? (int)fetch.GroupSize[1] : 0;

        string PoseParam(int dimension)
        {
            var index = fetch.LocalPose.Length > dimension ? (int)fetch.LocalPose[dimension] : -1;
            return index >= 0 && index < poseParamNames.Length ? poseParamNames[index] : string.Empty;
        }

        var rowWeights = KVObject.Array();
        var columnWeights = KVObject.Array();
        var animations = KVObject.Array();

        for (var row = 0; row < rows; row++)
        {
            rowWeights.Add(row < fetch.PoseKeyArray.Length ? fetch.PoseKeyArray[row] : 0f);

            var rowAnimations = KVObject.Array();

            for (var column = 0; column < columns; column++)
            {
                var reference = row + (rows * column);
                rowAnimations.Add(reference < fetch.LocalReferenceArray.Length
                    ? ResolveNodeName(localSequenceNameArray[fetch.LocalReferenceArray[reference]], nodeNames)
                    : string.Empty);
            }

            animations.Add(rowAnimations);
        }

        for (var column = 0; column < columns; column++)
        {
            var key = rows * column;
            columnWeights.Add(key < fetch.PoseKeyArray1.Length ? fetch.PoseKeyArray1[key] : 0f);
        }

        var blendNode = MakeNode("2DBlend",
            ("name", animation.Name),
            ("fade_in_time", animation.SequenceParams.FadeInTime),
            ("fade_out_time", animation.SequenceParams.FadeOutTime),
            ("looping", animation.IsLooping),
            ("delta", animation.Delta),
            ("worldSpace", animation.Worldspace),
            ("hidden", animation.Hidden),
            ("row_pose_param_name", PoseParam(0)),
            ("col_pose_param_name", PoseParam(1)),
            ("row_weight_list", rowWeights),
            ("col_weight_list", columnWeights),
            ("blend_anim_list", animations)
        );

        if (blendAnimEvents)
        {
            blendNode.Add("blend_anim_events", true);
        }

        var children = KVObject.Array();
        AddActivities(blendNode, children, animation);

        foreach (var autoLayer in animation.AutoLayers)
        {
            children.Add(ProcessAnimationAutoLayer(animation.CycleFrames, autoLayer, localSequenceNameArray, poseParamNames, nodeNames));
        }

        if (animation.Autoplay)
        {
            children.Add(MakeNode("AnimAutoLayer"));
        }

        if (children.Count > 0)
        {
            blendNode.Add("children", children);
        }

        return blendNode;
    }

    /// <summary>
    /// Rebuilds the blend node behind a sequence that plays several animations at once. The compiler
    /// resolves such a node into one sequence that fetches every listed animation, positioned along a
    /// pose parameter.
    /// </summary>
    static KVObject ProcessBlendSequence(SequenceAnimation animation, string[] localSequenceNameArray, string[] poseParamNames,
        HashSet<string> nodeNames, bool blendAnimEvents)
    {
        var fetch = animation.Fetch!.Value;

        if (fetch.Is2D)
        {
            return Process2DBlendSequence(animation, localSequenceNameArray, poseParamNames, nodeNames, blendAnimEvents);
        }

        var poseParamIndex = fetch.LocalPose.Length > 0 ? (int)fetch.LocalPose[0] : -1;
        var poseParam = poseParamIndex >= 0 && poseParamIndex < poseParamNames.Length
            ? poseParamNames[poseParamIndex]
            : string.Empty;

        var blendList = KVObject.Array();

        for (var i = 0; i < fetch.LocalReferenceArray.Length; i++)
        {
            var reference = (int)fetch.LocalReferenceArray[i];

            if (reference < 0 || reference >= localSequenceNameArray.Length)
            {
                continue;
            }

            blendList.Add(MakeNode("AnimProxy",
                ("name", ResolveNodeName(localSequenceNameArray[reference], nodeNames)),
                ("weight", i < fetch.PoseKeyArray.Length ? fetch.PoseKeyArray[i] : 0f)
            ));
        }

        var blendNode = MakeNode("1DBlend",
            ("name", animation.Name),
            ("fixed_blend", fetch.FixedBlendWeight),
            ("fixed_blend_val", fetch.FixedBlendWeightValue),
            ("fade_in_time", animation.SequenceParams.FadeInTime),
            ("fade_out_time", animation.SequenceParams.FadeOutTime),
            ("looping", animation.IsLooping),
            ("delta", animation.Delta),
            ("worldSpace", animation.Worldspace),
            ("hidden", animation.Hidden),
            ("poseParam", poseParam),
            ("blendList", blendList)
        );

        if (blendAnimEvents)
        {
            blendNode.Add("blend_anim_events", true);
        }

        var children = KVObject.Array();
        AddActivities(blendNode, children, animation);

        foreach (var autoLayer in animation.AutoLayers)
        {
            children.Add(ProcessAnimationAutoLayer(animation.CycleFrames, autoLayer, localSequenceNameArray, poseParamNames, nodeNames));
        }

        if (animation.Autoplay)
        {
            children.Add(MakeNode("AnimAutoLayer"));
        }

        if (children.Count > 0)
        {
            blendNode.Add("children", children);
        }

        return blendNode;
    }

    /// <summary>
    /// Rebuilds the <c>FaceposerKeys</c> child node behind a sequence's <c>faceposer</c> gesture/posture
    /// markup. The compiler folds the node's <c>key_type</c>/<c>entry</c>/<c>start_loop</c>/<c>end_loop</c>
    /// attributes into a fixed <c>type</c>/<c>entrytag</c>/<c>startloop</c>/<c>endloop</c>/<c>tags</c>
    /// shape; only the gesture shape (the only one seen in shipped Dota content) is reconstructed here.
    /// </summary>
    static KVObject? ProcessFaceposerKeys(KVObject? sequenceKeys)
    {
        var faceposer = sequenceKeys?.GetSubCollection("faceposer");

        if (faceposer == null || faceposer.GetStringProperty("type") != "gesture")
        {
            return null;
        }

        var entryTag = faceposer.GetStringProperty("entrytag");
        var startLoopTag = faceposer.GetStringProperty("startloop");
        var endLoopTag = faceposer.GetStringProperty("endloop");
        var tags = faceposer.GetSubCollection("tags");

        if (tags == null || entryTag.Length == 0 || startLoopTag.Length == 0 || endLoopTag.Length == 0)
        {
            return null;
        }

        return MakeNode("FaceposerKeys",
            ("key_type", "Gesture"),
            ("entry", tags.GetInt32Property(entryTag, -1)),
            ("start_loop", tags.GetInt32Property(startLoopTag, -1)),
            ("end_loop", tags.GetInt32Property(endLoopTag, -1))
        );
    }

    /// <summary>
    /// Writes an animation's primary activity onto its node, and every further one as the modifier
    /// node the compiler folds back into the sequence's activity list.
    /// </summary>
    static void AddActivities(KVObject node, KVObject children, SequenceAnimation animation)
        => AddActivities(node, children, [.. animation.Activities.Select(activity => (activity.Name, activity.Weight))]);

    static void AddActivities(KVObject node, KVObject children, (string Name, int Weight)[] activities)
    {
        if (activities.Length == 0)
        {
            return;
        }

        node.Add("activity_name", activities[0].Name);
        node.Add("activity_weight", activities[0].Weight);

        for (var i = 1; i < activities.Length; i++)
        {
            children.Add(MakeNode("ActivityModifier",
                ("activity_name", activities[i].Name),
                ("activity_weight", activities[i].Weight)
            ));
        }
    }

    /// <summary>
    /// Matches a name a sequence refers to against the nodes the document actually declares. The
    /// compiled name tables spell the generated animations inconsistently, differing from the node
    /// they belong to in case or in the leading marker.
    /// </summary>
    static string ResolveNodeName(string name, HashSet<string> nodeNames)
    {
        if (nodeNames.Contains(name))
        {
            return name;
        }

        var bare = name.TrimStart('@');

        foreach (var candidate in nodeNames)
        {
            if (candidate.TrimStart('@').Equals(bare, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return name;
    }

    static KVObject ProcessAnimationAutoLayer(int cycleFrames, AnimationAutoLayer autoLayer, string[] localSequenceNameArray,
        string[] poseParamNames, HashSet<string> nodeNames)
    {
        var animName = ResolveNodeName(localSequenceNameArray[autoLayer.LocalReference], nodeNames);

        if (autoLayer.Pose == true)
        {
            var poseParam = poseParamNames[autoLayer.LocalPose];
            return MakeNode("AnimBlendLayerPoseParam", [
                ("anim_name", animName),
                ("spline", autoLayer.Spline),
                ("xfade", autoLayer.XFade),
                ("no_blend", autoLayer.NoBlend),
                ("local_space", autoLayer.Local),
                ("pose_param_name", poseParam),
                ("start_cycle", autoLayer.Start),
                ("peak_cycle", autoLayer.Peak),
                ("tail_cycle", autoLayer.Tail),
                ("end_cycle", autoLayer.End),
            ]);
        }
        else if (autoLayer.LocalPose != -1)
        {
            return MakeNode("AnimAddLayer", [
                ("anim_name", animName),
            ]);
        }
        else
        {
            return MakeNode("AnimBlendLayer", [
                ("anim_name", animName),
                ("spline", autoLayer.Spline),
                ("xfade", autoLayer.XFade),
                ("no_blend", autoLayer.NoBlend),
                ("local_space", autoLayer.Local),
                ("start_frame", (int)(autoLayer.Start * cycleFrames)),
                ("peak_frame", (int)(autoLayer.Peak * cycleFrames)),
                ("tail_frame", (int)(autoLayer.Tail * cycleFrames)),
                ("end_frame", (int)(autoLayer.End * cycleFrames)),
            ]);
        }
    }
}
