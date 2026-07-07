namespace ValveResourceFormat.ResourceTypes.SmartProps.Elements
{
    /// <summary>
    /// Evaluates its children under this element's state. Plain deformer containers behave the
    /// same way, since vertex warping is not applied.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropElement_Group">CSmartPropElement_Group</seealso>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropElement_Deformer">CSmartPropElement_Deformer</seealso>
    sealed class GroupElement : SmartPropElement
    {
        public GroupElement(SmartPropDefinitionParser parse) : base(parse)
        {
        }

        protected override void OnEvaluate(ref SmartPropState state, SmartPropEvaluationContext ctx)
            => EvaluateChildren(ref state, ctx);
    }
}
