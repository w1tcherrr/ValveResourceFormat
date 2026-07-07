namespace ValveResourceFormat.ResourceTypes.SmartProps.Operations
{
    /// <summary>
    /// Rejects the element unless its expression evaluates to true.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropFilter_Expression">CSmartPropFilter_Expression</seealso>
    sealed class ExpressionFilter : SmartPropOperation
    {
        private readonly ExpressionBoolAttribute expression;

        public ExpressionFilter(SmartPropDefinitionParser parse) : base(parse)
        {
            expression = parse.ExpressionBool("m_Expression", true);
        }

        public override bool Apply(ref SmartPropState state, SmartPropEvaluationContext ctx)
            => expression.Evaluate(ctx);
    }
}
