using ValveKeyValue;
using ValveKeyValue.KeyValues3;
using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.ResourceTypes.ParticleUpgrade;

/// <summary>
/// Replays the engine's vpcf KV3 format-conversion chain, upgrading particle system documents
/// from their stored format to the newest implemented format.
/// </summary>
public static class ParticleFormatUpgrader
{
    /// <summary>
    /// Gets the implemented conversion steps in chain order.
    /// </summary>
    public static IReadOnlyList<ParticleUpgradeStep> Steps { get; } =
    [
        new GenericToVpcf1(),
        new Vpcf1ToVpcf2(),
        new Vpcf2ToVpcf3(),
        new Vpcf3ToVpcf4(),
        new Vpcf4ToVpcf5(),
        new Vpcf5ToVpcf6(),
        new Vpcf6ToVpcf7(),
        new Vpcf7ToVpcf8(),
        new Vpcf8ToVpcf9(),
        new Vpcf9ToVpcf10(),
        new Vpcf10ToVpcf11(),
        new Vpcf11ToVpcf12(),
        new Vpcf12ToVpcf13(),
        new Vpcf13ToVpcf14(),
        new Vpcf14ToVpcf15(),
        new Vpcf15ToVpcf16(),
        new Vpcf16ToVpcf17(),
    ];

    private static readonly Guid[] ChainIds = BuildChainIds();

    private static Guid[] BuildChainIds()
    {
        var ids = new Guid[67];
        ids[0] = KV3IDLookup.Table["generic"];

        for (var i = 1; i < ids.Length; i++)
        {
            ids[i] = KV3IDLookup.Table[FormattableString.Invariant($"vpcf{i}")];
        }

        return ids;
    }

    /// <summary>
    /// Deep-clones the given document root and applies every implemented chain step past the
    /// stored format, returning the upgraded clone. Missing and unknown formats start at the
    /// oldest step, matching the engine treating headerless data as oldest. A stored format
    /// newer than the implemented steps returns the root unchanged.
    /// </summary>
    public static KVObject UpgradeToLatest(KVObject root, KV3ID? storedFormat)
    {
        ArgumentNullException.ThrowIfNull(root);

        var start = ResolveStartIndex(storedFormat);

        if (start >= Steps.Count)
        {
            return root;
        }

        var upgraded = KVObjectDeepClone.Clone(root);

        for (var i = start; i < Steps.Count; i++)
        {
            Steps[i].Apply(upgraded);
        }

        return upgraded;
    }

    private static int ResolveStartIndex(KV3ID? storedFormat)
    {
        if (storedFormat is not { } format)
        {
            return 0;
        }

        var index = Array.IndexOf(ChainIds, format.Id);
        return index < 0 ? 0 : index;
    }
}
