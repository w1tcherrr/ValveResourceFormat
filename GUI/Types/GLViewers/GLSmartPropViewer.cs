using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using GUI.Controls;
using GUI.Utils;
using ValveResourceFormat.Renderer;
using ValveResourceFormat.Renderer.SceneNodes;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.ResourceTypes.SmartProps;

namespace GUI.Types.GLViewers
{
    class GLSmartPropViewer : GLSingleNodeViewer
    {
        private readonly SmartProp smartProp;
        private readonly List<ModelSceneNode> spawnedNodes = [];
        private readonly Dictionary<string, object> variableOverrides = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> loggedWarnings = [];
        private readonly List<Control> createdControls = [];
        private readonly List<(SmartPropParameter Parameter, Control Target, Action<object>? SetValue)> parameterControls = [];
        private SmartPropDocument? document;
        private SmartPropEvaluationResult? lastResult;
        private Label? statusLabel;
        private Timer? rebuildTimer;
        private int seed;
        private bool suppressUiEvents;
        private bool disposed;

        public GLSmartPropViewer(VrfGuiContext vrfGuiContext, RendererContext rendererContext, SmartProp smartProp) : base(vrfGuiContext, rendererContext)
        {
            this.smartProp = smartProp;
        }

        public override void Dispose()
        {
            // Stop the debounce timer before the GL context goes away; a WM_TIMER already queued
            // could otherwise trigger a rebuild against a disposed context
            disposed = true;
            rebuildTimer?.Stop();
            rebuildTimer?.Dispose();

            base.Dispose();

            statusLabel?.Dispose();

            foreach (var control in createdControls)
            {
                control.Dispose();
            }

            createdControls.Clear();
            parameterControls.Clear();
        }

        protected override void LoadScene()
        {
            base.LoadScene();

            document = SmartPropDocument.Load(smartProp);
            EvaluateAndSpawn();
        }

        private void EvaluateAndSpawn()
        {
            Debug.Assert(document != null);

            lastResult = document.Evaluate(new SmartPropEvaluationOptions
            {
                Seed = seed,
                FileLoader = GuiContext,
                VariableOverrides = variableOverrides,
            });

            foreach (var diagnostic in lastResult.Diagnostics)
            {
                if (loggedWarnings.Add(diagnostic.Message))
                {
                    Log.Warn(nameof(GLSmartPropViewer), diagnostic.Message);
                }
            }

            foreach (var placement in lastResult.Placements)
            {
                var resource = GuiContext.LoadFileCompiled(placement.ModelName);

                if (resource?.DataBlock is not Model model)
                {
                    if (loggedWarnings.Add(placement.ModelName))
                    {
                        Log.Warn(nameof(GLSmartPropViewer), $"Failed to load model \"{placement.ModelName}\"");
                    }

                    continue;
                }

                var node = new ModelSceneNode(Scene, model)
                {
                    Transform = Matrix4x4.CreateScale(placement.Scale) * placement.Transform,
                };

                node.Tint = placement.TintColor;

                if (placement.MaterialGroupName != null)
                {
                    node.SetMaterialGroup(placement.MaterialGroupName);
                }

                Scene.Add(node, true);
                spawnedNodes.Add(node);
            }
        }

        /// <summary>
        /// Coalesces rapid UI changes (e.g. dragging a numeric input) into a single rebuild.
        /// </summary>
        private void RequestRebuild()
        {
            if (suppressUiEvents)
            {
                return;
            }

            if (rebuildTimer == null)
            {
                Rebuild();
                return;
            }

            rebuildTimer.Stop();
            rebuildTimer.Start();
        }

        private void Rebuild()
        {
            if (suppressUiEvents || disposed)
            {
                return;
            }

            try
            {
                RebuildCore();
            }
            catch (Exception e)
            {
                Log.Error(nameof(GLSmartPropViewer), $"Smart prop rebuild failed: {e}");

                if (statusLabel != null)
                {
                    statusLabel.Text = "Evaluation failed (see console)";
                }
            }
        }

