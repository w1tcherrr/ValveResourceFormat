using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using GUI.Controls;
using GUI.Types.Audio;
using GUI.Types.Exporter;
using GUI.Utils;
using ValveKeyValue;
using ValveResourceFormat.IO;
using ValveResourceFormat.Renderer;
using ValveResourceFormat.Renderer.Audio;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;
using Captions = ValveResourceFormat.ClosedCaptions.ClosedCaptions;

namespace GUI.Types.Viewers
{
    /// <summary>
    /// Lists the sound events of a file or of the whole game, and shows the selected event with the keys it
    /// resolves to, grouped by its stack, its sounds, the events it references and the events that use it.
    /// </summary>
    internal sealed class SoundEventViewer : UserControl
    {
        private const string PreviewChannel = "sound_event_viewer";

        private sealed record EventRow(string Name, string File, KVObject Data, string Type, float Duration, bool Duplicate, bool HasProblems, bool MissingBase, bool MissingSound, bool Playable);

        private enum ValueSource
        {
            Own,
            Inherited,
            Default,
            Group,
        }

        // Read by the engine when it parses the event, outside of its stack
        private static readonly HashSet<string> EventKeys = new(["groups", "preload_vsnds", "memory_type"], StringComparer.OrdinalIgnoreCase);

        private static readonly ConditionalWeakTable<VrfGuiContext, Task<(SoundEventIndex Index, List<Captions> Captions)>> Indexes = [];

        private readonly VrfGuiContext guiContext;
        private readonly string fileName;
        private readonly IReadOnlyList<(string Name, KVObject Data)> fileEvents;

        private SoundEventIndex? index;
        private List<Captions> captions = [];
        private List<EventRow> rows = [];
        private List<EventRow> filteredRows = [];
        private readonly Dictionary<string, bool> playableTypes = new(StringComparer.OrdinalIgnoreCase);
        private EventRow? current;
        private readonly List<(string Name, string File)> backHistory = [];
        private readonly List<(string Name, string File)> forwardHistory = [];
        private int sortColumn;
        private bool sortDescending;
        private bool updatingList;

        private SoundEventPlayer? player;
        private readonly Timer playerTimer = new() { Interval = 30 };
        private readonly Camera listener = new();
        private ValveResourceFormat.Resource? soundResource;
        private AudioPlaybackPanel? audioPanel;

        private readonly Font titleFont;
        private readonly Font boldFont;
        private readonly Font italicFont;
        private readonly ToolTip toolTip = new();

#pragma warning disable CA2213 // Added to Controls, which disposes them
        private readonly TextBox searchBox;
        private readonly ThemedComboBox scopeBox;
        private readonly ThemedComboBox typeBox;
        private readonly ThemedComboBox statusBox;
        private readonly Label countLabel;
        private readonly Grid eventGrid;

        private readonly ThemedButton backButton;
        private readonly ThemedButton forwardButton;
        private readonly ThemedButton playButton;
        private readonly ThemedButton stopButton;
        private readonly ThemedButton copyButton;
        private readonly ThemedButton openButton;
        private readonly Label titleLabel;
        private readonly LinkLabel typeLink;
        private readonly LinkLabel fileLink;
        private readonly Label noticeLabel;
        private readonly Label captionLabel;
        private readonly CheckBox defaultsBox;
        private readonly Grid propertyGrid;
        private readonly ThemedTabControl linkTabs;
        private readonly TabPage soundsPage;
        private readonly TabPage referencesPage;
        private readonly TabPage usedByPage;
        private readonly Grid soundGrid;
        private readonly Grid referenceGrid;
        private readonly Grid usedByGrid;
        private readonly Panel audioHost;
#pragma warning restore CA2213

        public SoundEventViewer(VrfGuiContext guiContext, IReadOnlyList<(string Name, KVObject Data)> fileEvents)
        {
            this.guiContext = guiContext;
            this.fileEvents = fileEvents;
            fileName = guiContext.FileName;

            Dock = DockStyle.Fill;
            titleFont = new Font(Font.FontFamily, Font.Size * 1.5f, FontStyle.Bold);
            boldFont = new Font(Font, FontStyle.Bold);
            italicFont = new Font(Font, FontStyle.Italic);

            searchBox = new TextBox
            {
                Dock = DockStyle.Top,
                PlaceholderText = "Search events by name or type (Ctrl+F)",
                AccessibleName = "Search",
            };
            searchBox.TextChanged += (_, _) => ApplyFilter();
            searchBox.KeyDown += OnSearchKeyDown;

            scopeBox = CreateFilterBox("Scope", ["This file", "All files"]);
            typeBox = CreateFilterBox("Type", ["All types"]);
            statusBox = CreateFilterBox("Status", ["All events", "Duplicate names", "Rejected or skipped bases", "Missing base events", "Missing sounds", "Not playable here"]);
            scopeBox.SelectedIndexChanged += (_, _) => OnScopeChanged();

            var filters = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 3,
                Padding = new Padding(0, 4, 0, 0),
            };
            filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
            filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
            filters.Controls.Add(scopeBox, 0, 0);
            filters.Controls.Add(typeBox, 1, 0);
            filters.Controls.Add(statusBox, 2, 0);

            countLabel = new Label
            {
                Dock = DockStyle.Top,
                AutoSize = false,
                Height = this.AdjustForDPI(24),
                TextAlign = ContentAlignment.MiddleLeft,
                Text = "Indexing sound events...",
                AccessibleName = "Count",
            };

            eventGrid = new Grid("Events");
            eventGrid.VirtualMode = true;
            eventGrid.Columns.Add(CreateColumn("Name", 55));
            eventGrid.Columns.Add(CreateColumn("Type", 30));
            eventGrid.Columns.Add(CreateColumn("Duration", 15));
            eventGrid.Columns.Add(CreateColumn("File", 35));
            eventGrid.Columns[3].Visible = false;
            eventGrid.Columns[2].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;

            foreach (DataGridViewColumn column in eventGrid.Columns)
            {
                column.SortMode = DataGridViewColumnSortMode.Programmatic;
            }

            eventGrid.CellValueNeeded += OnEventCellValueNeeded;
            eventGrid.SelectionChanged += OnEventSelectionChanged;
            eventGrid.ColumnHeaderMouseClick += OnEventColumnHeaderClick;

