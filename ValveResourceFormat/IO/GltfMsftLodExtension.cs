using SharpGLTF.Schema2;
using JSONREADER = System.Text.Json.Utf8JsonReader;
using JSONWRITER = System.Text.Json.Utf8JsonWriter;

namespace ValveResourceFormat.IO
{
    /// <summary>
    /// The <c>MSFT_lod</c> node extension. Lives on the highest-detail node; <c>ids</c> lists the logical
    /// node indices of progressively lower-detail levels.
    /// See https://github.com/KhronosGroup/glTF/blob/main/extensions/2.0/Vendor/MSFT_lod/README.md.
    /// </summary>
    internal sealed class GltfMsftLod : ExtraProperties
    {
        public const string SchemaName = "MSFT_lod";

        private readonly List<int> ids = [];

        // The factory passes the owning node; the extension itself does not need it.
        public GltfMsftLod(Node node)
        {
            _ = node;
        }

        public IReadOnlyList<int> Ids => ids;

        public void SetIds(IEnumerable<int> values)
        {
            ids.Clear();
            ids.AddRange(values);
        }

        protected override string GetSchemaName() => SchemaName;

        protected override void SerializeProperties(JSONWRITER writer)
        {
            base.SerializeProperties(writer);
            SerializeProperty(writer, "ids", ids);
        }

        protected override void DeserializeProperty(string jsonPropertyName, ref JSONREADER reader)
        {
            switch (jsonPropertyName)
            {
                case "ids": DeserializePropertyList<GltfMsftLod, int>(ref reader, this, ids); break;
                default: base.DeserializeProperty(jsonPropertyName, ref reader); break;
            }
        }
    }
}
