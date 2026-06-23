using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using GUI.Types.Exporter;
using GUI.Types.PackageViewer;
using GUI.Utils;
using SteamDatabase.ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.IO;

namespace GUI.Forms
{
    partial class ExtractProgressForm : ThemedForm
    {
        private class ExtractProgress(Action<string> SetProgress) : IProgress<string>
        {
            public void Report(string value) => SetProgress(value);
        }

        private class FileTypeToExtract
        {
            public string? OutputFormat;
            public int Count = 1;
        }

        // A file to extract, carrying the package and context it belongs to (which differ for nested vpks)
        private readonly record struct QueuedFile(Package Package, VrfGuiContext Context, PackageEntry Entry);

        private readonly bool decompile;
        private string? path;
        private readonly ExportData exportData;
        private readonly Dictionary<string, Queue<QueuedFile>> filesToExtractSorted = [];
        private readonly Dictionary<string, FileTypeToExtract> fileTypesToExtract = [];
        private readonly Queue<QueuedFile> filesToExtract = new();
        private readonly HashSet<string> extractedFiles = [];
        // Nested vpks opened while queuing; kept alive until extraction finishes
        private readonly List<VrfGuiContext> nestedContexts = [];
        private CancellationTokenSource cancellationTokenSource = new();
        private readonly GltfModelExporter? gltfExporter;
        private readonly IProgress<string> progressReporter;
        private readonly Stopwatch exportStopwatch = new();
        private int filesFailedToExport;

        private static readonly List<ResourceType> ExtractOrder =
        [
            ResourceType.Map,
            ResourceType.World,
            ResourceType.WorldNode,
            ResourceType.Model,
            ResourceType.Mesh,
            ResourceType.AnimationGroup,
            ResourceType.Animation,
            ResourceType.Sequence,
            ResourceType.Morph,

            ResourceType.Material,
            ResourceType.Texture,
        ];

        public Action<ExtractProgressForm, CancellationToken>? ShownCallback { get; init; }

        public ExtractProgressForm(ExportData exportData, string? path, bool decompile)
        {
            InitializeComponent();

            foreach (var resourceType in ExtractOrder)
            {
                var extension = resourceType.GetExtension();
                filesToExtractSorted.Add(extension + GameFileLoader.CompiledFileSuffix, new());
            }

            this.path = path;
            this.decompile = decompile;
            this.exportData = exportData;
            progressReporter = new ExtractProgress(SetProgress);

            if (decompile)
            {
                // We need to know what files were handled by the glTF exporter
                var trackingFileLoader = new TrackingFileLoader(exportData.VrfGuiContext);

                gltfExporter = new GltfModelExporter(trackingFileLoader)
                {
                    ProgressReporter = progressReporter,
                };
            }
        }

        public void ExecuteMultipleFileExtract()
        {
            if (filesToExtract.Count == 0 && filesToExtractSorted.Sum(x => x.Value.Count) == 0)
            {
                MessageBox.Show("There are no files to extract", "Failed to extract");
                return;
            }

            if (decompile && ShowTypesDialog() != DialogResult.Continue)
            {
                return;
            }

            using var dialog = new FolderBrowserDialog
            {
                Description = "Choose which folder to extract files to",
                UseDescriptionForTitle = true,
                SelectedPath = Settings.Config.SaveDirectory,
                AddToRecent = true,
            };

            if (dialog.ShowDialog() != DialogResult.OK)
            {
                return;
            }

            path = dialog.SelectedPath;
            Settings.Config.SaveDirectory = dialog.SelectedPath;

            ShowDialog();
        }

