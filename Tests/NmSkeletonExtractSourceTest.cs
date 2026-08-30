using System.IO;
using System.Threading.Tasks;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.IO;
using ValveResourceFormat.Serialization.KeyValues;

namespace Tests
{
    public class NmSkeletonExtractSourceTest
    {
        [Test]
        public async Task SourceFilenameIsTheAuthoredMesh()
        {
            var file = Path.Combine(TestContext.TestDirectory!, "Files", "chicken.vnmskel_c");
            using var resource = new Resource
            {
                FileName = file,
            };
            resource.Read(file);

            using var contentFile = new NmSkeletonExtract(resource).ToContentFile();
            using var ms = new MemoryStream(contentFile.Data!);
            KVObject doc = KVDocumentExtensions.ParseKV3(ms).Root;

            // The compiled skeleton does not store it, but the compiler records it as an input dependency.
            await Assert.That(doc.GetStringProperty("m_sourceFilename")).IsEqualTo("models/chicken/dmx/chicken_mike.dmx");
        }
    }
}
