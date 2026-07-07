using System.Collections.Concurrent;
using ValveKeyValue;
using ValveResourceFormat.IO;

namespace ValveResourceFormat.ResourceTypes.SmartProps
{
    /// <summary>
    /// Running transform and render state. Passed by value down the element tree so that
    /// an element's operations never leak to its siblings; <c>ModifyState</c> elements and
    /// root modifiers mutate the caller's copy by reference.
    /// </summary>
    internal struct SmartPropState
    {
        /// <summary>Rotation and translation of the current element.</summary>
        public Matrix4x4 Transform;

        /// <summary>Accumulated uniform/per-axis scale of the current element.</summary>
        public Vector3 Scale;

        /// <summary>Current tint in normalized RGBA.</summary>
        public Vector4 Tint;

        /// <summary>Frame the current smart prop document was placed in (the OBJECT coordinate space).</summary>
        public Matrix4x4 ObjectTransform;

        /// <summary>Scale of the OBJECT frame.</summary>
        public Vector3 ObjectScale;

        /// <summary>Tint of the OBJECT frame.</summary>
        public Vector4 ObjectTint;

        /// <summary>Material overrides in effect. Copy-on-write: mutating operations replace the list.</summary>
        public List<(string Original, string Replacement)> MaterialOverrides;

        public static SmartPropState CreateDefault() => new()
        {
            Transform = Matrix4x4.Identity,
            Scale = Vector3.One,
            Tint = Vector4.One,
            ObjectTransform = Matrix4x4.Identity,
            ObjectScale = Vector3.One,
            ObjectTint = Vector4.One,
            MaterialOverrides = [],
        };
    }

    /// <summary>
    /// Shared, sequentially mutated evaluation state: the variable environment, saved states,
    /// random stream, recursion tracking and the result being built.
    /// </summary>
    internal sealed class SmartPropEvaluationContext
    {
        /// <summary>
        /// The working stream all placement randomness draws from. Reseeded per document from
        /// <see cref="MasterRandom"/>, matching the engine: nested copies of the same sub-prop
        /// get identical internal randomness by design; variety between copies comes from the
        /// parent's own draws before entering.
        /// </summary>
        public required UniformRandomStream Random { get; set; }

        /// <summary>Seed generator: only ever drawn from to produce per-document seeds.</summary>
        public required UniformRandomStream MasterRandom { get; init; }

        /// <summary>Per-document seeds, drawn lazily from the master and cached for the run.</summary>
        public Dictionary<string, int> DocumentSeeds { get; } = new(StringComparer.OrdinalIgnoreCase);

        public required SmartPropEvaluationResult Result { get; init; }
        public required ConcurrentDictionary<string, SmartPropExpression.Node> ExpressionCache { get; init; }
        public required ConcurrentDictionary<string, SmartPropDocument?> NestedDocumentCache { get; init; }
        public IFileLoader? FileLoader { get; init; }
        public Func<string, KVObject?>? NestedDocumentResolver { get; init; }
        public bool Strict { get; init; }

        /// <summary>Names of variables pinned by user overrides; evaluation-time writes to them are ignored.</summary>
        public HashSet<string> OverriddenVariables { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, object> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, SmartPropState> SavedStates { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Current evaluation depth; 1 while evaluating the top-level document.</summary>
        public int Depth { get; set; } = 1;
        public int MaxDepth { get; set; }

        /// <summary>
        /// Self-referencing kits are legitimate (bounded by depth and filters), so recursion is not
        /// cycle-checked; these budgets bound runaway evaluation instead.
        /// </summary>
        public int RemainingNestedEvaluations { get; set; } = 4096;

        /// <summary>Upper bound on emitted placements.</summary>
        public int MaxPlacements { get; } = 100_000;

        /// <summary>Current instance number inside a placement loop, exposed to expressions.</summary>
        public int InstanceIndex { get; set; }

        /// <summary>Instance count of the innermost placement loop, exposed to expressions.</summary>
        public int InstanceCount { get; set; } = 1;

        /// <summary>Stretch factor assigned to the current fit-on-line segment, exposed to expressions.</summary>
        public double LinearScale { get; set; } = 1.0;

        /// <summary>Length of the innermost fit-on-line span, exposed to expressions.</summary>
        public double LineLength { get; set; }

        /// <summary>Normalized position along the innermost path, exposed to expressions.</summary>
        public double PathParameter { get; set; }

        /// <summary>Keys of gizmos already reported, so re-runs of the same operation don't duplicate UI entries.</summary>
        public HashSet<string> ReportedGizmos { get; } = new(StringComparer.OrdinalIgnoreCase);

        private readonly HashSet<(SmartPropDiagnosticCode, string?)> reported = [];

        public void Warn(SmartPropDiagnosticCode code, string? subject, string message)
        {
            if (!reported.Add((code, subject)))
            {
                return;
            }

            var isError = Strict && code is SmartPropDiagnosticCode.UnhandledElement or SmartPropDiagnosticCode.UnhandledModifier;
            Result.Diagnostics.Add(new SmartPropDiagnostic(code, subject, message, isError));
        }

        /// <summary>
        /// Gets the seed of a document (top level uses an empty key), drawing and caching it on
        /// first use.
        /// </summary>
        public int GetDocumentSeed(string documentKey)
        {
            if (!DocumentSeeds.TryGetValue(documentKey, out var seed))
            {
                seed = MasterRandom.RandomInt(0, int.MaxValue);
                DocumentSeeds[documentKey] = seed;
            }

            return seed;
        }

        public object? GetVariable(string name)
            => Variables.TryGetValue(name, out var value) ? value : null;

        public void SetVariable(string name, object value)
        {
            if (OverriddenVariables.Contains(name))
            {
                return;
            }

            Variables[name] = value;
        }

        /// <summary>
        /// Snapshots the loop-context values (instance index/count, line and path context) and
        /// restores them when disposed. Placement generators wrap their instance loops in this.
        /// </summary>
        public LoopScope EnterLoop(int instanceCount) => new(this, instanceCount);

        public ref struct LoopScope
        {
            private readonly SmartPropEvaluationContext ctx;
            private readonly int instanceIndex;
            private readonly int instanceCount;
            private readonly double linearScale;
            private readonly double lineLength;
            private readonly double pathParameter;

            public LoopScope(SmartPropEvaluationContext ctx, int newInstanceCount)
            {
                this.ctx = ctx;
                instanceIndex = ctx.InstanceIndex;
                instanceCount = ctx.InstanceCount;
                linearScale = ctx.LinearScale;
                lineLength = ctx.LineLength;
                pathParameter = ctx.PathParameter;

                ctx.InstanceIndex = 0;
                ctx.InstanceCount = newInstanceCount;
            }

            public void Dispose()
            {
                ctx.InstanceIndex = instanceIndex;
                ctx.InstanceCount = instanceCount;
                ctx.LinearScale = linearScale;
                ctx.LineLength = lineLength;
                ctx.PathParameter = pathParameter;
            }
        }
    }
}
