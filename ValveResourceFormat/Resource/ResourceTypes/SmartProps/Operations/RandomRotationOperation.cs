namespace ValveResourceFormat.ResourceTypes.SmartProps.Operations
{
    /// <summary>
    /// Applies a random euler rotation to the current transform, optionally snapped to increments.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropOperation_RandomRotation">CSmartPropOperation_RandomRotation</seealso>
    sealed class RandomRotationOperation : SmartPropOperation
    {
        private readonly VectorAttribute rotationMin;
        private readonly VectorAttribute rotationMax;
        private readonly VectorAttribute snapIncrement;

        public RandomRotationOperation(SmartPropDefinitionParser parse) : base(parse)
        {
            rotationMin = parse.Vector("m_vRandomRotationMin");
            rotationMax = parse.Vector("m_vRandomRotationMax");
            snapIncrement = parse.Vector("m_vSnapIncrement");
        }

        public override bool Apply(ref SmartPropState state, SmartPropEvaluationContext ctx)
        {
            var min = rotationMin.Evaluate(ctx);
            var max = rotationMax.Evaluate(ctx);
            var snap = snapIncrement.Evaluate(ctx);
            var angles = new Vector3(
                SmartPropHelpers.Snap(ctx.Random.RandomFloat(min.X, max.X), snap.X),
                SmartPropHelpers.Snap(ctx.Random.RandomFloat(min.Y, max.Y), snap.Y),
                SmartPropHelpers.Snap(ctx.Random.RandomFloat(min.Z, max.Z), snap.Z));

            state.Transform = EntityTransformHelper.CreateRotationMatrixFromEulerAngles(angles) * state.Transform;
            return true;
        }
    }
}