        private void RebuildCore()
        {
            using (var lockedGl = MakeCurrent())
            {
                foreach (var node in spawnedNodes)
                {
                    Scene.Remove(node, true);
                    node.Delete();
                }

                spawnedNodes.Clear();

                EvaluateAndSpawn();

                // Scene.Initialize assigned lighting bindings and per-object GPU buffers to the initial
                // nodes once; nodes spawned after that need the same treatment or they render unlit
                foreach (var node in spawnedNodes)
                {
                    node.EnvMaps.Clear();
                    node.EnvMaps.AddRange(Scene.LightingInfo.EnvMaps);
                    node.ShaderEnvMapVisibility = node.ShaderEnvMapVisibility.Store(node.EnvMaps);
                }

                if (spawnedNodes.Count > 0)
                {
                    Scene.RecreateObjectBuffers();
                }

                Scene.UpdateOctrees();
            }

            UpdateParameterStates();
        }

        protected override void AddUiControls()
        {
            Debug.Assert(UiControl != null);
            Debug.Assert(document != null);
            Debug.Assert(lastResult != null);

            suppressUiEvents = true;

            try
            {
                rebuildTimer = new Timer { Interval = 120 };
                rebuildTimer.Tick += (_, __) =>
                {
                    rebuildTimer.Stop();
                    Rebuild();
                };

                using (UiControl.BeginGroup("Smart Prop"))
                {
                    AddSeedControls();

                    statusLabel = new Label
                    {
                        AutoSize = true,
                        Padding = new Padding(2),
                    };
                    UiControl.AddControl(statusLabel);
                    createdControls.Add(statusLabel);
                }

                if (document.Choices.Count > 0 || document.Parameters.Count > 0)
                {
                    using var _ = UiControl.BeginGroup("Variables");

                    foreach (var choice in document.Choices)
                    {
                        AddChoiceControl(choice);
                    }

                    foreach (var parameter in document.Parameters)
                    {
                        AddParameterControl(parameter);
                    }
                }

                if (lastResult.GizmoOutputs.Count > 0)
                {
                    using var _ = UiControl.BeginGroup("Handles");

                    foreach (var gizmo in lastResult.GizmoOutputs)
                    {
                        AddGizmoControl(gizmo);
                    }
                }
            }
            finally
            {
                suppressUiEvents = false;
            }

            UpdateParameterStates();

            base.AddUiControls();
        }

        /// <summary>
        /// Applies hide/read-only expressions against the latest variable values and refreshes the status line.
        /// </summary>
        private void UpdateParameterStates()
        {
            if (document == null || lastResult == null)
            {
                return;
            }

            foreach (var (parameter, control, _) in parameterControls)
            {
                if (!string.IsNullOrEmpty(parameter.HideExpression))
                {
                    control.Visible = !document.EvaluateCondition(parameter.HideExpression, lastResult.VariableValues);
                }

                if (!string.IsNullOrEmpty(parameter.ReadOnlyExpression))
                {
                    control.Enabled = !document.EvaluateCondition(parameter.ReadOnlyExpression, lastResult.VariableValues);
                }
            }

            if (statusLabel != null)
            {
                if (lastResult.Placements.Count == 0)
                {
                    // An empty viewport needs an explanation: authored-empty, over-filtered, or a load failure
                    var firstDiagnostic = lastResult.Diagnostics.Count > 0
                        ? $" - {lastResult.Diagnostics[0].Message}"
                        : " - nothing places with the current variables";

                    statusLabel.Text = $"0 models{firstDiagnostic}";
                    statusLabel.ForeColor = System.Drawing.Color.OrangeRed;
                }
                else
                {
                    var warningsText = lastResult.Diagnostics.Count > 0
                        ? $", {lastResult.Diagnostics.Count} warnings (see console)"
                        : string.Empty;

                    statusLabel.Text = $"{lastResult.Placements.Count} models{warningsText}";
                    statusLabel.ResetForeColor();
                }
            }
        }