            var leftPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4) };
            leftPanel.Controls.Add(eventGrid);
            leftPanel.Controls.Add(countLabel);
            leftPanel.Controls.Add(filters);
            leftPanel.Controls.Add(searchBox);

            backButton = CreateToolButton("NavigateBack", "Back", "Back (Alt+Left)", (_, _) => GoBack());
            forwardButton = CreateToolButton("NavigateForward", "Forward", "Forward (Alt+Right)", (_, _) => GoForward());
            playButton = CreateToolButton("AudioPlay", "Play event", "Play event (Space)", (_, _) => PlayEvent());
            stopButton = CreateToolButton("RendererStop", "Stop", "Stop (Esc)", (_, _) => StopPlayback());
            copyButton = CreateToolButton("CopyName", "Copy name", "Copy name (Ctrl+C)", (_, _) => CopyName());
            openButton = CreateToolButton("File", "Open file", "Open the file that defines this event", (_, _) => OpenDefiningFile());

            titleLabel = new Label
            {
                AutoSize = false,
                AutoEllipsis = true,
                Dock = DockStyle.Fill,
                Font = titleFont,
                TextAlign = ContentAlignment.MiddleLeft,
                UseMnemonic = false,
                AccessibleName = "Event",
            };

            var toolbar = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = this.AdjustForDPI(40),
                ColumnCount = 8,
                RowCount = 1,
            };
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            for (var i = 0; i < 5; i++)
            {
                toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            }

            toolbar.Controls.Add(backButton, 0, 0);
            toolbar.Controls.Add(forwardButton, 1, 0);
            toolbar.Controls.Add(titleLabel, 2, 0);
            toolbar.Controls.Add(playButton, 3, 0);
            toolbar.Controls.Add(stopButton, 4, 0);
            toolbar.Controls.Add(copyButton, 5, 0);
            toolbar.Controls.Add(openButton, 6, 0);

            typeLink = CreateLink("Stack");
            typeLink.LinkClicked += (_, _) => OpenStackFile();
            fileLink = CreateLink("File");
            fileLink.LinkClicked += (_, _) => OpenDefiningFile();

            defaultsBox = new CheckBox
            {
                Text = "Show stack defaults",
                Checked = true,
                AutoSize = true,
                Anchor = AnchorStyles.Right,
                AccessibleName = "Show stack defaults",
            };
            defaultsBox.CheckedChanged += (_, _) => ShowProperties();

            var subtitle = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 3,
                RowCount = 1,
            };
            subtitle.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            subtitle.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            subtitle.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            subtitle.Controls.Add(typeLink, 0, 0);
            subtitle.Controls.Add(fileLink, 1, 0);
            subtitle.Controls.Add(defaultsBox, 2, 0);

            noticeLabel = CreateInfoLabel("Notice");
            captionLabel = CreateInfoLabel("Caption");

            propertyGrid = new Grid("Properties");
            propertyGrid.Columns.Add(CreateColumn("Property", 30));
            propertyGrid.Columns.Add(CreateColumn("Value", 45));
            propertyGrid.Columns.Add(CreateColumn("Source", 25));
            propertyGrid.CellPainting += OnPropertyCellPainting;

            soundGrid = new Grid("Sounds");
            soundGrid.Columns.Add(CreateColumn("Sound", 60));
            soundGrid.Columns.Add(CreateColumn("Key", 25));
            soundGrid.Columns.Add(CreateColumn("Status", 15));
            soundGrid.SelectionChanged += (_, _) => PlaySound(soundGrid.CurrentCell?.RowIndex ?? -1, autoPlay: false);

            referenceGrid = new Grid("References");
            referenceGrid.Columns.Add(CreateColumn("Event", 60));
            referenceGrid.Columns.Add(CreateColumn("Key", 25));
            referenceGrid.Columns.Add(CreateColumn("Status", 15));

            usedByGrid = new Grid("Used by");
            usedByGrid.Columns.Add(CreateColumn("Event", 65));
            usedByGrid.Columns.Add(CreateColumn("Key", 35));

            foreach (var grid in new[] { propertyGrid, soundGrid, referenceGrid, usedByGrid })
            {
                grid.Activated += OnGridRowActivated;
            }

            eventGrid.Activated += (_, _) => PlayEvent();

            audioHost = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = this.AdjustForDPI(170),
                Visible = false,
            };

            soundsPage = new ThemedTabPage("Sounds");
            soundsPage.Controls.Add(soundGrid);
            soundsPage.Controls.Add(audioHost);
            referencesPage = new ThemedTabPage("References");
            referencesPage.Controls.Add(referenceGrid);
            usedByPage = new ThemedTabPage("Used by");
            usedByPage.Controls.Add(usedByGrid);

            linkTabs = new ThemedTabControl { Dock = DockStyle.Fill };
            linkTabs.TabPages.Add(soundsPage);
            linkTabs.TabPages.Add(referencesPage);
            linkTabs.TabPages.Add(usedByPage);

            var detailsSplit = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterWidth = this.AdjustForDPI(6),
            };
            detailsSplit.Panel1.Controls.Add(propertyGrid);
            detailsSplit.Panel2.Controls.Add(linkTabs);

            var rightPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 4, 4, 4) };
            rightPanel.Controls.Add(detailsSplit);
            rightPanel.Controls.Add(captionLabel);
            rightPanel.Controls.Add(noticeLabel);
            rightPanel.Controls.Add(subtitle);
            rightPanel.Controls.Add(toolbar);

            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                SplitterWidth = this.AdjustForDPI(6),
            };
            split.Panel1.Controls.Add(leftPanel);
            split.Panel2.Controls.Add(rightPanel);
            Controls.Add(split);

            Layout += OnFirstLayout;

            void OnFirstLayout(object? sender, LayoutEventArgs e)
            {
                if (Width <= 0)
                {
                    return;
                }

                Layout -= OnFirstLayout;
                split.SplitterDistance = Width * 2 / 5;
                detailsSplit.SplitterDistance = detailsSplit.Height * 3 / 5;
            }

            playerTimer.Tick += (_, _) => player?.Update(listener);

            ShowEmptyDetails();
            _ = LoadAsync();
        }

        /// <summary>
        /// Gets the index of the sound events and stacks that a file's package and game search paths hold, built once per
        /// package off the UI thread, with the English closed captions found there.
        /// </summary>
        public static Task<(SoundEventIndex Index, List<Captions> Captions)> GetIndexAsync(VrfGuiContext context)
        {
            var owner = context.ParentGuiContext ?? context;

            return Indexes.GetValue(owner, static owner => Task.Run(() => BuildIndex(owner)));
        }

        private static (SoundEventIndex Index, List<Captions> Captions) BuildIndex(VrfGuiContext context)
        {
            var manifestFiles = SoundEventIndex.GetManifestFiles(context.FileLoaderNoCache, "soundevents/soundevents_manifest.vrman").Select(static file => file + GameFileLoader.CompiledFileSuffix);
            List<string> eventFiles = [.. manifestFiles.Concat(context.EnumerateFiles("vsndevts_c")).Distinct(StringComparer.OrdinalIgnoreCase)];
            var stackFiles = context.EnumerateFiles("vsndstck_c").ToList();
            var index = SoundEventIndex.Build(context.FileLoaderNoCache, eventFiles, stackFiles);
            var captions = new List<Captions>();

            foreach (var file in context.EnumerateFiles("dat"))
            {
                if (!file.EndsWith("english.dat", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                using var stream = context.GetFileStream(file);

                if (stream == null)
                {
                    continue;
                }

                try
                {
                    var fileCaptions = new Captions();
                    fileCaptions.Read(file, stream);
                    captions.Add(fileCaptions);
                }
                catch (InvalidDataException)
                {
                    // Not every .dat file is a caption file
                }
            }

            return (index, captions);
        }

        /// <summary>
        /// Creates a list of the sound events that play a sound, filled once the index is built. Double-clicking an event
        /// opens the file that defines it.
        /// </summary>
        public static Control CreateSoundUsersList(VrfGuiContext context, string sound)
        {
            var grid = new Grid("Used by events")
            {
                Dock = DockStyle.Bottom,
            };
            grid.Height = grid.AdjustForDPI(150);
            grid.Columns.Add(CreateColumn("Used by event", 55));
            grid.Columns.Add(CreateColumn("File", 45));
            grid.Activated += (_, rowIndex) =>
            {
                if (grid.Rows[rowIndex].Tag is (string name, string file))
                {
                    _ = OpenEventAsync(context, file, name);
                }
            };

            _ = FillAsync();

            return grid;

            async Task FillAsync()
            {
                try
                {
                    var (index, _) = await GetIndexAsync(context).ConfigureAwait(true);

                    if (grid.IsDisposed)
                    {
                        return;
                    }

                    foreach (var name in index.GetEventsUsingSound(sound))
                    {
                        var file = index.GetDefinitions(name)[0].File;
                        var rowIndex = grid.Rows.Add(name, file);
                        grid.Rows[rowIndex].Tag = (name, file);
                    }

                    if (grid.RowCount == 0)
                    {
                        grid.Rows.Add("No sound event plays this sound", string.Empty);
                    }

                    grid.ClearSelection();
                }
                catch (Exception e)
                {
                    Log.Error(nameof(SoundEventViewer), $"Failed to index sound events for \"{context.DisplayPath}\": {e}");
                }
            }
        }

        private async Task LoadAsync()
        {
            try
            {
                (index, captions) = await GetIndexAsync(guiContext).ConfigureAwait(true);

                if (!index.Files.Contains(fileName, StringComparer.OrdinalIgnoreCase))
                {
                    index.AddEvents(fileName, fileEvents);
                }

                var loadedIndex = index;
                rows = await Task.Run(() => CreateRows(loadedIndex)).ConfigureAwait(true);
            }
            catch (Exception e)
            {
                Log.Error(nameof(SoundEventViewer), $"Failed to index sound events for \"{guiContext.DisplayPath}\": {e}");

                if (!IsDisposed)
                {
                    countLabel.Text = $"Failed to index sound events: {e.Message}";
                }

                return;
            }

            if (IsDisposed)
            {
                return;
            }

            OnScopeChanged();

            if (filteredRows.Count > 0)
            {
                ShowRow(filteredRows[0], addToHistory: false);
                SelectCurrentInList();
            }
        }

        private List<EventRow> CreateRows(SoundEventIndex loadedIndex)
        {
            var result = new List<EventRow>(loadedIndex.Count);
            var soundExists = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            foreach (var name in loadedIndex.EventNames)
            {
                var definitions = loadedIndex.GetDefinitions(name);

                foreach (var (file, data) in definitions)
                {
                    var (resolved, problems, _) = loadedIndex.Resolve(data);
                    var type = GetString(resolved, "type");
                    var duration = resolved.FirstOrDefault(static r => r.Key == "vsnd_duration").Value is { } durationValue && IsNumber(durationValue)
                        ? durationValue.ToSingle(CultureInfo.InvariantCulture)
                        : -1f;
                    var missingBase = SoundEventIndex.GetBases(data).Any(b => loadedIndex.Find(b.Name) == null);
                    var missingSound = false;

                    foreach (var (_, sound) in loadedIndex.GetSoundReferences(data))
                    {
                        if (!soundExists.TryGetValue(sound, out var exists))
                        {
                            exists = SoundExists(sound);
                            soundExists[sound] = exists;
                        }

                        missingSound |= !exists;
                    }

                    result.Add(new EventRow(name, file, data, type, duration, definitions.Count > 1, problems.Count > 0, missingBase, missingSound, IsPlayable(type)));
                }
            }

            return result;
        }

        private bool SoundExists(string sound)
        {
            var found = guiContext.FindFile(sound.Replace('\\', '/') + GameFileLoader.CompiledFileSuffix, logNotFound: false);
            return found.PathOnDisk != null || found.PackageEntry != null;
        }

        private bool IsPlayable(string type)
        {
            if (type.Length == 0)
            {
                return false;
            }

            lock (playableTypes)
            {
                if (!playableTypes.TryGetValue(type, out var playable))
                {
                    var bank = new SoundEventBank();
                    var data = KVObject.Collection();
                    data.Add("type", type);
                    bank.AddSoundEvent(type, data);
                    playable = SoundEvent.Build(bank.GetSoundEvent(type)!) != null;
                    playableTypes.Add(type, playable);
                }

                return playable;
            }
        }

        private void OnScopeChanged()
        {
            var scoped = ScopedRows();
            var types = scoped.GroupBy(static r => r.Type, StringComparer.OrdinalIgnoreCase)
                .OrderBy(static g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(static g => $"{(g.Key.Length == 0 ? "(no type)" : g.Key)} ({g.Count().ToString("N0", CultureInfo.InvariantCulture)})");
            var selectedType = TypeFilter();

            updatingList = true;
            typeBox.BeginUpdate();
            typeBox.Items.Clear();
            typeBox.Items.Add("All types");

            foreach (var type in types)
            {
                typeBox.Items.Add(type);
            }

            typeBox.SelectedIndex = 0;

            for (var i = 1; i < typeBox.Items.Count && selectedType != null; i++)
            {
                if (TypeOfItem(typeBox.Items[i]!.ToString()!) == selectedType)
                {
                    typeBox.SelectedIndex = i;
                }
            }

            typeBox.EndUpdate();
            updatingList = false;

            eventGrid.Columns[3].Visible = scopeBox.SelectedIndex == 1;
            ApplyFilter();
        }

        private IEnumerable<EventRow> ScopedRows()
            => scopeBox.SelectedIndex == 1 ? rows : rows.Where(r => r.File.Equals(fileName, StringComparison.OrdinalIgnoreCase));

        private string? TypeFilter() => typeBox.SelectedIndex > 0 ? TypeOfItem(typeBox.SelectedItem!.ToString()!) : null;

        private static string TypeOfItem(string item)
        {
            var type = item[..item.LastIndexOf(" (", StringComparison.Ordinal)];
            return type == "(no type)" ? string.Empty : type;
        }

        private void ApplyFilter()
        {
            if (updatingList || index == null)
            {
                return;
            }

            var search = searchBox.Text.Trim();
            var type = TypeFilter();
            var status = statusBox.SelectedIndex;

            filteredRows = [.. ScopedRows().Where(row =>
                (search.Length == 0 || row.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || row.Type.Contains(search, StringComparison.OrdinalIgnoreCase))
                && (type == null || row.Type.Equals(type, StringComparison.OrdinalIgnoreCase))
                && status switch
                {
                    1 => row.Duplicate,
                    2 => row.HasProblems,
                    3 => row.MissingBase,
                    4 => row.MissingSound,
                    5 => !row.Playable,
                    _ => true,
                })];

            SortRows();

            var total = ScopedRows().Count();
            countLabel.Text = filteredRows.Count == total
                ? $"{total.ToString("N0", CultureInfo.InvariantCulture)} events"
                : $"{filteredRows.Count.ToString("N0", CultureInfo.InvariantCulture)} of {total.ToString("N0", CultureInfo.InvariantCulture)} events";

            RefreshEventGrid();
        }

        private void SortRows()
        {
            Comparison<EventRow> comparison = sortColumn switch
            {
                1 => static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Type, b.Type),
                2 => static (a, b) => a.Duration.CompareTo(b.Duration),
                3 => static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.File, b.File),
                _ => static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name),
            };

            filteredRows.Sort((a, b) =>
            {
                var result = comparison(a, b);

                if (result == 0)
                {
                    result = StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
                }

                return sortDescending ? -result : result;
            });

            for (var i = 0; i < eventGrid.Columns.Count; i++)
            {
                eventGrid.Columns[i].HeaderCell.SortGlyphDirection = i != sortColumn ? SortOrder.None : sortDescending ? SortOrder.Descending : SortOrder.Ascending;
            }
        }

        private void RefreshEventGrid()
        {
            updatingList = true;
            eventGrid.RowCount = 0;
            eventGrid.RowCount = filteredRows.Count;
            updatingList = false;

            SelectCurrentInList();
            eventGrid.Invalidate();
        }

        private void OnEventCellValueNeeded(object? sender, DataGridViewCellValueEventArgs e)
        {
            if (e.RowIndex >= filteredRows.Count)
            {
                return;
            }

            var row = filteredRows[e.RowIndex];

            e.Value = e.ColumnIndex switch
            {
                0 => row.Name,
                1 => row.Type,
                2 => row.Duration >= 0f ? row.Duration.ToString("0.00 s", CultureInfo.InvariantCulture) : string.Empty,
                _ => row.File,
            };
        }

        private void OnEventSelectionChanged(object? sender, EventArgs e)
        {
            if (updatingList || eventGrid.CurrentCell == null || eventGrid.CurrentCell.RowIndex >= filteredRows.Count)
            {
                return;
            }

            var row = filteredRows[eventGrid.CurrentCell.RowIndex];

            if (row != current)
            {
                ShowRow(row, addToHistory: current != null);
            }
        }

        private void OnEventColumnHeaderClick(object? sender, DataGridViewCellMouseEventArgs e)
        {
            sortDescending = sortColumn == e.ColumnIndex && !sortDescending;
            sortColumn = e.ColumnIndex;
            SortRows();
            RefreshEventGrid();
        }

        /// <summary>
        /// Shows an event in the details, as following a link to it does. The definition in <paramref name="file"/> is shown
        /// when given, otherwise the one that wins.
        /// </summary>
        /// <returns>Whether the event is known.</returns>
        public bool ShowEvent(string name, string? file = null)
        {
            var row = rows.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && (file == null || r.File.Equals(file, StringComparison.OrdinalIgnoreCase)));

            if (row == null)
            {
                return false;
            }

            ShowRow(row, addToHistory: current != null && current != row);
            SelectCurrentInList();
            return true;
        }

        private void SelectCurrentInList()
        {
            var rowIndex = current == null ? -1 : filteredRows.IndexOf(current);

            updatingList = true;
            eventGrid.ClearSelection();

            if (rowIndex >= 0)
            {
                eventGrid.CurrentCell = eventGrid.Rows[rowIndex].Cells[0];
                eventGrid.Rows[rowIndex].Selected = true;
            }

            updatingList = false;
        }

        public void GoBack() => Navigate(backHistory, forwardHistory);

        public void GoForward() => Navigate(forwardHistory, backHistory);

        private void Navigate(List<(string Name, string File)> from, List<(string Name, string File)> to)
        {
            if (from.Count == 0 || current == null)
            {
                return;
            }

            var (name, file) = from[^1];
            from.RemoveAt(from.Count - 1);
            to.Add((current.Name, current.File));

            var row = rows.FirstOrDefault(r => r.Name == name && r.File == file);

            if (row != null)
            {
                ShowRow(row, addToHistory: false);
                SelectCurrentInList();
            }
        }

        private void ShowRow(EventRow row, bool addToHistory)
        {
            if (addToHistory && current != null)
            {
                backHistory.Add((current.Name, current.File));
                forwardHistory.Clear();
            }

            current = row;

            titleLabel.Text = row.Name;
            toolTip.SetToolTip(titleLabel, row.Name);
            typeLink.Text = row.Type.Length > 0 ? row.Type : "(no type)";
            typeLink.Links[0].Enabled = index?.GetStackFile(row.Type) != null;
            fileLink.Text = row.File;

            var notices = new List<string>();
            var definitions = index!.GetDefinitions(row.Name);

            if (definitions.Count > 1)
            {
                var others = string.Join(", ", definitions.Where(d => d.File != row.File).Select(static d => d.File));
                notices.Add(definitions[^1].File == row.File
                    ? $"Also defined in {others}. This definition is used, as it loads last."
                    : $"Also defined in {others}. The definition in {definitions[^1].File} is used, as it loads last.");
            }

            var (resolved, problems, _) = index.Resolve(row.Data);
            notices.AddRange(problems);

            if (!row.Playable)
            {
                notices.Add(row.Type.Length == 0
                    ? "Has no type, so it cannot be played as an event."
                    : $"Events of type {row.Type} cannot be played here. Play its sounds below.");
            }

            noticeLabel.Text = string.Join(Environment.NewLine, notices);
            noticeLabel.Visible = notices.Count > 0;
            noticeLabel.ForeColor = problems.Count > 0 ? Themer.CurrentThemeColors.Attention : Themer.CurrentThemeColors.ContrastSoft;

            var lineText = GetString(resolved, "line_text");
            var closedCaption = captions.Select(c => c[row.Name.ToLowerInvariant()]).FirstOrDefault(static c => c != null);
            string?[] captionLines =
            [
                lineText.Length > 0 ? $"Line text: {lineText}" : null,
                closedCaption != null ? $"Closed caption: {closedCaption.Text}" : null,
            ];
            var caption = string.Join(Environment.NewLine, captionLines.OfType<string>());

            captionLabel.Text = caption;
            captionLabel.Visible = caption.Length > 0;
            captionLabel.ForeColor = Themer.CurrentThemeColors.Contrast;
            captionLabel.Font = italicFont;

            playButton.Enabled = row.Playable;
            toolTip.SetToolTip(playButton, row.Playable ? "Play event (Space)" : "This event cannot be played here");
            backButton.Enabled = backHistory.Count > 0;
            forwardButton.Enabled = forwardHistory.Count > 0;
            copyButton.Enabled = true;
            openButton.Enabled = true;
            typeLink.LinkColor = Themer.CurrentThemeColors.Accent;
            fileLink.LinkColor = Themer.CurrentThemeColors.Accent;

            ShowProperties();
            ShowLinks(row);
        }

        private void ShowEmptyDetails()
        {
            titleLabel.Text = string.Empty;
            typeLink.Text = string.Empty;
            fileLink.Text = string.Empty;
            noticeLabel.Visible = false;
            captionLabel.Visible = false;

            foreach (var button in new[] { backButton, forwardButton, playButton, stopButton, copyButton, openButton })
            {
                button.Enabled = false;
            }
        }

        private void ShowProperties()
        {
            if (current == null || index == null)
            {
                return;
            }

            var resolved = index.Resolve(current.Data).Keys;
            var values = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < resolved.Count; i++)
            {
                values.TryAdd(resolved[i].Key, i);
            }

            var fields = current.Type.Length > 0 ? index.GetStackFields(current.Type) : [];
            var groups = new List<(string Name, List<(string Key, KVObject? Value, string Source, ValueSource Kind, bool EditorHidden)> Rows)>();
            var shown = new HashSet<int>();

            foreach (var (name, field, stack) in fields)
            {
                var groupName = field.GetSubCollection("_system_properties")?.GetStringProperty("property_group") is { Length: > 0 } propertyGroup ? propertyGroup : "General";
                var group = groups.Find(g => g.Name == groupName).Rows;

                if (group == null)
                {
                    group = [];
                    groups.Add((groupName, group));
                }

                var editorHidden = IsEditorHidden(field, resolved, values, fields);

                if (values.ContainsKey(name))
                {
                    for (var i = 0; i < resolved.Count; i++)
                    {
                        var (key, value, source) = resolved[i];

                        if (key.Equals(name, StringComparison.OrdinalIgnoreCase) && shown.Add(i))
                        {
                            group.Add((name, value, source ?? string.Empty, source == null ? ValueSource.Own : ValueSource.Inherited, editorHidden));
                        }
                    }
                }
                else if (defaultsBox.Checked)
                {
                    var origin = stack.Equals(current.Type, StringComparison.OrdinalIgnoreCase) ? stack : $"{stack}, imported";
                    group.Add((name, field.TryGetValue("value", out var defaultValue) ? defaultValue : null, $"default ({origin})", ValueSource.Default, editorHidden));
                }
            }

            var unshown = resolved.Where((r, i) => !shown.Contains(i) && !r.Key.Equals("type", StringComparison.OrdinalIgnoreCase))
                .Select(static r => (r.Key, (KVObject?)r.Value, r.Source ?? string.Empty, r.Source == null ? ValueSource.Own : ValueSource.Inherited, false))
                .ToList();
            var eventKeys = unshown.FindAll(static r => EventKeys.Contains(r.Key));
            var other = unshown.FindAll(static r => !EventKeys.Contains(r.Key));

            if (eventKeys.Count > 0)
            {
                groups.Add(("Event", eventKeys));
            }

            if (other.Count > 0)
            {
                groups.Add((fields.Count > 0 ? "Not in stack, ignored by the engine" : "Keys", other));
            }

            propertyGrid.SuspendLayout();
            propertyGrid.Rows.Clear();

            foreach (var (groupName, groupRows) in groups)
            {
                if (groupRows.Count == 0)
                {
                    continue;
                }

                var headerIndex = propertyGrid.Rows.Add(groupName, string.Empty, string.Empty);
                var header = propertyGrid.Rows[headerIndex];
                header.Tag = ValueSource.Group;
                header.DefaultCellStyle.Font = boldFont;
                header.DefaultCellStyle.BackColor = Themer.CurrentThemeColors.App;
                header.DefaultCellStyle.SelectionBackColor = Themer.CurrentThemeColors.App;
                header.DefaultCellStyle.SelectionForeColor = Themer.CurrentThemeColors.Contrast;

                foreach (var (key, value, source, kind, editorHidden) in groupRows)
                {
                    var rowIndex = propertyGrid.Rows.Add(key, value == null ? string.Empty : FormatValue(value), source);
                    var gridRow = propertyGrid.Rows[rowIndex];
                    gridRow.Tag = kind;

                    if (value != null && ParseCurve(value) is { } curve)
                    {
                        gridRow.Cells[1].Tag = curve;
                        gridRow.Height = this.AdjustForDPI(44);
                    }

                    gridRow.Cells[1].ToolTipText = value == null ? string.Empty : FormatValue(value, maxLength: 4000);

                    switch (kind)
                    {
                        case ValueSource.Own:
                            gridRow.Cells[1].Style.Font = boldFont;
                            break;
                        case ValueSource.Inherited:
                            gridRow.Cells[2].Style.ForeColor = Themer.CurrentThemeColors.Accent;
                            gridRow.Cells[2].ToolTipText = $"Inherited from {source}, double-click to show it";
                            break;
                        case ValueSource.Default:
                            gridRow.DefaultCellStyle.Font = italicFont;
                            gridRow.DefaultCellStyle.ForeColor = Themer.CurrentThemeColors.ContrastSoft;
                            break;
                    }

                    if (editorHidden)
                    {
                        gridRow.DefaultCellStyle.ForeColor = Themer.CurrentThemeColors.Border;
                        gridRow.Cells[0].ToolTipText = "The editor hides this field while the flag that enables it is off. It has no effect on the engine.";
                    }
                }
            }

            propertyGrid.ResumeLayout();
            propertyGrid.ClearSelection();
        }

        private static bool IsEditorHidden(KVObject field, List<(string Key, KVObject Value, string? Source)> resolved, Dictionary<string, int> values,
            List<(string Name, KVObject Field, string Stack)> fields)
        {
            var flag = field.GetSubCollection("_system_properties")?.GetStringProperty("display_edit_flag");

            if (string.IsNullOrEmpty(flag))
            {
                return false;
            }

            var flagName = flag.StartsWith("public.", StringComparison.Ordinal) ? flag["public.".Length..] : flag;

            if (values.TryGetValue(flagName, out var valueIndex))
            {
                return !IsTrue(resolved[valueIndex].Value);
            }

            var flagField = fields.Find(f => f.Name == flagName).Field;
            return flagField != null && flagField.TryGetValue("value", out var defaultValue) && !IsTrue(defaultValue);
        }

        private static bool IsTrue(KVObject value) => value.ValueType switch
        {
            KVValueType.Boolean => (bool)value,
            KVValueType.String => (string)value is "true" or "1" || (float.TryParse((string)value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number != 0f),
            _ when IsNumber(value) => value.ToSingle(CultureInfo.InvariantCulture) != 0f,
            _ => true,
        };

        private static bool IsNumber(KVObject value) => value.ValueType is KVValueType.FloatingPoint or KVValueType.FloatingPoint64
            or KVValueType.Int16 or KVValueType.Int32 or KVValueType.Int64 or KVValueType.UInt16 or KVValueType.UInt32 or KVValueType.UInt64;

        private static string GetString(List<(string Key, KVObject Value, string? Source)> resolved, string key)
        {
            var value = resolved.FirstOrDefault(r => r.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;
            return value?.ValueType == KVValueType.String ? (string)value : string.Empty;
        }

        private static string FormatValue(KVObject value, int maxLength = 300)
        {
            var text = new System.Text.StringBuilder();
            Append(value);
            return text.Length > maxLength ? string.Concat(text.ToString(0, maxLength), "...") : text.ToString();

            void Append(KVObject item)
            {
                if (text.Length > maxLength)
                {
                    return;
                }

                switch (item.ValueType)
                {
                    case KVValueType.Array:
                        text.Append('[');
                        var first = true;

                        foreach (var child in item.Values)
                        {
                            text.Append(first ? string.Empty : ", ");
                            first = false;
                            Append(child);
                        }

                        text.Append(']');
                        break;

                    case KVValueType.Collection:
                        text.Append('{');
                        var firstKey = true;

                        foreach (var (key, child) in item)
                        {
                            text.Append(firstKey ? string.Empty : ", ").Append(key).Append(" = ");
                            firstKey = false;
                            Append(child);
                        }

                        text.Append('}');
                        break;

                    case KVValueType.Boolean:
                        text.Append((bool)item ? "true" : "false");
                        break;

                    case KVValueType.FloatingPoint or KVValueType.FloatingPoint64:
                        text.Append(item.ToDouble(CultureInfo.InvariantCulture).ToString("0.######", CultureInfo.InvariantCulture));
                        break;

                    case KVValueType.Null:
                        text.Append("null");
                        break;

                    default:
                        text.Append(item.ToString(CultureInfo.InvariantCulture));
                        break;
                }
            }
        }

        private static SoundEventCurve? ParseCurve(KVObject value)
        {
            KVObject points;

            if (value.ValueType == KVValueType.Array && value.Count > 0 && value[0].ValueType == KVValueType.Array)
            {
                points = value;
            }
            else if (value.ValueType == KVValueType.Collection && value.Count > 0 && value.Values.All(static v => v.ValueType == KVValueType.String))
            {
                points = KVObject.Array();

                foreach (var text in value.Values)
                {
                    var numbers = ((string)text).Trim('[', ']', ' ').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                    if (numbers.Length < 2)
                    {
                        return null;
                    }

                    points.Add(KVObject.Array(numbers.Select(static n => (KVObject)float.Parse(n, CultureInfo.InvariantCulture))));
                }
            }
            else
            {
                return null;
            }

            var holder = KVObject.Collection();
            holder.Add("curve", points);

            try
            {
                return SoundEventCurve.Parse(holder, "curve");
            }
            catch (FormatException)
            {
                return null;
            }
        }

        private void OnPropertyCellPainting(object? sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex != 1 || e.Graphics == null || propertyGrid.Rows[e.RowIndex].Cells[1].Tag is not SoundEventCurve curve)
            {
                return;
            }

            e.Paint(e.CellBounds, DataGridViewPaintParts.Background | DataGridViewPaintParts.Border | DataGridViewPaintParts.SelectionBackground);

            var padding = this.AdjustForDPI(4);
            var plot = Rectangle.Inflate(e.CellBounds, -padding, -padding);
            var labelWidth = Math.Min(plot.Width / 2, this.AdjustForDPI(150));
            var labelArea = new Rectangle(plot.Right - labelWidth, plot.Top, labelWidth, plot.Height);
            plot.Width -= labelWidth + padding;
            var samples = Math.Max(2, plot.Width / 2);
            var minX = curve.MinX;
            var maxX = Math.Max(curve.MaxX, minX + 0.0001f);
            var points = new PointF[samples];
            var minY = float.MaxValue;
            var maxY = float.MinValue;

            for (var i = 0; i < samples; i++)
            {
                var y = curve.Evaluate(float.Lerp(minX, maxX, i / (samples - 1f)));
                minY = MathF.Min(minY, y);
                maxY = MathF.Max(maxY, y);
                points[i] = new PointF(i, y);
            }

            if (maxY - minY < 0.0001f)
            {
                minY -= 0.5f;
                maxY += 0.5f;
            }

            for (var i = 0; i < samples; i++)
            {
                points[i] = new PointF(plot.Left + points[i].X * plot.Width / (samples - 1f), plot.Bottom - (points[i].Y - minY) / (maxY - minY) * plot.Height);
            }

            using var axisPen = new Pen(Themer.CurrentThemeColors.Border);
            using var curvePen = new Pen(Themer.CurrentThemeColors.Accent, this.AdjustForDPI(2));

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.DrawRectangle(axisPen, plot);
            e.Graphics.DrawLines(curvePen, points);

            var range = string.Create(CultureInfo.InvariantCulture, $"x {minX:0.##} to {maxX:0.##}{Environment.NewLine}y {minY:0.##} to {maxY:0.##}");
            System.Windows.Forms.TextRenderer.DrawText(e.Graphics, range, Font, labelArea, Themer.CurrentThemeColors.ContrastSoft, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            e.Handled = true;
        }

        private void ShowLinks(EventRow row)
        {
            soundGrid.Rows.Clear();

            foreach (var (key, sound) in index!.GetSoundReferences(row.Data))
            {
                var exists = SoundExists(sound);
                var rowIndex = soundGrid.Rows.Add(sound, key, exists ? string.Empty : "Not shipped");
                soundGrid.Rows[rowIndex].Tag = exists ? sound : null;

                if (!exists)
                {
                    soundGrid.Rows[rowIndex].DefaultCellStyle.ForeColor = Themer.CurrentThemeColors.Attention;
                }
            }

            referenceGrid.Rows.Clear();

            foreach (var (key, target) in index.GetEventReferences(row.Name, row.Data))
            {
                var exists = index.Find(target) != null;
                var rowIndex = referenceGrid.Rows.Add(target, key, exists ? string.Empty : "Missing");
                referenceGrid.Rows[rowIndex].Tag = exists ? target : null;

                if (!exists)
                {
                    referenceGrid.Rows[rowIndex].DefaultCellStyle.ForeColor = Themer.CurrentThemeColors.Attention;
                }
            }

            usedByGrid.Rows.Clear();

            foreach (var (user, key) in index.GetReferencedBy(row.Name))
            {
                var rowIndex = usedByGrid.Rows.Add(user, key);
                usedByGrid.Rows[rowIndex].Tag = user;
            }

            soundsPage.Text = $"Sounds ({soundGrid.RowCount})";
            referencesPage.Text = $"References ({referenceGrid.RowCount})";
            usedByPage.Text = $"Used by ({usedByGrid.RowCount})";

            foreach (var grid in new[] { soundGrid, referenceGrid, usedByGrid })
            {
                grid.CurrentCell = null;
                grid.ClearSelection();
            }

            StopSound();
        }

        private void OnGridRowActivated(object? sender, int rowIndex)
        {
            if (sender == soundGrid)
            {
                PlaySound(rowIndex, autoPlay: true);
                return;
            }

            var grid = (Grid)sender!;
            var tag = grid.Rows[rowIndex].Tag;

            var target = tag switch
            {
                string name => name,
                ValueSource.Inherited => ((string)grid.Rows[rowIndex].Cells[2].Value!).Split(" via ")[0],
                _ => null,
            };

            if (target != null)
            {
                ShowEvent(target);
            }
        }

        /// <summary>Shows a sound of the sounds list in the player, as selecting it does, and plays it when <paramref name="autoPlay"/> is set.</summary>
        /// <returns>Whether the sound could be loaded.</returns>
        public bool PlaySound(int rowIndex, bool autoPlay)
        {
            if (rowIndex < 0 || rowIndex >= soundGrid.RowCount || soundGrid.Rows[rowIndex].Tag is not string sound)
            {
                return false;
            }

            StopSound();

            try
            {
                soundResource = guiContext.FileLoaderNoCache.LoadFile(sound.Replace('\\', '/') + GameFileLoader.CompiledFileSuffix);

                if (soundResource != null && AudioPlayer.CreateWaveStream(soundResource) is var (waveStream, loopMarkers))
                {
                    var ownedStream = waveStream;

                    try
                    {
                        audioPanel = new AudioPlaybackPanel(ownedStream, sound, autoPlay, loopMarkers);
                        ownedStream = null;
                    }
                    finally
                    {
                        ownedStream?.Dispose();
                    }

                    audioHost.Controls.Add(audioPanel);
                    Themer.ThemeControl(audioPanel);
                    audioHost.Visible = true;
                }
            }
            catch (Exception e)
            {
                Log.Error(nameof(SoundEventViewer), $"Failed to play \"{sound}\": {e}");
            }

            return audioPanel != null;
        }

        private void StopSound()
        {
            audioHost.Visible = false;
            audioPanel?.Dispose();
            audioPanel = null;
            soundResource?.Dispose();
            soundResource = null;
        }

        /// <summary>Plays the shown event through the renderer's sound event player, without spatialization.</summary>
        public void PlayEvent()
        {
            if (current is not { Playable: true } row || index == null)
            {
                return;
            }

            if (player == null)
            {
                try
                {
#pragma warning disable CA2000 // The player owns the device and disposes it
                    player = new SoundEventPlayer(guiContext, new NAudioDevice(), VrfGuiContext.Logger);
#pragma warning restore CA2000
                }
                catch (System.Runtime.InteropServices.COMException e)
                {
                    Log.Warn(nameof(SoundEventViewer), $"No audio device available, sound playback disabled: {e.Message}");
                    return;
                }

                foreach (var name in index.EventNames)
                {
                    player.Bank.AddSoundEvent(name, index.Find(name)!);
                }

                playerTimer.Start();
            }

            // The shown definition may not be the one that wins
            player.Bank.RemoveSoundEvent(row.Name);
            player.Bank.AddSoundEvent(row.Name, row.Data);
            player.Volume = Settings.Config.Volume;
            player.Update(listener);

            if (player.Play(row.Name, channel: PreviewChannel) == null)
            {
                Log.Warn(nameof(SoundEventViewer), $"Sound event \"{row.Name}\" played nothing");
            }

            stopButton.Enabled = true;
        }

        public void StopPlayback()
        {
            player?.StopChannel(PreviewChannel);
            StopSound();
        }

        private void CopyName()
        {
            if (current != null)
            {
                AppClipboard.SetText(current.Name);
            }
        }

        private void OpenDefiningFile()
        {
            if (current == null)
            {
                return;
            }

            if (current.File.Equals(fileName, StringComparison.OrdinalIgnoreCase))
            {
                scopeBox.SelectedIndex = 0;
                ShowEvent(current.Name, current.File);
                return;
            }

            _ = OpenEventAsync(guiContext, current.File, current.Name);
        }

        private void OpenStackFile()
        {
            if (current != null && index?.GetStackFile(current.Type) is { } stackFile)
            {
                Resource.OpenExternalReference(guiContext, stackFile);
            }
        }

        /// <summary>Opens a sound event file in a new tab and shows one of its events there.</summary>
        public static async Task OpenEventAsync(VrfGuiContext context, string file, string eventName)
        {
            var found = context.FindFileWithContext(file);

            if (found.Context == null)
            {
                Log.Warn(nameof(SoundEventViewer), $"Could not find \"{file}\"");
                return;
            }

            var tab = Program.MainForm.OpenFile(found.Context, found.PackageEntry);

            if (tab.Tag is ExportData exportData && await exportData.Loaded.ConfigureAwait(true) == null && Find(tab) is { } viewer)
            {
                await viewer.WaitForRowsAsync().ConfigureAwait(true);
                viewer.ShowEvent(eventName, file);
            }
        }

        private async Task WaitForRowsAsync()
        {
            while (index == null || rows.Count == 0)
            {
                if (IsDisposed)
                {
                    return;
                }

                await Task.Delay(50).ConfigureAwait(true);
            }
        }

        /// <summary>Finds the sound event viewer in a control, such as the tab of a file.</summary>
        public static SoundEventViewer? Find(Control control)
        {
            if (control is SoundEventViewer viewer)
            {
                return viewer;
            }

            foreach (Control child in control.Controls)
            {
                if (Find(child) is { } found)
                {
                    return found;
                }
            }

            return null;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            var typing = ActiveControl == searchBox || (ActiveControl as ContainerControl)?.ActiveControl is TextBox;

            switch (keyData)
            {
                case Keys.Control | Keys.F:
                    searchBox.Focus();
                    searchBox.SelectAll();
                    return true;

                case Keys.Alt | Keys.Left:
                    GoBack();
                    return true;

                case Keys.Alt | Keys.Right:
                    GoForward();
                    return true;

                case Keys.Escape:
                    StopPlayback();
                    return true;

                case Keys.Space when !typing && audioPanel?.ContainsFocus != true:
                    PlayEvent();
                    return true;

                case Keys.Control | Keys.C when !typing:
                    CopyName();
                    return true;
            }

            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void OnSearchKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode is Keys.Down or Keys.Enter && filteredRows.Count > 0)
            {
                eventGrid.Focus();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            HandleMouseNavigation(e);
        }

        private void HandleMouseNavigation(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.XButton1)
            {
                GoBack();
            }
            else if (e.Button == MouseButtons.XButton2)
            {
                GoForward();
            }
        }

        private ThemedComboBox CreateFilterBox(string name, string[] items)
        {
            var box = new ThemedComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList,
                AccessibleName = name,
                Margin = new Padding(0, 0, 4, 0),
            };

            box.Items.AddRange(items);
            box.SelectedIndex = 0;
            box.SelectedIndexChanged += (_, _) => ApplyFilter();

            return box;
        }

        private static DataGridViewTextBoxColumn CreateColumn(string header, float fillWeight) => new()
        {
            HeaderText = header,
            FillWeight = fillWeight,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        };

        private ThemedButton CreateToolButton(string icon, string name, string tooltip, EventHandler click)
        {
            var size = this.AdjustForDPI(32);
            var button = new ThemedButton
            {
                Image = AppIcons.ImageList.Images[AppIcons.Icons[icon]],
                Size = new Size(size, size),
                Margin = new Padding(2, 4, 2, 4),
                AccessibleName = name,
            };

            button.Click += click;
            toolTip.SetToolTip(button, tooltip);

            return button;
        }

        private static LinkLabel CreateLink(string name) => new()
        {
            AutoSize = true,
            Margin = new Padding(0, 2, 12, 2),
            UseMnemonic = false,
            AccessibleName = name,
        };

        private static Label CreateInfoLabel(string name) => new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            MaximumSize = new Size(4000, 0),
            Padding = new Padding(0, 2, 0, 4),
            UseMnemonic = false,
            AccessibleName = name,
        };

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);

            foreach (var label in new[] { noticeLabel, captionLabel })
            {
                label.MaximumSize = new Size(Math.Max(100, label.Parent?.ClientSize.Width ?? Width), 0);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                playerTimer.Dispose();
                player?.Dispose();
                StopSound();
                toolTip.Dispose();
                titleFont.Dispose();
                boldFont.Dispose();
                italicFont.Dispose();
            }

            base.Dispose(disposing);
        }

        /// <summary>A read only grid that selects whole rows, and raises <see cref="Activated"/> on double-click or Enter.</summary>
        private sealed class Grid : DataGridView
        {
            public event EventHandler<int>? Activated;

            public Grid(string name)
            {
                Dock = DockStyle.Fill;
                DoubleBuffered = true;
                ReadOnly = true;
                AllowUserToAddRows = false;
                AllowUserToDeleteRows = false;
                AllowUserToResizeRows = false;
                RowHeadersVisible = false;
                SelectionMode = DataGridViewSelectionMode.FullRowSelect;
                MultiSelect = false;
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
                CellBorderStyle = DataGridViewCellBorderStyle.None;
                EditMode = DataGridViewEditMode.EditProgrammatically;
                StandardTab = true;
                ShowCellToolTips = true;
                AccessibleName = name;
                RowTemplate.Height = this.AdjustForDPI(24);
                ColumnHeadersHeight = this.AdjustForDPI(26);
            }

            protected override void OnCellDoubleClick(DataGridViewCellEventArgs e)
            {
                base.OnCellDoubleClick(e);

                if (e.RowIndex >= 0)
                {
                    Activated?.Invoke(this, e.RowIndex);
                }
            }

            protected override bool ProcessDialogKey(Keys keyData)
            {
                if (keyData == Keys.Enter && CurrentRow != null)
                {
                    Activated?.Invoke(this, CurrentRow.Index);
                    return true;
                }

                return base.ProcessDialogKey(keyData);
            }

            protected override bool ProcessDataGridViewKey(KeyEventArgs e)
            {
                if (e.KeyData == Keys.Enter && CurrentRow != null)
                {
                    Activated?.Invoke(this, CurrentRow.Index);
                    return true;
                }

                return base.ProcessDataGridViewKey(e);
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);

                if (e.Button is MouseButtons.XButton1 or MouseButtons.XButton2)
                {
                    for (var parent = Parent; parent != null; parent = parent.Parent)
                    {
                        if (parent is SoundEventViewer viewer)
                        {
                            viewer.HandleMouseNavigation(e);
                            break;
                        }
                    }
                }
            }
        }
    }
}
