using System.Diagnostics;
using ValveResourceFormat.ResourceTypes.ModelAnimation;

namespace ValveResourceFormat.Renderer
{
    /// <summary>
    /// Blends the weighted animation clips playing on one skeleton into a single frame.
    /// </summary>
    public partial class AnimationPlayer
    {
        /// <summary>Represents an animation clip with its playback state.</summary>
        public record class PlaybackClip(Animation Animation)
        {
            /// <summary>Gets or sets the current playback time in seconds.</summary>
            public float Time { get; set; }

            /// <summary>Gets or sets whether this clip should blend additively with other animations.</summary>
            public bool IsAdditive { get; set; }

            /// <summary>Gets or sets whether playback is paused.</summary>
            public bool IsPaused { get; set; }

            /// <summary>Gets or sets whether the clip should loop when reaching the end.</summary>
            public bool Looping { get; set; } = true;

            /// <summary>Gets or sets the blend weight (0.0 to 1.0) for this clip.</summary>
            public float Weight { get; set; } = 1f;

            /// <summary>Gets or sets the blend transition time in seconds. A value of -1 indicates manual blending.</summary>
            public float BlendTime { get; set; }

            /// <summary>Gets or sets the bone mask name to apply per-bone weighting. Empty string means no mask.</summary>
            public string BoneMask { get; set; } = string.Empty;

            /// <summary>
            /// Gets or sets the auto layer this clip plays. When set, <see cref="LayerOwner"/> must also be
            /// set, and <see cref="Weight"/> and <see cref="Time"/> are recomputed every tick from its cycle
            /// instead of advancing on their own.
            /// </summary>
            public AnimationAutoLayer? Layer { get; set; }

            /// <summary>
            /// Gets or sets the clip whose cycle position drives this auto layer's weight and time. Null for
            /// a clip that is not an auto layer.
            /// </summary>
            public PlaybackClip? LayerOwner { get; set; }

            /// <summary>
            /// Gets or sets the clip playing the blend sequence this clip is one reference animation of.
            /// When set, <see cref="Weight"/> and <see cref="Time"/> are recomputed every tick from the
            /// owner's blend state instead of advancing on their own. Null for a clip that is not a blend
            /// reference.
            /// </summary>
            public PlaybackClip? BlendOwner { get; set; }

            /// <summary>
            /// Gets or sets which entry of <see cref="BlendOwner"/>'s <see cref="SequenceAnimation.Fetch"/>
            /// this clip plays. Meaningless when <see cref="BlendOwner"/> is null.
            /// </summary>
            public int BlendIndex { get; set; }

            /// <summary>
            /// Gets whether this clip's time and weight are driven by another clip (<see cref="LayerOwner"/>
            /// or <see cref="BlendOwner"/>) rather than advanced or blended on its own.
            /// </summary>
            public bool IsDriven => LayerOwner != null || BlendOwner != null;

            /// <summary>Gets whether this clip uses time-based transition blending.</summary>
            public bool IsTimeBasedTransition => BlendTime > 0f;

            /// <summary>Gets whether this clip uses manual weight blending.</summary>
            public bool IsManualBlend => BlendTime == -1;

            /// <summary>Gets or sets the current frame index within the cycle being played.</summary>
            public int Frame
            {
                get
                {
                    var (_, frame, remainder) = Animation.GetCyclePosition(Time);
                    return remainder < 0.5f ? frame : frame + 1;
                }
                set
                {
                    Time = Animation.Fps != 0 ? value / Animation.Fps : 0f;
                }
            }
        }

        /// <summary>
        /// Gets whether the active animation clip has finished playing (is not looping and has reached the end).
        /// </summary>
        public bool ActiveClipFinished => activeClip != null && !activeClip.Looping && activeClip.IsPaused;

        /// <summary>
        /// Gets the current clips.
        /// </summary>
        public Dictionary<string, PlaybackClip> Clips => clips;

        private PlaybackClip? activeClip;
        private PlaybackClip? previousClip;
        private readonly Dictionary<string, PlaybackClip> clips = [];
        private const string WarpSuffix = ".warp";
        private readonly Frame BlendedFrame;
        private float currentBlendTime;

