using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using GUI.Controls;
using GUI.Types.Exporter;
using GUI.Types.PackageViewer;
using GUI.Utils;
using ValveResourceFormat.IO;

namespace GUI
{
    partial class MainForm
    {
        private static readonly TimeSpan PackageReleaseTimeout = TimeSpan.FromSeconds(10);

        private static PackageViewer? GetPackageViewer(TabPage tab)
            => tab.Controls[nameof(TreeViewWithSearchResults)] is TreeViewWithSearchResults treeView ? treeView.Viewer : null;

        private static VrfGuiContext? GetTabContext(TabPage tab)
            => tab.Tag is ExportData exportData ? exportData.VrfGuiContext : GetPackageViewer(tab)?.GuiContext;

        private static VrfGuiContext GetRootContext(VrfGuiContext context)
        {
            while (context.ParentGuiContext != null)
            {
                context = context.ParentGuiContext;
            }

            return context;
        }

        private static bool IsSameFile(string fileName, string path)
            => Path.IsPathRooted(fileName) && Path.GetFullPath(fileName).Equals(path, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Closes the tabs of files opened from the package file at <paramref name="path"/>, and of files opened from
        /// <paramref name="context"/>, without asking to save changes.
        /// </summary>
        /// <returns>The contexts that held the file open, they release it once disposed.</returns>
        private List<VrfGuiContext> CloseTabsUsing(string? path, VrfGuiContext? context)
        {
            var holders = new List<VrfGuiContext>();

            for (var i = mainTabs.TabPages.Count - 1; i > 0; i--)
            {
                var tab = mainTabs.TabPages[i];

                if (GetTabContext(tab) is not { } tabContext)
                {
                    continue;
                }

                var root = GetRootContext(tabContext);
                var holdsFile = path != null && IsSameFile(root.FileName, path);

                if (!holdsFile && root != context)
                {
                    continue;
                }

                if (holdsFile && !holders.Contains(root))
                {
                    holders.Add(root);
                }

                mainTabs.CloseTab(tab, skipConfirmation: true);
            }

            return holders;
        }

        private void AddPackageEditorTab(Control contents, string title, string toolTip, int index)
        {
            var tab = new ThemedTabPage(title)
            {
                ToolTipText = toolTip,
                ImageIndex = AppIcons.ExtensionIcons["vpk"],
            };

            try
            {
                tab.Controls.Add(contents);
                mainTabs.TabPages.Insert(Math.Clamp(index, 1, mainTabs.TabPages.Count), tab);
                mainTabs.SelectTab(tab);
                tab = null;
            }
            finally
            {
                tab?.Dispose();
            }
        }

        /// <summary>
        /// Opens a single file package for editing in its own tab, closing the other tabs of that file.
        /// </summary>
        private async Task OpenPackageEditorAsync(string path, int? index = null)
        {
            path = Path.GetFullPath(path);

            if (PackageFileNames.IsSplitPackageFile(path))
            {
                await AppMessageDialogs.ShowMessageAsync("Only VPKs that are a single file can be edited, not ones split into chunk files.", "Can not edit VPK", MessageIcon.Warning).ConfigureAwait(true);
                return;
            }

            foreach (TabPage tab in mainTabs.TabPages)
            {
                if (GetPackageViewer(tab) is { IsEditingPackage: true } openEditor && openEditor.FileOnDisk is { } editedFile && editedFile.Equals(path, StringComparison.OrdinalIgnoreCase))
                {
                    mainTabs.SelectTab(tab);
                    return;
                }
            }

#pragma warning disable CA2000 // The editor tab owns the viewer and its tree view disposes the context
            var context = new VrfGuiContext(path, null);
            var viewer = new PackageViewer(context);
#pragma warning restore CA2000
            Control contents;

            try
            {
                contents = viewer.CreateEditor();
            }
            catch (Exception e)
            {
                context.Dispose();
                ErrorReport.ShowError(e, VrfGuiContext.GetDisplayPath(path));
                return;
            }

            if (viewer.IsSigned && !await AppMessageDialogs.ConfirmAsync(
                "This VPK is signed. Saving it writes it without the signature, so games that check signatures will not load it.",
                "Signed VPK",
                MessageIcon.Warning).ConfigureAwait(true))
            {
                contents.Dispose();
                return;
            }

            CloseTabsUsing(path, null);

            AddPackageEditorTab(contents, $"Editing {Path.GetFileName(path)}", path, index ?? mainTabs.SelectedIndex + 1);
        }

        private void CreateVpkToolStripMenuItem_Click(object sender, EventArgs e)
        {
            CreatePackageEditor(null);
        }

        private void CreateVpkFromFolderToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var inputFolder = AppFileDialogs.PickFolder("Choose which folder to pack into a VPK", AppFileDialogs.RememberIn.OpenDirectory);

            if (inputFolder != null)
            {
                CreatePackageEditor(inputFolder);
            }
        }

