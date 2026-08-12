using System.Linq;
using ValveKeyValue;
using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.Graphs;

/// <summary>
/// Builds the signal-flow graph of a mix graph file (.vmix). Every graph in the file becomes its
/// own island: a card per submix, wired by the mix commands that move audio between them, with
/// each DSP processor drawn on the submix it runs in.
/// </summary>
/// <remarks>
/// The file stores a compiled instruction list rather than a graph, so the wires are recovered by
/// walking <c>m_MixCommands</c> and reading the submix indices each command names.
/// </remarks>
public sealed class VMixGraphBuilder
{
    /// <summary>Commands that only report levels, and so carry no signal of their own.</summary>
    private static readonly HashSet<string> Instrumentation = new(StringComparer.Ordinal)
    {
        "CMD_SUBMIX_DEBUG", "CMD_SUBMIX_METER", "CMD_SUBMIX_METER_SPECTRUM",
    };

    private readonly KVObject root;

    /// <summary>Receives diagnostics about commands that could not be resolved.</summary>
    public IProgress<string>? ProgressReporter { get; set; }

    /// <summary>
    /// Whether the metering and debug commands are drawn. They outnumber the signal path in every
    /// shipped graph, so they stay off unless asked for.
    /// </summary>
    public bool DrawInstrumentation { get; set; }

    /// <summary>How many graphs the last build produced.</summary>
    public int GraphCount { get; private set; }

    /// <summary>Creates a builder over one mix graph file.</summary>
    /// <param name="data">The file's root object.</param>
    public VMixGraphBuilder(KVObject data)
    {
        root = data;
    }

    /// <summary>Fills <paramref name="document"/> with every graph in the file and lays it out.</summary>
    /// <param name="document">The graph to fill.</param>
    public void Build(GraphDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        GraphCount = 0;

        if (root.ContainsKey("Graphs"))
        {
            foreach (var graph in root.GetArray("Graphs"))
            {
                if (BuildGraph(document, graph))
                {
                    GraphCount++;
                }
            }
        }

        if (document.NodeCount == 0)
        {
            document.AddNode(new KVGraphNode(null) { Title = "Empty mix graph file", Subtitle = "vmix" });
        }

        document.LayoutNodesPacked();
        document.Legend.AddRange(SoundGraphHues.MixLegend());
    }

    private bool BuildGraph(GraphDocument document, KVObject graph)
    {
        var graphName = graph.GetStringProperty("Name") ?? "unnamed";
        var submixes = graph.ContainsKey("m_Submixes") ? graph.GetArray("m_Submixes") : [];

        if (submixes.Count == 0)
        {
            return false;
        }

        var processors = graph.ContainsKey("m_Processors") ? graph.GetArray("m_Processors") : [];
        var isMain = graph.GetBooleanProperty("m_bIsMainGraph");

        var cards = new KVGraphNode[submixes.Count];

        for (var i = 0; i < submixes.Count; i++)
        {
            var submix = submixes[i];
            var send = submix.GetStringProperty("sendOperator");

            var card = new KVGraphNode(submix)
            {
                Title = submix.GetStringProperty("name") ?? $"submix {i}",
                Subtitle = string.IsNullOrEmpty(send) || send == "SendNone" ? "submix" : send,
                Category = SoundGraphHues.SubmixHue,
                GroupPath = graphName,
            };

            var channels = submix.GetInt32Property("channels");
            card.AddText(channels < 0 ? "channels: inherit" : $"channels: {channels}");

            if (submix.GetStringProperty("send0") is { Length: > 0 } send0)
            {
                card.AddAnnotation($"send: {send0}", SoundGraphHues.SignalHue);
            }

            document.AddNode(card);
            cards[i] = card;
        }

        var output = new KVGraphNode(graph)
        {
            Title = isMain ? $"{graphName} (main)" : graphName,
            Subtitle = "graph output",
            Category = GraphHue.Green,
            GroupPath = graphName,
        };

        AddGraphInputRows(output, graph);
        document.AddNode(output);

        WireCommands(document, graph, graphName, cards, processors, output);
        return true;
    }

