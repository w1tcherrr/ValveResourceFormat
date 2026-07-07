namespace ValveResourceFormat.ResourceTypes.SmartProps.Operations
{
    /// <summary>
    /// Operations with no standalone effect: editor locator gizmos at their default value,
    /// rigid-deformation markers consumed by deformers, and Hammer 5 Tools comments.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropOperation_CreateLocator">CSmartPropOperation_CreateLocator</seealso>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropOperation_RigidDeformation">CSmartPropOperation_RigidDeformation</seealso>
    sealed class NoOpOperation : SmartPropOperation
    {
        public NoOpOperation(SmartPropDefinitionParser parse) : base(parse)
        {
        }

        public override bool Apply(ref SmartPropState state, SmartPropEvaluationContext ctx) => true;
    }
}
