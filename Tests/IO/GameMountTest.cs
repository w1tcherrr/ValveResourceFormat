using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TUnit.Assertions.Enums;
using ValvePak;
using ValveResourceFormat.IO;

namespace Tests.IO
{
    public class GameMountTest
    {
        [Test]
        public async Task ListsMountedFilesInSearchOrder()
        {
            var gameRoot = Directory.CreateTempSubdirectory("vrf_game_mount_");

            try
            {
                var mod = gameRoot.CreateSubdirectory("mod").FullName;
                var core = gameRoot.CreateSubdirectory("core").FullName;
                var gameInfo = Path.Join(mod, "gameinfo.gi");

                await File.WriteAllTextAsync(gameInfo, "\"GameInfo\"\n{\n\t\"FileSystem\"\n\t{\n\t\t\"SearchPaths\"\n\t\t{\n\t\t\t\"Game\" \"mod\"\n\t\t\t\"Game\" \"core\"\n\t\t}\n\t}\n}\n");
                await File.WriteAllTextAsync(Path.Join(core, "gameinfo.gi"), "\"GameInfo\"\n{\n}\n");
                WritePackage(Path.Join(mod, "pak01_dir.vpk"), "scripts/shared.txt", "scripts/mod.txt");
                WritePackage(Path.Join(core, "pak01_dir.vpk"), "scripts/shared.txt", "scripts/core.txt");
                Directory.CreateDirectory(Path.Join(mod, "scripts"));
                Directory.CreateDirectory(Path.Join(mod, "bin"));
                await File.WriteAllTextAsync(Path.Join(mod, "scripts", "shared.txt"), "loose");
                await File.WriteAllTextAsync(Path.Join(mod, "scripts", "loose.txt"), "loose");
                await File.WriteAllTextAsync(Path.Join(mod, "bin", "server.dll"), "binary");

                using var loader = new GameFileLoader(null, null);
                loader.FindAndLoadSearchPaths(gameInfo);

                var files = loader.EnumerateMountedFiles()
                    .Select(file => (
                        Path: file.Entry.GetFullPath(),
                        Source: file.Package != null ? Path.GetFileName(Path.GetDirectoryName(file.Package.FilePath)) : Path.GetRelativePath(gameRoot.FullName, file.PathOnDisk!).Replace('\\', '/'),
                        OverriddenBy: file.OverriddenBy?.GetFullPath()))
                    .Where(file => file.Path != "gameinfo.gi")
                    .ToList();

                using (Assert.Multiple())
                {
                    await Assert.That(GameFileLoader.FindModGameInfos(gameRoot.FullName)).IsEquivalentTo([gameInfo]);
                    await Assert.That(loader.EnumerateSearchOrder().Select(source => source.Package != null ? "pak" : Path.GetFileName(source.Folder!)))
                        .IsEquivalentTo(["pak", "pak", "mod", "core"], CollectionOrdering.Matching);
                    await Assert.That(files).IsEquivalentTo(new (string, string?, string?)[]
                    {
                        ("scripts/mod.txt", "mod", null),
                        ("scripts/shared.txt", "mod", null),
                        ("scripts/core.txt", "core", null),
                        ("scripts/shared.txt", "core", "scripts/shared.txt"),
                        ("scripts/loose.txt", "mod/scripts/loose.txt", null),
                        ("scripts/shared.txt", "mod/scripts/shared.txt", "scripts/shared.txt"),
                    });
                }
            }
            finally
            {
                gameRoot.Delete(recursive: true);
            }
        }

        [Test]
        public async Task SkipsSearchPathsThatAreNotFoldersAndPackagesThatDoNotRead()
        {
            var gameRoot = Directory.CreateTempSubdirectory("vrf_game_mount_");

            try
            {
                var mod = gameRoot.CreateSubdirectory("mymod").FullName;
                var csgo = gameRoot.CreateSubdirectory("csgo").FullName;
                var core = gameRoot.CreateSubdirectory("core").FullName;
                var gameInfo = Path.Join(mod, "GameInfo.gi");

                await File.WriteAllTextAsync(gameInfo, """
                    "GameInfo"
                    {
                        "filesystem"
                        {
                            "searchpaths"
                            {
                                "Game" "missing"
                                "Game" "|gameinfo_path|."
                                "Game" "mymod/*"
                                "Game" "mymod/notafolder.txt"
                                "Game" "./mymod/"
                                "Game" "mymod"
                                "Game" "csgo"
                                "Game" "core"
                            }
                        }
                    }
                    """);
                await File.WriteAllTextAsync(Path.Join(mod, "notafolder.txt"), "file");
                await File.WriteAllTextAsync(Path.Join(mod, "match.dem"), "demo");
                await File.WriteAllBytesAsync(Path.Join(csgo, "pak01_dir.vpk"), []);
                WritePackage(Path.Join(mod, "pak01_dir.vpk"), "scripts/mod.txt");
                WritePackage(Path.Join(core, "pak01_dir.vpk"), "scripts/core.txt");

                using var loader = new GameFileLoader(null, null);
                loader.FindAndLoadSearchPaths(gameInfo);

                using (Assert.Multiple())
                {
                    await Assert.That(GameFileLoader.FindModGameInfos(gameRoot.FullName)).IsEquivalentTo([gameInfo]);
                    await Assert.That(loader.EnumerateSearchOrder().Select(source => Path.GetRelativePath(gameRoot.FullName, source.Package?.FilePath ?? source.Folder!).Replace('\\', '/')))
                        .IsEquivalentTo(["mymod/pak01_dir.vpk", "core/pak01_dir.vpk", "mymod", "csgo", "core"], CollectionOrdering.Matching);
                    await Assert.That(loader.EnumerateMountedFiles().Select(static file => file.Entry.GetFullPath()))
                        .Contains("scripts/mod.txt").And.Contains("notafolder.txt").And.Contains("pak01_dir.vpk").And.DoesNotContain("match.dem");
                }
            }
            finally
            {
                gameRoot.Delete(recursive: true);
            }
        }

