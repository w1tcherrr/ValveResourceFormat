namespace ValveResourceFormat.ResourceTypes.SmartProps.Elements
{
    /// <summary>
    /// Evaluates another smart prop document at the current transform. The referenced document
    /// gets its own OBJECT frame, its own working random stream (seeded from its cached
    /// per-document seed) and, by default, its own variable environment.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropElement_SmartProp">CSmartPropElement_SmartProp</seealso>
    sealed class NestedSmartPropElement : SmartPropElement
    {
        private readonly StringAttribute smartPropResource;
        private readonly BoolAttribute localEvaluationState;

        public NestedSmartPropElement(SmartPropDefinitionParser parse) : base(parse)
        {
            smartPropResource = parse.String("m_sSmartProp");
            localEvaluationState = parse.Bool("m_bLocalEvaluationState", true);
        }

        protected override void OnEvaluate(ref SmartPropState state, SmartPropEvaluationContext ctx)
        {
            var resourceName = smartPropResource.Evaluate(ctx);

            if (string.IsNullOrEmpty(resourceName))
            {
                return;
            }

            if (ctx.Depth >= ctx.MaxDepth)
            {
                return;
            }

            if (--ctx.RemainingNestedEvaluations < 0)
            {
                ctx.Warn(SmartPropDiagnosticCode.NestedBudgetExhausted, null, "Nested smart prop evaluation budget exhausted, output is truncated");
                return;
            }

            var childDocument = ctx.NestedDocumentCache.GetOrAdd(resourceName, SmartPropDocument.ResolveNested, ctx);

            if (childDocument == null)
            {
                if (ctx.FileLoader == null)
                {
                    ctx.Warn(SmartPropDiagnosticCode.NestedNoLoader, resourceName, "No file loader available to resolve nested smart props");
                }
                else
                {
                    ctx.Warn(SmartPropDiagnosticCode.NestedLoadFailed, resourceName, $"Failed to load nested smart prop '{resourceName}'");
                }

                return;
            }

            var childState = state;
            childState.ObjectTransform = state.Transform;
            childState.ObjectScale = state.Scale;
            childState.ObjectTint = state.Tint;

            // Each document evaluates with a working stream seeded from its own cached seed, so
            // repeated placements of the same nested prop are internally identical by design
            var parentRandom = ctx.Random;
            ctx.Random = new UniformRandomStream(ctx.GetDocumentSeed(resourceName));

            var localState = localEvaluationState.Evaluate(ctx);
            Dictionary<string, object>? savedVariables = null;
            Dictionary<string, SmartPropState>? savedStates = null;

            if (localState)
            {
                savedVariables = new Dictionary<string, object>(ctx.Variables, StringComparer.OrdinalIgnoreCase);
                savedStates = new Dictionary<string, SmartPropState>(ctx.SavedStates, StringComparer.OrdinalIgnoreCase);
                ctx.Variables.Clear();
                ctx.SavedStates.Clear();
            }

            ctx.Depth++;

            // A nested document may narrow the remaining recursion depth via its own m_nMaxDepth
            var previousMaxDepth = ctx.MaxDepth;

            if (childDocument.AuthoredMaxDepth > 0)
            {
                ctx.MaxDepth = Math.Min(ctx.MaxDepth, ctx.Depth + childDocument.AuthoredMaxDepth);
            }

            try
            {
                childDocument.ApplyEnvironment(ctx);

                if (childDocument.ApplyRootOperations(ref childState, ctx))
                {
                    EvaluateChildList(childDocument.Children, ref childState, ctx);
                }
            }
            finally
            {
                ctx.Depth--;
                ctx.MaxDepth = previousMaxDepth;
                ctx.Random = parentRandom;

                if (localState)
                {
                    ctx.Variables.Clear();
                    ctx.SavedStates.Clear();

                    foreach (var (key, value) in savedVariables!)
                    {
                        ctx.Variables[key] = value;
                    }

                    foreach (var (key, value) in savedStates!)
                    {
                        ctx.SavedStates[key] = value;
                    }
                }
            }
        }
    }
}
