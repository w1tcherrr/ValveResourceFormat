using ValveKeyValue;
using ValveResourceFormat.ResourceTypes;

namespace ValveResourceFormat.Renderer.Audio;

/// <summary>Stores sound event definitions loaded from soundevent (vsndevts) files.</summary>
public sealed class SoundEventBank
{
    // Sound event names are case-insensitive, so lookups here ignore case.
    private readonly Dictionary<string, KVObject> soundEvents = new(capacity: 32768, StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SoundEventDefinition> definitions = new(capacity: 2048, StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> removed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets the number of loaded sound event definitions.</summary>
    public int Count => soundEvents.Count;

    /// <summary>Adds a single sound event definition. An existing definition with the same name is kept.</summary>
    public void AddSoundEvent(string name, KVObject soundEventData)
    {
        if (soundEventData != null)
        {
            soundEvents.TryAdd(name, soundEventData);
        }
    }

    /// <summary>Drops a sound event, so playing it - or resolving it as another event's child - finds nothing.</summary>
    /// <param name="name">Name of the event to drop (case-insensitive).</param>
    /// <returns><see langword="true"/> if the bank held the event.</returns>
    public bool RemoveSoundEvent(string name)
    {
        removed.Add(name);
        definitions.Remove(name);
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

    /// <summary>Gets a sound event definition by name (case-insensitive), or null when unknown. Resolved and parsed once, then cached.</summary>
    public SoundEventDefinition? GetSoundEvent(string name)
    {
        if (definitions.TryGetValue(name, out var definition))
        {
            return definition;
        }

        if (!soundEvents.TryGetValue(name, out var soundEvent))
        {
            return null;
        }

        definition = new SoundEventDefinition(name, ResolveBase(soundEvent));
        definitions.Add(name, definition);
        return definition;
    }

    private KVObject ResolveBase(KVObject soundEvent)
    {
        if (!soundEvent.ContainsKey("base"))
        {
            return soundEvent;
        }

        var merged = KVObject.Collection();

        foreach (var (key, value, _) in SoundEventIndex.Resolve(soundEvent, name => soundEvents.GetValueOrDefault(name)))
        {
            merged.Add(key, value);
        }

        return merged;
    }
}
