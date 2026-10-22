using System.Buffers;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using GUI.Controls;
using GUI.Forms;
using GUI.Utils;
using ValvePak;
using ValveResourceFormat.IO;

namespace GUI.Types.PackageViewer
{
    /// <summary>
    /// Represents a TreeView with the ability to have its contents searched.
    /// </summary>
    partial class BetterTreeView : TreeViewDoubleBuffered
    {
        public VirtualPackageNode? Root;
        public Dictionary<string, int> ExtensionIconList = [];
        public int FolderImage;

        public VrfGuiContext? VrfGuiContext { get; set; }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
        }

        /// <summary>
        /// Finds the files in the searched folder and its subfolders that match a search.
        /// </summary>
        public List<PackageEntry> Search(SearchRequest request)
        {
            var candidates = new List<PackageEntry>();

            if ((request.Folder ?? Root) is { } folder)
            {
                CollectFiles(folder, request.Extension, candidates);
            }

            if (request.Text.Length == 0)
            {
                return candidates;
            }

            if (request.Target == SearchTarget.Contents)
            {
                return SearchFileContents(candidates, request);
            }

            var text = request.UseRegex ? request.Text : request.Text.Replace('\\', Package.DirectorySeparatorChar);

            // A file name can not contain a slash, so such text is matched against the full path
            var matchPath = request.Target == SearchTarget.Path || (!request.UseRegex && text.Contains(Package.DirectorySeparatorChar, StringComparison.Ordinal));

            Func<string, bool> isMatch;
            var timedOut = false;

            if (request.UseRegex || request.WholeWord)
            {
                var regex = CreateRegex(request.UseRegex ? text : Regex.Escape(text), request, TimeSpan.FromMilliseconds(100));

                isMatch = value =>
                {
                    try
                    {
                        return !timedOut && regex.IsMatch(value);
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        timedOut = true;
                        return false;
                    }
                };
            }
            else
            {
                var comparison = request.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                isMatch = value => value.Contains(text, comparison);
            }

            candidates.RemoveAll(entry => !isMatch(matchPath ? entry.GetFullPath() : entry.GetFileName()));

            if (timedOut)
            {
                Log.Warn(nameof(BetterTreeView), "Stopped the search because the regular expression took too long to match a file name");
            }

            return candidates;
        }

        private static Regex CreateRegex(string pattern, SearchRequest request, TimeSpan matchTimeout)
        {
            if (request.WholeWord)
            {
                pattern = string.Concat(@"(?<!\w)(?:", pattern, @")(?!\w)");
            }

            var options = request.MatchCase ? RegexOptions.CultureInvariant : RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;

            return new Regex(pattern, options, matchTimeout);
        }

        private static void CollectFiles(VirtualPackageNode node, string? extension, List<PackageEntry> files)
        {
            foreach (var entry in node.Files)
            {
                if (extension == null || entry.TypeName == extension)
                {
                    files.Add(entry);
                }
            }

            foreach (var folder in node.Folders.Values)
            {
                CollectFiles(folder, extension, files);
            }
        }

