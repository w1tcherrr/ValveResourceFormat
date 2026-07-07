namespace ValveResourceFormat.ResourceTypes.SmartProps.Criteria
{
    /// <summary>
    /// Excludes this element from selection when its expression evaluates to false.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropSelectionCriteria_IsValid">CSmartPropSelectionCriteria_IsValid</seealso>
    sealed class IsValidCriterion : SmartPropSelectionCriterion
    {
        public ExpressionBoolAttribute Expression { get; }

        public IsValidCriterion(SmartPropDefinitionParser parse) : base(parse)
        {
            Expression = parse.ExpressionBool("m_Expression", true);
        }
    }
}
