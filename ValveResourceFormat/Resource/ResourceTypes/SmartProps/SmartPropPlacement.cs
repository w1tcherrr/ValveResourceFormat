namespace ValveResourceFormat.ResourceTypes.SmartProps
{
    /// <summary>
    /// A single model instance produced by evaluating a smart prop.
    /// </summary>
    public sealed class SmartPropPlacement
    {
        /// <summary>Model resource path (without the <c>_c</c> suffix).</summary>
        public required string ModelName { get; init; }

        /// <summary>Rotation and translation of the instance. Scale is kept separately in <see cref="Scale"/>. Settable so deformers can post-process placements.</summary>
        public Matrix4x4 Transform { get; set; } = Matrix4x4.Identity;

        /// <summary>Per-axis scale, including accumulated element scale and the model element's own scale.</summary>
        public Vector3 Scale { get; init; } = Vector3.One;

        /// <summary>Tint color in normalized RGBA.</summary>
        public Vector4 TintColor { get; init; } = Vector4.One;

        /// <summary>Material group (skin) to apply, or null for the default.</summary>
        public string? MaterialGroupName { get; init; }

        /// <summary>Forced LOD level, or -1 for automatic.</summary>
        public long LodLevel { get; init; } = -1;

        /// <summary>Material overrides accumulated from <c>CSmartPropOperation_MaterialOverride</c>.</summary>
        public IReadOnlyList<(string Original, string Replacement)> MaterialOverrides { get; init; } = [];
    }
}
