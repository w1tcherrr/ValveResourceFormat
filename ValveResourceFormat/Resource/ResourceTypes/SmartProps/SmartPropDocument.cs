using System.Collections.Concurrent;
using ValveKeyValue;
using ValveResourceFormat.ResourceTypes.SmartProps.Elements;
using ValveResourceFormat.ResourceTypes.SmartProps.Operations;
using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.ResourceTypes.SmartProps
{
    /// <summary>
    /// A loaded smart prop document: the authored tree compiled into typed elements and
    /// operations, plus everything derivable from it once (exposed parameters with value
    /// domains, choices, parsed expressions). Load a document once and call
    /// <see cref="Evaluate"/> per seed/override combination; evaluation is cheap.
    /// </summary>
    public sealed class SmartPropDocument
    {
        /// <summary>The authored <c>CSmartPropRoot</c> tree.</summary>
        public KVObject Root { get; }

        /// <summary>Variables exposed as editable parameters, in authored order.</summary>
        public IReadOnlyList<SmartPropParameter> Parameters { get; }

        /// <summary>User-facing choices, in authored order.</summary>
        public IReadOnlyList<SmartPropChoice> Choices { get; }

        internal List<SmartPropOperation> RootOperations { get; } = [];
        internal List<SmartPropElement> Children { get; } = [];
        internal int AuthoredMaxDepth { get; }
        internal ConcurrentDictionary<string, SmartPropExpression.Node> ExpressionCache { get; } = new(StringComparer.Ordinal);

        /// <summary>Nested documents compiled on first reference, shared across evaluations.</summary>
        internal ConcurrentDictionary<string, SmartPropDocument?> NestedDocumentCache { get; } = new(StringComparer.OrdinalIgnoreCase);

        private readonly List<(string Name, SmartPropParameterType Type, KVObject Variable)> variableDeclarations = [];
        private readonly List<KVObject> choiceNodes = [];

        private SmartPropDocument(KVObject root, bool includeParameters)
        {
            Root = root;
            AuthoredMaxDepth = root.GetInt32Property("m_nMaxDepth");

            var parse = new SmartPropDefinitionParser(root);

            foreach (var modifier in parse.Array("m_Modifiers"))
            {
                RootOperations.Add(SmartPropOperationFactory.Create(modifier));
            }

            foreach (var child in parse.Array("m_Children"))
            {
                Children.Add(SmartPropElementFactory.Create(child));
            }

            var variables = root.GetArray("m_Variables");

            if (variables != null)
            {
                foreach (var variable in variables)
                {
                    var name = variable.GetStringProperty("m_VariableName");

                    if (string.IsNullOrEmpty(name))
                    {
                        continue;
                    }

                    var type = SmartPropParameterBuilder.GetParameterType(variable.GetStringProperty("_class", string.Empty));
                    variableDeclarations.Add((name, type, variable));
                }
            }

            var choices = root.GetArray("m_Choices");

            if (choices != null)
            {
                choiceNodes.AddRange(choices);
            }

            if (includeParameters)
            {
                var scan = SmartPropParameterBuilder.ScanDocument(root);
                var loadContext = CreateContext(new SmartPropEvaluationResult(), new SmartPropEvaluationOptions());
                Parameters = SmartPropParameterBuilder.BuildParameters(root, scan, loadContext);
                Choices = SmartPropParameterBuilder.BuildChoices(root, loadContext);
            }
            else
            {
                Parameters = [];
                Choices = [];
            }
        }

        /// <summary>
        /// Loads a smart prop document from a <c>CSmartPropRoot</c> tree.
        /// </summary>
        public static SmartPropDocument Load(KVObject root) => new(root, includeParameters: true);

        /// <summary>
        /// Loads a smart prop document from a smart prop resource.
        /// </summary>
        public static SmartPropDocument Load(SmartProp smartProp) => new(smartProp.Data.Root, includeParameters: true);

        /// <summary>
        /// Resolves and compiles a nested document reference, skipping the UI-only parameter
        /// derivation. Returns null when the reference cannot be resolved.
        /// </summary>
        internal static SmartPropDocument? ResolveNested(string resourceName, SmartPropEvaluationContext ctx)
        {
            var childRoot = ctx.NestedDocumentResolver?.Invoke(resourceName);

            if (childRoot == null)
            {
                if (ctx.FileLoader == null)
                {
                    return null;
                }

                var resource = ctx.FileLoader.LoadFileCompiled(resourceName);

                if (resource?.DataBlock is not BinaryKV3 childData)
                {
                    return null;
                }

                childRoot = childData.Data.Root;
            }

            return new SmartPropDocument(childRoot, includeParameters: false);
        }

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

            ctx.MaxDepth = Math.Min(AuthoredMaxDepth > 0 ? AuthoredMaxDepth : 32, options.MaxDepth);

            ApplyEnvironment(ctx);

            if (options.VariableOverrides != null)
            {
                foreach (var (name, value) in options.VariableOverrides)
                {
                    ctx.Variables[name] = value;
                    ctx.OverriddenVariables.Add(name);
                }
            }

            var state = SmartPropState.CreateDefault();

            if (ApplyRootOperations(ref state, ctx))
            {
                SmartPropElement.EvaluateChildList(Children, ref state, ctx);
            }

            foreach (var (name, value) in ctx.Variables)
            {
                result.VariableValues[name] = value;
            }

            return result;
        }

        /// <summary>
        /// Declares the document's variables with their defaults (existing entries win when the
        /// environment is shared) and applies the default option of every choice.
        /// </summary>
        internal void ApplyEnvironment(SmartPropEvaluationContext ctx)
        {
            foreach (var (name, type, variable) in variableDeclarations)
            {
                ctx.Variables.TryAdd(name, SmartPropParameterBuilder.GetVariableDefault(variable, type, ctx));
            }

            foreach (var choice in choiceNodes)
            {
                var defaultOption = choice.GetStringProperty("m_DefaultOption");
                var options = choice.GetArray("m_Options");

                if (options == null || options.Count == 0)
                {
                    continue;
                }

                var selected = options[0];

                if (!string.IsNullOrEmpty(defaultOption))
                {
                    foreach (var option in options)
                    {
                        if (option.GetStringProperty("m_Name") == defaultOption)
                        {
                            selected = option;
                            break;
                        }
                    }
                }

                var variableValues = selected.GetArray("m_VariableValues");

                if (variableValues == null)
                {
                    continue;
                }

                foreach (var variableValue in variableValues)
                {
                    var target = variableValue.GetStringProperty("m_TargetName");

                    if (!string.IsNullOrEmpty(target))
                    {
                        ctx.Variables[target] = SmartPropValue.ConvertDataTypedValue(variableValue, ctx);
                    }
                }
            }
        }

        /// <summary>
        /// Applies the document's root modifiers in order. Returns false when a filter rejects
        /// the whole document.
        /// </summary>
        internal bool ApplyRootOperations(ref SmartPropState state, SmartPropEvaluationContext ctx)
        {
            foreach (var operation in RootOperations)
            {
                if (!operation.Enabled.Evaluate(ctx))
                {
                    continue;
                }

                if (!operation.Apply(ref state, ctx))
                {
                    return false;
                }
            }

            return true;
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
            NestedDocumentCache = NestedDocumentCache,
            FileLoader = options.FileLoader,
            NestedDocumentResolver = options.NestedDocumentResolver,
            Strict = options.Strict,
        };
    }
}
