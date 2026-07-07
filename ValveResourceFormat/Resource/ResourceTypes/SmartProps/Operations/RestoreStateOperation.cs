namespace ValveResourceFormat.ResourceTypes.SmartProps.Operations
{
    /// <summary>
    /// Restores a previously saved transform/tint state, optionally discarding the element when
    /// the state is unknown.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropOperation_RestoreState">CSmartPropOperation_RestoreState</seealso>
    sealed class RestoreStateOperation : SmartPropOperation
    {
        private readonly string? stateName;
        private readonly bool discardIfUnknown;

        public RestoreStateOperation(SmartPropDefinitionParser parse) : base(parse)
        {
            stateName = parse.RawString("m_StateName");

            // Valve's schema typo, kept verbatim
            discardIfUnknown = parse.Boolean("m_bDiscardIfUknown");
        }

        public override bool Apply(ref SmartPropState state, SmartPropEvaluationContext ctx)
        {
            if (!string.IsNullOrEmpty(stateName) && ctx.SavedStates.TryGetValue(stateName, out var saved))
            {
                state = saved;
                return true;
            }

            return !discardIfUnknown;
        }
    }
}