        private DialogResult ShowTypesDialog()
        {
            using var typesDialog = new ExtractOutputTypesForm();
            typesDialog.ChangeTypeEvent += OnTypesDialogSelectedValueChanged;

            // Only when decompiling a single vmap on its own do we default everything else off
            // (its pulled-in dependencies). Extracting a bundle (vpk/folder) defaults every type to export.
            var onlyVmap = fileTypesToExtract.Sum(x => x.Value.Count) == 1
                && fileTypesToExtract.ContainsKey("vmap_c");

            foreach (var type in fileTypesToExtract.OrderByDescending(x => x.Value.Count))
            {
                /// See <see cref="ResourceTypeExtensions.GetExtension(ResourceType)"/>
                var firstType = type.Key switch
                {
                    "vjs_c" => "js",
                    "vts_c" => "js",
                    "vxml_c" => "xml",
                    "vcss_c" => "css",
                    "vsvg_c" => "svg",
                    _ when type.Key.EndsWith(GameFileLoader.CompiledFileSuffix, StringComparison.OrdinalIgnoreCase) => type.Key[..^2],
                    _ => type.Key,
                };

                var outputTypes = new List<string>()
                {
                    "* Do not export *",
                    firstType,
                };

                if (firstType is "vmdl" or "vmesh" or "vmap" or "vwrld" or "vwnod" or "vnmclip")
                {
                    outputTypes.Add("gltf");
                    outputTypes.Add("glb");
                }
                else if (firstType == "vtex")
                {
                    outputTypes.Insert(1, "image");
                }
                else if (firstType == "vsnd")
                {
                    outputTypes.Insert(1, "sound");
                }

                // Select first suggested type, the 0th item is always "do not export"
                var selectedIndex = onlyVmap
                    ? firstType is "vmap" ? 1 : 0
                    : 1;

                type.Value.OutputFormat = selectedIndex == 0 ? null : outputTypes[selectedIndex];
                typesDialog.AddTypeToTable(type.Key, type.Value.Count, outputTypes, selectedIndex);
            }

            var result = typesDialog.ShowDialog();
            typesDialog.ChangeTypeEvent -= OnTypesDialogSelectedValueChanged;

            return result;
        }

        private void OnTypesDialogSelectedValueChanged(object? sender, EventArgs e)
        {
            if (sender is not ComboBox control)
            {
                return;
            }

            if (control.Tag is not string type)
            {
                return;
            }

            // TODO: Remember last selected value in settings?
            if (control.SelectedIndex == 0)
            {
                fileTypesToExtract[type].OutputFormat = null;
                return;
            }

            fileTypesToExtract[type].OutputFormat = control.SelectedItem as string;
        }

        protected override void OnShown(EventArgs e)
        {
            exportStopwatch.Restart();

            if (ShownCallback != null)
            {
                extractProgressBar.Style = ProgressBarStyle.Marquee;
                ShownCallback(this, cancellationTokenSource.Token);
                return;
            }

            Task.Run(async () =>
            {
                SetProgress($"Folder export started to \"{path}\"");

                if (decompile)
                {
                    foreach (var resourceType in ExtractOrder)
                    {
                        var extension = resourceType.GetExtension();
                        var files = filesToExtractSorted[extension + GameFileLoader.CompiledFileSuffix];

                        if (files.Count > 0)
                        {
                            SetProgress($"Extracting {resourceType}s…");
                            await ExtractFilesAsync(files).ConfigureAwait(false);
                        }
                    }

                    if (filesToExtract.Count > 0)
                    {
                        SetProgress("Extracting files…");
                    }
                }

                await ExtractFilesAsync(filesToExtract).ConfigureAwait(false);
            }, cancellationTokenSource.Token).ContinueWith(ExportContinueWith, CancellationToken.None);
        }

        public void ExportContinueWith(Task t)
        {
            exportStopwatch.Stop();

            foreach (var context in nestedContexts)
            {
                context.Dispose();
            }

            nestedContexts.Clear();

            if (t.IsFaulted)
            {
                var ex = t.Exception.ToString();
                Log.Error(nameof(ExtractProgressForm), ex);
                SetProgress(ex);

                cancellationTokenSource.Cancel();
            }

            Invoke(() =>
            {
                if (filesFailedToExport > 0)
                {
                    var nl = Environment.NewLine;
                    SetProgress($"WARNING:{nl}{nl}{filesFailedToExport} files failed to extract, check console for more information.{nl}");
                }

                Text = t.IsFaulted ? "Source 2 Viewer - Export failed, check console for details" : "Source 2 Viewer - Export completed";
                cancelButton.Text = "Close";
                extractProgressBar.Value = 100;
                extractProgressBar.Style = ProgressBarStyle.Blocks;
                extractProgressBar.Update();
            });

            SetProgress($"Export completed in {exportStopwatch.Elapsed}.");
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            cancellationTokenSource.Cancel();
            base.OnFormClosing(e);
        }