        /// <summary>
        /// Bone masks are used by clips to weigh transforms on a per-bone basis.
        /// </summary>
        public Dictionary<string, Half[]> BoneMaskDefinitions { get; } = [];

        /// <summary>
        /// Optional resolver from an animation or sequence name to the loaded <see cref="Animation"/>
        /// instance, used to resolve an auto layer's target to the clip it plays.
        /// </summary>
        public Func<string, Animation?>? AnimationLookup { get; set; }

        private readonly Dictionary<string, PoseParameter> poseParameterDefinitions = [];
        private readonly Dictionary<string, float> poseParameterValues = [];

        /// <summary>
        /// Clears all clips and blend state so a later transition starts from a clean state.
        /// </summary>
        public void ClearClips()
        {
            activeClip = null;
            previousClip = null;
            clips.Clear();
        }

        /// <summary>
        /// Registers a pose parameter a 1D or 2D blend sequence can position its animations along by
        /// name, defaulting its live value to zero clamped into range.
        /// </summary>
        public void RegisterPoseParameter(PoseParameter parameter)
        {
            poseParameterDefinitions[parameter.Name] = parameter;
            poseParameterValues[parameter.Name] = parameter.Clamp(0f);
        }

        /// <summary>
        /// Sets the live value of a registered pose parameter, clamped to its range, and forces the next
        /// <see cref="Update"/> to recompute the pose even if nothing else would otherwise change it (a
        /// blend of single-frame poses has no other reason to tick). A name that was never registered is
        /// stored unclamped, since its range is not known.
        /// </summary>
        public void SetPoseParameter(string name, float value)
        {
            poseParameterValues[name] = poseParameterDefinitions.TryGetValue(name, out var parameter)
                ? parameter.Clamp(value)
                : value;

            forceUpdate = true;
        }

        /// <summary>
        /// Gets the live value of a pose parameter, or zero for one that was never registered or set.
        /// </summary>
        public float GetPoseParameter(string name)
            => string.IsNullOrEmpty(name) ? 0f : poseParameterValues.GetValueOrDefault(name);

        /// <summary>
        /// Registers a bone mask for per-bone transform weighting.
        /// </summary>
        /// <param name="name">The name of the bone mask.</param>
        /// <param name="boneWeights">Dictionary mapping bone names to weight values (0.0 to 1.0).</param>
        public void RegisterBoneMask(string name, Dictionary<string, float> boneWeights)
        {
            var maskArray = new Half[Skeleton.Bones.Length];

            foreach (var (boneName, weight) in boneWeights)
            {
                var boneIndex = Skeleton.GetBoneIndex(boneName);
                if (boneIndex != -1)
                {
                    maskArray[boneIndex] = (Half)weight;
                }
            }

            BoneMaskDefinitions[name] = maskArray;
        }