        private List<PackageEntry> SearchFileContents(List<PackageEntry> candidates, SearchRequest request)
        {
            byte[] pattern = [];
            SearchValues<byte>? firstByte = null;
            Regex? regex = null;

            if (request.HexBytes)
            {
                pattern = Convert.FromHexString(request.Text.Replace(" ", "", StringComparison.Ordinal));
            }
            else if (request.MatchCase && !request.WholeWord && !request.UseRegex)
            {
                pattern = Encoding.UTF8.GetBytes(request.Text);
            }
            else if (!request.WholeWord && !request.UseRegex && Ascii.IsValid(request.Text))
            {
                pattern = Encoding.ASCII.GetBytes(request.Text);
                firstByte = SearchValues.Create([(byte)char.ToLowerInvariant(request.Text[0]), (byte)char.ToUpperInvariant(request.Text[0])]);
            }
            else
            {
                regex = CreateRegex(request.UseRegex ? request.Text : Regex.Escape(request.Text), request, TimeSpan.FromSeconds(3));
            }

            if (regex == null && pattern.Length < 3)
            {
                throw new ArgumentException("Search input is too short.", nameof(request));
            }

            var results = new List<PackageEntry>();

            if (VrfGuiContext?.CurrentPackage is not { } package)
            {
                return results;
            }

            using var progressDialog = new GenericProgressForm
            {
                Text = "Searching file contents…",
                WaitForWorkToStop = true,
            };
            progressDialog.OnProcess = cancellationToken =>
            {
                Log.Info(nameof(BetterTreeView), "Pattern search");

                var maxArchiveIndex = -1;
                var sortedEntriesPerArchive = new Dictionary<int, List<PackageEntry>>();
                var unreadableEntries = 0;

                foreach (var entry in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Chunk files of a package inside of another package can not be read
                    if (package is NestedPackage && entry.ArchiveIndex != Package.DirectoryArchiveIndex)
                    {
                        unreadableEntries++;
                        continue;
                    }

                    // Preload data is not next to the rest of the file, and files added while editing are not on disk yet
                    if (entry.SmallData.Length > 0 || entry.ArchiveIndex == Package.DirectoryArchiveIndex)
                    {
                        if (entry.TotalLength == 0)
                        {
                            continue;
                        }

                        package.ReadEntry(entry, out var entryData, validateCrc: false);

                        if (regex != null ? IsTextMatch(regex, entry, entryData) : IndexOf(entryData, pattern, firstByte) >= 0)
                        {
                            results.Add(entry);
                        }

                        continue;
                    }

                    if (entry.ArchiveIndex > maxArchiveIndex)
                    {
                        maxArchiveIndex = entry.ArchiveIndex;
                    }

                    if (entry.Length == 0)
                    {
                        continue;
                    }

                    if (!sortedEntriesPerArchive.TryGetValue(entry.ArchiveIndex, out var archiveEntries))
                    {
                        archiveEntries = [];
                        sortedEntriesPerArchive.Add(entry.ArchiveIndex, archiveEntries);
                    }

                    archiveEntries.Add(entry);
                }

                if (unreadableEntries > 0)
                {
                    Log.Warn(nameof(BetterTreeView), string.Create(CultureInfo.InvariantCulture, $"Not searching {unreadableEntries} files in chunk files of a package inside of another package"));
                }

                foreach (var archiveEntries in sortedEntriesPerArchive.Values)
                {
                    archiveEntries.Sort((a, b) => a.Offset.CompareTo(b.Offset));
                }

                if (maxArchiveIndex > -1)
                {
                    var matches = new HashSet<PackageEntry>();
                    var archivesScanned = 0;

                    Parallel.For(
                        0,
                        maxArchiveIndex + 1,
                        new ParallelOptions
                        {
                            MaxDegreeOfParallelism = 3,
                            CancellationToken = cancellationToken,
                        },
                        archiveIndex =>
                        {
                            // Archives that only hold empty entries are never read
                            if (!sortedEntriesPerArchive.TryGetValue(archiveIndex, out var archiveEntries))
                            {
                                Interlocked.Increment(ref archivesScanned);
                                return;
                            }

                            using var archive = package.OpenArchiveStream((ushort)archiveIndex);
                            var archiveMatches = regex != null
                                ? SearchEntriesInArchive(archive, regex, archiveEntries, cancellationToken)
                                : SearchForContentsInArchive(archive, pattern, firstByte, archiveEntries, cancellationToken);

                            if (archiveMatches.Count > 0)
                            {
                                lock (matches)
                                {
                                    foreach (var match in archiveMatches)
                                    {
                                        if (matches.Add(match))
                                        {
                                            results.Add(match);
                                        }
                                    }
                                }
                            }

                            var scanned = Interlocked.Increment(ref archivesScanned);
                            progressDialog.SetProgress(string.Create(CultureInfo.InvariantCulture, $"Searched {scanned} vpks out of {maxArchiveIndex + 1}, found {results.Count} matches so far"));
                        }
                    );
                }

                Log.Info(nameof(BetterTreeView), $"Found {results.Count} matches");

                progressDialog.SetProgress($"Found {results.Count} matches");

                return Task.CompletedTask;
            };
            progressDialog.ShowDialog();

            return results;
        }

