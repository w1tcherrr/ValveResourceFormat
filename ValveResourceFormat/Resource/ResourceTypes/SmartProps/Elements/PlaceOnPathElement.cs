using System.Linq;
using ValveResourceFormat.Serialization.KeyValues;
using ValveResourceFormat.ResourceTypes.SmartProps.Criteria;

namespace ValveResourceFormat.ResourceTypes.SmartProps.Elements
{
    /// <summary>
    /// Places children at regular intervals along a polyline path, oriented to the path
    /// direction. Children gated to control points are placed at the path's vertices instead.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropElement_PlaceOnPath">CSmartPropElement_PlaceOnPath</seealso>
    sealed class PlaceOnPathElement : SmartPropElement
    {
        private const int MaxLoopInstances = 4096;

        private readonly VectorAttribute[] defaultPath;
        private readonly BoolAttribute useProjectedDistance;
        private readonly FloatAttribute spacing;
        private readonly FloatAttribute offsetAlongPath;
        private readonly VectorAttribute pathOffset;
        private readonly VectorAttribute upDirection;
        private readonly StringAttribute upDirectionSpace;

        public PlaceOnPathElement(SmartPropDefinitionParser parse) : base(parse)
        {
            var pathNodes = parse.Data.GetArray("m_DefaultPath");
            defaultPath = pathNodes == null
                ? []
                : [.. pathNodes.Select(node => VectorAttribute.ParseNode(node, new RawComponents(Vector4.Zero, 3)))];

            useProjectedDistance = parse.Bool("m_bUseProjectedDistance", false);
            spacing = parse.Float("m_flSpacing", 1f);
            offsetAlongPath = parse.Float("m_flOffsetAlongPath");
            pathOffset = parse.Vector("m_vPathOffset");
            upDirection = parse.Vector("m_vUpDirection", Vector3.UnitZ);
            upDirectionSpace = parse.String("m_UpDirectionSpace");
        }

        protected override void OnEvaluate(ref SmartPropState state, SmartPropEvaluationContext ctx)
        {
            if (Children.Count == 0 || defaultPath.Length < 2)
            {
                return;
            }

            // Literal default path points are in object space per the schema description
            var points = new Vector3[defaultPath.Length];

            for (var i = 0; i < defaultPath.Length; i++)
            {
                var point = defaultPath[i].Evaluate(ctx);
                points[i] = SmartPropHelpers.PointToWorld(point, SmartPropSpace.Object, state);
            }

            var useProjected = useProjectedDistance.Evaluate(ctx);
            var segmentLengths = new float[points.Length - 1];
            var totalLength = 0f;

            for (var i = 0; i < points.Length - 1; i++)
            {
                var delta = points[i + 1] - points[i];

                if (useProjected)
                {
                    delta.Z = 0f;
                }

                segmentLengths[i] = delta.Length();
                totalLength += segmentLengths[i];
            }

            if (totalLength < 1e-3f)
            {
                return;
            }

            var step = Math.Max(spacing.Evaluate(ctx), 0.1f);
            var pathStart = offsetAlongPath.Evaluate(ctx);
            var pathOffsetRaw = pathOffset.Evaluate(ctx);
            var up = upDirection.Evaluate(ctx);
            var upSpace = SmartPropHelpers.ParseSpace(upDirectionSpace.Evaluate(ctx), SmartPropSpace.Object);
            var upWorld = SmartPropHelpers.DirectionToWorld(up, upSpace, state);
            var elementRotation = state.Transform with { Translation = Vector3.Zero };

            var positionCount = (int)((totalLength - pathStart) / step) + 1;
            positionCount = Math.Clamp(positionCount, 0, MaxLoopInstances);

            using var loop = ctx.EnterLoop(positionCount);

            for (var i = 0; i < positionCount; i++)
            {
                var distance = pathStart + i * step;
                SamplePolyline(points, segmentLengths, distance, out var position, out var tangent);

                var child = SelectPathChild(ctx, i, i == 0, i == positionCount - 1, controlPointPass: false);

                if (child == null)
                {
                    continue;
                }

                ctx.InstanceIndex = i;
                ctx.PathParameter = distance / totalLength;
                PlacePathInstance(child, position, tangent, upWorld, pathOffsetRaw, in elementRotation, in state, ctx);
            }

            // Children gated to CONTROL_POINTS are placed at the path's vertices instead
            ctx.InstanceCount = points.Length;

            for (var i = 0; i < points.Length; i++)
            {
                var child = SelectPathChild(ctx, i, i == 0, i == points.Length - 1, controlPointPass: true);

                if (child == null)
                {
                    continue;
                }

                var tangent = i < points.Length - 1
                    ? points[i + 1] - points[i]
                    : points[i] - points[i - 1];

                ctx.InstanceIndex = i;
                ctx.PathParameter = points.Length > 1 ? (double)i / (points.Length - 1) : 0.0;
                PlacePathInstance(child, points[i], tangent, upWorld, pathOffsetRaw, in elementRotation, in state, ctx);
            }
        }

