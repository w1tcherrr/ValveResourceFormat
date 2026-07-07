namespace ValveResourceFormat.ResourceTypes.SmartProps.Elements
{
    /// <summary>
    /// Bends the placements its children emitted along an arc. Rigid approximation: placements
    /// are repositioned and reoriented, but vertices are not warped.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropElement_BendDeformer">CSmartPropElement_BendDeformer</seealso>
    sealed class BendDeformerElement : SmartPropElement
    {
        private readonly BoolAttribute deformationEnabled;
        private readonly FloatAttribute bendAngle;
        private readonly VectorAttribute origin;
        private readonly VectorAttribute angles;
        private readonly VectorAttribute size;
        private readonly FloatAttribute bendPoint;
        private readonly FloatAttribute bendRadius;

        public BendDeformerElement(SmartPropDefinitionParser parse) : base(parse)
        {
            deformationEnabled = parse.Bool("m_bDeformationEnabled", true);
            bendAngle = parse.Float("m_flBendAngle");
            origin = parse.Vector("m_vOrigin");
            angles = parse.Vector("m_vAngles");
            size = parse.Vector("m_vSize");
            bendPoint = parse.Float("m_flBendPoint");
            bendRadius = parse.Float("m_flBendRadius");
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

            var angle = bendAngle.Evaluate(ctx);

            if (Math.Abs(angle) < 1e-3f)
            {
                return;
            }

            var deformerOrigin = origin.Evaluate(ctx);
            var deformerAngles = angles.Evaluate(ctx);
            var deformerSize = size.Evaluate(ctx);
            var bendStartFraction = Math.Clamp(bendPoint.Evaluate(ctx), 0f, 1f);
            var radiusOverride = bendRadius.Evaluate(ctx);

            var angleRadians = float.DegreesToRadians(angle);
            var bendStart = bendStartFraction * deformerSize.X;
            var arcLength = Math.Max(deformerSize.X - bendStart, 1e-3f);
            var radius = radiusOverride > 0f ? radiusOverride : arcLength / Math.Abs(angleRadians);

            if (radius < 1e-3f)
            {
                return;
            }

            var sign = MathF.Sign(angleRadians);

            var deformerFrame = EntityTransformHelper.CreateRotationMatrixFromEulerAngles(deformerAngles)
                * Matrix4x4.CreateTranslation(deformerOrigin)
                * state.Transform;

            if (!Matrix4x4.Invert(deformerFrame, out var worldToDeformer))
            {
                return;
            }

            for (var i = firstPlacement; i < lastPlacement; i++)
            {
                var placement = ctx.Result.Placements[i];
                var local = placement.Transform * worldToDeformer;
                var p = local.Translation;

                if (p.X <= bendStart)
                {
                    continue;
                }

                var theta = (p.X - bendStart) / radius;
                var effectiveRadius = radius - sign * p.Y;

                var bent = new Vector3(
                    bendStart + effectiveRadius * MathF.Sin(theta),
                    sign * (radius - effectiveRadius * MathF.Cos(theta)),
                    p.Z);

                var rotation = local with { Translation = Vector3.Zero };
                var newLocal = rotation * Matrix4x4.CreateRotationZ(sign * theta);
                newLocal.Translation = bent;

                placement.Transform = newLocal * deformerFrame;
            }
        }
    }
}