        public void QueueFiles(IBetterBaseItem root)
        {
            if (root.IsFolder)
            {
                if (root.PkgNode != null)
                {
                    QueueFiles(root.PkgNode);
                }
            }
            else
            {
                if (root.PackageEntry != null)
                {
                    QueueFiles(root.PackageEntry);
                }
            }
        }

        public void QueueFiles(VirtualPackageNode root)
        {
            foreach (var node in root.Folders)
            {
                QueueFiles(node.Value);
            }

            foreach (var file in root.Files)
            {
                QueueFiles(file);
            }
        }

        public void QueueFiles(PackageEntry file)
        {
            var package = exportData.VrfGuiContext.CurrentPackage;
            if (package == null)
            {
                Log.Error(nameof(ExtractProgressForm), "CurrentPackage is null, cannot queue file");
                return;
            }

            QueueFile(package, exportData.VrfGuiContext, file);
        }

        private void QueueFile(Package package, VrfGuiContext context, PackageEntry file)
        {
            // When decompiling, descend into nested vpks so their contents flow through the
            // same type selection dialog and extraction logic as top-level files. Their contents
            // merge into the same output root as the parent package's files.
            if (decompile && file.TypeName == "vpk")
            {
                QueueNestedVpk(context, file);
                return;
            }

            if (fileTypesToExtract.TryGetValue(file.TypeName, out var fileType))
            {
                fileType.Count++;
            }
            else
            {
                fileTypesToExtract[file.TypeName] = new FileTypeToExtract(); // Type to be filled in later
            }

            var queued = new QueuedFile(package, context, file);

            if (decompile && filesToExtractSorted.TryGetValue(file.TypeName, out var specializedQueue))
            {
                specializedQueue.Enqueue(queued);
                return;
            }

            filesToExtract.Enqueue(queued);
        }

        private void QueueNestedVpk(VrfGuiContext parentContext, PackageEntry vpkEntry)
        {
            var opened = OpenNestedTracked(parentContext, vpkEntry);
            if (opened == null)
            {
                return;
            }

            NestedPackageEnumerator.EnumerateEntries(
                opened.Value.Package,
                opened.Value.State,
                (package, entry, context) => QueueFile(package, context, entry),
                (_, nestedVpkEntry, parent) => OpenNestedTracked(parent, nestedVpkEntry));
        }

        // Opens a nested vpk into a child context kept alive until extraction finishes; logs and counts failures.
        private (Package Package, VrfGuiContext State)? OpenNestedTracked(VrfGuiContext parentContext, PackageEntry vpkEntry)
        {
            VrfGuiContext? childContext = null;

            try
            {
                childContext = ExportFile.OpenNestedPackage(vpkEntry, parentContext);
                if (childContext?.CurrentPackage == null)
                {
                    filesFailedToExport++;
                    Log.Error(nameof(ExtractProgressForm), $"Failed to open nested vpk '{vpkEntry.GetFullPath()}': parent package is null");
                    return null;
                }

                var package = childContext.CurrentPackage;
                nestedContexts.Add(childContext);
                var ownedContext = childContext;
                childContext = null; // ownership transferred to nestedContexts

                return (package, ownedContext);
            }
            catch (Exception e)
            {
                filesFailedToExport++;
                Log.Error(nameof(ExtractProgressForm), $"Failed to open nested vpk '{vpkEntry.GetFullPath()}': {e}");
                return null;
            }
            finally
            {
                childContext?.Dispose();
            }
        }

