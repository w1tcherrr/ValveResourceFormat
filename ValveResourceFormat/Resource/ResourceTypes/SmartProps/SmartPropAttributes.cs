using ValveKeyValue;
using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.ResourceTypes.SmartProps
{
    /// <summary>
    /// A literal vector read from KV3, before any interpretation. Colors are authored as 0-255
    /// component arrays, vectors as plain floats, so the consumer decides how to normalize.
    /// </summary>
    internal readonly record struct RawComponents(Vector4 Value, int Count);

    /// <summary>How an authored attribute field provides its value.</summary>
    internal enum AttributeKind : byte
    {
        Missing,
        Literal,
        Variable,
        Expression,
        Components,
    }

    /// <summary>
    /// Shared resolution for smart prop attribute fields. Nearly every field can be authored as a
    /// literal, an object binding it to a variable (<c>m_SourceName</c>), an expression
    /// (<c>m_Expression</c>), or a vector with independently bound components (<c>m_Components</c>).
    /// The typed attribute structs below classify a field once at document compile time and
    /// evaluate cheaply per run.
    /// </summary>
    internal static class SmartPropValue
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
                    if (TryResolveComponents(node, ctx, out var components))
                    {
                        return components;
                    }

                    return ResolveBinding(node, ctx);
                default:
                    return null;
            }
        }

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

        public static bool TryResolveComponents(KVObject node, SmartPropEvaluationContext ctx, out RawComponents components)
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
                var component = (float)SmartPropExpression.ToNumber(Resolve(elements[i], ctx));

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

        public static Vector3 ToVector3(object? value, Vector3 defaultValue = default) => value switch
        {
            RawComponents raw => new Vector3(raw.Value.X, raw.Value.Y, raw.Value.Z),
            Vector3 v => v,
            Vector4 v => new Vector3(v.X, v.Y, v.Z),
            double d => new Vector3((float)d),
            _ => defaultValue,
        };

        public static Vector4 ToColor(object? value, Vector4 defaultValue) => value switch
        {
            RawComponents { Count: >= 4 } raw => raw.Value / 255f,
            RawComponents { Count: 3 } raw => new Vector4(raw.Value.X / 255f, raw.Value.Y / 255f, raw.Value.Z / 255f, 1f),
            Vector4 v => v,
            Vector3 v => new Vector4(v, 1f),
            _ => defaultValue,
        };

        /// <summary>
        /// Converts a container holding <c>m_Value</c> + <c>m_DataType</c> (choice values,
        /// SetVariable payloads) into the environment representation of that type.
        /// </summary>
        public static object ConvertDataTypedValue(KVObject container, SmartPropEvaluationContext ctx)
        {
            var resolved = container.TryGetValue("m_Value", out var valueNode)
                ? Resolve(valueNode!, ctx)
                : null;

            var dataType = container.GetStringProperty("m_DataType", string.Empty);

            return dataType.ToUpperInvariant() switch
            {
                "INTEGER" or "INT" or "FLOAT" or "DOUBLE" => SmartPropExpression.ToNumber(resolved),
                "BOOL" or "BOOLEAN" => SmartPropExpression.ToBool(resolved),
                "STRING" => resolved as string ?? string.Empty,
                "COLOR" => ToColor(resolved, Vector4.One),
                _ => NormalizeValue(resolved),
            };
        }

        public static object NormalizeValue(object? value) => value switch
        {
            RawComponents { Count: >= 4 } raw => raw.Value,
            RawComponents raw => new Vector3(raw.Value.X, raw.Value.Y, raw.Value.Z),
            null => 0.0,
            _ => value,
        };
    }

    /// <summary>A boolean attribute field, classified once at compile time.</summary>
    internal readonly struct BoolAttribute
    {
        private readonly AttributeKind kind;
        private readonly bool literal;
        private readonly string? text;
        private readonly KVObject? node;

        private BoolAttribute(AttributeKind kind, bool literal, string? text, KVObject? node)
        {
            this.kind = kind;
            this.literal = literal;
            this.text = text;
            this.node = node;
        }

        public static BoolAttribute Parse(KVObject data, string name, bool defaultValue)
        {
            if (!data.TryGetValue(name, out var field))
            {
                return new BoolAttribute(AttributeKind.Missing, defaultValue, null, null);
            }

            return field!.ValueType switch
            {
                KVValueType.Boolean => new BoolAttribute(AttributeKind.Literal, (bool)field, null, null),
                KVValueType.Collection when field.TryGetValue("m_SourceName", out var source)
                    => new BoolAttribute(AttributeKind.Variable, defaultValue, (string)source!, null),
                KVValueType.Collection when field.TryGetValue("m_Expression", out var expression)
                    => new BoolAttribute(AttributeKind.Expression, defaultValue, (string)expression!, null),
                _ => new BoolAttribute(AttributeKind.Components, defaultValue, null, field),
            };
        }

        public bool Evaluate(SmartPropEvaluationContext ctx) => kind switch
        {
            AttributeKind.Missing => literal,
            AttributeKind.Literal => literal,
            AttributeKind.Variable => ctx.GetVariable(text!) is object value ? SmartPropExpression.ToBool(value) : literal,
            AttributeKind.Expression => SmartPropExpression.Evaluate(text!, ctx) is object value ? SmartPropExpression.ToBool(value) : literal,
            _ => SmartPropValue.Resolve(node!, ctx) is object fallback ? SmartPropExpression.ToBool(fallback) : literal,
        };
    }

    /// <summary>A float attribute field, classified once at compile time.</summary>
    internal readonly struct FloatAttribute
    {
        private readonly AttributeKind kind;
        private readonly float literal;
        private readonly string? text;
        private readonly KVObject? node;

        private FloatAttribute(AttributeKind kind, float literal, string? text, KVObject? node)
        {
            this.kind = kind;
            this.literal = literal;
            this.text = text;
            this.node = node;
        }

        public static FloatAttribute Parse(KVObject data, string name, float defaultValue)
        {
            if (!data.TryGetValue(name, out var field))
            {
                return new FloatAttribute(AttributeKind.Missing, defaultValue, null, null);
            }

            return ParseNode(field!, defaultValue);
        }

        /// <summary>Parses a standalone scalar node (e.g. one component of a vector attribute).</summary>
        public static FloatAttribute ParseNode(KVObject field, float defaultValue)
        {
            return field.ValueType switch
            {
                KVValueType.Int16 or KVValueType.Int32 or KVValueType.Int64
                    or KVValueType.UInt16 or KVValueType.UInt32 or KVValueType.UInt64
                    or KVValueType.FloatingPoint or KVValueType.FloatingPoint64
                    => new FloatAttribute(AttributeKind.Literal, (float)(double)field, null, null),
                KVValueType.Boolean
                    => new FloatAttribute(AttributeKind.Literal, (bool)field ? 1f : 0f, null, null),
                KVValueType.Collection when field.TryGetValue("m_SourceName", out var source)
                    => new FloatAttribute(AttributeKind.Variable, defaultValue, (string)source!, null),
                KVValueType.Collection when field.TryGetValue("m_Expression", out var expression)
                    => new FloatAttribute(AttributeKind.Expression, defaultValue, (string)expression!, null),
                _ => new FloatAttribute(AttributeKind.Components, defaultValue, null, field),
            };
        }

        public float Evaluate(SmartPropEvaluationContext ctx) => kind switch
        {
            AttributeKind.Missing => literal,
            AttributeKind.Literal => literal,
            AttributeKind.Variable => ctx.GetVariable(text!) is object value ? (float)SmartPropExpression.ToNumber(value) : literal,
            AttributeKind.Expression => SmartPropExpression.Evaluate(text!, ctx) is object value ? (float)SmartPropExpression.ToNumber(value) : literal,
            _ => SmartPropValue.Resolve(node!, ctx) is object fallback ? (float)SmartPropExpression.ToNumber(fallback) : literal,
        };

        public int EvaluateInt(SmartPropEvaluationContext ctx) => (int)MathF.Round(Evaluate(ctx));

        public bool IsMissing => kind == AttributeKind.Missing;
    }

    /// <summary>A string attribute field (also used for enum-valued fields), classified once at compile time.</summary>
    internal readonly struct StringAttribute
    {
        private readonly AttributeKind kind;
        private readonly string? literal;
        private readonly string? text;

        private StringAttribute(AttributeKind kind, string? literal, string? text)
        {
            this.kind = kind;
            this.literal = literal;
            this.text = text;
        }

        public static StringAttribute Parse(KVObject data, string name, string? defaultValue)
        {
            if (!data.TryGetValue(name, out var field))
            {
                return new StringAttribute(AttributeKind.Missing, defaultValue, null);
            }

            return field!.ValueType switch
            {
                KVValueType.String => new StringAttribute(AttributeKind.Literal, (string)field, null),
                KVValueType.Collection when field.TryGetValue("m_SourceName", out var source)
                    => new StringAttribute(AttributeKind.Variable, defaultValue, (string)source!),
                KVValueType.Collection when field.TryGetValue("m_Expression", out var expression)
                    => new StringAttribute(AttributeKind.Expression, defaultValue, (string)expression!),
                _ => new StringAttribute(AttributeKind.Missing, defaultValue, null),
            };
        }

        public string? Evaluate(SmartPropEvaluationContext ctx) => kind switch
        {
            AttributeKind.Missing => literal,
            AttributeKind.Literal => literal,
            AttributeKind.Variable => ctx.GetVariable(text!) switch
            {
                string s => s,
                null => literal,
                var value => value.ToString(),
            },
            _ => SmartPropExpression.Evaluate(text!, ctx) switch
            {
                string s => s,
                null => literal,
                var value => value.ToString(),
            },
        };
    }

    /// <summary>A vector attribute field, classified once at compile time.</summary>
    internal readonly struct VectorAttribute
    {
        private readonly AttributeKind kind;
        private readonly RawComponents literal;
        private readonly string? text;
        private readonly FloatAttribute[]? components;

        private VectorAttribute(AttributeKind kind, RawComponents literal, string? text, FloatAttribute[]? components)
        {
            this.kind = kind;
            this.literal = literal;
            this.text = text;
            this.components = components;
        }

        public static VectorAttribute Parse(KVObject data, string name, Vector3 defaultValue)
            => Parse(data, name, new RawComponents(new Vector4(defaultValue, 0f), 3));

        public static VectorAttribute Parse(KVObject data, string name, RawComponents defaultValue)
        {
            if (!data.TryGetValue(name, out var field))
            {
                return new VectorAttribute(AttributeKind.Missing, defaultValue, null, null);
            }

            return ParseNode(field!, defaultValue);
        }

        /// <summary>Parses a standalone vector node (e.g. one entry of a default path array).</summary>
        public static VectorAttribute ParseNode(KVObject field, RawComponents defaultValue)
        {
            switch (field.ValueType)
            {
                case KVValueType.Array:
                    return new VectorAttribute(AttributeKind.Literal, ReadLiteral(field), null, null);

                case KVValueType.Collection when field.TryGetValue("m_Components", out var componentsNode) && componentsNode!.IsArray:
                    {
                        var elements = (IReadOnlyList<KVObject>)componentsNode.Values;
                        var parsed = new FloatAttribute[Math.Min(elements.Count, 4)];

                        for (var i = 0; i < parsed.Length; i++)
                        {
                            parsed[i] = FloatAttribute.ParseNode(elements[i], 0f);
                        }

                        return new VectorAttribute(AttributeKind.Components, defaultValue, null, parsed);
                    }

                case KVValueType.Collection when field.TryGetValue("m_SourceName", out var source):
                    return new VectorAttribute(AttributeKind.Variable, defaultValue, (string)source!, null);

                case KVValueType.Collection when field.TryGetValue("m_Expression", out var expression):
                    return new VectorAttribute(AttributeKind.Expression, defaultValue, (string)expression!, null);

                default:
                    return new VectorAttribute(AttributeKind.Missing, defaultValue, null, null);
            }
        }

        private static RawComponents ReadLiteral(KVObject array)
        {
            var elements = (IReadOnlyList<KVObject>)array.Values;
            var value = Vector4.Zero;
            var count = Math.Min(elements.Count, 4);

            for (var i = 0; i < count; i++)
            {
                var element = elements[i];
                var component = element.ValueType switch
                {
                    KVValueType.Int16 or KVValueType.Int32 or KVValueType.Int64
                        or KVValueType.UInt16 or KVValueType.UInt32 or KVValueType.UInt64
                        or KVValueType.FloatingPoint or KVValueType.FloatingPoint64 => (float)(double)element,
                    _ => 0f,
                };

                value = i switch
                {
                    0 => value with { X = component },
                    1 => value with { Y = component },
                    2 => value with { Z = component },
                    _ => value with { W = component },
                };
            }

            return new RawComponents(value, count);
        }

        public RawComponents EvaluateRaw(SmartPropEvaluationContext ctx)
        {
            switch (kind)
            {
                case AttributeKind.Missing:
                case AttributeKind.Literal:
                    return literal;

                case AttributeKind.Components:
                    {
                        var value = Vector4.Zero;

                        for (var i = 0; i < components!.Length; i++)
                        {
                            var component = components[i].Evaluate(ctx);

                            value = i switch
                            {
                                0 => value with { X = component },
                                1 => value with { Y = component },
                                2 => value with { Z = component },
                                _ => value with { W = component },
                            };
                        }

                        return new RawComponents(value, components.Length);
                    }

                case AttributeKind.Variable:
                    return ToRaw(ctx.GetVariable(text!), literal);

                default:
                    return ToRaw(SmartPropExpression.Evaluate(text!, ctx), literal);
            }

            static RawComponents ToRaw(object? value, RawComponents fallback) => value switch
            {
                RawComponents raw => raw,
                Vector3 v => new RawComponents(new Vector4(v, 0f), 3),
                Vector4 v => new RawComponents(v, 4),
                double d => new RawComponents(new Vector4((float)d), 3),
                _ => fallback,
            };
        }

        public Vector3 Evaluate(SmartPropEvaluationContext ctx)
        {
            var raw = EvaluateRaw(ctx);
            return new Vector3(raw.Value.X, raw.Value.Y, raw.Value.Z);
        }

        /// <summary>Evaluates as a color: literal component arrays are 0-255, bound values are already normalized.</summary>
        public Vector4 EvaluateColor(SmartPropEvaluationContext ctx, Vector4 defaultColor)
        {
            switch (kind)
            {
                case AttributeKind.Missing:
                    return defaultColor;
                case AttributeKind.Literal:
                case AttributeKind.Components:
                    var raw = EvaluateRaw(ctx);
                    return raw.Count >= 4
                        ? raw.Value / 255f
                        : new Vector4(raw.Value.X / 255f, raw.Value.Y / 255f, raw.Value.Z / 255f, 1f);
                case AttributeKind.Variable:
                    return SmartPropValue.ToColor(ctx.GetVariable(text!), defaultColor);
                default:
                    return SmartPropValue.ToColor(SmartPropExpression.Evaluate(text!, ctx), defaultColor);
            }
        }
    }

    /// <summary>
    /// An expression-string field evaluated as a boolean. Tolerates fields where the value is
    /// already a boolean (the KV3 text parser coerces quoted "false" to a bool) or a binding.
    /// </summary>
    internal readonly struct ExpressionBoolAttribute
    {
        private readonly BoolAttribute inner;
        private readonly string? expression;
        private readonly bool defaultValue;

        private ExpressionBoolAttribute(BoolAttribute inner, string? expression, bool defaultValue)
        {
            this.inner = inner;
            this.expression = expression;
            this.defaultValue = defaultValue;
        }

        public static ExpressionBoolAttribute Parse(KVObject data, string name, bool defaultValue)
        {
            if (data.TryGetValue(name, out var field) && field!.ValueType == KVValueType.String)
            {
                var text = (string)field;
                return new ExpressionBoolAttribute(default, text.Length > 0 ? text : null, defaultValue);
            }

            return new ExpressionBoolAttribute(BoolAttribute.Parse(data, name, defaultValue), null, defaultValue);
        }

        public bool Evaluate(SmartPropEvaluationContext ctx)
        {
            if (expression != null)
            {
                var value = SmartPropExpression.Evaluate(expression, ctx);
                return value == null ? defaultValue : SmartPropExpression.ToBool(value);
            }

            return inner.Evaluate(ctx);
        }
    }
}
