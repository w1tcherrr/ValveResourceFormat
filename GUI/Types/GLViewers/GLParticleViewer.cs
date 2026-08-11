using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using GUI.Controls;
using GUI.Utils;
using ValveKeyValue;
using ValveResourceFormat.Renderer;
using ValveResourceFormat.Renderer.Particles;
using ValveResourceFormat.Renderer.SceneNodes;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.ResourceTypes.ParticleUpgrade;
using ValveResourceFormat.Serialization.KeyValues;

namespace GUI.Types.GLViewers
{
    /// <summary>
    /// GL Render control with particle controls (control points? particle counts?).
    /// Renders a single <see cref="ParticleSystem"/> via a <see cref="ParticleSceneNode"/>, with UI controls for playback and an operator/renderer tree.
    /// </summary>
    class GLParticleViewer : GLSceneViewer
    {
        private static readonly Color UnsupportedColor = Color.FromArgb(224, 80, 80);
        private static readonly Color RemovedColor = Color.FromArgb(140, 140, 140);

        // Order matches the CS2 particle editor (PET): pre-emission first, then emit/init/operate,
        // forces, constraints, and renderers last.
        private static readonly (string Title, string ListName, Func<string, bool> IsSupported)[] FunctionGroups =
        [
            ("Pre-Emission Operators", "m_PreEmissionOperators", ParticleSupportInfo.IsPreEmissionOperatorSupported),
            ("Emitters", "m_Emitters", ParticleSupportInfo.IsEmitterSupported),
            ("Initializers", "m_Initializers", ParticleSupportInfo.IsInitializerSupported),
            ("Operators", "m_Operators", ParticleSupportInfo.IsOperatorSupported),
            ("Force Generators", "m_ForceGenerators", ParticleSupportInfo.IsForceGeneratorSupported),
            ("Constraints", "m_Constraints", ParticleSupportInfo.IsConstraintSupported),
            ("Renderers", "m_Renderers", ParticleSupportInfo.IsRendererSupported),
        ];

        private const int MaxChildDepth = 8;

        private readonly ParticleSystem particleSystem;
        private ParticleSceneNode? particleSceneNode;
        private GLViewerSliderControl? slowmodeTrackBar;
        private ThemedButton? restartButton;
        private ThemedButton? endCapButton;
        private bool ShowRenderBounds { get; set; }

        public GLParticleViewer(VrfGuiContext vrfGuiContext, RendererContext rendererContext, ParticleSystem particleSystem) : base(vrfGuiContext, rendererContext, Frustum.CreateEmpty())
        {
            this.particleSystem = particleSystem;
        }

        public override void Dispose()
        {
            base.Dispose();

            slowmodeTrackBar?.Dispose();
            restartButton?.Dispose();
            endCapButton?.Dispose();
        }

        protected override void LoadScene()
        {
            InitializeSoundPlayer();
            LoadDefaultLighting();
            Scene.LightingInfo.UseSceneBoundsForSunLightFrustum = false;
            Renderer.ViewBuffer!.Data!.ExperimentalLightsEnabled = true;

            particleSceneNode = new ParticleSceneNode(Scene, particleSystem, null, true)
            {
                Transform = Matrix4x4.Identity
            };
            Scene.Add(particleSceneNode, true);
        }

        protected override void OnGLLoad()
        {
            base.OnGLLoad();

            Input.Camera.SetLocation(new Vector3(200, 200, 200));
            Input.Camera.LookAt(Vector3.Zero);
        }