        private void AddSeedControls()
        {
            Debug.Assert(UiControl != null);

            var (seedPanel, seedNumeric) = CreateIntInput("Seed", 0, 0, 1_000_000, value =>
            {
                if (value != seed)
                {
                    seed = value;
                    RequestRebuild();
                }
            });

            var rerollButton = new ThemedButton
            {
                Text = "Re-roll Seed",
                Dock = DockStyle.Top,
            };
            rerollButton.Click += (_, __) =>
            {
                // Setting the numeric fires its ValueChanged, which rebuilds
                seedNumeric.Value = Random.Shared.Next(0, 1_000_000);
            };

            UiControl.AddControl(seedPanel);
            UiControl.AddControl(rerollButton);
            createdControls.Add(seedPanel);
            createdControls.Add(rerollButton);
        }

        private (Panel Panel, ThemedIntNumeric Numeric) CreateIntInput(string labelText, int startValue, int minValue, int maxValue, Action<int> onChanged)
        {
            var panel = new Panel();
            panel.Height = panel.AdjustForDPI(22);

            var label = new Label
            {
                Text = labelText,
                Dock = DockStyle.Fill,
            };

            var numeric = new ThemedIntNumeric
            {
                MinValue = minValue,
                MaxValue = maxValue,
                DragWithinRange = true,
                DragDistance = 600,
                Value = startValue,
                Dock = DockStyle.Right,
                Padding = new Padding(0, 0, 4, 0),
            };
            numeric.Width = numeric.AdjustForDPI(50);
            numeric.ValueChanged += (sender, _) =>
            {
                if (!suppressUiEvents)
                {
                    onChanged(((ThemedIntNumeric)sender!).Value);
                }
            };

            panel.Controls.Add(label);
            panel.Controls.Add(numeric);

            return (panel, numeric);
        }

        private ComboBox AddDropdown(string labelText, string[] items, int defaultIndex, Action<int> onSelected)
        {
            Debug.Assert(UiControl != null);

            var comboBox = UiControl.AddSelection(labelText, (_, index) =>
            {
                if (suppressUiEvents || index < 0 || index >= items.Length)
                {
                    return;
                }

                onSelected(index);
            });

            createdControls.Add(comboBox);
            comboBox.Items.AddRange(items);
            comboBox.SelectedIndex = defaultIndex >= 0 && defaultIndex < items.Length ? defaultIndex : 0;

            return comboBox;
        }

        private void AddChoiceControl(SmartPropChoice choice)
        {
            if (choice.Options.Count == 0)
            {
                return;
            }

            var optionNames = choice.Options.Select(o => o.Name).ToArray();
            var defaultIndex = Array.FindIndex(optionNames, name => name == choice.DefaultOption);

            AddDropdown(choice.Name, optionNames, defaultIndex, index =>
            {
                // Clear everything any option of this choice can set, so switching between
                // options with different variable sets doesn't leave stale values pinned
                foreach (var option in choice.Options)
                {
                    foreach (var name in option.VariableValues.Keys)
                    {
                        variableOverrides.Remove(name);
                    }
                }

                foreach (var (name, value) in choice.Options[index].VariableValues)
                {
                    variableOverrides[name] = value;
                }

                // Reflect the option's values in the individual parameter controls
                suppressUiEvents = true;

                try
                {
                    foreach (var (parameter, _, setValue) in parameterControls)
                    {
                        if (setValue != null && choice.Options[index].VariableValues.TryGetValue(parameter.Name, out var value))
                        {
                            setValue(value);
                        }
                    }
                }
                finally
                {
                    suppressUiEvents = false;
                }

                RequestRebuild();
            });
        }

