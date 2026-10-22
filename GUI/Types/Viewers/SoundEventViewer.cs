using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
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

namespace GUI.Types.Viewers
{
    /// <summary>
    /// Lists the sound events of a file or of the whole game, and shows the selected event with the keys it
    /// resolves to, grouped by its stack, its sounds, the events it references and the events that use it.
    /// </summary>
    internal sealed partial class SoundEventViewer : UserControl
    {
        private const string PreviewChannel = "sound_event_viewer";
        private const TextFormatFlags SegmentFlags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix;

        private sealed class EventRow(string name, string file, KVObject data, string type, float duration, bool duplicate, bool hasProblems, bool rejected, bool missingBase)
        {
            public string Name { get; } = name;
            public string File { get; } = file;
            public KVObject Data { get; } = data;
            public string Type { get; } = type;
            public float Duration { get; } = duration;
            public bool Duplicate { get; } = duplicate;
            public bool HasProblems { get; } = hasProblems;
            public bool MissingBase { get; } = missingBase;
            public bool Rejected { get; } = rejected;
            public bool Playable => !Rejected && SoundEvent.IsSupportedType(Type);
            public bool MissingSound { get; set; }
            public bool HasWarning => HasProblems || MissingBase || MissingSound;
        }

        private enum ValueSource
        {
            Own,
            Inherited,
            Default,
            Group,
        }

        // Held by the event outside of its stack: read by the engine when it parses the event, or written by the compiler
        private static readonly HashSet<string> EventKeys = new(["groups", "preload_vsnds", "memory_type", "vsnd_duration"], StringComparer.OrdinalIgnoreCase);

        private static Color LinkColor => Themer.CurrentThemeColors.ColorMode == SystemColorMode.Dark ? Color.FromArgb(99, 161, 255) : Color.FromArgb(0, 102, 204);

        private static readonly Dictionary<string, (Task<(SoundEventIndex Index, List<ValveResourceFormat.ClosedCaptions.ClosedCaptions> Captions)> Task, HashSet<VrfGuiContext> Roots)> GameIndexes
            = new(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<VrfGuiContext, Task<(SoundEventIndex Index, List<ValveResourceFormat.ClosedCaptions.ClosedCaptions> Captions)>> PackageIndexes = [];

        private readonly VrfGuiContext guiContext;
        private readonly string fileName;
        private readonly IReadOnlyList<(string Name, KVObject Data)> fileEvents;
        private readonly Task loading;

        private SoundEventIndex? index;
        private List<ValveResourceFormat.ClosedCaptions.ClosedCaptions> captions = [];
        private List<EventRow> rows = [];
        private List<EventRow> filteredRows = [];
        private bool? soundsChecked;
        private EventRow? current;
        private string captionText = string.Empty;
        private readonly List<(string Name, string File)> backHistory = [];
        private readonly List<(string Name, string File)> forwardHistory = [];
        private readonly List<string?> typeFilters = [null];
        private int sortColumn = 1;
        private bool sortDescending;
        private bool updating;
        private bool listSplitMoved;
        private bool detailsSplitMoved;

        private SoundEventPlayer? player;
        private SoundEvent? playing;
        private readonly Timer playerTimer = new() { Interval = 30 };
        private readonly Camera listener = new();
        private readonly List<(Vector3 Position, string Text)> audiblePositioned = [];
        private readonly List<string> audibleFlat = [];
        private ValveResourceFormat.Resource? soundResource;
        private AudioPlaybackPanel? audioPanel;

        private readonly Font titleFont;
        private readonly Font boldFont;
        private readonly Font italicFont;
        private readonly Font groupFont;
        private readonly ToolTip toolTip = new();

#pragma warning disable CA2213 // Added to Controls, which disposes them
        private readonly SearchBox searchBox;
        private readonly ThemedComboBox scopeBox;
        private readonly ThemedComboBox typeBox;
        private readonly ThemedComboBox statusBox;
        private readonly Label countLabel;
        private readonly LinkLabel noMatchLink;
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
        private readonly LinkLabel baseLink;
        private readonly LinkLabel noticeLabel;
        private readonly Label captionLabel;
        private readonly CheckBox defaultsBox;
        private readonly Grid propertyGrid;
        private readonly TabPage soundsPage;
        private readonly TabPage referencesPage;
        private readonly TabPage usedByPage;
        private readonly ThemedTabControl linkTabs;
        private readonly SplitContainer listSplit;
        private readonly SplitContainer detailsSplit;
        private readonly Grid soundGrid;
        private readonly Grid referenceGrid;
        private readonly Grid usedByGrid;
        private readonly Panel audioHost;
#pragma warning restore CA2213

        public SoundEventViewer(VrfGuiContext guiContext, IReadOnlyList<(string Name, KVObject Data)> fileEvents)
        {
            this.guiContext = guiContext;
            this.fileEvents = fileEvents;
            fileName = GetIndexPath(guiContext);

            Dock = DockStyle.Fill;
            titleFont = new Font(Font.FontFamily, Font.Size * 1.5f, FontStyle.Bold);
            boldFont = new Font(Font, FontStyle.Bold);
            italicFont = new Font(Font, FontStyle.Italic);
            groupFont = new Font("Segoe UI Semibold", Font.Size);

            searchBox = new SearchBox
            {
                Dock = DockStyle.Top,
                Hint = "Search events by name or type (Ctrl+F)",
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
                Padding = new Padding(0, 4, 0, 4),
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

            noMatchLink = CreateLink("No events match");
            noMatchLink.Dock = DockStyle.Top;
            noMatchLink.Padding = new Padding(4, 8, 0, 8);
            noMatchLink.Text = "No events match. Clear filters";
            noMatchLink.LinkArea = new LinkArea("No events match. ".Length, "Clear filters".Length);
            noMatchLink.Visible = false;
            noMatchLink.LinkClicked += (_, _) => ClearFilters();

            eventGrid = new Grid("Events") { VirtualMode = true, CopiesCell = false };
            eventGrid.Columns.Add(CreateColumn(string.Empty, 1));
            eventGrid.Columns.Add(CreateColumn("Name", 55));
            eventGrid.Columns.Add(CreateColumn("Type", 30));
            eventGrid.Columns.Add(CreateColumn("Duration", 15));
            eventGrid.Columns.Add(CreateColumn("File", 35));
            eventGrid.Columns[0].AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
            eventGrid.Columns[0].Width = this.AdjustForDPI(22);
            eventGrid.Columns[0].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            eventGrid.Columns[0].DefaultCellStyle.ForeColor = Themer.CurrentThemeColors.Attention;
            eventGrid.Columns[3].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            eventGrid.Columns[3].DefaultCellStyle.Padding = new Padding(0, 0, this.AdjustForDPI(16), 0);
            eventGrid.Columns[4].Visible = false;

            foreach (DataGridViewColumn column in eventGrid.Columns)
            {
                column.SortMode = DataGridViewColumnSortMode.Programmatic;
            }

            eventGrid.CellValueNeeded += OnEventCellValueNeeded;
            eventGrid.CellFormatting += OnEventCellFormatting;
            eventGrid.CellToolTipTextNeeded += OnEventCellToolTipTextNeeded;
            eventGrid.SelectionChanged += OnEventSelectionChanged;
            eventGrid.ColumnHeaderMouseClick += OnEventColumnHeaderClick;
            eventGrid.Activated += (_, _) => PlayEvent();

            var leftPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4) };
            leftPanel.Controls.Add(eventGrid);
            leftPanel.Controls.Add(noMatchLink);
            leftPanel.Controls.Add(countLabel);
            leftPanel.Controls.Add(filters);
            leftPanel.Controls.Add(searchBox);

            backButton = CreateToolButton("NavigateBack", "Back", "Back (Alt+Left)", (_, _) => GoBack());
            forwardButton = CreateToolButton("NavigateForward", "Forward", "Forward (Alt+Right)", (_, _) => GoForward());
            playButton = CreateToolButton("AudioPlay", "Play event", "Play event (Space)", (_, _) => PlayEvent());
            stopButton = CreateToolButton("RendererStop", "Stop", "Stop (Esc)", (_, _) => StopPlayback());
            copyButton = CreateToolButton("CopyName", "Copy name", "Copy name (Ctrl+C in the list)", (_, _) => CopyName());
            openButton = CreateToolButton("Open", "Open file", "Open the file that defines this event", (_, _) => OpenDefiningFile());
            copyButton.Margin = copyButton.Margin with { Left = this.AdjustForDPI(16) };

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
            baseLink = CreateLink("Base");
            baseLink.DisabledLinkColor = Themer.CurrentThemeColors.Attention;
            baseLink.LinkClicked += (_, e) => FollowLink((string)e.Link!.LinkData!);

            defaultsBox = new CheckBox
            {
                Text = "Show stack defaults",
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
                RowCount = 2,
            };
            subtitle.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            subtitle.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            subtitle.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            subtitle.Controls.Add(typeLink, 0, 0);
            subtitle.Controls.Add(fileLink, 1, 0);
            subtitle.Controls.Add(defaultsBox, 2, 0);
            subtitle.Controls.Add(baseLink, 0, 1);
            subtitle.SetColumnSpan(baseLink, 3);

            noticeLabel = CreateLink("Notice");
            noticeLabel.Dock = DockStyle.Top;
            noticeLabel.MaximumSize = new Size(4000, 0);
            noticeLabel.Padding = new Padding(0, 2, 0, 4);
            noticeLabel.LinkClicked += (_, e) => ShowEvent(current!.Name, (string)e.Link!.LinkData!);

            captionLabel = new Label
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                MaximumSize = new Size(4000, 0),
                Padding = new Padding(this.AdjustForDPI(12), this.AdjustForDPI(4), 0, this.AdjustForDPI(4)),
                UseMnemonic = false,
                AccessibleName = "Caption",
            };
            captionLabel.Paint += (_, e) =>
            {
                using var bar = new SolidBrush(Themer.CurrentThemeColors.Accent);
                e.Graphics.FillRectangle(bar, 0, this.AdjustForDPI(2), this.AdjustForDPI(3), captionLabel.Height - this.AdjustForDPI(4));
            };

