using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using GUI.Controls;
using GUI.Types.PackageViewer;
using GUI.Utils;
using ValvePak;

namespace GUI.Forms
{
    partial class SearchForm : ThemedForm
    {
        internal static readonly StringComparer NumericComparer =
            StringComparer.Create(CultureInfo.InvariantCulture, CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);

        private record FilterKeyEntry(string Key, SortedSet<string> Values)
        {
            public override string ToString() => Key;
        }

        private record ExtensionEntry(string Extension, int Count)
        {
            public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{(Extension == " " ? "No extension" : Extension)} ({Count})");
        }

        private const string AnyKey = "Any key";
        private const string AnyValue = "Any value";

        private readonly TaskScheduler uiTaskScheduler;
        private int filterLoadVersion;
        private int lastPopulatedKeysHash;
        private VirtualPackageNode? folder;
        private FilterKeyEntry? valuesKey;
        private bool hasInputError;

        private SearchTarget SelectedTarget => contentsRadioButton.Checked ? SearchTarget.Contents : pathRadioButton.Checked ? SearchTarget.Path : SearchTarget.Name;

        private FilterKeyEntry? SelectedFilterKey => FindTypedItem(filterKeyComboBox) as FilterKeyEntry;

        private string? SelectedFilterValue => filterValueComboBox.Enabled && filterValueComboBox.Text is { Length: > 0 } value && value != AnyValue ? value : null;

        /// <summary>
        /// Gets the search as entered in the dialog.
        /// </summary>
        public SearchRequest Request => new(
            findTextBox.Text,
            SelectedTarget,
            matchCaseToggle.Checked,
            wholeWordToggle.Checked,
            regexToggle.Checked,
            hexBytesToggle.Checked && SelectedTarget == SearchTarget.Contents,
            onlyInFolderCheckBox.Checked ? folder : null,
            (extensionComboBox.SelectedItem as ExtensionEntry)?.Extension,
            SelectedFilterKey?.Key,
            SelectedFilterKey != null ? SelectedFilterValue : null);

        public SearchForm()
        {
            InitializeComponent();
            uiTaskScheduler = TaskScheduler.FromCurrentSynchronizationContext();

            findTextBox.TextChanged += (_, _) => UpdateState();
            findTextBox.GotFocus += (_, _) => findTextBoxPanel.Invalidate();
            findTextBox.LostFocus += (_, _) => findTextBoxPanel.Invalidate();
            nameRadioButton.CheckedChanged += (_, _) => UpdateState();
            pathRadioButton.CheckedChanged += (_, _) => UpdateState();
            contentsRadioButton.CheckedChanged += (_, _) => UpdateState();
            matchCaseToggle.Toggled += _ => UpdateState();
            wholeWordToggle.Toggled += _ => UpdateState();
            regexToggle.Toggled += _ => UpdateState();
            hexBytesToggle.Toggled += _ => UpdateState();
            filterKeyComboBox.TextChanged += (_, _) =>
            {
                UpdateFilterValues();
                UpdateState();
            };

            UpdateState();
        }

        private void UpdateState()
        {
            hexBytesToggle.Enabled = contentsRadioButton.Checked;

            var request = Request;
            matchCaseToggle.Enabled = !request.HexBytes;
            wholeWordToggle.Enabled = !request.HexBytes;
            regexToggle.Enabled = !request.HexBytes;

            var unknownKey = filterKeyComboBox.Enabled && filterKeyComboBox.Text.Length > 0 && FindTypedItem(filterKeyComboBox) == null
                ? $"There is no asset info key named \"{filterKeyComboBox.Text}\"."
                : null;
            var (hint, isError, canSearch) = unknownKey != null ? (unknownKey, true, false) : GetHint(request);
            hasInputError = isError && unknownKey == null;
            hintLabel.Text = hint;
            hintLabel.ForeColor = isError ? Themer.CurrentThemeColors.Attention : Themer.CurrentThemeColors.ContrastSoft;
            toolTip.SetToolTip(hintLabel, hint);
            findTextBoxPanel.Invalidate();

            findButton.Enabled = canSearch;
            findButton.BackColor = canSearch ? Themer.CurrentThemeColors.Accent : Themer.CurrentThemeColors.Border;
            findButton.ForeColor = canSearch ? Color.Black : Themer.CurrentThemeColors.Contrast;
        }

