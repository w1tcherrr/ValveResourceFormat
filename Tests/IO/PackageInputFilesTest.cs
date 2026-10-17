using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ValveResourceFormat.IO;

namespace Tests.IO
{
    public class PackageInputFilesTest
    {
        [Test]
        public async Task CollectFromFolder()
        {
            var folder = Directory.CreateTempSubdirectory().FullName;

            try
            {
                Directory.CreateDirectory(Path.Join(folder, "Materials"));
                Directory.CreateDirectory(Path.Join(folder, ".git"));

                foreach (var name in (string[])["Materials/Floor.vmat", "readme.txt", ".gitignore", ".git/config", "out.vpk", "out_000.vpk"])
                {
                    await File.WriteAllBytesAsync(Path.Join(folder, name), [1, 2, 3]);
                }

                var files = PackageInputFiles.CollectFromFolder(folder, Path.Join(folder, "out_dir.vpk"));

                using (Assert.Multiple())
                {
                    await Assert.That(string.Join(',', files.Select(file => file.EntryPath))).IsEqualTo("materials/floor.vmat,out.vpk,readme.txt");
                    await Assert.That(files[0].WasLowercased).IsTrue();
                    await Assert.That(files[2].WasLowercased).IsFalse();
                    await Assert.That(files[0].Length).IsEqualTo(3);
                }
            }
            finally
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }
}
