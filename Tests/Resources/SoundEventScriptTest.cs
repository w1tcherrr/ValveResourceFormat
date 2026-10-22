using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
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

        [Test]
        public async Task ImportsKeyValues1StackFieldsWithoutReplacingOwnFields()
        {
            const string Text = """
                "child"
                {
                    "public"
                    {
                        "operator" "util_null"
                        "operator_variables"
                        {
                            "volume" { "data_type" "float" "value" "1.0" }
                            "volume" { "data_type" "float" "value" "0.5" }
                            "import_public_opvars" "imported"
                            "reverb" { "data_type" "public_opvar" "value" "reverb_stack" }
                        }
                    }
                    "import_operator"
                    {
                        "operator" "sos_import_stack"
                        "import_stack" "not_public"
                        "auto_connect_public_fields" "true"
                    }
                }
                "imported"
                {
                    "public"
                    {
                        "operator" "util_null"
                        "operator_variables"
                        {
                            "volume" { "data_type" "float" "value" "9.0" }
                            "pitch" { "data_type" "float" "value" "1.0" }
                        }
                    }
                }
                "reverb_stack"
                {
                    "public"
                    {
                        "operator" "util_null"
                        "operator_variables"
                        {
                            "reverb_level" { "data_type" "float" "value" "0.2" }
                        }
                    }
                }
                "not_public"
                {
                    "public"
                    {
                        "operator" "util_null"
                        "operator_variables"
                        {
                            "hidden" { "data_type" "float" "value" "1.0" }
                        }
                    }
                }
                """;

            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Text));
            var index = new SoundEventIndex();

            foreach (var (name, stack) in KVSerializer.Create(KVSerializationFormat.KeyValues1Text).DeserializeWithoutRoot(stream))
            {
                index.AddStack("soundstacks/test.vsndstck_c", name, stack);
            }

            var fields = index.GetStackFields("child");

            using (Assert.Multiple())
            {
                await Assert.That(fields.Select(f => f.Name)).IsEquivalentTo(["volume", "reverb", "pitch", "reverb_level"]);
                await Assert.That(fields.Single(f => f.Name == "volume").Field["value"].ToSingle(CultureInfo.InvariantCulture)).IsEqualTo(0.5f);
                await Assert.That(fields.Single(f => f.Name == "pitch").Stack).IsEqualTo("imported");
                await Assert.That(fields.Single(f => f.Name == "reverb_level").Stack).IsEqualTo("reverb_stack");
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

        private static KVObject Fragment(string eventName, string? fieldName = null)
            => fieldName == null ? Event(("event_name", eventName)) : Event(("event_name", eventName), ("field_name", fieldName));

        private static SoundEventIndex CreateIndex()
        {
            var index = new SoundEventIndex();

            index.AddEvents("soundevents/base.vsndevts_c",
            [
                ("Base.Weapon", Event(("type", "csgo_mega"), ("volume", 0.5f), ("pitch", 1f))),
                ("Base.Grand", Event(("type", "csgo_mega"), ("x", "grand"), ("y", "grand"), ("z", "grand"))),
                ("Base.Early", Event(("base", "Base.Grand"), ("x", "early"), ("y", "early"))),
                ("Base.Late", Event(("type", "csgo_mega"), ("x", "late"))),
                ("Base.Other", Event(("type", "other_stack"), ("w", "other"))),
                ("Loop.A", Event(("base", "Loop.B"), ("a", 1))),
                ("Loop.B", Event(("base", "Loop.A"), ("b", 2))),
                ("Shared", Event(("volume", 1f))),
            ]);

            index.AddEvents("soundevents/weapons.vsndevts_c",
            [
                ("Weapon.Fire", Event(("base", "Base.Weapon"), ("volume", 0.9f), ("vsnd_files", KVObject.Array(["sounds/weapons/fire_01.vsnd", "sounds/weapons/fire_02.vsnd"])))),
                ("Weapon.Typed", Event(("base", "Base.Weapon"), ("type", "own_stack"))),
                ("Weapon.Fragments", Event(("type", "csgo_mega"), ("base", KVObject.Array([Fragment("Base.Early"), Fragment("Base.Late"), Fragment("Base.Missing")])), ("own", 1))),
                ("Weapon.Untyped", Event(("base", KVObject.Array([Fragment("Base.Late")])))),
                ("Weapon.Mismatch", Event(("type", "csgo_mega"), ("base", KVObject.Array([Fragment("Base.Other"), Fragment("Base.Late")])))),
                ("Weapon.Layered", Event(("type", "csgo_mega"), ("soundevent_01", "Weapon.Fire"), ("limiter_event_name", "Weapon.Layered"), ("dsp_preset", "Shared"), ("vsnd_files", "sounds/weapons/fire_01.vsnd"))),
                ("Shared", Event(("volume", 0.2f))),
            ]);

            return index;
        }

        private static List<(string Key, KVObject Value, string? Source)> ResolveKeys(SoundEventIndex index, string name) => index.Resolve(index.Find(name)!).Keys;

        [Test]
        public async Task StringBaseGivesTypeOnlyToUntypedEvents()
        {
            var index = CreateIndex();
            var fire = ResolveKeys(index, "Weapon.Fire");
            var typed = ResolveKeys(index, "Weapon.Typed");

            using (Assert.Multiple())
            {
                await Assert.That(fire.Select(r => r.Key)).IsEquivalentTo(["volume", "vsnd_files", "type", "pitch"]);
                await Assert.That(fire.Single(r => r.Key == "volume").Source).IsNull();
                await Assert.That((float)fire.Single(r => r.Key == "volume").Value).IsEqualTo(0.9f);
                await Assert.That(fire.Single(r => r.Key == "type").Source).IsEqualTo("Base.Weapon");
                await Assert.That((string)typed.Single(r => r.Key == "type").Value).IsEqualTo("own_stack");
            }
        }

        [Test]
        public async Task OwnKeysWinOverLaterFragmentsOverEarlierFragmentsOverTheirBases()
        {
            var index = CreateIndex();
            var (keys, problems, rejected) = index.Resolve(index.Find("Weapon.Fragments")!);

            using (Assert.Multiple())
            {
                await Assert.That(keys.Single(r => r.Key == "own").Source).IsNull();
                await Assert.That((string)keys.Single(r => r.Key == "x").Value).IsEqualTo("late");
                await Assert.That((string)keys.Single(r => r.Key == "y").Value).IsEqualTo("early");
                await Assert.That((string)keys.Single(r => r.Key == "z").Value).IsEqualTo("grand");
                await Assert.That((string)keys.Single(r => r.Key == "type").Value).IsEqualTo("csgo_mega");
                await Assert.That(keys.Single(r => r.Key == "type").Source).IsNull();
                await Assert.That(problems).IsEmpty();
                await Assert.That(rejected).IsFalse();
            }
        }

        [Test]
        public async Task ArrayBaseNeedsOwnTypeAndMatchingFragments()
        {
            var index = CreateIndex();
            var untyped = index.Resolve(index.Find("Weapon.Untyped")!);
            var mismatch = index.Resolve(index.Find("Weapon.Mismatch")!);

            using (Assert.Multiple())
            {
                await Assert.That(untyped.Rejected).IsTrue();
                await Assert.That(untyped.Keys.Select(r => r.Key)).DoesNotContain("type");
                await Assert.That(mismatch.Rejected).IsFalse();
                await Assert.That(mismatch.Problems).Count().IsEqualTo(1);
                await Assert.That(mismatch.Keys.Select(r => r.Key)).IsEquivalentTo(["type", "x"]);
            }
        }

        [Test]
        public async Task FieldFragmentsMustHaveTheFieldsSubStackType()
        {
            var events = new Dictionary<string, KVObject>(StringComparer.OrdinalIgnoreCase)
            {
                ["Reverb"] = Event(("type", "reverb_send"), ("volume", 0.1f)),
                ["Falloff"] = Event(("type", "falloff"), ("distance", 2000f)),
            };
            var soundEvent = Event(("type", "hero"), ("volume", 1f), ("base", KVObject.Array([Fragment("Reverb", "reverb_field"), Fragment("Falloff", "falloff_field")])));

            var (keys, problems, _) = SoundEventIndex.Resolve(soundEvent, events.GetValueOrDefault,
                (stack, field) => (stack, field) switch
                {
                    ("hero", "reverb_field") => "reverb_send",
                    ("hero", "falloff_field") => "other_falloff",
                    _ => null,
                });

            using (Assert.Multiple())
            {
                await Assert.That(keys.Where(r => r.Key == "volume").Select(r => r.Source)).IsEquivalentTo([null, "Reverb via reverb_field"]);
                await Assert.That(keys.Select(r => r.Key)).DoesNotContain("distance");
                await Assert.That(problems).Count().IsEqualTo(1);
            }
        }

        [Test]
        public async Task RejectsMoreThanFourBaseVisits()
        {
            var index = new SoundEventIndex();
            var events = new List<(string Name, KVObject Data)>();

            for (var i = 0; i <= SoundEventIndex.MaxBaseVisits + 1; i++)
            {
                events.Add(($"Chain.{i}", Event(("base", $"Chain.{i + 1}"), ($"key_{i}", i))));
            }

            index.AddEvents("soundevents/chain.vsndevts_c", events);

            var withinLimit = index.Resolve(index.Find("Chain.1")!);
            var overLimit = index.Resolve(index.Find("Chain.0")!);
            var loops = CreateIndex();
            var cycle = loops.Resolve(loops.Find("Loop.A")!);

            using (Assert.Multiple())
            {
                await Assert.That(withinLimit.Rejected).IsFalse();
                await Assert.That(overLimit.Rejected).IsTrue();
                await Assert.That(overLimit.Keys.Select(r => r.Key)).Contains($"key_{SoundEventIndex.MaxBaseVisits + 1}");
                await Assert.That(cycle.Rejected).IsTrue();
                await Assert.That(cycle.Keys.Select(r => r.Key)).IsEquivalentTo(["a", "b"]);
            }
        }

        [Test]
        public async Task LastDefinitionWins()
        {
            var index = CreateIndex();
            var definitions = index.GetDefinitions("Shared");

            using (Assert.Multiple())
            {
                await Assert.That(definitions.Select(d => d.File)).IsEquivalentTo(["soundevents/base.vsndevts_c", "soundevents/weapons.vsndevts_c"]);
                await Assert.That(index.Find("Shared")!.GetFloatProperty("volume")).IsEqualTo(0.2f);
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
                await Assert.That(index.GetEventReferences("Weapon.Fragments", index.Find("Weapon.Fragments")!)).IsEquivalentTo([("base", "Base.Early"), ("base", "Base.Late"), ("base", "Base.Missing")]);
                await Assert.That(index.GetReferencedBy("Weapon.Fire")).IsEquivalentTo([("Weapon.Layered", "soundevent_01")]);
                await Assert.That(index.GetReferencedBy("base.grand")).IsEquivalentTo([("Base.Early", "base")]);
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
