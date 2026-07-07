namespace ValveResourceFormat.ResourceTypes.SmartProps
{
    /// <summary>
    /// Data type of a smart prop variable.
    /// </summary>
    public enum SmartPropParameterType
    {
        /// <summary>Unrecognized variable class.</summary>
        Unknown,
        /// <summary>Boolean variable.</summary>
        Bool,
        /// <summary>Float variable.</summary>
        Float,
        /// <summary>Integer variable.</summary>
        Int,
        /// <summary>RGBA color variable.</summary>
        Color,
        /// <summary>2D vector variable.</summary>
        Vector2,
        /// <summary>3D vector variable.</summary>
        Vector3,
        /// <summary>4D vector variable.</summary>
        Vector4,
        /// <summary>String or enum-valued variable.</summary>
        String,
        /// <summary>Material group (skin) name for a specific model.</summary>
        MaterialGroup,
        /// <summary>Model resource path.</summary>
        Model,
        /// <summary>Material resource path.</summary>
        Material,
    }

    /// <summary>
    /// A smart prop variable that is exposed as an editable parameter.
    /// </summary>
    public sealed record SmartPropParameter
    {
        /// <summary>Internal variable name used by expressions and bindings.</summary>
        public required string Name { get; init; }

        /// <summary>Name to display in UI. Synthesized from <see cref="Name"/> when not authored.</summary>
        public required string DisplayName { get; init; }

        /// <summary>Variable data type.</summary>
        public SmartPropParameterType Type { get; init; }

        /// <summary>Default value, in the same representation the variable environment uses.</summary>
        public object? DefaultValue { get; init; }

        /// <summary>Minimum value for numeric parameters, if authored.</summary>
        public double? MinValue { get; init; }

        /// <summary>Maximum value for numeric parameters, if authored.</summary>
        public double? MaxValue { get; init; }

        /// <summary>Model whose material groups this parameter selects from (MaterialGroup type only).</summary>
        public string? ModelName { get; init; }

        /// <summary>Expression controlling whether the parameter is hidden in UI.</summary>
        public string? HideExpression { get; init; }

        /// <summary>Expression controlling whether the parameter is read-only in UI.</summary>
        public string? ReadOnlyExpression { get; init; }

        /// <summary>
        /// The values this parameter is known to take: enum members for enum-typed variables,
        /// otherwise mined from the authored range and comparisons in the document.
        /// Null when the parameter has no enumerable domain (free input).
        /// </summary>
        public IReadOnlyList<object>? KnownValues { get; init; }
    }

    /// <summary>
    /// A user-facing choice: a named preset that assigns several variables at once.
    /// </summary>
    public sealed record SmartPropChoice
    {
        /// <summary>Choice name shown in UI.</summary>
        public required string Name { get; init; }

        /// <summary>Name of the default option.</summary>
        public string? DefaultOption { get; init; }

        /// <summary>Options by name, each carrying the variable values it assigns.</summary>
        public required IReadOnlyList<SmartPropChoiceOption> Options { get; init; }
    }

    /// <summary>
    /// One selectable option of a <see cref="SmartPropChoice"/>.
    /// </summary>
    public sealed record SmartPropChoiceOption
    {
        /// <summary>Option name.</summary>
        public required string Name { get; init; }

        /// <summary>Variable assignments this option applies.</summary>
        public required IReadOnlyDictionary<string, object> VariableValues { get; init; }
    }

    /// <summary>
    /// A value driven by a Hammer gizmo (sizer or rotator handle) that a viewer can expose as a numeric input.
    /// </summary>
    public sealed record SmartPropGizmoOutput
    {
        /// <summary>Variable the gizmo writes.</summary>
        public required string VariableName { get; init; }

        /// <summary>Label to display, e.g. the gizmo name plus axis.</summary>
        public required string Label { get; init; }

        /// <summary>Value the gizmo starts at.</summary>
        public double InitialValue { get; init; }

        /// <summary>Authored constraint minimum, if any.</summary>
        public double? MinValue { get; init; }

        /// <summary>Authored constraint maximum, if any.</summary>
        public double? MaxValue { get; init; }
    }

    /// <summary>
    /// Result of evaluating a smart prop.
    /// </summary>
    public sealed class SmartPropEvaluationResult
    {
        /// <summary>Model instances to render.</summary>
        public List<SmartPropPlacement> Placements { get; } = [];

        /// <summary>Values driven by sizer/rotator gizmos in Hammer, exposable as numeric inputs.</summary>
        public List<SmartPropGizmoOutput> GizmoOutputs { get; } = [];

        /// <summary>Variable values as they stood at the end of evaluation, for UI hide/read-only expressions.</summary>
        public Dictionary<string, object> VariableValues { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Structured diagnostics, deduplicated by code and subject.</summary>
        public List<SmartPropDiagnostic> Diagnostics { get; } = [];

        /// <summary>Whether any diagnostic is an error (only possible with <see cref="SmartPropEvaluationOptions.Strict"/>).</summary>
        public bool HasErrors
        {
            get
            {
                foreach (var diagnostic in Diagnostics)
                {
                    if (diagnostic.IsError)
                    {
                        return true;
                    }
                }

                return false;
            }
        }
    }
}