        protected override void AddUiControls()
        {
            Debug.Assert(UiControl != null);
            Debug.Assert(SelectedNodeRenderer != null);

            AddRenderModeSelectionControl();

            var detailLevelComboBox = UiControl.AddSelection("Detail Level", (_, i) =>
            {
                if (i < 0)
                {
                    return;
                }

                using var lockedGl = MakeCurrent();
                particleSceneNode?.SetDetailLevel((ParticleDetailLevel)i);
                particleSceneNode?.Restart();
            }, horizontal: true, fill: true);
            detailLevelComboBox.Items.AddRange(["Low", "Medium", "High", "Ultra"]);
            detailLevelComboBox.SelectedIndex = (int)ParticleDetailLevel.PARTICLEDETAIL_ULTRA;

            AddBaseGridControl();

            restartButton = new ThemedButton
            {
                Text = "Restart",
                AutoSize = true,
            };
            restartButton.Click += (_, _) =>
            {
                using var lockedGl = MakeCurrent();
                particleSceneNode?.Restart();
            };

            endCapButton = new ThemedButton
            {
                Text = "Play Endcap",
                AutoSize = true,
            };
            endCapButton.Click += (_, _) =>
            {
                using var lockedGl = MakeCurrent();
                particleSceneNode?.PlayEndCap();
            };

            using (UiControl.BeginGroup("Playback"))
            {
                UiControl.AddControl(restartButton);
                UiControl.AddControl(endCapButton);

                slowmodeTrackBar = UiControl.AddTrackBar(value =>
                {
                    particleSceneNode?.FrametimeMultiplier = value;
                }, particleSceneNode?.FrametimeMultiplier ?? 1f);
            }

            using (UiControl.BeginGroup("Display"))
            {
                UiControl.AddCheckBox("Show Render Bounds", ShowRenderBounds, value => SelectedNodeRenderer.SelectNode(value ? particleSceneNode : null));
            }

            AddOperatorTree();

            base.AddUiControls();
        }

        private void AddOperatorTree()
        {
            Debug.Assert(UiControl != null);

            var functionLists = ParticleUpgradeTrace.TraceFunctions(particleSystem.Data, particleSystem.Format);

            foreach (var (title, listName, isSupported) in FunctionGroups)
            {
                AddFunctionGroup(title, functionLists[listName], isSupported);
            }

            AddChildSystems(particleSystem.GetUpgradedData(), parentLabel: string.Empty, [], [], depth: 1);
        }

        private void AddChildSystems(KVObject parentData, string parentLabel, HashSet<string> pathStack, Dictionary<string, int> labelCounts, int depth)
        {
            if (depth > MaxChildDepth)
            {
                return;
            }

            var behaviorVersion = parentData.GetInt32Property("m_nBehaviorVersion");

            foreach (var childInfo in parentData.GetArray("m_Children") ?? [])
            {
                var childRef = childInfo.GetStringProperty("m_ChildRef");

                if (string.IsNullOrEmpty(childRef))
                {
                    continue;
                }

                var shortName = Path.GetFileNameWithoutExtension(childRef);
                var label = parentLabel.Length == 0 ? shortName : $"{parentLabel} / {shortName}";
                var occurrence = labelCounts.GetValueOrDefault(label);
                labelCounts[label] = occurrence + 1;

                if (occurrence > 0)
                {
                    label = $"{label} ({occurrence + 1})";
                }

                if (behaviorVersion >= 5 && childInfo.GetBooleanProperty("m_bDisableChild"))
                {
                    AddChildSystemGroup($"{label} (disabled)", null);
                    continue;
                }

                if (!pathStack.Add(childRef))
                {
                    continue;
                }

                ParticleSystem? childSystem = null;

                try
                {
                    childSystem = Scene.RendererContext.FileLoader.LoadFileCompiled(childRef)?.DataBlock as ParticleSystem;
                }
                catch (Exception e)
                {
                    Log.Error(nameof(GLParticleViewer), $"Failed to load child particle system '{childRef}': {e.Message}");
                }

                if (childSystem == null)
                {
                    AddChildSystemGroup($"{label} (failed to load)", null);
                }
                else
                {
                    AddChildSystemGroup(label, ParticleUpgradeTrace.TraceFunctions(childSystem.Data, childSystem.Format));
                    AddChildSystems(childSystem.GetUpgradedData(), label, pathStack, labelCounts, depth + 1);
                }

                pathStack.Remove(childRef);
            }
        }

