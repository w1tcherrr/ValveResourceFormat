namespace ValveResourceFormat.ResourceTypes.SmartProps.Operations
{
    /// <summary>
    /// Multiplies the current scale by a random uniform factor, optionally snapped to increments.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropOperation_RandomScale">CSmartPropOperation_RandomScale</seealso>
    sealed class RandomScaleOperation : SmartPropOperation
    {
        private readonly FloatAttribute scaleMin;
        private readonly FloatAttribute scaleMax;
        private readonly FloatAttribute snapIncrement;

        public RandomScaleOperation(SmartPropDefinitionParser parse) : base(parse)
        {
            scaleMin = parse.Float("m_flRandomScaleMin", 1f);
            scaleMax = parse.Float("m_flRandomScaleMax", 1f);
            snapIncrement = parse.Float("m_flSnapIncrement");
        }

        public override bool Apply(ref SmartPropState state, SmartPropEvaluationContext ctx)
        {
            var min = scaleMin.Evaluate(ctx);
            var max = scaleMax.Evaluate(ctx);
            var snap = snapIncrement.Evaluate(ctx);

            state.Scale *= SmartPropHelpers.Snap(ctx.Random.RandomFloat(min, max), snap);
            return true;
        }
    }
}