            propertyGrid = new Grid("Properties") { EmptyText = "This event has no keys" };
            propertyGrid.Columns.Add(CreateColumn("Property", 30));
            propertyGrid.Columns.Add(CreateColumn("Value", 50));
            propertyGrid.Columns.Add(CreateColumn("Source", 20));
            propertyGrid.Columns[2].AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCellsExceptHeader;
            propertyGrid.Columns[2].MinimumWidth = this.AdjustForDPI(90);
            propertyGrid.CellPainting += OnPropertyCellPainting;
            propertyGrid.FindLink = FindPropertyLink;

            soundGrid = new Grid("Sounds") { LinkColumn = 0, EmptyText = "This event names no sounds" };
            soundGrid.Columns.Add(CreateColumn("Sound", 70));
            soundGrid.Columns.Add(CreateColumn("Key", 30));
            soundGrid.Columns.Add(CreateColumn("Status", 10));
            soundGrid.SelectionChanged += OnSoundSelectionChanged;

            referenceGrid = new Grid("References") { LinkColumn = 0, EmptyText = "This event names no other events" };
            referenceGrid.Columns.Add(CreateColumn("Event", 70));
            referenceGrid.Columns.Add(CreateColumn("Key", 30));
            referenceGrid.Columns.Add(CreateColumn("Status", 10));

            usedByGrid = new Grid("Used by") { LinkColumn = 0, EmptyText = "No event names this event" };
            usedByGrid.Columns.Add(CreateColumn("Event", 60));
            usedByGrid.Columns.Add(CreateColumn("Key", 10));
            usedByGrid.Columns.Add(CreateColumn("File", 40));

            foreach (var grid in new[] { soundGrid, referenceGrid, usedByGrid })
            {
                grid.Columns[0].DefaultCellStyle.ForeColor = LinkColor;

                foreach (DataGridViewColumn column in grid.Columns)
                {
                    if (column.HeaderText is "Key" or "Status")
                    {
                        column.AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells;
                        column.MinimumWidth = this.AdjustForDPI(60);
                    }
                }
            }

            foreach (var grid in new[] { propertyGrid, soundGrid, referenceGrid, usedByGrid })
            {
                grid.Activated += OnGridRowActivated;
                grid.LinkClicked += (_, target) => FollowLink(target);
            }

            audioHost = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = this.AdjustForDPI(170),
                Visible = false,
                AccessibleName = "Sound player",
            };

            soundsPage = new ThemedTabPage("Sounds");
            soundsPage.Controls.Add(soundGrid);
            soundsPage.Controls.Add(audioHost);
            referencesPage = new ThemedTabPage("References");
            referencesPage.Controls.Add(referenceGrid);
            usedByPage = new ThemedTabPage("Used by");
            usedByPage.Controls.Add(usedByGrid);

            linkTabs = new ThemedTabControl { Dock = DockStyle.Fill, AccessibleName = "Links" };
            linkTabs.TabPages.Add(soundsPage);
            linkTabs.TabPages.Add(referencesPage);
            linkTabs.TabPages.Add(usedByPage);

