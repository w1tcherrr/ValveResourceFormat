namespace ValveResourceFormat.ResourceTypes.SmartProps.Operations
{
    /// <summary>
    /// Saves the current transform/tint state under a name for later restoration.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropOperation_SaveState">CSmartPropOperation_SaveState</seealso>
    sealed class SaveStateOperation : SmartPropOperation
    {
        private readonly string? stateName;

        public SaveStateOperation(SmartPropDefinitionParser parse) : base(parse)
        {
            stateName = parse.RawString("m_StateName");
        }

        public override bool Apply(ref SmartPropState state, SmartPropEvaluationContext ctx)
        {
            if (!string.IsNullOrEmpty(stateName))
            {
                ctx.SavedStates[stateName] = state;
            }

            return true;
        }
    }
}
