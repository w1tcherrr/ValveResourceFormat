namespace ValveResourceFormat.ResourceTypes.SmartProps.Criteria
{
    /// <summary>
    /// A criterion class no element consumes; retained so consumers can ignore it explicitly.
    /// </summary>
    sealed class UnknownCriterion : SmartPropSelectionCriterion
    {
        public string ClassName { get; }

        public UnknownCriterion(SmartPropDefinitionParser parse, string className) : base(parse)
        {
            ClassName = className;
        }
    }
}
