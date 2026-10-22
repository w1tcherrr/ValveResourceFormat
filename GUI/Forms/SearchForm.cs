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

        /// <summary>A dropdown item for a key, shown with how many files have it, and for an asset info key the values it has.</summary>
        private sealed class FilterEntry(string key, int count, Dictionary<string, int>? values = null, string? label = null)
        {
            public string Key => key;
            public Dictionary<string, int>? Values => values;

            public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{label ?? key} ({count})");
        }

        private const string AnyKey = "Any key";
        private const string AnyValue = "Any value";

        private readonly TaskScheduler uiTaskScheduler;
        private int filterLoadVersion;
        private VirtualPackageNode? folder;
        private Dictionary<string, List<PackageEntry>>? packageEntries;
        private FilterEntry? valuesKey;
        private bool hasInputError;
        private string? extensionChoice;
        private string? keyChoice;
        private string? valueChoice;

        private SearchTarget SelectedTarget => contentsRadioButton.Checked ? SearchTarget.Contents : pathRadioButton.Checked ? SearchTarget.Path : SearchTarget.Name;

        private FilterEntry? SelectedFilterKey => FindTypedItem(filterKeyComboBox) as FilterEntry;

        private string? SelectedFilterValue => filterValueComboBox.Enabled && filterValueComboBox.Text is { Length: > 0 } value && !value.Equals(AnyValue, StringComparison.OrdinalIgnoreCase)
            ? (FindTypedItem(filterValueComboBox) as FilterEntry)?.Key ?? value
            : null;

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
            (extensionComboBox.SelectedItem as FilterEntry)?.Key,
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
                toolTip.SetToolTip(filterKeyComboBox, filterKeyComboBox.Text);
                UpdateFilterValues();
                UpdateState();
            };
            filterValueComboBox.TextChanged += (_, _) =>
            {
                toolTip.SetToolTip(filterValueComboBox, filterValueComboBox.Text);
                UpdateState();
            };
            onlyInFolderCheckBox.CheckedChanged += (_, _) =>
            {
                folderPathLabel.Invalidate();
                UpdateExtensions();
                UpdateState();
            };
            extensionComboBox.SelectedIndexChanged += (_, _) => UpdateState();

            UpdateState();
        }

        private void UpdateState()
        {
            hexBytesToggle.Enabled = contentsRadioButton.Checked;

            var request = Request;
            matchCaseToggle.Enabled = !request.HexBytes;
            wholeWordToggle.Enabled = !request.HexBytes;
            regexToggle.Enabled = !request.HexBytes;

            var (filterError, filterComboBox) = GetFilterError();
            filterKeyComboBox.HasError = filterComboBox == filterKeyComboBox;
            filterValueComboBox.HasError = filterComboBox == filterValueComboBox;

            var (hint, isError, canSearch) = filterError != null ? (filterError, true, false) : GetHint(request);
            hasInputError = isError && filterError == null;

            if (request is { Text: "", Folder: null, Extension: null, FilterKey: null })
            {
                canSearch = false;
            }

            if (canSearch && SelectedFilterKey is { Values.Count: >= TreeViewWithSearchResults.MaxSearchDataValues } && SelectedFilterValue == null)
            {
                hint = string.Create(CultureInfo.InvariantCulture, $"First {TreeViewWithSearchResults.MaxSearchDataValues} values listed, type others");
            }

            hintLabel.Text = hint;
            hintLabel.ForeColor = isError ? Themer.CurrentThemeColors.Attention : Themer.CurrentThemeColors.ContrastSoft;
            toolTip.SetToolTip(hintLabel, hint);
            findTextBoxPanel.Invalidate();

            findButton.Enabled = canSearch;
            findButton.BackColor = canSearch ? Themer.CurrentThemeColors.Accent : Themer.CurrentThemeColors.Border;
            findButton.ForeColor = canSearch ? Color.Black : Themer.CurrentThemeColors.Contrast;
        }

        private (string? Error, ThemedComboBox? ComboBox) GetFilterError()
        {
            if (!filterKeyComboBox.Enabled || filterKeyComboBox.Text.Length == 0)
            {
                return (null, null);
            }

            if (FindTypedItem(filterKeyComboBox) == null)
            {
                return ($"Asset info: no key named \"{filterKeyComboBox.Text}\"", filterKeyComboBox);
            }

            if (SelectedFilterKey is { Values: { } values } key && SelectedFilterValue is { } value
                && FindTypedItem(filterValueComboBox) == null && values.Count < TreeViewWithSearchResults.MaxSearchDataValues)
            {
                return ($"Asset info: no value \"{value}\" for {key.Key}", filterValueComboBox);
            }

            return (null, null);
        }

        private static (string Hint, bool IsError, bool CanSearch) GetHint(SearchRequest request)
        {
            if (request.HexBytes)
            {
                byte[] bytes;

                try
                {
                    bytes = Convert.FromHexString(request.Text.Replace(" ", "", StringComparison.Ordinal));
                }
                catch (FormatException)
                {
                    return ("Enter pairs of hex digits, such as 56 42 4B 56", true, false);
                }

                if (request.Text.Length == 0)
                {
                    return ("Bytes in the file contents, such as 56 42 4B 56", false, true);
                }

                var text = string.Concat(bytes.Select(static b => b is >= 0x20 and < 0x7F ? (char)b : '.'));
                var preview = string.Create(CultureInfo.InvariantCulture, $"{bytes.Length} bytes: \"{text}\"");

                return bytes.Length < 3 ? ($"{preview}, enter at least 3 bytes", false, false) : (preview, false, true);
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

                    var message = string.Create(CultureInfo.InvariantCulture, $"{char.ToUpperInvariant(error[0])}{error[1..]} (at character {offset})");

                    if (request.Text[0] is '*' or '?')
                    {
                        message += ", use .* instead, or turn off regex";
                    }

                    return (message, true, false);
                }
            }

            if (request.Target == SearchTarget.Contents && !request.UseRegex && request.Text.Length > 0 && Encoding.UTF8.GetByteCount(request.Text) < 3)
            {
                return ("Enter at least 3 characters to search file contents", false, false);
            }

            var hint = (request.Target, request.UseRegex) switch
            {
                (SearchTarget.Contents, true) => "A regular expression over the text of each file",
                (SearchTarget.Contents, false) => "Text in the file contents",
                (SearchTarget.Path, true) => "A regular expression over the full path, ^path$ matches it exactly",
                (SearchTarget.Path, false) => "Part of the full path, such as materials/dev",
                (SearchTarget.Name, false) when request.Text.AsSpan().IndexOfAny('/', '\\') >= 0 => "Has a / or \\, so it matches the full path",
                (_, true) => "A regular expression over the file name, ^name$ matches it exactly",
                _ => "Part of the file name",
            };

            return (hint, false, true);
        }

        /// <summary>Finds the item whose text or key was typed into the box, ignoring case.</summary>
        private static object? FindTypedItem(ComboBox comboBox)
        {
            var text = comboBox.Text;

            if (comboBox.SelectedItem is { } selected && comboBox.GetItemText(selected) == text)
            {
                return selected;
            }

            return comboBox.Items.Cast<object>().FirstOrDefault(item => string.Equals(comboBox.GetItemText(item), text, StringComparison.OrdinalIgnoreCase)
                || (item is FilterEntry entry && string.Equals(entry.Key, text, StringComparison.OrdinalIgnoreCase)));
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
            packageEntries = entries;
            UpdateExtensions();
        }

        /// <summary>
        /// Lists the extensions with how many files have each, in the folder when "Only in" is ticked, otherwise in the package.
        /// </summary>
        private void UpdateExtensions()
        {
            var selected = (extensionComboBox.SelectedItem as FilterEntry)?.Key ?? extensionChoice;
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            if (onlyInFolderCheckBox.Checked && folder != null)
            {
                CountExtensions(folder, counts);
            }
            else
            {
                foreach (var (extension, files) in packageEntries ?? [])
                {
                    counts[extension] = counts.GetValueOrDefault(extension) + files.Count;
                }
            }

            extensionComboBox.BeginUpdate();
            extensionComboBox.Items.Clear();
            extensionComboBox.Items.Add("Any extension");
            extensionComboBox.SelectedIndex = 0;

            foreach (var (extension, count) in counts.OrderBy(static count => count.Key, NumericComparer))
            {
                if (count == 0)
                {
                    continue;
                }

                var index = extensionComboBox.Items.Add(new FilterEntry(extension, count, label: extension == " " ? "No extension" : null));

                if (selected != null && NumericComparer.Equals(extension, selected))
                {
                    extensionComboBox.SelectedIndex = index;
                }
            }

            extensionComboBox.EndUpdate();

            if (packageEntries != null)
            {
                extensionChoice = null;
            }
        }

        private static void CountExtensions(VirtualPackageNode node, Dictionary<string, int> counts)
        {
            foreach (var file in node.Files)
            {
                counts[file.TypeName] = counts.GetValueOrDefault(file.TypeName) + 1;
            }

            foreach (var child in node.Folders.Values)
            {
                CountExtensions(child, counts);
            }
        }

        /// <summary>
        /// Sets the folder that "Only in" limits the search to, or <see langword="null"/> when no folder is selected.
        /// </summary>
        /// <param name="onlyInFolder">Whether to tick "Only in", otherwise it stays as it was last used.</param>
        public void SetFolder(VirtualPackageNode? searchFolder, bool onlyInFolder)
        {
            folder = searchFolder;
            onlyInFolderTable.Visible = folder != null;
            noFolderLabel.Visible = folder == null;
            folderPathLabel.Invalidate();
            toolTip.SetToolTip(folderPathLabel, folder != null ? PackageViewer.GetFolderPath(folder) : null);

            if (onlyInFolder && !onlyInFolderCheckBox.Checked)
            {
                onlyInFolderCheckBox.Checked = true;
            }
            else
            {
                UpdateExtensions();
            }
        }

        /// <summary>
        /// Lets go of the package the dialog was opened for, keeping the chosen extension and asset info filter for the next time.
        /// </summary>
        public void ReleasePackage()
        {
            extensionChoice = (extensionComboBox.SelectedItem as FilterEntry)?.Key;
            keyChoice = SelectedFilterKey?.Key;
            valueChoice = SelectedFilterValue;

            packageEntries = null;
            SetFolder(null, onlyInFolder: false);

            filterLoadVersion++;
            SetFilterKeyItems(["No asset info"], enabled: false);
        }

        private void FolderPathLabel_Paint(object? sender, PaintEventArgs e)
        {
            if (folder == null)
            {
                return;
            }

            const TextFormatFlags Flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            var bounds = folderPathLabel.ClientRectangle;
            var path = ThemedComboBox.TrimStartToFit(e.Graphics, PackageViewer.GetFolderPath(folder), folderPathLabel.Font, bounds.Width, Flags);

            var color = onlyInFolderCheckBox.Checked ? folderPathLabel.ForeColor : Themer.CurrentThemeColors.ContrastSoft;
            TextRenderer.DrawText(e.Graphics, path, folderPathLabel.Font, bounds, color, Flags);
        }

        private void FolderPathLabel_Resize(object? sender, EventArgs e) => folderPathLabel.Invalidate();

        private void FolderPathLabel_Click(object? sender, EventArgs e) => onlyInFolderCheckBox.Checked = !onlyInFolderCheckBox.Checked;

        /// <summary>
        /// Sets the available SearchableUserData filter keys from a task.
        /// If the task is already completed, populates immediately.
        /// Otherwise shows a loading state and populates when the task completes.
        /// </summary>
        public void SetSearchableUserDataKeys(Task<Dictionary<string, (int Count, Dictionary<string, int> Values)>?> keysTask)
        {
            var version = ++filterLoadVersion;

            if (keysTask.IsCompleted)
            {
                PopulateFilterKeys(keysTask.IsCompletedSuccessfully ? keysTask.Result : null);
                return;
            }

            SetFilterKeyItems(["Loading asset info\u2026"], enabled: false);

            keysTask.ContinueWith(t =>
            {
                if (!IsDisposed && filterLoadVersion == version)
                {
                    PopulateFilterKeys(t.IsCompletedSuccessfully ? t.Result : null);
                }
            }, uiTaskScheduler);
        }

        private void PopulateFilterKeys(Dictionary<string, (int Count, Dictionary<string, int> Values)>? keys)
        {
            if (keys is not { Count: > 0 })
            {
                SetFilterKeyItems(["No asset info"], enabled: false);
                return;
            }

            SetFilterKeyItems([AnyKey, .. keys.OrderBy(kv => kv.Key, NumericComparer).Select(kv => new FilterEntry(kv.Key, kv.Value.Count, kv.Value.Values))], enabled: true);

            if (filterKeyComboBox.Items.OfType<FilterEntry>().FirstOrDefault(entry => entry.Key == keyChoice) is { } key)
            {
                filterKeyComboBox.SelectedItem = key;

                if (valueChoice != null)
                {
                    filterValueComboBox.Text = valueChoice;
                }
            }

            keyChoice = null;
            valueChoice = null;
        }

        private void SetFilterKeyItems(object[] items, bool enabled)
        {
            filterKeyComboBox.BeginUpdate();
            filterKeyComboBox.Items.Clear();
            filterKeyComboBox.Items.AddRange(items);
            filterKeyComboBox.SelectedIndex = 0;
            filterKeyComboBox.EndUpdate();
            SetComboBoxEnabled(filterKeyComboBox, enabled);
            filterKeyLabel.ForeColor = enabled ? Themer.CurrentThemeColors.Contrast : Themer.CurrentThemeColors.ContrastSoft;

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

            if (entry?.Values is { } values)
            {
                filterValueComboBox.Items.AddRange([.. values.OrderBy(static value => value.Key, NumericComparer).Select(static value => new FilterEntry(value.Key, value.Value))]);
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
            noFolderLabel.ForeColor = colors.ContrastSoft;
            filterKeyLabel.ForeColor = filterKeyComboBox.Enabled ? colors.Contrast : colors.ContrastSoft;
            findButton.HoveredBackColor = Color.FromArgb(140, 191, 255);
            findButton.ClickedBackColor = Color.FromArgb(70, 135, 235);

            UpdateState();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            if (MinimumSize.IsEmpty)
            {
                MinimumSize = Size;
                MaximumSize = new Size(short.MaxValue, Height);
            }
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
