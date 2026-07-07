namespace ValveResourceFormat.ResourceTypes.SmartProps.Elements
{
    /// <summary>
    /// Selects and evaluates exactly one eligible child: weighted-randomly, the first, or a
    /// specific index.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropElement_PickOne">CSmartPropElement_PickOne</seealso>
    sealed class PickOneElement : SmartPropElement
    {
        private readonly StringAttribute selectionMode;
        private readonly FloatAttribute specificChildIndex;
        private readonly string? outputChoiceVariableName;

        public PickOneElement(SmartPropDefinitionParser parse) : base(parse)
        {
            selectionMode = parse.String("m_SelectionMode", "RANDOM");
            specificChildIndex = parse.Float("m_SpecificChildIndex");
            outputChoiceVariableName = parse.RawString("m_OutputChoiceVariableName");
        }

        protected override void OnEvaluate(ref SmartPropState state, SmartPropEvaluationContext ctx)
        {
            if (Children.Count == 0)
            {
                return;
            }

            var mode = selectionMode.Evaluate(ctx);
            var pickedIndex = -1;

            if (string.Equals(mode, "SPECIFIC", StringComparison.OrdinalIgnoreCase))
            {
                pickedIndex = Math.Clamp(specificChildIndex.EvaluateInt(ctx), 0, Children.Count - 1);
            }
            else
            {
                var eligible = new List<(int Index, float Weight)>(Children.Count);

                for (var i = 0; i < Children.Count; i++)
                {
                    if (!Children[i].IsEligible(ctx, out var weight))
                    {
                        continue;
                    }

                    eligible.Add((i, weight));
                }

                if (eligible.Count == 0)
                {
                    return;
                }

                if (string.Equals(mode, "FIRST", StringComparison.OrdinalIgnoreCase))
                {
                    pickedIndex = eligible[0].Index;
                }
                else
                {
                    var picked = SmartPropHelpers.PickWeightedIndex(eligible.Count, i => eligible[i].Weight, ctx.Random);
                    pickedIndex = eligible[picked].Index;
                }
            }

            if (!string.IsNullOrEmpty(outputChoiceVariableName))
            {
                ctx.SetVariable(outputChoiceVariableName, (double)pickedIndex);
            }

            Children[pickedIndex].Evaluate(state, ctx);
        }
    }
}
