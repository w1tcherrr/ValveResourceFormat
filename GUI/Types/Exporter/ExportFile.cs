using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using GUI.Forms;
using GUI.Types.PackageViewer;
using GUI.Utils;
using SteamDatabase.ValvePak;
using ValveResourceFormat.IO;
using Resource = ValveResourceFormat.Resource;

namespace GUI.Types.Exporter
{
    static class ExportFile
    {
        public static void ExtractFileFromPackageEntry(PackageEntry file, VrfGuiContext vrfGuiContext, bool decompile)
        {
            var currentPackage = vrfGuiContext.CurrentPackage;
            if (currentPackage == null)
            {
                Log.Error(nameof(ExportFile), "CurrentPackage is null, cannot extract file");
                return;
            }

            var stream = GameFileLoader.GetPackageEntryStream(currentPackage, file);

            ExtractFileFromStream(file.GetFullPath(), stream, vrfGuiContext, decompile);
        }

        public static void ExtractFileFromStream(string fileName, Stream stream, VrfGuiContext vrfGuiContext, bool decompile)
        {
            if (!PreExportDisclaimer(Path.GetExtension(fileName)))
            {
                return;
            }

            if (decompile && fileName.EndsWith(GameFileLoader.CompiledFileSuffix, StringComparison.Ordinal))
            {
                var exportData = new ExportData
                {
                    VrfGuiContext = new VrfGuiContext(fileName, vrfGuiContext),
                };

                var resourceTemp = new Resource
                {
                    FileName = fileName,
                };
                var resource = resourceTemp;
                string filaNameToSave;

                try
                {
                    resource.Read(stream);

                    var extension = FileExtract.GetExtension(resource);

                    if (extension == null)
                    {
                        stream.Dispose();
                        Log.Error(nameof(ExportFile), $"Export for \"{fileName}\" has no suitable extension");
                        return;
                    }

                    var filter = $"{extension} file|*.{extension}";

                    if (GltfModelExporter.CanExport(resource))
                    {
                        const string gltfFilter = "glTF|*.gltf";
                        const string glbFilter = "glTF Binary|*.glb";

                        filter = $"{filter}|{gltfFilter}|{glbFilter}";
                    }

                    var fileNameForSave = Path.GetFileNameWithoutExtension(fileName);

                    if (Path.GetExtension(fileName) == ".vmap_c")
                    {
                        // When exporting a vmap, suggest saving with a suffix like de_dust2_d,
                        // to reduce conflicts when users end up recompiling the map with the same name as it exists in the game
                        fileNameForSave += "_d";
                    }

                    using var dialog = new SaveFileDialog
                    {
                        Title = "Choose where to save the file",
                        FileName = fileNameForSave,
                        InitialDirectory = Settings.Config.SaveDirectory,
                        DefaultExt = extension,
                        Filter = filter,
                        AddToRecent = true,
                    };

                    var result = dialog.ShowDialog();

                    if (result != DialogResult.OK)
                    {
                        return;
                    }

                    filaNameToSave = dialog.FileName;
                    resourceTemp = null;
                }
                finally
                {
                    resourceTemp?.Dispose();
                }

                var directory = Path.GetDirectoryName(filaNameToSave);
                if (directory != null)
                {
                    Settings.Config.SaveDirectory = directory;
                }

                var extractDialog = new ExtractProgressForm(exportData, directory ?? string.Empty, true)
                {
                    ShownCallback = (form, cancellationToken) =>
                    {
                        form.SetProgress($"Extracting {fileName} to \"{Path.GetFileName(filaNameToSave)}\"");

                        Task.Run(async () =>
                        {
                            await form.ExtractFile(resource, fileName, filaNameToSave, true).ConfigureAwait(false);
                        }, cancellationToken).ContinueWith(t =>
                        {
                            stream.Dispose();
                            resource.Dispose();

                            form.ExportContinueWith(t);
                        }, CancellationToken.None);
                    }
                };

                try
                {
                    extractDialog.ShowDialog();
                    extractDialog = null;
                }
                finally
                {
                    extractDialog?.Dispose();
                    exportData.VrfGuiContext.Dispose();
                }
            }
            else
            {
                if (decompile && FileExtract.TryExtractNonResource(stream, fileName, out var content))
                {
                    if (content.Data == null)
                    {
                        // Content has no data to extract, only potentially subfiles
                        content.Dispose();
                        stream.Dispose();
                        Log.Info(nameof(ExportFile), $"File \"{fileName}\" has no extractable data");
                        return;
                    }

                    var extension = Path.GetExtension(content.FileName);
                    fileName = Path.ChangeExtension(fileName, extension);
                    stream.Dispose();

                    stream = new MemoryStream(content.Data);
                    content.Dispose();
                }

                using var dialog = new SaveFileDialog
                {
                    Title = "Choose where to save the file",
                    InitialDirectory = Settings.Config.SaveDirectory,
                    Filter = "All files (*.*)|*.*",
                    FileName = Path.GetFileName(fileName),
                    AddToRecent = true,
                };
                var userOK = dialog.ShowDialog();

                if (userOK == DialogResult.OK)
                {
                    var directory = Path.GetDirectoryName(dialog.FileName);
                    if (directory != null)
                    {
                        Settings.Config.SaveDirectory = directory;
                    }

                    Log.Info(nameof(ExportFile), $"Saved \"{Path.GetFileName(dialog.FileName)}\"");

                    using var streamOutput = dialog.OpenFile();
                    stream.CopyTo(streamOutput);
                }

                stream.Dispose();
            }
        }

