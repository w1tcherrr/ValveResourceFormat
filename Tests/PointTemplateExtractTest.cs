using System.IO;
using System.Linq;
using NUnit.Framework;
using SteamDatabase.ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.IO;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;
using DMElement = Datamodel.Element;

namespace Tests
{
    /// <summary>
    /// Tests vmap export of point_template entities, whose child entities are compiled into
    /// separate entity lumps with transforms stored relative to the template.
    ///
    /// The fixture vpk was compiled with resourcecompiler from a map containing only
    /// prop_dynamic crates and the point_templates referencing them:
    /// <code>
    /// template_offset   at (100, 0, 0)             -> crate_offset  at (50, 50, 50), 30 yaw
    /// template_rotated  at (0, 100, 0), 90 yaw     -> crate_rotated at (-50, 60, 70), 30 yaw
    /// template_sheared  at (100, 0, 0), (1, 2, 1)  -> crate_sheared at (10, 20, 30), 45 yaw
    /// template_shared_a at (200, 0, 0)             -> crate_shared  at (25, 25, 25), 15 yaw
    /// template_shared_b at (0, 200, 0), 90 yaw     -> crate_shared  (same entity as above)
    /// template_multi    at (300, 0, 0)             -> crate_multi_a at (310, 10, 0)
    ///                                                 crate_multi_b at (310, -10, 0)
    /// </code>
    /// </summary>
    [TestFixture]
    public class PointTemplateExtractTest
    {
        private const float Tolerance = 0.01f;
        private const string CrateModel = "models/cs_italy/crate/italy_wood_crate_1.vmdl";

        private Package package = null!;
        private GameFileLoader loader = null!;
        private Datamodel.Datamodel vmap = null!;
        private List<DMElement> entities = null!;

        [OneTimeSetUp]
        public void ExtractMap()
        {
            var vpkPath = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "point_template_test.vpk");

            package = new Package();
            package.Read(vpkPath);

            loader = new GameFileLoader(package, vpkPath);
            using var vmapResource = loader.LoadFile("maps/point_template_test.vmap_c");

            var extract = new MapExtract(vmapResource!, loader);
            var vmapData = extract.ToValveMap();

            using var stream = new MemoryStream(vmapData);
            vmap = Datamodel.Datamodel.Load(stream, Datamodel.Codecs.DeferredMode.Disabled);

            var world = (DMElement)vmap.Root!["world"]!;
            entities = ((Datamodel.ElementArray)world["children"]!).OfType<DMElement>().ToList();
        }

        [OneTimeTearDown]
        public void Dispose()
        {
            vmap?.Dispose();
            loader?.Dispose();
            package?.Dispose();
        }

        [Test]
        public void CompiledMapResourceParses()
        {
            using var resource = new Resource();
            resource.Read(Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "point_template_test.vmap_c"));

            Assert.That(resource.ResourceType, Is.EqualTo(ResourceType.Map));

            foreach (var block in resource.Blocks)
            {
                Assert.That(block.ToString(), Is.Not.Null);
            }

            var references = resource.ExternalReferences!.ResourceRefInfoList.Select(r => r.Name).ToList();

