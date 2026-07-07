using ValveKeyValue;
using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.ResourceTypes.SmartProps
{
    /// <summary>
    /// A literal vector read from KV3, before any interpretation. Colors are authored as 0-255
    /// component arrays, vectors as plain floats, so the consumer decides how to normalize.
    /// </summary>
    internal readonly record struct RawComponents(Vector4 Value, int Count);

    /// <summary>
    /// Resolves smart prop attribute fields. Nearly every field can be authored as a literal,
    /// an object binding it to a variable (<c>m_SourceName</c>), an expression (<c>m_Expression</c>),
    /// or a vector with independently bound components (<c>m_Components</c>).
    /// </summary>
    internal static class SmartPropAttribute
    {
        public static object? Resolve(KVObject node, SmartPropEvaluationContext ctx)
        {
            switch (node.ValueType)
            {
                case KVValueType.Boolean:
                    return (bool)node;
                case KVValueType.String:
                    return (string)node;
                case KVValueType.Int16:
                case KVValueType.Int32:
                case KVValueType.Int64:
                case KVValueType.UInt16:
                case KVValueType.UInt32:
                case KVValueType.UInt64:
                case KVValueType.FloatingPoint:
                case KVValueType.FloatingPoint64:
                    return (double)node;
                case KVValueType.Null:
                    return null;
                case KVValueType.Array:
                case KVValueType.Collection:
                    return TryResolveComponents(node, ctx, out var components)
                        ? components
                        : ResolveBinding(node, ctx);
                default:
                    return null;
            }
        }

        /// <summary>
        /// Resolves a variable binding (<c>m_SourceName</c>) or expression object.
        /// </summary>
        private static object? ResolveBinding(KVObject node, SmartPropEvaluationContext ctx)
        {
            if (node.ValueType != KVValueType.Collection)
            {
                return null;
            }

            if (node.TryGetValue("m_SourceName", out var sourceName))
            {
                return ctx.GetVariable((string)sourceName!);
            }

            if (node.TryGetValue("m_Expression", out var expression))
            {
                return SmartPropExpression.Evaluate((string)expression!, ctx);
            }

            return null;
        }

        /// <summary>
        /// Resolves a literal component array or a <c>m_Components</c> object without allocating.
        /// </summary>
        private static bool TryResolveComponents(KVObject node, SmartPropEvaluationContext ctx, out RawComponents components)
        {
            IReadOnlyList<KVObject>? elements = null;

            if (node.IsArray)
            {
                elements = (IReadOnlyList<KVObject>)node.Values;
            }
            else if (node.ValueType == KVValueType.Collection
                && node.TryGetValue("m_Components", out var componentsNode)
                && componentsNode.IsArray)
            {
                elements = (IReadOnlyList<KVObject>)componentsNode.Values;
            }

            if (elements == null)
            {
                components = default;
                return false;
            }

            var value = Vector4.Zero;
            var count = Math.Min(elements.Count, 4);

            for (var i = 0; i < count; i++)
            {
                var element = elements[i];
                var component = element.ValueType is KVValueType.Collection or KVValueType.Array
                    ? (float)SmartPropExpression.ToNumber(Resolve(element, ctx))
                    : (float)SmartPropExpression.ToNumber(ResolveScalar(element));

                value = i switch
                {
                    0 => value with { X = component },
                    1 => value with { Y = component },
                    2 => value with { Z = component },
                    _ => value with { W = component },
                };
            }

            components = new RawComponents(value, count);
            return true;
        }

        private static object? ResolveScalar(KVObject node) => node.ValueType switch
        {
            KVValueType.Boolean => (bool)node,
            KVValueType.String => (string)node,
            KVValueType.Int16 or KVValueType.Int32 or KVValueType.Int64
                or KVValueType.UInt16 or KVValueType.UInt32 or KVValueType.UInt64
                or KVValueType.FloatingPoint or KVValueType.FloatingPoint64 => (double)node,
            _ => null,
        };

        public static float GetFloat(KVObject obj, string name, SmartPropEvaluationContext ctx, float defaultValue = 0f)
        {
            if (!obj.TryGetValue(name, out var node))
            {
                return defaultValue;
            }

            var value = Resolve(node!, ctx);
            return value == null ? defaultValue : (float)SmartPropExpression.ToNumber(value);
        }

        public static int GetInt(KVObject obj, string name, SmartPropEvaluationContext ctx, int defaultValue = 0)
        {
            if (!obj.TryGetValue(name, out var node))
            {
                return defaultValue;
            }

            var value = Resolve(node!, ctx);
            return value == null ? defaultValue : (int)Math.Round(SmartPropExpression.ToNumber(value));
        }

        public static bool GetBool(KVObject obj, string name, SmartPropEvaluationContext ctx, bool defaultValue = false)
        {
            if (!obj.TryGetValue(name, out var node))
            {
                return defaultValue;
            }

            var value = Resolve(node!, ctx);
            return value == null ? defaultValue : SmartPropExpression.ToBool(value);
        }

        public static string? GetString(KVObject obj, string name, SmartPropEvaluationContext ctx, string? defaultValue = null)
        {
            if (!obj.TryGetValue(name, out var node))
            {
                return defaultValue;
            }

            var value = Resolve(node!, ctx);
            return value switch
            {
                string s => s,
                null => defaultValue,
                _ => value.ToString(),
            };
        }

        /// <summary>
        /// Evaluates an expression-string field as a boolean. Tolerates fields where the value
        /// is already a boolean or number instead of an expression string.
        /// </summary>
        public static bool GetExpressionBool(KVObject obj, string name, SmartPropEvaluationContext ctx, bool defaultValue)
        {
            if (!obj.TryGetValue(name, out var node))
            {
                return defaultValue;
            }

            if (node!.ValueType == KVValueType.String)
            {
                var expression = (string)node;

                if (expression.Length == 0)
                {
                    return defaultValue;
                }

                return SmartPropExpression.ToBool(SmartPropExpression.Evaluate(expression, ctx));
            }

            var value = Resolve(node, ctx);
            return value == null ? defaultValue : SmartPropExpression.ToBool(value);
        }

        public static Vector3 GetVector3(KVObject obj, string name, SmartPropEvaluationContext ctx, Vector3 defaultValue = default)
        {
            if (!obj.TryGetValue(name, out var node))
            {
                return defaultValue;
            }

            return ToVector3(Resolve(node!, ctx), defaultValue);
        }

        public static Vector3 ToVector3(object? value, Vector3 defaultValue = default) => value switch
        {
            RawComponents raw => new Vector3(raw.Value.X, raw.Value.Y, raw.Value.Z),
            Vector3 v => v,
            Vector4 v => new Vector3(v.X, v.Y, v.Z),
            double d => new Vector3((float)d),
            _ => defaultValue,
        };

        /// <summary>
        /// Resolves a color field. Literal colors are authored as 0-255 component arrays;
        /// bound values may already be normalized vectors.
        /// </summary>
        public static Vector4 GetColor(KVObject obj, string name, SmartPropEvaluationContext ctx, Vector4 defaultValue)
        {
            if (!obj.TryGetValue(name, out var node))
            {
                return defaultValue;
            }

            return ToColor(Resolve(node!, ctx), defaultValue);
        }

        public static Vector4 ToColor(object? value, Vector4 defaultValue) => value switch
        {
            RawComponents { Count: >= 4 } raw => raw.Value / 255f,
            RawComponents { Count: 3 } raw => new Vector4(raw.Value.X / 255f, raw.Value.Y / 255f, raw.Value.Z / 255f, 1f),
            Vector4 v => v,
            Vector3 v => new Vector4(v, 1f),
            _ => defaultValue,
        };
    }
}