        /// <summary>
        /// Updates time and weights for all active clips during playback.
        /// </summary>
        /// <param name="timeStep">Elapsed time in seconds since the last update.</param>
        private void UpdateClips(float timeStep)
        {
            if (activeClip == null)
            {
                return;
            }

            foreach (var clip in clips.Values)
            {
                if (clip.IsDriven)
                {
                    // Driven from its owner's cycle below rather than advanced on its own.
                    continue;
                }

                if (!clip.IsPaused && clip.Animation.FrameCount > 1)
                {
                    var previousTime = clip.Time;
                    clip.Time += timeStep;

                    var finished = false;

                    if (!clip.Looping)
                    {
                        var lastFrame = clip.Animation!.FrameCount - 1;
                        var maxTime = lastFrame / clip.Animation.Fps;

                        if (clip.Time > maxTime)
                        {
                            clip.IsPaused = true;

                            // Clamping the overshoot also keeps the event sampling below from wrapping
                            // around and firing the events at the start of the clip again
                            clip.Frame = lastFrame;
                            finished = true;
                        }
                    }

                    SampleEvents(clip, previousTime, clip.Time, finished);
                }
            }

            var allPaused = true;
            foreach (var clip in clips.Values)
            {
                if (!clip.IsDriven && !clip.IsPaused)
                {
                    allPaused = false;
                    break;
                }
            }

            IsPaused = allPaused;

            UpdateActiveClipSounds();

            if (activeClip.IsTimeBasedTransition && previousClip != null)
            {
                // Distribute blend weights between previous clip and active clip only.
                currentBlendTime -= timeStep;

                if (currentBlendTime <= 0f)
                {
                    previousClip.Weight = 0f;
                    activeClip.Weight = 1f;
                    previousClip = null;
                }
                else
                {
                    var t = activeClip.BlendTime > 0f
                        ? 1f - Math.Clamp(currentBlendTime / activeClip.BlendTime, 0f, 1f)
                        : 1f;

                    var blendProgress = t * t * (3f - 2f * t);

                    activeClip.Weight = blendProgress;
                    previousClip.Weight = 1f - blendProgress;

                    foreach (var clip in clips.Values)
                    {
                        if (clip != activeClip && clip != previousClip)
                        {
                            clip.Weight = 0f;
                        }
                    }
                }

                var sum = 0f;
                foreach (var clip in clips.Values)
                {
                    sum += clip.Weight;
                }
                Debug.Assert(sum > 0f, "Total blend weight should be greater than zero.");
                Debug.Assert(Math.Abs(sum - 1f) < 0.01f, $"Total blend weight should be approximately 1. Found: {sum}");
            }

            // Runs last: earlier steps above zero out every clip but the active/previous pair, and an
            // auto layer's or blend reference's weight must win over that.
            UpdateAutoLayerClips();
            UpdateBlendClips();
        }

        /// <summary>
        /// The fraction (0 at the first frame, 1 at the last) <paramref name="clip"/> has played through
        /// its current cycle, zero for a clip with no cycle to speak of (a single-frame pose, or one with
        /// no frame rate).
        /// </summary>
        private static float GetCycleFraction(PlaybackClip clip)
        {
            var cycleFrames = clip.Animation.CycleFrames;

            if (cycleFrames <= 0)
            {
                return 0f;
            }

            var (_, frame, remainder) = clip.Animation.GetCyclePosition(clip.Time);
            return (frame + remainder) / cycleFrames;
        }

        /// <summary>
        /// Recomputes every auto layer clip's playback time and blend weight from its owner clip's
        /// current cycle position, so <see cref="GetBlendedFrame"/> can blend it in like any other clip.
        /// </summary>
        private void UpdateAutoLayerClips()
        {
            foreach (var clip in clips.Values)
            {
                if (clip.Layer is not { } layer || clip.LayerOwner is not { } owner)
                {
                    continue;
                }

                var cycle = GetCycleFraction(owner);

                clip.Weight = EvaluateAutoLayerWeight(layer, cycle) * owner.Weight;
                clip.Time = cycle * clip.Animation.CycleDuration;
            }
        }

        /// <summary>
        /// Recomputes every blend reference clip's playback time and blend weight from its owner's
        /// current cycle position and the live pose parameter value(s) its blend fetch names, so
        /// <see cref="GetBlendedFrame"/> can blend it in like any other clip. The owner clip itself
        /// carries no meaningful frame data of its own and is excluded from sampling there.
        /// </summary>
        private void UpdateBlendClips()
        {
            foreach (var clip in clips.Values)
            {
                if (clip.BlendOwner is not { } owner || owner.Animation is not SequenceAnimation sequence)
                {
                    continue;
                }

                var cycle = GetCycleFraction(owner);

                clip.Weight = EvaluateBlendReferenceWeight(sequence, clip.BlendIndex) * owner.Weight;
                clip.Time = cycle * clip.Animation.CycleDuration;
            }
        }

