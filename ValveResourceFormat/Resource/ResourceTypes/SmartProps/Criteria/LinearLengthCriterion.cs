namespace ValveResourceFormat.ResourceTypes.SmartProps.Criteria
{
    /// <summary>
    /// Declares how much line length this element occupies when fitted on a line, and whether
    /// it may stretch between the authored bounds.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropSelectionCriteria_LinearLength">CSmartPropSelectionCriteria_LinearLength</seealso>
    sealed class LinearLengthCriterion : SmartPropSelectionCriterion
    {
        public FloatAttribute Length { get; }
        public BoolAttribute AllowScale { get; }
        public FloatAttribute MinLength { get; }
        public FloatAttribute MaxLength { get; }

        public LinearLengthCriterion(SmartPropDefinitionParser parse) : base(parse)
        {
            Length = parse.Float("m_flLength", 1f);
            AllowScale = parse.Bool("m_bAllowScale", false);
            MinLength = parse.Float("m_flMinLength", 0f);
            MaxLength = parse.Float("m_flMaxLength", float.MaxValue);
        }
    }
}
