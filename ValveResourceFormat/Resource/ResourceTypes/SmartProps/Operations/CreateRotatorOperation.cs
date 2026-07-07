namespace ValveResourceFormat.ResourceTypes.SmartProps.Operations
{
    /// <summary>
    /// A Hammer rotator gizmo: writes its angle into a variable and, at the actual variable
    /// value, rotates the current transform around the configured axis.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropOperation_CreateRotator">CSmartPropOperation_CreateRotator</seealso>
    sealed class CreateRotatorOperation : SmartPropOperation
    {
        private readonly string? outputVariable;
        private readonly string? gizmoName;
        private readonly FloatAttribute initialAngle;
        private readonly bool enforceLimits;
        private readonly double minAngle;
        private readonly double maxAngle;
        private readonly bool applyToCurrentTransform;
        private readonly VectorAttribute rotationAxis;

        public CreateRotatorOperation(SmartPropDefinitionParser parse) : base(parse)
        {
            outputVariable = parse.RawString("m_OutputVariable");
            gizmoName = parse.RawString("m_Name");
            initialAngle = parse.Float("m_flInitialAngle");
            enforceLimits = parse.Boolean("m_bEnforceLimits");
            minAngle = parse.Double("m_flMinAngle");
            maxAngle = parse.Double("m_flMaxAngle");
            applyToCurrentTransform = parse.Boolean("m_bApplyToCurrentTransform", true);
            rotationAxis = parse.Vector("m_vRotationAxis", Vector3.UnitZ);
        }

        public override bool Apply(ref SmartPropState state, SmartPropEvaluationContext ctx)
        {
            var angle = initialAngle.Evaluate(ctx);

            if (!string.IsNullOrEmpty(outputVariable))
            {
                ctx.SetVariable(outputVariable, (double)angle);

                // The variable may be pinned by UI or defaults; the transform follows the actual value
                if (ctx.GetVariable(outputVariable) is object current)
                {
                    angle = (float)SmartPropExpression.ToNumber(current);
                }

                if (ctx.Depth == 1 && ctx.ReportedGizmos.Add($"rotator:{outputVariable}"))
                {
                    ctx.Result.GizmoOutputs.Add(new SmartPropGizmoOutput
                    {
                        VariableName = outputVariable,
                        Label = string.IsNullOrEmpty(gizmoName) ? outputVariable : $"{gizmoName}: {outputVariable}",
                        InitialValue = (double)angle,
                        MinValue = enforceLimits ? minAngle : -360.0,
                        MaxValue = enforceLimits ? maxAngle : 360.0,
                    });
                }
            }

            if (angle != 0f && applyToCurrentTransform)
            {
                var axis = rotationAxis.Evaluate(ctx);

                if (axis != Vector3.Zero)
                {
                    var rotation = Matrix4x4.CreateFromAxisAngle(Vector3.Normalize(axis), float.DegreesToRadians(angle));
                    state.Transform = rotation * state.Transform;
                }
            }

            return true;
        }
    }
}
