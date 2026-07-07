namespace ValveResourceFormat.ResourceTypes.SmartProps.Operations
{
    /// <summary>
    /// Writes the current tint color to a variable.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropOperation_SaveColor">CSmartPropOperation_SaveColor</seealso>
    sealed class SaveColorOperation : SmartPropOperation
    {
        private readonly string? variableName;

        public SaveColorOperation(SmartPropDefinitionParser parse) : base(parse)
        {
            variableName = parse.RawString("m_VariableName");
        }

        public override bool Apply(ref SmartPropState state, SmartPropEvaluationContext ctx)
        {
            if (!string.IsNullOrEmpty(variableName))
            {
                ctx.SetVariable(variableName, state.Tint);
            }

            return true;
        }
    }
}
