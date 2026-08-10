using ValveKeyValue;

namespace ValveResourceFormat.ResourceTypes.ParticleUpgrade;

/// <summary>
/// One step of the vpcf KV3 format-conversion chain, reimplementing the engine converter that
/// upgrades particle documents from <see cref="FromFormat"/> to <see cref="ToFormat"/>.
/// </summary>
public abstract class ParticleUpgradeStep
{
    /// <summary>
    /// Gets the KV3 format name this step consumes.
    /// </summary>
    public abstract string FromFormat { get; }

    /// <summary>
    /// Gets the KV3 format name this step produces.
    /// </summary>
    public abstract string ToFormat { get; }

    /// <summary>
    /// Applies the step's transformation to the document root in place. The tree must be
    /// list-backed as produced by <see cref="KVObjectDeepClone.Clone"/>.
    /// </summary>
    public abstract void Apply(KVObject root);
}
