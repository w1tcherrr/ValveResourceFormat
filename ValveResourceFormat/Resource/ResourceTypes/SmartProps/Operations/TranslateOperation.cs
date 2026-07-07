namespace ValveResourceFormat.ResourceTypes.SmartProps.Operations
{
    /// <summary>
    /// Offsets the current transform by a vector in the chosen coordinate space.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropOperation_Translate">CSmartPropOperation_Translate</seealso>
    sealed class TranslateOperation : SmartPropOperation
    {
        private readonly VectorAttribute position;
        private readonly StringAttribute coordinateSpace;

        public TranslateOperation(SmartPropDefinitionParser parse) : base(parse)
        {
            position = parse.Vector("m_vPosition");
            coordinateSpace = parse.String("m_CoordinateSpace");
        }

        public override bool Apply(ref SmartPropState state, SmartPropEvaluationContext ctx)
        {
            var offset = position.Evaluate(ctx);
            var space = SmartPropHelpers.ParseSpace(coordinateSpace.Evaluate(ctx), SmartPropSpace.Element);

            SmartPropHelpers.ApplyTranslate(ref state, offset, space);
            return true;
        }
    }
}
