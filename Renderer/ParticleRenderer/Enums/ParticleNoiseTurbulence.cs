namespace ValveResourceFormat.Renderer.Particles
{
    /// <summary>
    /// Turbulence post-passes for noise-typed particle float inputs.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/particleslib/PFNoiseTurbulence_t">PFNoiseTurbulence_t</seealso>
    public enum ParticleNoiseTurbulence
    {
        /// <summary>No turbulence pass.</summary>
        PF_NOISE_TURB_NONE = 0,
        /// <summary>Mixes toward an inverted-ridge sample at a fixed offset.</summary>
        PF_NOISE_TURB_HIGHLIGHT = 1,
        /// <summary>Mixes toward a sample displaced by the base noise value.</summary>
        PF_NOISE_TURB_FEEDBACK = 2,
        /// <summary>Mixes toward a sample displaced by a triangle wave of the base value.</summary>
        PF_NOISE_TURB_LOOPY = 3,
        /// <summary>Mixes toward a doubled, clamped sample displaced by the base value's complement.</summary>
        PF_NOISE_TURB_CONTRAST = 4,
        /// <summary>Mixes toward the product of the base value and a displaced sample.</summary>
        PF_NOISE_TURB_ALTERNATE = 5,
    }

    /// <summary>
    /// Output-shaping modifiers for noise-typed particle float inputs.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/particleslib/PFNoiseModifier_t">PFNoiseModifier_t</seealso>
    public enum ParticleNoiseModifier
    {
        /// <summary>Maps the signed noise into 0-1.</summary>
        PF_NOISE_MODIFIER_NONE = 0,
        /// <summary>Banded sine-squared response.</summary>
        PF_NOISE_MODIFIER_LINES = 1,
        /// <summary>Amplified absolute value.</summary>
        PF_NOISE_MODIFIER_CLUMPS = 2,
        /// <summary>Concentric ring response.</summary>
        PF_NOISE_MODIFIER_RINGS = 3,
    }
}