        /// <summary>
        /// The current blend weight of one entry of a blend sequence's
        /// <see cref="AnimationFetch.LocalReferenceArray"/>: bilinear interpolation between the up-to-4
        /// entries bracketing the live pose parameter value(s) for a 2D blend
        /// (<see cref="AnimationFetch.Is2D"/>), otherwise linear interpolation between the two entries
        /// bracketing it along <see cref="AnimationFetch.PoseKeyArray"/> - using
        /// <see cref="AnimationFetch.FixedBlendWeightValue"/> in place of the live value when the fetch
        /// ignores its pose parameter (<see cref="AnimationFetch.FixedBlendWeight"/>).
        /// </summary>
        private float EvaluateBlendReferenceWeight(SequenceAnimation sequence, int index)
        {
            var fetch = sequence.Fetch!.Value;

            if (fetch.Is2D)
            {
                var rows = fetch.GroupSize.Length > 0 ? (int)fetch.GroupSize[0] : 0;
                var columns = fetch.GroupSize.Length > 1 ? (int)fetch.GroupSize[1] : 0;

                if (rows <= 0 || columns <= 0)
                {
                    return 0f;
                }

                var rowKeys = new float[rows];
                for (var r = 0; r < rows; r++)
                {
                    rowKeys[r] = r < fetch.PoseKeyArray.Length ? fetch.PoseKeyArray[r] : 0f;
                }

                var columnKeys = new float[columns];
                for (var c = 0; c < columns; c++)
                {
                    var key = rows * c;
                    columnKeys[c] = key < fetch.PoseKeyArray1.Length ? fetch.PoseKeyArray1[key] : 0f;
                }

                var row = index % rows;
                var column = index / rows;

                var rowValue = GetBlendPoseValue(sequence, 0);
                var columnValue = GetBlendPoseValue(sequence, 1);

                return EvaluateBlendWeight(rowKeys, rowValue, row) * EvaluateBlendWeight(columnKeys, columnValue, column);
            }

            var value = fetch.FixedBlendWeight ? fetch.FixedBlendWeightValue : GetBlendPoseValue(sequence, 0);
            return EvaluateBlendWeight(fetch.PoseKeyArray, value, index);
        }

        /// <summary>
        /// The live value driving one dimension of a blend (row for dimension 0, column for dimension 1
        /// on a 2D blend): the value of the pose parameter <see cref="SequenceAnimation.PoseParameterNames"/>
        /// names for that dimension, or zero when the dimension names none.
        /// </summary>
        private float GetBlendPoseValue(SequenceAnimation sequence, int dimension)
        {
            var name = dimension < sequence.PoseParameterNames.Length ? sequence.PoseParameterNames[dimension] : string.Empty;
            return GetPoseParameter(name);
        }

        /// <summary>
        /// The weight of <paramref name="index"/> among a small, not-necessarily-sorted set of blend keys
        /// at <paramref name="value"/>: the two keys immediately bracketing it split the weight linearly
        /// between their indices, or the single nearest key past either end takes it all.
        /// </summary>
        private static float EvaluateBlendWeight(ReadOnlySpan<float> keys, float value, int index)
        {
            if ((uint)index >= (uint)keys.Length)
            {
                return 0f;
            }

            var lower = -1;
            var upper = -1;

            for (var i = 0; i < keys.Length; i++)
            {
                if (keys[i] <= value && (lower == -1 || keys[i] > keys[lower]))
                {
                    lower = i;
                }

                if (keys[i] >= value && (upper == -1 || keys[i] < keys[upper]))
                {
                    upper = i;
                }
            }

            if (lower == -1 || upper == -1 || upper == lower)
            {
                var only = lower != -1 ? lower : upper;
                return only == index ? 1f : 0f;
            }

            if (index != lower && index != upper)
            {
                return 0f;
            }

            var span = keys[upper] - keys[lower];
            var t = span != 0f ? (value - keys[lower]) / span : 0f;

            return index == lower ? 1f - t : t;
        }

