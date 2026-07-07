namespace ValveResourceFormat.ResourceTypes.SmartProps.Elements
{
    /// <summary>
    /// Applies its modifiers to the ongoing state that following siblings see. When selected
    /// directly by a placement element, the modifiers apply to that instance's state instead.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropElement_ModifyState">CSmartPropElement_ModifyState</seealso>
    sealed class ModifyStateElement : SmartPropElement
    {
        public ModifyStateElement(SmartPropDefinitionParser parse) : base(parse)
        {
        }

        protected override void OnEvaluate(ref SmartPropState state, SmartPropEvaluationContext ctx)
            => ApplyOperations(ref state, ctx);
    }
}
