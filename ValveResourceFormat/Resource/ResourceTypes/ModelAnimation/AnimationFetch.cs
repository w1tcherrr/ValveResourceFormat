using ValveKeyValue;
using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.ResourceTypes.ModelAnimation
{
    /// <summary>
    /// Represents an animation fetch that specifies a local cycle pose parameter.
    /// </summary>
    public struct AnimationFetch
    {
        /// <summary>
        /// Gets or sets the local cycle pose parameter index.
        /// </summary>
        public int LocalCyclePoseParameter { get; set; }

        /// <summary>
        /// Gets or sets the entries of the sequence group name array the sequence plays, one per
        /// animation it blends between.
        /// </summary>
        public long[] LocalReferenceArray { get; set; }

        /// <summary>
        /// Gets or sets the pose parameter index driving each blend dimension, -1 where unused.
        /// </summary>
        public long[] LocalPose { get; set; }

        /// <summary>
        /// Gets or sets the pose parameter value each referenced animation sits at.
        /// </summary>
        public float[] PoseKeyArray { get; set; }

        /// <summary>
        /// Gets or sets whether the sequence blends its references along one pose parameter.
        /// </summary>
        public bool Is1D { get; set; }

        /// <summary>
        /// Gets or sets whether the sequence blends its references across two pose parameters.
        /// </summary>
        public bool Is2D { get; set; }

        /// <summary>
        /// Gets or sets the pose parameter value each referenced animation sits at on the second
        /// dimension of a two dimensional blend.
        /// </summary>
        public float[] PoseKeyArray1 { get; set; }

        /// <summary>
        /// Gets or sets the size of each blend dimension.
        /// </summary>
        public long[] GroupSize { get; set; }

        /// <summary>
        /// Gets or sets whether the blend ignores its pose parameter and sits at a fixed weight.
        /// </summary>
        public bool FixedBlendWeight { get; set; }

        /// <summary>
        /// Gets or sets the weight a fixed blend sits at.
        /// </summary>
        public float FixedBlendWeightValue { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="AnimationFetch"/> struct.
        /// </summary>
        /// <param name="fetchKV">The KeyValues object containing the fetch data.</param>
        public AnimationFetch(KVObject fetchKV)
        {
            LocalCyclePoseParameter = fetchKV.GetInt32Property("m_nLocalCyclePoseParameter");
            LocalReferenceArray = fetchKV.GetIntegerArray("m_localReferenceArray");
            LocalPose = fetchKV.GetIntegerArray("m_nLocalPose");
            PoseKeyArray = fetchKV.GetFloatArray("m_poseKeyArray0");
            var flags = fetchKV.GetSubCollection("m_flags");
            Is1D = flags.GetBooleanProperty("m_b1D");
            // A triangular blend has no document node of its own, so it is rebuilt as the grid it
            // spreads its animations over.
            Is2D = flags.GetBooleanProperty("m_b2D") || flags.GetBooleanProperty("m_b2D_TRI");
            PoseKeyArray1 = fetchKV.GetFloatArray("m_poseKeyArray1");
            GroupSize = fetchKV.GetIntegerArray("m_nGroupSize");
            FixedBlendWeight = fetchKV.GetBooleanProperty("m_bFixedBlendWeight");
            var fixedWeights = fetchKV.GetFloatArray("m_flFixedBlendWeightVals");
            FixedBlendWeightValue = fixedWeights.Length > 0 ? fixedWeights[0] : 0f;
        }
    }
}
