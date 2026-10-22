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
using ValveResourceFormat;
using ValveResourceFormat.IO;
using ValveResourceFormat.Utils;

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

        internal const int MinContentsPatternLength = 3;
        private const int MaxPooledLength = 4 * 1024 * 1024;
        private const int MaxTextSearchLength = 256 * 1024 * 1024;
        private const int MaxWholeCompiledLength = 64 * 1024;
        private const int ScanBufferSize = 4 * 1024 * 1024;

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
        }

        /// <summary>
        /// Finds the files in the searched folder and its subfolders that match a search.
        /// </summary>
        /// <param name="include">Decides which files are searched at all, before their names or contents are read.</param>
        /// <returns>
        /// The matching files, whether the user cancelled a contents search before it read every file,
        /// and which files it could not search, or <see langword="null"/> when it searched them all.
        /// </returns>
        public (List<PackageEntry> Files, bool Cancelled, string? Unsearched) Search(SearchRequest request, Func<PackageEntry, bool>? include = null)
        {
            List<PackageEntry> candidates = (request.Folder ?? Root) is { } folder
                ? [.. TreeViewWithSearchResults.EnumerateFiles(folder).Where(entry =>
                    (request.Extension == null || string.Equals(entry.TypeName, request.Extension, StringComparison.OrdinalIgnoreCase)) && include?.Invoke(entry) != false)]
                : [];

            if (request.Text.Length == 0)
            {
                return (candidates, false, null);
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

            return (candidates, false, null);
        }

        internal static Regex CreateRegex(string pattern, SearchRequest request, TimeSpan matchTimeout)
        {
            if (request.WholeWord)
            {
                pattern = string.Concat(@"(?<!\w)(?:", pattern, @")(?!\w)");
            }

            var options = RegexOptions.CultureInvariant | RegexOptions.Multiline | (request.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase);

            return new Regex(pattern, options, matchTimeout);
        }

        private (List<PackageEntry> Files, bool Cancelled, string? Unsearched) SearchFileContents(List<PackageEntry> candidates, SearchRequest request)
        {
            byte[] pattern = [];
            SearchValues<byte>? firstByte = null;
            Regex? regex = null;

            if (request.HexBytes)
            {
                pattern = Convert.FromHexString(request.Text.Replace(" ", "", StringComparison.Ordinal));
            }
            else
            {
                if (request.MatchCase && !request.UseRegex)
                {
                    pattern = Encoding.UTF8.GetBytes(request.Text);
                }
                else if (!request.UseRegex && Ascii.IsValid(request.Text))
                {
                    pattern = Encoding.ASCII.GetBytes(request.Text);
                    firstByte = SearchValues.Create([(byte)char.ToLowerInvariant(request.Text[0]), (byte)char.ToUpperInvariant(request.Text[0])]);
                }

                if (request.UseRegex || request.WholeWord || pattern.Length == 0)
                {
                    regex = CreateRegex(request.UseRegex ? request.Text : Regex.Escape(request.Text), request, TimeSpan.FromSeconds(3));
                }
            }

            if (regex == null && pattern.Length < MinContentsPatternLength)
            {
                throw new ArgumentException("Search input is too short.", nameof(request));
            }

            var results = new List<PackageEntry>();
            var skippedEntries = 0;
            var unreadableEntries = 0;
            var missingEntries = 0;
            var tooLargeEntries = 0;
            var cancelled = false;

            if (VrfGuiContext?.CurrentPackage is not { } package)
            {
                return (results, false, null);
            }

            bool IsSearchedAsCompiled(PackageEntry entry) => !request.HexBytes && entry.TypeName.EndsWith(GameFileLoader.CompiledFileSuffix, StringComparison.Ordinal);

            bool Matches(ReadOnlySpan<byte> data) => IndexOf(data, pattern, firstByte) >= 0 && (regex == null || IsTextMatch(regex, data));

            bool IsEntryMatch(PackageEntry entry)
            {
                var length = (int)entry.TotalLength;
                var entryData = ArrayPool<byte>.Shared.Rent(length);

                try
                {
                    package.ReadEntry(entry, entryData, validateCrc: false);
                    return Matches(entryData.AsSpan(0, length));
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(entryData);
                }
            }

            bool IsCompiledMatch(PackageEntry entry, ArraySegment<byte>? stored)
            {
                try
                {
                    using var stream = stored is { } data ? new MemoryStream(data.Array!, data.Offset, data.Count, writable: false) : package.OpenEntryStream(entry);
                    return Resource.AnySearchableBytes(stream, Matches);
                }
                catch (Exception e) when (e is UnexpectedMagicException or InvalidDataException or EndOfStreamException)
                {
                    return stored is { } data ? Matches(data) : IsEntryMatch(entry);
                }
            }

            bool IsFileMatch(PackageEntry entry, ArraySegment<byte>? stored)
            {
                try
                {
                    return IsSearchedAsCompiled(entry) ? IsCompiledMatch(entry, stored)
                        : stored is { } data ? Matches(data)
                        : IsEntryMatch(entry);
                }
                catch (RegexMatchTimeoutException)
                {
                    Interlocked.Increment(ref skippedEntries);
                    Log.Warn(nameof(BetterTreeView), $"Skipped \"{entry.GetFullPath()}\" because the regular expression took too long to match its contents");
                    return false;
                }
            }

            using var progressDialog = new GenericProgressForm
            {
                Text = "Searching file contents…",
                WaitForWorkToStop = true,
                CancelOnEscape = true,
            };
            progressDialog.OnProcess = cancellationToken =>
            {
                try
                {
                    return SearchArchives(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    throw;
                }
            };

            Task SearchArchives(CancellationToken cancellationToken)
            {
                Log.Info(nameof(BetterTreeView), "Pattern search");

                var sortedEntriesPerArchive = new Dictionary<int, List<PackageEntry>>();

                foreach (var entry in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (package is NestedPackage && entry.ArchiveIndex != Package.DirectoryArchiveIndex)
                    {
                        unreadableEntries++;
                        continue;
                    }

                    if (regex != null && entry.TotalLength > MaxTextSearchLength && !IsSearchedAsCompiled(entry))
                    {
                        tooLargeEntries++;
                        continue;
                    }

                    if (entry.SmallData.Length > 0 || entry.ArchiveIndex == Package.DirectoryArchiveIndex)
                    {
                        if (entry.TotalLength == 0)
                        {
                            continue;
                        }

                        if (IsFileMatch(entry, null))
                        {
                            results.Add(entry);
                        }

                        continue;
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

                var archivesScanned = 0;
                progressDialog.SetBarMax(sortedEntriesPerArchive.Count);

                Parallel.ForEach(
                    sortedEntriesPerArchive,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = regex != null ? Math.Min(Environment.ProcessorCount, 3) : Environment.ProcessorCount,
                        CancellationToken = cancellationToken,
                    },
                    pair =>
                    {
                        var (archiveIndex, archiveEntries) = pair;
                        List<PackageEntry> archiveMatches = [];

                        try
                        {
                            using var archive = package.OpenArchiveStream((ushort)archiveIndex);
                            var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(ScanBufferSize, pattern.Length));

                            try
                            {
                                ScanEntries(
                                    archive,
                                    buffer,
                                    archiveEntries,
                                    archiveMatches,
                                    entry => IsSearchedAsCompiled(entry) && entry.Length > MaxWholeCompiledLength,
                                    entry => IsSearchedAsCompiled(entry) ? IsFileMatch(entry, null)
                                        : (pattern.Length == 0 || ContainsInLargeEntry(archive, entry.Offset, entry.Offset + entry.Length, buffer, pattern, firstByte, cancellationToken))
                                            && (regex == null || IsFileMatch(entry, null)),
                                    (entry, data) => IsFileMatch(entry, data),
                                    cancellationToken);
                            }
                            finally
                            {
                                ArrayPool<byte>.Shared.Return(buffer);
                            }
                        }
                        catch (IOException e)
                        {
                            Interlocked.Add(ref missingEntries, archiveEntries.Count);
                            Log.Warn(nameof(BetterTreeView), string.Create(CultureInfo.InvariantCulture, $"Not searching {archiveEntries.Count} files in archive {archiveIndex}: {e.Message}"));
                        }

                        lock (results)
                        {
                            results.AddRange(archiveMatches);
                            archivesScanned++;
                            progressDialog.SetBarValue(archivesScanned);
                            progressDialog.SetProgress(string.Create(CultureInfo.InvariantCulture, $"Searched {archivesScanned:N0} of {sortedEntriesPerArchive.Count:N0} archives, found {results.Count:N0} matches so far"));
                        }
                    }
                );

                var found = string.Create(CultureInfo.InvariantCulture, $"Found {results.Count} matches");
                Log.Info(nameof(BetterTreeView), found);
                progressDialog.SetProgress(found);

                return Task.CompletedTask;
            }

            progressDialog.ShowDialog();

            if (skippedEntries > 0)
            {
                _ = AppMessageDialogs.ShowMessageAsync(
                    string.Create(CultureInfo.InvariantCulture, $"{skippedEntries} files were not searched because the regular expression took too long to match their contents, the log lists them."),
                    "Some files were skipped",
                    MessageIcon.Warning);
            }

            List<string> unsearched = [];

            if (unreadableEntries > 0)
            {
                unsearched.Add(string.Create(CultureInfo.InvariantCulture, $"{unreadableEntries:N0} {(unreadableEntries == 1 ? "file" : "files")} in chunk files of a nested package"));
            }

            if (missingEntries > 0)
            {
                unsearched.Add(string.Create(CultureInfo.InvariantCulture, $"{missingEntries:N0} {(missingEntries == 1 ? "file" : "files")} in missing or unreadable chunk files"));
            }

            if (tooLargeEntries > 0)
            {
                unsearched.Add(string.Create(CultureInfo.InvariantCulture, $"{tooLargeEntries:N0} {(tooLargeEntries == 1 ? "file" : "files")} over {MaxTextSearchLength / 1024 / 1024} MiB"));
            }

            return (results, cancelled, unsearched.Count > 0 ? string.Join(" and ", unsearched) + " not searched" : null);
        }

        private static bool IsTextMatch(Regex regex, ReadOnlySpan<byte> data)
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

        /// <summary>
        /// Reads entries sorted by offset in runs of nearby entries that fit in the buffer,
        /// and leaves entries larger than the buffer or read on their own to <paramref name="matchesAlone"/>.
        /// </summary>
        private static void ScanEntries(Stream archive, byte[] buffer, List<PackageEntry> archiveEntries, List<PackageEntry> matches,
            Func<PackageEntry, bool> isReadAlone, Func<PackageEntry, bool> matchesAlone, Func<PackageEntry, ArraySegment<byte>, bool> matchesInBuffer,
            CancellationToken cancellationToken)
        {
            const int MaxReadGap = 64 * 1024;

            bool IsAlone(PackageEntry entry) => entry.Length > buffer.Length || isReadAlone(entry);

            var first = 0;

            while (first < archiveEntries.Count)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (IsAlone(archiveEntries[first]))
                {
                    if (matchesAlone(archiveEntries[first]))
                    {
                        matches.Add(archiveEntries[first]);
                    }

                    first++;
                    continue;
                }

                var start = (long)archiveEntries[first].Offset;
                var end = start + archiveEntries[first].Length;
                var last = first;

                while (last + 1 < archiveEntries.Count)
                {
                    var next = archiveEntries[last + 1];
                    var nextEnd = (long)next.Offset + next.Length;

                    if (next.Offset - end > MaxReadGap || nextEnd - start > buffer.Length || IsAlone(next))
                    {
                        break;
                    }

                    end = Math.Max(end, nextEnd);
                    last++;
                }

                archive.Position = start;
                archive.ReadExactly(buffer, 0, (int)(end - start));

                for (var i = first; i <= last; i++)
                {
                    var entry = archiveEntries[i];

                    if (matchesInBuffer(entry, new ArraySegment<byte>(buffer, (int)(entry.Offset - start), (int)entry.Length)))
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
