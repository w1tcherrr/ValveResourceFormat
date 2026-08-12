using System.Linq;
using ValveKeyValue;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.Graphs;

/// <summary>
/// Builds the operator graph of a sound stack file (.vsndstck). Every stack in the file becomes
/// its own island, one card per operator, wired by the <c>@operator.field</c> references the
/// operators address each other with.
/// </summary>
public sealed class SoundStackGraphBuilder
{
    /// <summary>Key the operators of a stack are listed under in the modern schema.</summary>
    private const string OperatorsKey = "operators";

    /// <summary>Keys that describe a stack or operator rather than naming another operator.</summary>
    private static readonly HashSet<string> MetadataKeys = new(StringComparer.Ordinal)
    {
        "_system_properties", "operator_variables", "name", "operator",
    };

    private readonly KVObject root;

    /// <summary>Receives diagnostics about operators that could not be read cleanly.</summary>
    public IProgress<string>? ProgressReporter { get; set; }

    /// <summary>How many stacks the last build produced.</summary>
    public int StackCount { get; private set; }

    /// <summary>Creates a builder over one sound stack file.</summary>
    /// <param name="data">The file's root object, one entry per stack.</param>
    public SoundStackGraphBuilder(KVObject data)
    {
        root = data;
    }

    /// <summary>
    /// Creates a builder over a sound stack file of the older schema, which the block stores one
    /// parsed stack at a time rather than as a single object.
    /// </summary>
    /// <param name="script">The block holding the parsed stacks.</param>
    /// <param name="progressReporter">Receives diagnostics about operators that could not be read.</param>
    public static SoundStackGraphBuilder FromLegacy(SoundStackScript script, IProgress<string>? progressReporter = null)
    {
        ArgumentNullException.ThrowIfNull(script);

        var root = new KVObject();

        foreach (var (name, stack) in script.SoundStacks)
        {
            root.Add(name, stack);
        }

        return new SoundStackGraphBuilder(root) { ProgressReporter = progressReporter };
    }

    /// <summary>Fills <paramref name="document"/> with every stack in the file and lays it out.</summary>
    /// <param name="document">The graph to fill.</param>
    public void Build(GraphDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        StackCount = 0;

        foreach (var (stackName, stack) in root)
        {
            if (stack is not KVObject stackObject || stackName.StartsWith('_'))
            {
                continue;
            }

            if (BuildStack(document, stackName, stackObject))
            {
                StackCount++;
            }
        }

        if (document.NodeCount == 0)
        {
            document.AddNode(new KVGraphNode(null) { Title = "Empty sound stack file", Subtitle = "vsndstck" });
        }

        document.LayoutNodesPacked();
        document.Legend.AddRange(SoundGraphHues.StackLegend());
    }

    private bool BuildStack(GraphDocument document, string stackName, KVObject stack)
    {
        var operators = EnumerateOperators(stack).ToArray();

        if (operators.Length == 0)
        {
            return false;
        }

        var nodes = new Dictionary<string, KVGraphNode>(StringComparer.Ordinal);

        foreach (var (name, definition) in operators)
        {
            var operatorType = definition.GetStringProperty("operator") ?? "unknown";

            var node = new KVGraphNode(definition)
            {
                Title = name,
                Subtitle = operatorType,
                Category = SoundGraphHues.HueOfOperator(operatorType),
                GroupPath = stackName,
            };

            AddOperatorRows(node, definition, operatorType);
            document.AddNode(node);
            nodes[name] = node;
        }

        foreach (var (name, definition) in operators)
        {
            var consumer = nodes[name];

            foreach (var (key, value) in definition)
            {
                if (MetadataKeys.Contains(key) || value.ValueType != KVValueType.String)
                {
                    continue;
                }

                if (!TryParseReference(value.ToString(), out var sourceName, out var sourceField))
                {
                    continue;
                }

                if (!nodes.TryGetValue(sourceName, out var source))
                {
                    ProgressReporter?.Report($"Stack \"{stackName}\" operator \"{name}\" reads \"{sourceName}\" which it does not declare.");
                    continue;
                }

                var hue = SoundGraphHues.HueOfOperator(source.Subtitle ?? string.Empty);
                var output = source.GetOrAddOutput(sourceField, hue);
                var input = consumer.GetOrAddInput(key, hue);

                if (!input.Wires.Exists(wire => wire.From == output))
                {
                    document.Connect(output, input);
                }
            }
        }

        return true;
    }

    /// <summary>
    /// The operators of a stack, whichever schema it uses. The modern one lists them in an
    /// <c>operators</c> array with a <c>name</c> each; the legacy one keys them by name directly
    /// on the stack.
    /// </summary>
    private static IEnumerable<(string Name, KVObject Definition)> EnumerateOperators(KVObject stack)
    {
        if (stack.ContainsKey(OperatorsKey))
        {
            foreach (var definition in stack.GetArray(OperatorsKey))
            {
                var name = definition.GetStringProperty("name");

                if (!string.IsNullOrEmpty(name))
                {
                    yield return (name, definition);
                }
            }

            yield break;
        }

        foreach (var (name, value) in stack)
        {
            if (value is KVObject definition && !name.StartsWith('_'))
            {
                yield return (name, definition);
            }
        }
    }

    private static void AddOperatorRows(KVGraphNode node, KVObject definition, string operatorType)
    {
        if (definition.GetStringProperty("import_stack") is { Length: > 0 } imported)
        {
            node.AddAnnotation($"imports {imported}", SoundGraphHues.ImportHue);
        }

        if (definition.GetStringProperty("_voicegraph") is { Length: > 0 } voicegraph)
        {
            node.AddAnnotation($"vmix graph: {voicegraph}", SoundGraphHues.MixGraphHue);
        }

        if (definition.GetSubCollection("operator_variables") is { } variables)
        {
            foreach (var (name, variable) in variables)
            {
                if (variable is not KVObject entry)
                {
                    continue;
                }

                var type = entry.GetStringProperty("data_type") ?? "?";
                var value = entry.GetStringProperty("value");
                node.AddText(value == null ? $"{name} : {type}" : $"{name} : {type} = {Shorten(value)}");
            }
        }

        var rows = 0;

        foreach (var (key, value) in definition)
        {
            if (MetadataKeys.Contains(key) || key is "import_stack" or "_voicegraph"
                || value.ValueType != KVValueType.String)
            {
                continue;
            }

            var text = value.ToString();

            if (TryParseReference(text, out _, out _) || rows++ >= 8)
            {
                continue;
            }

            node.AddText($"{key} = {Shorten(text)}");
        }

        if (operatorType.Length > 0 && node.Rows.Count == 0)
        {
            node.AddSpace();
        }
    }

    /// <summary>
    /// Splits an <c>@operator.field</c> reference. Anything else, including a plain constant, is
    /// not a reference and is left to be shown as a value.
    /// </summary>
    private static bool TryParseReference(string? text, out string source, out string field)
    {
        source = string.Empty;
        field = string.Empty;

        if (text == null || text.Length < 3 || text[0] != '@')
        {
            return false;
        }

        var dot = text.LastIndexOf('.');

        if (dot <= 1 || dot == text.Length - 1)
        {
            return false;
        }

        source = text[1..dot];
        field = text[(dot + 1)..];
        return true;
    }

    private static string Shorten(string value)
        => value.Length > 40 ? string.Concat(value.AsSpan(0, 39), "…") : value;
}
