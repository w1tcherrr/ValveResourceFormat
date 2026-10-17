using System.IO;
using ValvePak;
using ValveResourceFormat.IO;

namespace CLI
{
    public partial class Decompiler
    {
        private string? VpkCreatePath;
        private int VpkCreateChunkSize; // In megabytes, 0 writes everything into a single file

        private int CreateVpk()
        {
            var outputPath = Path.GetFullPath(VpkCreatePath!);
            var multiChunk = VpkCreateChunkSize > 0;

            List<PackageInputFile> files;

            try
            {
                files = PackageInputFiles.CollectFromFolder(InputFile, outputPath,
                    entryPath => !IsExcludedVpkFilePath(FixPathSlashes(entryPath)) && MatchesExtensionFilter(Path.GetExtension(entryPath).TrimStart('.')));
            }
            catch (InvalidDataException e)
            {
                Console.Error.WriteLine(e.Message);
                return 1;
            }

            var lowercasedPaths = 0;
            long totalSize = 0;

            foreach (var file in files)
            {
                if (file.WasLowercased)
                {
                    lowercasedPaths++;
                }

                if (file.Length > int.MaxValue)
                {
                    Console.Error.WriteLine($"\"{file.PathOnDisk}\" is larger than 2 GiB, which is not supported.");
                    return 1;
                }

                totalSize += file.Length;
            }

            if (files.Count == 0)
            {
                Console.Error.WriteLine(HasPathFilter || ExtFilterList != null
                    ? $"No files in \"{InputFile}\" matched the given filters."
                    : $"Unable to find any files in \"{InputFile}\" folder.");
                return 1;
            }

            if (!multiChunk && totalSize > int.MaxValue)
            {
                Console.Error.WriteLine("The files add up to more than 2 GiB, which does not fit into a single VPK. Use --vpk_create_chunk_size to split it into chunk files.");
                return 1;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

            using var package = new Package();
            package.SetFileName(outputPath);

            if (multiChunk)
            {
                package.WriteChunkSize = VpkCreateChunkSize * 1024 * 1024;
            }

            try
            {
                foreach (var file in files)
                {
                    if (!Quiet)
                    {
                        Console.WriteLine(file.EntryPath);
                    }

                    using var stream = File.OpenRead(file.PathOnDisk);
                    package.AddFile(file.EntryPath, stream, multiChunk);
                }

                package.Write(outputPath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Console.Error.WriteLine($"Failed to write \"{outputPath}\": {e.Message}");
                return 1;
            }

            if (lowercasedPaths > 0)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Error.WriteLine($"Warning: Lowercased {lowercasedPaths} file path(s), paths in VPKs are lowercase.");
                Console.ResetColor();
            }

            Console.WriteLine($"--- Packed {files.Count} files into \"{outputPath}\"");

            return 0;
        }
    }
}
