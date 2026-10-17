using System.Buffers;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using GUI.Forms;
using GUI.Types.Viewers;
using GUI.Utils;
using ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.Blocks.ResourceEditInfoStructs;
using ValveResourceFormat.IO;

namespace GUI.Types.PackageViewer
{
#pragma warning disable CA1001 // TreeView is not owned by this class, set to null in VPK_Disposed
    class PackageViewer(VrfGuiContext vrfGuiContext) : IViewer, IDisposable
#pragma warning restore CA1001
    {
#pragma warning disable CA2213 // TODO: Can we fix TreeView to be owned by this class?
        private TreeViewWithSearchResults? TreeView;
#pragma warning restore CA2213
        private VirtualPackageNode? VirtualRoot;
        private VirtualPackageNode? ContextFolder;

        /// <summary>
        /// Whether this package is opened for editing, with the editing context menu.
        /// </summary>
        public bool IsEditingPackage { get; private set; }

        /// <summary>
        /// Whether files were added to or removed from the package since it was opened for editing.
        /// </summary>
        public bool HasUnsavedChanges { get; private set; }

        public VrfGuiContext GuiContext => vrfGuiContext;

        /// <summary>
        /// Full path of the package file, or <see langword="null"/> for a new package that was never saved.
        /// </summary>
        public string? FileOnDisk => Path.IsPathRooted(vrfGuiContext.FileName) ? Path.GetFullPath(vrfGuiContext.FileName) : null;

        /// <summary>
        /// Whether this package can be opened for editing: a single file package on disk, not one inside of another package.
        /// </summary>
        public bool CanEdit => !IsEditingPackage
            && vrfGuiContext.ParentGuiContext == null
            && FileOnDisk != null
            && vrfGuiContext.CurrentPackage is { IsDirVPK: false };

        public bool IsSigned => vrfGuiContext.CurrentPackage?.SignatureSectionSize > 0;

        public static bool IsAccepted(uint magic)
        {
            return magic == ValvePak.Package.MAGIC;
        }

        public Control CreateEmpty()
        {
            var package = new Package();
            package.OptimizeEntriesForBinarySearch(StringComparison.OrdinalIgnoreCase);

            return CreateEditor(package);
        }

        /// <summary>
        /// Opens the package file of the context for editing.
        /// </summary>
        public Control CreateEditor()
        {
            var package = new Package();

            try
            {
                package.OptimizeEntriesForBinarySearch(StringComparison.OrdinalIgnoreCase);
                package.Read(vrfGuiContext.FileName);

                if (package.IsDirVPK)
                {
                    throw new InvalidDataException("Only VPKs that are a single file can be edited, not ones split into chunk files.");
                }

                return CreateEditor(package);
            }
            catch
            {
                package.Dispose();
                throw;
            }
        }

        private TreeViewWithSearchResults CreateEditor(Package package)
        {
            IsEditingPackage = true;

            vrfGuiContext.CurrentPackage = package;
            BuildVirtualRoot(package);
            CreateTreeViewWithSearchResults();

            return TreeView ?? throw new InvalidOperationException("TreeView was not created");
        }

        public async Task LoadAsync(Stream? stream)
        {
            var package = new Package();
            package.OptimizeEntriesForBinarySearch(StringComparison.OrdinalIgnoreCase);

            if (stream != null)
            {
                package.SetFileName(vrfGuiContext.FileName);
                package.Read(stream);
            }
            else
            {
                package.Read(vrfGuiContext.FileName);
            }

            vrfGuiContext.CurrentPackage = package;

            BuildVirtualRoot(package);
        }

        private void BuildVirtualRoot(Package package)
        {
            VirtualRoot = new VirtualPackageNode("root", 0, null);

            if (package.Entries != null)
            {
                foreach (var fileType in package.Entries)
                {
                    foreach (var file in fileType.Value)
                    {
                        BetterTreeView.AddFileNode(VirtualRoot, file);
                    }
                }
            }

            foreach (var node in VirtualRoot.Folders)
            {
                VirtualRoot.TotalSize += node.Value.TotalSize;
            }

            foreach (var node in VirtualRoot.Files)
            {
                VirtualRoot.TotalSize += node.TotalLength;
            }
        }

        public void Create(TabPage tab)
        {
            CreateTreeViewWithSearchResults();
            tab.Controls.Add(TreeView);
        }

        private void CreateTreeViewWithSearchResults()
        {
            if (VirtualRoot == null)
            {
                throw new InvalidOperationException("VirtualRoot must be initialized before creating TreeView");
            }

            // create a TreeView with search capabilities, register its events, and add it to the tab
            TreeView = new TreeViewWithSearchResults(this);
            TreeView.InitializeTreeViewFromPackage(vrfGuiContext, VirtualRoot);
            TreeView.OpenPackageEntry += VPK_OpenFile;
            TreeView.OpenContextMenu += VPK_OnContextMenu;
            TreeView.PreviewFile += VPK_PreviewFile;
            TreeView.PreviewCleared += VPK_PreviewCleared;
            TreeView.PreviewFocused += VPK_PreviewFocused;
            TreeView.PreviewBlurred += VPK_PreviewBlurred;
            TreeView.Disposed += VPK_Disposed;
        }

        public void AddFolder(string directory)
        {
            if (TreeView == null)
            {
                return;
            }

            directory = PackageInputFiles.GetEntryPath(directory).Trim(Package.DirectorySeparatorChar);

            TreeView.AddFolderNode(JoinPath(GetFolderPath(GetContextFolder()), directory));
            TreeView.RefreshListView();
        }

        public void AddFilesFromFolder(string inputDirectory)
        {
            List<PackageInputFile> files;

            try
            {
                files = PackageInputFiles.CollectFromFolder(inputDirectory, FileOnDisk);
            }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                _ = AppMessageDialogs.ShowMessageAsync(e.Message, "Failed to add folder", MessageIcon.Error);
                return;
            }

            AddFiles(files.Select(static file => (file.PathOnDisk, file.EntryPath)));
        }