        private async Task ExtractFilesAsync(Queue<QueuedFile> filesToExtract)
        {
            if (path == null)
            {
                throw new InvalidOperationException("Path must be set before extracting files");
            }

            var initialCount = filesToExtract.Count;
            while (filesToExtract.Count > 0)
            {
                cancellationTokenSource.Token.ThrowIfCancellationRequested();

                var (package, context, packageFile) = filesToExtract.Dequeue();
                var fileFullName = packageFile.GetFullPath();

                if (extractedFiles.Contains(fileFullName))
                {
                    continue;
                }

                await InvokeAsync(() =>
                {
                    extractProgressBar.Value = 100 - (int)(filesToExtract.Count / (float)initialCount * 100.0f);
                }).ConfigureAwait(false);

                var stream = GameFileLoader.GetPackageEntryStream(package, packageFile);
                var outFilePath = Path.Combine(path, fileFullName);
                var outFolder = Path.GetDirectoryName(outFilePath);

                if (decompile && fileTypesToExtract.TryGetValue(packageFile.TypeName, out var outputType))
                {
                    if (outputType.OutputFormat == null)
                    {
                        // Skip this file type
                        continue;
                    }

                    outFilePath = Path.ChangeExtension(outFilePath, outputType.OutputFormat);
                }

                SetProgress($"Extracting {fileFullName}");

                if (outFolder != null)
                {
                    Directory.CreateDirectory(outFolder);
                }

                if (!decompile || !packageFile.TypeName.EndsWith(GameFileLoader.CompiledFileSuffix, StringComparison.Ordinal))
                {
                    // Extract as is
                    using var outStream = File.OpenWrite(outFilePath);
                    await stream.CopyToAsync(outStream).ConfigureAwait(false);

                    continue;
                }

                using var resource = new Resource
                {
                    FileName = fileFullName,
                };

                try
                {
                    resource.Read(stream);
                }
                catch (Exception e)
                {
                    filesFailedToExport++;

                    Log.Error(nameof(ExtractProgressForm), $"Failed to extract '{fileFullName}': {e}");
                    SetProgress($"Failed to extract '{fileFullName}': {e.Message}");

                    continue;
                }

                await ExtractFile(resource, fileFullName, outFilePath, context: context).ConfigureAwait(false);
            }
        }

        public async Task ExtractFile(Resource resource, string inFilePath, string outFilePath, bool flatSubfiles = false, VrfGuiContext? context = null)
        {
            if (path == null)
            {
                throw new InvalidOperationException("Path must be set before extracting files");
            }

            context ??= exportData.VrfGuiContext;

            var outExtension = Path.GetExtension(outFilePath);

            if (GltfModelExporter.CanExport(resource) && outExtension is ".glb" or ".gltf")
            {
                if (gltfExporter == null)
                {
                    Log.Error(nameof(ExtractProgressForm), "gltfExporter is null, cannot export to glTF format");
                    return;
                }

                try
                {
                    gltfExporter.Export(resource, outFilePath, cancellationTokenSource.Token);

                    if (gltfExporter.FileLoader is TrackingFileLoader trackingFileLoader)
                    {
                        extractedFiles.UnionWith(trackingFileLoader.LoadedFilePaths);
                    }
                }
                catch (Exception e)
                {
                    filesFailedToExport++;

                    Log.Error(nameof(ExtractProgressForm), $"Failed to extract '{resource.FileName}': {e}");
                    SetProgress($"Failed to extract '{resource.FileName}': {e.Message}");
                }

                return;
            }

            if (outExtension == ".sound" || outExtension == ".image" || outExtension.EndsWith(GameFileLoader.CompiledFileSuffix, StringComparison.Ordinal))
            {
                var extension = FileExtract.GetExtension(resource);

                if (extension != null)
                {
                    outFilePath = Path.ChangeExtension(outFilePath, extension);
                }
            }
            else if (outExtension == ".vmap")
            {
                flatSubfiles = false;
            }

            ContentFile? contentFile = null;

            try
            {
                contentFile = FileExtract.Extract(resource, context, progressReporter);

                if (contentFile.Data != null)
                {
                    SetProgress($"+ {outFilePath[(path.Length + 1)..]}");
                    await File.WriteAllBytesAsync(outFilePath, contentFile.Data, cancellationTokenSource.Token).ConfigureAwait(false);
                }

                foreach (var additionalFile in contentFile.AdditionalFiles)
                {
                    extractedFiles.Add(additionalFile.FileName + GameFileLoader.CompiledFileSuffix);
                    var fileNameOut = additionalFile.FileName;
                    var flattenThis = flatSubfiles && !additionalFile.KeepFullPath;

                    if (additionalFile.Data != null)
                    {
                        if (flattenThis)
                        {
                            fileNameOut = Path.GetFileName(fileNameOut);
                        }

                        var outPath = CombineAssetFolder(path, fileNameOut);
                        var outPathDirectory = Path.GetDirectoryName(outPath.Full);
                        if (outPathDirectory != null)
                        {
                            Directory.CreateDirectory(outPathDirectory);
                        }
                        SetProgress($" + {outPath.Partial}");
                        await File.WriteAllBytesAsync(outPath.Full, additionalFile.Data, cancellationTokenSource.Token).ConfigureAwait(false);
                    }

                    var contentRelativeFolder = flattenThis ? string.Empty : Path.GetDirectoryName(fileNameOut) ?? string.Empty;

                    await ExtractSubfiles(contentRelativeFolder, additionalFile).ConfigureAwait(false);
                }

                extractedFiles.Add(inFilePath);

                var inFileContentRelativeFolder = flatSubfiles ? string.Empty : Path.GetDirectoryName(inFilePath) ?? string.Empty;

                await ExtractSubfiles(inFileContentRelativeFolder, contentFile).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                filesFailedToExport++;

                Log.Error(nameof(ExtractProgressForm), $"Failed to extract '{inFilePath}': {e}");
                SetProgress($"Failed to extract '{inFilePath}': {e.Message}");
            }
            finally
            {
                contentFile?.Dispose();
            }
        }

