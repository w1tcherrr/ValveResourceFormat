using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ValveKeyValue;
using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.ResourceTypes
{
    /// <summary>
    /// Represents a sound event script resource (vsndevts).
    /// </summary>
    public class SoundEventScript : KeyValuesOrNTRO
    {
        private static readonly KVSerializer KeyValues1Serializer = KVSerializer.Create(KVSerializationFormat.KeyValues1Text);
        private static readonly KVSerializer KeyValues3Serializer = KVSerializer.Create(KVSerializationFormat.KeyValues3Text);

        private List<(string Name, KVObject Data)>? events;

        /// <summary>
        /// Initializes a new instance of the <see cref="SoundEventScript"/> class.
        /// </summary>
        public SoundEventScript() : base(BlockType.DATA, "VSoundEventScript_t")
        { }

        /// <summary>
        /// Gets the sound events in file order, each as a collection of its keys.
        /// </summary>
        /// <remarks>
        /// Legacy files store each event as text. Events written as KeyValues3 text are parsed as they are.
        /// Events written as KeyValues1 operator stacks are mapped to the modern layout as the engine does: the
        /// <c>reference_stack</c> of <c>update_stack.reference_operator</c> becomes <c>type</c>, and the <c>value</c> of each of
        /// its operator variables becomes a key of that name, a string or an array of strings when the value has sub-keys.
        /// Everything else is dropped.
        /// </remarks>
        public IReadOnlyList<(string Name, KVObject Data)> Events => events ??= ReadEvents();

        private List<(string Name, KVObject Data)> ReadEvents()
        {
            var data = Data;
            var result = new List<(string Name, KVObject Data)>();

            if (data.GetArray("m_SoundEvents") is { } legacyEvents)
            {
                foreach (var legacyEvent in legacyEvents)
                {
                    result.Add((legacyEvent.GetStringProperty("m_SoundName"), ParseLegacyEvent(legacyEvent.GetStringProperty("m_OperatorsKV", string.Empty))));
                }

                return result;
            }

            foreach (var property in data)
            {
                if (property.Value.ValueType == KVValueType.Collection)
                {
                    result.Add((property.Key, property.Value));
                }
            }

            return result;
        }

        private static KVObject ParseLegacyEvent(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return KVObject.Collection();
            }

            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));

            if (text.TrimStart().StartsWith("<!--", StringComparison.Ordinal))
            {
                return KeyValues3Serializer.Deserialize(stream);
            }

            var referenceOperator = KeyValues1Serializer.DeserializeWithoutRoot(stream)
                .GetSubCollection("operator_stacks")?.GetSubCollection("update_stack")?.GetSubCollection("reference_operator");
            var soundEvent = KVObject.Collection();

            if (referenceOperator == null)
            {
                return soundEvent;
            }

            if (referenceOperator.GetStringProperty("reference_stack") is { } referenceStack)
            {
                soundEvent["type"] = referenceStack;
            }

            foreach (var (name, variable) in referenceOperator.GetSubCollection("operator_variables") ?? KVObject.Collection())
            {
                if (!variable.TryGetValue("value", out var value))
                {
                    continue;
                }

                soundEvent[name] = value.ValueType == KVValueType.Collection
                    ? KVObject.Array(value.Values.Select(static item => (KVObject)ToText(item)))
                    : ToText(value);
            }

            return soundEvent;
        }

        private static string ToText(KVObject value) => value.ValueType == KVValueType.String ? (string)value : value.ToString(CultureInfo.InvariantCulture);
    }
}
