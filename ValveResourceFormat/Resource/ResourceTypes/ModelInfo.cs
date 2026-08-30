using ValveKeyValue;
using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.ResourceTypes
{
    /// <summary>
    /// Exposes a model's authored collision/view bounds and other per-model metadata from
    /// <c>m_modelInfo</c>, beyond the embedded QC keyvalues text already surfaced via
    /// <see cref="Model.KeyValues"/>.
    /// </summary>
    public sealed class ModelInfo
    {
        /// <summary>Gets the raw model info flags. Bit meanings are not currently decoded.</summary>
        public uint Flags { get; }

        /// <summary>Gets the minimum corner of the authored collision hull, in local space.</summary>
        public Vector3 HullMin { get; }

        /// <summary>Gets the maximum corner of the authored collision hull, in local space.</summary>
        public Vector3 HullMax { get; }

        /// <summary>Gets the minimum corner of the authored view/culling bounds, in local space.</summary>
        public Vector3 ViewMin { get; }

        /// <summary>Gets the maximum corner of the authored view/culling bounds, in local space.</summary>
        public Vector3 ViewMax { get; }

        /// <summary>Gets the authored mass, in kilograms. 0 when not authored.</summary>
        public float Mass { get; }

        /// <summary>Gets the authored eye position, in local space.</summary>
        public Vector3 EyePosition { get; }

        /// <summary>Gets the authored maximum eye deflection.</summary>
        public float MaxEyeDeflection { get; }

        /// <summary>Gets the default surface property name for this model. Empty when not authored.</summary>
        public string SurfaceProperty { get; }

        /// <summary>
        /// Initializes model info from a model's <c>m_modelInfo</c> sub-collection.
        /// </summary>
        public ModelInfo(KVObject modelInfo)
        {
            Flags = (uint)modelInfo.GetUnsignedIntegerProperty("m_nFlags");
            HullMin = modelInfo.GetSubCollection("m_vHullMin").ToVector3();
            HullMax = modelInfo.GetSubCollection("m_vHullMax").ToVector3();
            ViewMin = modelInfo.GetSubCollection("m_vViewMin").ToVector3();
            ViewMax = modelInfo.GetSubCollection("m_vViewMax").ToVector3();
            Mass = modelInfo.GetFloatProperty("m_flMass");
            EyePosition = modelInfo.GetSubCollection("m_vEyePosition").ToVector3();
            MaxEyeDeflection = modelInfo.GetFloatProperty("m_flMaxEyeDeflection");
            SurfaceProperty = modelInfo.GetStringProperty("m_sSurfaceProperty");
        }
    }
}
