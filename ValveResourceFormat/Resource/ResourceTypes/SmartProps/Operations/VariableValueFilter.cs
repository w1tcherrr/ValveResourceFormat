using ValveKeyValue;
using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.ResourceTypes.SmartProps.Operations
{
    /// <summary>
    /// Rejects the element unless a variable compares as specified against a reference value.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropFilter_VariableValue">CSmartPropFilter_VariableValue</seealso>
    sealed class VariableValueFilter : SmartPropOperation
    {
        private readonly string variableName = string.Empty;
        private readonly KVObject? valueNode;
        private readonly string comparison = "EQUAL";

        public VariableValueFilter(SmartPropDefinitionParser parse) : base(parse)
        {
            var node = parse.Data.GetSubCollection("m_VariableComparison");

            if (node == null)
            {
                return;
            }

            variableName = node.GetStringProperty("m_Name", string.Empty);
            node.TryGetValue("m_Value", out valueNode);
            comparison = node.GetStringProperty("m_Comparison", "EQUAL");
        }

        public override bool Apply(ref SmartPropState state, SmartPropEvaluationContext ctx)
        {
            if (variableName.Length == 0 && valueNode == null)
            {
                return true;
            }

            var current = ctx.GetVariable(variableName);
            var target = valueNode == null ? null : SmartPropValue.Resolve(valueNode, ctx);

            return SmartPropHelpers.CompareValues(comparison, current, target);
        }
    }
}
