#if DEBUG
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using GUI.Controls;
using GUI.Types.Exporter;
using GUI.Types.Viewers;

namespace GUI.Automation;

/// <summary>
/// Tools that drive the sound event viewer of a vsndevts tab, and the list of events that play a vsnd. They find the
/// viewer's controls by their accessible names and change them as a user would.
/// </summary>
internal sealed partial class McpTools
{
    [Tool("Filter the event list of a sound event tab as a user would, and return the matching events in list order. The details pane does not change. 'search' matches the event name or type. Returns 'next_offset' when more events match.", Redraws = true)]
    private async Task<object> ListSoundEvents(
        TabPage tab,
        [Description("Text to type into the search box, or an empty string to clear it.")] string? search = null,
        [Description("List the events of every file of the game instead of only this file.")] bool? allFiles = null,
        [Description("Stack type to show, or an empty string for all types.")] string? type = null,
        [Description("Status filter: the text of a choice of the status dropdown, such as 'Missing sounds' or 'All events'.")] string? status = null,
        [Description("Index of the first event to return.")][Range(0, int.MaxValue)] int offset = 0,
        [Description("How many events to return.")][Range(1, 1000)] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        await WaitForSoundEvents(tab, cancellationToken).ConfigureAwait(false);

        return await OnUi(() =>
        {
            var viewer = SoundEventViewerOf(tab);

            if (allFiles != null)
            {
                Named<ComboBox>(viewer, "Scope").SelectedIndex = allFiles.Value ? 1 : 0;
            }

            if (type != null)
            {
                var typeBox = Named<ComboBox>(viewer, "Type");
                var item = type.Length == 0 ? typeBox.Items[0] : typeBox.Items.Cast<object>().Skip(1).FirstOrDefault(item => item.ToString()!.StartsWith(type + " (", StringComparison.OrdinalIgnoreCase));
                typeBox.SelectedItem = item ?? throw new ToolException($"No events of type '{type}' are listed. The types are: {string.Join(", ", typeBox.Items.Cast<object>().Skip(1))}.");
            }

            if (status != null)
            {
                var statusBox = Named<ComboBox>(viewer, "Status");
                var item = statusBox.Items.Cast<object>().FirstOrDefault(item => item.ToString()!.Equals(status, StringComparison.OrdinalIgnoreCase));
                statusBox.SelectedItem = item ?? throw new ToolException($"No status '{status}'. The choices are: {string.Join(", ", statusBox.Items.Cast<object>())}.");
            }

            if (search != null)
            {
                Named<TextBox>(viewer, "Search").Text = search;
            }

            var grid = Named<DataGridView>(viewer, "Events");
            var events = new List<object>();

            for (var i = offset; i < grid.RowCount && events.Count < limit; i++)
            {
                var cells = grid.Rows[i].Cells;
                events.Add(new
                {
                    Name = cells[0].Value,
                    Type = cells[1].Value,
                    Duration = cells[2].Value,
                    File = cells[3].Value,
                });
            }

            return new
            {
                Count = Named<Label>(viewer, "Count").Text,
                Events = events,
                NextOffset = offset + events.Count < grid.RowCount ? offset + events.Count : (int?)null,
            };
        }, cancellationToken).ConfigureAwait(false);
    }