        private static (string Hint, bool IsError, bool CanSearch) GetHint(SearchRequest request)
        {
            if (request.HexBytes)
            {
                try
                {
                    if (request.Text.Length > 0 && Convert.FromHexString(request.Text.Replace(" ", "", StringComparison.Ordinal)).Length < 3)
                    {
                        return ("Enter at least 3 bytes to search for.", false, false);
                    }
                }
                catch (FormatException)
                {
                    return ("Enter pairs of hex digits, such as 56 42 4B 56.", true, false);
                }

                return ("Bytes in the file contents, such as 56 42 4B 56", false, true);
            }

            if (request.UseRegex && request.Text.Length > 0)
            {
                try
                {
                    _ = BetterTreeView.CreateRegex(request.Text, request, Regex.InfiniteMatchTimeout);
                }
                catch (RegexParseException e)
                {
                    var error = Regex.Replace(e.Error.ToString(), "(?<=[a-z])(?=[A-Z])", " ").ToLowerInvariant();
                    var offset = Math.Clamp(e.Offset - (request.WholeWord ? BetterTreeView.WholeWordPrefix.Length : 0), 0, request.Text.Length);

                    return (string.Create(CultureInfo.InvariantCulture, $"{char.ToUpperInvariant(error[0])}{error[1..]} (at character {offset})"), true, false);
                }
            }

            if (request.Target == SearchTarget.Contents && !request.UseRegex && request.Text.Length > 0 && Encoding.UTF8.GetByteCount(request.Text) < 3)
            {
                return ("Enter at least 3 characters to search file contents.", false, false);
            }

            var hint = (request.Target, request.UseRegex) switch
            {
                (SearchTarget.Contents, true) => "A regular expression over the text of each file",
                (SearchTarget.Contents, false) => "Text in the file contents",
                (SearchTarget.Path, true) => "A regular expression over the full path, ^path$ matches it exactly",
                (SearchTarget.Path, false) => "Part of the full path, such as materials/dev",
                (_, true) => "A regular expression over the file name, ^name$ matches it exactly",
                _ => "Part of the file name",
            };

            if (request.Target != SearchTarget.Contents)
            {
                hint += ". Hex bytes (0x) are available when searching contents";
            }

            return (hint, false, true);
        }

        private static object? FindTypedItem(ComboBox comboBox)
        {
            var text = comboBox.Text;

            if (comboBox.SelectedItem is { } selected && comboBox.GetItemText(selected) == text)
            {
                return selected;
            }

            return comboBox.Items.Cast<object>().FirstOrDefault(item => string.Equals(comboBox.GetItemText(item), text, StringComparison.OrdinalIgnoreCase));
        }

        private void FindTextBoxPanel_Paint(object? sender, PaintEventArgs e)
        {
            var colors = Themer.CurrentThemeColors;
            var color = hasInputError ? colors.Attention : findTextBox.Focused ? colors.Accent : colors.ControlBorder;

            using var pen = new Pen(color, this.AdjustForDPI(1));
            e.Graphics.DrawRectangle(pen, 0, 0, findTextBoxPanel.Width - 1, findTextBoxPanel.Height - 1);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            ThemedToggleButton? toggle = keyData switch
            {
                Keys.Alt | Keys.C => matchCaseToggle,
                Keys.Alt | Keys.W => wholeWordToggle,
                Keys.Alt | Keys.R => regexToggle,
                Keys.Alt | Keys.X => hexBytesToggle,
                _ => null,
            };

            if (toggle == null)
            {
                return base.ProcessCmdKey(ref msg, keyData);
            }

            if (toggle.Enabled)
            {
                toggle.PerformClick();
            }

            return true;
        }

        /// <summary>
        /// Lists the extensions of the files in the package, keeping the selected one if the package has it.
        /// </summary>
        public void SetExtensions(Dictionary<string, List<PackageEntry>>? entries)
        {
            var selected = (extensionComboBox.SelectedItem as ExtensionEntry)?.Extension;
            var counts = new SortedDictionary<string, int>(NumericComparer);

            foreach (var (extension, files) in entries ?? [])
            {
                counts[extension] = counts.GetValueOrDefault(extension) + files.Count;
            }

            extensionComboBox.BeginUpdate();
            extensionComboBox.Items.Clear();
            extensionComboBox.Items.Add("Any extension");
            extensionComboBox.SelectedIndex = 0;

            foreach (var (extension, count) in counts)
            {
                var index = extensionComboBox.Items.Add(new ExtensionEntry(extension, count));

                if (selected != null && NumericComparer.Equals(extension, selected))
                {
                    extensionComboBox.SelectedIndex = index;
                }
            }

            extensionComboBox.EndUpdate();
        }

        /// <summary>
        /// Sets the folder that "Only in" limits the search to, or <see langword="null"/> when no folder is selected.
        /// </summary>
        /// <param name="searchFolder">The folder.</param>
        /// <param name="onlyInFolder">Whether to tick "Only in", otherwise it stays as it was last used.</param>
        public void SetFolder(VirtualPackageNode? searchFolder, bool onlyInFolder)
        {
            folder = searchFolder;
            onlyInFolderCheckBox.Enabled = folder != null;
            UpdateOnlyInFolderText();

            if (onlyInFolder)
            {
                onlyInFolderCheckBox.Checked = true;
            }
        }

