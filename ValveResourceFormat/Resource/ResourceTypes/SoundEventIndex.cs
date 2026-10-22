using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
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
    /// When several files define an event of the same name, the file added last wins, as the engine replaces an event
    /// that a later file defines again. The others are kept as further definitions. Events name other events in keys that differ per game, so an event reference is any
    /// string value that names a known event, other than the event itself and the keys that name something else.
    /// </remarks>
    public sealed class SoundEventIndex
    {
        /// <summary>
        /// How many base events the engine visits in total for one event, in all of its base chains, before it rejects
        /// the event. This is the default of the <c>snd_sos_max_event_base_depth</c> convar.
        /// </summary>
        public const int MaxBaseVisits = 4;

        // Bases are resolved past the engine's limit for display, this only stops cycles
        private const int MaxResolvedVisits = 64;

        /// <summary>
        /// The sound event file of an addon or workshop map, which the engine loads after the sound event manifest.
        /// </summary>
        public const string AddonEventsFile = "soundevents/soundevents_addon.vsndevts";

        private static readonly KVSerializer KeyValues1Serializer = KVSerializer.Create(KVSerializationFormat.KeyValues1Text);

        private readonly List<string> files = [];
        private readonly Dictionary<string, List<(string File, KVObject Data)>> definitions = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (string File, KVObject Root, List<(string Name, KVObject Operator)> Operators)> stacks = new(StringComparer.OrdinalIgnoreCase);
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
        /// A file that fails to load is logged and left out.
        /// </summary>
        /// <param name="fileLoader">Loads each file by its path.</param>
        /// <param name="eventFiles">Paths of the <c>vsndevts_c</c> files in the order the engine loads them, see
        /// <see cref="GetEventFilesInLoadOrder"/>. A later file wins.</param>
        /// <param name="stackFiles">Paths of the <c>vsndstck_c</c> files.</param>
        /// <param name="logger">Logs the files that fail to load.</param>
        /// <param name="cancellationToken">Cancels the load.</param>
        public static SoundEventIndex Build(IFileLoader fileLoader, IReadOnlyList<string> eventFiles, IReadOnlyList<string> stackFiles,
            ILogger? logger = null, CancellationToken cancellationToken = default)
        {
            var events = new IReadOnlyList<(string Name, KVObject Data)>?[eventFiles.Count];
            var options = new ParallelOptions { CancellationToken = cancellationToken };

            Parallel.For(0, eventFiles.Count, options, i =>
            {
                try
                {
                    using var resource = fileLoader.LoadFile(eventFiles[i]);
                    events[i] = (resource?.DataBlock as SoundEventScript)?.Events;
                }
                catch (Exception e)
                {
                    logger?.LogError(e, "Failed to load sound events from {File}", eventFiles[i]);
                }
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

                try
                {
                    using var resource = fileLoader.LoadFile(stackFile);

                    if (resource != null)
                    {
                        index.AddStacks(resource);
                    }
                }
                catch (Exception e)
                {
                    logger?.LogError(e, "Failed to load sound stacks from {File}", stackFile);
                }
            }

            index.IndexReferences();

            return index;
        }

        /// <summary>
        /// Adds the events of a sound event file. Its events win over events of the same name added before.
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
        /// </summary>
        public void AddStacks(Resource resource)
        {
            var fileName = resource.FileName ?? string.Empty;

            switch (resource.DataBlock)
            {
                case SoundStackScript script:
                    foreach (var text in script.SoundStackScriptValue.Values)
                    {
                        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));

                        foreach (var (name, stack) in KeyValues1Serializer.DeserializeWithoutRoot(stream))
                        {
                            AddStack(fileName, name, stack);
                        }
                    }

                    break;

                case BinaryKV3 kv3:
                    foreach (var (name, stack) in kv3.Data.Root)
                    {
                        AddStack(fileName, name, stack);
                    }

                    break;
            }
        }

        /// <summary>
        /// Adds a stack. Stacks already added stay first.
        /// </summary>
        /// <param name="fileName">The path of the file that holds the stack.</param>
        /// <param name="name">The name of the stack, which event types name.</param>
        /// <param name="stack">The stack, either with an <c>operators</c> array of operators that each have a <c>name</c>, as
        /// KeyValues3 stacks store it, or with each operator under its name, as KeyValues1 stacks store it.</param>
        public void AddStack(string fileName, string name, KVObject stack)
        {
            var operators = new List<(string Name, KVObject Operator)>();

            if (stack.GetArray("operators") is { } operatorArray)
            {
                foreach (var op in operatorArray)
                {
                    operators.Add((op.GetStringProperty("name", string.Empty), op));
                }
            }
            else
            {
                foreach (var (opName, op) in stack)
                {
                    if (op.ValueType == KVValueType.Collection && op.ContainsKey("operator"))
                    {
                        operators.Add((opName, op));
                    }
                }
            }

            stacks.TryAdd(name, (fileName, stack, operators));
        }

        /// <summary>
        /// Lists sound event files in the order the engine loads them: the files of the sound event manifest, including
        /// the manifests it lists, then <see cref="AddonEventsFile"/>, then <paramref name="otherFiles"/> that are not listed yet.
        /// </summary>
        /// <param name="fileLoader">Loads the manifests.</param>
        /// <param name="otherFiles">Further files to load last, such as every sound event file the loader can find.</param>
        /// <returns>The paths, without the compiled <c>_c</c> suffix. The files may not exist.</returns>
        public static List<string> GetEventFilesInLoadOrder(IFileLoader fileLoader, IEnumerable<string> otherFiles)
        {
            var files = new List<string>();
            var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            CollectManifest("soundevents/soundevents_manifest.vrman");
            Add(AddonEventsFile);

            foreach (var file in otherFiles)
            {
                Add(file);
            }

            return files;

            void Add(string file)
            {
                if (listed.Add(file))
                {
                    files.Add(file);
                }
            }

            void CollectManifest(string manifest)
            {
                if (!visited.Add(manifest))
                {
                    return;
                }

                using var resource = fileLoader.LoadFileCompiled(manifest);

                if (resource?.DataBlock is not ResourceManifest resourceManifest)
                {
                    return;
                }

                foreach (var resourceGroup in resourceManifest.Resources)
                {
                    foreach (var file in resourceGroup)
                    {
                        if (file.EndsWith(".vrman", StringComparison.OrdinalIgnoreCase))
                        {
                            CollectManifest(file);
                        }
                        else
                        {
                            Add(file);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Gets every definition of an event in the order they were added, so the last one wins, or an empty list when
        /// the event is unknown.
        /// </summary>
        public IReadOnlyList<(string File, KVObject Data)> GetDefinitions(string name)
            => definitions.TryGetValue(name, out var list) ? list : [];

        /// <summary>
        /// Gets the winning definition of an event, or null when the event is unknown.
        /// </summary>
        public KVObject? Find(string name)
            => definitions.TryGetValue(name, out var list) ? list[^1].Data : null;

        /// <summary>
        /// Gets whether a stack of this name was added.
        /// </summary>
        public bool ContainsStack(string name) => stacks.ContainsKey(name);

        /// <summary>
        /// Gets the file of the resource that a stack was added from, or null when the stack is unknown.
        /// </summary>
        public string? GetStackFile(string name) => stacks.TryGetValue(name, out var stack) ? stack.File : null;

        /// <summary>
        /// Resolves the keys of an event with the keys it inherits through <c>base</c>, checking fragments that name a
        /// field against the fields of the event's stack when that stack was added. See <see cref="Resolve(KVObject, Func{string, KVObject?}, Func{string, string, string?})"/>.
        /// </summary>
        /// <param name="soundEvent">The event to resolve.</param>
        /// <param name="findEvent">Finds a base event by name, in place of <see cref="Find"/>.</param>
        public (List<(string Key, KVObject Value, string? Source)> Keys, List<string> Problems, bool Rejected) Resolve(KVObject soundEvent, Func<string, KVObject?>? findEvent = null)
            => Resolve(soundEvent, findEvent ?? Find, soundEvent.GetStringProperty("type") is { } type && ContainsStack(type) ? GetFieldDefault : null);

        /// <summary>
        /// Resolves the keys of an event with the keys it inherits through <c>base</c>, as the engine does.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A base is either the name of an event, or an array of fragments that each name an event in <c>event_name</c>.
        /// The event's own keys win over later fragments, later fragments over earlier ones, and each base's own keys over
        /// the keys of its own bases. A string base gives its <c>type</c> to an event without one. An event with an array
        /// base needs its own type and never takes one from its fragments. A fragment without <c>field_name</c> must have
        /// the event's type, otherwise it is skipped. A fragment with <c>field_name</c> must have the type that the field of
        /// that name in the event's stack defaults to, and its keys apply to that field's sub-stack.
        /// </para>
        /// <para>
        /// The engine rejects an event whose tree visits more than <see cref="MaxBaseVisits"/> bases in total, by default.
        /// Such an event is still resolved in full, and only reported as rejected. A base that is not loaded is skipped.
        /// </para>
        /// </remarks>
        /// <param name="soundEvent">The event to resolve.</param>
        /// <param name="findEvent">Finds a base event by name, or returns null when it is not loaded.</param>
        /// <param name="findFieldDefault">Finds the default of a field of a stack, given the stack and the field. Without it,
        /// fragments with a <c>field_name</c> are not checked.</param>
        /// <returns>Each key with its value and where it comes from: null for the event's own keys, otherwise the base event,
        /// followed by " via " and the field for keys of a sub-stack. Also the problems the engine would have with the event,
        /// and whether it rejects the event. The <c>base</c> key itself is left out.</returns>
        public static (List<(string Key, KVObject Value, string? Source)> Keys, List<string> Problems, bool Rejected) Resolve(
            KVObject soundEvent, Func<string, KVObject?> findEvent, Func<string, string, string?>? findFieldDefault = null)
        {
            var resolved = new List<(string Key, KVObject Value, string? Source)>();
            var keys = new HashSet<(string? Field, string Key)>();
            var problems = new List<string>();
            var rejected = false;
            var visits = 0;

            Merge(soundEvent, null, null, inheritType: true);

            if (visits > MaxBaseVisits)
            {
                problems.Add($"The engine rejects this event: it visits more than {MaxBaseVisits} base events, the default of snd_sos_max_event_base_depth.");
                rejected = true;
            }

            return (resolved, problems, rejected);

            void Merge(KVObject current, string? source, string? field, bool inheritType)
            {
                foreach (var (key, value) in current)
                {
                    if (key.Equals("base", StringComparison.OrdinalIgnoreCase)
                        || (!inheritType && key.Equals("type", StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    if (keys.Add((field, key.ToLowerInvariant())))
                    {
                        resolved.Add((key, value, source == null ? null : field == null ? source : $"{source} via {field}"));
                    }
                }

                if (!current.TryGetValue("base", out var baseValue))
                {
                    return;
                }

                var who = source ?? "this event";

                if (baseValue.ValueType == KVValueType.String)
                {
                    if (Visit((string)baseValue) is { } baseEvent)
                    {
                        Merge(baseEvent, (string)baseValue, field, inheritType);
                    }

                    return;
                }

                var type = current.GetStringProperty("type");

                if (type == null)
                {
                    problems.Add($"The engine rejects this event: {who} has an array base but no type of its own.");
                    rejected = true;
                }

                List<KVObject> fragments = [.. baseValue.Values];

                for (var i = fragments.Count - 1; i >= 0; i--)
                {
                    var name = fragments[i].GetStringProperty("event_name");
                    var fieldName = fragments[i].GetStringProperty("field_name");

                    if (string.IsNullOrEmpty(name) || Visit(name) is not { } fragment)
                    {
                        continue;
                    }

                    var fragmentType = GetType(fragment, findEvent);

                    if (string.IsNullOrEmpty(fieldName))
                    {
                        if (!string.Equals(fragmentType, type, StringComparison.OrdinalIgnoreCase))
                        {
                            problems.Add($"The engine skips base {name}: its type {fragmentType ?? "(none)"} is not the type {type ?? "(none)"} of {who}.");
                            continue;
                        }
                    }
                    else if (findFieldDefault != null && type != null
                        && findFieldDefault(type, fieldName) is var expected && !string.Equals(fragmentType, expected, StringComparison.OrdinalIgnoreCase))
                    {
                        problems.Add(expected == null
                            ? $"The engine skips base {name}: the stack {type} has no field {fieldName}."
                            : $"The engine skips base {name}: field {fieldName} of the stack {type} takes type {expected}, but the base has type {fragmentType ?? "(none)"}.");
                        continue;
                    }

                    Merge(fragment, name, string.IsNullOrEmpty(fieldName) ? field : fieldName, inheritType: false);
                }
            }

            KVObject? Visit(string name)
            {
                if (visits >= MaxResolvedVisits || findEvent(name) is not { } baseEvent)
                {
                    return null;
                }

                visits++;
                return baseEvent;
            }
        }

        private static string? GetType(KVObject soundEvent, Func<string, KVObject?> findEvent)
        {
            for (var i = 0; i < MaxResolvedVisits; i++)
            {
                if (soundEvent.GetStringProperty("type") is { } type)
                {
                    return type;
                }

                if (soundEvent.GetStringProperty("base") is not { } baseName || findEvent(baseName) is not { } baseEvent)
                {
                    return null;
                }

                soundEvent = baseEvent;
            }

            return null;
        }

        private string? GetFieldDefault(string stack, string field)
        {
            foreach (var (name, definition, _) in GetStackFields(stack))
            {
                if (name.Equals(field, StringComparison.OrdinalIgnoreCase))
                {
                    return definition.GetStringProperty("value");
                }
            }

            return null;
        }

        /// <summary>
        /// Gets the events that an event names in its <c>base</c>, which may not be loaded.
        /// </summary>
        /// <returns>Each base name, and the field of the event's stack that a fragment of an array base applies to.</returns>
        public static List<(string Name, string? Field)> GetBases(KVObject soundEvent)
        {
            if (!soundEvent.TryGetValue("base", out var baseValue))
            {
                return [];
            }

            if (baseValue.ValueType == KVValueType.String)
            {
                return [((string)baseValue, null)];
            }

            var bases = new List<(string Name, string? Field)>();

            foreach (var fragment in baseValue.Values)
            {
                if (fragment.GetStringProperty("event_name") is { Length: > 0 } name)
                {
                    bases.Add((name, fragment.GetStringProperty("field_name")));
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
        /// Gets the public fields of a stack, which are the keys an event of that type sets, including the fields that it
        /// imports from other stacks with <c>import_public_opvars</c>, and the fields of the sub-stacks that its
        /// <c>public_opvar</c> fields name.
        /// </summary>
        /// <remarks>
        /// A later field of the same name replaces an earlier one. An imported field does not replace a field that already
        /// exists, but gives its <c>data_type</c> to one that has none. Fields marked <c>ignore_import</c> in <c>_stack_properties.&lt;stack&gt;._field_properties</c> are not imported.
        /// </remarks>
        /// <returns>Each field name, its definition with <c>data_type</c>, the default <c>value</c> and <c>_system_properties</c>,
        /// and the stack that declares it, which differs from <paramref name="stackName"/> for imported fields. The stack's
        /// own fields come first.</returns>
        public List<(string Name, KVObject Field, string Stack)> GetStackFields(string stackName)
        {
            var fields = new List<(string Name, KVObject Field, string Stack)>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            AddFields(stackName, imported: false);

            return fields;

            void AddFields(string stack, bool imported)
            {
                if (!visited.Add(stack) || !stacks.TryGetValue(stack, out var stackData))
                {
                    return;
                }

                var ignored = stacks.GetValueOrDefault("_stack_properties").Root?.GetSubCollection(stack)?.GetSubCollection("_field_properties");
                var imports = new List<string>();

                foreach (var (opName, op) in stackData.Operators)
                {
                    if (opName != "public")
                    {
                        continue;
                    }

                    foreach (var (name, field) in op.GetSubCollection("operator_variables") ?? KVObject.Collection())
                    {
                        if (name == "import_public_opvars" && field.ValueType == KVValueType.String)
                        {
                            imports.Add((string)field);
                            continue;
                        }

                        if (field.ValueType != KVValueType.Collection
                            || (imported && ignored?.GetSubCollection(name)?.GetBooleanProperty("ignore_import") == true))
                        {
                            continue;
                        }

                        var existing = fields.FindIndex(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

                        if (existing < 0)
                        {
                            fields.Add((name, field, stack));
                        }
                        else if (!imported && fields[existing].Stack == stack)
                        {
                            fields[existing] = (name, field, stack);
                        }
                        else if (imported && !fields[existing].Field.ContainsKey("data_type") && field.TryGetValue("data_type", out var dataType))
                        {
                            var overridden = KVObject.Collection();

                            foreach (var (key, value) in fields[existing].Field)
                            {
                                overridden.Add(key, value);
                            }

                            overridden.Add("data_type", dataType);
                            fields[existing] = (name, overridden, fields[existing].Stack);
                        }

                        if (field.GetStringProperty("data_type") == "public_opvar" && field.GetStringProperty("value") is { Length: > 0 } subStack)
                        {
                            imports.Add(subStack);
                        }
                    }
                }

                foreach (var import in imports)
                {
                    AddFields(import, imported: true);
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
