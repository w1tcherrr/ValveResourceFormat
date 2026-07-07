using System.Globalization;

namespace ValveResourceFormat.ResourceTypes.SmartProps
{
    /// <summary>
    /// Interpreter for the C-like expression strings used throughout smart props
    /// (<c>m_Expression</c>, bindable attribute components, hide/read-only expressions).
    /// Values are <see cref="double"/>, <see cref="bool"/> or <see cref="string"/>.
    /// </summary>
    internal static class SmartPropExpression
    {
        internal abstract class Node
        {
            public abstract object? Evaluate(SmartPropEvaluationContext ctx);
        }

        private sealed class ConstantNode(object value) : Node
        {
            public override object? Evaluate(SmartPropEvaluationContext ctx) => value;
        }

        private sealed class VariableNode(string name) : Node
        {
            public override object? Evaluate(SmartPropEvaluationContext ctx)
            {
                var value = ctx.GetVariable(name);

                if (value == null)
                {
                    ctx.Warn(SmartPropDiagnosticCode.UnknownVariable, name, $"Expression references unknown variable '{name}'");
                }

                return value;
            }
        }

        private sealed class MemberNode(string name, char component) : Node
        {
            public override object? Evaluate(SmartPropEvaluationContext ctx)
            {
                var value = ctx.GetVariable(name);

                if (value == null)
                {
                    ctx.Warn(SmartPropDiagnosticCode.UnknownVariable, name, $"Expression references unknown variable '{name}'");
                    return 0.0;
                }

                var vector = value switch
                {
                    Vector3 v => new Vector4(v, 0f),
                    Vector4 v => v,
                    _ => Vector4.Zero,
                };

                return (double)(component switch
                {
                    'y' => vector.Y,
                    'z' => vector.Z,
                    'w' => vector.W,
                    _ => vector.X,
                });
            }
        }

        private sealed class UnaryNode(char op, Node operand) : Node
        {
            public override object? Evaluate(SmartPropEvaluationContext ctx)
            {
                var value = operand.Evaluate(ctx);

                return op switch
                {
                    '-' => -ToNumber(value),
                    '!' => !ToBool(value),
                    _ => value,
                };
            }
        }

        private sealed class BinaryNode(string op, Node left, Node right) : Node
        {
            public override object? Evaluate(SmartPropEvaluationContext ctx)
            {
                // Short-circuiting logical operators
                if (op == "&&")
                {
                    return ToBool(left.Evaluate(ctx)) && ToBool(right.Evaluate(ctx));
                }

                if (op == "||")
                {
                    return ToBool(left.Evaluate(ctx)) || ToBool(right.Evaluate(ctx));
                }

                var l = left.Evaluate(ctx);
                var r = right.Evaluate(ctx);

                return op switch
                {
                    "+" => ToNumber(l) + ToNumber(r),
                    "-" => ToNumber(l) - ToNumber(r),
                    "*" => ToNumber(l) * ToNumber(r),
                    "/" => ToNumber(l) / ToNumber(r),
                    "%" => ToNumber(l) % ToNumber(r),
                    "<" => ToNumber(l) < ToNumber(r),
                    "<=" => ToNumber(l) <= ToNumber(r),
                    ">" => ToNumber(l) > ToNumber(r),
                    ">=" => ToNumber(l) >= ToNumber(r),
                    "==" => ValuesEqual(l, r),
                    "!=" => !ValuesEqual(l, r),
                    _ => null,
                };
            }
        }

        private sealed class TernaryNode(Node condition, Node whenTrue, Node whenFalse) : Node
        {
            public override object? Evaluate(SmartPropEvaluationContext ctx)
                => ToBool(condition.Evaluate(ctx)) ? whenTrue.Evaluate(ctx) : whenFalse.Evaluate(ctx);
        }

