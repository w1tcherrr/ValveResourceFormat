using ValveKeyValue;
using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.ResourceTypes.SmartProps.Operations
{
    /// <summary>
    /// Assigns a data-typed value to a variable.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropOperation_SetVariable">CSmartPropOperation_SetVariable</seealso>
    sealed class SetVariableOperation : SmartPropOperation
    {
        private readonly string? targetName;
        private readonly KVObject? variableValue;

        public SetVariableOperation(SmartPropDefinitionParser parse) : base(parse)
        {
            variableValue = parse.Data.GetSubCollection("m_VariableValue");
            targetName = variableValue?.GetStringProperty("m_TargetName");
        }

        public override bool Apply(ref SmartPropState state, SmartPropEvaluationContext ctx)
        {
            if (variableValue != null && !string.IsNullOrEmpty(targetName))
            {
                ctx.SetVariable(targetName, SmartPropValue.ConvertDataTypedValue(variableValue, ctx));
            }

            return true;
        }
    }

    /// <summary>
    /// Assigns a boolean or numeric value to a variable.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropOperation_SetVariableBool">CSmartPropOperation_SetVariableBool</seealso>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropOperation_SetVariableFloat">CSmartPropOperation_SetVariableFloat</seealso>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropOperation_SetVariableInt">CSmartPropOperation_SetVariableInt</seealso>
    sealed class SetVariableTypedOperation : SmartPropOperation
    {
        private readonly string? variableName;
        private readonly bool boolean;
        private readonly BoolAttribute boolValue;
        private readonly FloatAttribute numberValue;

        public SetVariableTypedOperation(SmartPropDefinitionParser parse, bool boolean) : base(parse)
        {
            variableName = parse.RawString("m_VariableName");
            this.boolean = boolean;

            if (boolean)
            {
                boolValue = parse.Bool("m_VariableValue", false);
            }
            else
            {
                numberValue = parse.Float("m_VariableValue");
            }
        }

        public override bool Apply(ref SmartPropState state, SmartPropEvaluationContext ctx)
        {
            if (!string.IsNullOrEmpty(variableName))
            {
                if (boolean)
                {
                    ctx.SetVariable(variableName, boolValue.Evaluate(ctx));
                }
                else
                {
                    ctx.SetVariable(variableName, (double)numberValue.Evaluate(ctx));
                }
            }

            return true;
        }
    }
}