        private void AddGizmoControl(SmartPropGizmoOutput gizmo)
        {
            Debug.Assert(UiControl != null);

            var initial = (float)gizmo.InitialValue;
            var min = (float)(gizmo.MinValue ?? Math.Min(initial - 512f, -512f));
            var max = (float)(gizmo.MaxValue ?? Math.Max(initial + 512f, 512f));

            var input = RendererControl.CreateFloatInput(gizmo.Label, value =>
            {
                if (suppressUiEvents)
                {
                    return;
                }

                variableOverrides[gizmo.VariableName] = (double)value;
                RequestRebuild();
            }, initial, min, max);

            UiControl.AddControl(input);
            createdControls.Add(input);
        }

        private void AddParameterControl(SmartPropParameter parameter)
        {
            Debug.Assert(UiControl != null);

            switch (parameter.Type)
            {
                case SmartPropParameterType.Bool:
                    {
                        var defaultChecked = parameter.DefaultValue is bool b && b;
                        var checkBox = UiControl.AddCheckBox(parameter.DisplayName, defaultChecked, isChecked =>
                        {
                            if (suppressUiEvents)
                            {
                                return;
                            }

                            variableOverrides[parameter.Name] = isChecked;
                            RequestRebuild();
                        });

                        createdControls.Add(checkBox);
                        parameterControls.Add((parameter, checkBox.Parent ?? checkBox,
                            value => checkBox.Checked = value is bool flag && flag));
                        break;
                    }

                case SmartPropParameterType.Int when parameter.KnownValues != null:
                case SmartPropParameterType.String when parameter.KnownValues != null:
                    AddValueDropdown(parameter, parameter.KnownValues);
                    break;

                case SmartPropParameterType.Int:
                    {
                        var defaultValue = parameter.DefaultValue is double d ? (int)d : 0;
                        var (min, max) = IntegerRange(parameter, defaultValue);

                        var (panel, numeric) = CreateIntInput(parameter.DisplayName, defaultValue, min, max, value =>
                        {
                            variableOverrides[parameter.Name] = (double)value;
                            RequestRebuild();
                        });

                        UiControl.AddControl(panel);
                        createdControls.Add(panel);
                        parameterControls.Add((parameter, panel,
                            value => numeric.Value = (int)Math.Round(SmartPropExpressionToNumber(value))));
                        break;
                    }

                case SmartPropParameterType.Float:
                    {
                        var defaultValue = parameter.DefaultValue is double d ? (float)d : 0f;
                        var (min, max) = FloatRange(parameter, defaultValue);

                        var input = RendererControl.CreateFloatInput(parameter.DisplayName, value =>
                        {
                            if (suppressUiEvents)
                            {
                                return;
                            }

                            variableOverrides[parameter.Name] = (double)value;
                            RequestRebuild();
                        }, defaultValue, min, max);

                        var numeric = input.Controls.OfType<ThemedFloatNumeric>().First();

                        UiControl.AddControl(input);
                        createdControls.Add(input);
                        parameterControls.Add((parameter, input,
                            value => numeric.Value = (float)SmartPropExpressionToNumber(value)));
                        break;
                    }

                case SmartPropParameterType.Color:
                    AddColorControl(parameter);
                    break;

                case SmartPropParameterType.MaterialGroup:
                    AddMaterialGroupControl(parameter);
                    break;

                case SmartPropParameterType.Model:
                case SmartPropParameterType.Material:
                    AddTextInput(parameter, validateResource: true);
                    break;

                case SmartPropParameterType.String:
                    AddTextInput(parameter, validateResource: false);
                    break;

                case SmartPropParameterType.Vector2:
                    AddVectorInput(parameter, 2);
                    break;

                case SmartPropParameterType.Vector3:
                    AddVectorInput(parameter, 3);
                    break;

                case SmartPropParameterType.Vector4:
                    AddVectorInput(parameter, 4);
                    break;

                default:
                    AddPlaceholder(parameter, "unsupported type");
                    break;
            }
        }

