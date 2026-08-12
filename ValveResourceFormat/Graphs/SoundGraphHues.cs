namespace ValveResourceFormat.Graphs;

/// <summary>
/// The colour scheme the two audio graphs share: sound stack operators are coloured by the family
/// their name prefixes them into, and mix graph cards by what the command does to the signal.
/// </summary>
public static class SoundGraphHues
{
    /// <summary>Hue of a reference from one stack to another.</summary>
    public const GraphHue ImportHue = GraphHue.Pink;

    /// <summary>Hue of a reference from a stack into a mix graph.</summary>
    public const GraphHue MixGraphHue = GraphHue.Magenta;

    /// <summary>Hue of a submix card in a mix graph.</summary>
    public const GraphHue SubmixHue = GraphHue.Blue;

    /// <summary>Hue of a signal path between submixes.</summary>
    public const GraphHue SignalHue = GraphHue.Emerald;

    /// <summary>Hue of a control value feeding a processor.</summary>
    public const GraphHue ControlHue = GraphHue.Olive;

    /// <summary>Hue of a DSP processor row.</summary>
    public const GraphHue ProcessorHue = GraphHue.Purple;

    /// <summary>Hue of the impulse response and .vsnd files a processor reads.</summary>
    public const GraphHue SoundFileHue = GraphHue.Amber;

    /// <summary>
    /// Colour slot for a sound stack operator, taken from the family its name starts with. The
    /// vocabulary is prefixed by design (<c>math_</c>, <c>opvar_</c>, <c>soundevent_</c>, ...), so
    /// the prefix is what makes one card readable against its neighbours.
    /// </summary>
    /// <param name="operatorType">The operator's type name.</param>
    public static GraphHue HueOfOperator(string operatorType)
    {
        ArgumentNullException.ThrowIfNull(operatorType);

        var underscore = operatorType.IndexOf('_', StringComparison.Ordinal);
        var family = underscore > 0 ? operatorType[..underscore] : operatorType;

        return family switch
        {
            "math" => GraphHue.Olive,
            "util" => GraphHue.Slate,
            "soundevent" => GraphHue.Green,
            "opvar" => GraphHue.Cyan,
            "sos" => GraphHue.Amber,
            "vmix" => GraphHue.Magenta,
            "ctrl" or "logic" => GraphHue.Blue,
            "calc" => GraphHue.Teal,
            "game" or "citadel" => GraphHue.Indigo,
            "container" or "track" or "arrangement" or "sampler" => GraphHue.Purple,
            "soundmixer" => GraphHue.Orange,
            "mod" or "harmonic" => GraphHue.Maroon,
            "vector" or "obb" or "sound" => GraphHue.Emerald,
            "convar" => GraphHue.Red,
            _ => GraphHue.Neutral,
        };
    }

    /// <summary>The legend a sound stack graph advertises.</summary>
    public static IEnumerable<GraphLegendEntry> StackLegend()
    {
        yield return new("Math", HueOfOperator("math_"));
        yield return new("Utility", HueOfOperator("util_"));
        yield return new("Sound event", HueOfOperator("soundevent_"));
        yield return new("Operator variable", HueOfOperator("opvar_"));
        yield return new("Stack control", HueOfOperator("sos_"));
        yield return new("Mixer send", HueOfOperator("vmix_"));
        yield return new("Switch / logic", HueOfOperator("ctrl_"));
        yield return new("Spatial calc", HueOfOperator("calc_"));
        yield return new("Game state", HueOfOperator("game_"));
        yield return new("Container / track", HueOfOperator("container_"));
        yield return new("Mix layer", HueOfOperator("soundmixer_"));
        yield return new("Modulation", HueOfOperator("mod_"));
        yield return new("Imported stack", ImportHue, GraphLegendKind.Marker);
        yield return new("Mix graph reference", MixGraphHue, GraphLegendKind.Marker);
    }

    /// <summary>The legend a mix graph advertises.</summary>
    public static IEnumerable<GraphLegendEntry> MixLegend()
    {
        yield return new("Signal path", SignalHue, GraphLegendKind.Wire);
        yield return new("Control value", ControlHue, GraphLegendKind.DashedWire);
        yield return new("Submix", SubmixHue);
        yield return new("Graph output", GraphHue.Green);
        yield return new("Processor", ProcessorHue, GraphLegendKind.Marker);
        yield return new("Impulse response / vsnd", SoundFileHue, GraphLegendKind.Marker);
    }
}