    [Tool("Show a sound event in the details pane of a sound event tab, as following a link to it does, or press one of its buttons. Then describe what the pane shows: the event, its notices and caption, its properties by group with where each value comes from, its sounds, the events it references and the events that use it. With no arguments, only describe it. 'open_file' opens the defining file in a new tab and returns that tab.", Redraws = true)]
    private async Task<object> ShowSoundEvent(
        TabPage tab,
        [Description("Name of the event to show.")] string? name = null,
        [Description("The file whose definition to show, when several files define the event.")] string? file = null,
        [Description("A button to press after showing the event: play_event, stop, back, forward, copy_name or open_file. play_sound plays the sound at 'sound' in the sounds list.")] string? action = null,
        [Description("Index in the sounds list for play_sound.")][Range(0, int.MaxValue)] int sound = 0,
        [Description("Leave out properties that only show a stack default.")] bool hideDefaults = false,
        CancellationToken cancellationToken = default)
    {
        await WaitForSoundEvents(tab, cancellationToken).ConfigureAwait(false);

        if (action == "open_file")
        {
            return await OpenTab(_ => OnUi(() => PressSoundEventButton(tab, name, file, "Open file"), cancellationToken), $"the file of '{name}'", cancellationToken).ConfigureAwait(false);
        }

        return await OnUi(() =>
        {
            var viewer = SoundEventViewerOf(tab);

            if (name != null && !viewer.ShowEvent(name, file))
            {
                throw new ToolException($"No event '{name}'{(file == null ? string.Empty : $" in '{file}'")} is indexed.");
            }

            Named<CheckBox>(viewer, "Show stack defaults").Checked = !hideDefaults;

            switch (action)
            {
                case null:
                    break;
                case "play_sound":
                    var sounds = Named<DataGridView>(viewer, "Sounds");

                    if (sound >= sounds.RowCount)
                    {
                        throw new ToolException($"The event has {sounds.RowCount} sounds.");
                    }

                    if (sounds.Parent is TabPage { Parent: TabControl linkTabs } soundsPage)
                    {
                        linkTabs.SelectedTab = soundsPage;
                    }

                    sounds.CurrentCell = sounds.Rows[sound].Cells[0];

                    if (!viewer.PlaySound(sound, autoPlay: true))
                    {
                        throw new ToolException($"Sound {sound} cannot be played. {sounds.Rows[sound].Cells[2].Value}");
                    }

                    break;
                default:
                    PressSoundEventButton(tab, null, null, action switch
                    {
                        "play_event" => "Play event",
                        "stop" => "Stop",
                        "back" => "Back",
                        "forward" => "Forward",
                        "copy_name" => "Copy name",
                        _ => throw new ArgumentException($"Unknown action '{action}'."),
                    });
                    break;
            }

            return DescribeSoundEvent(viewer);
        }, cancellationToken).ConfigureAwait(false);
    }