        private static void SamplePolyline(Vector3[] points, float[] segmentLengths, float distance, out Vector3 position, out Vector3 tangent)
        {
            var remaining = Math.Max(distance, 0f);

            for (var i = 0; i < segmentLengths.Length; i++)
            {
                if (remaining <= segmentLengths[i] || i == segmentLengths.Length - 1)
                {
                    var t = segmentLengths[i] > 1e-6f ? Math.Min(remaining / segmentLengths[i], 1f) : 0f;
                    position = Vector3.Lerp(points[i], points[i + 1], t);
                    tangent = points[i + 1] - points[i];
                    return;
                }

                remaining -= segmentLengths[i];
            }

            position = points[^1];
            tangent = points[^1] - points[^2];
        }

        private SmartPropElement? SelectPathChild(SmartPropEvaluationContext ctx, int positionIndex, bool isStart, bool isEnd, bool controlPointPass)
        {
            foreach (var child in Children)
            {
                if (!child.IsEligible(ctx, out _))
                {
                    continue;
                }

                var admitted = !controlPointPass;
                var hasPathCriteria = false;

                foreach (var criterion in child.Criteria)
                {
                    if (criterion is not PathPositionCriterion pathPosition || !criterion.Enabled.Evaluate(ctx))
                    {
                        continue;
                    }

                    hasPathCriteria = true;

                    admitted = pathPosition.PlaceAtPositions switch
                    {
                        "NTH" or "EVERY_N" => !controlPointPass && IsNthPosition(pathPosition, ctx, positionIndex),
                        "START_AND_END" => !controlPointPass
                            && ((isStart && pathPosition.AllowAtStart.Evaluate(ctx))
                                || (isEnd && pathPosition.AllowAtEnd.Evaluate(ctx))),
                        "CONTROL_POINTS" => controlPointPass,
                        _ => !controlPointPass,
                    };
                }

                if (controlPointPass && !hasPathCriteria)
                {
                    continue;
                }

                if (admitted)
                {
                    return child;
                }
            }

            return null;
        }

        private static bool IsNthPosition(PathPositionCriterion criterion, SmartPropEvaluationContext ctx, int positionIndex)
        {
            var every = criterion.PlaceEveryNthPosition.EvaluateInt(ctx);
            var offset = criterion.NthPositionIndexOffset.EvaluateInt(ctx);

            return every > 0 && (positionIndex - offset) % every == 0 && positionIndex >= offset;
        }

        private static void PlacePathInstance(SmartPropElement child, Vector3 position, Vector3 tangent, Vector3 up, Vector3 pathOffset, in Matrix4x4 elementRotation, in SmartPropState state, SmartPropEvaluationContext ctx)
        {
            var frame = SmartPropHelpers.BuildBasis(tangent, up) ?? Matrix4x4.Identity;

            var childState = state;
            childState.Transform = elementRotation * frame * Matrix4x4.CreateTranslation(position);

            // 2D path offset displaces the instance sideways/vertically in its path frame
            if (pathOffset.X != 0f || pathOffset.Y != 0f)
            {
                childState.Transform = Matrix4x4.CreateTranslation(new Vector3(0f, pathOffset.X, pathOffset.Y)) * childState.Transform;
            }

            child.Evaluate(childState, ctx);
        }
    }
}
