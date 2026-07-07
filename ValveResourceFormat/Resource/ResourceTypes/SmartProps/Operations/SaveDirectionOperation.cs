namespace ValveResourceFormat.ResourceTypes.SmartProps.Operations
{
    /// <summary>
    /// Writes one of the current transform's axes (forward, left or up), expressed in the chosen
    /// coordinate space, to a variable.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropOperation_SaveDirection">CSmartPropOperation_SaveDirection</seealso>
    sealed class SaveDirectionOperation : SmartPropOperation
    {
        private readonly string? variableName;
        private readonly Vector3 basis;
        private readonly StringAttribute coordinateSpace;

        public SaveDirectionOperation(SmartPropDefinitionParser parse) : base(parse)
        {
            variableName = parse.RawString("m_VariableName");
            basis = parse.RawString("m_DirectionVector", "FORWARD") switch
            {
                "LEFT" => Vector3.UnitY,
                "UP" => Vector3.UnitZ,
                _ => Vector3.UnitX,
            };
            coordinateSpace = parse.String("m_CoordinateSpace");
        }

        public override bool Apply(ref SmartPropState state, SmartPropEvaluationContext ctx)
        {
            if (!string.IsNullOrEmpty(variableName))
            {
                var direction = Vector3.TransformNormal(basis, state.Transform);
                var space = SmartPropHelpers.ParseSpace(coordinateSpace.Evaluate(ctx), SmartPropSpace.Object);
                ctx.SetVariable(variableName, SmartPropHelpers.DirectionToSpace(direction, space, state));
            }

            return true;
        }
    }
}
