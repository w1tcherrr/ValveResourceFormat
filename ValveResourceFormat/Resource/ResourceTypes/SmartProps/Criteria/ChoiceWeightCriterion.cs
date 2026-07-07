namespace ValveResourceFormat.ResourceTypes.SmartProps.Criteria
{
    /// <summary>
    /// Weights this element in random sibling selection.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropSelectionCriteria_ChoiceWeight">CSmartPropSelectionCriteria_ChoiceWeight</seealso>
    sealed class ChoiceWeightCriterion : SmartPropSelectionCriterion
    {
        public FloatAttribute Weight { get; }

        public ChoiceWeightCriterion(SmartPropDefinitionParser parse) : base(parse)
        {
            Weight = parse.Float("m_flWeight", 1f);
        }
    }
}