        /// <summary>
        /// Evaluates an auto layer's blend curve at a point in its owner's normalized cycle (0 at the
        /// first frame, 1 at the last): a trapezoid rising from <see cref="AnimationAutoLayer.Start"/> to
        /// <see cref="AnimationAutoLayer.Peak"/> and falling from <see cref="AnimationAutoLayer.Tail"/> to
        /// <see cref="AnimationAutoLayer.End"/>, full weight throughout when start and end coincide (an
        /// always-on "add" layer, as opposed to a ramped "blend" layer).
        /// </summary>
        private static float EvaluateAutoLayerWeight(AnimationAutoLayer layer, float cycle)
        {
            if (layer.Start == layer.End)
            {
                return 1f;
            }

            if (layer.NoBlend)
            {
                return cycle >= layer.Start && cycle <= layer.End ? 1f : 0f;
            }

            var rising = layer.Start != layer.Peak ? (cycle - layer.Start) / (layer.Peak - layer.Start) : 1f;
            var falling = layer.Tail != layer.End ? (layer.End - cycle) / (layer.End - layer.Tail) : 1f;

            var weight = Math.Clamp(Math.Min(rising, falling), 0f, 1f);

            if (layer.Spline)
            {
                weight = weight * weight * (3f - 2f * weight);
            }

            return weight;
        }

        /// <summary>
        /// Adds a clip for each of <paramref name="sequence"/>'s auto layers whose target resolves
        /// through <see cref="AnimationLookup"/>, keyed off <paramref name="ownerKey"/> so a warped
        /// re-entry of the same sequence gets its own set of layer clips. Pose-parameter-driven layers
        /// are skipped: nothing here supplies a live pose parameter value to drive them.
        /// </summary>
        private void CreateAutoLayerClips(string ownerKey, PlaybackClip owner, SequenceAnimation sequence)
        {
            for (var i = 0; i < sequence.AutoLayers.Length; i++)
            {
                var layer = sequence.AutoLayers[i];

                if (layer.Pose || string.IsNullOrEmpty(layer.ReferencedAnimationName))
                {
                    continue;
                }

                var referenced = AnimationLookup?.Invoke(layer.ReferencedAnimationName);

                if (referenced == null)
                {
                    continue;
                }

                var key = $"{ownerKey}$autolayer{i}";

                if (!clips.TryGetValue(key, out var layerClip))
                {
                    layerClip = new PlaybackClip(referenced) { Looping = true };
                    clips[key] = layerClip;
                }

                // A layer is additive either because studiomdl marked it so, or because the sequence it
                // targets is itself authored as a delta (its frames already are per-bone deltas).
                layerClip.IsAdditive = layer.Subtract || referenced.IsAdditive;
                layerClip.BoneMask = referenced is SequenceAnimation referencedSequence ? referencedSequence.BoneMaskName : string.Empty;
                layerClip.Layer = layer;
                layerClip.LayerOwner = owner;

                // A layer can itself target a blend (Hoodwink's turn-blend layered onto its run
                // sequences, issue #1334) rather than a single animation.
                if (referenced is SequenceAnimation { IsBlend: true } layerBlend)
                {
                    CreateBlendReferenceClips(key, layerClip, layerBlend);
                }
            }
        }

        /// <summary>
        /// Adds a clip for each entry of <paramref name="sequence"/>'s blend fetch that resolves through
        /// <see cref="AnimationLookup"/>, keyed off <paramref name="ownerKey"/> so a warped re-entry or a
        /// layer instance of the same blend gets its own set of reference clips. Additivity and bone mask
        /// come from the blend sequence itself, not from the individual referenced animations - a
        /// referenced pose is typically an absolute frame with no flags of its own, and it is the blend's
        /// own <c>m_bLegacyDelta</c>/weightlist that says how the composed result should be applied.
        /// </summary>
        private void CreateBlendReferenceClips(string ownerKey, PlaybackClip owner, SequenceAnimation sequence)
        {
            var referenceNames = sequence.BlendReferenceNames;

            for (var i = 0; i < referenceNames.Length; i++)
            {
                var name = referenceNames[i];

                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                var referenced = AnimationLookup?.Invoke(name);

                if (referenced == null)
                {
                    continue;
                }

                var key = $"{ownerKey}$blend{i}";

                if (!clips.TryGetValue(key, out var referenceClip))
                {
                    referenceClip = new PlaybackClip(referenced) { Looping = true };
                    clips[key] = referenceClip;
                }

                referenceClip.IsAdditive = sequence.IsAdditive;
                referenceClip.BoneMask = sequence.BoneMaskName;
                referenceClip.BlendOwner = owner;
                referenceClip.BlendIndex = i;
            }
        }