        private static (int Min, int Max) IntegerRange(SmartPropParameter parameter, int defaultValue)
        {
            var min = (int)(parameter.MinValue ?? Math.Min(defaultValue, 0));
            int max;

            if (parameter.MaxValue is double authoredMax)
            {
                max = (int)authoredMax;
            }
            else if (parameter.MinValue is double authoredMin)
            {
                // Mirror the single authored bound around the default
                max = Math.Max(defaultValue + (defaultValue - (int)authoredMin), defaultValue + 10);
            }
            else
            {
                max = Math.Max(defaultValue + 100, 100);
            }

            return (min, Math.Max(min, max));
        }

        private static (float Min, float Max) FloatRange(SmartPropParameter parameter, float defaultValue)
        {
            var min = (float)(parameter.MinValue ?? Math.Min(defaultValue, 0f));
            float max;

            if (parameter.MaxValue is double authoredMax)
            {
                max = (float)authoredMax;
            }
            else if (parameter.MinValue is double authoredMin)
            {
                max = Math.Max(defaultValue + (defaultValue - (float)authoredMin), defaultValue + 10f);
            }
            else
            {
                max = Math.Max(defaultValue + 100f, 100f);
            }

            return (min, Math.Max(min, max));
        }

        private static double SmartPropExpressionToNumber(object? value) => value switch
        {
            double d => d,
            bool b => b ? 1.0 : 0.0,
            string s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0.0,
            _ => 0.0,
        };

        private void AddValueDropdown(SmartPropParameter parameter, IReadOnlyList<object> values)
        {
            var items = values.Select(FormatValue).ToArray();
            var defaultText = FormatValue(parameter.DefaultValue);
            var defaultIndex = Array.FindIndex(items, text => string.Equals(text, defaultText, StringComparison.OrdinalIgnoreCase));

            var comboBox = AddDropdown(parameter.DisplayName, items, defaultIndex, index =>
            {
                variableOverrides[parameter.Name] = values[index];
                RequestRebuild();
            });

            parameterControls.Add((parameter, comboBox.Parent ?? comboBox, value =>
            {
                var text = FormatValue(value);
                var index = Array.FindIndex(items, item => string.Equals(item, text, StringComparison.OrdinalIgnoreCase));

                if (index >= 0)
                {
                    comboBox.SelectedIndex = index;
                }
            }));
        }

        private static string FormatValue(object? value) => value switch
        {
            double d => d == Math.Floor(d) ? ((long)d).ToString(CultureInfo.InvariantCulture) : d.ToString(CultureInfo.InvariantCulture),
            bool b => b ? "true" : "false",
            null => string.Empty,
            _ => value.ToString() ?? string.Empty,
        };

        private void AddColorControl(SmartPropParameter parameter)
        {
            Debug.Assert(UiControl != null);

            var currentColor = parameter.DefaultValue is Vector4 v ? v : Vector4.One;

            var panel = new Panel { Dock = DockStyle.Top };
            panel.Height = panel.AdjustForDPI(26);

            var button = new ThemedButton
            {
                Text = parameter.DisplayName,
                Dock = DockStyle.Fill,
                BackColor = ToDrawingColor(currentColor),
            };

            var alphaNumeric = new ThemedIntNumeric
            {
                MinValue = 0,
                MaxValue = 255,
                Value = (int)(Math.Clamp(currentColor.W, 0f, 1f) * 255f),
                Dock = DockStyle.Right,
            };
            alphaNumeric.Width = alphaNumeric.AdjustForDPI(40);

            void Push()
            {
                var color = button.BackColor;
                variableOverrides[parameter.Name] = new Vector4(color.R, color.G, color.B, alphaNumeric.Value) / 255f;
                RequestRebuild();
            }

            button.Click += (_, __) =>
            {
                using var dialog = new ColorDialog
                {
                    Color = button.BackColor,
                    FullOpen = true,
                };

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    button.BackColor = dialog.Color;
                    Push();
                }
            };

