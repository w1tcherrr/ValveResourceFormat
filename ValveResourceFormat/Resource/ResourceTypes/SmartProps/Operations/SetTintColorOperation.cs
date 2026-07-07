using System.Linq;
namespace ValveResourceFormat.ResourceTypes.SmartProps.Operations
{
    /// <summary>
    /// Sets the current tint from a list of color choices: picked randomly by weight, first, or
    /// by a specific index.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropOperation_SetTintColor">CSmartPropOperation_SetTintColor</seealso>
    sealed class SetTintColorOperation : SmartPropOperation
    {
        private readonly (FloatAttribute Weight, VectorAttribute Color)[] colorChoices;
        private readonly StringAttribute selectionMode;
        private readonly FloatAttribute colorSelection;
        private readonly StringAttribute mode;

        public SetTintColorOperation(SmartPropDefinitionParser parse) : base(parse)
        {
            colorChoices = [.. parse.Array("m_ColorChoices")
                .Select(choice => (choice.Float("m_flWeight", 1f), choice.Vector("m_Color")))];
            selectionMode = parse.String("m_SelectionMode", "RANDOM");
            colorSelection = parse.Float("m_ColorSelection");
            mode = parse.String("m_Mode", "MULTIPLY_OBJECT");
        }

        public override bool Apply(ref SmartPropState state, SmartPropEvaluationContext ctx)
        {
            if (colorChoices.Length == 0)
            {
                return true;
            }

            var selection = selectionMode.Evaluate(ctx)!;
            int index;

            if (selection.Equals("SPECIFIC", StringComparison.OrdinalIgnoreCase))
            {
                index = Math.Clamp(colorSelection.EvaluateInt(ctx), 0, colorChoices.Length - 1);
            }
            else if (selection.Equals("FIRST", StringComparison.OrdinalIgnoreCase))
            {
                index = 0;
            }
            else
            {
                index = SmartPropHelpers.PickWeightedIndex(
                    colorChoices.Length,
                    i => colorChoices[i].Weight.Evaluate(ctx),
                    ctx.Random);
            }

            var color = colorChoices[index].Color.EvaluateColor(ctx, Vector4.One);
            SmartPropHelpers.ApplyTint(ref state, color, mode.Evaluate(ctx));
            return true;
        }
    }
}
