namespace ValveResourceFormat.ResourceTypes.SmartProps
{
    /// <summary>
    /// Category of a smart prop evaluation diagnostic.
    /// </summary>
    public enum SmartPropDiagnosticCode
    {
        /// <summary>An element class the evaluator does not implement.</summary>
        UnhandledElement,
        /// <summary>A modifier class the evaluator does not implement.</summary>
        UnhandledModifier,
        /// <summary>An expression referenced a variable that is not declared.</summary>
        UnknownVariable,
        /// <summary>An expression called a function the interpreter does not provide.</summary>
        UnknownFunction,
        /// <summary>An expression string could not be parsed.</summary>
        ExpressionParseError,
        /// <summary>A nested smart prop reference could not be resolved without a file loader.</summary>
        NestedNoLoader,
        /// <summary>A nested smart prop resource failed to load.</summary>
        NestedLoadFailed,
        /// <summary>The nested evaluation budget was exhausted; output is truncated.</summary>
        NestedBudgetExhausted,
        /// <summary>The placement budget was exhausted; output is truncated.</summary>
        PlacementBudgetExhausted,
        /// <summary>The operation needs world/map context that a standalone prop does not have.</summary>
        NeedsWorldContext,
        /// <summary>The operation is recognized but not supported by this evaluator.</summary>
        UnsupportedOperation,
    }

    /// <summary>
    /// A structured warning or error produced during smart prop evaluation.
    /// </summary>
    /// <param name="Code">Diagnostic category.</param>
    /// <param name="Subject">What the diagnostic is about, e.g. a class or variable name.</param>
    /// <param name="Message">Human-readable description.</param>
    /// <param name="IsError">Whether this should fail strict validation.</param>
    public sealed record SmartPropDiagnostic(SmartPropDiagnosticCode Code, string? Subject, string Message, bool IsError = false)
    {
        /// <summary>Returns the human-readable message.</summary>
        public override string ToString() => Message;
    }
}
