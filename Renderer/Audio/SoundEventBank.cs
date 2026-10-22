using ValveKeyValue;
using ValveResourceFormat.IO;
using ValveResourceFormat.ResourceTypes;

namespace ValveResourceFormat.Renderer.Audio;

/// <summary>Stores sound event definitions loaded from soundevent (vsndevts) files.</summary>
/// <param name="maxBaseVisits">How many base events an event may visit before it does not play, the game's
/// <c>snd_sos_max_event_base_depth</c>, such as <see cref="GameFileLoader.SoundEventMaxBaseVisits"/>.</param>
public sealed class SoundEventBank(int maxBaseVisits = SoundEventIndex.DefaultMaxBaseVisits)
{
    // Sound event names are case-insensitive, so lookups here ignore case.
    private readonly Dictionary<string, KVObject> soundEvents = new(capacity: 32768, StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SoundEventDefinition> definitions = new(capacity: 2048, StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> removed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> rejected = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets how many base events an event may visit before it does not play.</summary>
    public int MaxBaseVisits => maxBaseVisits;

    /// <summary>Gets the number of loaded sound event definitions.</summary>
    public int Count => soundEvents.Count;

    /// <summary>Adds a single sound event definition. It replaces an existing definition with the same name, as the engine does.</summary>
    public void AddSoundEvent(string name, KVObject soundEventData)
    {
        if (soundEventData != null)
        {
            soundEvents[name] = soundEventData;
            definitions.Remove(name);
            rejected.Remove(name);
        }
    }

    /// <summary>Drops a sound event, so playing it - or resolving it as another event's child - finds nothing.</summary>
    /// <param name="name">Name of the event to drop (case-insensitive).</param>
    /// <returns><see langword="true"/> if the bank held the event.</returns>
    public bool RemoveSoundEvent(string name)
    {
        removed.Add(name);
        definitions.Remove(name);
        rejected.Remove(name);
        return soundEvents.Remove(name);
    }

    /// <summary>Drops every event this bank was asked to drop from <paramref name="bank"/> as well.</summary>
    internal void CopyRemovalsTo(SoundEventBank bank)
    {
        foreach (var name in removed)
        {
            bank.RemoveSoundEvent(name);
        }
    }

    /// <summary>Adds all sound event definitions from a sound event file.</summary>
    public void AddSoundEvents(IEnumerable<(string Name, KVObject Data)> soundEventsFile)
    {
        foreach (var (name, data) in soundEventsFile)
        {
            AddSoundEvent(name, data);
        }
    }

    /// <summary>
    /// Gets a sound event definition by name (case-insensitive), or null when unknown or when it visits more than
    /// <see cref="MaxBaseVisits"/> bases, which the engine does not play. Resolved and parsed once, then cached.
    /// </summary>
    public SoundEventDefinition? GetSoundEvent(string name)
    {
        if (definitions.TryGetValue(name, out var definition))
        {
            return definition;
        }

        if (rejected.Contains(name) || !soundEvents.TryGetValue(name, out var soundEvent))
        {
            return null;
        }

        if (!soundEvent.ContainsKey("base"))
        {
            definition = new SoundEventDefinition(name, soundEvent);
            definitions.Add(name, definition);
            return definition;
        }

        var (keys, _, isRejected) = SoundEventIndex.Resolve(soundEvent, name => soundEvents.GetValueOrDefault(name), maxBaseVisits: maxBaseVisits);

        if (isRejected)
        {
            rejected.Add(name);
            return null;
        }

        var merged = KVObject.Collection();

        // Keys of a sub-stack can repeat the event's own key names, which come first
        foreach (var (key, value, _, _) in keys)
        {
            merged.TryAdd(key, value);
        }

        definition = new SoundEventDefinition(name, merged);
        definitions.Add(name, definition);
        return definition;
    }
}
