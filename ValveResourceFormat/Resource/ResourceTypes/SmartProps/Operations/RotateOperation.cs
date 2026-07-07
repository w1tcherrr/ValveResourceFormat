namespace ValveResourceFormat.ResourceTypes.SmartProps.Operations
{
    /// <summary>
    /// Rotates the current transform by euler angles (pitch, yaw, roll).
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropOperation_Rotate">CSmartPropOperation_Rotate</seealso>
    sealed class RotateOperation : SmartPropOperation
    {
        private readonly VectorAttribute rotation;

        public RotateOperation(SmartPropDefinitionParser parse) : base(parse)
        {
            rotation = parse.Vector("m_vRotation");
        }

        public override bool Apply(ref SmartPropState state, SmartPropEvaluationContext ctx)
        {
            var angles = rotation.Evaluate(ctx);
            state.Transform = EntityTransformHelper.CreateRotationMatrixFromEulerAngles(angles) * state.Transform;
            return true;
        }
    }
}