        private async Task ExtractSubfiles(string contentRelativeFolder, ContentFile contentFile)
        {
            if (path == null)
            {
                throw new InvalidOperationException("Path must be set before extracting files");
            }

            foreach (var contentSubFile in contentFile.SubFiles)
            {
                cancellationTokenSource.Token.ThrowIfCancellationRequested();
                contentSubFile.FileName = Path.Combine(contentRelativeFolder, contentSubFile.FileName).Replace(Path.DirectorySeparatorChar, '/');
                var outPath = CombineAssetFolder(path, contentSubFile.FileName);

                if (extractedFiles.Contains(contentSubFile.FileName))
                {
                    SetProgress($"  - {outPath.Partial}");
                    continue;
                }

                var outPathDirectory = Path.GetDirectoryName(outPath.Full);
                if (outPathDirectory != null)
                {
                    Directory.CreateDirectory(outPathDirectory);
                }

                if (contentSubFile.Extract == null)
                {
                    Log.Error(nameof(ExtractProgressForm), $"Extract function is null for subfile '{contentSubFile.FileName}'");
                    continue;
                }

                byte[] subFileData;
                try
                {
                    subFileData = contentSubFile.Extract.Invoke();
                }
                catch (Exception e)
                {
                    filesFailedToExport++;

                    Log.Error(nameof(ExtractProgressForm), $"Failed to extract subfile '{contentSubFile.FileName}': {e}");
                    SetProgress($"Failed to extract subfile '{contentSubFile.FileName}': {e.Message}");
                    continue;
                }

                if (subFileData.Length > 0)
                {
                    SetProgress($"  + {outPath.Partial}");
                    extractedFiles.Add(contentSubFile.FileName);
                    await File.WriteAllBytesAsync(outPath.Full, subFileData, cancellationTokenSource.Token).ConfigureAwait(false);
                }
            }
        }

        private static (string Full, string Partial) CombineAssetFolder(string userFolder, string assetName)
        {
            var assetFolders = assetName.Split('/')[..^1];
            var userFolders = userFolder.Split(Path.DirectorySeparatorChar);

            var leftChop = 0;

            foreach (var i in Enumerable.Range(0, assetFolders.Length))
            {
                if (Enumerable.SequenceEqual(
                    assetFolders.Reverse().Skip(i),
                    userFolders.Reverse().Take(assetFolders.Length - i)
                ))
                {
                    leftChop = assetFolders.Reverse().Skip(i).Sum(static x => x.Length + 1);
                }
            }

            return (Path.Combine(userFolder, assetName[leftChop..]), assetName[leftChop..]);
        }

        private void CancelButton_Click(object sender, EventArgs e)
        {
            Close();
        }

        public void SetProgress(string text)
        {
            if (Disposing || IsDisposed || cancellationTokenSource.IsCancellationRequested)
            {
                return;
            }

            var str = $"[{DateTime.Now:HH:mm:ss.fff}] {text}{Environment.NewLine}";

            if (progressLog.InvokeRequired)
            {
                progressLog.Invoke(progressLog.AppendText, str);
            }
            else
            {
                progressLog.AppendText(str);
            }
        }
    }
}
