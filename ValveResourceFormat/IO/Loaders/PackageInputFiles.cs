using System.IO;
using System.IO.Enumeration;

namespace ValveResourceFormat.IO
{
    /// <summary>
    /// A file on disk and the path it is packed as.
    /// </summary>
    /// <param name="PathOnDisk">Full path of the file on disk.</param>
    /// <param name="EntryPath">Path of the file in the package.</param>
    /// <param name="Length">Size of the file in bytes.</param>
    /// <param name="WasLowercased">Whether <paramref name="EntryPath"/> was lowercased from the path on disk.</param>
    public readonly record struct PackageInputFile(string PathOnDisk, string EntryPath, long Length, bool WasLowercased);

    /// <summary>
    /// Collects files on disk to pack into a VPK package. Paths in packages use forward slashes and are lowercase.
    /// </summary>
    public static class PackageInputFiles
    {
        /// <summary>
        /// Gets the path a file is packed as from its path relative to the folder being packed.
        /// </summary>
        public static string GetEntryPath(string relativePath)
        {
            ArgumentNullException.ThrowIfNull(relativePath);

            return relativePath.Replace('\\', '/').ToLowerInvariant();
        }

        /// <summary>
        /// Collects the files in a folder and its subfolders, sorted by <see cref="PackageInputFile.EntryPath"/>.
        /// Files and folders starting with a dot, such as <c>.git</c>, are skipped.
        /// </summary>
        /// <param name="folder">The folder to pack, entry paths are relative to it.</param>
        /// <param name="outputPackagePath">The package being written, which is skipped along with its chunk files when it is inside of the folder.</param>
        /// <param name="filter">Returns whether to pack a file, given its entry path.</param>
        /// <exception cref="InvalidDataException">Two files would be packed as the same path, because paths in packages are case-insensitive.</exception>
        public static List<PackageInputFile> CollectFromFolder(string folder, string? outputPackagePath = null, Func<string, bool>? filter = null)
        {
            ArgumentNullException.ThrowIfNull(folder);

            folder = Path.GetFullPath(folder);

            if (outputPackagePath != null)
            {
                outputPackagePath = Path.GetFullPath(outputPackagePath);
            }

            var enumeration = new FileSystemEnumerable<(string Path, long Length)>(
                folder,
                (ref entry) => (entry.ToFullPath(), entry.Length),
                new EnumerationOptions { RecurseSubdirectories = true })
            {
                ShouldIncludePredicate = (ref entry) => !entry.IsDirectory && !entry.FileName.StartsWith('.'),
                ShouldRecursePredicate = (ref entry) => !entry.FileName.StartsWith('.'),
            };

            var files = new SortedDictionary<string, PackageInputFile>(StringComparer.Ordinal);

            foreach (var (path, length) in enumeration)
            {
                if (outputPackagePath != null && IsOutputPackage(path, outputPackagePath))
                {
                    continue;
                }

                var slashedPath = Path.GetRelativePath(folder, path).Replace('\\', '/');
                var entryPath = GetEntryPath(slashedPath);

                if (filter != null && !filter(entryPath))
                {
                    continue;
                }

                if (files.TryGetValue(entryPath, out var existing))
                {
                    throw new InvalidDataException($"\"{existing.PathOnDisk}\" and \"{path}\" would both be packed as \"{entryPath}\", paths in VPKs are case-insensitive.");
                }

                files.Add(entryPath, new PackageInputFile(path, entryPath, length, entryPath != slashedPath));
            }

            return [.. files.Values];
        }

        private static bool IsOutputPackage(string path, string outputPackagePath)
        {
            if (path.Equals(outputPackagePath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return PackageFileNames.IsArchiveChunk(path)
                && PackageFileNames.GetDirectoryFile(path).Equals(outputPackagePath, StringComparison.OrdinalIgnoreCase);
        }
    }
}
