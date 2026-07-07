namespace ValveResourceFormat.ResourceTypes.SmartProps.Criteria
{
    /// <summary>
    /// Restricts which positions along a path this element is placed at: every position, every
    /// Nth, only the ends, or the path's control points.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropSelectionCriteria_PathPosition">CSmartPropSelectionCriteria_PathPosition</seealso>
    sealed class PathPositionCriterion : SmartPropSelectionCriterion
    {
        public string PlaceAtPositions { get; }
        public FloatAttribute PlaceEveryNthPosition { get; }
        public FloatAttribute NthPositionIndexOffset { get; }
        public BoolAttribute AllowAtStart { get; }
        public BoolAttribute AllowAtEnd { get; }

        public PathPositionCriterion(SmartPropDefinitionParser parse) : base(parse)
        {
            PlaceAtPositions = parse.RawString("m_PlaceAtPositions", "ALL").ToUpperInvariant();
            PlaceEveryNthPosition = parse.Float("m_nPlaceEveryNthPosition", 2f);
            NthPositionIndexOffset = parse.Float("m_nNthPositionIndexOffset", 0f);
            AllowAtStart = parse.Bool("m_bAllowAtStart", true);
            AllowAtEnd = parse.Bool("m_bAllowAtEnd", true);
        }
    }
}
