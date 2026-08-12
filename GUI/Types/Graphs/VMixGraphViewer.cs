using GUI.Types.GLViewers;
using GUI.Types.Graphs.Core;
using GUI.Utils;
using ValveKeyValue;
using ValveResourceFormat.Graphs;
using ValveResourceFormat.Renderer;

namespace GUI.Types.Graphs;

/// <summary>
/// Graph viewer for mix graph files (.vmix). Each graph in the file is its own island, so the
/// island controls step between them.
/// </summary>
internal class VMixGraphViewer : GLGraphViewer
{
    private readonly VMixGraphBuilder builder;

    public VMixGraphViewer(VrfGuiContext vrfGuiContext, RendererContext rendererContext, KVObject data)
        : base(vrfGuiContext, rendererContext, new GraphView())
    {
        builder = new VMixGraphBuilder(data)
        {
            ProgressReporter = new Progress<string>(message => Log.Debug(nameof(VMixGraphViewer), message)),
        };

        BuildGraph();
    }

    private void BuildGraph() => builder.Build(View.Document);

    protected override bool HasInstrumentationToggle => true;

    protected override void SetDrawInstrumentation(bool draw)
    {
        if (builder.DrawInstrumentation == draw)
        {
            return;
        }

        builder.DrawInstrumentation = draw;
        View.Rebuild(BuildGraph);
        RefreshStatsLabel();
        RefitToGraph();
    }

    protected override string BuildStatsText(int islandCount)
    {
        var graphs = builder.GraphCount == 1 ? "graph" : "graphs";
        return $"{builder.GraphCount} {graphs}\n{base.BuildStatsText(islandCount)}";
    }
}