    private void WireCommands(
        GraphDocument document,
        KVObject graph,
        string graphName,
        KVGraphNode[] cards,
        IReadOnlyList<KVObject> processors,
        KVGraphNode output)
    {
        if (!graph.ContainsKey("m_MixCommands"))
        {
            return;
        }

        foreach (var command in graph.GetArray("m_MixCommands"))
        {
            var name = command.GetStringProperty("command") ?? string.Empty;

            if (!DrawInstrumentation && Instrumentation.Contains(name))
            {
                continue;
            }

            var target = Card(cards, command.GetInt32Property("outputSubmix"));
            var processor = Processor(processors, command.GetInt32Property("processor"));

            if (processor != null)
            {
                DescribeProcessor(target ?? output, processor, name);
            }

            foreach (var slot in (string[])["inputSubmix0", "inputSubmix1"])
            {
                var source = Card(cards, command.GetInt32Property(slot));

                if (source == null)
                {
                    continue;
                }

                if (Instrumentation.Contains(name))
                {
                    source.AddAnnotation(Label(name), SoundGraphHues.ControlHue);
                    continue;
                }

                Connect(document, source, target ?? output, Label(name), graphName);
            }
        }
    }

    private void Connect(GraphDocument document, KVGraphNode source, KVGraphNode target, string label, string graphName)
    {
        if (source == target)
        {
            return;
        }

        var from = source.GetOrAddOutput("out", SoundGraphHues.SignalHue);
        var to = target.GetOrAddInput(label, SoundGraphHues.SignalHue);

        if (to.Wires.Exists(wire => wire.From == from))
        {
            return;
        }

        try
        {
            document.Connect(from, to);
        }
        catch (InvalidOperationException)
        {
            ProgressReporter?.Report($"Graph \"{graphName}\" repeats the {label} path from \"{source.Title}\" to \"{target.Title}\".");
        }
    }

    private static void DescribeProcessor(KVGraphNode card, KVObject processor, string command)
    {
        var className = processor.GetStringProperty("_class") ?? "processor";
        const string Prefix = "CVMix";
        const string Suffix = "ProcessorDesc";

        if (className.StartsWith(Prefix, StringComparison.Ordinal) && className.EndsWith(Suffix, StringComparison.Ordinal))
        {
            className = className[Prefix.Length..^Suffix.Length];
        }

        var text = command == "CMD_SUBMIX_GENERATE_SIDECHAIN" ? $"{className} (sidechain)" : className;
        var existing = processor.GetStringProperty("m_name");

        card.AddAnnotation(string.IsNullOrEmpty(existing) ? text : $"{text}: {existing}", SoundGraphHues.ProcessorHue);
    }

    private static void AddGraphInputRows(KVGraphNode card, KVObject graph)
    {
        var channels = graph.GetInt32Property("m_nGraphOutputChannels");
        card.AddText(channels < 0 ? "channels: inherit" : $"channels: {channels}");

        if (graph.ContainsKey("m_controlInputs"))
        {
            var inputs = graph.GetArray("m_controlInputs");
            var shown = 0;

            foreach (var input in inputs)
            {
                if (shown++ >= 10)
                {
                    card.AddText($"… {inputs.Count - 10} more control inputs");
                    break;
                }

                card.AddText($"{input.GetStringProperty("m_name")} = {input.GetFloatProperty("m_flDefaultValue")}");
            }
        }

        if (graph.ContainsKey("m_vsndInputs"))
        {
            foreach (var input in graph.GetArray("m_vsndInputs"))
            {
                if (input.GetStringProperty("m_defaultValue") is { Length: > 0 } path)
                {
                    card.AddResourceReference(path, "sound", SoundGraphHues.SoundFileHue);
                }
            }
        }
    }

    private static KVGraphNode? Card(KVGraphNode[] cards, int index)
        => index >= 0 && index < cards.Length ? cards[index] : null;

    private static KVObject? Processor(IReadOnlyList<KVObject> processors, int index)
        => index >= 0 && index < processors.Count ? processors[index] : null;

    /// <summary>Short label for what a command does to the signal, used as the socket name.</summary>
    private static string Label(string command) => command switch
    {
        "CMD_SUBMIX_COPY" => "copy",
        "CMD_SUBMIX_ACCUMULATE" => "mix",
        "CMD_SUBMIX_PROCESS" => "process",
        "CMD_SUBMIX_OUTPUT" or "CMD_SUBMIX_OUTPUTx2" => "output",
        "CMD_SUBMIX_GENERATE" => "generate",
        "CMD_SUBMIX_GENERATE_SIDECHAIN" => "sidechain",
        "CMD_SUBMIX_METER" => "metered",
        "CMD_SUBMIX_METER_SPECTRUM" => "spectrum",
        "CMD_SUBMIX_DEBUG" => "debug",
        _ => command.StartsWith("CMD_", StringComparison.Ordinal) ? command[4..].ToLowerInvariant() : command,
    };
}