        public void AddFiles(IEnumerable<string> files)
        {
            var fileOnDisk = FileOnDisk;

            AddFiles(files
                .Where(file => fileOnDisk == null || !Path.GetFullPath(file).Equals(fileOnDisk, StringComparison.OrdinalIgnoreCase))
                .Select(static file => (file, PackageInputFiles.GetEntryPath(Path.GetFileName(file)))));
        }

        /// <summary>
        /// Adds files on disk to the folder the context menu was opened on, replacing files that already exist.
        /// </summary>
        private void AddFiles(IEnumerable<(string PathOnDisk, string EntryPath)> files)
        {
            if (TreeView == null || vrfGuiContext.CurrentPackage is not { } package)
            {
                return;
            }

            var folder = GetContextFolder();
            var folderPath = GetFolderPath(folder);
            var replacedFiles = new HashSet<PackageEntry>();
            var failedFiles = new List<string>();

            Cursor.Current = Cursors.WaitCursor;
            TreeView.BeginUpdate();

            foreach (var (pathOnDisk, entryPath) in files)
            {
                byte[] data;

                try
                {
                    data = File.ReadAllBytes(pathOnDisk);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    failedFiles.Add($"{pathOnDisk}: {e.Message}");
                    continue;
                }

                var fullPath = JoinPath(folderPath, entryPath);

                if (package.FindEntry(fullPath) is { } existingEntry)
                {
                    package.RemoveFile(existingEntry);
                    TreeView.RemoveFileNode(existingEntry);
                    replacedFiles.Add(existingEntry);
                }

                TreeView.AddFileNode(package.AddFile(fullPath, data));
                HasUnsavedChanges = true;
            }

            TreeView.EndUpdate();
            folder.CreatedNode?.Expand();
            TreeView.RefreshListView(replacedFiles);
            Cursor.Current = Cursors.Default;

            if (failedFiles.Count > 0)
            {
                const int MaxListedFailures = 10;
                var listedFailures = string.Join('\n', failedFiles.Take(MaxListedFailures));
                var message = failedFiles.Count > MaxListedFailures
                    ? string.Create(CultureInfo.InvariantCulture, $"Failed to read {failedFiles.Count} files:\n{listedFailures}\nand {failedFiles.Count - MaxListedFailures} more")
                    : string.Create(CultureInfo.InvariantCulture, $"Failed to read {failedFiles.Count} files:\n{listedFailures}");

                _ = AppMessageDialogs.ShowMessageAsync(message, "Failed to add files", MessageIcon.Error);
            }
        }