        private void AddChildSystemGroup(string title, IReadOnlyDictionary<string, IReadOnlyList<ParticleUpgradeTrace.TracedFunction>>? functionLists)
        {
            Debug.Assert(UiControl != null);

            using var group = UiControl.BeginGroup(title);

            if (functionLists == null)
            {
                return;
            }

            foreach (var (listTitle, listName, isSupported) in FunctionGroups)
            {
                var functions = functionLists[listName];

                if (functions.Count == 0)
                {
                    continue;
                }

                var header = new Label
                {
                    Text = listTitle,
                    AutoSize = true,
                    Font = new Font(UiControl.Font, FontStyle.Bold),
                    Padding = new Padding(0, 4, 0, 1),
                };

                UiControl.AddControl(header);
                UiControl.AddControl(BuildFunctionList(functions, isSupported));
            }
        }

        private void AddFunctionGroup(string groupName, IReadOnlyList<ParticleUpgradeTrace.TracedFunction> functions, Func<string, bool> isSupported)
        {
            Debug.Assert(UiControl != null);

            if (functions.Count == 0)
            {
                return;
            }

            using (UiControl.BeginGroup(groupName))
            {
                UiControl.AddControl(BuildFunctionList(functions, isSupported));
            }
        }

        private static ListBox BuildFunctionList(IReadOnlyList<ParticleUpgradeTrace.TracedFunction> functions, Func<string, bool> isSupported)
        {
            var listBox = new ListBox
            {
                Dock = DockStyle.Fill,
                DrawMode = DrawMode.OwnerDrawFixed,
                BorderStyle = BorderStyle.None,
                SelectionMode = SelectionMode.None,
                IntegralHeight = false,
            };

            foreach (var function in functions)
            {
                var displayName = StripClassPrefix(function.Class);

                if (function.RemovedByUpgrade)
                {
                    listBox.Items.Add(new ParticleFunctionItem($"{displayName} (removed by upgrade)", FunctionSupport.Removed));
                    continue;
                }

                if (function.OriginalClass != null)
                {
                    displayName = $"{displayName} (was {StripClassPrefix(function.OriginalClass)})";
                }

                var support = isSupported(function.Class) ? FunctionSupport.Supported : FunctionSupport.Unsupported;
                listBox.Items.Add(new ParticleFunctionItem(displayName, support));
            }

            listBox.DrawItem += (_, e) =>
            {
                if (e.Index < 0)
                {
                    return;
                }

                using var brush = new SolidBrush(listBox.BackColor);
                e.Graphics.FillRectangle(brush, e.Bounds);

                var item = (ParticleFunctionItem)listBox.Items[e.Index];
                var color = item.Support switch
                {
                    FunctionSupport.Unsupported => UnsupportedColor,
                    FunctionSupport.Removed => RemovedColor,
                    _ => listBox.ForeColor,
                };

                System.Windows.Forms.TextRenderer.DrawText(e.Graphics, item.Text, e.Font, e.Bounds, color, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            };

            Themer.ThemeControl(listBox);

            listBox.Height = listBox.ItemHeight * functions.Count + 2;

            return listBox;
        }

        private static string StripClassPrefix(string className)
        {
            if (className.StartsWith("C_OP_", StringComparison.Ordinal))
            {
                return className[5..];
            }

            if (className.StartsWith("C_INIT_", StringComparison.Ordinal))
            {
                return className[7..];
            }

            return className;
        }

        private enum FunctionSupport
        {
            Supported,
            Unsupported,
            Removed,
        }

        private sealed record ParticleFunctionItem(string Text, FunctionSupport Support);

        protected override void OnPicked(object? sender, PickingTexture.PickingResponse pixelInfo)
        {
            //
        }
    }
}
