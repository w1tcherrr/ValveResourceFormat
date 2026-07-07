namespace ValveResourceFormat.ResourceTypes.SmartProps.Elements
{
    /// <summary>
    /// An element class the evaluator does not implement; emits a diagnostic and places nothing.
    /// </summary>
    sealed class UnknownElement : SmartPropElement
    {
        private readonly string className;

        public UnknownElement(SmartPropDefinitionParser parse, string className) : base(parse)
        {
            this.className = className;
        }

        protected override void OnEvaluate(ref SmartPropState state, SmartPropEvaluationContext ctx)
        {
            ctx.Warn(SmartPropDiagnosticCode.UnhandledElement, className, $"Unhandled smart prop element {className}");
        }
    }
}
