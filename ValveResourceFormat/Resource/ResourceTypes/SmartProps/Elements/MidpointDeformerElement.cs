namespace ValveResourceFormat.ResourceTypes.SmartProps.Elements
{
    /// <summary>
    /// Displaces the placements its children emitted near the midpoint of a line, with distance
    /// falloff. Rigid approximation: placements move, vertices are not warped.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropElement_MidpointDeformer">CSmartPropElement_MidpointDeformer</seealso>
    sealed class MidpointDeformerElement : SmartPropElement
    {
        private readonly BoolAttribute deformationEnabled;
        private readonly VectorAttribute offset;
        private readonly FloatAttribute radius;
        private readonly VectorAttribute start;
        private readonly VectorAttribute end;
        private readonly FloatAttribute falloff;

        public MidpointDeformerElement(SmartPropDefinitionParser parse) : base(parse)
        {
            deformationEnabled = parse.Bool("m_bDeformationEnabled", true);
            offset = parse.Vector("m_vOffset");
            radius = parse.Float("m_fRadius", 64f);
            start = parse.Vector("m_vStart");
            end = parse.Vector("m_vEnd");
            falloff = parse.Float("m_fFalloff", 1f);
        }

        protected override void OnEvaluate(ref SmartPropState state, SmartPropEvaluationContext ctx)
        {
            var firstPlacement = ctx.Result.Placements.Count;
            var childState = state;
            EvaluateChildren(ref childState, ctx);

            if (!deformationEnabled.Evaluate(ctx))
            {
                return;
            }

            var lastPlacement = ctx.Result.Placements.Count;

            if (lastPlacement == firstPlacement)
            {
                return;
            }

            var displacement = offset.Evaluate(ctx);
            var effectRadius = radius.Evaluate(ctx);

            if (displacement == Vector3.Zero || effectRadius < 1e-3f)
            {
                return;
            }

            var startWorld = SmartPropHelpers.PointToWorld(start.Evaluate(ctx), SmartPropSpace.Element, state);
            var endWorld = SmartPropHelpers.PointToWorld(end.Evaluate(ctx), SmartPropSpace.Element, state);
            var falloffExponent = Math.Max(falloff.Evaluate(ctx), 0.01f);
            var midpoint = (startWorld + endWorld) * 0.5f;
            var worldOffset = Vector3.TransformNormal(displacement * state.Scale, state.Transform);

            for (var i = firstPlacement; i < lastPlacement; i++)
            {
                var placement = ctx.Result.Placements[i];
                var distance = Vector3.Distance(placement.Transform.Translation, midpoint);

                if (distance >= effectRadius)
                {
                    continue;
                }

                var weight = MathF.Pow(1f - distance / effectRadius, falloffExponent);
                var transform = placement.Transform;
                transform.Translation += worldOffset * weight;
                placement.Transform = transform;
            }
        }
    }
}
