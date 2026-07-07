namespace ValveResourceFormat.ResourceTypes.SmartProps.Elements
{
    /// <summary>
    /// Evaluates its children a fixed number of times, each instance starting from this
    /// element's state and differentiating itself via <c>InstanceIndex()</c>. An optional stop
    /// expression ends the loop early.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropElement_PlaceMultiple">CSmartPropElement_PlaceMultiple</seealso>
    sealed class PlaceMultipleElement : SmartPropElement
    {
        private const int MaxLoopInstances = 4096;

        private readonly FloatAttribute count;
        private readonly string? stopExpression;

        public PlaceMultipleElement(SmartPropDefinitionParser parse) : base(parse)
        {
            count = parse.Float("m_nCount", 1f);
            stopExpression = parse.RawString("m_Expression");
        }

        protected override void OnEvaluate(ref SmartPropState state, SmartPropEvaluationContext ctx)
        {
            var instanceCount = Math.Min(count.EvaluateInt(ctx), MaxLoopInstances);

            using var loop = ctx.EnterLoop(instanceCount);

            for (var i = 0; i < instanceCount; i++)
            {
                ctx.InstanceIndex = i;

                if (!string.IsNullOrEmpty(stopExpression)
                    && SmartPropExpression.ToBool(SmartPropExpression.Evaluate(stopExpression, ctx)))
                {
                    break;
                }

                var instanceState = state;
                EvaluateChildren(ref instanceState, ctx);
            }
        }
    }
}
