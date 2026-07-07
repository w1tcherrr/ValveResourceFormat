namespace ValveResourceFormat.ResourceTypes.SmartProps.Operations
{
    /// <summary>
    /// Moves the current transform to an absolute position in the chosen coordinate space.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropOperation_SetPosition">CSmartPropOperation_SetPosition</seealso>
    sealed class SetPositionOperation : SmartPropOperation
    {
        private readonly VectorAttribute position;
        private readonly StringAttribute coordinateSpace;

        public SetPositionOperation(SmartPropDefinitionParser parse) : base(parse)
        {
            position = parse.Vector("m_vPosition");
            coordinateSpace = parse.String("m_CoordinateSpace");
        }

        public override bool Apply(ref SmartPropState state, SmartPropEvaluationContext ctx)
        {
            var point = position.Evaluate(ctx);
            var space = SmartPropHelpers.ParseSpace(coordinateSpace.Evaluate(ctx), SmartPropSpace.Object);

            state.Transform.Translation = SmartPropHelpers.PointToWorld(point, space, state);
            return true;
        }
    }
}