        /// <summary>
        /// Opens a new package for editing, with the files of <paramref name="inputFolder"/> added to its root.
        /// </summary>
        private void CreatePackageEditor(string? inputFolder)
        {
            var folderName = inputFolder != null ? Path.GetFileName(Path.TrimEndingDirectorySeparator(inputFolder)) : string.Empty;
            var fileName = folderName.Length > 0 ? $"{folderName}.vpk" : "new.vpk";
            var context = new VrfGuiContext(fileName, null);

            try
            {
#pragma warning disable CA2000 // The editor tab owns the viewer and its tree view disposes the context
                var viewer = new PackageViewer(context);
#pragma warning restore CA2000
                var contents = viewer.CreateEmpty();
                context = null;

                AddPackageEditorTab(contents, "New VPK", inputFolder ?? "New VPK", mainTabs.SelectedIndex + 1);

                if (inputFolder != null)
                {
                    viewer.AddFilesFromFolder(inputFolder);
                }
            }
            finally
            {
                context?.Dispose();
            }
        }

        private async void EditVpkToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var path = AppFileDialogs.OpenFile("Choose a VPK to edit", "Valve Pak (*.vpk)|*.vpk");

            if (path != null)
            {
                await OpenPackageEditorAsync(path).ConfigureAwait(true);
            }
        }

