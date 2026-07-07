namespace ValveResourceFormat.ResourceTypes.SmartProps.Operations
{
    /// <summary>
    /// Base for trace operations. There is no world geometry to trace against in a standalone
    /// prop, so every trace misses and only the authored no-hit behavior applies.
    /// </summary>
    abstract class TraceOperation : SmartPropOperation
    {
        private readonly string className;
        private readonly StringAttribute noHitResult;

        protected TraceOperation(SmartPropDefinitionParser parse, string className) : base(parse)
        {
            this.className = className;
            noHitResult = parse.String("m_nNoHitResult", "NOTHING");
        }

        public sealed override bool Apply(ref SmartPropState state, SmartPropEvaluationContext ctx)
        {
            ctx.Warn(SmartPropDiagnosticCode.NeedsWorldContext, className, $"{className} has no world geometry to trace against, applying its no-hit behavior");

            var noHit = noHitResult.Evaluate(ctx)!.ToUpperInvariant();

            if (noHit == "DISCARD")
            {
                // Discarding on no-hit would delete every trace-gated element in a world-less
                // viewer (foliage scatters trace against ground), so keep the element instead
                ctx.Warn(SmartPropDiagnosticCode.NeedsWorldContext, "trace-discard", "Trace discard-on-miss ignored because there is no world geometry");
                return true;
            }

            if (noHit is not ("MOVE_TO_START" or "MOVE_TO_END"))
            {
                return true;
            }

            if (!ComputeTrace(in state, ctx, out var start, out var end))
            {
                return true;
            }

            state.Transform.Translation = noHit == "MOVE_TO_START" ? start : end;
            return true;
        }

        protected abstract bool ComputeTrace(in SmartPropState state, SmartPropEvaluationContext ctx, out Vector3 start, out Vector3 end);
    }

    /// <summary>
    /// Traces from the current position along a direction.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropOperation_TraceInDirection">CSmartPropOperation_TraceInDirection</seealso>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropOperation_Trace">CSmartPropOperation_Trace</seealso>
    sealed class TraceInDirectionOperation : TraceOperation
    {
        private readonly VectorAttribute traceDirection;
        private readonly StringAttribute directionSpace;
        private readonly FloatAttribute originOffset;
        private readonly FloatAttribute traceLength;

        public TraceInDirectionOperation(SmartPropDefinitionParser parse, string className) : base(parse, className)
        {
            traceDirection = parse.Vector("m_vTraceDirection", -Vector3.UnitZ);
            directionSpace = parse.String("m_DirectionSpace");
            originOffset = parse.Float("m_flOriginOffset");
            traceLength = parse.Float("m_flTraceLength", 1000f);
        }

        protected override bool ComputeTrace(in SmartPropState state, SmartPropEvaluationContext ctx, out Vector3 start, out Vector3 end)
        {
            var direction = traceDirection.Evaluate(ctx);
            var space = SmartPropHelpers.ParseSpace(directionSpace.Evaluate(ctx), SmartPropSpace.Object);
            var worldDirection = SmartPropHelpers.DirectionToWorld(direction, space, state);

            if (worldDirection == Vector3.Zero)
            {
                start = default;
                end = default;
                return false;
            }

            worldDirection = Vector3.Normalize(worldDirection);
            var offset = originOffset.Evaluate(ctx);
            var length = traceLength.Evaluate(ctx);

            start = state.Transform.Translation + worldDirection * offset;
            end = start + worldDirection * length;
            return true;
        }
    }

    /// <summary>
    /// Traces from the current position to a target point.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropOperation_TraceToPoint">CSmartPropOperation_TraceToPoint</seealso>
    sealed class TraceToPointOperation : TraceOperation
    {
        private readonly VectorAttribute targetPoint;
        private readonly StringAttribute targetPointSpace;

        public TraceToPointOperation(SmartPropDefinitionParser parse) : base(parse, "CSmartPropOperation_TraceToPoint")
        {
            targetPoint = parse.Vector("m_TargetPoint");
            targetPointSpace = parse.String("m_TargetPointSpace");
        }

        protected override bool ComputeTrace(in SmartPropState state, SmartPropEvaluationContext ctx, out Vector3 start, out Vector3 end)
        {
            var target = targetPoint.Evaluate(ctx);
            var space = SmartPropHelpers.ParseSpace(targetPointSpace.Evaluate(ctx), SmartPropSpace.Object);

            start = state.Transform.Translation;
            end = SmartPropHelpers.PointToWorld(target, space, state);
            return true;
        }
    }

    /// <summary>
    /// Traces from the current position to the nearest point on a line segment.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropOperation_TraceToLine">CSmartPropOperation_TraceToLine</seealso>
    sealed class TraceToLineOperation : TraceOperation
    {
        private readonly VectorAttribute endPointA;
        private readonly StringAttribute endPointSpaceA;
        private readonly VectorAttribute endPointB;
        private readonly StringAttribute endPointSpaceB;

        public TraceToLineOperation(SmartPropDefinitionParser parse) : base(parse, "CSmartPropOperation_TraceToLine")
        {
            endPointA = parse.Vector("m_EndPointA");
            endPointSpaceA = parse.String("m_EndPointSpaceA");
            endPointB = parse.Vector("m_EndPointB");
            endPointSpaceB = parse.String("m_EndPointSpaceB");
        }

        protected override bool ComputeTrace(in SmartPropState state, SmartPropEvaluationContext ctx, out Vector3 start, out Vector3 end)
        {
            var a = SmartPropHelpers.PointToWorld(endPointA.Evaluate(ctx), SmartPropHelpers.ParseSpace(endPointSpaceA.Evaluate(ctx), SmartPropSpace.Object), state);
            var b = SmartPropHelpers.PointToWorld(endPointB.Evaluate(ctx), SmartPropHelpers.ParseSpace(endPointSpaceB.Evaluate(ctx), SmartPropSpace.Object), state);
            var position = state.Transform.Translation;
            var ab = b - a;
            var t = ab.LengthSquared() > 1e-6f ? Math.Clamp(Vector3.Dot(position - a, ab) / ab.LengthSquared(), 0f, 1f) : 0f;

            start = position;
            end = a + ab * t;
            return true;
        }
    }
}