        [Test]
        public async Task MergesBranchAndLayeredGameInfos()
        {
            var gameRoot = Directory.CreateTempSubdirectory("vrf_game_mount_");

            try
            {
                var mod = gameRoot.CreateSubdirectory("mymod").FullName;
                var language = gameRoot.CreateSubdirectory("mymod_lang").FullName;
                var extra = gameRoot.CreateSubdirectory("extra").FullName;
                var core = gameRoot.CreateSubdirectory("core").FullName;
                var cycle = gameRoot.CreateSubdirectory("cycle").FullName;

                await File.WriteAllTextAsync(Path.Join(mod, "gameinfo.gi"), """
                    #base "missing.gi"
                    "GameInfo"
                    {
                        "game" "Base"
                        FileSystem { SearchPaths { Game "replaced" } }
                    }
                    """);
                await File.WriteAllTextAsync(Path.Join(mod, "gameinfo_branchspecific.gi"), """
                    "GameInfo"
                    {
                        FileSystem
                        {
                            SteamAppId 730
                            SearchPaths { Game "extra/extra.vpk" Game "mymod" Game "core" }
                        }
                    }
                    """);
                await File.WriteAllTextAsync(Path.Join(language, "gameinfo.gi"), """
                    "GameInfo"
                    {
                        LayeredOnMod mymod
                        FileSystem { SearchPaths { Game "mymod_lang" Game "mymod" Game "core" } }
                    }
                    """);
                await File.WriteAllTextAsync(Path.Join(cycle, "gameinfo.gi"), """
                    "GameInfo"
                    {
                        LayeredOnMod cycle
                        FileSystem { SearchPaths { Game "cycle" } }
                    }
                    """);
                WritePackage(Path.Join(mod, "pak01_dir.vpk"), "scripts/mod.txt");
                WritePackage(Path.Join(extra, "extra.vpk"), "scripts/extra.txt");
                WritePackage(Path.Join(core, "pak01_dir.vpk"), "scripts/core.txt");

                List<string> SearchOrder(string gameInfo)
                {
                    using var loader = new GameFileLoader(null, null);
                    loader.FindAndLoadSearchPaths(gameInfo);

                    return [.. loader.EnumerateSearchOrder().Select(source => Path.GetRelativePath(gameRoot.FullName, source.Package?.FilePath ?? source.Folder!).Replace('\\', '/')), loader.SteamAppId.ToString(CultureInfo.InvariantCulture)];
                }

                using (Assert.Multiple())
                {
                    await Assert.That(SearchOrder(Path.Join(mod, "gameinfo.gi")))
                        .IsEquivalentTo(["extra/extra.vpk", "mymod/pak01_dir.vpk", "core/pak01_dir.vpk", "mymod", "core", "730"], CollectionOrdering.Matching);
                    await Assert.That(SearchOrder(Path.Join(language, "gameinfo.gi")))
                        .IsEquivalentTo(["mymod/pak01_dir.vpk", "core/pak01_dir.vpk", "mymod_lang", "mymod", "core", "730"], CollectionOrdering.Matching);
                    await Assert.That(SearchOrder(Path.Join(cycle, "gameinfo.gi")))
                        .IsEquivalentTo(["core/pak01_dir.vpk", "cycle", "core", "0"], CollectionOrdering.Matching);
                }
            }
            finally
            {
                gameRoot.Delete(recursive: true);
            }
        }

        private static void WritePackage(string path, params string[] files)
        {
            using var package = new Package();

            foreach (var file in files)
            {
                package.AddFile(file, "pak"u8.ToArray());
            }

            package.Write(path);
        }
    }
}
