namespace ValveResourceFormat.ResourceTypes.SmartProps.Operations
{
    /// <summary>
    /// Resets the current scale to one.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropOperation_ResetScale">CSmartPropOperation_ResetScale</seealso>
    sealed class ResetScaleOperation : SmartPropOperation
    {
        public ResetScaleOperation(SmartPropDefinitionParser parse) : base(parse)
        {
        }

        public override bool Apply(ref SmartPropState state, SmartPropEvaluationContext ctx)
        {
            state.Scale = Vector3.One;
            return true;
        }
    }
}
