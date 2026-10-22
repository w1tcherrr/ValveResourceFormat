using System.Buffers;
using System.Globalization;
using System.IO;
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

        private const int MaxPooledLength = 4 * 1024 * 1024;

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
        }

        /// <summary>
        /// Finds the files in the searched folder and its subfolders that match a search.
        /// </summary>
        /// <returns>
        /// The matching files, whether a contents search was cancelled before it read every file,
        /// and how many files it could not read because they are in chunk files of a nested package.
        /// </returns>
        public (List<PackageEntry> Files, bool Cancelled, int UnsearchedFiles) Search(SearchRequest request)
        {
            var candidates = new List<PackageEntry>();

            if ((request.Folder ?? Root) is { } folder)
            {
                CollectFiles(folder, request.Extension, candidates);
            }

            if (request.Text.Length == 0)
            {
                return (candidates, false, 0);
            }

            if (request.Target == SearchTarget.Contents)
            {
                return SearchFileContents(candidates, request);
            }

            var text = request.UseRegex ? request.Text : request.Text.Replace('\\', Package.DirectorySeparatorChar);

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
                const string Message = "The search stopped early because the regular expression took too long to match a file name, so some matches may be missing.";
                Log.Warn(nameof(BetterTreeView), Message);
                _ = AppMessageDialogs.ShowMessageAsync(Message, "Search stopped early", MessageIcon.Warning);
            }

            return (candidates, false, 0);
        }

        internal const string WholeWordPrefix = @"(?<!\w)(?:";

        internal static Regex CreateRegex(string pattern, SearchRequest request, TimeSpan matchTimeout)
        {
            if (request.WholeWord)
            {
                pattern = string.Concat(WholeWordPrefix, pattern, @")(?!\w)");
            }

            var options = RegexOptions.CultureInvariant | RegexOptions.Multiline | (request.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase);

            return new Regex(pattern, options, matchTimeout);
        }

        private static void CollectFiles(VirtualPackageNode node, string? extension, List<PackageEntry> files)
        {
            foreach (var entry in node.Files)
            {
                if (extension == null || string.Equals(entry.TypeName, extension, StringComparison.OrdinalIgnoreCase))
                {
                    files.Add(entry);
                }
            }

            foreach (var folder in node.Folders.Values)
            {
                CollectFiles(folder, extension, files);
            }
        }

        private (List<PackageEntry> Files, bool Cancelled, int UnsearchedFiles) SearchFileContents(List<PackageEntry> candidates, SearchRequest request)
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
            var skippedEntries = 0;
            var unreadableEntries = 0;
            var completed = false;

            if (VrfGuiContext?.CurrentPackage is not { } package)
            {
                return (results, false, 0);
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

                        if (regex != null ? IsTextMatch(regex, entry, entryData, ref skippedEntries) : IndexOf(entryData, pattern, firstByte) >= 0)
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
                                ? SearchEntriesInArchive(archive, regex, archiveEntries, ref skippedEntries, cancellationToken)
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

                var found = string.Create(CultureInfo.InvariantCulture, $"Found {results.Count} matches");
                Log.Info(nameof(BetterTreeView), found);
                progressDialog.SetProgress(found);
                completed = true;

                return Task.CompletedTask;
            };
            progressDialog.ShowDialog();

            if (skippedEntries > 0)
            {
                _ = AppMessageDialogs.ShowMessageAsync(
                    string.Create(CultureInfo.InvariantCulture, $"{skippedEntries} files were not searched because the regular expression took too long to match their contents, the log lists them."),
                    "Some files were skipped",
                    MessageIcon.Warning);
            }

            return (results, !completed, unreadableEntries);
        }

        private static HashSet<PackageEntry> SearchEntriesInArchive(Stream archive, Regex regex, List<PackageEntry> archiveEntries, ref int skippedEntries, CancellationToken cancellationToken)
        {
            var matches = new HashSet<PackageEntry>();

            foreach (var entry in archiveEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var length = (int)entry.Length;
                var buffer = length <= MaxPooledLength ? ArrayPool<byte>.Shared.Rent(length) : new byte[length];

                try
                {
                    archive.Position = entry.Offset;
                    archive.ReadExactly(buffer, 0, length);

                    if (IsTextMatch(regex, entry, buffer.AsSpan(0, length), ref skippedEntries))
                    {
                        matches.Add(entry);
                    }
                }
                finally
                {
                    if (length <= MaxPooledLength)
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }
            }

            return matches;
        }

        private static bool IsTextMatch(Regex regex, PackageEntry entry, ReadOnlySpan<byte> data, ref int skippedEntries)
        {
            var charCount = Encoding.UTF8.GetMaxCharCount(data.Length);
            var chars = charCount <= MaxPooledLength ? ArrayPool<char>.Shared.Rent(charCount) : new char[charCount];

            try
            {
                var text = chars.AsSpan(0, Encoding.UTF8.GetChars(data, chars));

                if (text is ['\uFEFF', ..])
                {
                    text = text[1..];
                }

                return regex.IsMatch(text);
            }
            catch (RegexMatchTimeoutException)
            {
                Interlocked.Increment(ref skippedEntries);
                Log.Warn(nameof(BetterTreeView), $"Skipped \"{entry.GetFullPath()}\" because the regular expression took too long to match its contents");
                return false;
            }
            finally
            {
                if (charCount <= MaxPooledLength)
                {
                    ArrayPool<char>.Shared.Return(chars);
                }
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
                ScanEntries(archive, buffer, pattern, firstByte, archiveEntries, matches, cancellationToken);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            return matches;
        }

        private static void ScanEntries(Stream archive, byte[] buffer, byte[] pattern, SearchValues<byte>? firstByte, List<PackageEntry> archiveEntries, HashSet<PackageEntry> matches, CancellationToken cancellationToken)
        {
            const int MaxReadGap = 64 * 1024;

            var first = 0;

            while (first < archiveEntries.Count)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var start = (long)archiveEntries[first].Offset;
                var end = start + archiveEntries[first].Length;
                var last = first;

                while (last + 1 < archiveEntries.Count)
                {
                    var next = archiveEntries[last + 1];
                    var nextEnd = (long)next.Offset + next.Length;

                    if (next.Offset - end > MaxReadGap || nextEnd - start > buffer.Length)
                    {
                        break;
                    }

                    end = Math.Max(end, nextEnd);
                    last++;
                }

                if (end - start > buffer.Length)
                {
                    if (ContainsInLargeEntry(archive, start, end, buffer, pattern, firstByte, cancellationToken))
                    {
                        matches.Add(archiveEntries[first]);
                    }

                    first++;
                    continue;
                }

                archive.Position = start;
                archive.ReadExactly(buffer, 0, (int)(end - start));

                for (var i = first; i <= last; i++)
                {
                    var entry = archiveEntries[i];

                    if (IndexOf(buffer.AsSpan((int)(entry.Offset - start), (int)entry.Length), pattern, firstByte) >= 0)
                    {
                        matches.Add(entry);
                    }
                }

                first = last + 1;
            }
        }

        private static bool ContainsInLargeEntry(Stream archive, long start, long end, byte[] buffer, byte[] pattern, SearchValues<byte>? firstByte, CancellationToken cancellationToken)
        {
            for (var position = start; position + pattern.Length <= end;)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var length = (int)Math.Min(buffer.Length, end - position);
                archive.Position = position;
                archive.ReadExactly(buffer, 0, length);

                if (IndexOf(buffer.AsSpan(0, length), pattern, firstByte) >= 0)
                {
                    return true;
                }

                position += length - pattern.Length + 1;
            }

            return false;
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
