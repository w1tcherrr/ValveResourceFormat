using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ValveKeyValue;
using ValveResourceFormat.IO;
using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.ResourceTypes
{
    /// <summary>
    /// Indexes the sound events and sound stacks of a game by name (ignoring case), resolves the keys events
    /// inherit through <c>base</c>, and finds the events and sounds that events reference.
    /// </summary>
    /// <remarks>
    /// When several files define an event of the same name, the file added first wins and the others are kept as
    /// further definitions. Events name other events in keys that differ per game, so an event reference is any
    /// string value that names a known event, other than the event itself and the keys that name something else.
    /// </remarks>
    public sealed class SoundEventIndex
    {
        /// <summary>
        /// How deep in a chain of <c>base</c> events an event can be and still have its own bases followed.
        /// </summary>
        public const int MaxBaseDepth = 8;

        private static readonly KVSerializer KeyValues1Serializer = KVSerializer.Create(KVSerializationFormat.KeyValues1Text);

        private readonly List<string> files = [];
        private readonly Dictionary<string, List<(string File, KVObject Data)>> definitions = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (string File, List<(string Name, KVObject Operator)> Operators)> stacks = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, List<(string Event, string Key)>>? referencedBy;
        private Dictionary<string, List<string>>? soundUsers;

        /// <summary>
        /// Gets the sound event files in the order they were added.
        /// </summary>
        public IReadOnlyList<string> Files => files;

        /// <summary>
        /// Gets the names of every indexed event.
        /// </summary>
        public IEnumerable<string> EventNames => definitions.Keys;

        /// <summary>
        /// Gets the number of distinct event names.
        /// </summary>
        public int Count => definitions.Count;

        /// <summary>
        /// Loads sound event files and sound stack files into a new index. The files are loaded in parallel and added in order.
        /// </summary>
        /// <param name="fileLoader">Loads each file by its path.</param>
        /// <param name="eventFiles">Paths of the <c>vsndevts_c</c> files, in the order their events take precedence.</param>
        /// <param name="stackFiles">Paths of the <c>vsndstck_c</c> files.</param>
        /// <param name="cancellationToken">Cancels the load.</param>
        public static SoundEventIndex Build(IFileLoader fileLoader, IReadOnlyList<string> eventFiles, IReadOnlyList<string> stackFiles, CancellationToken cancellationToken = default)
        {
            var events = new IReadOnlyList<(string Name, KVObject Data)>?[eventFiles.Count];
            var options = new ParallelOptions { CancellationToken = cancellationToken };

            Parallel.For(0, eventFiles.Count, options, i =>
            {
                using var resource = fileLoader.LoadFile(eventFiles[i]);
                events[i] = (resource?.DataBlock as SoundEventScript)?.Events;
            });

            var index = new SoundEventIndex();

            for (var i = 0; i < eventFiles.Count; i++)
            {
                if (events[i] is { } fileEvents)
                {
                    index.AddEvents(eventFiles[i], fileEvents);
                }
            }

            foreach (var stackFile in stackFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var resource = fileLoader.LoadFile(stackFile);

                if (resource != null)
                {
                    index.AddStacks(resource);
                }
            }

            index.IndexReferences();

            return index;
        }

        /// <summary>
        /// Adds the events of a sound event file. Events already defined by an earlier file stay first.
        /// </summary>
        /// <param name="fileName">The path of the file, which definitions report as their file.</param>
        /// <param name="events">The events of the file, such as <see cref="SoundEventScript.Events"/>.</param>
        public void AddEvents(string fileName, IEnumerable<(string Name, KVObject Data)> events)
        {
            files.Add(fileName);

            foreach (var (name, data) in events)
            {
                if (!definitions.TryGetValue(name, out var list))
                {
                    list = [];
                    definitions.Add(name, list);
                }

                list.Add((fileName, data));
            }

            referencedBy = null;
            soundUsers = null;
        }

        /// <summary>
        /// Adds the stacks of a sound stack resource, in either the KeyValues1 text or the KeyValues3 layout.
        /// Stacks already added stay first.
        /// </summary>
        public void AddStacks(Resource resource)
        {
            switch (resource.DataBlock)
            {
                case SoundStackScript script:
                    foreach (var text in script.SoundStackScriptValue.Values)
                    {
                        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));

                        foreach (var stack in KeyValues1Serializer.DeserializeWithoutRoot(stream))
                        {
                            var operators = new List<(string Name, KVObject Operator)>();

                            foreach (var op in stack.Value)
                            {
                                if (op.Value.ValueType == KVValueType.Collection && op.Value.ContainsKey("operator"))
                                {
                                    operators.Add((op.Key, op.Value));
                                }
                            }

                            stacks.TryAdd(stack.Key, (resource.FileName ?? string.Empty, operators));
                        }
                    }

                    break;

                case BinaryKV3 kv3:
                    foreach (var stack in kv3.Data.Root)
                    {
                        var operators = new List<(string Name, KVObject Operator)>();

                        foreach (var op in stack.Value.GetArray("operators") ?? [])
                        {
                            operators.Add((op.GetStringProperty("name", string.Empty), op));
                        }

                        stacks.TryAdd(stack.Key, (resource.FileName ?? string.Empty, operators));
                    }

                    break;
            }
        }

        /// <summary>
        /// Gets every definition of an event, the one that wins first, or an empty list when the event is unknown.
        /// </summary>
        public IReadOnlyList<(string File, KVObject Data)> GetDefinitions(string name)
            => definitions.TryGetValue(name, out var list) ? list : [];

        /// <summary>
        /// Gets the winning definition of an event, or null when the event is unknown.
        /// </summary>
        public KVObject? Find(string name)
            => definitions.TryGetValue(name, out var list) ? list[0].Data : null;

        /// <summary>
        /// Gets whether a stack of this name was added.
        /// </summary>
        public bool ContainsStack(string name) => stacks.ContainsKey(name);

        /// <summary>
        /// Gets the file of the resource that a stack was added from, or null when the stack is unknown.
        /// </summary>
        public string? GetStackFile(string name) => stacks.TryGetValue(name, out var stack) ? stack.File : null;

        /// <summary>
        /// Resolves the keys of an event with the keys it inherits through <c>base</c>, see <see cref="Resolve(KVObject, Func{string, KVObject?})"/>.
        /// </summary>
        public List<(string Key, KVObject Value, string? Source)> Resolve(string name)
            => Find(name) is { } soundEvent ? Resolve(soundEvent, Find) : [];

        /// <summary>
        /// Resolves the keys of an event with the keys it inherits through <c>base</c>.
        /// </summary>
        /// <remarks>
        /// A base is either the name of an event, or an array of fragments that each name an event in <c>event_name</c>.
        /// The event's own keys win, then the keys of each base in order, each with the keys of its own bases.
        /// The <c>type</c> of a fragment is not inherited. Bases are not followed past <see cref="MaxBaseDepth"/>.
        /// </remarks>
        /// <param name="soundEvent">The event to resolve.</param>
        /// <param name="findEvent">Finds a base event by name, or returns null when it is unknown.</param>
        /// <returns>Each key with its value and the name of the event it comes from, which is null for the event's own keys.
        /// The <c>base</c> key itself is left out.</returns>
        public static List<(string Key, KVObject Value, string? Source)> Resolve(KVObject soundEvent, Func<string, KVObject?> findEvent)
        {
            var resolved = new List<(string Key, KVObject Value, string? Source)>();
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            Merge(soundEvent, null, inheritType: true, depth: 0);

            return resolved;

            void Merge(KVObject current, string? source, bool inheritType, int depth)
            {
                foreach (var (key, value) in current)
                {
                    if (key.Equals("base", StringComparison.OrdinalIgnoreCase)
                        || (!inheritType && key.Equals("type", StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    if (keys.Add(key))
                    {
                        resolved.Add((key, value, source));
                    }
                }

                if (depth > MaxBaseDepth)
                {
                    return;
                }

                foreach (var (baseName, isFragment) in GetBases(current))
                {
                    if (findEvent(baseName) is { } baseEvent)
                    {
                        Merge(baseEvent, baseName, inheritType && !isFragment, depth + 1);
                    }
                }
            }
        }

        /// <summary>
        /// Gets the events that an event names in its <c>base</c>, which may not exist.
        /// </summary>
        /// <returns>Each base name, and whether it is a fragment of an array base.</returns>
        public static List<(string Name, bool IsFragment)> GetBases(KVObject soundEvent)
        {
            if (!soundEvent.TryGetValue("base", out var baseValue))
            {
                return [];
            }

            if (baseValue.ValueType == KVValueType.String)
            {
                return [((string)baseValue, false)];
            }

            var bases = new List<(string Name, bool IsFragment)>();

            foreach (var fragment in baseValue.Values)
            {
                if (fragment.GetStringProperty("event_name") is { Length: > 0 } name)
                {
                    bases.Add((name, true));
                }
            }

            return bases;
        }

        /// <summary>
        /// Gets the known events that an event names in its own keys, with the top level key that names each.
        /// Bases are included under the <c>base</c> key whether or not they exist.
        /// </summary>
        /// <param name="name">The name of the event, which is not a reference to itself.</param>
        /// <param name="soundEvent">The keys of the event.</param>
        public List<(string Key, string Event)> GetEventReferences(string name, KVObject soundEvent)
        {
            var references = new List<(string Key, string Event)>();
            CollectReferences(name, soundEvent, references, sounds: null);
            return references;
        }

        /// <summary>
        /// Gets the sounds (<c>.vsnd</c> paths) that an event names in its own keys, with the top level key that names each.
        /// </summary>
        public List<(string Key, string Sound)> GetSoundReferences(KVObject soundEvent)
        {
            var sounds = new List<(string Key, string Sound)>();
            CollectReferences(null, soundEvent, events: null, sounds);
            return sounds;
        }

        /// <summary>
        /// Gets the events whose own keys name an event, with the key that names it, including events that base on it.
        /// </summary>
        public IReadOnlyList<(string Event, string Key)> GetReferencedBy(string name)
        {
            if (referencedBy == null)
            {
                IndexReferences();
            }

            return referencedBy!.TryGetValue(name, out var list) ? list : [];
        }

        /// <summary>
        /// Gets the events whose own keys name a sound.
        /// </summary>
        /// <param name="sound">The path of the sound, with or without the compiled <c>_c</c> suffix.</param>
        public IReadOnlyList<string> GetEventsUsingSound(string sound)
        {
            if (soundUsers == null)
            {
                IndexReferences();
            }

            return soundUsers!.TryGetValue(NormalizeSoundPath(sound), out var list) ? list : [];
        }

        /// <summary>
        /// Gets the public fields of a stack, which are the keys an event of that type sets, including the fields of the
        /// stacks it imports with <c>import_public_opvars</c> or auto connected <c>sos_import_stack</c> operators.
        /// </summary>
        /// <returns>Each field name, its definition with <c>data_type</c>, the default <c>value</c> and <c>_system_properties</c>,
        /// and the stack that declares it. The stack's own fields come first.</returns>
        public List<(string Name, KVObject Field, string Stack)> GetStackFields(string stackName)
        {
            var fields = new List<(string Name, KVObject Field, string Stack)>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            AddFields(stackName);

            return fields;

            void AddFields(string stack)
            {
                if (!visited.Add(stack) || !stacks.TryGetValue(stack, out var stackData))
                {
                    return;
                }

                var imports = new List<string>();

                foreach (var (opName, op) in stackData.Operators)
                {
                    if (opName == "public")
                    {
                        foreach (var (name, field) in op.GetSubCollection("operator_variables") ?? KVObject.Collection())
                        {
                            if (name == "import_public_opvars" && field.ValueType == KVValueType.String)
                            {
                                imports.Add((string)field);
                            }
                            else if (field.ValueType == KVValueType.Collection && names.Add(name))
                            {
                                fields.Add((name, field, stack));
                            }
                        }
                    }
                    else if (op.GetStringProperty("operator") == "sos_import_stack" && op.GetBooleanProperty("auto_connect_public_fields")
                        && op.GetStringProperty("import_stack") is { } importStack)
                    {
                        imports.Add(importStack);
                    }
                }

                foreach (var import in imports)
                {
                    AddFields(import);
                }
            }
        }

        private void IndexReferences()
        {
            var events = new List<(string Key, string Event)>();
            var sounds = new List<(string Key, string Sound)>();
            var seenEvents = new HashSet<(string Key, string Event)>();
            var seenSounds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            referencedBy = new(StringComparer.OrdinalIgnoreCase);
            soundUsers = new(StringComparer.OrdinalIgnoreCase);

            foreach (var (name, list) in definitions)
            {
                events.Clear();
                sounds.Clear();
                seenEvents.Clear();
                seenSounds.Clear();

                foreach (var (_, data) in list)
                {
                    CollectReferences(name, data, events, sounds);
                }

                foreach (var (key, target) in events)
                {
                    if (!seenEvents.Add((key, target.ToLowerInvariant())))
                    {
                        continue;
                    }

                    if (!referencedBy.TryGetValue(target, out var users))
                    {
                        users = [];
                        referencedBy.Add(target, users);
                    }

                    users.Add((name, key));
                }

                foreach (var (_, sound) in sounds)
                {
                    var path = NormalizeSoundPath(sound);

                    if (!seenSounds.Add(path))
                    {
                        continue;
                    }

                    if (!soundUsers.TryGetValue(path, out var users))
                    {
                        users = [];
                        soundUsers.Add(path, users);
                    }

                    users.Add(name);
                }
            }
        }

        private void CollectReferences(string? name, KVObject soundEvent, List<(string Key, string Event)>? events, List<(string Key, string Sound)>? sounds)
        {
            foreach (var (key, value) in soundEvent)
            {
                if (key.Equals("base", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var (baseName, _) in GetBases(soundEvent))
                    {
                        events?.Add((key, baseName));
                    }

                    continue;
                }

                // These name a stack, a DSP preset or nothing at all, and can happen to match an event name
                if (key is "type" or "dsp_preset" or "metadata" or "_system_properties")
                {
                    continue;
                }

                CollectValue(key, value);
            }

            void CollectValue(string key, KVObject value)
            {
                if (value.ValueType is KVValueType.Collection or KVValueType.Array)
                {
                    foreach (var child in value.Values)
                    {
                        CollectValue(key, child);
                    }

                    return;
                }

                if (value.ValueType != KVValueType.String || (string)value is not { Length: > 0 } text)
                {
                    return;
                }

                if (text.EndsWith(".vsnd", StringComparison.OrdinalIgnoreCase))
                {
                    sounds?.Add((key, text));
                }
                else if (events != null && !text.Equals(name, StringComparison.OrdinalIgnoreCase) && definitions.ContainsKey(text))
                {
                    events.Add((key, text));
                }
            }
        }

        private static string NormalizeSoundPath(string sound)
        {
            sound = sound.Replace('\\', '/');

            return sound.EndsWith(GameFileLoader.CompiledFileSuffix, StringComparison.Ordinal) ? sound[..^GameFileLoader.CompiledFileSuffix.Length] : sound;
        }
    }
}
