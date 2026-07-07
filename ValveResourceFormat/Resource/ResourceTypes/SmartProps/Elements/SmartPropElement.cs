using ValveResourceFormat.ResourceTypes.SmartProps.Criteria;
using ValveResourceFormat.ResourceTypes.SmartProps.Operations;

namespace ValveResourceFormat.ResourceTypes.SmartProps.Elements
{
    /// <summary>
    /// Base class for smart prop elements: nodes of the authored tree that place models,
    /// group children, or instance children multiple times. Constructed once per document;
    /// evaluation walks the compiled tree.
    /// </summary>
    abstract class SmartPropElement
    {
        public BoolAttribute Enabled { get; }
        public List<SmartPropOperation> Operations { get; } = [];
        public List<SmartPropElement> Children { get; } = [];
        public List<SmartPropSelectionCriterion> Criteria { get; } = [];

        protected SmartPropElement(SmartPropDefinitionParser parse)
        {
            Enabled = parse.Bool("m_bEnabled", true);

            foreach (var modifier in parse.Array("m_Modifiers"))
            {
                Operations.Add(SmartPropOperationFactory.Create(modifier));
            }

            foreach (var child in parse.Array("m_Children"))
            {
                Children.Add(SmartPropElementFactory.Create(child));
            }

            foreach (var criterion in parse.Array("m_SelectionCriteria"))
            {
                Criteria.Add(SmartPropCriterionFactory.Create(criterion));
            }
        }

        /// <summary>
        /// Evaluates this element against a copy of the caller's state: enabled gate, then
        /// modifiers (which may reject the element), then the element's own behavior.
        /// </summary>
        public void Evaluate(SmartPropState state, SmartPropEvaluationContext ctx)
        {
            if (!Enabled.Evaluate(ctx))
            {
                return;
            }

            if (!ApplyOperations(ref state, ctx))
            {
                return;
            }

            OnEvaluate(ref state, ctx);
        }

        protected abstract void OnEvaluate(ref SmartPropState state, SmartPropEvaluationContext ctx);

        /// <summary>
        /// Applies this element's modifiers in order. Returns false when a filter rejects the element.
        /// </summary>
        public bool ApplyOperations(ref SmartPropState state, SmartPropEvaluationContext ctx)
        {
            foreach (var operation in Operations)
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

        protected void EvaluateChildren(ref SmartPropState state, SmartPropEvaluationContext ctx)
            => EvaluateChildList(Children, ref state, ctx);

        /// <summary>
        /// Evaluates a child list in order. ModifyState children mutate the ongoing state that
        /// following siblings see; every other child works on its own copy.
        /// </summary>
        public static void EvaluateChildList(List<SmartPropElement> children, ref SmartPropState state, SmartPropEvaluationContext ctx)
        {
            foreach (var child in children)
            {
                if (child is ModifyStateElement modifyState)
                {
                    if (modifyState.Enabled.Evaluate(ctx))
                    {
                        modifyState.ApplyOperations(ref state, ctx);
                    }

                    continue;
                }

                child.Evaluate(state, ctx);
            }
        }

        /// <summary>
        /// Whether this element can be selected by a placement element, and with what weight.
        /// Deterministic filters and IsValid criteria exclude it; ChoiceWeight sets the weight.
        /// </summary>
        public bool IsEligible(SmartPropEvaluationContext ctx, out float weight)
        {
            weight = 1f;

            if (!Enabled.Evaluate(ctx))
            {
                return false;
            }

            // Deterministic filters exclude a child from selection entirely (authors gate PickOne
            // alternatives with variable filters); random filters only roll during real evaluation.
            // These filter classes never read or write the transform state
            var scratchState = default(SmartPropState);

            foreach (var operation in Operations)
            {
                if (operation is not (VariableValueFilter or ExpressionFilter))
                {
                    continue;
                }

                if (!operation.Enabled.Evaluate(ctx))
                {
                    continue;
                }

                if (!operation.Apply(ref scratchState, ctx))
                {
                    return false;
                }
            }

            foreach (var criterion in Criteria)
            {
                if (!criterion.Enabled.Evaluate(ctx))
                {
                    continue;
                }

                switch (criterion)
                {
                    case ChoiceWeightCriterion choiceWeight:
                        weight = choiceWeight.Weight.Evaluate(ctx);
                        break;

                    case IsValidCriterion isValid:
                        if (!isValid.Expression.Evaluate(ctx))
                        {
                            return false;
                        }

                        break;

                    // Length/end-cap/path/mesh criteria are consumed by the placement elements
                    default:
                        break;
                }
            }

            return true;
        }
    }
}