            detailsSplit = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterWidth = this.AdjustForDPI(6),
                FixedPanel = FixedPanel.Panel2,
            };
            detailsSplit.Panel1.Controls.Add(propertyGrid);
            detailsSplit.Panel2.Controls.Add(linkTabs);

            var rightPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 4, 4, 4) };
            rightPanel.Controls.Add(detailsSplit);
            rightPanel.Controls.Add(captionLabel);
            rightPanel.Controls.Add(noticeLabel);
            rightPanel.Controls.Add(subtitle);
            rightPanel.Controls.Add(toolbar);

            listSplit = new SplitContainer
            {
                Dock = DockStyle.Fill,
                SplitterWidth = this.AdjustForDPI(6),
                FixedPanel = FixedPanel.Panel1,
            };
            listSplit.Panel1.Controls.Add(leftPanel);
            listSplit.Panel2.Controls.Add(rightPanel);
            Controls.Add(listSplit);

            listSplit.SplitterMoving += (_, _) => listSplitMoved = true;
            detailsSplit.SplitterMoving += (_, _) => detailsSplitMoved = true;
            listSplit.SizeChanged += (_, _) => PlaceSplitters();
            detailsSplit.SizeChanged += (_, _) => PlaceSplitters();

            playerTimer.Tick += OnPlayerTick;

            ShowEmptyDetails();
            loading = LoadAsync();
        }

        /// <summary>
        /// Gives the event list 35% of the width and at least 380 pixels, and the links below the properties a fixed height
        /// that grows by the player's while it shows, until the user moves that splitter.
        /// </summary>
        private void PlaceSplitters()
        {
            if (!listSplitMoved && listSplit.Width > 0)
            {
                var distance = Math.Max(this.AdjustForDPI(380), listSplit.Width * 35 / 100);

                if (distance < listSplit.Width - listSplit.SplitterWidth - listSplit.Panel2MinSize)
                {
                    listSplit.SplitterDistance = distance;
                }
            }

            if (!detailsSplitMoved && detailsSplit.Height > 0)
            {
                var distance = detailsSplit.Height - detailsSplit.SplitterWidth - this.AdjustForDPI(220) - (audioPanel != null ? audioHost.Height : 0);

                if (distance > detailsSplit.Panel1MinSize)
                {
                    detailsSplit.SplitterDistance = distance;
                }
            }
        }

        /// <summary>
        /// Gets the index of the sound events and stacks of a file's game, with the English closed captions found there.
        /// It holds only the game's own files, is built once per game off the UI thread, and is kept until every root
        /// context of that game is disposed.
        /// </summary>
        private static Task<(SoundEventIndex Index, List<ValveResourceFormat.ClosedCaptions.ClosedCaptions> Captions)> GetGameIndexAsync(VrfGuiContext context)
        {
            var root = context.RootContext;
            var key = root.GameSearchPaths.Count > 0
                ? string.Join('|', root.GameSearchPaths.Order(StringComparer.OrdinalIgnoreCase))
                : root.FileName;

            lock (GameIndexes)
            {
                if (!GameIndexes.TryGetValue(key, out var entry) || entry.Task.IsFaulted || entry.Task.IsCanceled)
                {
                    entry = (Task.Run(() => BuildIndex(root)), []);
                    root.DisposeAfter(entry.Task);
                    GameIndexes[key] = entry;
                }

                if (entry.Roots.Add(root))
                {
                    root.DisposalCompleted.ContinueWith(_ => ReleaseGameIndex(key, root), TaskScheduler.Default);
                }

                return entry.Task;
            }
        }

        private static void ReleaseGameIndex(string key, VrfGuiContext root)
        {
            lock (GameIndexes)
            {
                if (GameIndexes.TryGetValue(key, out var entry) && entry.Roots.Remove(root) && entry.Roots.Count == 0)
                {
                    GameIndexes.Remove(key);
                }
            }
        }

        private static (SoundEventIndex Index, List<ValveResourceFormat.ClosedCaptions.ClosedCaptions> Captions) BuildIndex(VrfGuiContext context)
        {
            const string EventsExtension = "vsndevts_c";
            const string StacksExtension = "vsndstck_c";

            var files = context.EnumerateGameFiles(EventsExtension, StacksExtension, "dat").ToList();
            var eventFiles = files.Where(static file => file.EndsWith(EventsExtension, StringComparison.OrdinalIgnoreCase))
                .Select(static file => file[..^GameFileLoader.CompiledFileSuffix.Length]);
            List<string> loadOrder = [.. SoundEventIndex.GetEventFilesInLoadOrder(context.LoadGameFile, eventFiles).Select(static file => file + GameFileLoader.CompiledFileSuffix)];
            List<string> stackFiles = [.. files.Where(static file => file.EndsWith(StacksExtension, StringComparison.OrdinalIgnoreCase))];
            var index = SoundEventIndex.Build(context.LoadGameFile, loadOrder, stackFiles, context.SoundEventMaxBaseVisits, VrfGuiContext.Logger);
            var captions = new List<ValveResourceFormat.ClosedCaptions.ClosedCaptions>();

            foreach (var file in files.Where(static file => file.EndsWith("english.dat", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    using var stream = context.GetFileStream(file);

                    if (stream == null)
                    {
                        continue;
                    }

                    var fileCaptions = new ValveResourceFormat.ClosedCaptions.ClosedCaptions();
                    fileCaptions.Read(file, stream);
                    captions.Add(fileCaptions);
                }
                catch (InvalidDataException)
                {
                    // Not every .dat file is a caption file
                }
                catch (Exception e)
                {
                    Log.Error(nameof(SoundEventViewer), $"Failed to read captions from \"{file}\": {e}");
                }
            }

            return (index, captions);
        }

        /// <summary>
        /// Gets the index a file's tab shows: the game's index, with the sound event files that the file's packages, map
        /// packages and addons hold besides the game's own, and the file itself when it is a loose file outside of those,
        /// loaded after the game's files on a copy. The copy is shared by the files of a package, and kept until it closes.
        /// </summary>
        /// <param name="context">The context of the file.</param>
        /// <param name="fileEvents">The events of the file, when it is a sound event file.</param>
        public static Task<(SoundEventIndex Index, List<ValveResourceFormat.ClosedCaptions.ClosedCaptions> Captions)> GetIndexAsync(
            VrfGuiContext context, IReadOnlyList<(string Name, KVObject Data)>? fileEvents = null)
        {
            var looseFile = context.ParentGuiContext == null;
            var owner = context.ParentGuiContext ?? context;

            lock (PackageIndexes)
            {
                if (!looseFile && PackageIndexes.TryGetValue(owner, out var cached) && !cached.IsFaulted && !cached.IsCanceled)
                {
                    return cached;
                }

                var task = BuildViewIndexAsync(context, looseFile ? fileEvents : null);
                context.DisposeAfter(task);

                if (!looseFile)
                {
                    PackageIndexes[owner] = task;
                    owner.DisposalCompleted.ContinueWith(_ =>
                    {
                        lock (PackageIndexes)
                        {
                            PackageIndexes.Remove(owner);
                        }
                    }, TaskScheduler.Default);
                }

                return task;
            }
        }

        private static async Task<(SoundEventIndex Index, List<ValveResourceFormat.ClosedCaptions.ClosedCaptions> Captions)> BuildViewIndexAsync(
            VrfGuiContext context, IReadOnlyList<(string Name, KVObject Data)>? fileEvents)
        {
            var (gameIndex, captions) = await GetGameIndexAsync(context).ConfigureAwait(false);

            return await Task.Run(() =>
            {
                var ownFiles = context.EnumerateFiles("vsndevts_c").Where(file =>
                {
                    var found = context.FindFile(file, logNotFound: false);
                    return !context.RootContext.IsGameLocation(found.Package, found.PathOnDisk);
                }).ToList();
                var fileName = GetIndexPath(context);
                var addFile = fileEvents != null && !ownFiles.Contains(fileName, StringComparer.OrdinalIgnoreCase);

                if (ownFiles.Count == 0 && !addFile)
                {
                    return (gameIndex, captions);
                }

                var shadowedFiles = ownFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (addFile)
                {
                    shadowedFiles.Add(fileName);
                }

                var index = new SoundEventIndex(gameIndex, shadowedFiles);

                foreach (var file in ownFiles)
                {
                    try
                    {
                        using var resource = context.FileLoaderNoCache.LoadFile(file);

                        if (resource?.DataBlock is SoundEventScript script)
                        {
                            index.AddEvents(file, script.Events);
                        }
                    }
                    catch (Exception e)
                    {
                        Log.Error(nameof(SoundEventViewer), $"Failed to load sound events from \"{file}\": {e}");
                    }
                }

                if (addFile)
                {
                    index.AddEvents(fileName, fileEvents!);
                }

                index.IndexReferences();

                return (index, captions);
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// The path a file has in the index: its path in its package, or for a file on disk, its path in the game folder
        /// that holds it. A file outside every game folder keeps its full path.
        /// </summary>
        private static string GetIndexPath(VrfGuiContext context)
        {
            if (!Path.IsPathRooted(context.FileName))
            {
                return context.FileName;
            }

            foreach (var folder in context.RootContext.GameSearchPaths)
            {
                var relative = Path.GetRelativePath(folder, context.FileName);

                if (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
                {
                    return relative.Replace(Path.DirectorySeparatorChar, '/');
                }
            }

            return context.FileName;
        }

        /// <summary>
        /// Creates a list of the sound events that play a sound, filled once the index is built. Double-clicking an event
        /// opens the file that defines it.
        /// </summary>
        public static Control CreateSoundUsersList(VrfGuiContext context)
        {
            var grid = new Grid("Used by events")
            {
                Dock = DockStyle.Bottom,
                LinkColumn = 0,
                EmptyText = "Finding events that use this sound...",
            };
            grid.Height = grid.AdjustForDPI(150);
            grid.Columns.Add(CreateColumn("Used by event", 55));
            grid.Columns.Add(CreateColumn("File", 45));
            grid.Columns[0].DefaultCellStyle.ForeColor = LinkColor;
            grid.Activated += (_, rowIndex) => Open(grid.Rows[rowIndex].Tag);
            grid.LinkClicked += (_, target) => Open(target);

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

                    foreach (var name in index.GetEventsUsingSound(GetIndexPath(context)).Order(StringComparer.OrdinalIgnoreCase))
                    {
                        var file = index.GetDefinitions(name)[^1].File;
                        var row = grid.Rows[grid.Rows.Add(name, ShortFileName(file))];
                        row.Tag = (name, file);
                        row.Cells[1].ToolTipText = file;
                    }

                    grid.EmptyText = "No sound event plays this sound";
                    grid.CurrentCell = null;
                    grid.ClearSelection();
                }
                catch (Exception e)
                {
                    Log.Error(nameof(SoundEventViewer), $"Failed to index sound events for \"{context.DisplayPath}\": {e}");
                }
            }

            void Open(object? target)
            {
                if (target is (string name, string file))
                {
                    _ = OpenEventAsync(context, file, name);
                }
            }
        }

        /// <summary>The path of a sound event file inside <c>soundevents/</c>, without its extension.</summary>
        private static string ShortFileName(string file)
        {
            var name = file.StartsWith("soundevents/", StringComparison.OrdinalIgnoreCase) ? file["soundevents/".Length..] : file;
            return name.EndsWith(".vsndevts_c", StringComparison.OrdinalIgnoreCase) ? name[..^".vsndevts_c".Length]
                : name.EndsWith(".vsndevts", StringComparison.OrdinalIgnoreCase) ? name[..^".vsndevts".Length]
                : name;
        }

        private async Task LoadAsync()
        {
            try
            {
                (index, captions) = await GetIndexAsync(guiContext, fileEvents).ConfigureAwait(true);

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

        private static List<EventRow> CreateRows(SoundEventIndex loadedIndex)
        {
            var result = new List<EventRow>(loadedIndex.Count);

            foreach (var name in loadedIndex.EventNames)
            {
                var definitions = loadedIndex.GetDefinitions(name);
                var duplicate = definitions.Count > 1;

                foreach (var (file, data) in definitions)
                {
                    result.Add(CreateRow(loadedIndex, name, file, data, duplicate));
                }
            }

            return result;
        }

        private static EventRow CreateRow(SoundEventIndex loadedIndex, string name, string file, KVObject data, bool duplicate)
        {
            var (resolved, problems, rejected) = loadedIndex.Resolve(data);
            var duration = resolved.FirstOrDefault(static r => r.Key == "vsnd_duration").Value is { } durationValue && IsNumber(durationValue)
                ? durationValue.ToSingle(CultureInfo.InvariantCulture)
                : -1f;
            var missingBase = SoundEventIndex.GetBases(data).Any(b => loadedIndex.Find(b.Name) == null);

            return new EventRow(name, file, data, GetString(resolved, "type"), duration, duplicate, problems.Count > 0, rejected, missingBase);
        }

        private bool SoundExists(string sound)
        {
            var found = guiContext.FindFile(sound.Replace('\\', '/') + GameFileLoader.CompiledFileSuffix, logNotFound: false);
            return found.PathOnDisk != null || found.PackageEntry != null;
        }

        private async Task CheckSoundsAsync()
        {
            var loadedIndex = index!;
            var checkedRows = rows;

            soundsChecked = false;
            countLabel.Text = "Checking sounds...";

            await Task.Run(() =>
            {
                var exists = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

                foreach (var row in checkedRows)
                {
                    foreach (var (_, sound) in loadedIndex.GetSoundReferences(row.Data))
                    {
                        if (!exists.TryGetValue(sound, out var soundExists))
                        {
                            soundExists = SoundExists(sound);
                            exists[sound] = soundExists;
                        }

                        row.MissingSound |= !soundExists;
                    }
                }
            }).ConfigureAwait(true);

            soundsChecked = true;

            if (!IsDisposed && statusBox.SelectedIndex == 4)
            {
                ApplyFilter();
            }
        }

        private void OnScopeChanged()
        {
            var selectedType = typeFilters[Math.Max(0, typeBox.SelectedIndex)];
            var types = ScopedRows().GroupBy(static r => r.Type, StringComparer.OrdinalIgnoreCase)
                .OrderBy(static g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            updating = true;
            typeBox.BeginUpdate();
            typeBox.Items.Clear();
            typeFilters.Clear();
            typeBox.Items.Add("All types");
            typeFilters.Add(null);

            foreach (var type in types)
            {
                typeBox.Items.Add(string.Create(CultureInfo.InvariantCulture, $"{(type.Key.Length == 0 ? "(no type)" : type.Key)} ({type.Count():N0})"));
                typeFilters.Add(type.Key);
            }

            typeBox.SelectedIndex = Math.Max(0, typeFilters.IndexOf(selectedType));
            typeBox.EndUpdate();
            updating = false;

            eventGrid.Columns[3].Visible = ScopedRows().Any(static r => r.Duration >= 0f);
            eventGrid.Columns[4].Visible = scopeBox.SelectedIndex == 1;
            ApplyFilter();
        }

        private void ClearFilters()
        {
            updating = true;
            searchBox.Text = string.Empty;
            typeBox.SelectedIndex = 0;
            statusBox.SelectedIndex = 0;
            updating = false;
            ApplyFilter();
        }

        private IEnumerable<EventRow> ScopedRows()
            => scopeBox.SelectedIndex == 1 ? rows : rows.Where(r => r.File.Equals(fileName, StringComparison.OrdinalIgnoreCase));

        private void ApplyFilter()
        {
            if (updating || index == null)
            {
                return;
            }

            var status = statusBox.SelectedIndex;

            if (status == 4 && soundsChecked != true)
            {
                if (soundsChecked == null)
                {
                    _ = CheckSoundsAsync();
                }

                return;
            }

            var search = searchBox.Text.Trim();
            var type = typeFilters[Math.Max(0, typeBox.SelectedIndex)];

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
                ? string.Create(CultureInfo.InvariantCulture, $"{total:N0} events")
                : string.Create(CultureInfo.InvariantCulture, $"{filteredRows.Count:N0} of {total:N0} events");
            noMatchLink.Visible = filteredRows.Count == 0;

            RefreshEventGrid();
        }

        private void SortRows()
        {
            Comparison<EventRow> comparison = sortColumn switch
            {
                0 => static (a, b) => b.HasWarning.CompareTo(a.HasWarning),
                2 => static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Type, b.Type),
                3 => static (a, b) => a.Duration.CompareTo(b.Duration),
                4 => static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.File, b.File),
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
            updating = true;
            eventGrid.RowCount = 0;
            eventGrid.RowCount = filteredRows.Count;
            updating = false;

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
                0 => row.HasWarning ? "⚠" : string.Empty,
                1 => row.Name,
                2 => row.Type.Length > 0 ? row.Type : "(no type)",
                3 => row.Duration >= 0f ? row.Duration.ToString("0.00 s", CultureInfo.InvariantCulture) : string.Empty,
                _ => ShortFileName(row.File),
            };
        }

        private void OnEventCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex >= filteredRows.Count || e.CellStyle == null)
            {
                return;
            }

            var row = filteredRows[e.RowIndex];

            if ((e.ColumnIndex == 1 && !row.Playable) || (e.ColumnIndex == 2 && row.Type.Length == 0))
            {
                e.CellStyle.ForeColor = Themer.CurrentThemeColors.ContrastSoft;
            }
        }

        private void OnEventCellToolTipTextNeeded(object? sender, DataGridViewCellToolTipTextNeededEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= filteredRows.Count)
            {
                return;
            }

            var row = filteredRows[e.RowIndex];

            e.ToolTipText = e.ColumnIndex switch
            {
                0 => string.Join(Environment.NewLine, new[]
                {
                    row.Rejected ? "The engine does not play this event." : row.HasProblems ? "The engine skips some of its bases." : null,
                    row.MissingBase ? "A base event is not loaded." : null,
                    row.MissingSound ? "A sound it names is not shipped." : null,
                }.OfType<string>()),
                1 when !row.Playable => row.Rejected ? "The engine does not play this event" : "This event cannot be played here",
                4 => row.File,
                _ => string.Empty,
            };
        }

        private void OnEventSelectionChanged(object? sender, EventArgs e)
        {
            if (updating || eventGrid.CurrentCell == null || eventGrid.CurrentCell.RowIndex >= filteredRows.Count)
            {
                return;
            }

            var row = filteredRows[eventGrid.CurrentCell.RowIndex];

            if (row != current)
            {
                ShowRow(row, addToHistory: false);
            }
        }

        private void OnEventColumnHeaderClick(object? sender, DataGridViewCellMouseEventArgs e)
        {
            sortDescending = sortColumn == e.ColumnIndex && !sortDescending;
            sortColumn = e.ColumnIndex;
            SortRows();
            RefreshEventGrid();
        }

        /// <summary>Completes once the events are indexed and listed.</summary>
        public Task Loaded => loading;

        /// <summary>
        /// Shows an event in the details, as following a link to it does. The definition in <paramref name="file"/> is shown
        /// when given, otherwise the one that wins.
        /// </summary>
        /// <returns>Whether the event is known.</returns>
        public bool ShowEvent(string name, string? file = null)
        {
            var row = file != null
                ? rows.Find(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && r.File.Equals(file, StringComparison.OrdinalIgnoreCase))
                : rows.FindLast(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

            if (row == null)
            {
                return false;
            }

            if (row != current)
            {
                ShowRow(row, addToHistory: true);
                SelectCurrentInList();
            }

            return true;
        }

        /// <summary>Selects one of the tabs below the properties: 0 for sounds, 1 for references and 2 for the events that use this one.</summary>
        public void ShowLinksTab(int tabIndex) => linkTabs.SelectedIndex = tabIndex;

        private void SelectCurrentInList()
        {
            var rowIndex = current == null ? -1 : filteredRows.IndexOf(current);

            updating = true;
            eventGrid.ClearSelection();

            if (rowIndex >= 0)
            {
                var visible = eventGrid.DisplayedRowCount(includePartialRow: false);

                if (rowIndex < eventGrid.FirstDisplayedScrollingRowIndex || rowIndex >= eventGrid.FirstDisplayedScrollingRowIndex + visible)
                {
                    eventGrid.FirstDisplayedScrollingRowIndex = Math.Max(0, rowIndex - visible / 2);
                }

                eventGrid.CurrentCell = eventGrid.Rows[rowIndex].Cells[1];
                eventGrid.Rows[rowIndex].Selected = true;
            }

            updating = false;
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

            if (rows.Find(r => r.Name == name && r.File == file) is not { } row)
            {
                return;
            }

            to.Add((current.Name, current.File));
            ShowRow(row, addToHistory: false);
            SelectCurrentInList();
        }

        private void ShowRow(EventRow row, bool addToHistory)
        {
            if (addToHistory && current != null)
            {
                backHistory.Add((current.Name, current.File));
                forwardHistory.Clear();
            }

            current = row;

            if (scopeBox.SelectedIndex == 0 && !row.File.Equals(fileName, StringComparison.OrdinalIgnoreCase))
            {
                scopeBox.SelectedIndex = 1;
            }

            titleLabel.Text = row.Name;
            toolTip.SetToolTip(titleLabel, row.Name);
            SetLink(typeLink, "Stack: ", row.Type.Length > 0 ? row.Type : "(no type)", index!.GetStackFile(row.Type) != null);
            SetLink(fileLink, "File: ", row.File, enabled: true);
            ShowBases(row);

            var (resolved, problems, _) = index.Resolve(row.Data);
            var notice = new StringBuilder();
            var noticeLinks = new List<(int Start, int Length, string File)>();
            List<string> definitionFiles = [.. index.GetDefinitions(row.Name).Select(static d => d.File)];

            if (definitionFiles.Count > 1)
            {
                notice.Append("Also defined in ");

                foreach (var file in definitionFiles.Where(f => f != row.File))
                {
                    notice.Append(noticeLinks.Count > 0 ? ", " : string.Empty);
                    noticeLinks.Add((notice.Length, file.Length, file));
                    notice.Append(file);
                }

                notice.Append(definitionFiles[^1] == row.File
                    ? ". This definition is used, as it loads last."
                    : $". The definition in {definitionFiles[^1]} is used, as it loads last.");
            }

            List<string> lines = [.. problems];
            lines.AddRange(SoundEventIndex.GetBases(row.Data).Where(b => index.Find(b.Name) == null)
                .Select(static b => $"Base {b.Name} is not loaded, so the keys it would give are missing."));
            var hasProblems = lines.Count > 0;
            var playsSounds = resolved.Any(r => GetLinks(r.Key, r.Value).Count > 0);

            if (!row.Playable && !row.Rejected)
            {
                lines.Add(row.Type.Length == 0
                    ? "Has no type, so it cannot be played as an event."
                    : index.GetSoundReferences(row.Data).Count > 0
                        ? $"Events of type {row.Type} cannot be played here. Play its sounds below."
                        : $"Events of type {row.Type} cannot be played here.");
            }

            foreach (var line in lines)
            {
                notice.Append(notice.Length > 0 ? Environment.NewLine : string.Empty).Append(line);
            }

            noticeLabel.Text = notice.ToString();
            noticeLabel.Links.Clear();

            foreach (var (start, length, file) in noticeLinks)
            {
                noticeLabel.Links.Add(start, length, file);
            }

            noticeLabel.Visible = notice.Length > 0;
            noticeLabel.ForeColor = hasProblems ? Themer.CurrentThemeColors.Attention : Themer.CurrentThemeColors.ContrastSoft;

            var closedCaption = captions.Select(c => c[row.Name.ToLowerInvariant()]).FirstOrDefault(static c => c != null);
            captionText = CaptionMarkupRegex().Replace(closedCaption?.Text ?? GetString(resolved, "line_text"), string.Empty).Trim();
            captionLabel.Text = captionText;
            captionLabel.Visible = captionText.Length > 0;
            captionLabel.ForeColor = Themer.CurrentThemeColors.Contrast;

            playButton.Enabled = row.Playable && playsSounds;
            toolTip.SetToolTip(playButton, !row.Playable ? "This event cannot be played here" : !playsSounds ? "This event plays no sounds" : "Play event (Space)");
            backButton.Enabled = backHistory.Count > 0;
            forwardButton.Enabled = forwardHistory.Count > 0;
            copyButton.Enabled = true;
            openButton.Enabled = true;

            ShowProperties();
            ShowLinks(row);
        }

        private static void SetLink(LinkLabel label, string prefix, string text, bool enabled)
        {
            label.Text = prefix + text;
            label.LinkArea = new LinkArea(prefix.Length, text.Length);
            label.Links[0].Enabled = enabled;
        }

        /// <summary>
        /// Shows the chain of bases, following a single string base down to its own bases. Fragments of an array base are
        /// listed with the field they apply to, and end the chain.
        /// </summary>
        private void ShowBases(EventRow row)
        {
            var text = new StringBuilder("Base: ");
            var links = new List<(int Start, int Length, string Name, bool Loaded)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { row.Name };
            var bases = SoundEventIndex.GetBases(row.Data);

            while (bases.Count > 0)
            {
                text.Append(links.Count > 0 ? " > " : string.Empty);

                for (var i = 0; i < bases.Count; i++)
                {
                    var (name, field) = bases[i];
                    text.Append(i > 0 ? ", " : string.Empty);
                    links.Add((text.Length, name.Length, name, index!.Find(name) != null));
                    text.Append(name);

                    if (field != null)
                    {
                        text.Append(" (for ").Append(field).Append(')');
                    }
                }

                if (bases.Count > 1 || bases[0].Field != null || !seen.Add(bases[0].Name) || index!.Find(bases[0].Name) is not { } baseEvent)
                {
                    break;
                }

                bases = SoundEventIndex.GetBases(baseEvent);
            }

            baseLink.Text = text.ToString();
            baseLink.Links.Clear();

            foreach (var (start, length, name, loaded) in links)
            {
                baseLink.Links.Add(start, length, name).Enabled = loaded;
            }

            baseLink.Visible = links.Count > 0;
        }

        /// <summary>Gets the sounds and the known events that a key's value names.</summary>
        private List<string> GetLinks(string key, KVObject value)
        {
            var holder = KVObject.Collection();
            holder.Add(key, value);

            return [.. index!.GetSoundReferences(holder).Select(static r => r.Sound), .. index.GetEventReferences(current!.Name, holder).Select(static r => r.Event)];
        }

        private void FollowLink(object target)
        {
            switch (target)
            {
                case string sound when sound.EndsWith(".vsnd", StringComparison.OrdinalIgnoreCase):
                    var soundRow = soundGrid.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => sound.Equals(r.Tag as string, StringComparison.OrdinalIgnoreCase));

                    if (soundRow == null)
                    {
                        Resource.OpenExternalReference(guiContext, sound);
                        break;
                    }

                    linkTabs.SelectedTab = soundsPage;

                    if (soundGrid.CurrentRow != soundRow)
                    {
                        soundGrid.CurrentCell = soundRow.Cells[0];
                    }
                    else if (audioPanel == null)
                    {
                        PlaySound(soundRow.Index, autoPlay: false);
                    }

                    break;

                case string name:
                    ShowEvent(name);
                    break;
            }
        }

        private void ShowEmptyDetails()
        {
            titleLabel.Text = string.Empty;
            typeLink.Text = string.Empty;
            fileLink.Text = string.Empty;
            baseLink.Visible = false;
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
            var fields = current.Type.Length > 0 ? index.GetStackFields(current.Type) : [];
            var groups = new List<(string Name, List<(string Key, KVObject? Value, string Source, ValueSource Kind, bool Curve)> Rows)>();
            var fieldNames = new Dictionary<string, KVObject>(StringComparer.OrdinalIgnoreCase);
            var byFieldName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var (name, field, _) in fields)
            {
                fieldNames[name] = field;
            }

            // Keys of a scoped sub-stack are named after its field, keys of an unscoped one keep their own names
            for (var i = 0; i < resolved.Count; i++)
            {
                var (key, _, _, subStack) = resolved[i];

                if (subStack == null)
                {
                    byFieldName.TryAdd(key, i);
                }
                else if (fieldNames.TryGetValue(subStack, out var opvar) && opvar.GetBooleanProperty("use_public_opvar_scope"))
                {
                    byFieldName.TryAdd($"{subStack}.{key}", i);
                }
            }

            var shown = new HashSet<int>();
            var captionLine = resolved.FindIndex(r => r.Key.Equals("line_text", StringComparison.OrdinalIgnoreCase) && r.Field == null
                && r.Value.ValueType == KVValueType.String && CaptionMarkupRegex().Replace((string)r.Value, string.Empty).Trim() == captionText);

            if (captionLine >= 0)
            {
                shown.Add(captionLine);
            }

            List<(string Key, KVObject? Value, string Source, ValueSource Kind, bool Curve)> Group(string name)
            {
                var group = groups.Find(g => g.Name == name).Rows;

                if (group == null)
                {
                    group = [];
                    groups.Add((name, group));
                }

                return group;
            }

            (string Key, KVObject? Value, string Source, ValueSource Kind, bool Curve) Row(int i, string name, bool curve)
            {
                var (_, value, source, subStack) = resolved[i];
                var origin = source == null ? string.Empty : subStack == null ? source : $"{source} via {subStack}";
                return (name, value, origin, source == null ? ValueSource.Own : ValueSource.Inherited, curve);
            }

            foreach (var (name, field, stack) in fields)
            {
                var group = Group(field.GetSubCollection("_system_properties")?.GetStringProperty("property_group") is { Length: > 0 } propertyGroup ? propertyGroup : "General");
                var dataType = field.GetStringProperty("data_type");
                var curve = dataType == "float6";

                if (byFieldName.TryGetValue(name, out var i))
                {
                    if (shown.Add(i))
                    {
                        group.Add(Row(i, name, curve));
                    }
                }
                else if (defaultsBox.Checked && dataType != "public_opvar")
                {
                    var source = stack.Equals(current.Type, StringComparison.OrdinalIgnoreCase) ? "default" : $"default (imported from {stack})";
                    group.Add((name, field.TryGetValue("value", out var defaultValue) ? defaultValue : null, source, ValueSource.Default, curve));
                }
            }

            for (var i = 0; i < resolved.Count; i++)
            {
                var (key, _, _, subStack) = resolved[i];

                if (shown.Contains(i) || key.Equals("type", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var groupName = subStack != null && fieldNames.ContainsKey(subStack) ? subStack
                    : EventKeys.Contains(key) ? "Event"
                    : fields.Count > 0 ? "Not read by this stack" : "Keys";
                var curve = subStack != null && fieldNames.TryGetValue(key, out var field) && field.GetStringProperty("data_type") == "float6";
                Group(groupName).Add(Row(i, key, curve));
            }

            var values = groups.SelectMany(static g => g.Rows).Where(static r => r.Value != null)
                .DistinctBy(static r => r.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(static r => r.Key, static r => r.Value!, StringComparer.OrdinalIgnoreCase);

            propertyGrid.SuspendLayout();
            propertyGrid.Rows.Clear();

            foreach (var (groupName, groupRows) in groups)
            {
                if (groupRows.Count == 0)
                {
                    continue;
                }

                var header = propertyGrid.Rows[propertyGrid.Rows.Add(groupName, string.Empty, string.Empty)];
                header.Tag = ValueSource.Group;
                header.Height = this.AdjustForDPI(30);

                foreach (var (key, value, source, kind, isCurve) in groupRows)
                {
                    var text = value == null ? string.Empty : kind == ValueSource.Default ? FormatDefault(value) : FormatValue(value);
                    var gridRow = propertyGrid.Rows[propertyGrid.Rows.Add(key, text, source)];
                    gridRow.Tag = kind;
                    gridRow.Cells[1].ToolTipText = value == null ? string.Empty : kind == ValueSource.Default ? text : FormatValue(value, maxLength: 4000);

                    if (isCurve && value != null && ParseCurve(value) is var (curve, keyPoints))
                    {
                        var dimmed = values.TryGetValue($"use_{key}", out var flag) && !IsTrue(flag);
                        gridRow.Cells[1].Tag = (curve, keyPoints, dimmed);
                        gridRow.Height = this.AdjustForDPI(44);
                    }
                    else if (value != null && kind != ValueSource.Default && GetLinks(key, value) is { Count: > 0 } links)
                    {
                        gridRow.Cells[1].Tag = GetSegments(value, links);
                    }

                    switch (kind)
                    {
                        case ValueSource.Own:
                            gridRow.Cells[1].Style.Font = boldFont;
                            break;
                        case ValueSource.Inherited:
                            gridRow.Cells[2].Style.ForeColor = LinkColor;
                            gridRow.Cells[2].ToolTipText = $"Inherited from {source}";
                            break;
                        case ValueSource.Default:
                            gridRow.DefaultCellStyle.Font = italicFont;
                            gridRow.DefaultCellStyle.ForeColor = Themer.CurrentThemeColors.ContrastSoft;
                            break;
                    }
                }
            }

            propertyGrid.ResumeLayout();
            propertyGrid.ClearSelection();
        }

        private static bool IsTrue(KVObject value) => value.ValueType switch
        {
            KVValueType.Boolean => (bool)value,
            KVValueType.String => (string)value is not ("false" or "0" or ""),
            _ => !IsNumber(value) || value.ToDouble(CultureInfo.InvariantCulture) != 0,
        };

        /// <summary>Formats a stack default, which holds numbers as text with six decimals, like the event's own numbers.</summary>
        private static string FormatDefault(KVObject value)
            => value.ValueType == KVValueType.String && double.TryParse((string)value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                ? number.ToString("0.######", CultureInfo.InvariantCulture)
                : FormatValue(value);

        /// <summary>Splits a value that names sounds or events into its text and the names to link.</summary>
        private static List<(string Text, bool Link)> GetSegments(KVObject value, List<string> links)
        {
            var segments = new List<(string Text, bool Link)>();
            var items = value.ValueType == KVValueType.Array ? value.Values.ToList() : [value];

            if (value.ValueType == KVValueType.Array)
            {
                segments.Add(("[", false));
            }

            for (var i = 0; i < items.Count; i++)
            {
                if (i > 0)
                {
                    segments.Add((", ", false));
                }

                var text = FormatValue(items[i]);
                segments.Add((text, items[i].ValueType == KVValueType.String && links.Contains(text)));
            }

            if (value.ValueType == KVValueType.Array)
            {
                segments.Add(("]", false));
            }

            return segments;
        }

        private IEnumerable<(Rectangle Bounds, string Text, bool Link)> LayoutSegments(List<(string Text, bool Link)> segments, Rectangle cell, Font font)
        {
            var x = cell.Left + this.AdjustForDPI(2);

            foreach (var (text, link) in segments)
            {
                if (x >= cell.Right)
                {
                    yield break;
                }

                var width = System.Windows.Forms.TextRenderer.MeasureText(text, font, Size.Empty, SegmentFlags).Width;
                yield return (new Rectangle(x, cell.Top, Math.Min(width, cell.Right - x), cell.Height), text, link);
                x += width;
            }
        }

        private string? FindPropertyLink(int rowIndex, int columnIndex, Point location)
        {
            var row = propertyGrid.Rows[rowIndex];

            if (columnIndex == 2 && row.Tag is ValueSource.Inherited)
            {
                return ((string)row.Cells[2].Value!).Split(" via ")[0];
            }

            if (columnIndex != 1 || row.Cells[1].Tag is not List<(string Text, bool Link)> segments)
            {
                return null;
            }

            var cell = propertyGrid.GetCellDisplayRectangle(1, rowIndex, cutOverflow: false);
            var font = row.Cells[1].InheritedStyle.Font ?? propertyGrid.Font;

            return LayoutSegments(segments, new Rectangle(Point.Empty, cell.Size), font).FirstOrDefault(s => s.Link && s.Bounds.Contains(location)).Text;
        }

        [GeneratedRegex("<[^<>]*>")]
        private static partial Regex CaptionMarkupRegex();

        private static bool IsNumber(KVObject value) => value.ValueType is KVValueType.FloatingPoint or KVValueType.FloatingPoint64
            or KVValueType.Int16 or KVValueType.Int32 or KVValueType.Int64 or KVValueType.UInt16 or KVValueType.UInt32 or KVValueType.UInt64;

        private static string GetString(List<(string Key, KVObject Value, string? Source, string? Field)> resolved, string key)
        {
            var value = resolved.FirstOrDefault(r => r.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;
            return value?.ValueType == KVValueType.String ? (string)value : string.Empty;
        }

        private static string FormatValue(KVObject value, int maxLength = 300)
        {
            var text = new StringBuilder();
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

        /// <summary>
        /// Reads a float6 curve, either compiled as an array of points or written as a stack default that holds each point
        /// as text. Returns null for anything else.
        /// </summary>
        private static (SoundEventCurve Curve, PointF[] Keys)? ParseCurve(KVObject value)
        {
            var points = KVObject.Array();
            var keys = new List<PointF>();

            foreach (var point in value.ValueType is KVValueType.Array or KVValueType.Collection ? value.Values : [])
            {
                var numbers = point.ValueType switch
                {
                    KVValueType.Array when point.Values.All(IsNumber) => point.Values.Select(static n => n.ToSingle(CultureInfo.InvariantCulture)).ToArray(),
                    KVValueType.String => ParseNumbers((string)point),
                    _ => null,
                };

                if (numbers is not { Length: 6 })
                {
                    return null;
                }

                points.Add(KVObject.Array(numbers.Select(static n => (KVObject)n)));
                keys.Add(new PointF(numbers[0], numbers[1]));
            }

            if (points.Count == 0)
            {
                return null;
            }

            var holder = KVObject.Collection();
            holder.Add("curve", points);
            return SoundEventCurve.Parse(holder, "curve") is { } curve ? (curve, [.. keys]) : null;

            static float[]? ParseNumbers(string text)
            {
                var parts = text.Trim('[', ']', ' ').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var numbers = new float[parts.Length];

                for (var i = 0; i < parts.Length; i++)
                {
                    if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i]))
                    {
                        return null;
                    }
                }

                return numbers;
            }
        }

        private void OnPropertyCellPainting(object? sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0 || e.Graphics == null || e.CellStyle == null)
            {
                return;
            }

            var row = propertyGrid.Rows[e.RowIndex];

            if (row.Tag is ValueSource.Group)
            {
                using var back = new SolidBrush(Themer.CurrentThemeColors.App);
                using var border = new Pen(Themer.CurrentThemeColors.Border);
                e.Graphics.FillRectangle(back, e.CellBounds);
                e.Graphics.DrawLine(border, e.CellBounds.Left, e.CellBounds.Top, e.CellBounds.Right, e.CellBounds.Top);

                if (e.ColumnIndex == 0)
                {
                    var text = Rectangle.FromLTRB(e.CellBounds.Left + this.AdjustForDPI(4), e.CellBounds.Top, e.CellBounds.Right, e.CellBounds.Bottom - this.AdjustForDPI(4));
                    System.Windows.Forms.TextRenderer.DrawText(e.Graphics, (string?)e.Value, groupFont, text, Themer.CurrentThemeColors.ContrastSoft,
                        TextFormatFlags.Left | TextFormatFlags.Bottom | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                }

                e.Handled = true;
                return;
            }

            if (e.ColumnIndex != 1)
            {
                return;
            }

            var selected = (e.State & DataGridViewElementStates.Selected) != 0;
            var font = e.CellStyle.Font ?? propertyGrid.Font;

            switch (row.Cells[1].Tag)
            {
                case List<(string Text, bool Link)> segments:
                    e.Paint(e.CellBounds, DataGridViewPaintParts.Background | DataGridViewPaintParts.SelectionBackground);

                    foreach (var (bounds, text, link) in LayoutSegments(segments, e.CellBounds, font))
                    {
                        var color = selected ? e.CellStyle.SelectionForeColor : link ? LinkColor : e.CellStyle.ForeColor;
                        System.Windows.Forms.TextRenderer.DrawText(e.Graphics, text, font, bounds, color, SegmentFlags);
                    }

                    e.Handled = true;
                    break;

                case (SoundEventCurve curve, PointF[] keys, bool dimmed):
                    e.Paint(e.CellBounds, DataGridViewPaintParts.Background | DataGridViewPaintParts.SelectionBackground);
                    DrawCurve(e.Graphics, e.CellBounds, curve, keys, dimmed, font);
                    e.Handled = true;
                    break;
            }
        }

        private void DrawCurve(Graphics graphics, Rectangle cell, SoundEventCurve curve, PointF[] keys, bool dimmed, Font font)
        {
            var padding = this.AdjustForDPI(4);
            var plot = Rectangle.Inflate(cell, -padding, -padding);
            var labelWidth = Math.Min(plot.Width / 2, this.AdjustForDPI(150));
            var labelArea = new Rectangle(plot.Right - labelWidth, plot.Top, labelWidth, plot.Height);
            plot.Width -= labelWidth + padding;

            var samples = Math.Max(2, plot.Width / 2);
            var minX = curve.MinX;
            var maxX = Math.Max(curve.MaxX, minX + 0.0001f);
            var values = new float[samples];

            for (var i = 0; i < samples; i++)
            {
                values[i] = curve.Evaluate(float.Lerp(minX, maxX, i / (samples - 1f)));
            }

            var minY = MathF.Min(values.Min(), keys.Min(static k => k.Y));
            var maxY = MathF.Max(values.Max(), keys.Max(static k => k.Y));
            var constant = maxY - minY <= MathF.Max(0.0001f, 0.01f * MathF.Max(MathF.Abs(minY), MathF.Abs(maxY)));
            var color = dimmed ? Themer.CurrentThemeColors.ContrastSoft : Themer.CurrentThemeColors.Accent;

            float ToX(float x) => plot.Left + (x - minX) / (maxX - minX) * plot.Width;
            float ToY(float y) => constant ? plot.Top + plot.Height / 2f : plot.Bottom - (y - minY) / (maxY - minY) * plot.Height;

            using var framePen = new Pen(Themer.CurrentThemeColors.Border);
            using var curvePen = new Pen(color, this.AdjustForDPI(2));
            using var dotBrush = new SolidBrush(color);

            var state = graphics.Save();
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.DrawRectangle(framePen, plot);

            if (!constant && minY < 0f && maxY > 0f)
            {
                using var baselinePen = new Pen(Themer.CurrentThemeColors.Border) { DashStyle = DashStyle.Dash };
                graphics.DrawLine(baselinePen, plot.Left, ToY(0f), plot.Right, ToY(0f));
            }

            graphics.DrawLines(curvePen, [.. values.Select((y, i) => new PointF(plot.Left + i * plot.Width / (samples - 1f), ToY(y)))]);

            var radius = this.AdjustForDPI(2.5f);

            foreach (var key in keys)
            {
                graphics.FillEllipse(dotBrush, ToX(key.X) - radius, ToY(key.Y) - radius, radius * 2, radius * 2);
            }

            var range = constant
                ? $"constant {FormatNumber((minY + maxY) / 2f, "G2")}"
                : $"x {FormatNumber(minX)} to {FormatNumber(maxX)}{Environment.NewLine}y {FormatNumber(minY)} to {FormatNumber(maxY)}";
            graphics.Restore(state);
            System.Windows.Forms.TextRenderer.DrawText(graphics, range, font, labelArea, Themer.CurrentThemeColors.ContrastSoft,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        private static string FormatNumber(float value, string format = "G3")
            => value.ToString(MathF.Abs(value) >= 1000f ? "0" : format, CultureInfo.InvariantCulture);

        private void ShowLinks(EventRow row)
        {
            updating = true;
            soundGrid.Rows.Clear();

            foreach (var (key, sound) in index!.GetSoundReferences(row.Data))
            {
                var exists = SoundExists(sound);
                var soundRow = soundGrid.Rows[soundGrid.Rows.Add(sound, key, exists ? string.Empty : "Not shipped")];
                soundRow.Tag = exists ? sound : null;

                if (!exists)
                {
                    soundRow.DefaultCellStyle.ForeColor = Themer.CurrentThemeColors.Attention;
                }
            }

            referenceGrid.Rows.Clear();

            foreach (var (key, target) in index.GetEventReferences(row.Name, row.Data))
            {
                var exists = index.Find(target) != null;
                var referenceRow = referenceGrid.Rows[referenceGrid.Rows.Add(target, key, exists ? string.Empty : "Not loaded")];
                referenceRow.Tag = exists ? target : null;

                if (!exists)
                {
                    referenceRow.DefaultCellStyle.ForeColor = Themer.CurrentThemeColors.Attention;
                }
            }

            usedByGrid.Rows.Clear();

            foreach (var (user, key) in index.GetReferencedBy(row.Name).OrderBy(static u => u.Event, StringComparer.OrdinalIgnoreCase))
            {
                var file = index.GetDefinitions(user)[^1].File;
                var userRow = usedByGrid.Rows[usedByGrid.Rows.Add(user, key, ShortFileName(file))];
                userRow.Tag = user;
                userRow.Cells[2].ToolTipText = file;
            }

            soundsPage.Text = string.Create(CultureInfo.InvariantCulture, $"Sounds ({soundGrid.RowCount})");
            referencesPage.Text = string.Create(CultureInfo.InvariantCulture, $"References ({referenceGrid.RowCount})");
            usedByPage.Text = string.Create(CultureInfo.InvariantCulture, $"Used by ({usedByGrid.RowCount})");

            foreach (var grid in new[] { soundGrid, referenceGrid, usedByGrid })
            {
                grid.CurrentCell = null;
                grid.ClearSelection();
            }

            updating = false;
            StopSound();
        }

        private void OnSoundSelectionChanged(object? sender, EventArgs e)
        {
            if (!updating && soundGrid.CurrentCell != null)
            {
                PlaySound(soundGrid.CurrentCell.RowIndex, autoPlay: false);
            }
        }

        private void OnGridRowActivated(object? sender, int rowIndex)
        {
            if (sender == soundGrid)
            {
                PlaySound(rowIndex, autoPlay: true);
                return;
            }

            var grid = (Grid)sender!;

            var target = grid.Rows[rowIndex].Tag switch
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
            ReleasePlayer();
            StopSound();

            if (rowIndex < 0 || rowIndex >= soundGrid.RowCount || soundGrid.Rows[rowIndex].Tag is not string sound)
            {
                return false;
            }

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

            UpdatePlaybackState();
            return audioPanel != null;
        }

        private void StopSound()
        {
            audioHost.Visible = false;
            audioPanel?.Dispose();
            audioPanel = null;
            soundResource?.Dispose();
            soundResource = null;
            UpdatePlaybackState();
        }

        private void UpdatePlaybackState()
        {
            stopButton.Enabled = player != null || audioPanel != null;
            PlaceSplitters();
        }

        /// <summary>Plays the shown event through the renderer's sound event player, without spatialization.</summary>
        public void PlayEvent()
        {
            if (current is not { Playable: true } row || index == null)
            {
                return;
            }

            ReleasePlayer();
            StopSound();

            try
            {
#pragma warning disable CA2000 // The player owns the device and disposes it
                player = new SoundEventPlayer(guiContext, new NAudioDevice(), VrfGuiContext.Logger, index.MaxBaseVisits);
#pragma warning restore CA2000
            }
            catch (COMException e)
            {
                Log.Warn(nameof(SoundEventViewer), $"No audio device available, sound playback disabled: {e.Message}");
                return;
            }

            foreach (var name in index.EventNames)
            {
                player.Bank.AddSoundEvent(name, index.Find(name)!);
            }

            // The shown definition may not be the one that wins
            player.Bank.AddSoundEvent(row.Name, row.Data);
            player.Volume = Settings.Config.Volume;
            player.Update(listener);
            playing = player.Play(row.Name, channel: PreviewChannel);

            if (playing == null)
            {
                Log.Warn(nameof(SoundEventViewer), $"Sound event \"{row.Name}\" played nothing");
                ReleasePlayer();
                return;
            }

            playerTimer.Start();
            UpdatePlaybackState();
        }

        private void OnPlayerTick(object? sender, EventArgs e)
        {
            if (player == null || !Visible)
            {
                ReleasePlayer();
                return;
            }

            player.Update(listener);
            audiblePositioned.Clear();
            audibleFlat.Clear();
            player.CollectDebugSounds(audiblePositioned, audibleFlat);

            if (playing is not { Started: true } && audiblePositioned.Count == 0 && audibleFlat.Count == 0)
            {
                ReleasePlayer();
            }
        }

        /// <summary>Stops the event and the sound that play.</summary>
        public void StopPlayback()
        {
            ReleasePlayer();
            StopSound();
        }

        /// <summary>Disposes the event player with its audio device, which also gives up being the renderer's active player.</summary>
        private void ReleasePlayer()
        {
            playerTimer.Stop();
            playing = null;
            player?.Dispose();
            player = null;
            UpdatePlaybackState();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);

            if (!Visible)
            {
                StopPlayback();
            }
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
                await viewer.Loaded.ConfigureAwait(true);
                viewer.ShowEvent(eventName, file);
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

                case Keys.Escape when searchBox.Focused && searchBox.TextLength > 0:
                    searchBox.Clear();
                    return true;

                case Keys.Escape:
                    StopPlayback();
                    return true;

                case Keys.Space when !searchBox.Focused:
                    PlayEvent();
                    return true;

                case Keys.Control | Keys.C when eventGrid.Focused:
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
            Margin = new Padding(0, 2, 16, 2),
            UseMnemonic = false,
            LinkBehavior = LinkBehavior.HoverUnderline,
            LinkColor = LinkColor,
            ActiveLinkColor = LinkColor,
            VisitedLinkColor = LinkColor,
            DisabledLinkColor = Themer.CurrentThemeColors.ContrastSoft,
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
                ReleasePlayer();
                playerTimer.Dispose();
                StopSound();
                toolTip.Dispose();
                titleFont.Dispose();
                boldFont.Dispose();
                italicFont.Dispose();
                groupFont.Dispose();
            }

            base.Dispose(disposing);
        }

        /// <summary>A text box that draws its hint in the theme's soft text colour while it is empty.</summary>
        private sealed class SearchBox : TextBox
        {
            private const int WM_PAINT = 0x000F;

            public string Hint { get; init; } = string.Empty;

            protected override void WndProc(ref Message m)
            {
                base.WndProc(ref m);

                if (m.Msg == WM_PAINT && TextLength == 0)
                {
                    using var graphics = CreateGraphics();
                    System.Windows.Forms.TextRenderer.DrawText(graphics, Hint, Font, ClientRectangle, Themer.CurrentThemeColors.ContrastSoft,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                }
            }
        }

        /// <summary>
        /// A read only grid that selects whole rows, raises <see cref="Activated"/> on double-click or Enter, raises
        /// <see cref="LinkClicked"/> when a link is clicked, and copies the current cell with Ctrl+C.
        /// </summary>
        private sealed class Grid : DataGridView
        {
            public event EventHandler<int>? Activated;

            public event EventHandler<object>? LinkClicked;

            public bool CopiesCell { get; init; } = true;

            /// <summary>A column whose cells link to the <see cref="DataGridViewRow.Tag"/> of their row.</summary>
            public int LinkColumn { get; init; } = -1;

            /// <summary>Finds what a point in a cell links to, given the row, the column and the point in the cell.</summary>
            public Func<int, int, Point, object?>? FindLink { get; set; }

            /// <summary>Text shown in place of the rows while there are none.</summary>
            public string EmptyText
            {
                get => AccessibleDescription ?? string.Empty;
                set
                {
                    AccessibleDescription = value;
                    Invalidate();
                }
            }

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

            private object? FindLinkAt(int rowIndex, int columnIndex, Point location)
            {
                if (rowIndex < 0 || columnIndex < 0)
                {
                    return null;
                }

                return FindLink != null ? FindLink(rowIndex, columnIndex, location) : columnIndex == LinkColumn ? Rows[rowIndex].Tag : null;
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);

                CurrentCell = null;
            }

            protected override void OnCellPainting(DataGridViewCellPaintingEventArgs e)
            {
                if (e.RowIndex < 0 && e.CellStyle != null)
                {
                    e.CellStyle.SelectionBackColor = e.CellStyle.BackColor;
                    e.CellStyle.SelectionForeColor = e.CellStyle.ForeColor;
                }

                base.OnCellPainting(e);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);

                if (RowCount == 0 && EmptyText.Length > 0)
                {
                    var area = Rectangle.FromLTRB(this.AdjustForDPI(6), ColumnHeadersHeight + this.AdjustForDPI(6), ClientSize.Width, ClientSize.Height);
                    System.Windows.Forms.TextRenderer.DrawText(e.Graphics, EmptyText, Font, area, Themer.CurrentThemeColors.ContrastSoft,
                        TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                }
            }

            protected override void OnCellMouseMove(DataGridViewCellMouseEventArgs e)
            {
                base.OnCellMouseMove(e);

                Cursor = FindLinkAt(e.RowIndex, e.ColumnIndex, e.Location) != null ? Cursors.Hand : Cursors.Default;
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);

                Cursor = Cursors.Default;
            }

            protected override void OnCellMouseClick(DataGridViewCellMouseEventArgs e)
            {
                base.OnCellMouseClick(e);

                if (e.Button == MouseButtons.Left && FindLinkAt(e.RowIndex, e.ColumnIndex, e.Location) is { } target)
                {
                    LinkClicked?.Invoke(this, target);
                }
            }

            protected override void OnCellDoubleClick(DataGridViewCellEventArgs e)
            {
                base.OnCellDoubleClick(e);

                if (e.RowIndex >= 0)
                {
                    Activated?.Invoke(this, e.RowIndex);
                }
            }

            protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
            {
                if (CopiesCell && keyData == (Keys.Control | Keys.C) && CurrentCell?.FormattedValue is string { Length: > 0 } text)
                {
                    AppClipboard.SetText(text);
                    return true;
                }

                return base.ProcessCmdKey(ref msg, keyData);
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
