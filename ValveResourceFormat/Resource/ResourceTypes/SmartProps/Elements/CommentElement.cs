namespace ValveResourceFormat.ResourceTypes.SmartProps.Elements
{
    /// <summary>
    /// A Hammer 5 Tools comment node; has no effect.
    /// </summary>
    sealed class CommentElement : SmartPropElement
    {
        public CommentElement(SmartPropDefinitionParser parse) : base(parse)
        {
        }

        protected override void OnEvaluate(ref SmartPropState state, SmartPropEvaluationContext ctx)
        {
        }
    }
}
