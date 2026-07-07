using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.ResourceTypes.SmartProps;
using ValveResourceFormat.Serialization.KeyValues;

namespace Tests
{
    public class SmartPropTest
    {
        private const string Kv3Header = "<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:generic:version{7412167c-06e9-4698-aff2-e63eb59037e7} -->\n";

        private static SmartPropDocument LoadFileDocument(string filename)
        {
            var file = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", filename);
            using var resource = new Resource
            {
                FileName = file,
            };
            resource.Read(file);

            var smartProp = (SmartProp?)resource.DataBlock;
            ArgumentNullException.ThrowIfNull(smartProp);

            return SmartPropDocument.Load(smartProp);
        }

        private static SmartPropEvaluationResult EvaluateFile(string filename, SmartPropEvaluationOptions? options = null)
            => LoadFileDocument(filename).Evaluate(options ?? new SmartPropEvaluationOptions());

        private static KVObject ParseRoot(string kv3Body)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Kv3Header + kv3Body));
            return KVDocumentExtensions.ParseKV3(stream).Root;
        }

        private static SmartPropEvaluationResult EvaluateText(string kv3Body, SmartPropEvaluationOptions? options = null)
            => SmartPropDocument.Load(ParseRoot(kv3Body)).Evaluate(options ?? new SmartPropEvaluationOptions());

        private static IEnumerable<string> Messages(SmartPropEvaluationResult result)
            => result.Diagnostics.Select(d => d.Message);

        [Test]
        public void ColorTint_DefaultsFollowChoices()
        {
            var document = LoadFileDocument("test_color_tint.vsmart_c");
            var result = document.Evaluate(new SmartPropEvaluationOptions());

            Assert.That(result.Placements, Has.Count.EqualTo(3));

            // Choice "Base Color" defaults to Green (ColorIndex = 1), driving a SPECIFIC tint selection
            var sphere = result.Placements[0];
            Assert.That(sphere.ModelName, Is.EqualTo("models/editor/placeholder_sphere.vmdl"));
            Assert.That(sphere.Scale.X, Is.EqualTo(4f).Within(1e-5));
            Assert.That(sphere.TintColor.X, Is.EqualTo(0f).Within(1e-5));
            Assert.That(sphere.TintColor.Y, Is.EqualTo(1f).Within(1e-5));
            Assert.That(sphere.TintColor.Z, Is.EqualTo(0f).Within(1e-5));

            // Choice "Secondary Model" defaults to Sphere (index 0), placed 50 units up,
            // tinted by sampling the gradient at SecondaryColorValue = 0 (first stop, yellow)
            var secondary = result.Placements[1];
            Assert.That(secondary.ModelName, Is.EqualTo("models/editor/placeholder_sphere.vmdl"));
            Assert.That(secondary.Transform.Translation.Z, Is.EqualTo(50f).Within(1e-5));
            Assert.That(secondary.TintColor.X, Is.EqualTo(1f).Within(1e-5));
            Assert.That(secondary.TintColor.Y, Is.EqualTo(1f).Within(1e-5));
            Assert.That(secondary.TintColor.Z, Is.EqualTo(0f).Within(1e-5));

            // The box reads back the color the secondary model saved via SaveColor
            var box = result.Placements[2];
            Assert.That(box.ModelName, Is.EqualTo("models/editor/placeholder_box.vmdl"));
            Assert.That(box.Transform.Translation.X, Is.EqualTo(50f).Within(1e-5));
            Assert.That(box.TintColor, Is.EqualTo(secondary.TintColor));

            Assert.That(document.Parameters, Has.Count.EqualTo(1));
            Assert.That(document.Parameters[0].Name, Is.EqualTo("SecondaryColorValue"));
            Assert.That(document.Parameters[0].DisplayName, Is.EqualTo("Secondary Color"));
            Assert.That(document.Parameters[0].Type, Is.EqualTo(SmartPropParameterType.Float));

            Assert.That(document.Choices, Has.Count.EqualTo(2));
            Assert.That(document.Choices[0].Name, Is.EqualTo("Base Color"));
            Assert.That(document.Choices[0].DefaultOption, Is.EqualTo("Green"));
            Assert.That(document.Choices[0].Options, Has.Count.EqualTo(3));

            Assert.That(result.Diagnostics, Is.Empty);
        }

        [Test]
        public void ColorTint_VariableOverrides()
        {
            var result = EvaluateFile("test_color_tint.vsmart_c", new SmartPropEvaluationOptions
            {
                VariableOverrides = new Dictionary<string, object>
                {
                    ["ColorIndex"] = 2.0,
                    ["SecondaryModelIndex"] = 2.0,
                },
            });

            Assert.That(result.Placements, Has.Count.EqualTo(3));

            // ColorIndex = 2 selects blue
            var sphere = result.Placements[0];
            Assert.That(sphere.TintColor.X, Is.EqualTo(0f).Within(1e-5));
            Assert.That(sphere.TintColor.Y, Is.EqualTo(0f).Within(1e-5));
            Assert.That(sphere.TintColor.Z, Is.EqualTo(1f).Within(1e-5));

            // SecondaryModelIndex = 2 picks the arrow
            var secondary = result.Placements[1];
            Assert.That(secondary.ModelName, Is.EqualTo("models/editor/arrow.vmdl"));
            Assert.That(secondary.Transform.Translation.Z, Is.EqualTo(50f).Within(1e-5));
        }

        [Test]
        public void PickOne_RandomIsDeterministicPerSeed()
        {
            var first = EvaluateFile("fk01_f04_window01.vsmart_c");
            var again = EvaluateFile("fk01_f04_window01.vsmart_c");

            Assert.That(first.Placements, Has.Count.EqualTo(1));
            Assert.That(first.Placements[0].ModelName, Does.StartWith("models/de_inferno/frame_kit_01/fk01_f04_window01_"));
            Assert.That(again.Placements[0].ModelName, Is.EqualTo(first.Placements[0].ModelName));

            // Some seed in a small range must produce the other window
            var seenModels = new HashSet<string>();

            for (var seed = 0; seed < 32; seed++)
            {
                var result = EvaluateFile("fk01_f04_window01.vsmart_c", new SmartPropEvaluationOptions { Seed = seed });
                seenModels.Add(result.Placements[0].ModelName);
            }

            Assert.That(seenModels, Has.Count.EqualTo(2));
        }

        [Test]
        public void PlaceOnPath_EmptyDefaultPathPlacesNothing()
        {
            var result = EvaluateFile("street_curb_01.vsmart_c");

            Assert.That(result.Placements, Is.Empty);
            Assert.That(result.Diagnostics, Is.Empty);
        }

        [Test]
        public void Attributes_LiteralSourceComponentsExpression()
        {
            var result = EvaluateText(
                """
                {
                    generic_data_type = "CSmartPropRoot"
                    m_Variables =
                    [
                        { _class = "CSmartPropVariable_Float" m_VariableName = "y" m_DefaultValue = 4.0 },
                    ]
                    m_Children =
                    [
                        {
                            _class = "CSmartPropElement_Model"
                            m_sModelName = "models/test.vmdl"
                            m_Modifiers =
                            [
                                {
                                    _class = "CSmartPropOperation_Translate"
                                    m_vPosition =
                                    {
                                        m_Components =
                                        [
                                            { m_Expression = "min(3, 5) + 2" },
                                            { m_SourceName = "y" },
                                            1.5,
                                        ]
                                    }
                                },
                                {
                                    _class = "CSmartPropOperation_Scale"
                                    m_flScale = { m_Expression = "y > 2 ? 2.0 : 0.5" }
                                },
                            ]
                        },
                    ]
                }
                """);

            Assert.That(result.Placements, Has.Count.EqualTo(1));

            var placement = result.Placements[0];
            Assert.That(placement.Transform.Translation.X, Is.EqualTo(5f).Within(1e-5));
            Assert.That(placement.Transform.Translation.Y, Is.EqualTo(4f).Within(1e-5));
            Assert.That(placement.Transform.Translation.Z, Is.EqualTo(1.5f).Within(1e-5));
            Assert.That(placement.Scale.X, Is.EqualTo(2f).Within(1e-5));
            Assert.That(result.Diagnostics, Is.Empty);
        }

        [TestCase("EQUAL", 3.0, true)]
        [TestCase("EQUAL", 4.0, false)]
        [TestCase("NOT_EQUAL", 4.0, true)]
        [TestCase("GREATER", 2.0, true)]
        [TestCase("GREATER", 3.0, false)]
        [TestCase("GREATER_OR_EQUAL", 3.0, true)]
        [TestCase("LESS", 4.0, true)]
        [TestCase("LESS_OR_EQUAL", 2.0, false)]
        public void Filter_VariableValueComparisons(string comparison, double value, bool expectPlacement)
        {
            var result = EvaluateText(
                $$"""
                {
                    generic_data_type = "CSmartPropRoot"
                    m_Variables =
                    [
                        { _class = "CSmartPropVariable_Int" m_VariableName = "num" m_DefaultValue = 3 },
                    ]
                    m_Children =
                    [
                        {
                            _class = "CSmartPropElement_Model"
                            m_sModelName = "models/test.vmdl"
                            m_Modifiers =
                            [
                                {
                                    _class = "CSmartPropFilter_VariableValue"
                                    m_VariableComparison =
                                    {
                                        m_Name = "num"
                                        m_Value = {{value.ToString(System.Globalization.CultureInfo.InvariantCulture)}}
                                        m_Comparison = "{{comparison}}"
                                    }
                                },
                            ]
                        },
                    ]
                }
                """);

            Assert.That(result.Placements, Has.Count.EqualTo(expectPlacement ? 1 : 0));
        }

        [Test]
        public void PickOne_FirstSkipsIneligibleChildren()
        {
            var result = EvaluateText(
                """
                {
                    generic_data_type = "CSmartPropRoot"
                    m_Children =
                    [
                        {
                            _class = "CSmartPropElement_PickOne"
                            m_SelectionMode = "FIRST"
                            m_Children =
                            [
                                {
                                    _class = "CSmartPropElement_Model"
                                    m_sModelName = "models/a.vmdl"
                                    m_SelectionCriteria =
                                    [
                                        { _class = "CSmartPropSelectionCriteria_IsValid" m_Expression = "1 == 2" },
                                    ]
                                },
                                { _class = "CSmartPropElement_Model" m_sModelName = "models/b.vmdl" m_bEnabled = false },
                                { _class = "CSmartPropElement_Model" m_sModelName = "models/c.vmdl" },
                            ]
                        },
                    ]
                }
                """);

            Assert.That(result.Placements, Has.Count.EqualTo(1));
            Assert.That(result.Placements[0].ModelName, Is.EqualTo("models/c.vmdl"));
        }

        [Test]
        public void GroupState_DoesNotLeakToSiblings()
        {
            var result = EvaluateText(
                """
                {
                    generic_data_type = "CSmartPropRoot"
                    m_Children =
                    [
                        {
                            _class = "CSmartPropElement_Group"
                            m_Modifiers =
                            [
                                { _class = "CSmartPropOperation_Translate" m_vPosition = [ 100.0, 0.0, 0.0 ] },
                                { _class = "CSmartPropOperation_Scale" m_flScale = 2.0 },
                            ]
                            m_Children =
                            [
                                { _class = "CSmartPropElement_Model" m_sModelName = "models/inner.vmdl" },
                            ]
                        },
                        { _class = "CSmartPropElement_Model" m_sModelName = "models/outer.vmdl" },
                        {
                            _class = "CSmartPropElement_ModifyState"
                            m_Modifiers =
                            [
                                { _class = "CSmartPropOperation_Translate" m_vPosition = [ 0.0, 0.0, 10.0 ] },
                            ]
                        },
                        { _class = "CSmartPropElement_Model" m_sModelName = "models/after.vmdl" },
                    ]
                }
                """);

            Assert.That(result.Placements, Has.Count.EqualTo(3));

            var inner = result.Placements[0];
            Assert.That(inner.Transform.Translation.X, Is.EqualTo(100f).Within(1e-5));
            Assert.That(inner.Scale.X, Is.EqualTo(2f).Within(1e-5));

            // Sibling after the group is unaffected by the group's modifiers
            var outer = result.Placements[1];
            Assert.That(outer.Transform.Translation.X, Is.Zero);
            Assert.That(outer.Scale.X, Is.EqualTo(1f).Within(1e-5));

            // ModifyState affects siblings that follow it
            var after = result.Placements[2];
            Assert.That(after.Transform.Translation.Z, Is.EqualTo(10f).Within(1e-5));
        }

        [Test]
        public void RandomOffset_DeterministicPerSeed()
        {
            const string Body =
                """
                {
                    generic_data_type = "CSmartPropRoot"
                    m_Children =
                    [
                        {
                            _class = "CSmartPropElement_Model"
                            m_sModelName = "models/test.vmdl"
                            m_Modifiers =
                            [
                                {
                                    _class = "CSmartPropOperation_RandomOffset"
                                    m_vRandomPositionMin = [ -100.0, -100.0, -100.0 ]
                                    m_vRandomPositionMax = [ 100.0, 100.0, 100.0 ]
                                },
                            ]
                        },
                    ]
                }
                """;

            var first = EvaluateText(Body, new SmartPropEvaluationOptions { Seed = 7 });
            var again = EvaluateText(Body, new SmartPropEvaluationOptions { Seed = 7 });
            var other = EvaluateText(Body, new SmartPropEvaluationOptions { Seed = 8 });

            Assert.That(again.Placements[0].Transform.Translation, Is.EqualTo(first.Placements[0].Transform.Translation));
            Assert.That(other.Placements[0].Transform.Translation, Is.Not.EqualTo(first.Placements[0].Transform.Translation));
        }

        [Test]
        public void NestedSmartProp_WithoutLoaderWarns()
        {
            var result = EvaluateText(
                """
                {
                    generic_data_type = "CSmartPropRoot"
                    m_Children =
                    [
                        { _class = "CSmartPropElement_SmartProp" m_sSmartProp = "smart_props/other.vsmart" },
                    ]
                }
                """);

            Assert.That(result.Placements, Is.Empty);
            Assert.That(result.Diagnostics, Has.Some.Matches<SmartPropDiagnostic>(d => d.Code == SmartPropDiagnosticCode.NestedNoLoader));
        }

        [Test]
        public void NestedSmartProp_TransformAndObjectFrame()
        {
            var child = ParseRoot(
                """
                {
                    generic_data_type = "CSmartPropRoot"
                    m_Children =
                    [
                        {
                            _class = "CSmartPropElement_Model"
                            m_sModelName = "models/element_space.vmdl"
                            m_Modifiers =
                            [
                                { _class = "CSmartPropOperation_Translate" m_vPosition = [ 1.0, 2.0, 3.0 ] },
                            ]
                        },
                        {
                            _class = "CSmartPropElement_Model"
                            m_sModelName = "models/object_space.vmdl"
                            m_Modifiers =
                            [
                                { _class = "CSmartPropOperation_SetPosition" m_vPosition = [ 5.0, 0.0, 0.0 ] },
                            ]
                        },
                    ]
                }
                """);

            var result = EvaluateText(
                """
                {
                    generic_data_type = "CSmartPropRoot"
                    m_Children =
                    [
                        {
                            _class = "CSmartPropElement_Group"
                            m_Modifiers =
                            [
                                { _class = "CSmartPropOperation_Translate" m_vPosition = [ 0.0, 0.0, 10.0 ] },
                            ]
                            m_Children =
                            [
                                { _class = "CSmartPropElement_SmartProp" m_sSmartProp = "child.vsmart" },
                            ]
                        },
                    ]
                }
                """,
                new SmartPropEvaluationOptions
                {
                    NestedDocumentResolver = name => name == "child.vsmart" ? child : null,
                });

            Assert.That(result.Placements, Has.Count.EqualTo(2));

            // Element-space translate inside the child composes with the placement transform
            var elementSpace = result.Placements[0];
            Assert.That(elementSpace.ModelName, Is.EqualTo("models/element_space.vmdl"));
            Assert.That(elementSpace.Transform.Translation.X, Is.EqualTo(1f).Within(1e-5));
            Assert.That(elementSpace.Transform.Translation.Y, Is.EqualTo(2f).Within(1e-5));
            Assert.That(elementSpace.Transform.Translation.Z, Is.EqualTo(13f).Within(1e-5));

            // The child's OBJECT frame is the placement point, so SetPosition is relative to it
            var objectSpace = result.Placements[1];
            Assert.That(objectSpace.ModelName, Is.EqualTo("models/object_space.vmdl"));
            Assert.That(objectSpace.Transform.Translation.X, Is.EqualTo(5f).Within(1e-5));
            Assert.That(objectSpace.Transform.Translation.Z, Is.EqualTo(10f).Within(1e-5));
        }

        [Test]
        public void NestedSmartProp_LocalStateIsolation()
        {
            var child = ParseRoot(
                """
                {
                    generic_data_type = "CSmartPropRoot"
                    m_Variables =
                    [
                        { _class = "CSmartPropVariable_Int" m_VariableName = "num" m_DefaultValue = 2 },
                    ]
                    m_Children =
                    [
                        {
                            _class = "CSmartPropElement_Model"
                            m_sModelName = "models/child.vmdl"
                            m_Modifiers =
                            [
                                {
                                    _class = "CSmartPropFilter_VariableValue"
                                    m_VariableComparison = { m_Name = "num" m_Value = 2 m_Comparison = "EQUAL" }
                                },
                            ]
                        },
                    ]
                }
                """);

            const string ParentBody =
                """
                {
                    generic_data_type = "CSmartPropRoot"
                    m_Variables =
                    [
                        { _class = "CSmartPropVariable_Int" m_VariableName = "num" m_DefaultValue = 1 },
                    ]
                    m_Children =
                    [
                        { _class = "CSmartPropElement_SmartProp" m_sSmartProp = "child.vsmart" {LOCAL} },
                        {
                            _class = "CSmartPropElement_Model"
                            m_sModelName = "models/parent.vmdl"
                            m_Modifiers =
                            [
                                {
                                    _class = "CSmartPropFilter_VariableValue"
                                    m_VariableComparison = { m_Name = "num" m_Value = 1 m_Comparison = "EQUAL" }
                                },
                            ]
                        },
                    ]
                }
                """;

            var options = new SmartPropEvaluationOptions
            {
                NestedDocumentResolver = name => name == "child.vsmart" ? child : null,
            };

            // Local evaluation state (default): the child sees its own declaration of num = 2
            var localResult = EvaluateText(ParentBody.Replace("{LOCAL}", string.Empty, StringComparison.Ordinal), options);
            Assert.That(localResult.Placements.Select(p => p.ModelName),
                Is.EqualTo(["models/child.vmdl", "models/parent.vmdl"]).AsCollection);

            // Shared evaluation state: the parent's num = 1 is visible inside the child, failing its filter
            var sharedResult = EvaluateText(ParentBody.Replace("{LOCAL}", "m_bLocalEvaluationState = false", StringComparison.Ordinal), options);
            Assert.That(sharedResult.Placements.Select(p => p.ModelName),
                Is.EqualTo(["models/parent.vmdl"]).AsCollection);
        }

        [Test]
        public void NestedSmartProp_DepthBudget()
        {
            KVObject? self = null;
            self = ParseRoot(
                """
                {
                    generic_data_type = "CSmartPropRoot"
                    m_nMaxDepth = 3
                    m_Children =
                    [
                        { _class = "CSmartPropElement_Model" m_sModelName = "models/level.vmdl" },
                        {
                            _class = "CSmartPropElement_SmartProp"
                            m_sSmartProp = "self.vsmart"
                            m_Modifiers =
                            [
                                { _class = "CSmartPropOperation_Translate" m_vPosition = [ 0.0, 0.0, 10.0 ] },
                            ]
                        },
                    ]
                }
                """);

            var result = SmartPropDocument.Load(self).Evaluate(new SmartPropEvaluationOptions
            {
                NestedDocumentResolver = name => name == "self.vsmart" ? self : null,
            });

            // Authored max depth 3: the document itself plus two nested levels
            Assert.That(result.Placements, Has.Count.EqualTo(3));
            Assert.That(result.Placements[0].Transform.Translation.Z, Is.EqualTo(0f).Within(1e-5));
            Assert.That(result.Placements[1].Transform.Translation.Z, Is.EqualTo(10f).Within(1e-5));
            Assert.That(result.Placements[2].Transform.Translation.Z, Is.EqualTo(20f).Within(1e-5));
        }

        [Test]
        public void NestedSmartProp_SameDocumentIdenticalRandomness()
        {
            var leaf = ParseRoot(
                """
                {
                    generic_data_type = "CSmartPropRoot"
                    m_Children =
                    [
                        {
                            _class = "CSmartPropElement_Model"
                            m_sModelName = "models/leaf.vmdl"
                            m_Modifiers =
                            [
                                {
                                    _class = "CSmartPropOperation_RandomOffset"
                                    m_vRandomPositionMin = [ -100.0, -100.0, -100.0 ]
                                    m_vRandomPositionMax = [ 100.0, 100.0, 100.0 ]
                                },
                            ]
                        },
                    ]
                }
                """);

            var result = EvaluateText(
                """
                {
                    generic_data_type = "CSmartPropRoot"
                    m_Children =
                    [
                        { _class = "CSmartPropElement_SmartProp" m_sSmartProp = "leaf.vsmart" },
                        {
                            _class = "CSmartPropElement_SmartProp"
                            m_sSmartProp = "leaf.vsmart"
                            m_Modifiers =
                            [
                                { _class = "CSmartPropOperation_Translate" m_vPosition = [ 500.0, 0.0, 0.0 ] },
                            ]
                        },
                    ]
                }
                """,
                new SmartPropEvaluationOptions
                {
                    NestedDocumentResolver = name => name == "leaf.vsmart" ? leaf : null,
                });

            Assert.That(result.Placements, Has.Count.EqualTo(2));

            // Both copies of the same nested document draw from the same cached document seed,
            // so their internal random offsets are identical relative to their anchors
            var firstOffset = result.Placements[0].Transform.Translation;
            var secondOffset = result.Placements[1].Transform.Translation - new Vector3(500f, 0f, 0f);
            Assert.That((secondOffset - firstOffset).Length(), Is.LessThan(1e-4f));
        }

        [Test]
        public void Strict_PromotesUnhandledToErrors()
        {
            const string Body =
                """
                {
                    generic_data_type = "CSmartPropRoot"
                    m_Children =
                    [
                        { _class = "CSmartPropElement_Bogus" m_sModelName = "models/a.vmdl" },
                    ]
                }
                """;

            var lenient = EvaluateText(Body);
            Assert.That(lenient.HasErrors, Is.False);
            Assert.That(lenient.Diagnostics, Has.Some.Matches<SmartPropDiagnostic>(d => d.Code == SmartPropDiagnosticCode.UnhandledElement));

            var strict = EvaluateText(Body, new SmartPropEvaluationOptions { Strict = true });
            Assert.That(strict.HasErrors, Is.True);
        }

        [Test]
        public void Hammer5Tools_ToleratedWithoutWarnings()
        {
            var root = ParseRoot(
                """
                {
                    generic_data_type = "CSmartPropRoot"
                    m_Variables =
                    [
                        {
                            _class = "CSmartPropVariable_Float"
                            m_VariableName = "size"
                            m_bExposeAsParameter = true
                            m_ParameterName = "Size"
                            m_DefaultValue = ""
                        },
                    ]
                    m_Children =
                    [
                        { _class = "Hammer5Tools_Comment" m_Comment = "note" },
                        { _class = "CSmartPropElement_Model" m_sModelName = "models/plain_string.vmdl" },
                    ]
                }
                """);

            var document = SmartPropDocument.Load(root);
            var result = document.Evaluate(new SmartPropEvaluationOptions());

            Assert.That(result.Placements, Has.Count.EqualTo(1));
            Assert.That(result.Placements[0].ModelName, Is.EqualTo("models/plain_string.vmdl"));
            Assert.That(document.Parameters, Has.Count.EqualTo(1));
            Assert.That(document.Parameters[0].DisplayName, Is.EqualTo("Size"));
            Assert.That(document.Parameters[0].DefaultValue, Is.EqualTo(0.0));
            Assert.That(result.Diagnostics, Is.Empty);
        }

        [Test]
        public void Parameters_LabelsAreHumanized()
        {
            var document = SmartPropDocument.Load(ParseRoot(
                """
                {
                    generic_data_type = "CSmartPropRoot"
                    m_Variables =
                    [
                        { _class = "CSmartPropVariable_Bool" m_VariableName = "bRandomRotation" m_bExposeAsParameter = true },
                        { _class = "CSmartPropVariable_Float" m_VariableName = "start_cap" m_bExposeAsParameter = true },
                        { _class = "CSmartPropVariable_Float" m_VariableName = "flOffset" m_bExposeAsParameter = true },
                        { _class = "CSmartPropVariable_Float" m_VariableName = "width" m_bExposeAsParameter = true m_DisplayName = "Width" },
                        { _class = "CSmartPropVariable_Float" m_VariableName = "width2" m_bExposeAsParameter = true m_DisplayName = "Width" },
                    ]
                    m_Children = []
                }
                """));

            Assert.That(document.Parameters[0].DisplayName, Is.EqualTo("Random Rotation"));
            Assert.That(document.Parameters[1].DisplayName, Is.EqualTo("Start Cap"));
            Assert.That(document.Parameters[2].DisplayName, Is.EqualTo("Offset"));

            // Authored duplicate labels are disambiguated with the variable name
            Assert.That(document.Parameters[3].DisplayName, Is.EqualTo("Width (width)"));
            Assert.That(document.Parameters[4].DisplayName, Is.EqualTo("Width (width2)"));
        }

        [Test]
        public void SaveRestoreState_RoundTrips()
        {
            var result = EvaluateText(
                """
                {
                    generic_data_type = "CSmartPropRoot"
                    m_Children =
                    [
                        {
                            _class = "CSmartPropElement_Group"
                            m_Modifiers =
                            [
                                { _class = "CSmartPropOperation_Translate" m_vPosition = [ 0.0, 0.0, 32.0 ] },
                                { _class = "CSmartPropOperation_SaveState" m_StateName = "anchor" },
                            ]
                            m_Children = []
                        },
                        {
                            _class = "CSmartPropElement_Model"
                            m_sModelName = "models/restored.vmdl"
                            m_Modifiers =
                            [
                                { _class = "CSmartPropOperation_RestoreState" m_StateName = "anchor" },
                            ]
                        },
                        {
                            _class = "CSmartPropElement_Model"
                            m_sModelName = "models/discarded.vmdl"
                            m_Modifiers =
                            [
                                { _class = "CSmartPropOperation_RestoreState" m_StateName = "missing" m_bDiscardIfUknown = true },
                            ]
                        },
                    ]
                }
                """);

            Assert.That(result.Placements, Has.Count.EqualTo(1));
            Assert.That(result.Placements[0].ModelName, Is.EqualTo("models/restored.vmdl"));
            Assert.That(result.Placements[0].Transform.Translation.Z, Is.EqualTo(32f).Within(1e-5));
        }

        [Test]
        public void PlaceMultiple_StepsUseInstanceIndex()
        {
            var result = EvaluateFile("test_steps.vsmart_c");

            Assert.That(result.Placements, Has.Count.EqualTo(36));
            Assert.That(result.Diagnostics, Is.Empty);

            for (var i = 0; i < result.Placements.Count; i++)
            {
                var placement = result.Placements[i];
                Assert.That(placement.Transform.Translation.X, Is.EqualTo(8f + 16f * i).Within(1e-3));
                Assert.That(placement.Transform.Translation.Z, Is.EqualTo(6f * i).Within(1e-3));
                Assert.That(placement.Scale.X, Is.EqualTo(1.6f).Within(1e-4));
                Assert.That(placement.Scale.Y, Is.EqualTo(6.4f).Within(1e-4));
            }
        }

        [Test]
        public void PlaceMultiple_CountOverride()
        {
            var result = EvaluateFile("test_steps.vsmart_c", new SmartPropEvaluationOptions
            {
                VariableOverrides = new Dictionary<string, object> { ["count"] = 5.0 },
            });

            Assert.That(result.Placements, Has.Count.EqualTo(5));
        }

        [Test]
        public void FitOnLine_DefaultLineFitsCapsAndSegment()
        {
            var document = LoadFileDocument("test_fit_on_line_scale.vsmart_c");
            var result = document.Evaluate(new SmartPropEvaluationOptions());

            // 64 unit line: 16 start cap, one 32 segment, 16 end cap (flipped by its own modifiers)
            Assert.That(result.Placements, Has.Count.EqualTo(3));
            Assert.That(result.Placements[0].ModelName, Is.EqualTo("models/dev/test/pipe_cap.vmdl"));
            Assert.That(result.Placements[0].Transform.Translation.X, Is.EqualTo(0f).Within(1e-3));
            Assert.That(result.Placements[1].ModelName, Is.EqualTo("models/dev/test/pipe_segment_32.vmdl"));
            Assert.That(result.Placements[1].Transform.Translation.X, Is.EqualTo(16f).Within(1e-3));
            Assert.That(result.Placements[1].Scale.X, Is.EqualTo(1f).Within(1e-4));
            Assert.That(result.Placements[2].ModelName, Is.EqualTo("models/dev/test/pipe_cap.vmdl"));
            Assert.That(result.Placements[2].Transform.Translation.X, Is.EqualTo(64f).Within(1e-3));

            // The sizer handle variables surface for UI
            Assert.That(result.GizmoOutputs.Select(g => g.VariableName), Does.Contain("end"));
        }

        [Test]
        public void FitOnLine_StretchedLineScalesLastSegment()
        {
            var result = EvaluateFile("test_fit_on_line_scale.vsmart_c", new SmartPropEvaluationOptions
            {
                VariableOverrides = new Dictionary<string, object> { ["end"] = 112.0 },
            });

            // 112 unit line: cap, a 64 segment, the 32 segment scaled to the remaining 16, cap
            Assert.That(result.Placements, Has.Count.EqualTo(4));
            Assert.That(result.Placements[1].ModelName, Does.StartWith("models/dev/test/pipe_segment_64"));
            Assert.That(result.Placements[1].Transform.Translation.X, Is.EqualTo(16f).Within(1e-3));
            Assert.That(result.Placements[2].ModelName, Is.EqualTo("models/dev/test/pipe_segment_32.vmdl"));
            Assert.That(result.Placements[2].Transform.Translation.X, Is.EqualTo(80f).Within(1e-3));
            Assert.That(result.Placements[2].Scale.X, Is.EqualTo(0.5f).Within(1e-4));
            Assert.That(result.Placements[3].Transform.Translation.X, Is.EqualTo(112f).Within(1e-3));
        }

        [Test]
        public void FillBox_ScattersWithinSizerBounds()
        {
            var result = EvaluateFile("test_fill_box.vsmart_c");

            Assert.That(result.Placements, Has.Count.EqualTo(100));

            var models = new HashSet<string>();

            foreach (var placement in result.Placements)
            {
                models.Add(placement.ModelName);
                Assert.That(Math.Abs(placement.Transform.Translation.X), Is.LessThanOrEqualTo(64.001f));
                Assert.That(Math.Abs(placement.Transform.Translation.Y), Is.LessThanOrEqualTo(64.001f));
                Assert.That(Math.Abs(placement.Transform.Translation.Z), Is.LessThanOrEqualTo(64.001f));
            }

            // The PickOne alternates randomly between box and sphere
            Assert.That(models, Has.Count.EqualTo(2));

            // Sizer bounds are exposed and overridable
            Assert.That(result.GizmoOutputs, Has.Count.EqualTo(6));

            var constrained = EvaluateFile("test_fill_box.vsmart_c", new SmartPropEvaluationOptions
            {
                VariableOverrides = new Dictionary<string, object> { ["maxX"] = 0.0 },
            });

            foreach (var placement in constrained.Placements)
            {
                Assert.That(placement.Transform.Translation.X, Is.LessThanOrEqualTo(0.001f));
            }
        }

        [Test]
        public void PlaceOnPath_FollowsSavedLocatorPositions()
        {
            var result = EvaluateFile("test_path_locators.vsmart_c");

            // Path through 4 locators, three 141.42 unit segments, spacing 20
            Assert.That(result.Placements, Has.Count.EqualTo(22));

            var first = result.Placements[0].Transform.Translation;
            Assert.That(first.X, Is.EqualTo(-150f).Within(1e-3));
            Assert.That(first.Y, Is.EqualTo(-50f).Within(1e-3));

            // Every instance lies within the path's bounding range
            foreach (var placement in result.Placements)
            {
                Assert.That(placement.Transform.Translation.X, Is.InRange(-150.001f, 150.001f));
                Assert.That(placement.Transform.Translation.Y, Is.InRange(-50.001f, 50.001f));
            }
        }

        [Test]
        public void Rotation_ComposesWithTranslation()
        {
            var result = EvaluateText(
                """
                {
                    generic_data_type = "CSmartPropRoot"
                    m_Children =
                    [
                        {
                            _class = "CSmartPropElement_Group"
                            m_Modifiers =
                            [
                                { _class = "CSmartPropOperation_Rotate" m_vRotation = [ 0.0, 90.0, 0.0 ] },
                            ]
                            m_Children =
                            [
                                {
                                    _class = "CSmartPropElement_Model"
                                    m_sModelName = "models/test.vmdl"
                                    m_Modifiers =
                                    [
                                        { _class = "CSmartPropOperation_Translate" m_vPosition = [ 10.0, 0.0, 0.0 ] },
                                    ]
                                },
                            ]
                        },
                    ]
                }
                """);

            // 90 degrees yaw rotates the element-space +X offset onto +Y
            var translation = result.Placements[0].Transform.Translation;
            Assert.That(translation.X, Is.EqualTo(0f).Within(1e-4));
            Assert.That(translation.Y, Is.EqualTo(10f).Within(1e-4));
        }
    }
}
