namespace ValveResourceFormat.ResourceTypes.SmartProps.Elements
{
    /// <summary>
    /// Scatters children over a named map mesh. The mesh comes from the containing map, which a
    /// standalone prop does not have, so the children are placed once instead.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropElement_PlaceOnMesh">CSmartPropElement_PlaceOnMesh</seealso>
    sealed class PlaceOnMeshElement : SmartPropElement
    {
        private const string ClassName = "CSmartPropElement_PlaceOnMesh";

        public PlaceOnMeshElement(SmartPropDefinitionParser parse) : base(parse)
        {
        }

        protected override void OnEvaluate(ref SmartPropState state, SmartPropEvaluationContext ctx)
        {
            ctx.Warn(SmartPropDiagnosticCode.NeedsWorldContext, ClassName, $"{ClassName} needs map mesh data, placing its children once");
            EvaluateChildren(ref state, ctx);
        }
    }
}
