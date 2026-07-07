namespace ValveResourceFormat.ResourceTypes.SmartProps.Operations
{
    /// <summary>
    /// Applies a random position offset to the current transform, optionally snapped to increments.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropOperation_RandomOffset">CSmartPropOperation_RandomOffset</seealso>
    sealed class RandomOffsetOperation : SmartPropOperation
    {
        private readonly VectorAttribute positionMin;
        private readonly VectorAttribute positionMax;
        private readonly VectorAttribute snapIncrement;

        public RandomOffsetOperation(SmartPropDefinitionParser parse) : base(parse)
        {
            positionMin = parse.Vector("m_vRandomPositionMin");
            positionMax = parse.Vector("m_vRandomPositionMax");
            snapIncrement = parse.Vector("m_vSnapIncrement");
        }

        public override bool Apply(ref SmartPropState state, SmartPropEvaluationContext ctx)
        {
            var min = positionMin.Evaluate(ctx);
            var max = positionMax.Evaluate(ctx);
            var snap = snapIncrement.Evaluate(ctx);
            var offset = new Vector3(
                SmartPropHelpers.Snap(ctx.Random.RandomFloat(min.X, max.X), snap.X),
                SmartPropHelpers.Snap(ctx.Random.RandomFloat(min.Y, max.Y), snap.Y),
                SmartPropHelpers.Snap(ctx.Random.RandomFloat(min.Z, max.Z), snap.Z));

            SmartPropHelpers.ApplyTranslate(ref state, offset, SmartPropSpace.Element);
            return true;
        }
    }
}