            Assert.Multiple(() =>
            {
                Assert.That(references, Has.Member("maps/point_template_test/world.vwrld"));
                Assert.That(references, Has.Member("maps/point_template_test/entities/default_ents.vents"));
                Assert.That(references.Count(r => r.EndsWith("#entitylumpname.vents", StringComparison.Ordinal)), Is.EqualTo(6));
            });
        }

        [Test]
        public void WorldResourceReferencesEntityLumps()
        {
            using var worldResource = loader.LoadFile("maps/point_template_test/world.vwrld_c");
            var world = (World)worldResource!.DataBlock!;

            Assert.Multiple(() =>
            {
                Assert.That(world.GetEntityLumpNames(), Has.Some.Contain("default_ents"));
                Assert.That(world.GetWorldNodeNames(), Is.Not.Empty);
            });
        }

        [Test]
        public void EntityLumpHierarchyParses()
        {
            using var lumpResource = loader.LoadFile("maps/point_template_test/entities/default_ents.vents_c");
            var lump = (EntityLump)lumpResource!.DataBlock!;

            var childNames = lump.GetChildEntityNames();
            Assert.That(childNames, Has.Length.EqualTo(6));

            var compiledEntities = lump.GetEntities();
            var templates = compiledEntities.Where(e => e.GetStringProperty("classname") == "point_template").ToList();

            Assert.Multiple(() =>
            {
                Assert.That(compiledEntities.Single(e => e.GetStringProperty("classname") == "worldspawn"), Is.Not.Null);
                Assert.That(templates, Has.Count.EqualTo(6));
            });

            // every template's entitylumpname must resolve to one of the child lumps
            var childLumpNames = childNames.Select(name =>
            {
                using var childResource = loader.LoadFileCompiled(name);
                return ((EntityLump)childResource!.DataBlock!).Name;
            }).ToList();

            foreach (var template in templates)
            {
                Assert.That(childLumpNames, Has.Member(template.GetStringProperty("entitylumpname")));
            }

            Assert.That(lump.ToString(), Is.Not.Empty);
        }

        // The compiler stores child lump entities inverse-transformed by the template's origin and
        // rotation (but not its scale), which is why these origins differ from the authored ones.
        [TestCase("3#entityLumpName", "crate_offset", -50f, 50f, 50f, 30f)]
        [TestCase("5#entityLumpName", "crate_rotated", -40f, 50f, 70f, -60f)]
        [TestCase("7#entityLumpName", "crate_sheared", -90f, 20f, 30f, 45f)]
        [TestCase("9#entityLumpName", "crate_shared", -175f, 25f, 25f, 15f)]
        [TestCase("10#entityLumpName", "crate_shared", -175f, -25f, 25f, -75f)]
        public void ChildLumpStoresTemplateRelativeTransform(string lumpName, string targetname, float x, float y, float z, float yaw)
        {
            var lump = LoadChildLump(lumpName);
            var crate = lump.GetEntities().Single();

            Assert.Multiple(() =>
            {
                Assert.That(crate.GetStringProperty("classname"), Is.EqualTo("prop_dynamic"));
                Assert.That(crate.GetStringProperty("targetname"), Is.EqualTo($"[PR#]{targetname}"));
                Assert.That(crate.GetStringProperty("model"), Is.EqualTo(CrateModel));
            });

            AssertVector(crate.GetVector3Property("origin"), new Vector3(x, y, z));
            Assert.That(crate.GetVector3Property("angles").Y, Is.EqualTo(yaw).Within(Tolerance));
            AssertVector(crate.GetVector3Property("scales", Vector3.One), Vector3.One);
        }

        [Test]
        public void ChildLumpKeepsMultipleEntitiesInTemplateOrder()
        {
            var lump = LoadChildLump("13#entityLumpName");
            var crates = lump.GetEntities();

            Assert.That(crates, Has.Count.EqualTo(2));

            Assert.Multiple(() =>
            {
                Assert.That(crates[0].GetStringProperty("targetname"), Is.EqualTo("[PR#]crate_multi_a"));
                Assert.That(crates[1].GetStringProperty("targetname"), Is.EqualTo("[PR#]crate_multi_b"));
                Assert.That(crates[0].GetStringProperty("_template_lump_ent_index"), Is.EqualTo("0"));
                Assert.That(crates[1].GetStringProperty("_template_lump_ent_index"), Is.EqualTo("1"));
            });

            AssertVector(crates[0].GetVector3Property("origin"), new Vector3(10, 10, 0));
            AssertVector(crates[1].GetVector3Property("origin"), new Vector3(10, -10, 0));
        }

        private EntityLump LoadChildLump(string lumpName)
        {
            var path = $"maps/point_template_test/entities/{lumpName.ToLowerInvariant()}.vents_c";
            using var resource = loader.LoadFile(path);
            var lump = (EntityLump)resource!.DataBlock!;

            Assert.That(lump.Name, Is.EqualTo(lumpName));
            return lump;
        }

        [Test]
        public void ChildPlacementIsRestored()
        {
            var crate = FindEntity("crate_offset");

            AssertVector((Vector3)crate["origin"]!, new Vector3(50, 50, 50));
            AssertAngles((Datamodel.QAngle)crate["angles"]!, 0, 30, 0);
            AssertVector((Vector3)crate["scales"]!, Vector3.One);

            var template = FindEntity("template_offset");
            Assert.That(Properties(template)["Template01"], Is.EqualTo("crate_offset"));
        }

        [Test]
        public void RotatedTemplateChildPlacementIsRestored()
        {
            // the child is stored inverse-rotated relative to the template, so composing it
            // with the template transform must restore the authored placement
            var crate = FindEntity("crate_rotated");

            AssertVector((Vector3)crate["origin"]!, new Vector3(-50, 60, 70));
            AssertAngles((Datamodel.QAngle)crate["angles"]!, 0, 30, 0);

            var template = FindEntity("template_rotated");
            AssertVector((Vector3)template["origin"]!, new Vector3(0, 100, 0));
            AssertAngles((Datamodel.QAngle)template["angles"]!, 0, 90, 0);
        }

        [Test]
        public void NonUniformlyScaledTemplateExportsRepresentableTransform()
        {
            // composing the rotated child with the non-uniformly scaled template produces a
            // sheared matrix that Matrix4x4.Decompose rejects; the export must not fail on it.
            // The compiler stores the child offset without inverse-scaling it, while the export
            // (like the renderer and gltf exporter) applies the full template transform,
            // which is why the expected origin is not the authored (10, 20, 30)
            var crate = FindEntity("crate_sheared");

            AssertVector((Vector3)crate["origin"]!, new Vector3(10, 40, 30));
            AssertAngles((Datamodel.QAngle)crate["angles"]!, 0, 45, 0);
            AssertVector((Vector3)crate["scales"]!, new Vector3(1, 2, 1));
        }

        [Test]
        public void EntitySharedByTwoTemplatesIsDeduplicated()
        {
            // the compiler clones an entity referenced by two templates into both child lumps;
            // the clones must collapse back into one entity, or each recompile would make
            // every template capture all the same-named copies and multiply the spawns
            var crates = entities.Where(e => Properties(e).TryGetValue("targetname", out var name) && (string?)name == "crate_shared").ToList();

            Assert.That(crates, Has.Count.EqualTo(1));
            AssertVector((Vector3)crates[0]["origin"]!, new Vector3(25, 25, 25));
            AssertAngles((Datamodel.QAngle)crates[0]["angles"]!, 0, 15, 0);

            Assert.That(Properties(FindEntity("template_shared_a"))["Template01"], Is.EqualTo("crate_shared"));
            Assert.That(Properties(FindEntity("template_shared_b"))["Template01"], Is.EqualTo("crate_shared"));
        }

        [Test]
        public void TemplateWithMultipleChildrenNumbersItsReferences()
        {
            AssertVector((Vector3)FindEntity("crate_multi_a")["origin"]!, new Vector3(310, 10, 0));
            AssertVector((Vector3)FindEntity("crate_multi_b")["origin"]!, new Vector3(310, -10, 0));

            var properties = Properties(FindEntity("template_multi"));
            Assert.That(properties["Template01"], Is.EqualTo("crate_multi_a"));
            Assert.That(properties["Template02"], Is.EqualTo("crate_multi_b"));
        }

        [Test]
        public void ComposeWithMatchesMatrixProductWhenRepresentable()
        {
            var child = new MapExtract.EntityTransform(
                new Vector3(0.5f),
                Quaternion.CreateFromAxisAngle(Vector3.UnitZ, float.DegreesToRadians(30)),
                new Vector3(10, 20, 30));
            var parent = new MapExtract.EntityTransform(
                new Vector3(2f),
                Quaternion.CreateFromAxisAngle(Vector3.UnitZ, float.DegreesToRadians(90)),
                new Vector3(100, 0, 0));

            var composed = child.ComposeWith(parent);

            var matrixProduct = TrsMatrix(child) * TrsMatrix(parent);
            Assert.That(Matrix4x4.Decompose(matrixProduct, out var scales, out var rotation, out var translation), Is.True);

            AssertVector(composed.Origin, translation);
            AssertVector(composed.Scales, scales);
            if (Quaternion.Dot(composed.Rotation, rotation) < 0)
            {
                rotation = -rotation;
            }

            Assert.That((composed.Rotation - rotation).Length(), Is.LessThan(Tolerance));
        }

        [Test]
        public void ComposeWithNeverProducesShear()
        {
            var child = new MapExtract.EntityTransform(
                Vector3.One,
                Quaternion.CreateFromAxisAngle(Vector3.UnitZ, float.DegreesToRadians(45)),
                new Vector3(64, 0, 0));
            var parent = new MapExtract.EntityTransform(
                new Vector3(1, 2, 1),
                Quaternion.Identity,
                new Vector3(100, 200, 0));

            var matrixProduct = TrsMatrix(child) * TrsMatrix(parent);
            Assert.That(Matrix4x4.Decompose(matrixProduct, out _, out _, out _), Is.False);

            var composed = child.ComposeWith(parent);

            AssertVector(composed.Origin, matrixProduct.Translation);
            AssertVector(composed.Scales, new Vector3(1, 2, 1));
            AssertVector(Vector3.Transform(Vector3.UnitX, composed.Rotation), new Vector3(0.7071f, 0.7071f, 0));
        }

        private static Matrix4x4 TrsMatrix(MapExtract.EntityTransform transform)
            => Matrix4x4.CreateScale(transform.Scales)
                * Matrix4x4.CreateFromQuaternion(transform.Rotation)
                * Matrix4x4.CreateTranslation(transform.Origin);

        private static DMElement Properties(DMElement entity)
            => (DMElement)entity["entity_properties"]!;

        private DMElement FindEntity(string targetname)
            => entities.Single(entity =>
                entity.TryGetValue("entity_properties", out var propsObject)
                && propsObject is DMElement properties
                && properties.TryGetValue("targetname", out var name)
                && (string?)name == targetname);

        private static void AssertVector(Vector3 actual, Vector3 expected)
        {
            Assert.Multiple(() =>
            {
                Assert.That(actual.X, Is.EqualTo(expected.X).Within(Tolerance));
                Assert.That(actual.Y, Is.EqualTo(expected.Y).Within(Tolerance));
                Assert.That(actual.Z, Is.EqualTo(expected.Z).Within(Tolerance));
            });
        }

        private static void AssertAngles(Datamodel.QAngle actual, float pitch, float yaw, float roll)
        {
            Assert.Multiple(() =>
            {
                Assert.That(actual.Pitch, Is.EqualTo(pitch).Within(Tolerance));
                Assert.That(actual.Yaw, Is.EqualTo(yaw).Within(Tolerance));
                Assert.That(actual.Roll, Is.EqualTo(roll).Within(Tolerance));
            });
        }
    }
}
