namespace ValveResourceFormat.ResourceTypes.SmartProps.Criteria
{
    /// <summary>
    /// Marks this element as the start and/or end cap of a fit-on-line span.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropSelectionCriteria_EndCap">CSmartPropSelectionCriteria_EndCap</seealso>
    sealed class EndCapCriterion : SmartPropSelectionCriterion
    {
        public BoolAttribute Start { get; }
        public BoolAttribute End { get; }

        public EndCapCriterion(SmartPropDefinitionParser parse) : base(parse)
        {
            Start = parse.Bool("m_bStart", true);
            End = parse.Bool("m_bEnd", true);
        }
    }
}
