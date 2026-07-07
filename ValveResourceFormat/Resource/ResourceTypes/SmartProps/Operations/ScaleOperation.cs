namespace ValveResourceFormat.ResourceTypes.SmartProps.Operations
{
    /// <summary>
    /// Multiplies the current scale by a uniform factor.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropOperation_Scale">CSmartPropOperation_Scale</seealso>
    sealed class ScaleOperation : SmartPropOperation
    {
        private readonly FloatAttribute scale;

        public ScaleOperation(SmartPropDefinitionParser parse) : base(parse)
        {
            scale = parse.Float("m_flScale", 1f);
        }

        public override bool Apply(ref SmartPropState state, SmartPropEvaluationContext ctx)
        {
            state.Scale *= scale.Evaluate(ctx);
            return true;
        }
    }
}
