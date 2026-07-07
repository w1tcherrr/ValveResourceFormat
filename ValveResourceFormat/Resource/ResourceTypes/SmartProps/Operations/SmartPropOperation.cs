namespace ValveResourceFormat.ResourceTypes.SmartProps.Operations
{
    /// <summary>
    /// Base class for modifiers attached to an element: filters that gate the element,
    /// operations that mutate the running transform/tint/variable state, and editor gizmos.
    /// </summary>
    abstract class SmartPropOperation
    {
        public BoolAttribute Enabled { get; }

        protected SmartPropOperation(SmartPropDefinitionParser parse)
        {
            Enabled = parse.Bool("m_bEnabled", true);
        }

        /// <summary>
        /// Applies the operation to the running state. Returns false when a filter rejects the element.
        /// </summary>
        public abstract bool Apply(ref SmartPropState state, SmartPropEvaluationContext ctx);
    }
}
