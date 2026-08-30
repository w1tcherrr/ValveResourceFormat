using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ValveResourceFormat;
using ValveResourceFormat.IO;
using ValveKeyValue;
using ValveResourceFormat.Serialization.KeyValues;

namespace Tests
{
    public class NmClipExtractTest
    {
        private static KVObject ExtractClipDoc(string fileName)
        {
            var file = Path.Combine(TestContext.TestDirectory!, "Files", fileName);
            using var resource = new Resource
            {
                FileName = file,
            };
            resource.Read(file);

            using var contentFile = new NmClipExtract(resource, new NullFileLoader()).ToContentFile();
            using var ms = new MemoryStream(contentFile.Data!);

            return KVDocumentExtensions.ParseKV3(ms).Root;
        }

        [Test]
        public async Task SourceFilenameIsTheAuthoredAnimation()
        {
            var doc = ExtractClipDoc("idle_ak.vnmclip_c");

            // The compiled clip does not store it, but the compiler records it as an input dependency.
            await Assert.That(doc.GetStringProperty("m_sourceFilename"))
                .IsEqualTo("phase2/animation/anims/viewmodel/rifle/rifle_ak/idles/dmx/idle_ak.dmx");
        }

        [Test]
        public async Task AdditiveBaseFrameIsTheFrameThatDecodesToIdentity()
        {
            var doc = ExtractClipDoc("shoot_cz75.vnmclip_c");

            using (Assert.Multiple())
            {
                await Assert.That(doc.GetStringProperty("m_additiveType")).IsEqualTo("RelativeToFrame");
                await Assert.That(doc.GetStringProperty("m_additiveBaseFrame")).IsEqualTo("UserSpecifiedFrame");
                await Assert.That(doc.GetInt32Property("m_nAdditiveBaseFrameIdx")).IsEqualTo(12);
            }
        }

        [Test]
        public async Task DocEventsCarryTheEventIdButNotTheCompiledSyncId()
        {
            var doc = ExtractClipDoc("shoot1_nova.vnmclip_c");

            var events = doc.GetArray("m_eventTracks")!
                .SelectMany(track => track.GetArray("m_events")!)
                .ToArray();

            var idEvent = events.Single(ev => ev.GetStringProperty("_class") == "CNmClipDocEvent_ID");

            using (Assert.Multiple())
            {
                await Assert.That(idEvent.GetStringProperty("m_ID")).IsEqualTo("WPN_BLOCK_INSPECT");
                await Assert.That(events.Any(ev => ev.ContainsKey("m_syncID"))).IsFalse();
            }
        }
    }
}