            alphaNumeric.ValueChanged += (_, __) =>
            {
                if (!suppressUiEvents)
                {
                    Push();
                }
            };

            panel.Controls.Add(button);
            panel.Controls.Add(alphaNumeric);
            UiControl.AddControl(panel);
            createdControls.Add(panel);
            parameterControls.Add((parameter, panel, value =>
            {
                if (value is Vector4 color)
                {
                    button.BackColor = ToDrawingColor(color);
                    alphaNumeric.Value = (int)(Math.Clamp(color.W, 0f, 1f) * 255f);
                }
            }));
        }

        private void AddMaterialGroupControl(SmartPropParameter parameter)
        {
            if (parameter.ModelName == null)
            {
                AddPlaceholder(parameter, "no model reference");
                return;
            }

            var resource = GuiContext.LoadFileCompiled(parameter.ModelName);

            if (resource?.DataBlock is not Model model)
            {
                AddPlaceholder(parameter, "model not loaded");
                return;
            }

            var groups = model.GetMaterialGroups().Select(g => g.Name).ToArray();

            if (groups.Length == 0)
            {
                AddPlaceholder(parameter, "no skins");
                return;
            }

            var modelFileName = System.IO.Path.GetFileNameWithoutExtension(parameter.ModelName);
            var defaultGroup = parameter.DefaultValue as string;
            var defaultIndex = string.IsNullOrEmpty(defaultGroup) ? 0 : Array.IndexOf(groups, defaultGroup);

            var comboBox = AddDropdown($"{parameter.DisplayName} ({modelFileName})", groups, defaultIndex, index =>
            {
                variableOverrides[parameter.Name] = groups[index];
                RequestRebuild();
            });

            parameterControls.Add((parameter, comboBox.Parent ?? comboBox, value =>
            {
                if (value is string group)
                {
                    var index = Array.IndexOf(groups, group);

                    if (index >= 0)
                    {
                        comboBox.SelectedIndex = index;
                    }
                }
            }));
        }

        /// <summary>
        /// A parameter whose control cannot be built still shows up, disabled, with the reason.
        /// </summary>
        private void AddPlaceholder(SmartPropParameter parameter, string reason)
        {
            Debug.Assert(UiControl != null);

            var label = new Label
            {
                Text = $"{parameter.DisplayName}: {reason}",
                AutoSize = true,
                Enabled = false,
                Padding = new Padding(2),
            };

            UiControl.AddControl(label);
            createdControls.Add(label);
            parameterControls.Add((parameter, label, null));
        }

