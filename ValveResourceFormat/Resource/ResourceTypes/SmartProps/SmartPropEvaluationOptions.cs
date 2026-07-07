using ValveKeyValue;
using ValveResourceFormat.IO;

namespace ValveResourceFormat.ResourceTypes.SmartProps
{
    /// <summary>
    /// Inputs for a smart prop evaluation.
    /// </summary>
    public sealed class SmartPropEvaluationOptions
    {
        /// <summary>Random seed. The same seed always produces the same result.</summary>
        public int Seed { get; init; }

        /// <summary>Loader used to resolve nested smart prop references. Without one, nested smart props are skipped.</summary>
        public IFileLoader? FileLoader { get; init; }

        /// <summary>
        /// Resolver for nested smart prop references, consulted before <see cref="FileLoader"/>.
        /// Returns the referenced document's root, or null to fall through. Intended for tests.
        /// </summary>
        public Func<string, KVObject?>? NestedDocumentResolver { get; init; }

        /// <summary>Values overriding variable defaults, keyed by variable name (case-insensitive).</summary>
        public IReadOnlyDictionary<string, object>? VariableOverrides { get; init; }

        /// <summary>Hard cap on nested smart prop recursion, applied on top of the authored <c>m_nMaxDepth</c>.</summary>
        public int MaxDepth { get; init; } = 64;

        /// <summary>Promotes unhandled element/modifier classes to errors, for validation use.</summary>
        public bool Strict { get; init; }
    }
}