        /// <summary>
        /// Removes files, and folders with all of the files in them, from the package.
        /// </summary>
        public void RemoveItems(IEnumerable<IBetterBaseItem> items)
        {
            if (TreeView == null || vrfGuiContext.CurrentPackage is not { } package)
            {
                return;
            }

            var removedFiles = new HashSet<PackageEntry>();

            TreeView.BeginUpdate();

            foreach (var item in items)
            {
                if (item.PackageEntry is { } entry)
                {
                    if (package.RemoveFile(entry))
                    {
                        removedFiles.Add(entry);
                        TreeView.RemoveFileNode(entry);
                    }
                }
                else if (item.PkgNode is { Parent: not null } folder)
                {
                    foreach (var file in TreeViewWithSearchResults.EnumerateFiles(folder))
                    {
                        package.RemoveFile(file);
                        removedFiles.Add(file);
                    }

                    TreeView.RemoveFolderNode(folder);
                }
            }

            TreeView.EndUpdate();
            TreeView.RefreshListView(removedFiles);

            if (removedFiles.Count > 0)
            {
                HasUnsavedChanges = true;
            }
        }

        /// <summary>
        /// Renames a file or a folder, keeping it in the same folder. Refuses names that other files already have.
        /// </summary>
        public void RenameItem(IBetterBaseItem item, string newName)
        {
            if (TreeView == null)
            {
                return;
            }

            newName = PackageInputFiles.GetEntryPath(newName.Trim());

            if (newName.Length == 0 || newName.Contains(Package.DirectorySeparatorChar, StringComparison.Ordinal) || newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                _ = AppMessageDialogs.ShowMessageAsync($"\"{newName}\" is not a valid name.", "Failed to rename", MessageIcon.Warning);
                return;
            }

            if (item.PackageEntry is { } entry)
            {
                var directory = entry.DirectoryName == " " ? string.Empty : entry.DirectoryName;
                RenameFiles([(entry, JoinPath(directory, newName))], null);
                return;
            }

            if (item.PkgNode is not { Parent: not null } folder)
            {
                return;
            }

            var oldFolderPath = GetFolderPath(folder);
            var newFolderPath = JoinPath(GetFolderPath(folder.Parent), newName);
            var files = TreeViewWithSearchResults.EnumerateFiles(folder)
                .Select(file => (file, string.Concat(newFolderPath, file.GetFullPath().AsSpan(oldFolderPath.Length))))
                .ToList();
            var emptyFolders = EnumerateEmptyFolders(folder)
                .Select(emptyFolder => string.Concat(newFolderPath, GetFolderPath(emptyFolder).AsSpan(oldFolderPath.Length)))
                .ToList();

            if (!RenameFiles(files, folder) || emptyFolders.Count == 0)
            {
                return;
            }

            foreach (var emptyFolder in emptyFolders)
            {
                TreeView.AddFolderNode(emptyFolder);
            }

            TreeView.RefreshListView();
        }

        private static IEnumerable<VirtualPackageNode> EnumerateEmptyFolders(VirtualPackageNode folder)
        {
            if (folder.Files.Count == 0 && folder.Folders.Count == 0)
            {
                yield return folder;
            }

            foreach (var subFolder in folder.Folders.Values)
            {
                foreach (var emptyFolder in EnumerateEmptyFolders(subFolder))
                {
                    yield return emptyFolder;
                }
            }
        }

        /// <summary>
        /// Moves files to new paths, after removing the nodes of the folder they are moved out of, or of the files themselves.
        /// </summary>
        /// <returns>Whether the files were moved, they are not when another file already has one of the new paths.</returns>
        private bool RenameFiles(List<(PackageEntry Entry, string NewPath)> files, VirtualPackageNode? movedFolder)
        {
            var package = vrfGuiContext.CurrentPackage!;
            var renamedEntries = files.Select(static file => file.Entry).ToHashSet();

            foreach (var (_, newPath) in files)
            {
                if (package.FindEntry(newPath) is { } existingEntry && !renamedEntries.Contains(existingEntry))
                {
                    _ = AppMessageDialogs.ShowMessageAsync($"\"{existingEntry.GetFullPath()}\" already exists.", "Failed to rename", MessageIcon.Warning);
                    return false;
                }
            }

            TreeView!.BeginUpdate();

            if (movedFolder != null)
            {
                TreeView.RemoveFolderNode(movedFolder);
            }
            else
            {
                foreach (var (entry, _) in files)
                {
                    TreeView.RemoveFileNode(entry);
                }
            }

            foreach (var (entry, newPath) in files)
            {
                package.RenameFile(entry, newPath);
                TreeView.AddFileNode(entry);
            }

            TreeView.EndUpdate();
            TreeView.RefreshListView();

            if (files.Count > 0)
            {
                HasUnsavedChanges = true;
            }

            return true;
        }

