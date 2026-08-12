using System.IO;
using System.Text;
using ValveKeyValue;

namespace ValveResourceFormat.ResourceTypes
{
    /// <summary>
    /// Represents a sound stack script resource.
    /// </summary>
    public class SoundStackScript : Block
    {
        /// <inheritdoc/>
        public override BlockType Type => BlockType.DATA;

        /// <summary>
        /// Gets the sound stack script values, as the KeyValues1 text they are stored as. This is
        /// what <see cref="WriteText"/> emits, so it keeps the authored formatting.
        /// </summary>
        public Dictionary<string, string> SoundStackScriptValue { get; private set; } = [];

        /// <summary>
        /// Gets each stack parsed from its <see cref="SoundStackScriptValue"/> text. A stack whose
        /// text does not parse is left out, so this may hold fewer entries than that dictionary.
        /// </summary>
        public Dictionary<string, KVObject> SoundStacks { get; private set; } = [];

        /// <inheritdoc/>
        public override void Read(BinaryReader reader)
        {
            reader.BaseStream.Position = Offset;

            var version = reader.ReadInt32();

            if (version != 8)
            {
                throw new UnexpectedMagicException("Unknown version", version, nameof(version));
            }

            var count = reader.ReadInt32();
            var offset = reader.BaseStream.Position;

            for (var i = 0; i < count; i++)
            {
                var offsetToName = offset + reader.ReadInt32();
                offset += 4;
                var offsetToValue = offset + reader.ReadInt32();
                offset += 4;

                reader.BaseStream.Position = offsetToName;
                var name = reader.ReadNullTermString(Encoding.UTF8);

                reader.BaseStream.Position = offsetToValue;
                var value = reader.ReadNullTermString(Encoding.UTF8);

                reader.BaseStream.Position = offset;

                // Valve have duplicates, assume last is correct?
                SoundStackScriptValue.Remove(name);
                SoundStacks.Remove(name);

                SoundStackScriptValue.Add(name, value);

                if (Parse(value) is { } stack)
                {
                    SoundStacks.Add(name, stack);
                }
            }
        }

        /// <summary>
        /// Reads one stack's KeyValues1 text, or null when it does not parse. These are authored
        /// files, so a malformed one is skipped rather than allowed to fail the whole resource.
        /// </summary>
        private static KVObject? Parse(string text)
        {
            try
            {
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
                return KVSerializer.Create(KVSerializationFormat.KeyValues1Text).Deserialize(stream);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <inheritdoc/>
        public override void Serialize(Stream stream)
        {
            throw new NotImplementedException("Serializing this block is not yet supported. If you need this, send us a pull request!");
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Writes each sound stack script entry with its name as a comment.
        /// </remarks>
        public override void WriteText(IndentedTextWriter writer)
        {
            foreach (var entry in SoundStackScriptValue)
            {
                writer.WriteLine($"// {entry.Key}");
                writer.Write(entry.Value);
                writer.WriteLine(string.Empty);
            }
        }
    }
}
