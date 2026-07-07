using System.Linq;
using ValveResourceFormat.Serialization.KeyValues;
namespace ValveResourceFormat.ResourceTypes.SmartProps.Operations
{
    /// <summary>
    /// Sets the current tint by sampling a color gradient: at a random position, at a random
    /// stop, or at a specific position.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropOperation_RandomColorTintColor">CSmartPropOperation_RandomColorTintColor</seealso>
    sealed class RandomColorTintOperation : SmartPropOperation
    {
        private readonly (float Position, VectorAttribute Color)[] stops;
        private readonly StringAttribute selectionMode;
        private readonly FloatAttribute colorPosition;
        private readonly StringAttribute mode;

        public RandomColorTintOperation(SmartPropDefinitionParser parse) : base(parse)
        {
            var gradient = parse.Data.GetSubCollection("m_Gradient");
            var gradientStops = gradient?.GetArray("m_Stops");

            stops = gradientStops == null
                ? []
                : [.. gradientStops.Select(stop => (
                    stop.GetFloatProperty("m_flPosition"),
                    new SmartPropDefinitionParser(stop).Vector("m_Color")))];

            selectionMode = parse.String("m_SelectionMode", "RANDOM");
            colorPosition = parse.Float("m_ColorPosition");
            mode = parse.String("m_Mode", "MULTIPLY_OBJECT");
        }

        public override bool Apply(ref SmartPropState state, SmartPropEvaluationContext ctx)
        {
            if (stops.Length == 0)
            {
                return true;
            }

            var selection = selectionMode.Evaluate(ctx)!.ToUpperInvariant();
            Vector4 color;

            if (selection is "SPECIFIC" or "SPECIFIC_COLOR")
            {
                color = SampleGradient(colorPosition.Evaluate(ctx), ctx);
            }
            else if (selection == "GRADIENT_RANDOM_STOP")
            {
                var stop = stops[ctx.Random.RandomInt(0, stops.Length - 1)];
                color = stop.Color.EvaluateColor(ctx, Vector4.One);
            }
            else
            {
                color = SampleGradient(ctx.Random.RandomFloat(), ctx);
            }

            SmartPropHelpers.ApplyTint(ref state, color, mode.Evaluate(ctx));
            return true;
        }

        private Vector4 SampleGradient(float position, SmartPropEvaluationContext ctx)
        {
            var previousPosition = float.MinValue;
            var previousColor = stops[0].Color.EvaluateColor(ctx, Vector4.One);

            foreach (var (stopPosition, stopColorAttribute) in stops)
            {
                var stopColor = stopColorAttribute.EvaluateColor(ctx, Vector4.One);

                if (position <= stopPosition)
                {
                    if (previousPosition == float.MinValue || stopPosition <= previousPosition)
                    {
                        return stopColor;
                    }

                    var t = (position - previousPosition) / (stopPosition - previousPosition);
                    return Vector4.Lerp(previousColor, stopColor, t);
                }

                previousPosition = stopPosition;
                previousColor = stopColor;
            }

            return previousColor;
        }
    }
}
