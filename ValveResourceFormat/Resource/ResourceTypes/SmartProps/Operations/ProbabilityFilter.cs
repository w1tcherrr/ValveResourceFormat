namespace ValveResourceFormat.ResourceTypes.SmartProps.Operations
{
    /// <summary>
    /// Rejects the element randomly with the configured probability of keeping it.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropFilter_Probability">CSmartPropFilter_Probability</seealso>
    sealed class ProbabilityFilter : SmartPropOperation
    {
        private readonly FloatAttribute probability;

        public ProbabilityFilter(SmartPropDefinitionParser parse) : base(parse)
        {
            probability = parse.Float("m_flProbability", 0.5f);
        }

        public override bool Apply(ref SmartPropState state, SmartPropEvaluationContext ctx)
        {
            var threshold = probability.Evaluate(ctx);
            return ctx.Random.RandomFloat() <= threshold;
        }
    }
}
