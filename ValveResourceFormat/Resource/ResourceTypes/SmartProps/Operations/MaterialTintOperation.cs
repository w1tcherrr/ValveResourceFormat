namespace ValveResourceFormat.ResourceTypes.SmartProps.Operations
{
    /// <summary>
    /// Tints a single material on the model. The renderer has no per-material tint, so this
    /// stays unapplied.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropOperation_MaterialTint">CSmartPropOperation_MaterialTint</seealso>
    sealed class MaterialTintOperation : SmartPropOperation
    {
        public MaterialTintOperation(SmartPropDefinitionParser parse) : base(parse)
        {
        }

        public override bool Apply(ref SmartPropState state, SmartPropEvaluationContext ctx)
        {
            ctx.Warn(SmartPropDiagnosticCode.UnsupportedOperation, "CSmartPropOperation_MaterialTint", "CSmartPropOperation_MaterialTint is not supported, skipped");
            return true;
        }
    }
}