        private async void EditThisVpkToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (mainTabs.SelectedTab is { } tab && GetPackageViewer(tab) is { CanEdit: true, FileOnDisk: { } path })
            {
                await OpenPackageEditorAsync(path, mainTabs.GetTabIndex(tab)).ConfigureAwait(true);
            }
        }

        private async void OnSaveVPKToDiskToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (mainTabs.SelectedTab is { } tab && GetPackageViewer(tab) is { IsEditingPackage: true } viewer)
            {
                await SavePackageAsync(viewer, tab, saveAs: false, reopen: true).ConfigureAwait(true);
            }
        }

        private async void OnSaveVPKAsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (mainTabs.SelectedTab is { } tab && GetPackageViewer(tab) is { IsEditingPackage: true } viewer)
            {
                await SavePackageAsync(viewer, tab, saveAs: true, reopen: true).ConfigureAwait(true);
            }
        }

        /// <summary>
        /// Writes the package next to its target first, closes the tab and the tabs holding the target open,
        /// then replaces the target once it is released.
        /// </summary>
        /// <param name="viewer">The package being edited.</param>
        /// <param name="tab">The tab of the package.</param>
        /// <param name="saveAs">Ask for the file to save to, a new package always asks.</param>
        /// <param name="reopen">Open the saved file for editing in place of the tab.</param>
        /// <returns>Whether the package was saved to its target.</returns>
        private async Task<bool> SavePackageAsync(PackageViewer viewer, TabPage tab, bool saveAs, bool reopen)
        {
            var target = viewer.FileOnDisk;

            if (saveAs || target == null)
            {
                target = AppFileDialogs.SaveFile("Save VPK package", Path.GetFileName(target) ?? "new.vpk", "vpk", "Valve Pak|*.vpk");

                if (target == null)
                {
                    return false;
                }

                target = Path.GetFullPath(target);
            }

            if (PackageFileNames.IsSplitPackageFile(target))
            {
                await AppMessageDialogs.ShowMessageAsync("The file name must not end in \"_dir\" or a chunk number, it would be read as a VPK split into chunk files.", "Failed to save VPK", MessageIcon.Warning).ConfigureAwait(true);
                return false;
            }

            foreach (TabPage otherTab in mainTabs.TabPages)
            {
                if (GetPackageViewer(otherTab) is { HasUnsavedChanges: true } otherViewer && otherViewer != viewer && otherViewer.FileOnDisk is { } otherFile && otherFile.Equals(target, StringComparison.OrdinalIgnoreCase))
                {
                    await AppMessageDialogs.ShowMessageAsync($"\"{Path.GetFileName(target)}\" has unsaved changes in another tab, save or close it first.", "Failed to save VPK", MessageIcon.Warning).ConfigureAwait(true);
                    return false;
                }
            }

            var tempPath = string.Concat(target, ".tmp");

            Log.Info(nameof(MainForm), $"Packing to \"{tempPath}\"...");

            try
            {
                viewer.WritePackage(tempPath);
            }
            catch (Exception e)
            {
                DeleteTemporaryFile(tempPath);

                if (e is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    await AppMessageDialogs.ShowMessageAsync(e.Message, "Failed to save VPK", MessageIcon.Error).ConfigureAwait(true);
                }
                else
                {
                    ErrorReport.ShowError(e, VrfGuiContext.GetDisplayPath(target));
                }

                return false;
            }

            var originalFile = viewer.FileOnDisk;
            var index = mainTabs.GetTabIndex(tab);
            var holders = CloseTabsUsing(target, viewer.GuiContext);
            var released = Task.WhenAll(holders.ConvertAll(static context => context.DisposalCompleted));

            if (await Task.WhenAny(released, Task.Delay(PackageReleaseTimeout)).ConfigureAwait(true) != released)
            {
                await AppMessageDialogs.ShowMessageAsync($"\"{target}\" is still open, the VPK was saved to \"{tempPath}\" instead.", "Failed to save VPK", MessageIcon.Error).ConfigureAwait(true);
                await ReopenOriginalFileAsync().ConfigureAwait(true);
                return false;
            }

            try
            {
                File.Move(tempPath, target, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                await AppMessageDialogs.ShowMessageAsync($"Failed to replace \"{target}\": {e.Message}\n\nThe VPK was saved to \"{tempPath}\" instead.", "Failed to save VPK", MessageIcon.Error).ConfigureAwait(true);
                await ReopenOriginalFileAsync().ConfigureAwait(true);
                return false;
            }

            Log.Info(nameof(MainForm), $"Saved {VrfGuiContext.GetDisplayPath(target)}");

            if (reopen)
            {
                await OpenPackageEditorAsync(target, index).ConfigureAwait(true);
            }

            return true;

            async Task ReopenOriginalFileAsync()
            {
                if (reopen && originalFile != null && File.Exists(originalFile))
                {
                    await OpenPackageEditorAsync(originalFile, index).ConfigureAwait(true);
                }
            }
        }

        private static void DeleteTemporaryFile(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Warn(nameof(MainForm), $"Failed to delete \"{path}\": {e.Message}");
            }
        }

        /// <summary>
        /// Asks to save the changes of a package being edited before its tab closes.
        /// </summary>
        /// <returns>Whether to close the tab now.</returns>
        private bool ConfirmCloseTab(TabPage tab)
        {
            if (GetPackageViewer(tab) is not { HasUnsavedChanges: true } viewer)
            {
                return true;
            }

            mainTabs.SelectTab(tab);

            switch (AskToSavePackage(viewer))
            {
                case SaveChangesChoice.DontSave:
                    return true;

                case SaveChangesChoice.Save:
                    _ = SavePackageAsync(viewer, tab, saveAs: false, reopen: false);
                    return false;

                default:
                    return false;
            }
        }

        private static SaveChangesChoice AskToSavePackage(PackageViewer viewer)
        {
            var name = viewer.FileOnDisk is { } path ? Path.GetFileName(path) : "the new VPK";

            return AppMessageDialogs.AskToSaveChanges($"Save changes to {name}?", "Unsaved changes");
        }

        /// <summary>
        /// Asks to save the changes of every package being edited before the app closes.
        /// </summary>
        /// <returns>Whether the app can close now.</returns>
        private bool ConfirmCloseApp() => ConfirmCloseApp([]);

        /// <param name="discarded">Packages whose changes the user chose not to save, they are not asked about again.</param>
        private bool ConfirmCloseApp(HashSet<PackageViewer> discarded)
        {
            foreach (TabPage tab in mainTabs.TabPages)
            {
                if (GetPackageViewer(tab) is not { HasUnsavedChanges: true } viewer || discarded.Contains(viewer))
                {
                    continue;
                }

                mainTabs.SelectTab(tab);

                switch (AskToSavePackage(viewer))
                {
                    case SaveChangesChoice.DontSave:
                        discarded.Add(viewer);
                        continue;

                    case SaveChangesChoice.Save:
                        _ = SaveAndCloseAppAsync(viewer, tab, discarded);
                        return false;

                    default:
                        return false;
                }
            }

            return true;
        }

        private async Task SaveAndCloseAppAsync(PackageViewer viewer, TabPage tab, HashSet<PackageViewer> discarded)
        {
            if (!await SavePackageAsync(viewer, tab, saveAs: false, reopen: false).ConfigureAwait(true) || !ConfirmCloseApp(discarded))
            {
                return;
            }

            foreach (var discardedViewer in discarded)
            {
                discardedViewer.DiscardChanges();
            }

            Close();
        }
    }
}
