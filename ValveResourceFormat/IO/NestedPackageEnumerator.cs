using SteamDatabase.ValvePak;

namespace ValveResourceFormat.IO
{
    /// <summary>
    /// Walks a <see cref="Package"/> recursively, descending into entries that are themselves vpk archives.
    /// </summary>
    public static class NestedPackageEnumerator
    {
        /// <summary>
        /// Reports every non-vpk entry in <paramref name="package"/> to <paramref name="onEntry"/>, descending into
        /// entries of type "vpk". Nested contents are reported by their own path so they merge into the same output
        /// root as the parent package's files.
        /// </summary>
        /// <typeparam name="TState">Caller state threaded down the recursion (e.g. a file loading context).</typeparam>
        /// <param name="package">The package to walk.</param>
        /// <param name="state">State associated with <paramref name="package"/>.</param>
        /// <param name="onEntry">Invoked for each non-vpk entry with its owning package, entry and state.</param>
        /// <param name="openNested">
        /// Opens a nested vpk entry and returns the opened package together with the state for its children, or
        /// <see langword="null"/> to skip it (for example when opening failed).
        /// </param>
        public static void EnumerateEntries<TState>(
            Package package,
            TState state,
            Action<Package, PackageEntry, TState> onEntry,
            Func<Package, PackageEntry, TState, (Package Package, TState State)?> openNested)
        {
            if (package.Entries == null)
            {
                return;
            }

            foreach (var entries in package.Entries.Values)
            {
                foreach (var entry in entries)
                {
                    if (entry.TypeName == "vpk")
                    {
                        var child = openNested(package, entry, state);

                        if (child != null)
                        {
                            EnumerateEntries(child.Value.Package, child.Value.State, onEntry, openNested);
                        }
                    }
                    else
                    {
                        onEntry(package, entry, state);
                    }
                }
            }
        }
    }
}
