using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using ValveKeyValue;
using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.ResourceTypes.SmartProps
{
    /// <summary>
    /// Builds the exposed parameters and choices of a document at load time, deriving the value
    /// domains of parameters so UIs can offer valid choices instead of free-form input.
    /// </summary>
    static class SmartPropParameterBuilder
    {
        internal static SmartPropParameterType GetParameterType(string className) => className switch
        {
            "CSmartPropVariable_Float" => SmartPropParameterType.Float,
            "CSmartPropVariable_Int" => SmartPropParameterType.Int,
            "CSmartPropVariable_Bool" => SmartPropParameterType.Bool,
            "CSmartPropVariable_Color" => SmartPropParameterType.Color,
            "CSmartPropVariable_Vector2D" => SmartPropParameterType.Vector2,
            "CSmartPropVariable_Vector3D" => SmartPropParameterType.Vector3,
            "CSmartPropVariable_Vector4D" => SmartPropParameterType.Vector4,
            "CSmartPropVariable_String" => SmartPropParameterType.String,
            "CSmartPropVariable_MaterialGroup" => SmartPropParameterType.MaterialGroup,
            "CSmartPropVariable_Model" => SmartPropParameterType.Model,
            "CSmartPropVariable_Material" => SmartPropParameterType.Material,
            _ when className.StartsWith("CSmartPropVariable_", StringComparison.Ordinal) => SmartPropParameterType.String,
            _ => SmartPropParameterType.Unknown,
        };

        internal static object GetVariableDefault(KVObject variable, SmartPropParameterType type, SmartPropEvaluationContext ctx)
        {
            var resolved = variable.TryGetValue("m_DefaultValue", out var defaultNode)
                ? SmartPropValue.Resolve(defaultNode!, ctx)
                : null;

            switch (type)
            {
                case SmartPropParameterType.Float:
                case SmartPropParameterType.Int:
                    // Hammer 5 Tools sometimes writes numeric defaults as empty strings
                    if (resolved is string s && s.Length == 0)
                    {
                        return 0.0;
                    }

                    return resolved == null ? 0.0 : SmartPropExpression.ToNumber(resolved);
                case SmartPropParameterType.Bool:
                    return resolved != null && SmartPropExpression.ToBool(resolved);
                case SmartPropParameterType.Color:
                    return SmartPropValue.ToColor(resolved, Vector4.One);
                case SmartPropParameterType.Vector2:
                case SmartPropParameterType.Vector3:
                    return SmartPropValue.ToVector3(resolved);
                case SmartPropParameterType.Vector4:
                    return resolved is RawComponents raw ? raw.Value : SmartPropValue.ToColor(resolved, Vector4.Zero);
                default:
                    return resolved as string ?? string.Empty;
            }
        }

        private static readonly Dictionary<string, string[]> VariableEnumMembers = new(StringComparer.Ordinal)
        {
            ["CSmartPropVariable_ApplyColorMode"] = ["MULTIPLY_OBJECT", "MULTIPLY_CURRENT", "REPLACE"],
            ["CSmartPropVariable_ChoiceSelectionMode"] = ["RANDOM", "FIRST", "SPECIFIC"],
            ["CSmartPropVariable_ColorSelectionMode"] = ["SPECIFIC_COLOR", "GRADIENT_RANDOM", "GRADIENT_RANDOM_STOP", "GRADIENT_LOCATION"],
            ["CSmartPropVariable_CoordinateSpace"] = ["WORLD", "OBJECT", "ELEMENT"],
            ["CSmartPropVariable_DirectionVector"] = ["FORWARD", "LEFT", "UP"],
            ["CSmartPropVariable_DistributionMode"] = ["RANDOM", "REGULAR"],
            ["CSmartPropVariable_GridOriginMode"] = ["CENTER", "CORNER"],
            ["CSmartPropVariable_GridPlacementMode"] = ["SEGMENT", "FILL"],
            ["CSmartPropVariable_OrientationMode"] = ["FIRST_OPEN_EDGE", "FIRST_CLOSED_EDGE", "UVMAP1", "UVMAP2"],
            ["CSmartPropVariable_PathPositions"] = ["ALL", "NTH", "START_AND_END", "CONTROL_POINTS"],
            ["CSmartPropVariable_PickMode"] = ["LARGEST_FIRST", "RANDOM", "ALL_IN_ORDER"],
            ["CSmartPropVariable_RadiusPlacementMode"] = ["SPHERE", "CIRCLE"],
            ["CSmartPropVariable_ScaleMode"] = ["NONE", "SCALE_END_TO_FIT", "SCALE_EQUALLY", "SCALE_MAXIMIZE"],
            ["CSmartPropVariable_TraceNoHit"] = ["NOTHING", "DISCARD", "MOVE_TO_START", "MOVE_TO_END"],
        };

        /// <summary>Literal values and expression strings found by walking a smart prop document.</summary>
        internal sealed class DocumentValueScan
        {
            public Dictionary<string, HashSet<double>> NumericLiterals { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, HashSet<string>> StringLiterals { get; } = new(StringComparer.OrdinalIgnoreCase);
            public List<string> Expressions { get; } = [];

            public void AddLiteral(string name, KVObject value)
            {
                switch (value.ValueType)
                {
                    case KVValueType.Int16:
                    case KVValueType.Int32:
                    case KVValueType.Int64:
                    case KVValueType.UInt16:
                    case KVValueType.UInt32:
                    case KVValueType.UInt64:
                    case KVValueType.FloatingPoint:
                    case KVValueType.FloatingPoint64:
                        GetOrAdd(NumericLiterals, name).Add((double)value);
                        break;
                    case KVValueType.String:
                        var text = (string)value;

                        if (text.Length > 0)
                        {
                            GetOrAdd(StringLiterals, name).Add(text);
                        }

                        break;
                    default:
                        break;
                }
            }

            private static HashSet<T> GetOrAdd<T>(Dictionary<string, HashSet<T>> dictionary, string name)
            {
                if (!dictionary.TryGetValue(name, out var set))
                {
                    set = [];
                    dictionary[name] = set;
                }

                return set;
            }
        }

        internal static DocumentValueScan ScanDocument(KVObject root)
        {
            var scan = new DocumentValueScan();
            ScanNode(root, scan, 0);
            return scan;
        }

        private static void ScanNode(KVObject node, DocumentValueScan scan, int depth)
        {
            if (depth > 64)
            {
                return;
            }

            if (node.IsCollection)
            {
                // CSmartPropFilter_VariableValue comparisons
                if (node.TryGetValue("m_VariableComparison", out var comparison) && comparison.IsCollection)
                {
                    var comparedName = comparison.GetStringProperty("m_Name");

                    if (!string.IsNullOrEmpty(comparedName) && comparison.TryGetValue("m_Value", out var comparedValue))
                    {
                        scan.AddLiteral(comparedName, comparedValue);
                    }
                }

                // CSmartPropOperation_SetVariable and its typed variants
                if (node.TryGetValue("m_VariableValue", out var variableValue))
                {
                    if (variableValue.IsCollection)
                    {
                        var targetName = variableValue.GetStringProperty("m_TargetName");

                        if (!string.IsNullOrEmpty(targetName) && variableValue.TryGetValue("m_Value", out var setValue))
                        {
                            scan.AddLiteral(targetName, setValue);
                        }
                    }
                    else if (node.TryGetValue("m_VariableName", out var variableName) && variableName.ValueType == KVValueType.String)
                    {
                        scan.AddLiteral((string)variableName, variableValue);
                    }
                }

                foreach (var (key, value) in node)
                {
                    if (value.ValueType == KVValueType.String)
                    {
                        if (key is "m_Expression" or "m_HideExpression" or "m_ReadOnlyExpression")
                        {
                            var expression = (string)value;

                            if (expression.Length > 0)
                            {
                                scan.Expressions.Add(expression);
                            }
                        }
                    }
                    else if (value.IsCollection || value.IsArray)
                    {
                        ScanNode(value, scan, depth + 1);
                    }
                }
            }
            else if (node.IsArray)
            {
                foreach (var element in (IReadOnlyList<KVObject>)node.Values)
                {
                    if (element.IsCollection || element.IsArray)
                    {
                        ScanNode(element, scan, depth + 1);
                    }
                }
            }
        }

        /// <summary>
        /// Mines <c>name == literal</c> / <c>name != literal</c> comparisons out of the document's expressions.
        /// </summary>
        private static void MineExpressionComparisons(DocumentValueScan scan, string name, SortedSet<double>? numbers, HashSet<string>? strings)
        {
            var pattern = new Regex(
                $@"\b{Regex.Escape(name)}\s*[=!]=\s*(?:(?<num>-?\d+(?:\.\d+)?)|""(?<quoted>[^""]*)""|(?<word>[A-Za-z_][A-Za-z0-9_]*))",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            foreach (var expression in scan.Expressions)
            {
                foreach (Match match in pattern.Matches(expression))
                {
                    if (match.Groups["num"].Success)
                    {
                        numbers?.Add(double.Parse(match.Groups["num"].Value, CultureInfo.InvariantCulture));
                    }
                    else if (match.Groups["quoted"].Success)
                    {
                        strings?.Add(match.Groups["quoted"].Value);
                    }
                    else if (match.Groups["word"].Success)
                    {
                        var word = match.Groups["word"].Value;

                        // Bare words other than the boolean literals are enum tokens
                        if (!word.Equals("true", StringComparison.OrdinalIgnoreCase)
                            && !word.Equals("false", StringComparison.OrdinalIgnoreCase))
                        {
                            strings?.Add(word);
                        }
                    }
                }
            }
        }

        private const int MaxEnumeratedRange = 24;

        private static List<object>? ComputeKnownValues(KVObject variable, string className, SmartPropParameterType type, object defaultValue, DocumentValueScan scan, string name)
        {
            if (VariableEnumMembers.TryGetValue(className, out var members))
            {
                var list = new List<object>(members);

                if (defaultValue is string current && current.Length > 0
                    && !members.Contains(current, StringComparer.OrdinalIgnoreCase))
                {
                    list.Add(current);
                }

                return list;
            }

            switch (type)
            {
                case SmartPropParameterType.Int:
                    {
                        var values = new SortedSet<double>();

                        if (scan.NumericLiterals.TryGetValue(name, out var literals))
                        {
                            foreach (var literal in literals)
                            {
                                values.Add(literal);
                            }
                        }

                        MineExpressionComparisons(scan, name, values, null);

                        // A small authored range enumerates fully; the schema defaults are 0 and 1
                        var hasMin = variable.TryGetValue("m_nParamaterMinValue", out _);
                        var hasMax = variable.TryGetValue("m_nParamaterMaxValue", out _);

                        if (hasMin || hasMax)
                        {
                            var min = hasMin ? variable.GetInt32Property("m_nParamaterMinValue") : 0;
                            var max = hasMax ? variable.GetInt32Property("m_nParamaterMaxValue") : 1;

                            if (max >= min && max - min <= MaxEnumeratedRange)
                            {
                                for (var i = min; i <= max; i++)
                                {
                                    values.Add(i);
                                }
                            }
                        }

                        if (defaultValue is double defaultNumber)
                        {
                            values.Add(defaultNumber);
                        }

                        return values.Count >= 2 ? [.. values.Cast<object>()] : null;
                    }

                case SmartPropParameterType.String:
                    {
                        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                        if (scan.StringLiterals.TryGetValue(name, out var literals))
                        {
                            values.UnionWith(literals);
                        }

                        MineExpressionComparisons(scan, name, null, values);

                        if (defaultValue is string text && text.Length > 0)
                        {
                            values.Add(text);
                        }

                        return values.Count >= 2 ? [.. values.Order(StringComparer.OrdinalIgnoreCase).Cast<object>()] : null;
                    }

                default:
                    return null;
            }
        }

        /// <summary>
        /// Builds the exposed-parameter list of a document: types, defaults, ranges, value domains
        /// and display names (synthesized from the variable name when not authored).
        /// </summary>
        internal static List<SmartPropParameter> BuildParameters(KVObject root, DocumentValueScan scan, SmartPropEvaluationContext ctx)
        {
            var parameters = new List<SmartPropParameter>();
            var variables = root.GetArray("m_Variables");

            if (variables == null)
            {
                return parameters;
            }

            foreach (var variable in variables)
            {
                var name = variable.GetStringProperty("m_VariableName");

                if (string.IsNullOrEmpty(name) || !variable.GetBooleanProperty("m_bExposeAsParameter"))
                {
                    continue;
                }

                var className = variable.GetStringProperty("_class", string.Empty);
                var type = GetParameterType(className);
                var defaultValue = GetVariableDefault(variable, type, ctx);

                // Hammer 5 Tools files use m_ParameterName where Valve's editor writes m_DisplayName
                var displayName = variable.GetStringProperty("m_DisplayName");

                if (string.IsNullOrEmpty(displayName))
                {
                    displayName = variable.GetStringProperty("m_ParameterName");
                }

                if (string.IsNullOrEmpty(displayName))
                {
                    displayName = HumanizeName(name);
                }

                double? min = null;
                double? max = null;

                if (variable.TryGetValue("m_flParamaterMinValue", out _))
                {
                    min = variable.GetDoubleProperty("m_flParamaterMinValue");
                }
                else if (variable.TryGetValue("m_nParamaterMinValue", out _))
                {
                    min = variable.GetDoubleProperty("m_nParamaterMinValue");
                }

                if (variable.TryGetValue("m_flParamaterMaxValue", out _))
                {
                    max = variable.GetDoubleProperty("m_flParamaterMaxValue");
                }
                else if (variable.TryGetValue("m_nParamaterMaxValue", out _))
                {
                    max = variable.GetDoubleProperty("m_nParamaterMaxValue");
                }

                var modelName = variable.GetStringProperty("m_sModelName");

                if (string.IsNullOrEmpty(modelName) || modelName == "None")
                {
                    modelName = null;
                }

                parameters.Add(new SmartPropParameter
                {
                    Name = name,
                    DisplayName = displayName,
                    Type = type,
                    DefaultValue = defaultValue,
                    MinValue = min,
                    MaxValue = max,
                    ModelName = modelName,
                    HideExpression = variable.GetStringProperty("m_HideExpression"),
                    ReadOnlyExpression = variable.GetStringProperty("m_ReadOnlyExpression"),
                    KnownValues = ComputeKnownValues(variable, className, type, defaultValue, scan, name),
                });
            }

            // Identical labels are indistinguishable in UI; disambiguate with the variable name
            var labelCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var parameter in parameters)
            {
                labelCounts[parameter.DisplayName] = labelCounts.GetValueOrDefault(parameter.DisplayName) + 1;
            }

            for (var i = 0; i < parameters.Count; i++)
            {
                if (labelCounts[parameters[i].DisplayName] > 1)
                {
                    parameters[i] = parameters[i] with { DisplayName = $"{parameters[i].DisplayName} ({parameters[i].Name})" };
                }
            }

            return parameters;
        }

        /// <summary>
        /// Turns an internal variable name like <c>bRandomRotation</c> or <c>start_cap</c> into a
        /// readable label. Used only when no display name was authored.
        /// </summary>
        internal static string HumanizeName(string name)
        {
            var text = Regex.Replace(name, "^(?:fl|b|f|n|m|v|sz)(?=[A-Z])", string.Empty);
            text = text.Replace('_', ' ');
            text = Regex.Replace(text, "(?<=[a-z0-9])(?=[A-Z])", " ");

            var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 0)
            {
                return name;
            }

            for (var i = 0; i < parts.Length; i++)
            {
                parts[i] = char.ToUpperInvariant(parts[i][0]) + parts[i][1..];
            }

            return string.Join(' ', parts);
        }

        /// <summary>
        /// Builds the choice list of a document.
        /// </summary>
        internal static List<SmartPropChoice> BuildChoices(KVObject root, SmartPropEvaluationContext ctx)
        {
            var result = new List<SmartPropChoice>();
            var choices = root.GetArray("m_Choices");

            if (choices == null)
            {
                return result;
            }

            foreach (var choice in choices)
            {
                var optionList = new List<SmartPropChoiceOption>();
                var options = choice.GetArray("m_Options");

                if (options != null)
                {
                    foreach (var option in options)
                    {
                        var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                        var variableValues = option.GetArray("m_VariableValues");

                        if (variableValues != null)
                        {
                            foreach (var variableValue in variableValues)
                            {
                                var target = variableValue.GetStringProperty("m_TargetName");

                                if (!string.IsNullOrEmpty(target))
                                {
                                    values[target] = SmartPropValue.ConvertDataTypedValue(variableValue, ctx);
                                }
                            }
                        }

                        optionList.Add(new SmartPropChoiceOption
                        {
                            Name = option.GetStringProperty("m_Name", string.Empty),
                            VariableValues = values,
                        });
                    }
                }

                result.Add(new SmartPropChoice
                {
                    Name = choice.GetStringProperty("m_Name", string.Empty),
                    DefaultOption = choice.GetStringProperty("m_DefaultOption"),
                    Options = optionList,
                });
            }

            return result;
        }
    }
}
