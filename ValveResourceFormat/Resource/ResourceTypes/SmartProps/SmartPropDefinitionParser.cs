using System.Linq;
using ValveKeyValue;
using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.ResourceTypes.SmartProps
{
    /// <summary>
    /// Typed accessor over a smart prop KV3 node, passed to every element/operation/criterion
    /// constructor. Bindable fields parse into attribute structs evaluated per run; the literal
    /// helpers are for fields that are never bindable (names, class strings).
    /// </summary>
    internal readonly record struct SmartPropDefinitionParser(KVObject Data)
    {
        public BoolAttribute Bool(string key, bool defaultValue) => BoolAttribute.Parse(Data, key, defaultValue);

        public FloatAttribute Float(string key, float defaultValue = default) => FloatAttribute.Parse(Data, key, defaultValue);

        public StringAttribute String(string key, string? defaultValue = null) => StringAttribute.Parse(Data, key, defaultValue);

        public VectorAttribute Vector(string key, Vector3 defaultValue = default) => VectorAttribute.Parse(Data, key, defaultValue);

        public ExpressionBoolAttribute ExpressionBool(string key, bool defaultValue) => ExpressionBoolAttribute.Parse(Data, key, defaultValue);

        public string? RawString(string key) => Data.GetStringProperty(key);

        public string RawString(string key, string defaultValue) => Data.GetStringProperty(key, defaultValue);

        public bool Boolean(string key, bool defaultValue = default) => Data.GetBooleanProperty(key, defaultValue);

        public long Int64(string key, long defaultValue = default) => Data.GetIntegerProperty(key, defaultValue);

        public double Double(string key, double defaultValue = default) => Data.GetDoubleProperty(key, defaultValue);

        public bool Contains(string key) => Data.ContainsKey(key);

        public SmartPropDefinitionParser[] Array(string key)
        {
            var array = Data.GetArray(key);

            return array == null
                ? []
                : [.. array.Select(item => new SmartPropDefinitionParser(item))];
        }
    }
}
