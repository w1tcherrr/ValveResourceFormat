using ValveResourceFormat.ResourceTypes.SmartProps.Operations;

namespace ValveResourceFormat.ResourceTypes.SmartProps
{
    /// <summary>
    /// Creates typed smart prop operations (modifiers) by their Source 2 class name.
    /// </summary>
    static class SmartPropOperationFactory
    {
        // These can all be found in the smartprops schema

        private static readonly Dictionary<string, Func<SmartPropDefinitionParser, SmartPropOperation>> Operations = new()
        {
            // Filters
            ["CSmartPropFilter_VariableValue"] = parse => new VariableValueFilter(parse),
            ["CSmartPropFilter_Expression"] = parse => new ExpressionFilter(parse),
            ["CSmartPropFilter_Probability"] = parse => new ProbabilityFilter(parse),
            ["CSmartPropFilter_SurfaceProperties"] = parse => new SurfaceFilter(parse, "CSmartPropFilter_SurfaceProperties"),
            ["CSmartPropFilter_SurfaceAngle"] = parse => new SurfaceFilter(parse, "CSmartPropFilter_SurfaceAngle"),
            ["CSmartPropFilter_MaterialAttributes"] = parse => new SurfaceFilter(parse, "CSmartPropFilter_MaterialAttributes"),

            // Transform operations
            ["CSmartPropOperation_Translate"] = parse => new TranslateOperation(parse),
            ["CSmartPropOperation_SetPosition"] = parse => new SetPositionOperation(parse),
            ["CSmartPropOperation_Rotate"] = parse => new RotateOperation(parse),
            ["CSmartPropOperation_Scale"] = parse => new ScaleOperation(parse),
            ["CSmartPropOperation_RandomOffset"] = parse => new RandomOffsetOperation(parse),
            ["CSmartPropOperation_RandomRotation"] = parse => new RandomRotationOperation(parse),
            ["CSmartPropOperation_RandomScale"] = parse => new RandomScaleOperation(parse),
            ["CSmartPropOperation_SetOrientation"] = parse => new SetOrientationOperation(parse),
            ["CSmartPropOperation_ResetRotation"] = parse => new ResetRotationOperation(parse),
            ["CSmartPropOperation_ResetScale"] = parse => new ResetScaleOperation(parse),
            ["CSmartPropOperation_RotateTowards"] = parse => new RotateTowardsOperation(parse),

            // State operations
            ["CSmartPropOperation_SaveState"] = parse => new SaveStateOperation(parse),
            ["CSmartPropOperation_RestoreState"] = parse => new RestoreStateOperation(parse),
            ["CSmartPropOperation_SavePosition"] = parse => new SavePositionOperation(parse),
            ["CSmartPropOperation_SaveDirection"] = parse => new SaveDirectionOperation(parse),
            ["CSmartPropOperation_SaveColor"] = parse => new SaveColorOperation(parse),
            ["CSmartPropOperation_SetVariable"] = parse => new SetVariableOperation(parse),
            ["CSmartPropOperation_SetVariableBool"] = parse => new SetVariableTypedOperation(parse, boolean: true),
            ["CSmartPropOperation_SetVariableFloat"] = parse => new SetVariableTypedOperation(parse, boolean: false),
            ["CSmartPropOperation_SetVariableInt"] = parse => new SetVariableTypedOperation(parse, boolean: false),

            // Tint and material operations
            ["CSmartPropOperation_SetTintColor"] = parse => new SetTintColorOperation(parse),
            ["CSmartPropOperation_RandomColorTintColor"] = parse => new RandomColorTintOperation(parse),
            ["CSmartPropOperation_MaterialOverride"] = parse => new MaterialOverrideOperation(parse),
            ["CSmartPropOperation_MaterialTint"] = parse => new MaterialTintOperation(parse),

            // Editor gizmos
            ["CSmartPropOperation_CreateSizer"] = parse => new CreateSizerOperation(parse),
            ["CSmartPropOperation_CreateRotator"] = parse => new CreateRotatorOperation(parse),
            ["CSmartPropOperation_CreateLocator"] = parse => new NoOpOperation(parse),

            // Traces
            ["CSmartPropOperation_TraceInDirection"] = parse => new TraceInDirectionOperation(parse, "CSmartPropOperation_TraceInDirection"),
            ["CSmartPropOperation_Trace"] = parse => new TraceInDirectionOperation(parse, "CSmartPropOperation_Trace"),
            ["CSmartPropOperation_TraceToPoint"] = parse => new TraceToPointOperation(parse),
            ["CSmartPropOperation_TraceToLine"] = parse => new TraceToLineOperation(parse),

            // Computations
            ["CSmartPropOperation_ComputeDistance3D"] = parse => new ComputeDistanceOperation(parse),
            ["CSmartPropOperation_ComputeDotProduct3D"] = parse => new ComputeDotProductOperation(parse),
            ["CSmartPropOperation_ComputeCrossProduct3D"] = parse => new ComputeCrossProductOperation(parse),
            ["CSmartPropOperation_ComputeVectorBetweenPoints3D"] = parse => new ComputeVectorBetweenPointsOperation(parse),
            ["CSmartPropOperation_ComputeNormalizedVector3D"] = parse => new ComputeNormalizedVectorOperation(parse),
            ["CSmartPropOperation_ComputeProjectVector3D"] = parse => new ComputeProjectVectorOperation(parse),

            // Markers with no standalone effect
            ["CSmartPropOperation_RigidDeformation"] = parse => new NoOpOperation(parse),
            ["Hammer5Tools_Comment"] = parse => new NoOpOperation(parse),
        };

        public static SmartPropOperation Create(SmartPropDefinitionParser parse)
        {
            var className = parse.RawString("_class", string.Empty);

            return Operations.TryGetValue(className, out var factory)
                ? factory(parse)
                : new UnknownOperation(parse, className);
        }
    }
}
