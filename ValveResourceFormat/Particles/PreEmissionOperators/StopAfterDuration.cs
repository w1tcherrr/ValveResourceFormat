namespace ValveResourceFormat.Particles.PreEmissionOperators
{
    /// <summary>
    /// Stops the particle system once its age passes the duration, optionally destroying all
    /// remaining particles immediately or playing the endcap. The age is compared each time the
    /// operator runs, so a run-once instance only stops a system that is already past it.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/particles/C_OP_StopAfterCPDuration">C_OP_StopAfterCPDuration</seealso>
    class StopAfterDuration : ParticleFunctionPreEmissionOperator
    {
        private readonly INumberProvider duration = new LiteralNumberProvider(1.0f);
        private readonly bool destroy;

        /// <summary>
        /// Defaults on: the engine's stop path always enters the endcap, and content only ever authors
        /// this key to turn it off.
        /// </summary>
        private readonly bool playEndCap = true;

        public StopAfterDuration(ParticleDefinitionParser parse) : base(parse)
        {
            duration = parse.NumberProvider("m_flDuration", duration);
            destroy = parse.Boolean("m_bDestroyImmediately", destroy);
            playEndCap = parse.Boolean("m_bPlayEndCap", playEndCap);
        }

        public override void Operate(ref ParticleSystemState particleSystemState, float frameTime)
        {
            var stopTime = duration.NextNumber(particleSystemState);

            if (particleSystemState.Age > stopTime)
            {
                particleSystemState.SetStopTime(stopTime, destroy, playEndCap);
            }
        }
    }
}