        /// <summary>
        /// Forgets the changes, so closing the package does not ask to save them.
        /// </summary>
        public void DiscardChanges() => HasUnsavedChanges = false;

        /// <summary>
        /// Writes the package to a file.
        /// </summary>
        /// <exception cref="InvalidOperationException">The files add up to more than a single file package can hold.</exception>
        public void WritePackage(string path)
        {
            var package = vrfGuiContext.CurrentPackage ?? throw new InvalidOperationException("There is no package to write.");
            var totalSize = package.Entries?.Values.Sum(static entries => entries.Sum(static entry => (long)entry.TotalLength)) ?? 0;

            if (totalSize > int.MaxValue)
            {
                throw new InvalidOperationException("The files add up to more than 2 GiB, which does not fit into a single VPK. Use the CLI with --vpk_create_chunk_size to split it into chunk files.");
            }

            package.Write(path);
        }

        internal static List<PackageEntry> RecoverDeletedFiles(Package package, GenericProgressForm progress)
        {
            if (package.Entries == null)
            {
                return [];
            }

            var allEntries = package.Entries
                .SelectMany(file => file.Value)
                .OrderBy(file => file.Offset)
                .GroupBy(file => file.ArchiveIndex)
                .OrderBy(x => x.Key)
                .ToDictionary(x => x.Key, x => x.ToList());

            // Every known entry has a gap in front of it to scan, so that is the unit of progress
            progress.SetBarMax(allEntries.Sum(x => x.Value.Count));
            var processed = 0;

            var hiddenIndex = 0;
            var totalSlackSize = 0u;
            var hiddenFiles = new List<PackageEntry>();
            var kv3header = Encoding.ASCII.GetBytes("<!-- kv3 ");
            var previousArchiveIndex = 0;

            foreach (var (archiveIndex, entries) in allEntries)
            {
                if (archiveIndex - previousArchiveIndex > 1)
                {
                    Log.Warn(nameof(PackageViewer), $"There is probably an unused {previousArchiveIndex:D3}.vpk");
                }

                previousArchiveIndex = archiveIndex;

                var nextOffset = 0u;

                void FindValidFiles(uint entryOffset, uint entryLength)
                {
                    var offset = nextOffset;
                    nextOffset = entryOffset + entryLength;

                    totalSlackSize += entryOffset - offset;

                    var scan = true;

                    while (scan)
                    {
                        scan = false;

                        if (offset == entryOffset)
                        {
                            break;
                        }

                        offset = offset + 16 - 1 & ~(16u - 1); // TODO: Validate this gap

                        var length = entryOffset - offset;

                        if (length <= 16)
                        {
                            // TODO: Verify what this gap is, seems to be null bytes
                            break;
                        }

                        hiddenIndex++;
                        var newEntry = new PackageEntry
                        {
                            FileName = $"Archive {archiveIndex:D3} File {hiddenIndex}",
                            DirectoryName = "Undetected filenames",
                            TypeName = " ",
                            CRC32 = 0,
                            SmallData = [],
                            ArchiveIndex = archiveIndex,
                            Offset = offset,
                            Length = length,
                        };

                        var bytes = ArrayPool<byte>.Shared.Rent((int)newEntry.TotalLength);

                        try
                        {
                            package.ReadEntry(newEntry, bytes, validateCrc: false);
                            using var stream = new MemoryStream(bytes, 0, (int)newEntry.TotalLength);
                            using var resource = new ValveResourceFormat.Resource();
                            resource.Read(stream, mode: ReadMode.Deferred);

                            var fileSize = resource.FullFileSize;

                            if (fileSize != length)
                            {
                                if (fileSize > length)
                                {
                                    throw new InvalidDataException("Resource filesize is bigger than the gap length we found");
                                }

                                newEntry.Length = fileSize;
                                offset += fileSize;
                                scan = true;
                            }

                            string? resourceTypeExtensionWithDot = null;

                            if (resource.ResourceType != ResourceType.Unknown)
                            {
                                var resourceTypeExtension = resource.ResourceType.GetExtension();
                                resourceTypeExtensionWithDot = string.Concat(".", resourceTypeExtension);
                                newEntry.TypeName = string.Concat(resourceTypeExtension, GameFileLoader.CompiledFileSuffix);
                            }

                            string? filepath = null;

                            // Use input dependency as the file name if there is one
                            if (resource.EditInfo != null)
                            {
                                filepath = RecoverDeletedFilesGetPossiblePath(resource.EditInfo.InputDependencies, resourceTypeExtensionWithDot);
                                filepath ??= RecoverDeletedFilesGetPossiblePath(resource.EditInfo.AdditionalInputDependencies, resourceTypeExtensionWithDot);

                                // Fix panorama extension
                                if (filepath != null && resourceTypeExtensionWithDot == ".vtxt")
                                {
                                    newEntry.TypeName = string.Concat(Path.GetExtension(filepath)[1..], GameFileLoader.CompiledFileSuffix);
                                }
                            }

                            if (filepath != null)
                            {
                                var dirName = Path.GetDirectoryName(filepath);
                                if (dirName != null)
                                {
                                    newEntry.DirectoryName = dirName.Replace('\\', ValvePak.Package.DirectorySeparatorChar);
                                }
                                newEntry.FileName = Path.GetFileNameWithoutExtension(filepath);
                            }
                            else
                            {
                                newEntry.DirectoryName += string.Concat(ValvePak.Package.DirectorySeparatorChar, resource.ResourceType);
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Debug(nameof(PackageViewer), $"Failed to parse \"{newEntry.FileName}\": {ex.Message}");

                            newEntry.FileName += $" ({length} bytes)";

                            var span = bytes.AsSpan(0, (int)newEntry.TotalLength);

                            if (span.StartsWith(kv3header))
                            {
                                newEntry.TypeName = "kv3";
                            }
                            else if (!span.Contains((byte)0))
                            {
                                newEntry.TypeName = "txt";
                            }
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(bytes);
                        }

                        hiddenFiles.Add(newEntry);

                        progress.SetProgress($"Found {hiddenFiles.Count} files ({HumanReadableByteSizeFormatter.Format(totalSlackSize)}) so far…");
                    }
                }

                // Recover files in gaps between entries
                foreach (var entry in entries)
                {
                    progress.SetBarValue(++processed);

                    if (entry.Length == 0)
                    {
                        continue;
                    }

                    FindValidFiles(entry.Offset, entry.Length);
                }

                // Recover files in archives after last possible entry for that archive
                if (archiveIndex != short.MaxValue)
                {
                    var archiveFileSize = nextOffset;
                    var archiveFileName = PackageFileNames.GetArchiveChunkFile(package.FileName!, archiveIndex);

                    try
                    {
                        archiveFileSize = (uint)new FileInfo(archiveFileName).Length;
                    }
                    catch (Exception e)
                    {
                        Log.Debug(nameof(PackageViewer), $"Failed to get size of \"{Path.GetFileName(archiveFileName)}\": {e.Message}");
                    }

                    if (archiveFileSize != nextOffset)
                    {
                        FindValidFiles(archiveFileSize, 0);
                    }
                }
            }

            Log.Info(nameof(PackageViewer), $"Found {hiddenIndex} deleted files totaling {HumanReadableByteSizeFormatter.Format(totalSlackSize)}");

            return hiddenFiles;
        }

        private static string? RecoverDeletedFilesGetPossiblePath(List<InputDependency> inputDeps, string? resourceTypeExtensionWithDot)
        {
            if (inputDeps.Count == 0)
            {
                return null;
            }

            foreach (var inputDependency in inputDeps)
            {
                if (Path.GetExtension(inputDependency.ContentRelativeFilename) == resourceTypeExtensionWithDot)
                {
                    return inputDependency.ContentRelativeFilename;
                }
            }

            // We can't detect correct panorama file type from compiler information, so we have to guess
            if (resourceTypeExtensionWithDot == ".vtxt")
            {
                var preferredExtensions = new string[]
                {
                    ".vcss",
                    ".vxml",
                    ".vpdi",
                    ".vjs",
                    ".vts",
                };

                foreach (var inputDependency in inputDeps)
                {
                    if (preferredExtensions.Contains(Path.GetExtension(inputDependency.ContentRelativeFilename)))
                    {
                        return inputDependency.ContentRelativeFilename;
                    }
                }
            }

            return inputDeps[0].ContentRelativeFilename;
        }

        private void VPK_Disposed(object? sender, EventArgs e)
        {
            if (sender is TreeViewWithSearchResults treeViewWithSearch)
            {
                treeViewWithSearch.OpenPackageEntry -= VPK_OpenFile;
                treeViewWithSearch.OpenContextMenu -= VPK_OnContextMenu;
                treeViewWithSearch.PreviewFile -= VPK_PreviewFile;
                treeViewWithSearch.PreviewCleared -= VPK_PreviewCleared;
                treeViewWithSearch.PreviewFocused -= VPK_PreviewFocused;
                treeViewWithSearch.PreviewBlurred -= VPK_PreviewBlurred;
                treeViewWithSearch.Disposed -= VPK_Disposed;
                TreeView = null;
                ContextFolder = null;
            }
        }

        /// <summary>
        /// Opens the given package entry in a new tab.
        /// </summary>
        /// <param name="sender">Object which raised event.</param>
        /// <param name="entry">The package entry to open.</param>
        private void VPK_OpenFile(object? sender, PackageEntry entry)
        {
            var newVrfGuiContext = new VrfGuiContext(entry.GetFullPath(), vrfGuiContext);
            Program.MainForm.OpenFile(newVrfGuiContext, entry);
        }

        private void VPK_PreviewCleared(object? sender, EventArgs e)
        {
            // A folder is shown instead of a file preview, so the window title should no longer reflect a file.
            Program.MainForm.ResetPreviewTitle();
        }

        private void VPK_PreviewFocused(object? sender, TabPage previewTab)
        {
            Program.MainForm.ShowPreviewKeybindings(previewTab);
        }

        private void VPK_PreviewBlurred(object? sender, EventArgs e)
        {
            Program.MainForm.ShowSelectedTabKeybindings();
        }

        private void VPK_PreviewFile(object? sender, PackageEntry entry)
        {
            if (TreeView == null)
            {
                return;
            }

            if (!TreeViewWithSearchResults.CanQuickPreviewFile(entry))
            {
                return;
            }

            var newVrfGuiContext = new VrfGuiContext(entry.GetFullPath(), vrfGuiContext);
            Program.MainForm.OpenFile(newVrfGuiContext, entry, TreeView);
        }

        /// <summary>
        /// Folder the context menu was opened on, files are added into it.
        /// </summary>
        private VirtualPackageNode GetContextFolder() => ContextFolder ?? VirtualRoot!;

        private static string GetFolderPath(VirtualPackageNode folder)
        {
            var names = new Stack<string>();

            for (var node = folder; node.Parent != null; node = node.Parent)
            {
                names.Push(node.Name);
            }

            return string.Join(Package.DirectorySeparatorChar, names);
        }

        private static string JoinPath(string folderPath, string path)
            => folderPath.Length > 0 ? string.Join(Package.DirectorySeparatorChar, folderPath, path) : path;

        /// <summary>
        /// Opens a context menu where the user right-clicked in the TreeView or ListView.
        /// </summary>
        /// <param name="sender">Object which raised event.</param>
        /// <param name="e">Event data.</param>
        private void VPK_OnContextMenu(object? sender, PackageContextMenuEventArgs e)
        {
            if (TreeView == null)
            {
                return;
            }

            var isRoot = e.PkgNode == TreeView.mainTreeView.Root;
            var isFolder = e.PackageEntry is null;

            if (e.TreeNode is not null)
            {
                isFolder = e.TreeNode.IsFolder;
            }

            if (sender is not Control control)
            {
                return;
            }

            if (IsEditingPackage)
            {
                ContextFolder = e.PkgNode
                    ?? (e.TreeNode?.Parent as BetterTreeNode)?.PkgNode
                    ?? (e.PackageEntry != null ? TreeView.FindFolderNode(e.PackageEntry.DirectoryName) : null);

                Program.MainForm.ShowVpkEditingContextMenu(control, e.Location, isRoot, isFolder, hasItem: !e.IsBackground);
                return;
            }

            Program.MainForm.ShowVpkContextMenu(control, e.Location, isRoot, isFolder, TreeView.DeletedFilesRecovered, CanEdit);
        }

        public void Dispose()
        {
            TreeView = null;
        }
    }
}
