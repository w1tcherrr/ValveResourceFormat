using ValveKeyValue;
using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.ResourceTypes.SmartProps
{
    /// <summary>
    /// Placement generators: elements that instance their children multiple times
    /// (loops, line fitting, paths, scattering, grids) and deformers.
    /// </summary>
    public static partial class SmartPropEvaluator
    {
        private const int MaxLoopInstances = 4096;

        private static void EvaluatePlaceMultiple(KVObject element, in SmartPropState state, SmartPropEvaluationContext ctx)
        {
            var count = Math.Min(SmartPropAttribute.GetInt(element, "m_nCount", ctx, 1), MaxLoopInstances);
            var stopExpression = element.GetStringProperty("m_Expression");

            using var loop = ctx.EnterLoop(count);

            for (var i = 0; i < count; i++)
            {
                ctx.InstanceIndex = i;

                if (!string.IsNullOrEmpty(stopExpression)
                    && SmartPropExpression.ToBool(SmartPropExpression.Evaluate(stopExpression, ctx)))
                {
                    break;
                }

                var instanceState = state;
                EvaluateChildren(element, ref instanceState, ctx);
            }
        }

        private readonly record struct FitItem(int ChildIndex, float Length, bool AllowScale, float MinLength, float MaxLength, bool IsStartCap, bool IsEndCap, float Weight);

        private static FitItem? ReadFitItem(KVObject child, int index, SmartPropEvaluationContext ctx)
        {
            if (!IsEligible(child, ctx, out var weight))
            {
                return null;
            }

            var length = 0f;
            var allowScale = false;
            var minLength = 0f;
            var maxLength = float.MaxValue;
            var isStartCap = false;
            var isEndCap = false;

            foreach (var (criterionClass, criterion) in EnabledCriteria(child, ctx))
            {
                switch (criterionClass)
                {
                    case "CSmartPropSelectionCriteria_LinearLength":
                        length = SmartPropAttribute.GetFloat(criterion, "m_flLength", ctx, 1f);
                        allowScale = SmartPropAttribute.GetBool(criterion, "m_bAllowScale", ctx);

                        if (criterion.TryGetValue("m_flMinLength", out _))
                        {
                            minLength = SmartPropAttribute.GetFloat(criterion, "m_flMinLength", ctx);
                        }

                        if (criterion.TryGetValue("m_flMaxLength", out _))
                        {
                            maxLength = SmartPropAttribute.GetFloat(criterion, "m_flMaxLength", ctx);
                        }

                        break;

                    case "CSmartPropSelectionCriteria_EndCap":
                        isStartCap = SmartPropAttribute.GetBool(criterion, "m_bStart", ctx, true);
                        isEndCap = SmartPropAttribute.GetBool(criterion, "m_bEnd", ctx, true);
                        break;

                    default:
                        break;
                }
            }

            return new FitItem(index, length, allowScale, minLength, maxLength, isStartCap, isEndCap, weight);
        }

        private static void EvaluateFitOnLine(KVObject element, in SmartPropState state, SmartPropEvaluationContext ctx)
        {
            var children = element.GetArray("m_Children");

            if (children == null || children.Count == 0)
            {
                return;
            }

            var pointSpace = GetSpace(element, "m_PointSpace", ctx, SmartPropSpace.Element);
            var startWorld = PointToWorld(SmartPropAttribute.GetVector3(element, "m_vStart", ctx), pointSpace, state);
            var endWorld = PointToWorld(SmartPropAttribute.GetVector3(element, "m_vEnd", ctx), pointSpace, state);

            var lineVector = endWorld - startWorld;
            var lineLength = lineVector.Length();

            if (lineLength < 1e-3f)
            {
                return;
            }

            var direction = lineVector / lineLength;

            Matrix4x4 frame;

            if (SmartPropAttribute.GetBool(element, "m_bOrientAlongLine", ctx))
            {
                var up = SmartPropAttribute.GetVector3(element, "m_vUpDirection", ctx, Vector3.UnitZ);
                var upSpace = GetSpace(element, "m_UpDirectionSpace", ctx, SmartPropSpace.Element);
                var upWorld = DirectionToWorld(up, upSpace, state);
                var prioritizeUp = SmartPropAttribute.GetBool(element, "m_bPrioritizeUp", ctx);

                frame = BuildBasis(direction, upWorld, prioritizeUp) ?? Matrix4x4.Identity;
            }
            else
            {
                frame = state.Transform with { Translation = Vector3.Zero };
            }

            // Gather eligible children with their fitting metadata
            var items = new List<FitItem>(children.Count);

            for (var i = 0; i < children.Count; i++)
            {
                if (ReadFitItem(children[i], i, ctx) is FitItem item)
                {
                    items.Add(item);
                }
            }

            if (items.Count == 0)
            {
                return;
            }

            var startCap = items.Find(item => item.IsStartCap);
            var endCap = items.Find(item => item.IsEndCap);
            var fillItems = items.FindAll(item => item is { IsStartCap: false, IsEndCap: false, Length: > 1e-3f });

            var segments = new List<(FitItem Item, float Scale, bool IsEndCapSlot)>();
            var cursor = 0f;
            var endReserve = 0f;

            if (startCap.IsStartCap)
            {
                segments.Add((startCap, 1f, false));
                cursor += startCap.Length;
            }

            if (endCap.IsEndCap)
            {
                endReserve = endCap.Length;
            }

            var available = Math.Max(lineLength - endReserve, 0f);
            var pickMode = SmartPropAttribute.GetString(element, "m_nPickMode", ctx, "LARGEST_FIRST")!.ToUpperInvariant();
            var fillStart = segments.Count;
            var orderIndex = 0;
            var iterations = 0;

            while (fillItems.Count > 0 && available - cursor > 1e-3f && iterations++ < MaxLoopInstances)
            {
                var remaining = available - cursor;
                FitItem? pickedItem = null;
                var scale = 1f;

                if (pickMode == "ALL_IN_ORDER")
                {
                    var item = fillItems[orderIndex % fillItems.Count];
                    orderIndex++;

                    if (item.Length <= remaining + 1e-3f)
                    {
                        pickedItem = item;
                    }
                    else if (item.AllowScale && item.MinLength <= remaining)
                    {
                        pickedItem = item;
                        scale = remaining / item.Length;
                    }
                    else
                    {
                        break;
                    }
                }
                else if (pickMode == "RANDOM")
                {
                    var candidates = fillItems.FindAll(item =>
                        item.Length <= remaining + 1e-3f || (item.AllowScale && item.MinLength <= remaining));

                    if (candidates.Count == 0)
                    {
                        break;
                    }

                    var picked = candidates[PickWeightedIndex(candidates.Count, i => candidates[i].Weight, ctx.Random)];
                    pickedItem = picked;

                    if (picked.Length > remaining)
                    {
                        scale = remaining / picked.Length;
                    }
                }
                else // LARGEST_FIRST
                {
                    var bestNatural = default(FitItem?);
                    var bestScalable = default(FitItem?);

                    foreach (var item in fillItems)
                    {
                        if (item.Length <= remaining + 1e-3f)
                        {
                            if (bestNatural == null || item.Length > bestNatural.Value.Length)
                            {
                                bestNatural = item;
                            }
                        }
                        else if (item.AllowScale && item.MinLength <= remaining)
                        {
                            if (bestScalable == null || item.Length < bestScalable.Value.Length)
                            {
                                bestScalable = item;
                            }
                        }
                    }

                    if (bestNatural != null)
                    {
                        pickedItem = bestNatural;
                    }
                    else if (bestScalable != null)
                    {
                        pickedItem = bestScalable;
                        scale = remaining / bestScalable.Value.Length;
                    }
                    else
                    {
                        break;
                    }
                }

                if (pickedItem == null)
                {
                    break;
                }

                segments.Add((pickedItem.Value, scale, false));
                cursor += pickedItem.Value.Length * scale;
            }

            // Resolve the leftover gap according to the scale mode
            var scaleMode = SmartPropAttribute.GetString(element, "m_nScaleMode", ctx, "NONE")!.ToUpperInvariant();
            var leftover = available - cursor;

            if (leftover > 1e-3f && segments.Count > fillStart)
            {
                if (scaleMode == "SCALE_END_TO_FIT")
                {
                    for (var i = segments.Count - 1; i >= fillStart; i--)
                    {
                        var (item, scale, isEndCapSlot) = segments[i];

                        if (!item.AllowScale)
                        {
                            continue;
                        }

                        var newLength = Math.Clamp(item.Length * scale + leftover, item.MinLength, item.MaxLength);
                        segments[i] = (item, newLength / item.Length, isEndCapSlot);
                        break;
                    }
                }
                else if (scaleMode is "SCALE_EQUALLY" or "SCALE_MAXIMIZE")
                {
                    var scalableLength = 0f;

                    for (var i = fillStart; i < segments.Count; i++)
                    {
                        if (segments[i].Item.AllowScale)
                        {
                            scalableLength += segments[i].Item.Length * segments[i].Scale;
                        }
                    }

                    if (scalableLength > 1e-3f)
                    {
                        var factor = 1f + leftover / scalableLength;

                        for (var i = fillStart; i < segments.Count; i++)
                        {
                            var (item, scale, isEndCapSlot) = segments[i];

                            if (!item.AllowScale)
                            {
                                continue;
                            }

                            var newLength = Math.Clamp(item.Length * scale * factor, item.MinLength, item.MaxLength);
                            segments[i] = (item, newLength / item.Length, isEndCapSlot);
                        }
                    }
                }
            }

            // The end cap occupies the reserved slot at the far end of the line; authors flip it
            // themselves with modifiers when the model needs to face inward
            if (endCap.IsEndCap)
            {
                segments.Add((endCap, 1f, true));
            }

            using var loop = ctx.EnterLoop(segments.Count);
            ctx.LineLength = lineLength;

            var position = 0f;

            for (var i = 0; i < segments.Count; i++)
            {
                var (item, scale, isEndCapSlot) = segments[i];

                if (isEndCapSlot)
                {
                    position = available;
                }

                var childState = state;
                childState.Transform = frame * Matrix4x4.CreateTranslation(startWorld + direction * position);

                ctx.InstanceIndex = i;
                ctx.LinearScale = scale;

                var child = children[item.ChildIndex];
                EvaluateElement(child, child.GetStringProperty("_class", string.Empty), childState, ctx);

                position += item.Length * scale;
            }
        }

        private static void EvaluatePlaceOnPath(KVObject element, in SmartPropState state, SmartPropEvaluationContext ctx)
        {
            var children = element.GetArray("m_Children");
            var pathNodes = element.GetArray("m_DefaultPath");

            if (children == null || children.Count == 0 || pathNodes == null || pathNodes.Count < 2)
            {
                return;
            }

            // Literal default path points are in object space per the schema description
            var points = new Vector3[pathNodes.Count];

            for (var i = 0; i < pathNodes.Count; i++)
            {
                var point = SmartPropAttribute.ToVector3(SmartPropAttribute.Resolve(pathNodes[i], ctx));
                points[i] = PointToWorld(point, SmartPropSpace.Object, state);
            }

            var useProjected = SmartPropAttribute.GetBool(element, "m_bUseProjectedDistance", ctx);
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

            var spacing = Math.Max(SmartPropAttribute.GetFloat(element, "m_flSpacing", ctx, 1f), 0.1f);
            var offsetAlongPath = SmartPropAttribute.GetFloat(element, "m_flOffsetAlongPath", ctx);
            var pathOffsetRaw = SmartPropAttribute.GetVector3(element, "m_vPathOffset", ctx);
            var up = SmartPropAttribute.GetVector3(element, "m_vUpDirection", ctx, Vector3.UnitZ);
            var upSpace = GetSpace(element, "m_UpDirectionSpace", ctx, SmartPropSpace.Object);
            var upWorld = DirectionToWorld(up, upSpace, state);
            var elementRotation = state.Transform with { Translation = Vector3.Zero };

            var positionCount = (int)((totalLength - offsetAlongPath) / spacing) + 1;
            positionCount = Math.Clamp(positionCount, 0, MaxLoopInstances);

            using var loop = ctx.EnterLoop(positionCount);

            for (var i = 0; i < positionCount; i++)
            {
                var distance = offsetAlongPath + i * spacing;
                SamplePolyline(points, segmentLengths, distance, out var position, out var tangent);

                var child = SelectPathChild(children, ctx, i, i == 0, i == positionCount - 1, controlPointPass: false);

                if (child == null)
                {
                    continue;
                }

                ctx.InstanceIndex = i;
                ctx.PathParameter = distance / totalLength;
                PlacePathInstance(child, position, tangent, upWorld, pathOffsetRaw, elementRotation, state, ctx);
            }

            // Children gated to CONTROL_POINTS are placed at the path's vertices instead
            ctx.InstanceCount = points.Length;

            for (var i = 0; i < points.Length; i++)
            {
                var child = SelectPathChild(children, ctx, i, i == 0, i == points.Length - 1, controlPointPass: true);

                if (child == null)
                {
                    continue;
                }

                var tangent = i < points.Length - 1
                    ? points[i + 1] - points[i]
                    : points[i] - points[i - 1];

                ctx.InstanceIndex = i;
                ctx.PathParameter = points.Length > 1 ? (double)i / (points.Length - 1) : 0.0;
                PlacePathInstance(child, points[i], tangent, upWorld, pathOffsetRaw, elementRotation, state, ctx);
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

        private static KVObject? SelectPathChild(IReadOnlyList<KVObject> children, SmartPropEvaluationContext ctx, int positionIndex, bool isStart, bool isEnd, bool controlPointPass)
        {
            foreach (var child in children)
            {
                if (!IsEligible(child, ctx, out _))
                {
                    continue;
                }

                var admitted = !controlPointPass;
                var hasPathCriteria = false;

                foreach (var (criterionClass, criterion) in EnabledCriteria(child, ctx))
                {
                    if (criterionClass != "CSmartPropSelectionCriteria_PathPosition")
                    {
                        continue;
                    }

                    hasPathCriteria = true;
                    var placeAt = criterion.GetStringProperty("m_PlaceAtPositions", "ALL").ToUpperInvariant();

                    admitted = placeAt switch
                    {
                        "NTH" or "EVERY_N" => !controlPointPass && IsNthPosition(criterion, ctx, positionIndex),
                        "START_AND_END" => !controlPointPass
                            && ((isStart && SmartPropAttribute.GetBool(criterion, "m_bAllowAtStart", ctx, true))
                                || (isEnd && SmartPropAttribute.GetBool(criterion, "m_bAllowAtEnd", ctx, true))),
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

        private static bool IsNthPosition(KVObject criterion, SmartPropEvaluationContext ctx, int positionIndex)
        {
            var every = SmartPropAttribute.GetInt(criterion, "m_nPlaceEveryNthPosition", ctx, 2);
            var offset = SmartPropAttribute.GetInt(criterion, "m_nNthPositionIndexOffset", ctx);

            return every > 0 && (positionIndex - offset) % every == 0 && positionIndex >= offset;
        }

        private static void PlacePathInstance(KVObject child, Vector3 position, Vector3 tangent, Vector3 up, Vector3 pathOffset, in Matrix4x4 elementRotation, in SmartPropState state, SmartPropEvaluationContext ctx)
        {
            var frame = BuildBasis(tangent, up) ?? Matrix4x4.Identity;

            var childState = state;
            childState.Transform = elementRotation * frame * Matrix4x4.CreateTranslation(position);

            // 2D path offset displaces the instance sideways/vertically in its path frame
            if (pathOffset.X != 0f || pathOffset.Y != 0f)
            {
                childState.Transform = Matrix4x4.CreateTranslation(new Vector3(0f, pathOffset.X, pathOffset.Y)) * childState.Transform;
            }

            EvaluateElement(child, child.GetStringProperty("_class", string.Empty), childState, ctx);
        }

        private static void EvaluatePlaceInSphere(KVObject element, in SmartPropState state, SmartPropEvaluationContext ctx)
        {
            var children = element.GetArray("m_Children");

            if (children == null || children.Count == 0)
            {
                return;
            }

            var countMin = SmartPropAttribute.GetInt(element, "m_nCountMin", ctx, 1);
            var countMax = SmartPropAttribute.GetInt(element, "m_nCountMax", ctx, 1);
            var count = Math.Min(ctx.Random.RandomInt(Math.Min(countMin, countMax), Math.Max(countMin, countMax)), MaxLoopInstances);

            var inner = SmartPropAttribute.GetFloat(element, "m_flPositionRadiusInner", ctx);
            var outer = SmartPropAttribute.GetFloat(element, "m_flPositionRadiusOuter", ctx);
            var circle = SmartPropAttribute.GetString(element, "m_PlacementMode", ctx, "SPHERE")!.Equals("CIRCLE", StringComparison.OrdinalIgnoreCase);
            var regular = SmartPropAttribute.GetString(element, "m_DistributionMode", ctx, "RANDOM")!.Equals("REGULAR", StringComparison.OrdinalIgnoreCase);
            var randomness = Math.Clamp(SmartPropAttribute.GetFloat(element, "m_flRandomness", ctx), 0f, 1f);
            var planeUp = SmartPropAttribute.GetVector3(element, "m_vPlaneUpDirection", ctx, Vector3.UnitZ);
            var align = SmartPropAttribute.GetBool(element, "m_bAlignOrientation", ctx);
            var alignDirection = SmartPropAttribute.GetVector3(element, "m_vAlignDirection", ctx, Vector3.UnitZ);

            var planeBasis = BuildBasis(Vector3.UnitX, planeUp == Vector3.Zero ? Vector3.UnitZ : planeUp, prioritizeUp: true) ?? Matrix4x4.Identity;

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
                            + randomness * ctx.Random.RandomFloat(-MathF.Tau, MathF.Tau) / (2f * Math.Max(count, 1));
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
                    var theta = i * 2.399963f + randomness * ctx.Random.RandomFloat(-0.5f, 0.5f);
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

                var radius = regular && randomness == 0f
                    ? (inner + outer) * 0.5f
                    : ctx.Random.RandomFloat(Math.Min(inner, outer), Math.Max(inner, outer));

                var childState = state;
                ApplyTranslate(ref childState, direction * radius, SmartPropSpace.Element);

                if (align && alignDirection != Vector3.Zero && direction != Vector3.Zero)
                {
                    var worldRadial = Vector3.TransformNormal(direction, state.Transform);
                    var worldAlign = Vector3.TransformNormal(alignDirection, childState.Transform);
                    var rotation = FromToRotation(worldAlign, worldRadial);
                    childState.Transform = childState.Transform with { Translation = Vector3.Zero } * rotation;
                    childState.Transform.Translation = Vector3.Transform(direction * radius * state.Scale, state.Transform);
                }

                ctx.InstanceIndex = i;
                EvaluateChildren(element, ref childState, ctx);
            }
        }

        private static Matrix4x4 FromToRotation(Vector3 from, Vector3 to)
        {
            from = Vector3.Normalize(from);
            to = Vector3.Normalize(to);
            var axis = Vector3.Cross(from, to);
            var axisLength = axis.Length();

            if (axisLength < 1e-6f)
            {
                return Vector3.Dot(from, to) > 0f
                    ? Matrix4x4.Identity
                    : Matrix4x4.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI);
            }

            var angle = MathF.Atan2(axisLength, Vector3.Dot(from, to));
            return Matrix4x4.CreateFromAxisAngle(axis / axisLength, angle);
        }

        private static void EvaluateLayout2DGrid(KVObject element, in SmartPropState state, SmartPropEvaluationContext ctx)
        {
            var children = element.GetArray("m_Children");

            if (children == null || children.Count == 0)
            {
                return;
            }

            var width = SmartPropAttribute.GetFloat(element, "m_flWidth", ctx, 100f);
            var length = SmartPropAttribute.GetFloat(element, "m_flLength", ctx, 100f);
            var vertical = SmartPropAttribute.GetBool(element, "m_bVerticalLength", ctx);

            // FILL derives counts from the dimensions and spacing; SEGMENT uses the authored counts
            var fill = SmartPropAttribute.GetString(element, "m_GridArrangement", ctx, "SEGMENT")!.Equals("FILL", StringComparison.OrdinalIgnoreCase);

            ResolveGridAxis(fill ? 0 : SmartPropAttribute.GetInt(element, "m_nCountW", ctx, 5), SmartPropAttribute.GetFloat(element, "m_flSpacingWidth", ctx, 20f), width, out var countW, out var stepW);
            ResolveGridAxis(fill ? 0 : SmartPropAttribute.GetInt(element, "m_nCountL", ctx, 5), SmartPropAttribute.GetFloat(element, "m_flSpacingLength", ctx, 20f), length, out var countL, out var stepL);

            var corner = SmartPropAttribute.GetString(element, "m_GridOriginMode", ctx, "CENTER")!.Equals("CORNER", StringComparison.OrdinalIgnoreCase);
            var startW = corner ? 0f : -(countW - 1) * stepW * 0.5f;
            var startL = corner ? 0f : -(countL - 1) * stepL * 0.5f;

            // Alternate shift amounts are fractions of the cell step (schema default width shift is 0.5)
            var alternateShift = SmartPropAttribute.GetBool(element, "m_bAlternateShift", ctx);
            var shiftW = alternateShift ? SmartPropAttribute.GetFloat(element, "m_flAlternateShiftWidth", ctx, 0.5f) * stepW : 0f;
            var shiftL = alternateShift ? SmartPropAttribute.GetFloat(element, "m_flAlternateShiftLength", ctx) * stepL : 0f;

            using var loop = ctx.EnterLoop(countW * countL);
            var instance = 0;

            for (var row = 0; row < countL; row++)
            {
                for (var column = 0; column < countW; column++)
                {
                    var w = startW + column * stepW + (row % 2 == 1 ? shiftW : 0f);
                    var l = startL + row * stepL + (column % 2 == 1 ? shiftL : 0f);
                    var offset = vertical ? new Vector3(w, 0f, l) : new Vector3(w, l, 0f);

                    var childState = state;
                    ApplyTranslate(ref childState, offset, SmartPropSpace.Element);

                    ctx.InstanceIndex = instance++;
                    EvaluateChildren(element, ref childState, ctx);
                }
            }
        }

        private static void ResolveGridAxis(int count, float spacing, float dimension, out int resolvedCount, out float step)
        {
            if (count > 0)
            {
                resolvedCount = Math.Min(count, 1024);
                step = spacing > 0f ? spacing : (resolvedCount > 1 ? dimension / (resolvedCount - 1) : 0f);
            }
            else if (spacing > 0f && dimension > 0f)
            {
                resolvedCount = Math.Min((int)(dimension / spacing) + 1, 1024);
                step = spacing;
            }
            else
            {
                resolvedCount = 1;
                step = 0f;
            }
        }

        private static void EvaluateDeformer(KVObject element, in SmartPropState state, SmartPropEvaluationContext ctx, bool bend)
        {
            var firstPlacement = ctx.Result.Placements.Count;
            var childState = state;
            EvaluateChildren(element, ref childState, ctx);

            if (!SmartPropAttribute.GetBool(element, "m_bDeformationEnabled", ctx, true))
            {
                return;
            }

            var lastPlacement = ctx.Result.Placements.Count;

            if (lastPlacement == firstPlacement)
            {
                return;
            }

            if (bend)
            {
                ApplyBend(element, state, ctx, firstPlacement, lastPlacement);
            }
            else
            {
                ApplyMidpoint(element, state, ctx, firstPlacement, lastPlacement);
            }
        }

        /// <summary>
        /// Rigid approximation of the bend deformer: placements are repositioned and reoriented
        /// along the bend arc, but vertices are not warped.
        /// </summary>
        private static void ApplyBend(KVObject element, in SmartPropState state, SmartPropEvaluationContext ctx, int firstPlacement, int lastPlacement)
        {
            var bendAngle = SmartPropAttribute.GetFloat(element, "m_flBendAngle", ctx);

            if (Math.Abs(bendAngle) < 1e-3f)
            {
                return;
            }

            var origin = SmartPropAttribute.GetVector3(element, "m_vOrigin", ctx);
            var angles = SmartPropAttribute.GetVector3(element, "m_vAngles", ctx);
            var size = SmartPropAttribute.GetVector3(element, "m_vSize", ctx);
            var bendPoint = Math.Clamp(SmartPropAttribute.GetFloat(element, "m_flBendPoint", ctx), 0f, 1f);
            var bendRadius = SmartPropAttribute.GetFloat(element, "m_flBendRadius", ctx);

            var angleRadians = float.DegreesToRadians(bendAngle);
            var bendStart = bendPoint * size.X;
            var arcLength = Math.Max(size.X - bendStart, 1e-3f);
            var radius = bendRadius > 0f ? bendRadius : arcLength / Math.Abs(angleRadians);

            if (radius < 1e-3f)
            {
                return;
            }

            var sign = MathF.Sign(angleRadians);

            var deformerFrame = EntityTransformHelper.CreateRotationMatrixFromEulerAngles(angles)
                * Matrix4x4.CreateTranslation(origin)
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

        /// <summary>
        /// Rigid approximation of the midpoint deformer: placements near the midpoint are displaced
        /// by the offset with distance falloff.
        /// </summary>
        private static void ApplyMidpoint(KVObject element, in SmartPropState state, SmartPropEvaluationContext ctx, int firstPlacement, int lastPlacement)
        {
            var offset = SmartPropAttribute.GetVector3(element, "m_vOffset", ctx);
            var radius = SmartPropAttribute.GetFloat(element, "m_fRadius", ctx, 64f);

            if (offset == Vector3.Zero || radius < 1e-3f)
            {
                return;
            }

            var start = PointToWorld(SmartPropAttribute.GetVector3(element, "m_vStart", ctx), SmartPropSpace.Element, state);
            var end = PointToWorld(SmartPropAttribute.GetVector3(element, "m_vEnd", ctx), SmartPropSpace.Element, state);
            var falloff = Math.Max(SmartPropAttribute.GetFloat(element, "m_fFalloff", ctx, 1f), 0.01f);
            var midpoint = (start + end) * 0.5f;
            var worldOffset = Vector3.TransformNormal(offset * state.Scale, state.Transform);

            for (var i = firstPlacement; i < lastPlacement; i++)
            {
                var placement = ctx.Result.Placements[i];
                var distance = Vector3.Distance(placement.Transform.Translation, midpoint);

                if (distance >= radius)
                {
                    continue;
                }

                var weight = MathF.Pow(1f - distance / radius, falloff);
                var transform = placement.Transform;
                transform.Translation += worldOffset * weight;
                placement.Transform = transform;
            }
        }

        private static bool ApplyTraceNoHit(KVObject modifier, string className, ref SmartPropState state, SmartPropEvaluationContext ctx)
        {
            var noHitResult = SmartPropAttribute.GetString(modifier, "m_nNoHitResult", ctx, "NOTHING")!.ToUpperInvariant();

            if (noHitResult == "DISCARD")
            {
                // Discarding on no-hit would delete every trace-gated element in a world-less
                // viewer (foliage scatters trace against ground), so keep the element instead
                ctx.Warn(SmartPropDiagnosticCode.NeedsWorldContext, "trace-discard", "Trace discard-on-miss ignored because there is no world geometry");
                return true;
            }

            if (noHitResult is not ("MOVE_TO_START" or "MOVE_TO_END"))
            {
                return true;
            }

            Vector3 start;
            Vector3 end;
            var position = state.Transform.Translation;

            switch (className)
            {
                case "CSmartPropOperation_TraceInDirection":
                case "CSmartPropOperation_Trace":
                    {
                        var direction = SmartPropAttribute.GetVector3(modifier, "m_vTraceDirection", ctx, -Vector3.UnitZ);
                        var space = GetSpace(modifier, "m_DirectionSpace", ctx, SmartPropSpace.Object);
                        var worldDirection = DirectionToWorld(direction, space, state);

                        if (worldDirection == Vector3.Zero)
                        {
                            return true;
                        }

                        worldDirection = Vector3.Normalize(worldDirection);
                        var originOffset = SmartPropAttribute.GetFloat(modifier, "m_flOriginOffset", ctx);
                        var traceLength = SmartPropAttribute.GetFloat(modifier, "m_flTraceLength", ctx, 1000f);

                        start = position + worldDirection * originOffset;
                        end = start + worldDirection * traceLength;
                        break;
                    }

                case "CSmartPropOperation_TraceToPoint":
                    {
                        var target = SmartPropAttribute.GetVector3(modifier, "m_TargetPoint", ctx);
                        var space = GetSpace(modifier, "m_TargetPointSpace", ctx, SmartPropSpace.Object);
                        start = position;
                        end = PointToWorld(target, space, state);
                        break;
                    }

                case "CSmartPropOperation_TraceToLine":
                    {
                        var a = PointToWorld(SmartPropAttribute.GetVector3(modifier, "m_EndPointA", ctx), GetSpace(modifier, "m_EndPointSpaceA", ctx, SmartPropSpace.Object), state);
                        var b = PointToWorld(SmartPropAttribute.GetVector3(modifier, "m_EndPointB", ctx), GetSpace(modifier, "m_EndPointSpaceB", ctx, SmartPropSpace.Object), state);
                        var ab = b - a;
                        var t = ab.LengthSquared() > 1e-6f ? Math.Clamp(Vector3.Dot(position - a, ab) / ab.LengthSquared(), 0f, 1f) : 0f;
                        start = position;
                        end = a + ab * t;
                        break;
                    }

                default:
                    return true;
            }

            state.Transform.Translation = noHitResult == "MOVE_TO_START" ? start : end;
            return true;
        }

        private static void ApplyComputeOperation(KVObject modifier, string className, in SmartPropState state, SmartPropEvaluationContext ctx)
        {
            var outputVariable = modifier.GetStringProperty("m_OutputVariableName");

            if (string.IsNullOrEmpty(outputVariable))
            {
                return;
            }

            var outputSpace = GetSpace(modifier, "m_OutputCoordinateSpace", ctx, SmartPropSpace.Object);
            var localState = state;

            Vector3 Point(string field, string spaceField)
            {
                var space = GetSpace(modifier, spaceField, ctx, SmartPropSpace.Object);

                // A missing input position means the current element position
                return modifier.TryGetValue(field, out _)
                    ? PointToWorld(SmartPropAttribute.GetVector3(modifier, field, ctx), space, localState)
                    : localState.Transform.Translation;
            }

            Vector3 Direction(string field, string spaceField)
            {
                var space = GetSpace(modifier, spaceField, ctx, SmartPropSpace.Object);
                return DirectionToWorld(SmartPropAttribute.GetVector3(modifier, field, ctx), space, localState);
            }

            switch (className)
            {
                case "CSmartPropOperation_ComputeDistance3D":
                    {
                        var a = Point("m_InputPositionA", "m_CoordinateSpaceA");
                        var b = Point("m_InputPositionB", "m_CoordinateSpaceB");
                        ctx.SetVariable(outputVariable, (double)Vector3.Distance(a, b));
                        break;
                    }

                case "CSmartPropOperation_ComputeDotProduct3D":
                    {
                        var a = Direction("m_InputVectorA", "m_CoordinateSpaceA");
                        var b = Direction("m_InputVectorB", "m_CoordinateSpaceB");
                        ctx.SetVariable(outputVariable, (double)Vector3.Dot(a, b));
                        break;
                    }

                case "CSmartPropOperation_ComputeCrossProduct3D":
                    {
                        var a = Direction("m_InputVectorA", "m_CoordinateSpaceA");
                        var b = Direction("m_InputVectorB", "m_CoordinateSpaceB");
                        ctx.SetVariable(outputVariable, DirectionToSpace(Vector3.Cross(a, b), outputSpace, state));
                        break;
                    }

                case "CSmartPropOperation_ComputeVectorBetweenPoints3D":
                    {
                        var a = Point("m_InputPositionA", "m_CoordinateSpaceA");
                        var b = Point("m_InputPositionB", "m_CoordinateSpaceB");
                        ctx.SetVariable(outputVariable, DirectionToSpace(b - a, outputSpace, state));
                        break;
                    }

                case "CSmartPropOperation_ComputeNormalizedVector3D":
                    {
                        var a = Direction("m_InputVectorA", "m_CoordinateSpaceA");
                        var normalized = SmartPropAttribute.GetBool(modifier, "m_bNormalized", ctx, true) && a != Vector3.Zero
                            ? Vector3.Normalize(a)
                            : a;
                        ctx.SetVariable(outputVariable, DirectionToSpace(normalized, outputSpace, state));
                        break;
                    }

                case "CSmartPropOperation_ComputeProjectVector3D":
                    {
                        var a = Direction("m_InputVectorA", "m_CoordinateSpaceA");
                        var b = Direction("m_InputVectorB", "m_CoordinateSpaceB");

                        if (b == Vector3.Zero)
                        {
                            break;
                        }

                        var normal = Vector3.Normalize(b);
                        var projected = SmartPropAttribute.GetBool(modifier, "m_bPlane", ctx)
                            ? a - Vector3.Dot(a, normal) * normal
                            : Vector3.Dot(a, normal) * normal;

                        ctx.SetVariable(outputVariable, DirectionToSpace(projected, outputSpace, state));
                        break;
                    }

                default:
                    break;
            }
        }

        private static void ApplyRotateTowards(KVObject modifier, ref SmartPropState state, SmartPropEvaluationContext ctx)
        {
            var origin = PointToWorld(
                SmartPropAttribute.GetVector3(modifier, "m_vOriginPos", ctx),
                GetSpace(modifier, "m_OriginSpace", ctx, SmartPropSpace.Object),
                state);
            var target = PointToWorld(
                SmartPropAttribute.GetVector3(modifier, "m_vTargetPos", ctx),
                GetSpace(modifier, "m_TargetSpace", ctx, SmartPropSpace.Object),
                state);

            var direction = target - origin;

            if (direction.LengthSquared() < 1e-6f)
            {
                return;
            }

            var up = modifier.TryGetValue("m_vUpPos", out _)
                ? Vector3.Normalize(PointToWorld(
                    SmartPropAttribute.GetVector3(modifier, "m_vUpPos", ctx, Vector3.UnitZ),
                    GetSpace(modifier, "m_UpSpace", ctx, SmartPropSpace.Object),
                    state) - origin)
                : Vector3.UnitZ;

            if (BuildBasis(direction, up) is not Matrix4x4 targetRotation)
            {
                return;
            }

            var weight = Math.Clamp(SmartPropAttribute.GetFloat(modifier, "m_flWeight", ctx, 1f), 0f, 1f);
            var current = Quaternion.CreateFromRotationMatrix(state.Transform with { Translation = Vector3.Zero });
            var desired = Quaternion.CreateFromRotationMatrix(targetRotation);
            var blended = Matrix4x4.CreateFromQuaternion(Quaternion.Slerp(current, desired, weight));

            blended.Translation = state.Transform.Translation;
            state.Transform = blended;
        }
    }
}
