namespace ValveResourceFormat.ResourceTypes.SmartProps.Elements
{
    /// <summary>
    /// Lays children out on a 2D grid, horizontal or vertical, filling the dimensions by spacing
    /// or using authored counts, with optional alternate row/column shifting.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropElement_Layout2DGrid">CSmartPropElement_Layout2DGrid</seealso>
    sealed class Layout2DGridElement : SmartPropElement
    {
        private readonly FloatAttribute width;
        private readonly FloatAttribute length;
        private readonly BoolAttribute verticalLength;
        private readonly StringAttribute gridArrangement;
        private readonly FloatAttribute countW;
        private readonly FloatAttribute spacingWidth;
        private readonly FloatAttribute countL;
        private readonly FloatAttribute spacingLength;
        private readonly StringAttribute gridOriginMode;
        private readonly BoolAttribute alternateShift;
        private readonly FloatAttribute alternateShiftWidth;
        private readonly FloatAttribute alternateShiftLength;

        public Layout2DGridElement(SmartPropDefinitionParser parse) : base(parse)
        {
            width = parse.Float("m_flWidth", 100f);
            length = parse.Float("m_flLength", 100f);
            verticalLength = parse.Bool("m_bVerticalLength", false);
            gridArrangement = parse.String("m_GridArrangement", "SEGMENT");
            countW = parse.Float("m_nCountW", 5f);
            spacingWidth = parse.Float("m_flSpacingWidth", 20f);
            countL = parse.Float("m_nCountL", 5f);
            spacingLength = parse.Float("m_flSpacingLength", 20f);
            gridOriginMode = parse.String("m_GridOriginMode", "CENTER");
            alternateShift = parse.Bool("m_bAlternateShift", false);
            alternateShiftWidth = parse.Float("m_flAlternateShiftWidth", 0.5f);
            alternateShiftLength = parse.Float("m_flAlternateShiftLength");
        }

        protected override void OnEvaluate(ref SmartPropState state, SmartPropEvaluationContext ctx)
        {
            if (Children.Count == 0)
            {
                return;
            }

            var gridWidth = width.Evaluate(ctx);
            var gridLength = length.Evaluate(ctx);
            var vertical = verticalLength.Evaluate(ctx);

            // FILL derives counts from the dimensions and spacing; SEGMENT uses the authored counts
            var fill = gridArrangement.Evaluate(ctx)!.Equals("FILL", StringComparison.OrdinalIgnoreCase);

            ResolveGridAxis(fill ? 0 : countW.EvaluateInt(ctx), spacingWidth.Evaluate(ctx), gridWidth, out var cellsW, out var stepW);
            ResolveGridAxis(fill ? 0 : countL.EvaluateInt(ctx), spacingLength.Evaluate(ctx), gridLength, out var cellsL, out var stepL);

            var corner = gridOriginMode.Evaluate(ctx)!.Equals("CORNER", StringComparison.OrdinalIgnoreCase);
            var startW = corner ? 0f : -(cellsW - 1) * stepW * 0.5f;
            var startL = corner ? 0f : -(cellsL - 1) * stepL * 0.5f;

            // Alternate shift amounts are fractions of the cell step (schema default width shift is 0.5)
            var alternate = alternateShift.Evaluate(ctx);
            var shiftW = alternate ? alternateShiftWidth.Evaluate(ctx) * stepW : 0f;
            var shiftL = alternate ? alternateShiftLength.Evaluate(ctx) * stepL : 0f;

            using var loop = ctx.EnterLoop(cellsW * cellsL);
            var instance = 0;

            for (var row = 0; row < cellsL; row++)
            {
                for (var column = 0; column < cellsW; column++)
                {
                    var w = startW + column * stepW + (row % 2 == 1 ? shiftW : 0f);
                    var l = startL + row * stepL + (column % 2 == 1 ? shiftL : 0f);
                    var offset = vertical ? new Vector3(w, 0f, l) : new Vector3(w, l, 0f);

                    var childState = state;
                    SmartPropHelpers.ApplyTranslate(ref childState, offset, SmartPropSpace.Element);

                    ctx.InstanceIndex = instance++;
                    EvaluateChildren(ref childState, ctx);
                }
            }
        }

        private static void ResolveGridAxis(int count, float spacing, float dimension, out int resolvedCount, out float step)
        {
            if (count > 0)
            {
                resolvedCount = Math.Min(count, 1024);
                step = spacing > 0f ? spacing : (resolvedCount > 1 ? dimension / (resolvedCount - 1) : 0f);
            }
            else if (spacing > 0f && dimension > 0f)
            {
                resolvedCount = Math.Min((int)(dimension / spacing) + 1, 1024);
                step = spacing;
            }
            else
            {
                resolvedCount = 1;
                step = 0f;
            }
        }
    }
}
