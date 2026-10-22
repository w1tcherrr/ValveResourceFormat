using System.Linq;
using System.Threading.Tasks;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;

namespace Tests.Resources
{
    public class SoundEventScriptTest
    {
        private static IReadOnlyList<(string Name, KVObject Data)> ReadEvents(string fileName)
        {
            using var resource = new Resource();
            resource.Read(TestFixtures.Path(fileName));

            return ((SoundEventScript)resource.DataBlock!).Events;
        }

        [Test]
        public async Task ReadsKeyValues3Events()
        {
            var events = ReadEvents("soundevents_aghanim.vsndevts_c");
            var (name, data) = events.Single(e => e.Name == "Aghanim.CheckOutThisGuy");

            using (Assert.Multiple())
            {
                await Assert.That(name).IsEqualTo("Aghanim.CheckOutThisGuy");
                await Assert.That(data.GetStringProperty("type")).IsEqualTo("dota_src1_2d");
                await Assert.That(data.GetStringProperty("vsnd_files")).IsEqualTo("sounds/misc/aghanim/agh_event_room_intro_03.vsnd");
            }
        }

        [Test]
        public async Task ReadsLegacyKeyValues3TextEvents()
        {
            var events = ReadEvents("soundevents_dota.vsndevts_c");

            using (Assert.Multiple())
            {
                await Assert.That(events.Select(e => e.Name)).IsEquivalentTo(["Dota.UpdateVOAnnouncer", "core.soundscape_2d", "core.soundscape_3d"]);
                await Assert.That(events[1].Data.GetStringProperty("type")).IsEqualTo("dota_src1_2d");
                await Assert.That(events[1].Data.GetStringProperty("vsnd_files")).IsEqualTo("sounds/diagnostics/bell.vsnd");
            }
        }

        [Test]
        public async Task FlattensLegacyOperatorStacks()
        {
            var events = ReadEvents("soundevents_vr_core.vsndevts_c");

            using (Assert.Multiple())
            {
                await Assert.That(events.Select(e => e.Name)).IsEquivalentTo(["core.soundscape_2d", "core.soundscape_3d"]);
                await Assert.That(events[1].Data.GetStringProperty("type")).IsEqualTo("vr_core.src1_code_vr");
                await Assert.That(events[1].Data.GetStringProperty("vsnd_files")).IsEqualTo("sounds/diagnostics/bell.vsnd");
                await Assert.That(events[0].Data.ContainsKey("voice_limiter_on")).IsTrue();
            }
        }

        [Test]
        [Arguments("soundstacks_dota_addons.vsndstck_c", "dota_addon_src1_2d", "mixgroup", "string")]
        [Arguments("soundstacks_csgo_core.vsndstck_c", "csgo_movie", "vsnd_files", "vsnd")]
        public async Task ReadsStackPublicFields(string fileName, string stack, string field, string dataType)
        {
            using var resource = new Resource();
            resource.Read(TestFixtures.Path(fileName));

            var index = new SoundEventIndex();
            index.AddStacks(resource);

            var fields = index.GetStackFields(stack);

            using (Assert.Multiple())
            {
                await Assert.That(index.ContainsStack(stack)).IsTrue();
                await Assert.That(fields.Single(f => f.Name == field).Field.GetStringProperty("data_type")).IsEqualTo(dataType);
                await Assert.That(fields.Select(f => f.Stack).Distinct()).IsEquivalentTo([stack]);
            }
        }

        private static KVObject Event(params (string Key, KVObject Value)[] keys)
        {
            var data = KVObject.Collection();

            foreach (var (key, value) in keys)
            {
                data.Add(key, value);
            }

            return data;
        }

        private static KVObject Fragment(string eventName, string fieldName) => Event(("event_name", eventName), ("field_name", fieldName));

        private static SoundEventIndex CreateIndex()
        {
            var index = new SoundEventIndex();

            index.AddEvents("soundevents/base.vsndevts_c",
            [
                ("Base.Weapon", Event(("type", "csgo_mega"), ("volume", 0.5f), ("pitch", 1f))),
                ("Base.Reverb", Event(("type", "reverb_send"), ("reverb", 0.3f), ("volume", 0.1f))),
                ("Base.Falloff", Event(("type", "falloff"), ("distance_max", 2000f), ("base", "Base.Reverb"))),
                ("Loop.A", Event(("base", "Loop.B"), ("a", 1))),
                ("Loop.B", Event(("base", "Loop.A"), ("b", 2))),
                ("Shared", Event(("volume", 1f))),
            ]);

            index.AddEvents("soundevents/weapons.vsndevts_c",
            [
                ("Weapon.Fire", Event(("base", "Base.Weapon"), ("volume", 0.9f), ("vsnd_files", KVObject.Array(["sounds/weapons/fire_01.vsnd", "sounds/weapons/fire_02.vsnd"])))),
                ("Weapon.Fragments", Event(("base", KVObject.Array([Fragment("Base.Falloff", "falloff"), Fragment("Base.Missing", "missing")])), ("pitch", 2f))),
                ("Weapon.Layered", Event(("type", "csgo_mega"), ("soundevent_01", "Weapon.Fire"), ("limiter_event_name", "Weapon.Layered"), ("dsp_preset", "Shared"), ("vsnd_files", "sounds/weapons/fire_01.vsnd"))),
                ("Shared", Event(("volume", 0.2f))),
            ]);

            return index;
        }

