using ValveKeyValue;
using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.ResourceTypes.SmartProps
{
    /// <summary>Coordinate space of a transform operation or authored point.</summary>
    internal enum SmartPropSpace
    {
        /// <summary>The frame the current document was placed in. During evaluation there is no
        /// true world, so WORLD resolves here too.</summary>
        Object,
        /// <summary>The current element transform.</summary>
        Element,
    }

    /// <summary>
    /// Evaluates a smart prop document (<c>CSmartPropRoot</c> tree) into a flat list of model placements.
    /// Prefer <see cref="SmartPropDocument"/> when evaluating the same document repeatedly.
    /// </summary>
    public static partial class SmartPropEvaluator
    {
        /// <summary>
        /// Loads and evaluates a smart prop resource in one step.
        /// </summary>
        public static SmartPropEvaluationResult Evaluate(SmartProp smartProp, SmartPropEvaluationOptions options)
            => SmartPropDocument.Load(smartProp).Evaluate(options);

        /// <summary>
        /// Loads and evaluates a <c>CSmartPropRoot</c> KV3 tree in one step.
        /// </summary>
        public static SmartPropEvaluationResult Evaluate(KVObject root, SmartPropEvaluationOptions options)
            => SmartPropDocument.Load(root).Evaluate(options);

        /// <summary>
        /// Declares the document's variables into the context environment with their defaults.
        /// Existing entries win (used when a nested document shares the parent environment).
        /// </summary>
        internal static void DeclareVariables(KVObject root, SmartPropEvaluationContext ctx)
        {
            var variables = root.GetArray("m_Variables");

            if (variables == null)
            {
                return;
            }

            foreach (var variable in variables)
            {
                var name = variable.GetStringProperty("m_VariableName");

                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                var type = GetParameterType(variable.GetStringProperty("_class", string.Empty));
                ctx.Variables.TryAdd(name, GetVariableDefault(variable, type, ctx));
            }
        }

        internal static SmartPropParameterType GetParameterType(string className) => className switch
        {
            "CSmartPropVariable_Float" => SmartPropParameterType.Float,
            "CSmartPropVariable_Int" => SmartPropParameterType.Int,
            "CSmartPropVariable_Bool" => SmartPropParameterType.Bool,
            "CSmartPropVariable_Color" => SmartPropParameterType.Color,
            "CSmartPropVariable_Vector2D" => SmartPropParameterType.Vector2,
            "CSmartPropVariable_Vector3D" => SmartPropParameterType.Vector3,
            "CSmartPropVariable_Vector4D" => SmartPropParameterType.Vector4,
            "CSmartPropVariable_String" => SmartPropParameterType.String,
            "CSmartPropVariable_MaterialGroup" => SmartPropParameterType.MaterialGroup,
            "CSmartPropVariable_Model" => SmartPropParameterType.Model,
            "CSmartPropVariable_Material" => SmartPropParameterType.Material,
            _ when className.StartsWith("CSmartPropVariable_", StringComparison.Ordinal) => SmartPropParameterType.String,
            _ => SmartPropParameterType.Unknown,
        };

        internal static object GetVariableDefault(KVObject variable, SmartPropParameterType type, SmartPropEvaluationContext ctx)
        {
            var resolved = variable.TryGetValue("m_DefaultValue", out var defaultNode)
                ? SmartPropAttribute.Resolve(defaultNode!, ctx)
                : null;

            switch (type)
            {
                case SmartPropParameterType.Float:
                case SmartPropParameterType.Int:
                    // Hammer 5 Tools sometimes writes numeric defaults as empty strings
                    if (resolved is string s && s.Length == 0)
                    {
                        return 0.0;
                    }

                    return resolved == null ? 0.0 : SmartPropExpression.ToNumber(resolved);
                case SmartPropParameterType.Bool:
                    return resolved != null && SmartPropExpression.ToBool(resolved);
                case SmartPropParameterType.Color:
                    return SmartPropAttribute.ToColor(resolved, Vector4.One);
                case SmartPropParameterType.Vector2:
                case SmartPropParameterType.Vector3:
                    return SmartPropAttribute.ToVector3(resolved);
                case SmartPropParameterType.Vector4:
                    return resolved is RawComponents raw ? raw.Value : SmartPropAttribute.ToColor(resolved, Vector4.Zero);
                default:
                    return resolved as string ?? string.Empty;
            }
        }

        /// <summary>
        /// Applies the default option of every choice to the variable environment.
        /// </summary>
        internal static void ApplyChoices(KVObject root, SmartPropEvaluationContext ctx)
        {
            var choices = root.GetArray("m_Choices");

            if (choices == null)
            {
                return;
            }

            foreach (var choice in choices)
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
                        ctx.Variables[target] = ConvertDataTypedValue(variableValue, ctx);
                    }
                }
            }
        }

        internal static object ConvertDataTypedValue(KVObject container, SmartPropEvaluationContext ctx)
        {
            var resolved = container.TryGetValue("m_Value", out var valueNode)
                ? SmartPropAttribute.Resolve(valueNode!, ctx)
                : null;

            var dataType = container.GetStringProperty("m_DataType", string.Empty);

            return dataType.ToUpperInvariant() switch
            {
                "INTEGER" or "INT" or "FLOAT" or "DOUBLE" => SmartPropExpression.ToNumber(resolved),
                "BOOL" or "BOOLEAN" => SmartPropExpression.ToBool(resolved),
                "STRING" => resolved as string ?? string.Empty,
                "COLOR" => SmartPropAttribute.ToColor(resolved, Vector4.One),
                _ => NormalizeValue(resolved),
            };
        }

        internal static object NormalizeValue(object? value) => value switch
        {
            RawComponents { Count: >= 4 } raw => raw.Value,
            RawComponents raw => new Vector3(raw.Value.X, raw.Value.Y, raw.Value.Z),
            null => 0.0,
            _ => value,
        };

        internal static void EvaluateChildren(KVObject parent, ref SmartPropState state, SmartPropEvaluationContext ctx)
        {
            var children = parent.GetArray("m_Children");

            if (children == null)
            {
                return;
            }

            foreach (var child in children)
            {
                var className = child.GetStringProperty("_class", string.Empty);

                // ModifyState mutates the ongoing state that following siblings see
                if (className == "CSmartPropElement_ModifyState")
                {
                    if (SmartPropAttribute.GetBool(child, "m_bEnabled", ctx, true))
                    {
                        ApplyModifiers(child, ref state, ctx);
                    }

                    continue;
                }

                EvaluateElement(child, className, state, ctx);
            }
        }

        internal static void EvaluateElement(KVObject element, string className, SmartPropState state, SmartPropEvaluationContext ctx)
        {
            if (!SmartPropAttribute.GetBool(element, "m_bEnabled", ctx, true))
            {
                return;
            }

            if (!ApplyModifiers(element, ref state, ctx))
            {
                return;
            }

            switch (className)
            {
                case "CSmartPropElement_Model":
                case "CSmartPropElement_PropDynamic":
                case "CSmartPropElement_PropPhysics":
                    EmitModel(element, state, ctx);
                    break;

                case "CSmartPropElement_Group":
                    EvaluateChildren(element, ref state, ctx);
                    break;

                case "CSmartPropElement_PickOne":
                    EvaluatePickOne(element, state, ctx);
                    break;

                case "CSmartPropElement_SmartProp":
                    EvaluateNestedSmartProp(element, state, ctx);
                    break;

                case "CSmartPropElement_ModifyState":
                    ApplyModifiers(element, ref state, ctx);
                    break;

                case "Hammer5Tools_Comment":
                    break;

                case "CSmartPropElement_FitOnLine":
                    EvaluateFitOnLine(element, state, ctx);
                    break;

                case "CSmartPropElement_PlaceMultiple":
                    EvaluatePlaceMultiple(element, state, ctx);
                    break;

                case "CSmartPropElement_PlaceOnPath":
                    EvaluatePlaceOnPath(element, state, ctx);
                    break;

                case "CSmartPropElement_PlaceInSphere":
                    EvaluatePlaceInSphere(element, state, ctx);
                    break;

                case "CSmartPropElement_Layout2DGrid":
                    EvaluateLayout2DGrid(element, state, ctx);
                    break;

                case "CSmartPropElement_BendDeformer":
                    EvaluateDeformer(element, state, ctx, bend: true);
                    break;

                case "CSmartPropElement_MidpointDeformer":
                    EvaluateDeformer(element, state, ctx, bend: false);
                    break;

                case "CSmartPropElement_Deformer":
                    EvaluateChildren(element, ref state, ctx);
                    break;

                case "CSmartPropElement_PlaceOnMesh":
                    // Needs the named mesh from the containing map, which a standalone prop doesn't have
                    ctx.Warn(SmartPropDiagnosticCode.NeedsWorldContext, className, $"{className} needs map mesh data, placing its children once");
                    EvaluateChildren(element, ref state, ctx);
                    break;

                default:
                    ctx.Warn(SmartPropDiagnosticCode.UnhandledElement, className, $"Unhandled smart prop element {className}");
                    break;
            }
        }

        private static void EmitModel(KVObject element, in SmartPropState state, SmartPropEvaluationContext ctx)
        {
            var modelName = SmartPropAttribute.GetString(element, "m_sModelName", ctx);

            if (string.IsNullOrEmpty(modelName) || modelName == "None")
            {
                return;
            }

            if (ctx.Result.Placements.Count >= ctx.MaxPlacements)
            {
                ctx.Warn(SmartPropDiagnosticCode.PlacementBudgetExhausted, null, "Placement budget exhausted, output is truncated");
                return;
            }

            var modelScale = SmartPropAttribute.GetVector3(element, "m_vModelScale", ctx, Vector3.One);
            var uniformScale = SmartPropAttribute.GetFloat(element, "m_flUniformModelScale", ctx, 1f);
            var materialGroup = SmartPropAttribute.GetString(element, "m_MaterialGroupName", ctx);

            if (string.IsNullOrEmpty(materialGroup))
            {
                materialGroup = null;
            }

            ctx.Result.Placements.Add(new SmartPropPlacement
            {
                ModelName = modelName,
                Transform = state.Transform,
                Scale = state.Scale * modelScale * uniformScale,
                TintColor = state.Tint,
                MaterialGroupName = materialGroup,
                LodLevel = element.GetIntegerProperty("m_nLodLevel", -1),
                MaterialOverrides = state.MaterialOverrides,
            });
        }

        private static void EvaluatePickOne(KVObject element, in SmartPropState state, SmartPropEvaluationContext ctx)
        {
            var children = element.GetArray("m_Children");

            if (children == null || children.Count == 0)
            {
                return;
            }

            var selectionMode = SmartPropAttribute.GetString(element, "m_SelectionMode", ctx, "RANDOM");
            var pickedIndex = -1;

            if (string.Equals(selectionMode, "SPECIFIC", StringComparison.OrdinalIgnoreCase))
            {
                pickedIndex = Math.Clamp(SmartPropAttribute.GetInt(element, "m_SpecificChildIndex", ctx), 0, children.Count - 1);
            }
            else
            {
                var eligible = new List<(int Index, float Weight)>(children.Count);

                for (var i = 0; i < children.Count; i++)
                {
                    if (!IsEligible(children[i], ctx, out var weight))
                    {
                        continue;
                    }

                    eligible.Add((i, weight));
                }

                if (eligible.Count == 0)
                {
                    return;
                }

                if (string.Equals(selectionMode, "FIRST", StringComparison.OrdinalIgnoreCase))
                {
                    pickedIndex = eligible[0].Index;
                }
                else
                {
                    var picked = PickWeightedIndex(eligible.Count, i => eligible[i].Weight, ctx.Random);
                    pickedIndex = eligible[picked].Index;
                }
            }

            var outputVariable = element.GetStringProperty("m_OutputChoiceVariableName");

            if (!string.IsNullOrEmpty(outputVariable))
            {
                ctx.SetVariable(outputVariable, (double)pickedIndex);
            }

            var pickedChild = children[pickedIndex];
            EvaluateElement(pickedChild, pickedChild.GetStringProperty("_class", string.Empty), state, ctx);
        }

        /// <summary>
        /// Weighted random selection: roll in [0, total], walk the buckets, default to the last.
        /// </summary>
        internal static int PickWeightedIndex(int count, Func<int, float> weightAt, UniformRandomStream random)
        {
            var totalWeight = 0f;

            for (var i = 0; i < count; i++)
            {
                totalWeight += weightAt(i);
            }

            var roll = random.RandomFloat(0f, totalWeight);
            var accumulated = 0f;

            for (var i = 0; i < count; i++)
            {
                accumulated += weightAt(i);

                if (roll <= accumulated)
                {
                    return i;
                }
            }

            return count - 1;
        }

        /// <summary>
        /// Yields the enabled selection criteria of a child element with their class names.
        /// </summary>
        internal static IEnumerable<(string Class, KVObject Criterion)> EnabledCriteria(KVObject child, SmartPropEvaluationContext ctx)
        {
            var criteria = child.GetArray("m_SelectionCriteria");

            if (criteria == null)
            {
                yield break;
            }

            foreach (var criterion in criteria)
            {
                if (!SmartPropAttribute.GetBool(criterion, "m_bEnabled", ctx, true))
                {
                    continue;
                }

                yield return (criterion.GetStringProperty("_class", string.Empty), criterion);
            }
        }

        internal static bool IsEligible(KVObject child, SmartPropEvaluationContext ctx, out float weight)
        {
            weight = 1f;

            if (!SmartPropAttribute.GetBool(child, "m_bEnabled", ctx, true))
            {
                return false;
            }

            // Deterministic filters exclude a child from selection entirely (authors gate PickOne
            // alternatives with variable filters); random filters only roll during real evaluation
            var modifiers = child.GetArray("m_Modifiers");

            if (modifiers != null)
            {
                // These filter classes never read or write the transform state
                var scratchState = default(SmartPropState);

                foreach (var modifier in modifiers)
                {
                    var modifierClass = modifier.GetStringProperty("_class", string.Empty);

                    if (modifierClass is not ("CSmartPropFilter_VariableValue" or "CSmartPropFilter_Expression")
                        || !SmartPropAttribute.GetBool(modifier, "m_bEnabled", ctx, true))
                    {
                        continue;
                    }

                    if (!ApplyModifier(modifier, modifierClass, ref scratchState, ctx))
                    {
                        return false;
                    }
                }
            }

            foreach (var (criterionClass, criterion) in EnabledCriteria(child, ctx))
            {
                switch (criterionClass)
                {
                    case "CSmartPropSelectionCriteria_ChoiceWeight":
                        weight = SmartPropAttribute.GetFloat(criterion, "m_flWeight", ctx, 1f);
                        break;

                    case "CSmartPropSelectionCriteria_IsValid":
                        if (!SmartPropAttribute.GetExpressionBool(criterion, "m_Expression", ctx, true))
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

        private static void EvaluateNestedSmartProp(KVObject element, in SmartPropState state, SmartPropEvaluationContext ctx)
        {
            var resourceName = SmartPropAttribute.GetString(element, "m_sSmartProp", ctx);

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

            var childRoot = ctx.NestedDocumentResolver?.Invoke(resourceName);

            if (childRoot == null)
            {
                if (ctx.FileLoader == null)
                {
                    ctx.Warn(SmartPropDiagnosticCode.NestedNoLoader, resourceName, "No file loader available to resolve nested smart props");
                    return;
                }

                var resource = ctx.FileLoader.LoadFileCompiled(resourceName);

                if (resource?.DataBlock is not BinaryKV3 childData)
                {
                    ctx.Warn(SmartPropDiagnosticCode.NestedLoadFailed, resourceName, $"Failed to load nested smart prop '{resourceName}'");
                    return;
                }

                childRoot = childData.Data.Root;
            }

            var childState = state;
            childState.ObjectTransform = state.Transform;
            childState.ObjectScale = state.Scale;
            childState.ObjectTint = state.Tint;

            // Each document evaluates with a working stream seeded from its own cached seed, so
            // repeated placements of the same nested prop are internally identical by design
            var parentRandom = ctx.Random;
            ctx.Random = new UniformRandomStream(ctx.GetDocumentSeed(resourceName));

            var localEvaluationState = SmartPropAttribute.GetBool(element, "m_bLocalEvaluationState", ctx, true);
            Dictionary<string, object>? savedVariables = null;
            Dictionary<string, SmartPropState>? savedStates = null;

            if (localEvaluationState)
            {
                savedVariables = new Dictionary<string, object>(ctx.Variables, StringComparer.OrdinalIgnoreCase);
                savedStates = new Dictionary<string, SmartPropState>(ctx.SavedStates, StringComparer.OrdinalIgnoreCase);
                ctx.Variables.Clear();
                ctx.SavedStates.Clear();
            }

            ctx.Depth++;

            // A nested document may narrow the remaining recursion depth via its own m_nMaxDepth
            var previousMaxDepth = ctx.MaxDepth;
            var childMaxDepth = childRoot.GetInt32Property("m_nMaxDepth");

            if (childMaxDepth > 0)
            {
                ctx.MaxDepth = Math.Min(ctx.MaxDepth, ctx.Depth + childMaxDepth);
            }

            try
            {
                DeclareVariables(childRoot, ctx);
                ApplyChoices(childRoot, ctx);

                if (ApplyModifiers(childRoot, ref childState, ctx))
                {
                    EvaluateChildren(childRoot, ref childState, ctx);
                }
            }
            finally
            {
                ctx.Depth--;
                ctx.MaxDepth = previousMaxDepth;
                ctx.Random = parentRandom;

                if (localEvaluationState)
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

        internal static SmartPropSpace GetSpace(KVObject obj, string name, SmartPropEvaluationContext ctx, SmartPropSpace defaultSpace)
        {
            var text = SmartPropAttribute.GetString(obj, name, ctx);

            if (string.IsNullOrEmpty(text))
            {
                return defaultSpace;
            }

            // Evaluation happens in the prop's own space, so WORLD and OBJECT both refer to the
            // frame the current document was placed in; only ELEMENT differs
            return text.Equals("ELEMENT", StringComparison.OrdinalIgnoreCase) ? SmartPropSpace.Element : SmartPropSpace.Object;
        }

        internal static void ApplyTranslate(ref SmartPropState state, Vector3 offset, SmartPropSpace space)
        {
            if (space == SmartPropSpace.Element)
            {
                state.Transform = Matrix4x4.CreateTranslation(offset * state.Scale) * state.Transform;
            }
            else
            {
                state.Transform.Translation += Vector3.TransformNormal(offset * state.ObjectScale, state.ObjectTransform);
            }
        }

        internal static Vector3 PointToWorld(Vector3 point, SmartPropSpace space, in SmartPropState state)
            => space == SmartPropSpace.Element
                ? Vector3.Transform(point * state.Scale, state.Transform)
                : Vector3.Transform(point * state.ObjectScale, state.ObjectTransform);

        internal static Vector3 DirectionToWorld(Vector3 direction, SmartPropSpace space, in SmartPropState state)
            => space == SmartPropSpace.Element
                ? Vector3.TransformNormal(direction, state.Transform)
                : Vector3.TransformNormal(direction, state.ObjectTransform);

        internal static Vector3 PointToSpace(Vector3 worldPoint, SmartPropSpace space, in SmartPropState state)
        {
            var frame = space == SmartPropSpace.Element ? state.Transform : state.ObjectTransform;

            return Matrix4x4.Invert(frame, out var inverse)
                ? Vector3.Transform(worldPoint, inverse)
                : worldPoint;
        }

        internal static Vector3 DirectionToSpace(Vector3 worldDirection, SmartPropSpace space, in SmartPropState state)
        {
            var frame = space == SmartPropSpace.Element ? state.Transform : state.ObjectTransform;

            return Matrix4x4.Invert(frame, out var inverse)
                ? Vector3.TransformNormal(worldDirection, inverse)
                : worldDirection;
        }

        /// <summary>
        /// Builds a rotation whose x-axis is <paramref name="forward"/> and z-axis approximates
        /// <paramref name="up"/> (or exactly up, with forward approximated, when prioritized).
        /// </summary>
        internal static Matrix4x4? BuildBasis(Vector3 forward, Vector3 up, bool prioritizeUp = false)
        {
            if (forward == Vector3.Zero || up == Vector3.Zero)
            {
                return null;
            }

            forward = Vector3.Normalize(forward);
            up = Vector3.Normalize(up);
            var left = Vector3.Cross(up, forward);

            if (left.LengthSquared() < 1e-6f)
            {
                // Forward is parallel to up; pick any perpendicular frame
                left = Vector3.Cross(Vector3.UnitX, forward);

                if (left.LengthSquared() < 1e-6f)
                {
                    left = Vector3.Cross(Vector3.UnitY, forward);
                }
            }

            left = Vector3.Normalize(left);

            if (prioritizeUp)
            {
                forward = Vector3.Cross(left, up);
            }
            else
            {
                up = Vector3.Cross(forward, left);
            }

            return new Matrix4x4(
                forward.X, forward.Y, forward.Z, 0,
                left.X, left.Y, left.Z, 0,
                up.X, up.Y, up.Z, 0,
                0, 0, 0, 1);
        }
    }
}
