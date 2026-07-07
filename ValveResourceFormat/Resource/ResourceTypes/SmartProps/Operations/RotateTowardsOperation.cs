namespace ValveResourceFormat.ResourceTypes.SmartProps.Operations
{
    /// <summary>
    /// Blends the current rotation towards facing a target point, weighted 0 to 1.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropOperation_RotateTowards">CSmartPropOperation_RotateTowards</seealso>
    sealed class RotateTowardsOperation : SmartPropOperation
    {
        private readonly VectorAttribute originPos;
        private readonly StringAttribute originSpace;
        private readonly VectorAttribute targetPos;
        private readonly StringAttribute targetSpace;
        private readonly bool hasUp;
        private readonly VectorAttribute upPos;
        private readonly StringAttribute upSpace;
        private readonly FloatAttribute weight;

        public RotateTowardsOperation(SmartPropDefinitionParser parse) : base(parse)
        {
            originPos = parse.Vector("m_vOriginPos");
            originSpace = parse.String("m_OriginSpace");
            targetPos = parse.Vector("m_vTargetPos");
            targetSpace = parse.String("m_TargetSpace");
            hasUp = parse.Contains("m_vUpPos");
            upPos = parse.Vector("m_vUpPos", Vector3.UnitZ);
            upSpace = parse.String("m_UpSpace");
            weight = parse.Float("m_flWeight", 1f);
        }

        public override bool Apply(ref SmartPropState state, SmartPropEvaluationContext ctx)
        {
            var origin = SmartPropHelpers.PointToWorld(
                originPos.Evaluate(ctx),
                SmartPropHelpers.ParseSpace(originSpace.Evaluate(ctx), SmartPropSpace.Object),
                state);
            var target = SmartPropHelpers.PointToWorld(
                targetPos.Evaluate(ctx),
                SmartPropHelpers.ParseSpace(targetSpace.Evaluate(ctx), SmartPropSpace.Object),
                state);

            var direction = target - origin;

            if (direction.LengthSquared() < 1e-6f)
            {
                return true;
            }

            var up = hasUp
                ? Vector3.Normalize(SmartPropHelpers.PointToWorld(
                    upPos.Evaluate(ctx),
                    SmartPropHelpers.ParseSpace(upSpace.Evaluate(ctx), SmartPropSpace.Object),
                    state) - origin)
                : Vector3.UnitZ;

            if (SmartPropHelpers.BuildBasis(direction, up) is not Matrix4x4 targetRotation)
            {
                return true;
            }

            var blend = Math.Clamp(weight.Evaluate(ctx), 0f, 1f);
            var current = Quaternion.CreateFromRotationMatrix(state.Transform with { Translation = Vector3.Zero });
            var desired = Quaternion.CreateFromRotationMatrix(targetRotation);
            var blended = Matrix4x4.CreateFromQuaternion(Quaternion.Slerp(current, desired, blend));

            blended.Translation = state.Transform.Translation;
            state.Transform = blended;
            return true;
        }
    }
}
