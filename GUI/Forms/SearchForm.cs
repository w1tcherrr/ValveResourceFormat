using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
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
            public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{(Extension == " " ? "(no extension)" : Extension)} ({Count})");
        }

        private readonly TaskScheduler uiTaskScheduler;
        private int filterLoadVersion;
        private int lastPopulatedKeysHash;
        private VirtualPackageNode? folder;

        private SearchTarget SelectedTarget => contentsRadioButton.Checked ? SearchTarget.Contents : pathRadioButton.Checked ? SearchTarget.Path : SearchTarget.Name;

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
            (filterKeyComboBox.SelectedItem as FilterKeyEntry)?.Key,
            filterValueComboBox.SelectedIndex > 0 ? filterValueComboBox.SelectedItem as string : null);

        public SearchForm()
        {
            InitializeComponent();
            uiTaskScheduler = TaskScheduler.FromCurrentSynchronizationContext();

            filterKeyComboBox.SelectedIndexChanged += FilterKeyComboBox_SelectedIndexChanged;
            findTextBox.TextChanged += (_, _) => UpdateState();
            nameRadioButton.CheckedChanged += (_, _) => UpdateState();
            pathRadioButton.CheckedChanged += (_, _) => UpdateState();
            contentsRadioButton.CheckedChanged += (_, _) => UpdateState();
            matchCaseToggle.Toggled += _ => UpdateState();
            wholeWordToggle.Toggled += _ => UpdateState();
            regexToggle.Toggled += _ => UpdateState();
            hexBytesToggle.Toggled += _ => UpdateState();

            UpdateState();
        }

        private void UpdateState()
        {
            hexBytesToggle.Enabled = contentsRadioButton.Checked;

            var request = Request;
            matchCaseToggle.Enabled = !request.HexBytes;
            wholeWordToggle.Enabled = !request.HexBytes;
            regexToggle.Enabled = !request.HexBytes;

            findTextBox.PlaceholderText = request switch
            {
                { HexBytes: true } => "Hex bytes, such as 56 42 4B 56",
                { Target: SearchTarget.Contents } => "Text in file contents",
                { Target: SearchTarget.Path } => "Path, such as materials/dev",
                _ => "File name",
            };

            var error = GetInputError(request);
            findTextBox.ForeColor = error == null ? Themer.CurrentThemeColors.Contrast : Themer.CurrentThemeColors.Attention;
            errorLabel.Text = error;
            toolTip.SetToolTip(errorLabel, error);
            findButton.Enabled = error == null;
        }

        private static string? GetInputError(SearchRequest request)
        {
            if (request.Text.Length == 0)
            {
                return null;
            }

            if (request.HexBytes)
            {
                try
                {
                    return Convert.FromHexString(request.Text.Replace(" ", "", StringComparison.Ordinal)).Length < 3 ? "Enter at least 3 bytes." : null;
                }
                catch (FormatException)
                {
                    return "Enter pairs of hex digits, such as 56 42 4B 56.";
                }
            }

            if (request.UseRegex)
            {
                try
                {
                    _ = new Regex(request.Text);
                }
                catch (ArgumentException e)
                {
                    return e.Message;
                }

                return null;
            }

            if (request.Target == SearchTarget.Contents && Encoding.UTF8.GetByteCount(request.Text) < 3)
            {
                return "Enter at least 3 characters to search file contents.";
            }

            return null;
        }

        /// <summary>
        /// Lists the extensions of the files in the package, keeping the selected one if the package has it.
        /// </summary>
        public void SetExtensions(Dictionary<string, List<PackageEntry>>? entries)
        {
            var selected = (extensionComboBox.SelectedItem as ExtensionEntry)?.Extension;

            extensionComboBox.BeginUpdate();
            extensionComboBox.Items.Clear();
            extensionComboBox.Items.Add("(All)");
            extensionComboBox.SelectedIndex = 0;

            if (entries != null)
            {
                foreach (var (extension, files) in entries.OrderBy(static entry => entry.Key, NumericComparer))
                {
                    var index = extensionComboBox.Items.Add(new ExtensionEntry(extension, files.Count));

                    if (extension == selected)
                    {
                        extensionComboBox.SelectedIndex = index;
                    }
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
            onlyInFolderCheckBox.Text = folder != null ? $"&Only in {PackageViewer.GetFolderPath(folder).Replace("&", "&&", StringComparison.Ordinal)}" : "&Only in selected folder";

            if (onlyInFolder)
            {
                onlyInFolderCheckBox.Checked = true;
            }
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

            filterKeyComboBox.SelectedIndexChanged -= FilterKeyComboBox_SelectedIndexChanged;

            if (keysTask.IsCompleted)
            {
                PopulateFilterKeys(keysTask.IsCompletedSuccessfully ? keysTask.Result : null);
                filterKeyComboBox.SelectedIndexChanged += FilterKeyComboBox_SelectedIndexChanged;
                return;
            }

            // Show loading state
            filterKeyComboBox.BeginUpdate();
            filterKeyComboBox.Items.Clear();
            filterKeyComboBox.Items.Add("Loading asset info\u2026");
            filterKeyComboBox.SelectedIndex = 0;
            filterKeyComboBox.Enabled = false;
            filterKeyComboBox.EndUpdate();

            filterValueComboBox.Items.Clear();
            filterValueComboBox.Visible = false;

            filterKeyComboBox.SelectedIndexChanged += FilterKeyComboBox_SelectedIndexChanged;

            keysTask.ContinueWith(t =>
            {
                if (!IsDisposed && filterLoadVersion == version)
                {
                    filterKeyComboBox.SelectedIndexChanged -= FilterKeyComboBox_SelectedIndexChanged;
                    PopulateFilterKeys(t.IsCompletedSuccessfully ? t.Result : null);
                    filterKeyComboBox.SelectedIndexChanged += FilterKeyComboBox_SelectedIndexChanged;
                }
            }, uiTaskScheduler);
        }

        private void PopulateFilterKeys(Dictionary<string, SortedSet<string>>? keys)
        {
            lastPopulatedKeysHash = keys?.GetHashCode() ?? 0;
            var hasKeys = keys != null && keys.Count > 0;

            filterKeyComboBox.BeginUpdate();
            filterKeyComboBox.Items.Clear();

            if (hasKeys)
            {
                var sorted = keys!.OrderBy(kv => kv.Key, NumericComparer).Select(kv => (object)new FilterKeyEntry(kv.Key, kv.Value)).ToArray();

                filterKeyComboBox.Items.Add("(No filter)");
                filterKeyComboBox.Items.AddRange(sorted);
                filterKeyComboBox.SelectedIndex = 0;
                filterKeyComboBox.Enabled = true;
            }
            else
            {
                filterKeyComboBox.Items.Add("No asset info");
                filterKeyComboBox.SelectedIndex = 0;
                filterKeyComboBox.Enabled = false;
            }

            filterKeyComboBox.EndUpdate();

            filterValueComboBox.Items.Clear();
            filterValueComboBox.Visible = false;
        }

        private void FilterKeyComboBox_SelectedIndexChanged(object? sender, EventArgs e)
        {
            filterValueComboBox.BeginUpdate();
            filterValueComboBox.Items.Clear();

            if (filterKeyComboBox.SelectedItem is not FilterKeyEntry entry)
            {
                filterValueComboBox.EndUpdate();
                filterValueComboBox.Visible = false;
                return;
            }

            filterValueComboBox.Items.Add("(Any value)");
            filterValueComboBox.Items.AddRange([.. entry.Values]);
            filterValueComboBox.SelectedIndex = 0;
            filterValueComboBox.EndUpdate();
            filterValueComboBox.Visible = true;
        }

        protected override void OnCreateControl()
        {
            base.OnCreateControl();

            findTextBox.BackColor = Themer.CurrentThemeColors.AppMiddle;
            errorLabel.ForeColor = Themer.CurrentThemeColors.Attention;
        }

        /// <summary>
        /// On form load, set the search textbox as the focused control.
        /// </summary>
        /// <param name="sender">Object which raised event.</param>
        /// <param name="e">Event data.</param>
        private void SearchForm_Load(object sender, EventArgs e)
        {
            ActiveControl = findTextBox;
            findTextBox.SelectAll();
        }
    }
}
