namespace ValveResourceFormat.ResourceTypes.SmartProps.Operations
{
    /// <summary>
    /// Replaces the current rotation with a basis built from forward and up direction vectors.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropOperation_SetOrientation">CSmartPropOperation_SetOrientation</seealso>
    sealed class SetOrientationOperation : SmartPropOperation
    {
        private readonly VectorAttribute forwardVector;
        private readonly StringAttribute forwardSpace;
        private readonly VectorAttribute upVector;
        private readonly StringAttribute upSpace;
        private readonly BoolAttribute prioritizeUp;

        public SetOrientationOperation(SmartPropDefinitionParser parse) : base(parse)
        {
            forwardVector = parse.Vector("m_vForwardVector", Vector3.UnitX);
            forwardSpace = parse.String("m_ForwardDirectionSpace");
            upVector = parse.Vector("m_vUpVector", Vector3.UnitZ);
            upSpace = parse.String("m_UpDirectionSpace");
            prioritizeUp = parse.Bool("m_bPrioritizeUp", false);
        }

        public override bool Apply(ref SmartPropState state, SmartPropEvaluationContext ctx)
        {
            var forward = forwardVector.Evaluate(ctx);
            var forwardDirectionSpace = SmartPropHelpers.ParseSpace(forwardSpace.Evaluate(ctx), SmartPropSpace.Object);
            var up = upVector.Evaluate(ctx);
            var upDirectionSpace = SmartPropHelpers.ParseSpace(upSpace.Evaluate(ctx), SmartPropSpace.Object);
            var priorityUp = prioritizeUp.Evaluate(ctx);

            forward = SmartPropHelpers.DirectionToWorld(forward, forwardDirectionSpace, state);
            up = SmartPropHelpers.DirectionToWorld(up, upDirectionSpace, state);

            if (SmartPropHelpers.BuildBasis(forward, up, priorityUp) is not Matrix4x4 rotation)
            {
                return true;
            }

            rotation.Translation = state.Transform.Translation;
            state.Transform = rotation;
            return true;
        }
    }
}
