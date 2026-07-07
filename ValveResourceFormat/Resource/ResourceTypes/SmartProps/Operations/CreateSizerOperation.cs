namespace ValveResourceFormat.ResourceTypes.SmartProps.Operations
{
    /// <summary>
    /// A Hammer sizer gizmo: writes its per-axis handle positions into variables, so a viewer
    /// exposes them as numeric inputs with the authored constraints as ranges.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropOperation_CreateSizer">CSmartPropOperation_CreateSizer</seealso>
    sealed class CreateSizerOperation : SmartPropOperation
    {
        private readonly string? gizmoName;
        private readonly Axis[] axes;

        private readonly record struct Axis(string? VariableName, FloatAttribute Initial, FloatAttribute PairedInitial, FloatAttribute ConstraintMin, FloatAttribute ConstraintMax, bool IsMaxSide);

        public CreateSizerOperation(SmartPropDefinitionParser parse) : base(parse)
        {
            gizmoName = parse.RawString("m_Name");
            axes =
            [
                ParseAxis(parse, "m_OutputVariableMinX", "m_flInitialMinX", "m_flInitialMaxX", "m_flConstraintMinX", "m_flConstraintMaxX", isMaxSide: false),
                ParseAxis(parse, "m_OutputVariableMaxX", "m_flInitialMaxX", "m_flInitialMinX", "m_flConstraintMinX", "m_flConstraintMaxX", isMaxSide: true),
                ParseAxis(parse, "m_OutputVariableMinY", "m_flInitialMinY", "m_flInitialMaxY", "m_flConstraintMinY", "m_flConstraintMaxY", isMaxSide: false),
                ParseAxis(parse, "m_OutputVariableMaxY", "m_flInitialMaxY", "m_flInitialMinY", "m_flConstraintMinY", "m_flConstraintMaxY", isMaxSide: true),
                ParseAxis(parse, "m_OutputVariableMinZ", "m_flInitialMinZ", "m_flInitialMaxZ", "m_flConstraintMinZ", "m_flConstraintMaxZ", isMaxSide: false),
                ParseAxis(parse, "m_OutputVariableMaxZ", "m_flInitialMaxZ", "m_flInitialMinZ", "m_flConstraintMinZ", "m_flConstraintMaxZ", isMaxSide: true),
            ];
        }

        private static Axis ParseAxis(SmartPropDefinitionParser parse, string outputField, string initialField, string pairedInitialField, string constraintMinField, string constraintMaxField, bool isMaxSide)
            => new(
                parse.RawString(outputField),
                parse.Float(initialField),
                parse.Float(pairedInitialField),
                parse.Float(constraintMinField),
                parse.Float(constraintMaxField),
                isMaxSide);

        public override bool Apply(ref SmartPropState state, SmartPropEvaluationContext ctx)
        {
            foreach (var axis in axes)
            {
                if (string.IsNullOrEmpty(axis.VariableName))
                {
                    continue;
                }

                var initial = axis.Initial.Evaluate(ctx);
                ctx.SetVariable(axis.VariableName, (double)initial);

                if (ctx.Depth == 1 && ctx.ReportedGizmos.Add($"sizer:{axis.VariableName}"))
                {
                    // The authored constraints bound the axis span relative to the opposite handle
                    double? min = null;
                    double? max = null;
                    var constraintMin = axis.ConstraintMin.Evaluate(ctx);
                    var constraintMax = axis.ConstraintMax.Evaluate(ctx);

                    if (constraintMax > constraintMin)
                    {
                        var paired = axis.PairedInitial.Evaluate(ctx);

                        if (axis.IsMaxSide)
                        {
                            min = paired + constraintMin;
                            max = paired + constraintMax;
                        }
                        else
                        {
                            min = paired - constraintMax;
                            max = paired - constraintMin;
                        }
                    }

                    ctx.Result.GizmoOutputs.Add(new SmartPropGizmoOutput
                    {
                        VariableName = axis.VariableName,
                        Label = string.IsNullOrEmpty(gizmoName) ? axis.VariableName : $"{gizmoName}: {axis.VariableName}",
                        InitialValue = SmartPropExpression.ToNumber(ctx.GetVariable(axis.VariableName)),
                        MinValue = min,
                        MaxValue = max,
                    });
                }
            }

            return true;
        }
    }
}