        private sealed class CallNode(string name, Node[] args) : Node
        {
            public override object? Evaluate(SmartPropEvaluationContext ctx)
            {
                double Arg(int i, double def = 0) => args.Length > i ? ToNumber(args[i].Evaluate(ctx)) : def;

                switch (name.ToUpperInvariant())
                {
                    case "MIN": return Math.Min(Arg(0), Arg(1));
                    case "MAX": return Math.Max(Arg(0), Arg(1));
                    case "ABS": return Math.Abs(Arg(0));
                    case "FLOOR": return Math.Floor(Arg(0));
                    case "CEIL": return Math.Ceiling(Arg(0));
                    case "ROUND": return Math.Round(Arg(0));
                    case "SQRT": return Math.Sqrt(Arg(0));
                    case "SIN": return Math.Sin(Arg(0));
                    case "COS": return Math.Cos(Arg(0));
                    case "TAN": return Math.Tan(Arg(0));
                    case "ASIN": return Math.Asin(Arg(0));
                    case "ACOS": return Math.Acos(Arg(0));
                    case "ATAN": return Math.Atan(Arg(0));
                    case "CLAMP": return Math.Clamp(Arg(0), Arg(1), Arg(2, 1));
                    case "LERP": return Arg(0) + (Arg(1) - Arg(0)) * Arg(2);
                    case "POW": return Math.Pow(Arg(0), Arg(1));
                    case "EXP": return Math.Exp(Arg(0));
                    case "LOG": return Math.Log(Arg(0));
                    case "ATAN2": return Math.Atan2(Arg(0), Arg(1));
                    case "SIGN": return (double)Math.Sign(Arg(0));
                    case "FRAC": return Arg(0) - Math.Floor(Arg(0));
                    case "DEG2RAD": return Arg(0) * (Math.PI / 180.0);
                    case "RAD2DEG": return Arg(0) * (180.0 / Math.PI);
                    case "LENGTH2D": return Math.Sqrt(Arg(0) * Arg(0) + Arg(1) * Arg(1));
                    case "LENGTH3D": return Math.Sqrt(Arg(0) * Arg(0) + Arg(1) * Arg(1) + Arg(2) * Arg(2));
                    case "RANDOMFLOAT": return (double)ctx.Random.RandomFloat((float)Arg(0), (float)Arg(1, 1));
                    case "RANDOMINT": return (double)ctx.Random.RandomInt((int)Arg(0), (int)Arg(1));
                    case "INSTANCEINDEX": return (double)ctx.InstanceIndex;
                    case "INSTANCECOUNT": return (double)ctx.InstanceCount;
                    case "EVALUATIONDEPTH": return (double)ctx.Depth;
                    case "MAXEVALUATIONDEPTH": return (double)ctx.MaxDepth;
                    case "LINEARSCALE": return ctx.LinearScale;
                    case "LINELENGTH": return ctx.LineLength;
                    case "PATHPARAMETER": return ctx.PathParameter;
                    default:
                        ctx.Warn(SmartPropDiagnosticCode.UnknownFunction, name, $"Expression calls unknown function '{name}'");
                        return 0.0;
                }
            }
        }

        private sealed class FailedNode(string expression) : Node
        {
            public override object? Evaluate(SmartPropEvaluationContext ctx)
            {
                ctx.Warn(SmartPropDiagnosticCode.ExpressionParseError, expression, $"Failed to parse expression '{expression}'");
                return null;
            }
        }

        /// <summary>
        /// Evaluates an expression against the given context, caching the parsed form in the
        /// context's document-owned cache. Returns null when the expression cannot be parsed.
        /// </summary>
        public static object? Evaluate(string expression, SmartPropEvaluationContext ctx)
        {
            if (string.IsNullOrWhiteSpace(expression))
            {
                return null;
            }

            var node = ctx.ExpressionCache.GetOrAdd(expression, static text =>
            {
                try
                {
                    var parser = new Parser(text);
                    var parsed = parser.ParseExpression();
                    parser.ExpectEnd();
                    return parsed;
                }
                catch (FormatException)
                {
                    return new FailedNode(text);
                }
            });

            return node.Evaluate(ctx);
        }

        public static double ToNumber(object? value) => value switch
        {
            double d => d,
            bool b => b ? 1.0 : 0.0,
            string s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0.0,
            _ => 0.0,
        };

