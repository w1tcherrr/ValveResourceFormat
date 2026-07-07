namespace ValveResourceFormat.ResourceTypes.SmartProps.Operations
{
    /// <summary>
    /// A modifier class the evaluator does not implement; passes the element through with a diagnostic.
    /// </summary>
    sealed class UnknownOperation : SmartPropOperation
    {
        private readonly string className;

        public UnknownOperation(SmartPropDefinitionParser parse, string className) : base(parse)
        {
            this.className = className;
        }

        public override bool Apply(ref SmartPropState state, SmartPropEvaluationContext ctx)
        {
            ctx.Warn(SmartPropDiagnosticCode.UnhandledModifier, className, $"Unhandled smart prop modifier {className}");
            return true;
        }
    }
}
