using System.Linq;
namespace ValveResourceFormat.ResourceTypes.SmartProps.Operations
{
    /// <summary>
    /// Adds material replacements to the current state, optionally clearing the ones in effect.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropOperation_MaterialOverride">CSmartPropOperation_MaterialOverride</seealso>
    sealed class MaterialOverrideOperation : SmartPropOperation
    {
        private readonly bool clearCurrentOverrides;
        private readonly (string Original, string Replacement)[] replacements;

        public MaterialOverrideOperation(SmartPropDefinitionParser parse) : base(parse)
        {
            clearCurrentOverrides = parse.Boolean("m_bClearCurrentOverrides");
            replacements = [.. parse.Array("m_MaterialReplacements")
                .Select(replacement => (
                    replacement.RawString("m_OriginalMaterial") ?? string.Empty,
                    replacement.RawString("m_ReplacementMaterial") ?? string.Empty))
                .Where(pair => pair.Item1.Length > 0 && pair.Item2.Length > 0)];
        }

        public override bool Apply(ref SmartPropState state, SmartPropEvaluationContext ctx)
        {
            var overrides = clearCurrentOverrides
                ? new List<(string, string)>()
                : new List<(string, string)>(state.MaterialOverrides);

            overrides.AddRange(replacements);
            state.MaterialOverrides = overrides;
            return true;
        }
    }
}