        public static bool ToBool(object? value) => value switch
        {
            bool b => b,
            double d => d != 0.0,
            string s => s.Equals("true", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };

        public static bool ValuesEqual(object? left, object? right)
        {
            if (left is string ls && right is string rs)
            {
                return string.Equals(ls, rs, StringComparison.OrdinalIgnoreCase);
            }

            if (left is bool || right is bool)
            {
                return ToBool(left) == ToBool(right);
            }

            return ToNumber(left) == ToNumber(right);
        }

        private ref struct Parser(string text)
        {
            private readonly string text = text;
            private int pos;

            public void ExpectEnd()
            {
                SkipWhitespace();

                if (pos < text.Length)
                {
                    throw new FormatException("Trailing characters in expression");
                }
            }

            public Node ParseExpression() => ParseTernary();

            private Node ParseTernary()
            {
                var condition = ParseOr();
                SkipWhitespace();

                if (!TryConsume('?'))
                {
                    return condition;
                }

                var whenTrue = ParseTernary();
                SkipWhitespace();

                if (!TryConsume(':'))
                {
                    throw new FormatException("Expected ':' in ternary");
                }

                var whenFalse = ParseTernary();
                return new TernaryNode(condition, whenTrue, whenFalse);
            }

            private Node ParseOr()
            {
                var left = ParseAnd();

                while (TryConsumeOperator("||"))
                {
                    left = new BinaryNode("||", left, ParseAnd());
                }

                return left;
            }

            private Node ParseAnd()
            {
                var left = ParseEquality();

                while (TryConsumeOperator("&&"))
                {
                    left = new BinaryNode("&&", left, ParseEquality());
                }

                return left;
            }

            private Node ParseEquality()
            {
                var left = ParseRelational();

                while (true)
                {
                    if (TryConsumeOperator("=="))
                    {
                        left = new BinaryNode("==", left, ParseRelational());
                    }
                    else if (TryConsumeOperator("!="))
                    {
                        left = new BinaryNode("!=", left, ParseRelational());
                    }
                    else
                    {
                        return left;
                    }
                }
            }

            private Node ParseRelational()
            {
                var left = ParseAdditive();

                while (true)
                {
                    if (TryConsumeOperator("<="))
                    {
                        left = new BinaryNode("<=", left, ParseAdditive());
                    }
                    else if (TryConsumeOperator(">="))
                    {
                        left = new BinaryNode(">=", left, ParseAdditive());
                    }
                    else if (TryConsumeOperator("<"))
                    {
                        left = new BinaryNode("<", left, ParseAdditive());
                    }
                    else if (TryConsumeOperator(">"))
                    {
                        left = new BinaryNode(">", left, ParseAdditive());
                    }
                    else
                    {
                        return left;
                    }
                }
            }

            private Node ParseAdditive()
            {
                var left = ParseMultiplicative();

                while (true)
                {
                    SkipWhitespace();

                    if (TryConsume('+'))
                    {
                        left = new BinaryNode("+", left, ParseMultiplicative());
                    }
                    else if (pos < text.Length && text[pos] == '-')
                    {
                        pos++;
                        left = new BinaryNode("-", left, ParseMultiplicative());
                    }
                    else
                    {
                        return left;
                    }
                }
            }

            private Node ParseMultiplicative()
            {
                var left = ParseUnary();

                while (true)
                {
                    SkipWhitespace();

                    if (TryConsume('*'))
                    {
                        left = new BinaryNode("*", left, ParseUnary());
                    }
                    else if (TryConsume('/'))
                    {
                        left = new BinaryNode("/", left, ParseUnary());
                    }
                    else if (TryConsume('%'))
                    {
                        left = new BinaryNode("%", left, ParseUnary());
                    }
                    else
                    {
                        return left;
                    }
                }
            }

            private Node ParseUnary()
            {
                SkipWhitespace();

                if (TryConsume('-'))
                {
                    return new UnaryNode('-', ParseUnary());
                }

                if (pos < text.Length && text[pos] == '!' && (pos + 1 >= text.Length || text[pos + 1] != '='))
                {
                    pos++;
                    return new UnaryNode('!', ParseUnary());
                }

                return ParsePrimary();
            }

            private Node ParsePrimary()
            {
                SkipWhitespace();

                if (pos >= text.Length)
                {
                    throw new FormatException("Unexpected end of expression");
                }

                var c = text[pos];

                if (c == '(')
                {
                    pos++;
                    var inner = ParseExpression();
                    SkipWhitespace();

                    if (!TryConsume(')'))
                    {
                        throw new FormatException("Expected ')'");
                    }

                    return inner;
                }

                if (c == '"' || c == '\'')
                {
                    pos++;
                    var start = pos;

                    while (pos < text.Length && text[pos] != c)
                    {
                        pos++;
                    }

                    if (pos >= text.Length)
                    {
                        throw new FormatException("Unterminated string literal");
                    }

                    var literal = text[start..pos];
                    pos++;
                    return new ConstantNode(literal);
                }

                if (char.IsAsciiDigit(c) || c == '.')
                {
                    var start = pos;

                    while (pos < text.Length && (char.IsAsciiDigit(text[pos]) || text[pos] == '.'))
                    {
                        pos++;
                    }

                    if (!double.TryParse(text[start..pos], NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    {
                        throw new FormatException("Invalid number");
                    }

                    return new ConstantNode(number);
                }

                if (char.IsLetter(c) || c == '_')
                {
                    var start = pos;

                    while (pos < text.Length && (char.IsLetterOrDigit(text[pos]) || text[pos] == '_'))
                    {
                        pos++;
                    }

                    var identifier = text[start..pos];
                    SkipWhitespace();

                    if (TryConsume('('))
                    {
                        var args = new List<Node>();
                        SkipWhitespace();

                        if (!TryConsume(')'))
                        {
                            while (true)
                            {
                                args.Add(ParseExpression());
                                SkipWhitespace();

                                if (TryConsume(','))
                                {
                                    continue;
                                }

                                if (TryConsume(')'))
                                {
                                    break;
                                }

                                throw new FormatException("Expected ',' or ')' in call");
                            }
                        }

                        return new CallNode(identifier, [.. args]);
                    }

                    if (identifier.Equals("true", StringComparison.OrdinalIgnoreCase))
                    {
                        return new ConstantNode(true);
                    }

                    if (identifier.Equals("false", StringComparison.OrdinalIgnoreCase))
                    {
                        return new ConstantNode(false);
                    }

                    if (identifier.Equals("pi", StringComparison.OrdinalIgnoreCase))
                    {
                        return new ConstantNode(Math.PI);
                    }

                    // Vector component access like fOffset.x
                    if (pos + 1 < text.Length && text[pos] == '.' && char.IsLetter(text[pos + 1]))
                    {
                        var component = char.ToLowerInvariant(text[pos + 1]);

                        if (component is 'x' or 'y' or 'z' or 'w')
                        {
                            pos += 2;
                            return new MemberNode(identifier, component);
                        }
                    }

                    return new VariableNode(identifier);
                }

                throw new FormatException($"Unexpected character '{c}'");
            }

            private void SkipWhitespace()
            {
                while (pos < text.Length)
                {
                    if (char.IsWhiteSpace(text[pos]))
                    {
                        pos++;
                        continue;
                    }

                    // Line comments appear in community-authored expressions
                    if (pos + 1 < text.Length && text[pos] == '/' && text[pos + 1] == '/')
                    {
                        while (pos < text.Length && text[pos] != '\n')
                        {
                            pos++;
                        }

                        continue;
                    }

                    break;
                }
            }

            private bool TryConsume(char c)
            {
                SkipWhitespace();

                if (pos < text.Length && text[pos] == c)
                {
                    pos++;
                    return true;
                }

                return false;
            }

            private bool TryConsumeOperator(string op)
            {
                SkipWhitespace();

                if (pos + op.Length <= text.Length && text.AsSpan(pos, op.Length).SequenceEqual(op))
                {
                    // Don't let '<' consume the '<' of '<=' etc.
                    if (op.Length == 1 && pos + 1 < text.Length && text[pos + 1] == '=' && (op[0] == '<' || op[0] == '>'))
                    {
                        return false;
                    }

                    pos += op.Length;
                    return true;
                }

                return false;
            }
        }
    }
}