        private void AddTextInput(SmartPropParameter parameter, bool validateResource)
        {
            Debug.Assert(UiControl != null);

            var panel = new Panel { Dock = DockStyle.Top };
            panel.Height = panel.AdjustForDPI(validateResource ? 58 : 44);
            var label = new Label { Text = parameter.DisplayName, Dock = DockStyle.Top, AutoSize = false };
            label.Height = label.AdjustForDPI(18);
            var textBox = new ThemedTextBox { Dock = DockStyle.Top, Text = parameter.DefaultValue as string ?? string.Empty };

            Label? hintLabel = null;

            if (validateResource)
            {
                hintLabel = new Label
                {
                    Text = "not found",
                    Dock = DockStyle.Bottom,
                    AutoSize = false,
                    ForeColor = System.Drawing.Color.OrangeRed,
                    Visible = false,
                };
                hintLabel.Height = hintLabel.AdjustForDPI(14);
            }

            void Commit()
            {
                if (suppressUiEvents)
                {
                    return;
                }

                var text = textBox.Text.Trim();
                var current = variableOverrides.TryGetValue(parameter.Name, out var existing) ? existing as string : parameter.DefaultValue as string;

                if (string.Equals(text, current, StringComparison.Ordinal))
                {
                    return;
                }

                if (hintLabel != null && text.Length > 0)
                {
                    hintLabel.Visible = GuiContext.LoadFileCompiled(text) == null;
                }

                variableOverrides[parameter.Name] = text;
                RequestRebuild();
            }

            textBox.LostFocus += (_, __) => Commit();
            textBox.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    Commit();
                }
            };

            if (hintLabel != null)
            {
                panel.Controls.Add(hintLabel);
            }

            panel.Controls.Add(textBox);
            panel.Controls.Add(label);
            UiControl.AddControl(panel);
            createdControls.Add(panel);
            parameterControls.Add((parameter, panel, value => textBox.Text = value as string ?? string.Empty));
        }

        private void AddVectorInput(SmartPropParameter parameter, int axisCount)
        {
            Debug.Assert(UiControl != null);

            var current = parameter.DefaultValue switch
            {
                Vector3 v => new Vector4(v, 0f),
                Vector4 v => v,
                _ => Vector4.Zero,
            };

            var panel = new Panel { Dock = DockStyle.Top };
            panel.Height = panel.AdjustForDPI(44);
            var label = new Label { Text = parameter.DisplayName, Dock = DockStyle.Top, AutoSize = false };
            label.Height = label.AdjustForDPI(18);
            var row = new Panel { Dock = DockStyle.Fill };

            void Changed()
            {
                if (suppressUiEvents)
                {
                    return;
                }

                variableOverrides[parameter.Name] = axisCount == 4
                    ? current
                    : new Vector3(current.X, current.Y, current.Z);
                RequestRebuild();
            }

            var numerics = new ThemedFloatNumeric[axisCount];

            ThemedFloatNumeric MakeAxis(float value, Action<float> setter)
            {
                var numeric = new ThemedFloatNumeric
                {
                    MinValue = -100_000f,
                    MaxValue = 100_000f,
                    Value = value,
                    Dock = DockStyle.Left,
                };
                numeric.Width = numeric.AdjustForDPI(60);
                numeric.ValueChanged += (sender, _) =>
                {
                    setter(((ThemedFloatNumeric)sender!).Value);
                    Changed();
                };
                return numeric;
            }

            // Add in reverse order because DockStyle.Left stacks controls right to left
            for (var axis = axisCount - 1; axis >= 0; axis--)
            {
                var capturedAxis = axis;
                numerics[axis] = MakeAxis(GetAxis(current, axis), value => SetAxis(ref current, capturedAxis, value));
                row.Controls.Add(numerics[axis]);
            }

            panel.Controls.Add(row);
            panel.Controls.Add(label);
            UiControl.AddControl(panel);
            createdControls.Add(panel);
            parameterControls.Add((parameter, panel, value =>
            {
                current = value switch
                {
                    Vector3 v => new Vector4(v, 0f),
                    Vector4 v => v,
                    _ => current,
                };

                for (var axis = 0; axis < axisCount; axis++)
                {
                    numerics[axis].Value = GetAxis(current, axis);
                }
            }));
        }

        private static float GetAxis(Vector4 value, int axis) => axis switch
        {
            0 => value.X,
            1 => value.Y,
            2 => value.Z,
            _ => value.W,
        };

        private static void SetAxis(ref Vector4 value, int axis, float component)
        {
            value = axis switch
            {
                0 => value with { X = component },
                1 => value with { Y = component },
                2 => value with { Z = component },
                _ => value with { W = component },
            };
        }

        private static System.Drawing.Color ToDrawingColor(Vector4 color) => System.Drawing.Color.FromArgb(
            (int)(Math.Clamp(color.X, 0f, 1f) * 255f),
            (int)(Math.Clamp(color.Y, 0f, 1f) * 255f),
            (int)(Math.Clamp(color.Z, 0f, 1f) * 255f));
    }
}
