using GUI.Types.GLViewers;
using GUI.Types.Graphs.Core;
using GUI.Utils;
using ValveKeyValue;
using ValveResourceFormat.Graphs;
using ValveResourceFormat.Renderer;
using ValveResourceFormat.ResourceTypes;

namespace GUI.Types.Graphs;

/// <summary>
/// Graph viewer for sound stack files (.vsndstck). Each stack in the file is its own island, so
/// the island controls step between them.
/// </summary>
internal class SoundStackGraphViewer : GLGraphViewer
{
    private readonly SoundStackGraphBuilder builder;

    public SoundStackGraphViewer(VrfGuiContext vrfGuiContext, RendererContext rendererContext, KVObject data)
        : this(vrfGuiContext, rendererContext, reporter => new SoundStackGraphBuilder(data) { ProgressReporter = reporter })
    {
    }

    /// <summary>Opens a sound stack file of the older schema, whose stacks are KeyValues1 text.</summary>
    public SoundStackGraphViewer(VrfGuiContext vrfGuiContext, RendererContext rendererContext, SoundStackScript script)
        : this(vrfGuiContext, rendererContext, reporter => SoundStackGraphBuilder.FromLegacy(script, reporter))
    {
    }

    private SoundStackGraphViewer(VrfGuiContext vrfGuiContext, RendererContext rendererContext, Func<IProgress<string>, SoundStackGraphBuilder> create)
        : base(vrfGuiContext, rendererContext, new GraphView())
    {
        builder = create(new Progress<string>(message => Log.Debug(nameof(SoundStackGraphViewer), message)));
        builder.Build(View.Document);
    }

    protected override string BuildStatsText(int islandCount)
    {
        var stacks = builder.StackCount == 1 ? "stack" : "stacks";
        return $"{builder.StackCount} {stacks}\n{base.BuildStatsText(islandCount)}";
    }
}
