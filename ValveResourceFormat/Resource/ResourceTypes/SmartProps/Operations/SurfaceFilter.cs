namespace ValveResourceFormat.ResourceTypes.SmartProps.Operations
{
    /// <summary>
    /// Surface-based filters need the placement surface from the containing map, which a
    /// standalone prop does not have, so they pass every element through with a diagnostic.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropFilter_SurfaceProperties">CSmartPropFilter_SurfaceProperties</seealso>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropFilter_SurfaceAngle">CSmartPropFilter_SurfaceAngle</seealso>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropFilter_MaterialAttributes">CSmartPropFilter_MaterialAttributes</seealso>
    sealed class SurfaceFilter : SmartPropOperation
    {
        private readonly string className;

        public SurfaceFilter(SmartPropDefinitionParser parse, string className) : base(parse)
        {
            this.className = className;
        }

        public override bool Apply(ref SmartPropState state, SmartPropEvaluationContext ctx)
        {
            ctx.Warn(SmartPropDiagnosticCode.NeedsWorldContext, className, $"{className} needs world context, treating as pass");
            return true;
        }
    }
}
