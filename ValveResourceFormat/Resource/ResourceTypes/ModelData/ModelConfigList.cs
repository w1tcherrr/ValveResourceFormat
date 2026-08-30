using System.Linq;
using ValveKeyValue;
using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.ResourceTypes.ModelData
{
    /// <summary>
    /// Represents one named configuration in a model's <see cref="ModelConfigList"/> - a preset
    /// combination of bodygroup, material group, render color, and attached-model changes.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/modellib/CModelConfig">CModelConfig</seealso>
    public sealed class ModelConfig
    {
        /// <summary>Gets the name of this configuration.</summary>
        public string Name { get; }

        /// <summary>Gets a value indicating whether this configuration is shown at the top level of the config list.</summary>
        public bool TopLevel { get; }

        /// <summary>Gets a value indicating whether this configuration is active by default in tools.</summary>
        public bool ActiveInEditorByDefault { get; }

        /// <summary>
        /// Gets the raw elements of this configuration. Each element's <c>_class</c> key names its
        /// <c>CModelConfigElement</c> subtype (e.g. <c>CModelConfigElement_SetBodygroup</c>,
        /// <c>CModelConfigElement_SetMaterialGroup</c>, <c>CModelConfigElement_AttachedModel</c>).
        /// </summary>
        public IReadOnlyList<KVObject> Elements { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="ModelConfig"/> class from <see cref="KVObject"/> data.
        /// </summary>
        public ModelConfig(KVObject data)
        {
            Name = data.GetStringProperty("m_ConfigName");
            TopLevel = data.GetBooleanProperty("m_bTopLevel");
            ActiveInEditorByDefault = data.GetBooleanProperty("m_bActiveInEditorByDefault");
            Elements = data.GetArray("m_Elements") ?? [];
        }
    }

    /// <summary>
    /// Represents a model's list of named configurations, an alternative to bodygroup/material-group
    /// selection driven by tools and gameplay code rather than authored directly on the model.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/modellib/CModelConfigList">CModelConfigList</seealso>
    public sealed class ModelConfigList
    {
        /// <summary>Gets a value indicating whether the material group selector is hidden in tools.</summary>
        public bool HideMaterialGroupInTools { get; }

        /// <summary>Gets a value indicating whether the render color selector is hidden in tools.</summary>
        public bool HideRenderColorInTools { get; }

        /// <summary>Gets the named configurations.</summary>
        public ModelConfig[] Configs { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="ModelConfigList"/> class from <see cref="KVObject"/> data.
        /// </summary>
        public ModelConfigList(KVObject data)
        {
            HideMaterialGroupInTools = data.GetBooleanProperty("m_bHideMaterialGroupInTools");
            HideRenderColorInTools = data.GetBooleanProperty("m_bHideRenderColorInTools");
            Configs = (data.GetArray("m_Configs") ?? []).Select(c => new ModelConfig(c)).ToArray();
        }
    }
}