    [Tool("List the sound events that play the sound of a vsnd tab, as its SOUND tab shows them, with the file that defines each.")]
    private async Task<object> ListSoundUsers(TabPage tab, CancellationToken cancellationToken = default)
    {
        if (await OnUi(() => tab.Tag as ExportData, cancellationToken).ConfigureAwait(false) is { } data)
        {
            await data.Loaded.WaitAsync(LoadTimeout, cancellationToken).ConfigureAwait(false);
        }

        for (var waited = TimeSpan.Zero; ; waited += TimeSpan.FromMilliseconds(100))
        {
            var users = await OnUi(() =>
            {
                var grid = Named<DataGridView>(tab, "Used by events");
                return grid.RowCount == 0 ? null : GridRows(grid);
            }, cancellationToken).ConfigureAwait(false);

            if (users != null || waited > LoadTimeout)
            {
                return new { Events = users };
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    private static SoundEventViewer SoundEventViewerOf(TabPage tab)
        => SoundEventViewer.Find(tab) ?? throw new ToolException($"Tab '{tab.Text}' has no sound event viewer. Open a .vsndevts_c file.");

    /// <summary>Waits for a tab to load and for its sound event viewer to finish indexing.</summary>
    private static async Task WaitForSoundEvents(TabPage tab, CancellationToken cancellationToken)
    {
        if (await OnUi(() => tab.Tag as ExportData, cancellationToken).ConfigureAwait(false) is { } data)
        {
            await data.Loaded.WaitAsync(LoadTimeout, cancellationToken).ConfigureAwait(false);
        }

        for (var waited = TimeSpan.Zero; waited < LoadTimeout; waited += TimeSpan.FromMilliseconds(100))
        {
            var count = await OnUi(() => Named<Label>(SoundEventViewerOf(tab), "Count").Text, cancellationToken).ConfigureAwait(false);

            if (count.StartsWith("Failed", StringComparison.Ordinal))
            {
                throw new ToolException(count);
            }

            if (count.EndsWith(" events", StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        throw new ToolException("The sound event viewer is still indexing.");
    }

    private static bool PressSoundEventButton(TabPage tab, string? name, string? file, string button)
    {
        var viewer = SoundEventViewerOf(tab);

        if (name != null && !viewer.ShowEvent(name, file))
        {
            throw new ToolException($"No event '{name}' is indexed.");
        }

        var control = Named<ThemedButton>(viewer, button);

        if (!control.Enabled)
        {
            throw new ToolException($"'{button}' is disabled, so a user cannot press it.");
        }

        control.PerformClick();
        return true;
    }

    private static object DescribeSoundEvent(SoundEventViewer viewer)
    {
        var properties = Named<DataGridView>(viewer, "Properties");
        var groups = new List<object>();
        string? group = null;
        List<object>? groupRows = null;

        foreach (DataGridViewRow row in properties.Rows)
        {
            var cells = row.Cells;

            if (row.Tag?.ToString() == "Group")
            {
                group = (string?)cells[0].Value;
                groupRows = [];
                groups.Add(new { Group = group, Properties = groupRows });
                continue;
            }

            groupRows?.Add(new
            {
                Key = cells[0].Value,
                Value = cells[1].ToolTipText.Length > 0 ? cells[1].ToolTipText : cells[1].Value,
                Kind = row.Tag?.ToString()?.ToLowerInvariant(),
                Source = cells[2].Value is string { Length: > 0 } source ? source : null,
                Curve = Flag(cells[1].Tag != null),
            });
        }

        string? Text(string name) => Named<Control>(viewer, name) is { Visible: true, Text.Length: > 0 } control ? control.Text : null;

        return new
        {
            Event = Text("Event"),
            Stack = Text("Stack"),
            File = Text("File"),
            Notice = Text("Notice"),
            Caption = Text("Caption"),
            CanPlay = Named<ThemedButton>(viewer, "Play event").Enabled,
            CanGoBack = Flag(Named<ThemedButton>(viewer, "Back").Enabled),
            CanGoForward = Flag(Named<ThemedButton>(viewer, "Forward").Enabled),
            SoundPanel = Flag(FindAudioPanel(viewer) != null),
            Properties = groups,
            Sounds = GridRows(Named<DataGridView>(viewer, "Sounds")),
            References = GridRows(Named<DataGridView>(viewer, "References")),
            UsedBy = GridRows(Named<DataGridView>(viewer, "Used by")),
        };
    }

    private static AudioPlaybackPanel? FindAudioPanel(Control control)
    {
        if (control is AudioPlaybackPanel panel)
        {
            return panel;
        }

        foreach (Control child in control.Controls)
        {
            if (FindAudioPanel(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static List<Dictionary<string, object?>> GridRows(DataGridView grid)
    {
        var rows = new List<Dictionary<string, object?>>();

        foreach (DataGridViewRow row in grid.Rows)
        {
            var cells = new Dictionary<string, object?>();

            foreach (DataGridViewCell cell in row.Cells)
            {
                if (cell.Value is string { Length: > 0 } value)
                {
                    cells[grid.Columns[cell.ColumnIndex].HeaderText.ToLowerInvariant().Replace(' ', '_')] = value;
                }
            }

            rows.Add(cells);
        }

        return rows;
    }

    /// <summary>Finds a control by its accessible name, the label that the tools use for it.</summary>
    private static T Named<T>(Control root, string name)
        where T : Control
        => FindNamed<T>(root, name) ?? throw new ToolException($"'{root.Text}' has no control named '{name}'.");

    private static T? FindNamed<T>(Control root, string name)
        where T : Control
    {
        foreach (Control child in root.Controls)
        {
            if (child is T match && child.AccessibleName == name)
            {
                return match;
            }

            if (FindNamed<T>(child, name) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
#endif