        /// <summary>
        /// Gets whether the current animation frame is the result of blending multiple clips together.
        /// </summary>
        public bool IsUsingMixer { get; private set; }

        /// <summary>
        /// Returns the animation frame for the current time, blending multiple clips if needed.
        /// </summary>
        /// <returns>The current animation frame, or <see langword="null"/> if no animation is active.</returns>
        private Frame? GetBlendedFrame()
        {
            IsUsingMixer = false;

            if (activeClip == null)
            {
                return null;
            }

            var needsBlending = false;
            foreach (var clip in clips.Values)
            {
                if (clip != activeClip && clip.Weight > 0f)
                {
                    needsBlending = true;
                    break;
                }
            }

            if (!needsBlending)
            {
                return SampleFrame(activeClip);
            }

            // Seeded with the bind pose so an all-additive mix (no non-additive clip contributing)
            // composes its deltas onto a valid base.
            IsUsingMixer = true;
            BlendedFrame.Clear(Skeleton);

            var totalWeight = 0f;
            foreach (var clip in clips.Values)
            {
                if (clip.Weight <= 0f)
                {
                    continue;
                }

                if (clip.Animation is SequenceAnimation { IsBlend: true })
                {
                    // A blend sequence carries no frame data of its own beyond its first reference; only
                    // its per-reference child clips (BlendOwner) are meant to be sampled.
                    continue;
                }

                var frame = SampleFrame(clip);
                var blendFactor = clip.IsAdditive
                    ? clip.Weight
                    : clip.Weight / (totalWeight + clip.Weight);

                Half[]? boneMask = null;
                if (!string.IsNullOrEmpty(clip.BoneMask))
                {
                    BoneMaskDefinitions.TryGetValue(clip.BoneMask, out boneMask);
                }

                for (var i = 0; i < frame.Bones.Length; i++)
                {
                    var boneMaskWeight = boneMask != null ? (float)boneMask[i] : 1f;
                    var weightedBlendFactor = blendFactor * boneMaskWeight;

                    BlendedFrame.Bones[i] = clip.IsAdditive
                        ? BlendedFrame.Bones[i].BlendAdd(clip.Animation.GetAdditiveDelta(i, frame.Bones[i]), weightedBlendFactor)
                        : BlendedFrame.Bones[i].Blend(frame.Bones[i], weightedBlendFactor);
                }

                for (var i = 0; i < frame.Datas.Length; i++)
                {
                    BlendedFrame.Datas[i] = clip.IsAdditive
                        ? BlendedFrame.Datas[i] + frame.Datas[i] * blendFactor
                        : float.Lerp(BlendedFrame.Datas[i], frame.Datas[i], blendFactor);
                }

                totalWeight += clip.Weight;
            }

            return BlendedFrame;
        }

        private Frame SampleFrame(PlaybackClip clip)
        {
            var ignoreCache = clip.Animation != ActiveAnimation;

            try
            {
                if (ignoreCache)
                {
                    FrameCache.PurgeCache();
                }

                return clip.IsPaused
                    ? FrameCache.GetFrame(clip.Animation, clip.Frame)
                    : FrameCache.GetInterpolatedFrame(clip.Animation, clip.Time);
            }
            finally
            {
                if (ignoreCache)
                {
                    FrameCache.PurgeCache();
                }
            }
        }