        private static HashSet<PackageEntry> SearchEntriesInArchive(Stream archive, Regex regex, List<PackageEntry> archiveEntries, CancellationToken cancellationToken)
        {
            var matches = new HashSet<PackageEntry>();

            foreach (var entry in archiveEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var length = (int)entry.Length;
                var buffer = ArrayPool<byte>.Shared.Rent(length);

                try
                {
                    archive.Position = entry.Offset;
                    archive.ReadExactly(buffer, 0, length);

                    if (IsTextMatch(regex, entry, buffer.AsSpan(0, length)))
                    {
                        matches.Add(entry);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            return matches;
        }

        private static bool IsTextMatch(Regex regex, PackageEntry entry, ReadOnlySpan<byte> data)
        {
            var chars = ArrayPool<char>.Shared.Rent(Encoding.UTF8.GetMaxCharCount(data.Length));

            try
            {
                var length = Encoding.UTF8.GetChars(data, chars);

                return regex.IsMatch(chars.AsSpan(0, length));
            }
            catch (RegexMatchTimeoutException)
            {
                Log.Warn(nameof(BetterTreeView), $"Skipped \"{entry.GetFullPath()}\" because the regular expression took too long to match its contents");
                return false;
            }
            finally
            {
                ArrayPool<char>.Shared.Return(chars);
            }
        }

        /// <summary>
        /// Finds a pattern in data, ignoring ASCII case when <paramref name="firstByte"/> holds both cases of its first byte.
        /// </summary>
        private static int IndexOf(ReadOnlySpan<byte> data, byte[] pattern, SearchValues<byte>? firstByte)
        {
            if (firstByte == null)
            {
                return data.IndexOf(pattern);
            }

            var offset = 0;

            while (true)
            {
                var index = data[offset..].IndexOfAny(firstByte);

                if (index < 0)
                {
                    return -1;
                }

                index += offset;

                if (data.Length - index < pattern.Length)
                {
                    return -1;
                }

                if (Ascii.EqualsIgnoreCase(data.Slice(index, pattern.Length), pattern))
                {
                    return index;
                }

                offset = index + 1;
            }
        }

        private static HashSet<PackageEntry> SearchForContentsInArchive(Stream archive, byte[] pattern, SearchValues<byte>? firstByte, List<PackageEntry> archiveEntries, CancellationToken cancellationToken)
        {
            const int BufferSize = 4 * 1024 * 1024;

            var matches = new HashSet<PackageEntry>();
            var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(BufferSize, pattern.Length));

            try
            {
                var bufferStart = 0L;
                var bufferLength = 0;
                var position = 0L;
                var entryIndex = 0;

                while (true)
                {
                    var bufferOffset = position - bufferStart;

                    if (bufferOffset < 0 || bufferOffset + pattern.Length > bufferLength)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        archive.Position = position;
                        bufferStart = position;
                        bufferOffset = 0;
                        bufferLength = archive.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);

                        if (bufferLength < pattern.Length)
                        {
                            break;
                        }
                    }

                    var match = IndexOf(buffer.AsSpan((int)bufferOffset, bufferLength - (int)bufferOffset), pattern, firstByte);

                    if (match < 0)
                    {
                        position = bufferStart + bufferLength - pattern.Length + 1;
                        continue;
                    }

                    var start = position + match;

                    while (entryIndex + 1 < archiveEntries.Count && archiveEntries[entryIndex + 1].Offset <= start)
                    {
                        entryIndex++;
                    }

                    var entry = archiveEntries[entryIndex];
                    var entryEnd = (long)entry.Offset + entry.Length;

                    if (entry.Offset > start || start + pattern.Length > entryEnd)
                    {
                        position = start + 1;
                        continue;
                    }

                    matches.Add(entry);
                    position = entryEnd;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            return matches;
        }

        public void GenerateIconList(IEnumerable<string> extensions)
        {
            var defaultImage = AppIcons.Icons["File"];

            foreach (var originalExtension in extensions)
            {
                var image = AppIcons.GetImageIndexForExtension(originalExtension.ToLowerInvariant());

                if (image == defaultImage)
                {
                    continue;
                }

                ExtensionIconList.TryAdd(originalExtension, image);
            }

            FolderImage = AppIcons.Icons["Folder"];
        }

        public static VirtualPackageNode AddFolderNode(VirtualPackageNode currentNode, string directory, uint size)
        {
            var directorySpan = directory.AsSpan();

            foreach (var subPathRange in directorySpan.Split([Package.DirectorySeparatorChar]))
            {
                var subPath = directorySpan[subPathRange];

                if (!currentNode.Folders.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(subPath, out var subNode))
                {
                    var subPathString = subPath.ToString();
                    var toAdd = new VirtualPackageNode(subPathString, size, currentNode);
                    currentNode.Folders.Add(subPathString, toAdd);
                    currentNode = toAdd;
                }
                else
                {
                    currentNode = subNode;
                    currentNode.TotalSize += size;
                }
            }

            return currentNode;
        }

        public static VirtualPackageNode AddFileNode(VirtualPackageNode currentNode, PackageEntry file)
        {
            var directory = file.GetDirectoryPath();

            if (directory.Length > 0)
            {
                currentNode = AddFolderNode(currentNode, directory, file.TotalLength);
            }

            currentNode.Files.Add(file);

            return currentNode;
        }
    }
}