        private void OnlyInFolderCheckBox_SizeChanged(object? sender, EventArgs e) => UpdateOnlyInFolderText();

        private void UpdateOnlyInFolderText()
        {
            const string Prefix = "Only in ";

            if (folder == null)
            {
                onlyInFolderCheckBox.Text = "&Only in selected folder";
                toolTip.SetToolTip(onlyInFolderCheckBox, null);
                return;
            }

            var path = PackageViewer.GetFolderPath(folder);

            using var graphics = onlyInFolderCheckBox.CreateGraphics();
            var font = onlyInFolderCheckBox.Font;
            var width = onlyInFolderCheckBox.ClientSize.Width - this.AdjustForDPI(24) - TextRenderer.MeasureText(graphics, Prefix, font).Width;
            var trimmed = ThemedComboBox.TrimStartToFit(graphics, path, font, width, TextFormatFlags.Left);

            onlyInFolderCheckBox.Text = string.Concat("&", Prefix, trimmed.Replace("&", "&&", StringComparison.Ordinal));
            toolTip.SetToolTip(onlyInFolderCheckBox, path);
        }

        /// <summary>
        /// Sets the available SearchableUserData filter keys from a task.
        /// If the task is already completed, populates immediately.
        /// Otherwise shows a loading state and populates when the task completes.
        /// </summary>
        public void SetSearchableUserDataKeys(Task<Dictionary<string, SortedSet<string>>?> keysTask)
        {
            // Skip repopulating if the same data is already loaded (preserves user's filter selection)
            if (keysTask.IsCompletedSuccessfully
                && keysTask.Result?.GetHashCode() == lastPopulatedKeysHash)
            {
                return;
            }

            var version = ++filterLoadVersion;

            if (keysTask.IsCompleted)
            {
                PopulateFilterKeys(keysTask.IsCompletedSuccessfully ? keysTask.Result : null);
                return;
            }

            SetFilterKeyItems(["Loading asset info…"], enabled: false);

            keysTask.ContinueWith(t =>
            {
                if (!IsDisposed && filterLoadVersion == version)
                {
                    PopulateFilterKeys(t.IsCompletedSuccessfully ? t.Result : null);
                }
            }, uiTaskScheduler);
        }

        private void PopulateFilterKeys(Dictionary<string, SortedSet<string>>? keys)
        {
            lastPopulatedKeysHash = keys?.GetHashCode() ?? 0;

            if (keys is not { Count: > 0 })
            {
                SetFilterKeyItems(["No asset info"], enabled: false);
                return;
            }

            SetFilterKeyItems([AnyKey, .. keys.OrderBy(kv => kv.Key, NumericComparer).Select(kv => new FilterKeyEntry(kv.Key, kv.Value))], enabled: true);
        }

        private void SetFilterKeyItems(object[] items, bool enabled)
        {
            filterKeyComboBox.BeginUpdate();
            filterKeyComboBox.Items.Clear();
            filterKeyComboBox.Items.AddRange(items);
            filterKeyComboBox.SelectedIndex = 0;
            filterKeyComboBox.EndUpdate();
            SetComboBoxEnabled(filterKeyComboBox, enabled);

            UpdateFilterValues();
            UpdateState();
        }

        private void UpdateFilterValues()
        {
            var entry = SelectedFilterKey;

            if (entry == valuesKey && filterValueComboBox.Items.Count > 0)
            {
                return;
            }

            valuesKey = entry;

            filterValueComboBox.BeginUpdate();
            filterValueComboBox.Items.Clear();
            filterValueComboBox.Items.Add(AnyValue);

            if (entry != null)
            {
                filterValueComboBox.Items.AddRange([.. entry.Values]);
            }

            filterValueComboBox.SelectedIndex = 0;
            filterValueComboBox.EndUpdate();
            SetComboBoxEnabled(filterValueComboBox, entry != null);
        }

        private static void SetComboBoxEnabled(ComboBox comboBox, bool enabled)
        {
            comboBox.DropDownStyle = enabled ? ComboBoxStyle.DropDown : ComboBoxStyle.DropDownList;
            comboBox.Enabled = enabled;
        }

        protected override void OnCreateControl()
        {
            base.OnCreateControl();

            var colors = Themer.CurrentThemeColors;
            findTextBoxPanel.BackColor = colors.AppMiddle;
            findTextBox.BackColor = colors.AppMiddle;
            findButton.HoveredBackColor = Color.FromArgb(140, 191, 255);
            findButton.ClickedBackColor = Color.FromArgb(70, 135, 235);

            UpdateState();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            MinimumSize = Size;
            MaximumSize = new Size(short.MaxValue, Height);
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);

            if (Visible)
            {
                ActiveControl = findTextBox;
                findTextBox.SelectAll();
            }
        }
    }
}