        /// <summary>
        /// Transitions to a new animation clip with the specified blend time, managing clip weights appropriately.
        /// </summary>
        /// <param name="animation">The animation to transition to.</param>
        /// <param name="blendTime">The blend time in seconds. 0 for instant transition, -1 for manual blending.</param>
        /// <param name="looping">Whether the clip should loop when reaching the end.</param>
        /// <param name="warp">Whether re-entering the animation already playing should cross
        /// over into a second instance of it rather than restarting it in place.</param>
        private void TransitionToClip(Animation animation, float blendTime, bool looping, bool warp)
        {
            var animName = animation.Name;

            if (warp && blendTime > 0f && activeClip?.Animation == animation)
            {
                animName = clips.TryGetValue(animName, out var primary) && primary == activeClip
                    ? animName + WarpSuffix
                    : animName;
            }

            // Check if clip already exists
            if (!clips.TryGetValue(animName, out var newClip))
            {
                newClip = new PlaybackClip(animation)
                {
                    Looping = looping,
                    BlendTime = blendTime,
                    IsAdditive = animation.IsAdditive,
                    BoneMask = animation is SequenceAnimation { BoneMaskName.Length: > 0 } newSequence ? newSequence.BoneMaskName : string.Empty,
                };
                clips[animName] = newClip;

                PrewarmAnimationSounds(animation);
            }
            else
            {
                newClip.Looping = looping;
                newClip.BlendTime = blendTime;

                newClip.IsPaused = false;
                newClip.Frame = 0;
            }

            if (animation is SequenceAnimation sequenceAnimation)
            {
                if (sequenceAnimation.AutoLayers.Length > 0)
                {
                    CreateAutoLayerClips(animName, newClip, sequenceAnimation);
                }

                if (sequenceAnimation.IsBlend)
                {
                    CreateBlendReferenceClips(animName, newClip, sequenceAnimation);
                }
            }

            if (activeClip == newClip)
            {
                // Re-setting the same animation should not create a self-blend transition.
                previousClip = null;

                foreach (var clip in clips.Values)
                {
                    clip.Weight = 0f;
                }

                newClip.Weight = 1f;

                if (blendTime == 0f)
                {
                    FrameCache.Clear();
                }
            }
            else if (blendTime > 0f && activeClip != null)
            {
                // Time-based transition: only blend from previous clip -> active clip.
                previousClip = activeClip;
                previousClip.Weight = 1f;

                // Set all other clips to zero immediately.
                foreach (var clip in clips.Values)
                {
                    if (clip != previousClip && clip != newClip)
                    {
                        clip.Weight = 0f;
                    }
                }

                newClip.Weight = 0f;
                currentBlendTime = blendTime;
            }
            else if (blendTime == -1f && activeClip != null)
            {
                // Manual blend: keep previous clip, user may set weights manually.
                previousClip = activeClip;
                previousClip.Weight = 1f;

                foreach (var clip in clips.Values)
                {
                    if (clip != previousClip && clip != newClip)
                    {
                        clip.Weight = 0f;
                    }
                }

                newClip.Weight = 0f;
            }
            else
            {
                // No blending - disable previous clip and all other clips.
                previousClip = null;

                foreach (var clip in clips.Values)
                {
                    clip.Weight = 0f;
                }

                newClip.Weight = 1f;

                if (blendTime == 0f)
                {
                    FrameCache.Clear();
                }
            }

            activeClip = newClip;
        }

        /// <summary>
        /// Sets the blend weight for a clip with the specified animation name.
        /// </summary>
        /// <param name="name">The name of the animation.</param>
        /// <param name="weight">The weight value (0.0 to 1.0).</param>
        /// <param name="restartIfNew">Whether to restart the animation if it's just now fading in.</param>
        public void SetAnimationWeight(string name, float weight, bool restartIfNew = false)
        {
            if (clips.TryGetValue(name, out var clip))
            {
                var wasZero = clip.Weight == 0f;
                clip.Weight = weight;

                if (restartIfNew && wasZero && weight > 0f)
                {
                    clip.Time = 0f;
                    clip.IsPaused = false;
                }
            }
        }

        /// <summary>
        /// Sets properties for a clip with the specified animation name.
        /// </summary>
        /// <param name="name">The name of the animation.</param>
        /// <param name="time">Optional playback time to set.</param>
        /// <param name="looping">Optional looping flag to set.</param>
        /// <param name="boneMask">Optional bone mask name to set.</param>
        public void SetAnimationProperties(string name, float? time = null, bool? looping = null, string? boneMask = null)
        {
            if (clips.TryGetValue(name, out var clip))
            {
                if (time.HasValue)
                {
                    clip.Time = time.Value;
                    clip.IsPaused = false;
                }

                if (looping.HasValue)
                {
                    clip.Looping = looping.Value;
                }

                if (boneMask != null)
                {
                    clip.BoneMask = boneMask;
                }
            }
        }
    }
}
