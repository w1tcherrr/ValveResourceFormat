using System.Collections.Concurrent;
using ValveKeyValue;
using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.ResourceTypes.SmartProps
{
    /// <summary>
    /// A loaded smart prop document: the authored tree plus everything derivable from it once
    /// (exposed parameters with value domains, choices, parsed expressions). Load a document once
    /// and call <see cref="Evaluate"/> per seed/override combination; evaluation is cheap.
    /// </summary>
    public sealed class SmartPropDocument
    {
        /// <summary>The authored <c>CSmartPropRoot</c> tree.</summary>
        public KVObject Root { get; }

        /// <summary>Variables exposed as editable parameters, in authored order.</summary>
        public IReadOnlyList<SmartPropParameter> Parameters { get; }

        /// <summary>User-facing choices, in authored order.</summary>
        public IReadOnlyList<SmartPropChoice> Choices { get; }

        internal ConcurrentDictionary<string, SmartPropExpression.Node> ExpressionCache { get; } = new(StringComparer.Ordinal);

        private SmartPropDocument(KVObject root)
        {
            Root = root;

            var scan = SmartPropEvaluator.ScanDocument(root);
            var loadContext = CreateContext(new SmartPropEvaluationResult(), new SmartPropEvaluationOptions());
            Parameters = SmartPropEvaluator.BuildParameters(root, scan, loadContext);
            Choices = SmartPropEvaluator.BuildChoices(root, loadContext);
        }

        /// <summary>
        /// Loads a smart prop document from a <c>CSmartPropRoot</c> tree.
        /// </summary>
        public static SmartPropDocument Load(KVObject root) => new(root);

        /// <summary>
        /// Loads a smart prop document from a smart prop resource.
        /// </summary>
        public static SmartPropDocument Load(SmartProp smartProp) => new(smartProp.Data.Root);

        /// <summary>
        /// Evaluates the document into a flat list of model placements. Deterministic for a given
        /// seed and set of variable overrides.
        /// </summary>
        public SmartPropEvaluationResult Evaluate(SmartPropEvaluationOptions options)
        {
            var result = new SmartPropEvaluationResult();
            var ctx = CreateContext(result, options);

            // The working stream starts from the top document's seed, drawn from the master
            ctx.Random = new UniformRandomStream(ctx.GetDocumentSeed(string.Empty));

            var authoredMaxDepth = Root.GetInt32Property("m_nMaxDepth", 32);
            ctx.MaxDepth = Math.Min(authoredMaxDepth > 0 ? authoredMaxDepth : 32, options.MaxDepth);

            SmartPropEvaluator.DeclareVariables(Root, ctx);
            SmartPropEvaluator.ApplyChoices(Root, ctx);

            if (options.VariableOverrides != null)
            {
                foreach (var (name, value) in options.VariableOverrides)
                {
                    ctx.Variables[name] = value;
                    ctx.OverriddenVariables.Add(name);
                }
            }

            var state = SmartPropState.CreateDefault();

            if (SmartPropEvaluator.ApplyModifiers(Root, ref state, ctx))
            {
                SmartPropEvaluator.EvaluateChildren(Root, ref state, ctx);
            }

            foreach (var (name, value) in ctx.Variables)
            {
                result.VariableValues[name] = value;
            }

            return result;
        }

        /// <summary>
        /// Evaluates a hide/read-only expression against a set of variable values.
        /// </summary>
        public bool EvaluateCondition(string? expression, IReadOnlyDictionary<string, object> variables, bool defaultValue = false)
        {
            if (string.IsNullOrWhiteSpace(expression))
            {
                return defaultValue;
            }

            var ctx = CreateContext(new SmartPropEvaluationResult(), new SmartPropEvaluationOptions());

            foreach (var (name, value) in variables)
            {
                ctx.Variables[name] = value;
            }

            var result = SmartPropExpression.Evaluate(expression, ctx);
            return result == null ? defaultValue : SmartPropExpression.ToBool(result);
        }

        private SmartPropEvaluationContext CreateContext(SmartPropEvaluationResult result, SmartPropEvaluationOptions options) => new()
        {
            Random = new UniformRandomStream(options.Seed),
            MasterRandom = new UniformRandomStream(options.Seed),
            Result = result,
            ExpressionCache = ExpressionCache,
            FileLoader = options.FileLoader,
            NestedDocumentResolver = options.NestedDocumentResolver,
            Strict = options.Strict,
        };
    }
}
