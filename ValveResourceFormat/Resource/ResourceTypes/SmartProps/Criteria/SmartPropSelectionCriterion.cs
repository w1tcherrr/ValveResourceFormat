namespace ValveResourceFormat.ResourceTypes.SmartProps.Criteria
{
    /// <summary>
    /// Base class for selection criteria: data attached to an element that placement elements
    /// consult when choosing between children (weights, validity, lengths, path positions).
    /// </summary>
    abstract class SmartPropSelectionCriterion
    {
        public BoolAttribute Enabled { get; }

        protected SmartPropSelectionCriterion(SmartPropDefinitionParser parse)
        {
            Enabled = parse.Bool("m_bEnabled", true);
        }
    }
}
