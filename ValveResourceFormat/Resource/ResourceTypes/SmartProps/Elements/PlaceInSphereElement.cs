namespace ValveResourceFormat.ResourceTypes.SmartProps.Elements
{
    /// <summary>
    /// Scatters children within a spherical shell or a circle, randomly or in a regular
    /// (fibonacci) distribution, optionally aligning them to the radial direction.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropElement_PlaceInSphere">CSmartPropElement_PlaceInSphere</seealso>
    sealed class PlaceInSphereElement : SmartPropElement
    {
        private const int MaxLoopInstances = 4096;

        private readonly FloatAttribute countMin;
        private readonly FloatAttribute countMax;
        private readonly FloatAttribute radiusInner;
        private readonly FloatAttribute radiusOuter;
        private readonly StringAttribute placementMode;
        private readonly StringAttribute distributionMode;
        private readonly FloatAttribute randomness;
        private readonly VectorAttribute planeUpDirection;
        private readonly BoolAttribute alignOrientation;
        private readonly VectorAttribute alignDirection;

        public PlaceInSphereElement(SmartPropDefinitionParser parse) : base(parse)
        {
            countMin = parse.Float("m_nCountMin", 1f);
            countMax = parse.Float("m_nCountMax", 1f);
            radiusInner = parse.Float("m_flPositionRadiusInner");
            radiusOuter = parse.Float("m_flPositionRadiusOuter");
            placementMode = parse.String("m_PlacementMode", "SPHERE");
            distributionMode = parse.String("m_DistributionMode", "RANDOM");
            randomness = parse.Float("m_flRandomness");
            planeUpDirection = parse.Vector("m_vPlaneUpDirection", Vector3.UnitZ);
            alignOrientation = parse.Bool("m_bAlignOrientation", false);
            alignDirection = parse.Vector("m_vAlignDirection", Vector3.UnitZ);
        }

        protected override void OnEvaluate(ref SmartPropState state, SmartPropEvaluationContext ctx)
        {
            if (Children.Count == 0)
            {
                return;
            }

            var minCount = countMin.EvaluateInt(ctx);
            var maxCount = countMax.EvaluateInt(ctx);
            var count = Math.Min(ctx.Random.RandomInt(Math.Min(minCount, maxCount), Math.Max(minCount, maxCount)), MaxLoopInstances);

            var inner = radiusInner.Evaluate(ctx);
            var outer = radiusOuter.Evaluate(ctx);
            var circle = placementMode.Evaluate(ctx)!.Equals("CIRCLE", StringComparison.OrdinalIgnoreCase);
            var regular = distributionMode.Evaluate(ctx)!.Equals("REGULAR", StringComparison.OrdinalIgnoreCase);
            var jitter = Math.Clamp(randomness.Evaluate(ctx), 0f, 1f);
            var planeUp = planeUpDirection.Evaluate(ctx);
            var align = alignOrientation.Evaluate(ctx);
            var alignAxis = alignDirection.Evaluate(ctx);

            var planeBasis = SmartPropHelpers.BuildBasis(Vector3.UnitX, planeUp == Vector3.Zero ? Vector3.UnitZ : planeUp, prioritizeUp: true) ?? Matrix4x4.Identity;

            using var loop = ctx.EnterLoop(count);

            for (var i = 0; i < count; i++)
            {
                Vector3 direction;

                if (circle)
                {
                    float angle;

                    if (regular)
                    {
                        angle = i * MathF.Tau / Math.Max(count, 1)
                            + jitter * ctx.Random.RandomFloat(-MathF.Tau, MathF.Tau) / (2f * Math.Max(count, 1));
                    }
                    else
                    {
                        angle = ctx.Random.RandomFloat(0f, MathF.Tau);
                    }

                    direction = Vector3.TransformNormal(new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0f), planeBasis);
                }
                else if (regular)
                {
                    // Fibonacci sphere with jitter
                    var z = 1f - (2f * i + 1f) / Math.Max(count, 1);
                    var theta = i * 2.399963f + jitter * ctx.Random.RandomFloat(-0.5f, 0.5f);
                    var ring = MathF.Sqrt(Math.Max(1f - z * z, 0f));
                    direction = new Vector3(ring * MathF.Cos(theta), ring * MathF.Sin(theta), z);
                }
                else
                {
                    var z = ctx.Random.RandomFloat(-1f, 1f);
                    var angle = ctx.Random.RandomFloat(0f, MathF.Tau);
                    var ring = MathF.Sqrt(Math.Max(1f - z * z, 0f));
                    direction = new Vector3(ring * MathF.Cos(angle), ring * MathF.Sin(angle), z);
                }

                var radius = regular && jitter == 0f
                    ? (inner + outer) * 0.5f
                    : ctx.Random.RandomFloat(Math.Min(inner, outer), Math.Max(inner, outer));

                var childState = state;
                SmartPropHelpers.ApplyTranslate(ref childState, direction * radius, SmartPropSpace.Element);

                if (align && alignAxis != Vector3.Zero && direction != Vector3.Zero)
                {
                    var worldRadial = Vector3.TransformNormal(direction, state.Transform);
                    var worldAlign = Vector3.TransformNormal(alignAxis, childState.Transform);
                    var rotation = SmartPropHelpers.FromToRotation(worldAlign, worldRadial);
                    childState.Transform = childState.Transform with { Translation = Vector3.Zero } * rotation;
                    childState.Transform.Translation = Vector3.Transform(direction * radius * state.Scale, state.Transform);
                }

                ctx.InstanceIndex = i;
                EvaluateChildren(ref childState, ctx);
            }
        }
    }
}