        public static void ExtractEntirePackage(VrfGuiContext vrfGuiContext, bool decompile)
        {
            var package = vrfGuiContext.CurrentPackage;
            if (package?.Entries == null)
            {
                Log.Error(nameof(ExportFile), "CurrentPackage is null, cannot extract package");
                return;
            }

            RunExtraction(vrfGuiContext, decompile, extractDialog =>
            {
                foreach (var fileType in package.Entries.Values)
                {
                    foreach (var entry in fileType)
                    {
                        extractDialog.QueueFiles(entry);
                    }
                }
            });
        }

        public static void ExtractNestedPackage(PackageEntry vpkEntry, VrfGuiContext parentContext, bool decompile)
        {
            var childContext = OpenNestedPackage(vpkEntry, parentContext);
            if (childContext == null)
            {
                Log.Error(nameof(ExportFile), "CurrentPackage is null, cannot extract nested package");
                return;
            }

            try
            {
                ExtractEntirePackage(childContext, decompile);
            }
            finally
            {
                childContext.Dispose();
            }
        }

        /// <summary>
        /// Opens a vpk entry that is itself a vpk archive into a child <see cref="VrfGuiContext"/> chained to
        /// <paramref name="parentContext"/>. The caller owns the returned context and must dispose it. Returns
        /// <see langword="null"/> only when the parent has no package; failures to read the nested vpk throw.
        /// </summary>
        public static VrfGuiContext? OpenNestedPackage(PackageEntry vpkEntry, VrfGuiContext parentContext)
        {
            var parentPackage = parentContext.CurrentPackage;
            if (parentPackage == null)
            {
                return null;
            }

            var childContext = new VrfGuiContext(vpkEntry.GetFullPath(), parentContext);

            try
            {
                var stream = GameFileLoader.GetPackageEntryStream(parentPackage, vpkEntry);
                var package = new Package();
                package.SetFileName(childContext.FileName);
                package.Read(stream); // Package takes ownership of the stream
                childContext.CurrentPackage = package;

                return childContext;
            }
            catch
            {
                childContext.Dispose();
                throw;
            }
        }

        public static void ExtractFilesFromTreeNode(IBetterBaseItem selectedNode, VrfGuiContext vrfGuiContext, bool decompile)
        {
            if (!selectedNode.IsFolder)
            {
                var file = selectedNode.PackageEntry;
                Debug.Assert(file != null);

                // Extracting a nested VPK descends into it (mirrors extracting the root folder)
                if (decompile && file.TypeName == "vpk")
                {
                    ExtractNestedPackage(file, vrfGuiContext, decompile);
                    return;
                }

                // We are a file
                ExtractFileFromPackageEntry(file, vrfGuiContext, decompile);
            }
            else
            {
                // We are a folder
                RunExtraction(vrfGuiContext, decompile, extractDialog => extractDialog.QueueFiles(selectedNode));
            }
        }

        public static void ExtractFilesFromListViewNodes(List<ListViewItem> items, VrfGuiContext vrfGuiContext, bool decompile)
        {
            RunExtraction(vrfGuiContext, decompile, extractDialog =>
            {
                // When queuing files this way, it'll preserve the original tree
                // which is probably unwanted behaviour? It works tho /shrug
                foreach (IBetterBaseItem item in items)
                {
                    extractDialog.QueueFiles(item);
                }
            });
        }

        public static void ExtractFilesFromTreeNodes(IEnumerable<IBetterBaseItem> items, VrfGuiContext vrfGuiContext, bool decompile)
        {
            RunExtraction(vrfGuiContext, decompile, extractDialog =>
            {
                foreach (var item in items)
                {
                    extractDialog.QueueFiles(item);
                }
            });
        }

        private static void RunExtraction(VrfGuiContext vrfGuiContext, bool decompile, Action<ExtractProgressForm> queueFiles)
        {
            var exportData = new ExportData
            {
                VrfGuiContext = vrfGuiContext,
            };

            var extractDialog = new ExtractProgressForm(exportData, null, decompile);

            try
            {
                queueFiles(extractDialog);
                extractDialog.ExecuteMultipleFileExtract();
                extractDialog = null;
            }
            finally
            {
                extractDialog?.Dispose();
            }
        }

        public static bool PreExportDisclaimer(string fileExtension)
        {
            var messageString = "";

            switch (fileExtension)
            {
                case ".vmap_c":

                    messageString =
                    """
                    Decompiling Source2 maps is a difficult process, as such the output will be messy and imperfect, and will not resemble how
                    real .vmap files are made!

                    - Models will be merged by material across the map.
                    - Parts of the skybox mesh might be missing.
                    - The collision of the map will be merged into one mesh using special materials.
                    - The map will lack lightmap resolution volumes.
                    - Hammer meshes will be triangulated.

                    It is NOT ADVISED to work on decompiled maps as your first map if you are new to mapping!
                    """;
                    break;

                default:
                    break;
            }

            if (!string.IsNullOrEmpty(messageString))
            {
                var result = MessageBox.Show(messageString, "Decompile warning", MessageBoxButtons.OKCancel);

                if (result == DialogResult.Cancel)
                {
                    return false;
                }
            }

            return true;
        }

    }
}
