using ValveResourceFormat.ResourceTypes.SmartProps.Criteria;

namespace ValveResourceFormat.ResourceTypes.SmartProps.Elements
{
    /// <summary>
    /// Fills a line segment with children according to their linear-length criteria: optional
    /// start/end caps, fill items picked largest-first, randomly or in order, and leftover space
    /// resolved by the scale mode.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropElement_FitOnLine">CSmartPropElement_FitOnLine</seealso>
    sealed class FitOnLineElement : SmartPropElement
    {
        private const int MaxLoopInstances = 4096;

        private readonly StringAttribute pointSpace;
        private readonly VectorAttribute start;
        private readonly VectorAttribute end;
        private readonly BoolAttribute orientAlongLine;
        private readonly VectorAttribute upDirection;
        private readonly StringAttribute upDirectionSpace;
        private readonly BoolAttribute prioritizeUp;
        private readonly StringAttribute pickMode;
        private readonly StringAttribute scaleMode;

        private readonly record struct FitItem(int ChildIndex, float Length, bool AllowScale, float MinLength, float MaxLength, bool IsStartCap, bool IsEndCap, float Weight);

        public FitOnLineElement(SmartPropDefinitionParser parse) : base(parse)
        {
            pointSpace = parse.String("m_PointSpace");
            start = parse.Vector("m_vStart");
            end = parse.Vector("m_vEnd");
            orientAlongLine = parse.Bool("m_bOrientAlongLine", false);
            upDirection = parse.Vector("m_vUpDirection", Vector3.UnitZ);
            upDirectionSpace = parse.String("m_UpDirectionSpace");
            prioritizeUp = parse.Bool("m_bPrioritizeUp", false);
            pickMode = parse.String("m_nPickMode", "LARGEST_FIRST");
            scaleMode = parse.String("m_nScaleMode", "NONE");
        }

        private FitItem? ReadFitItem(int childIndex, SmartPropEvaluationContext ctx)
        {
            var child = Children[childIndex];

            if (!child.IsEligible(ctx, out var weight))
            {
                return null;
            }

            var length = 0f;
            var allowScale = false;
            var minLength = 0f;
            var maxLength = float.MaxValue;
            var isStartCap = false;
            var isEndCap = false;

            foreach (var criterion in child.Criteria)
            {
                if (!criterion.Enabled.Evaluate(ctx))
                {
                    continue;
                }

                switch (criterion)
                {
                    case LinearLengthCriterion linearLength:
                        length = linearLength.Length.Evaluate(ctx);
                        allowScale = linearLength.AllowScale.Evaluate(ctx);

                        if (!linearLength.MinLength.IsMissing)
                        {
                            minLength = linearLength.MinLength.Evaluate(ctx);
                        }

                        if (!linearLength.MaxLength.IsMissing)
                        {
                            maxLength = linearLength.MaxLength.Evaluate(ctx);
                        }

                        break;

                    case EndCapCriterion endCap:
                        isStartCap = endCap.Start.Evaluate(ctx);
                        isEndCap = endCap.End.Evaluate(ctx);
                        break;

                    default:
                        break;
                }
            }

            return new FitItem(childIndex, length, allowScale, minLength, maxLength, isStartCap, isEndCap, weight);
        }

        protected override void OnEvaluate(ref SmartPropState state, SmartPropEvaluationContext ctx)
        {
            if (Children.Count == 0)
            {
                return;
            }

            var space = SmartPropHelpers.ParseSpace(pointSpace.Evaluate(ctx), SmartPropSpace.Element);
            var startWorld = SmartPropHelpers.PointToWorld(start.Evaluate(ctx), space, state);
            var endWorld = SmartPropHelpers.PointToWorld(end.Evaluate(ctx), space, state);

            var lineVector = endWorld - startWorld;
            var lineLength = lineVector.Length();

            if (lineLength < 1e-3f)
            {
                return;
            }

            var direction = lineVector / lineLength;

            Matrix4x4 frame;

            if (orientAlongLine.Evaluate(ctx))
            {
                var up = upDirection.Evaluate(ctx);
                var upSpace = SmartPropHelpers.ParseSpace(upDirectionSpace.Evaluate(ctx), SmartPropSpace.Element);
                var upWorld = SmartPropHelpers.DirectionToWorld(up, upSpace, state);
                var priorityUp = prioritizeUp.Evaluate(ctx);

                frame = SmartPropHelpers.BuildBasis(direction, upWorld, priorityUp) ?? Matrix4x4.Identity;
            }
            else
            {
                frame = state.Transform with { Translation = Vector3.Zero };
            }

            // Gather eligible children with their fitting metadata
            var items = new List<FitItem>(Children.Count);

            for (var i = 0; i < Children.Count; i++)
            {
                if (ReadFitItem(i, ctx) is FitItem item)
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
            var pick = pickMode.Evaluate(ctx)!.ToUpperInvariant();
            var fillStart = segments.Count;
            var orderIndex = 0;
            var iterations = 0;

            while (fillItems.Count > 0 && available - cursor > 1e-3f && iterations++ < MaxLoopInstances)
            {
                var remaining = available - cursor;
                FitItem? pickedItem = null;
                var scale = 1f;

                if (pick == "ALL_IN_ORDER")
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
                else if (pick == "RANDOM")
                {
                    var candidates = fillItems.FindAll(item =>
                        item.Length <= remaining + 1e-3f || (item.AllowScale && item.MinLength <= remaining));

                    if (candidates.Count == 0)
                    {
                        break;
                    }

                    var picked = candidates[SmartPropHelpers.PickWeightedIndex(candidates.Count, i => candidates[i].Weight, ctx.Random)];
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
            var scaling = scaleMode.Evaluate(ctx)!.ToUpperInvariant();
            var leftover = available - cursor;

            if (leftover > 1e-3f && segments.Count > fillStart)
            {
                if (scaling == "SCALE_END_TO_FIT")
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
                else if (scaling is "SCALE_EQUALLY" or "SCALE_MAXIMIZE")
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

                Children[item.ChildIndex].Evaluate(childState, ctx);

                position += item.Length * scale;
            }
        }
    }
}
