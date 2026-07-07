using ValveResourceFormat.ResourceTypes.SmartProps.Criteria;

namespace ValveResourceFormat.ResourceTypes.SmartProps
{
    /// <summary>
    /// Creates typed selection criteria by their Source 2 class name.
    /// </summary>
    static class SmartPropCriterionFactory
    {
        // These can all be found in the smartprops schema

        private static readonly Dictionary<string, Func<SmartPropDefinitionParser, SmartPropSelectionCriterion>> Criteria = new()
        {
            ["CSmartPropSelectionCriteria_ChoiceWeight"] = parse => new ChoiceWeightCriterion(parse),
            ["CSmartPropSelectionCriteria_IsValid"] = parse => new IsValidCriterion(parse),
            ["CSmartPropSelectionCriteria_LinearLength"] = parse => new LinearLengthCriterion(parse),
            ["CSmartPropSelectionCriteria_EndCap"] = parse => new EndCapCriterion(parse),
            ["CSmartPropSelectionCriteria_PathPosition"] = parse => new PathPositionCriterion(parse),
        };

        public static SmartPropSelectionCriterion Create(SmartPropDefinitionParser parse)
        {
            var className = parse.RawString("_class", string.Empty);

            return Criteria.TryGetValue(className, out var factory)
                ? factory(parse)
                : new UnknownCriterion(parse, className);
        }
    }
}
