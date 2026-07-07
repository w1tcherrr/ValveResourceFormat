namespace ValveResourceFormat.ResourceTypes.SmartProps.Operations
{
    /// <summary>
    /// Writes the current position, expressed in the chosen coordinate space, to a variable.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropOperation_SavePosition">CSmartPropOperation_SavePosition</seealso>
    sealed class SavePositionOperation : SmartPropOperation
    {
        private readonly string? variableName;
        private readonly StringAttribute coordinateSpace;

        public SavePositionOperation(SmartPropDefinitionParser parse) : base(parse)
        {
            variableName = parse.RawString("m_VariableName");
            coordinateSpace = parse.String("m_CoordinateSpace");
        }

        public override bool Apply(ref SmartPropState state, SmartPropEvaluationContext ctx)
        {
            if (!string.IsNullOrEmpty(variableName))
            {
                var space = SmartPropHelpers.ParseSpace(coordinateSpace.Evaluate(ctx), SmartPropSpace.Object);
                ctx.SetVariable(variableName, SmartPropHelpers.PointToSpace(state.Transform.Translation, space, state));
            }

            return true;
        }
    }
}