        [Test]
        public async Task ResolvesStringBaseWithType()
        {
            var resolved = CreateIndex().Resolve("Weapon.Fire");

            using (Assert.Multiple())
            {
                await Assert.That(resolved.Select(r => r.Key)).IsEquivalentTo(["volume", "vsnd_files", "type", "pitch"]);
                await Assert.That(resolved.Single(r => r.Key == "volume").Source).IsNull();
                await Assert.That((float)resolved.Single(r => r.Key == "volume").Value).IsEqualTo(0.9f);
                await Assert.That(resolved.Single(r => r.Key == "type").Source).IsEqualTo("Base.Weapon");
                await Assert.That((string)resolved.Single(r => r.Key == "type").Value).IsEqualTo("csgo_mega");
            }
        }

        [Test]
        public async Task ResolvesArrayBaseWithoutFragmentTypes()
        {
            var resolved = CreateIndex().Resolve("Weapon.Fragments");

            using (Assert.Multiple())
            {
                await Assert.That(resolved.Select(r => r.Key)).IsEquivalentTo(["pitch", "distance_max", "reverb", "volume"]);
                await Assert.That(resolved.Single(r => r.Key == "distance_max").Source).IsEqualTo("Base.Falloff");
                await Assert.That(resolved.Single(r => r.Key == "reverb").Source).IsEqualTo("Base.Reverb");
            }
        }

        [Test]
        public async Task ResolvingCyclicBasesTerminates()
        {
            var resolved = CreateIndex().Resolve("Loop.A");

            await Assert.That(resolved.Select(r => r.Key)).IsEquivalentTo(["a", "b"]);
        }

        [Test]
        public async Task FollowsBasesUpToMaxDepth()
        {
            var index = new SoundEventIndex();
            var events = new List<(string Name, KVObject Data)>();

            for (var i = 0; i <= SoundEventIndex.MaxBaseDepth + 2; i++)
            {
                events.Add(($"Chain.{i}", Event(("base", $"Chain.{i + 1}"), ($"key_{i}", i))));
            }

            index.AddEvents("soundevents/chain.vsndevts_c", events);

            var keys = index.Resolve("Chain.0").Select(r => r.Key).ToList();

            using (Assert.Multiple())
            {
                await Assert.That(keys).Contains($"key_{SoundEventIndex.MaxBaseDepth + 1}");
                await Assert.That(keys).DoesNotContain($"key_{SoundEventIndex.MaxBaseDepth + 2}");
            }
        }

        [Test]
        public async Task FirstDefinitionWins()
        {
            var index = CreateIndex();
            var definitions = index.GetDefinitions("Shared");

            using (Assert.Multiple())
            {
                await Assert.That(definitions.Select(d => d.File)).IsEquivalentTo(["soundevents/base.vsndevts_c", "soundevents/weapons.vsndevts_c"]);
                await Assert.That(index.Find("Shared")!.GetFloatProperty("volume")).IsEqualTo(1f);
                await Assert.That(index.GetDefinitions("Unknown")).IsEmpty();
            }
        }

        [Test]
        public async Task FindsEventReferences()
        {
            var index = CreateIndex();

            using (Assert.Multiple())
            {
                await Assert.That(index.GetEventReferences("Weapon.Layered", index.Find("Weapon.Layered")!)).IsEquivalentTo([("soundevent_01", "Weapon.Fire")]);
                await Assert.That(index.GetEventReferences("Weapon.Fragments", index.Find("Weapon.Fragments")!)).IsEquivalentTo([("base", "Base.Falloff"), ("base", "Base.Missing")]);
                await Assert.That(index.GetReferencedBy("Weapon.Fire")).IsEquivalentTo([("Weapon.Layered", "soundevent_01")]);
                await Assert.That(index.GetReferencedBy("base.reverb")).IsEquivalentTo([("Base.Falloff", "base")]);
                await Assert.That(index.GetReferencedBy("Shared")).IsEmpty();
            }
        }

        [Test]
        public async Task FindsSoundReferences()
        {
            var index = CreateIndex();

            using (Assert.Multiple())
            {
                await Assert.That(index.GetSoundReferences(index.Find("Weapon.Fire")!)).IsEquivalentTo([("vsnd_files", "sounds/weapons/fire_01.vsnd"), ("vsnd_files", "sounds/weapons/fire_02.vsnd")]);
                await Assert.That(index.GetEventsUsingSound("sounds/weapons/fire_01.vsnd_c")).IsEquivalentTo(["Weapon.Fire", "Weapon.Layered"]);
                await Assert.That(index.GetEventsUsingSound("sounds/weapons/fire_02.vsnd")).IsEquivalentTo(["Weapon.Fire"]);
            }
        }
    }
}
