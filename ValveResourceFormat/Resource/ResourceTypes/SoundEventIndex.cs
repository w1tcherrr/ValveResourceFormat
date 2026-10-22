using System.Globalization;
using System.IO;
using System.Linq;
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
    /// that a later file defines again. The others are kept as further definitions. Events name other events in keys
    /// that differ per game, so an event reference is any string value that names a known event, other than the event
    /// itself and the keys that name something else.
    /// </remarks>
    public sealed class SoundEventIndex
    {
        /// <summary>
        /// How many base events the engine visits in total for one event, in all of its base chains, before it does not
        /// play the event, unless the game sets the <c>snd_sos_max_event_base_depth</c> convar.
        /// </summary>
        public const int DefaultMaxBaseVisits = 4;

        // Bases are resolved past the engine's limit for display, this only stops cycles
        private const int MaxResolvedVisits = 64;

        /// <summary>
        /// The sound event file of an addon or workshop map, which the engine loads after the sound event manifest.
        /// </summary>
        public const string AddonEventsFile = "soundevents/soundevents_addon.vsndevts";

        private static readonly KVSerializer KeyValues1Serializer = KVSerializer.Create(KVSerializationFormat.KeyValues1Text);

        private readonly List<string> files = [];
        private readonly Dictionary<string, List<(string File, KVObject Data)>> definitions = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (string File, List<(string Name, KVObject Operator)> Operators)> stacks = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (List<(string Name, KVObject Field, string Stack, string? Block)> Fields, Dictionary<string, int> ByName,
            List<(string Block, string Name, string Stack, int Position)> Collisions)> stackFields
            = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, List<(string Event, string Key)>>? referencedBy;
        private Dictionary<string, List<string>>? soundUsers;

        /// <summary>
        /// Gets how many base events an event may visit before the engine does not play it, which is the game's
        /// <c>snd_sos_max_event_base_depth</c>, such as <see cref="GameFileLoader.SoundEventMaxBaseVisits"/>.
        /// </summary>
        public int MaxBaseVisits { get; init; } = DefaultMaxBaseVisits;

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
        /// <param name="loadFile">Loads a file by its path, or returns null when it is not found.</param>
        /// <param name="eventFiles">Paths of the <c>vsndevts_c</c> files in the order the engine loads them, see
        /// <see cref="GetEventFilesInLoadOrder"/>. A later file wins.</param>
        /// <param name="stackFiles">Paths of the <c>vsndstck_c</c> files.</param>
        /// <param name="maxBaseVisits">The game's <c>snd_sos_max_event_base_depth</c>, see <see cref="MaxBaseVisits"/>.</param>
        /// <param name="logger">Logs the files that fail to load.</param>
        /// <param name="cancellationToken">Cancels the load.</param>
        public static SoundEventIndex Build(Func<string, Resource?> loadFile, IReadOnlyList<string> eventFiles, IReadOnlyList<string> stackFiles,
            int maxBaseVisits = DefaultMaxBaseVisits, ILogger? logger = null, CancellationToken cancellationToken = default)
        {
            var events = new IReadOnlyList<(string Name, KVObject Data)>?[eventFiles.Count];
            var options = new ParallelOptions { CancellationToken = cancellationToken };

            Parallel.For(0, eventFiles.Count, options, i =>
            {
                try
                {
                    using var resource = loadFile(eventFiles[i]);
                    events[i] = (resource?.DataBlock as SoundEventScript)?.Events;
                }
                catch (Exception e)
                {
                    logger?.LogError(e, "Failed to load sound events from {File}", eventFiles[i]);
                }
            });

            var index = new SoundEventIndex { MaxBaseVisits = maxBaseVisits };

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
                    using var resource = loadFile(stackFile);

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
        /// Initializes a new empty instance of the <see cref="SoundEventIndex"/> class.
        /// </summary>
        public SoundEventIndex()
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="SoundEventIndex"/> class with the files, events and stacks of another
        /// index, so files can be added to it without changing the other.
        /// </summary>
        /// <param name="other">The index to copy.</param>
        /// <param name="shadowedFiles">Files of the other index to leave out, such as the ones that files of the same path
        /// added later replace.</param>
        public SoundEventIndex(SoundEventIndex other, IReadOnlySet<string>? shadowedFiles = null)
        {
            files.AddRange(shadowedFiles == null ? other.files : other.files.Where(file => !shadowedFiles.Contains(file)));
            MaxBaseVisits = other.MaxBaseVisits;

            foreach (var (name, list) in other.definitions)
            {
                List<(string File, KVObject Data)> kept = shadowedFiles == null ? [.. list] : [.. list.Where(definition => !shadowedFiles.Contains(definition.File))];

                if (kept.Count > 0)
                {
                    definitions.Add(name, kept);
                }
            }

            foreach (var (name, stack) in other.stacks)
            {
                stacks.Add(name, stack);
            }
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

            stacks.TryAdd(name, (fileName, operators));

            lock (stackFields)
            {
                stackFields.Clear();
            }
        }

        /// <summary>
        /// Lists sound event files in the order the engine loads them: the files of the sound event manifest, including
        /// the manifests it lists, then <see cref="AddonEventsFile"/>, then <paramref name="otherFiles"/> that are not listed yet,
        /// sorted by path, as the engine's order for those is not known.
        /// </summary>
        /// <param name="loadFile">Loads a manifest by its compiled path, or returns null when it is not found.</param>
        /// <param name="otherFiles">Further files to load last, such as every sound event file the loader can find.</param>
        /// <returns>The paths, without the compiled <c>_c</c> suffix. The files may not exist.</returns>
        public static List<string> GetEventFilesInLoadOrder(Func<string, Resource?> loadFile, IEnumerable<string> otherFiles)
        {
            var files = new List<string>();
            var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            CollectManifest("soundevents/soundevents_manifest.vrman");
            Add(AddonEventsFile);

            foreach (var file in otherFiles.Order(StringComparer.OrdinalIgnoreCase))
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

                using var resource = loadFile(manifest + GameFileLoader.CompiledFileSuffix);

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
        /// Gets the file of the resource that a stack was added from, or null when the stack is unknown.
        /// </summary>
        public string? GetStackFile(string name) => stacks.TryGetValue(name, out var stack) ? stack.File : null;

        /// <summary>
        /// Resolves the keys of an event with the keys it inherits through <c>base</c>, checking fragments that name a
        /// field against the fields of the event's stack when that stack was added, and limiting the base visits to
        /// <see cref="MaxBaseVisits"/>. See <see cref="Resolve(KVObject, Func{string, KVObject?}, Func{string, string, string?}, int)"/>.
        /// </summary>
        /// <param name="soundEvent">The event to resolve.</param>
        /// <param name="findEvent">Finds a base event by name, in place of <see cref="Find"/>.</param>
        public (List<(string Key, KVObject Value, string? Source, string? Field)> Keys, List<string> Problems, bool Rejected) Resolve(
            KVObject soundEvent, Func<string, KVObject?>? findEvent = null)
            => Resolve(soundEvent, findEvent ?? Find, GetFieldDefault, MaxBaseVisits);

        /// <summary>
        /// Resolves the keys of an event with the keys it inherits through <c>base</c>, as the engine does.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A base is either the name of an event, or an array of fragments that each name an event in <c>event_name</c>.
        /// The event's own keys win over later fragments, later fragments over earlier ones, and each base's own keys over
        /// the keys of its own bases. A string base gives its <c>type</c> to an event without one. An event with an array
        /// base needs its own type and never takes one from its fragments. A fragment without <c>field_name</c> must have
        /// the event's type. A fragment with <c>field_name</c> must have the type that the <c>public_opvar</c> field of that
        /// name in the event's stack names, and its keys apply to that field's sub-stack. The engine does not play an event
        /// that breaks these rules.
        /// </para>
        /// <para>
        /// The engine also does not play an event whose tree visits more than <paramref name="maxBaseVisits"/> bases in
        /// total, counting only the bases it keeps. Such an event is still resolved in full, and reported as rejected. A
        /// base that is not loaded is skipped.
        /// </para>
        /// </remarks>
        /// <param name="soundEvent">The event to resolve.</param>
        /// <param name="findEvent">Finds a base event by name, or returns null when it is not loaded.</param>
        /// <param name="findFieldDefault">Finds the sub-stack that a <c>public_opvar</c> field of a stack names, given the
        /// stack and the field, or returns null when it cannot tell. Fragments with a <c>field_name</c> are then not checked.</param>
        /// <param name="maxBaseVisits">How many base events the event may visit, the game's <c>snd_sos_max_event_base_depth</c>.</param>
        /// <returns>Each key with its value, the base event it comes from (null for the event's own keys) and the field
        /// whose block it applies to (null for the event's own stack). Also the problems the engine would have with the
        /// event, and whether it does not play it. The <c>base</c> key itself is left out.</returns>
        public static (List<(string Key, KVObject Value, string? Source, string? Field)> Keys, List<string> Problems, bool Rejected) Resolve(
            KVObject soundEvent, Func<string, KVObject?> findEvent, Func<string, string, string?>? findFieldDefault = null, int maxBaseVisits = DefaultMaxBaseVisits)
        {
            var resolved = new List<(string Key, KVObject Value, string? Source, string? Field)>();
            var keys = new HashSet<(string? Field, string Key)>();
            var problems = new List<string>();
            var rejected = false;
            var visits = 0;

            Merge(soundEvent, null, null, inheritType: true);

            if (visits > maxBaseVisits)
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture,
                    $"The engine does not play this event: it visits {visits} base events, more than the {maxBaseVisits} that snd_sos_max_event_base_depth allows."));
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
                        resolved.Add((key, value, source, field));
                    }
                }

                if (!current.TryGetValue("base", out var baseValue))
                {
                    return;
                }

                var who = source ?? "this event";

                if (baseValue.ValueType == KVValueType.String)
                {
                    if (findEvent((string)baseValue) is { } baseEvent && Visit())
                    {
                        Merge(baseEvent, (string)baseValue, field, inheritType);
                    }

                    return;
                }

                var type = current.GetStringProperty("type");

                if (type == null)
                {
                    problems.Add($"The engine does not play this event: {who} has an array base but no type of its own.");
                    rejected = true;
                }

                List<KVObject> fragments = [.. baseValue.Values];

                for (var i = fragments.Count - 1; i >= 0; i--)
                {
                    var name = fragments[i].GetStringProperty("event_name");
                    var fieldName = fragments[i].GetStringProperty("field_name");

                    if (string.IsNullOrEmpty(name) || findEvent(name) is not { } fragment)
                    {
                        continue;
                    }

                    var fragmentType = GetType(fragment, findEvent);
                    var expected = string.IsNullOrEmpty(fieldName) ? type : type != null ? findFieldDefault?.Invoke(type, fieldName) : null;

                    if ((string.IsNullOrEmpty(fieldName) || expected != null) && !string.Equals(fragmentType, expected, StringComparison.OrdinalIgnoreCase))
                    {
                        problems.Add(string.IsNullOrEmpty(fieldName)
                            ? $"The engine does not play this event: base {name} has type {fragmentType ?? "(none)"}, not the type {type ?? "(none)"} of {who}."
                            : $"The engine does not play this event: base {name} has type {fragmentType ?? "(none)"}, but field {fieldName} of the stack {type} takes {expected}.");
                        rejected = true;
                        continue;
                    }

                    if (Visit())
                    {
                        Merge(fragment, name, string.IsNullOrEmpty(fieldName) ? field : fieldName, inheritType: false);
                    }
                }
            }

            bool Visit()
            {
                if (visits >= MaxResolvedVisits)
                {
                    return false;
                }

                visits++;
                return true;
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
            var (fields, byName, _) = GetStackFieldMap(stack);

            return byName.TryGetValue(field, out var i) && fields[i].Field.GetStringProperty("data_type") == "public_opvar"
                ? fields[i].Field.GetStringProperty("value")
                : null;
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
        /// The fields that the sub-stack of a <c>public_opvar</c> field brings in form the block of that field, which the
        /// fragments of an array base with that <c>field_name</c> set. There is one field of each name: a later field of the
        /// same name replaces an earlier one of the same stack, and an imported field whose name, or name without its scope,
        /// is already taken is left out, giving only its <c>data_type</c> to a field that has none. The fields of a sub-stack
        /// whose <c>public_opvar</c> field sets <c>use_public_opvar_scope</c> are named after that field, such as
        /// <c>track_1.track_vsnd_files</c>.
        /// </remarks>
        /// <returns>Each field name, its definition with <c>data_type</c>, the default <c>value</c> and <c>_system_properties</c>,
        /// the stack that declares it, which differs from <paramref name="stackName"/> for imported fields, and the
        /// <c>public_opvar</c> field whose block it is in, null for the stack's own and <c>import_public_opvars</c> fields.
        /// The stack's own fields come first.</returns>
        public IReadOnlyList<(string Name, KVObject Field, string Stack, string? Block)> GetStackFields(string stackName) => GetStackFieldMap(stackName).Fields;

        /// <summary>
        /// Gets the fields of the blocks of a stack that were left out because a field of their name already exists. The
        /// engine places the values of a fragment by their position in the block, so a fragment's values for the fields of
        /// the block after a left out field land one field later.
        /// </summary>
        /// <returns>The block, the name of the left out field, the stack that declares it, and the index in
        /// <see cref="GetStackFields"/> of the first field added after it.</returns>
        public IReadOnlyList<(string Block, string Name, string Stack, int Position)> GetStackFieldCollisions(string stackName) => GetStackFieldMap(stackName).Collisions;

        private (List<(string Name, KVObject Field, string Stack, string? Block)> Fields, Dictionary<string, int> ByName,
            List<(string Block, string Name, string Stack, int Position)> Collisions) GetStackFieldMap(string stackName)
        {
            lock (stackFields)
            {
                if (stackFields.TryGetValue(stackName, out var cached))
                {
                    return cached;
                }
            }

            var fields = new List<(string Name, KVObject Field, string Stack, string? Block)>();
            var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var collisions = new List<(string Block, string Name, string Stack, int Position)>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var path = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            AddFields(stackName, imported: false, prefix: string.Empty, block: null);

            lock (stackFields)
            {
                stackFields[stackName] = (fields, byName, collisions);
            }

            return (fields, byName, collisions);

            void AddFields(string stack, bool imported, string prefix, string? block)
            {
                if (!stacks.TryGetValue(stack, out var stackData) || !visited.Add($"{block}|{prefix}{stack}") || !path.Add(stack))
                {
                    return;
                }

                var imports = new List<(string Stack, string Prefix, string? Block)>();

                foreach (var (opName, op) in stackData.Operators)
                {
                    if (opName != "public")
                    {
                        continue;
                    }

                    foreach (var (fieldName, field) in op.GetSubCollection("operator_variables") ?? KVObject.Collection())
                    {
                        if (fieldName == "import_public_opvars" && field.ValueType == KVValueType.String)
                        {
                            imports.Add(((string)field, prefix, block));
                            continue;
                        }

                        if (field.ValueType != KVValueType.Collection)
                        {
                            continue;
                        }

                        var name = prefix + fieldName;

                        if (!byName.TryGetValue(name, out var existing) && !byName.TryGetValue(fieldName, out existing))
                        {
                            byName.Add(name, fields.Count);
                            fields.Add((name, field, stack, block));
                        }
                        else if (!imported && fields[existing].Stack == stack)
                        {
                            fields[existing] = (name, field, stack, fields[existing].Block);
                        }
                        else
                        {
                            if (block != null && !block.Equals(fields[existing].Block, StringComparison.OrdinalIgnoreCase))
                            {
                                collisions.Add((block, name, stack, fields.Count));
                            }

                            if (imported && !fields[existing].Field.ContainsKey("data_type") && field.TryGetValue("data_type", out var dataType))
                            {
                                var overridden = KVObject.Collection();

                                foreach (var (key, value) in fields[existing].Field)
                                {
                                    overridden.Add(key, value);
                                }

                                overridden.Add("data_type", dataType);
                                fields[existing] = (fields[existing].Name, overridden, fields[existing].Stack, fields[existing].Block);
                            }
                        }

                        if (field.GetStringProperty("data_type") == "public_opvar" && field.GetStringProperty("value") is { Length: > 0 } subStack)
                        {
                            imports.Add((subStack, field.GetBooleanProperty("use_public_opvar_scope") ? $"{name}." : prefix, name));
                        }
                    }
                }

                foreach (var (import, importPrefix, importBlock) in imports)
                {
                    AddFields(import, imported: true, importPrefix, importBlock);
                }

                path.Remove(stack);
            }
        }

        /// <summary>
        /// Builds the reverse references that <see cref="GetReferencedBy"/> and <see cref="GetEventsUsingSound"/> read now,
        /// rather than on their first call.
        /// </summary>
        public void IndexReferences()
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
