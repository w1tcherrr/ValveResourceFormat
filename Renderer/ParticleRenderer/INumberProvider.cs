using ValveResourceFormat.Renderer.Particles.Utils;
using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.Renderer.Particles
{
    /// <summary>
    /// Provides a scalar float value that may vary per particle or per frame.
    /// </summary>
    interface INumberProvider
    {
        /// <summary>
        /// Returns the next number value, evaluated per particle.
        /// </summary>
        float NextNumber(ref Particle particle, ParticleSystemRenderState renderState);

        /// <summary>
        /// ONLY use this in emitters and renderers, where per-particle values can't be accessed. Otherwise, use the other version.
        /// </summary>
        public float NextNumber()
            => NextNumber(ref Particle.Default, ParticleSystemRenderState.Default);

        /// <summary>
        /// Returns the next number value using system-level state only.
        /// </summary>
        public float NextNumber(ParticleSystemRenderState renderState)
            => NextNumber(ref Particle.Default, renderState);

        /// <summary>
        /// Returns the next number value truncated to an integer.
        /// </summary>
        public int NextInt(ref Particle particle, ParticleSystemRenderState renderState)
            => (int)NextNumber(ref particle, renderState);
    }

    // Literal Number
    readonly struct LiteralNumberProvider : INumberProvider
    {
        private readonly float value;

        /// <summary>The constant this provider returns, for callers that specialise on known values.</summary>
        public float Value => value;

        public LiteralNumberProvider(float value)
        {
            this.value = value;
        }

        public float NextNumber(ref Particle particle, ParticleSystemRenderState renderState) => value;
    }

    // Random Uniform/Random Biased
    class RandomNumberProvider : INumberProvider
    {
        /// <summary>Displacement between the value draw and the draw that decides the sign.</summary>
        private const int SignFlipOffset = 37;

        private readonly float minRange;
        private readonly float maxRange;
        private readonly ParticleFloatRandomMode randomMode;

        private readonly bool isBiased;
        private readonly ParticleFloatBiasType biasType = ParticleFloatBiasType.PF_BIAS_TYPE_STANDARD;
        private readonly float biasParam;

        private readonly bool hasRandomSignFlip;

        private readonly int sampleOffset;

        public RandomNumberProvider(ParticleDefinitionParser parse, bool isBiased = false)
        {
            minRange = parse.Float("m_flRandomMin");
            maxRange = parse.Float("m_flRandomMax");
            hasRandomSignFlip = parse.Boolean("m_bHasRandomSignFlip", hasRandomSignFlip);
            randomMode = parse.Enum<ParticleFloatRandomMode>("m_nRandomMode", randomMode);

            this.isBiased = isBiased;

            if (isBiased)
            {
                biasParam = parse.Float("m_flBiasParameter");
                biasType = parse.Enum<ParticleFloatBiasType>("m_nBiasType", biasType);
            }

            // Only an input that is constant per particle needs its own slot; a varying one draws from
            // the running counter and is already separated from its siblings.
            if (randomMode == ParticleFloatRandomMode.PF_RANDOM_MODE_CONSTANT)
            {
                sampleOffset = parse.NextInputOrdinal();
            }
        }

        public float NextNumber(ref Particle particle, ParticleSystemRenderState renderState)
        {
            var varying = randomMode == ParticleFloatRandomMode.PF_RANDOM_MODE_VARYING;

            var random = varying
                ? renderState.Random.Next()
                : renderState.Random.ForParticle(particle.ParticleId, sampleOffset);

            if (isBiased)
            {
                random = NumericBias.FromBiasParameter(random, biasParam, biasType);
            }

            var value = float.Lerp(minRange, maxRange, random);

            if (hasRandomSignFlip)
            {
                var sign = varying
                    ? renderState.Random.Next()
                    : renderState.Random.ForParticle(particle.ParticleId, sampleOffset + SignFlipOffset);

                if (sign < 0.5f)
                {
                    value = -value;
                }
            }

            return value;
        }
    }

    // Collection Age
    class CollectionAgeNumberProvider : INumberProvider
    {
        private readonly AttributeMapping attributeMapping;
        public CollectionAgeNumberProvider(ParticleDefinitionParser parse) { attributeMapping = new AttributeMapping(parse); }
        public float NextNumber(ref Particle particle, ParticleSystemRenderState renderState) => attributeMapping.ApplyMapping(renderState.Age);
    }

    // How long the endcap has been running, 0 outside it
    class EndCapAgeNumberProvider : INumberProvider
    {
        private readonly AttributeMapping attributeMapping;
        public EndCapAgeNumberProvider(ParticleDefinitionParser parse) { attributeMapping = new AttributeMapping(parse); }
        public float NextNumber(ref Particle particle, ParticleSystemRenderState renderState) => attributeMapping.ApplyMapping(renderState.EndCapAge);
    }

    class DetailLevelNumberProvider : INumberProvider
    {
        private readonly float[] tiers = new float[4];

        public DetailLevelNumberProvider(ParticleDefinitionParser parse)
        {
            // Absent tiers fall back to the nearest lower authored tier.
            var value = parse.Float("m_flLOD0");
            tiers[0] = value;

            var tier = 1;
            foreach (var key in (ReadOnlySpan<string>)["m_flLOD1", "m_flLOD2", "m_flLOD3"])
            {
                if (parse.Data.ContainsKey(key))
                {
                    value = parse.Float(key);
                }

                tiers[tier++] = value;
            }
        }

        public float NextNumber(ref Particle particle, ParticleSystemRenderState renderState)
        {
            return tiers[Math.Clamp((int)renderState.DetailLevel, 0, tiers.Length - 1)];
        }
    }

    // Particle Age
    class ParticleAgeNumberProvider : INumberProvider
    {
        private readonly AttributeMapping attributeMapping;
        public ParticleAgeNumberProvider(ParticleDefinitionParser parse) { attributeMapping = new AttributeMapping(parse); }
        public float NextNumber(ref Particle particle, ParticleSystemRenderState renderState) => attributeMapping.ApplyMapping(particle.Age);
    }

    // Particle Age (0-1)
    class ParticleAgeNormalizedNumberProvider : INumberProvider
    {
        private readonly AttributeMapping attributeMapping;
        public ParticleAgeNormalizedNumberProvider(ParticleDefinitionParser parse) { attributeMapping = new AttributeMapping(parse); }
        public float NextNumber(ref Particle particle, ParticleSystemRenderState renderState) => attributeMapping.ApplyMapping(particle.NormalizedAge);
    }

    // Particle Float
    // Note that the per-particle parameters are not usable in initializers, so we don't need to account for that somehow
    class PerParticleNumberProvider : INumberProvider
    {
        private readonly ParticleField field;

        private readonly AttributeMapping mapping;

        public PerParticleNumberProvider(ParticleDefinitionParser parse)
        {
            field = parse.ParticleField("m_nScalarAttribute");
            mapping = new AttributeMapping(parse);
        }
        public float NextNumber(ref Particle particle, ParticleSystemRenderState renderState) => mapping.ApplyMapping(particle.GetScalar(field));
    }

    /// <summary>
    /// PF_TYPE_PARTICLE_INITIAL_FLOAT. Reads the particle's current attribute value, which equals
    /// the initial value for attributes that never mutate after spawn (creation time, particle id);
    /// for attributes operators rewrite it is an approximation.
    /// </summary>
    class PerParticleInitialNumberProvider : INumberProvider
    {
        private readonly ParticleField field;

        private readonly AttributeMapping mapping;

        public PerParticleInitialNumberProvider(ParticleDefinitionParser parse)
        {
            field = parse.ParticleField("m_nScalarAttribute");
            mapping = new AttributeMapping(parse);
        }
        public float NextNumber(ref Particle particle, ParticleSystemRenderState renderState) => mapping.ApplyMapping(particle.GetScalar(field));
    }

    // Particle Vector Component
    class PerParticleVectorComponentNumberProvider : INumberProvider
    {
        private readonly ParticleField field;
        private readonly int component;

        private readonly AttributeMapping mapping;

        public PerParticleVectorComponentNumberProvider(ParticleDefinitionParser parse)
        {
            field = parse.ParticleField("m_nVectorAttribute");
            component = parse.Int32("m_nVectorComponent");
            mapping = new AttributeMapping(parse);
        }
        public float NextNumber(ref Particle particle, ParticleSystemRenderState renderState)
        {
            return mapping.ApplyMapping(particle.GetVectorComponent(field, component));
        }
    }

    // Particle Speed
    class PerParticleSpeedNumberProvider : INumberProvider
    {
        private readonly AttributeMapping attributeMapping;
        public PerParticleSpeedNumberProvider(ParticleDefinitionParser parse) { attributeMapping = new AttributeMapping(parse); }
        public float NextNumber(ref Particle particle, ParticleSystemRenderState renderState) => attributeMapping.ApplyMapping(particle.Speed);
    }

    // Particle Count
    class PerParticleCountNumberProvider : INumberProvider
    {
        private readonly AttributeMapping attributeMapping;
        public PerParticleCountNumberProvider(ParticleDefinitionParser parse) { attributeMapping = new AttributeMapping(parse); }
        public float NextNumber(ref Particle particle, ParticleSystemRenderState renderState) => attributeMapping.ApplyMapping(particle.UniqueParticleId);
    }

    // Particle Count Percent of Total Count (0-1)
    class PerParticleCountNormalizedNumberProvider : INumberProvider
    {
        private readonly AttributeMapping attributeMapping;
        public PerParticleCountNormalizedNumberProvider(ParticleDefinitionParser parse) { attributeMapping = new AttributeMapping(parse); }
        public float NextNumber(ref Particle particle, ParticleSystemRenderState renderState)
        {
            // Mapping input ranges for this provider type are authored in normalized 0-1 space.
            // Index is the slot in the alive list; UniqueParticleId is a lifetime spawn counter and would exceed the count.
            return attributeMapping.ApplyMapping(particle.Index / (float)Math.Max(renderState.ParticleCount, 1));
        }
    }

    // Control Point Component
    class ControlPointComponentNumberProvider : INumberProvider
    {
        private readonly AttributeMapping attributeMapping;
        private readonly int cp;
        private readonly int vectorComponent;

        public ControlPointComponentNumberProvider(ParticleDefinitionParser parse)
        {
            attributeMapping = new AttributeMapping(parse);
            cp = parse.Int32("m_nControlPoint");
            vectorComponent = parse.Int32("m_nVectorComponent");
        }

        public float NextNumber(ref Particle particle, ParticleSystemRenderState renderState)
        {
            // Control points carry raw switches and scalars that the mapping turns into the value the
            // effect actually wants - an unset point commonly has to read as a multiplier of 1, not 0.
            return attributeMapping.ApplyMapping(renderState.GetControlPoint(cp).Position.GetComponent(vectorComponent));
        }
    }

    /// <summary>PF_TYPE_RENDERER_CAMERA_DISTANCE. Distance from the render camera to the particle.</summary>
    class RendererCameraDistanceNumberProvider : INumberProvider
    {
        private readonly AttributeMapping attributeMapping;
        public RendererCameraDistanceNumberProvider(ParticleDefinitionParser parse) { attributeMapping = new AttributeMapping(parse); }
        public float NextNumber(ref Particle particle, ParticleSystemRenderState renderState)
            => attributeMapping.ApplyMapping(Vector3.Distance(renderState.CameraPosition, particle.Position));
    }

    /// <summary>
    /// PF_TYPE_PARTICLE_NOISE. Fractal noise over a particle vector attribute with the engine's
    /// turbulence and modifier post-passes. The noise type picks the primitive for both the octave
    /// sum and the turbulence resample, but turbulence overrides worley with fixed per-mode jitter
    /// and seed values, and its alternate mode swaps a worley input to a simplex resample and every
    /// other input to a worley resample. The octave sum is unweighted against amplitude-falloff
    /// divisors, exactly as the engine computes it.
    /// </summary>
    class NoiseNumberProvider : INumberProvider
    {
        private static readonly float[] OctaveDivisors = [1f, 1.5f, 1.75f, 1.875f];

        private readonly ParticleField inputField = ParticleField.Position;
        private readonly float outputMin;
        private readonly float outputMax = 1f;
        private readonly float noiseScale = 0.1f;
        private readonly Vector3 offsetRate;
        private readonly float noiseOffset;
        private readonly int octaves = 1;
        private readonly ParticleNoiseType noiseType;
        private readonly ParticleNoiseTurbulence turbulence;
        private readonly ParticleNoiseModifier modifier;
        private readonly float turbulenceScale = 1.25f;
        private readonly float turbulenceMix = 0.5f;
        private readonly float worleyJitter;
        private readonly AttributeMapping mapping;

        public NoiseNumberProvider(ParticleDefinitionParser parse)
        {
            inputField = parse.ParticleField("m_nNoiseInputVectorAttribute", inputField);
            outputMin = parse.Float("m_flNoiseOutputMin", outputMin);
            outputMax = parse.Float("m_flNoiseOutputMax", outputMax);
            noiseScale = parse.Float("m_flNoiseScale", noiseScale);
            offsetRate = parse.Vector3("m_vecNoiseOffsetRate", offsetRate);
            noiseOffset = parse.Float("m_flNoiseOffset", noiseOffset);
            octaves = Math.Clamp(parse.Int32("m_nNoiseOctaves", octaves), 1, 4);
            noiseType = parse.Enum("m_nNoiseType", noiseType);
            turbulence = parse.Enum("m_nNoiseTurbulence", turbulence);
            modifier = parse.Enum("m_nNoiseModifier", modifier);
            turbulenceScale = parse.Float("m_flNoiseTurbulenceScale", turbulenceScale);
            turbulenceMix = parse.Float("m_flNoiseTurbulenceMix", turbulenceMix);
            worleyJitter = Utils.Noise.WorleyJitter(noiseOffset);
            mapping = new AttributeMapping(parse);
        }

        public float NextNumber(ref Particle particle, ParticleSystemRenderState renderState)
        {
            var coord = ((particle.GetVector(inputField) + Vector3.One) * (noiseScale * 0.1f))
                + new Vector3(noiseOffset) + (renderState.Age * 0.1f * offsetRate);

            var sum = 0f;
            var frequency = 1f;

            for (var i = 0; i < octaves; i++)
            {
                sum += SampleNoise(coord * frequency);
                frequency *= 2f;
            }

            var value = ApplyTurbulence(sum / OctaveDivisors[octaves - 1], coord);
            value = Math.Clamp(ApplyModifier(value), 0f, 1f);

            return mapping.ApplyMapping((value * (outputMax - outputMin)) + outputMin);
        }

        private float ApplyTurbulence(float value, Vector3 coord)
        {
            if (turbulence == ParticleNoiseTurbulence.PF_NOISE_TURB_NONE)
            {
                return value;
            }

            var scaled = coord * turbulenceScale;
            float target;

            switch (turbulence)
            {
                case ParticleNoiseTurbulence.PF_NOISE_TURB_HIGHLIGHT:
                    target = 1f - (2f * MathF.Abs(SampleTurbulence(scaled + new Vector3(2.804f, 1.98f, 2.43f), 132f)));
                    break;
                case ParticleNoiseTurbulence.PF_NOISE_TURB_FEEDBACK:
                    target = SampleTurbulence(scaled + (value * new Vector3(1.804f, 2.98f, 1.43f)), 324f);
                    break;
                case ParticleNoiseTurbulence.PF_NOISE_TURB_LOOPY:
                {
                    var magnitude = MathF.Abs(value);
                    var fraction = magnitude - MathF.Truncate(magnitude);
                    var triangle = (4f - (4f * fraction)) * fraction;
                    var wave = MathF.CopySign(triangle, value);

                    if (((int)magnitude & 1) == 1)
                    {
                        wave = -wave;
                    }

                    target = SampleTurbulence(scaled + (wave * new Vector3(1.244f, -1.15f, 2.37f)), 132f);
                    break;
                }
                case ParticleNoiseTurbulence.PF_NOISE_TURB_CONTRAST:
                    target = Math.Clamp(2f * SampleTurbulence(scaled + ((2f - value) * new Vector3(2.93f, 2.734f, 2.13f)), 132f), -1f, 1f);
                    break;
                case ParticleNoiseTurbulence.PF_NOISE_TURB_ALTERNATE:
                {
                    var warped = scaled + (value * new Vector3(3.393f, 3.734f, 3.313f));
                    var resampled = noiseType == ParticleNoiseType.PF_NOISE_TYPE_WORLEY
                        ? Utils.Noise.Simplex3D(warped)
                        : Utils.Noise.Worley3D(warped, 0.97f, 22f);

                    target = value * resampled;
                    break;
                }
                default:
                    return value;
            }

            return float.Lerp(value, target, turbulenceMix);
        }

        private float SampleNoise(Vector3 coord) => noiseType switch
        {
            ParticleNoiseType.PF_NOISE_TYPE_SIMPLEX => Utils.Noise.Simplex3D(coord),
            ParticleNoiseType.PF_NOISE_TYPE_WORLEY => Utils.Noise.Worley3D(coord, worleyJitter, noiseOffset),
            ParticleNoiseType.PF_NOISE_TYPE_CURL => Utils.Noise.Curl3D(coord).X,
            _ => Utils.Noise.Value3D(coord),
        };

        private float SampleTurbulence(Vector3 coord, float worleySeed) => noiseType switch
        {
            ParticleNoiseType.PF_NOISE_TYPE_SIMPLEX => Utils.Noise.Simplex3D(coord),
            ParticleNoiseType.PF_NOISE_TYPE_WORLEY => Utils.Noise.Worley3D(coord, 0.9f, worleySeed),
            ParticleNoiseType.PF_NOISE_TYPE_CURL => Utils.Noise.Curl3D(coord).X,
            _ => Utils.Noise.Value3D(coord),
        };

        private float ApplyModifier(float value) => modifier switch
        {
            ParticleNoiseModifier.PF_NOISE_MODIFIER_NONE => (value * 0.5f) + 0.5f,
            ParticleNoiseModifier.PF_NOISE_MODIFIER_LINES => MathF.Pow(MathF.Sin(value * (3f * MathF.PI)), 2f),
            ParticleNoiseModifier.PF_NOISE_MODIFIER_CLUMPS => (2.5f * MathF.Abs(value)) - 0.5f,
            ParticleNoiseModifier.PF_NOISE_MODIFIER_RINGS => Math.Clamp((MathF.Abs(MathF.Sin(value * MathF.PI)) + 0.25f) / 1.25f, 0f, 1f),
            _ => 1f,
        };
    }

    // Control Point Speed
    class ControlPointSpeedNumberProvider : INumberProvider
    {
        private readonly AttributeMapping attributeMapping;
        private readonly int cp;

        public ControlPointSpeedNumberProvider(ParticleDefinitionParser parse)
        {
            attributeMapping = new AttributeMapping(parse);
            cp = parse.Int32("m_nControlPoint");
        }

        public float NextNumber(ref Particle particle, ParticleSystemRenderState renderState)
        {
            var speed = renderState.GetControlPoint(cp).Velocity.Length();
            return attributeMapping.ApplyMapping(speed);
        }
    }

    /* Unaccounted for params:
     * m_NamedValue
     */
}
