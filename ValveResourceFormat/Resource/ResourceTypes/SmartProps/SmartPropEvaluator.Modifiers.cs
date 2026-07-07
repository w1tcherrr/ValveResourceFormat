using ValveKeyValue;
using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.ResourceTypes.SmartProps
{
    /// <summary>
    /// Modifier dispatch: filters that gate an element, operations that mutate the running
    /// transform/tint/variable state, and editor gizmo operations.
    /// </summary>
    public static partial class SmartPropEvaluator
    {
        /// <summary>
        /// Applies an element's modifiers in order. Returns false when a filter rejects the element.
        /// </summary>
        internal static bool ApplyModifiers(KVObject element, ref SmartPropState state, SmartPropEvaluationContext ctx)
        {
            var modifiers = element.GetArray("m_Modifiers");

            if (modifiers == null)
            {
                return true;
            }

            foreach (var modifier in modifiers)
            {
                if (!SmartPropAttribute.GetBool(modifier, "m_bEnabled", ctx, true))
                {
                    continue;
                }

                var className = modifier.GetStringProperty("_class", string.Empty);

                if (!ApplyModifier(modifier, className, ref state, ctx))
                {
                    return false;
                }
            }

            return true;
        }

        internal static bool ApplyModifier(KVObject modifier, string className, ref SmartPropState state, SmartPropEvaluationContext ctx)
        {
            switch (className)
            {
                case "CSmartPropFilter_VariableValue":
                    {
                        var comparison = modifier.GetSubCollection("m_VariableComparison");

                        if (comparison == null)
                        {
                            return true;
                        }

                        var variableName = comparison.GetStringProperty("m_Name", string.Empty);
                        var current = ctx.GetVariable(variableName);
                        var target = comparison.TryGetValue("m_Value", out var targetNode)
                            ? SmartPropAttribute.Resolve(targetNode!, ctx)
                            : null;
                        var op = comparison.GetStringProperty("m_Comparison", "EQUAL");

                        return CompareValues(op, current, target);
                    }

                case "CSmartPropFilter_Expression":
                    return SmartPropAttribute.GetExpressionBool(modifier, "m_Expression", ctx, true);

                case "CSmartPropFilter_Probability":
                    {
                        var probability = SmartPropAttribute.GetFloat(modifier, "m_flProbability", ctx, 0.5f);
                        return ctx.Random.RandomFloat() <= probability;
                    }

                case "CSmartPropFilter_SurfaceProperties":
                case "CSmartPropFilter_SurfaceAngle":
                case "CSmartPropFilter_MaterialAttributes":
                    // Surface-based filters need placement-surface context that a standalone viewer doesn't have
                    ctx.Warn(SmartPropDiagnosticCode.NeedsWorldContext, className, $"{className} needs world context, treating as pass");
                    return true;

                case "CSmartPropOperation_Translate":
                    ApplyTranslate(
                        ref state,
                        SmartPropAttribute.GetVector3(modifier, "m_vPosition", ctx),
                        GetSpace(modifier, "m_CoordinateSpace", ctx, SmartPropSpace.Element));
                    return true;

                case "CSmartPropOperation_SetPosition":
                    {
                        var position = SmartPropAttribute.GetVector3(modifier, "m_vPosition", ctx);
                        var space = GetSpace(modifier, "m_CoordinateSpace", ctx, SmartPropSpace.Object);

                        state.Transform.Translation = PointToWorld(position, space, state);
                        return true;
                    }

                case "CSmartPropOperation_Rotate":
                    {
                        var angles = SmartPropAttribute.GetVector3(modifier, "m_vRotation", ctx);
                        state.Transform = EntityTransformHelper.CreateRotationMatrixFromEulerAngles(angles) * state.Transform;
                        return true;
                    }

                case "CSmartPropOperation_Scale":
                    state.Scale *= SmartPropAttribute.GetFloat(modifier, "m_flScale", ctx, 1f);
                    return true;

                case "CSmartPropOperation_RandomOffset":
                    {
                        var min = SmartPropAttribute.GetVector3(modifier, "m_vRandomPositionMin", ctx);
                        var max = SmartPropAttribute.GetVector3(modifier, "m_vRandomPositionMax", ctx);
                        var snap = SmartPropAttribute.GetVector3(modifier, "m_vSnapIncrement", ctx);
                        var offset = new Vector3(
                            Snap(ctx.Random.RandomFloat(min.X, max.X), snap.X),
                            Snap(ctx.Random.RandomFloat(min.Y, max.Y), snap.Y),
                            Snap(ctx.Random.RandomFloat(min.Z, max.Z), snap.Z));

                        ApplyTranslate(ref state, offset, SmartPropSpace.Element);
                        return true;
                    }

                case "CSmartPropOperation_RandomRotation":
                    {
                        var min = SmartPropAttribute.GetVector3(modifier, "m_vRandomRotationMin", ctx);
                        var max = SmartPropAttribute.GetVector3(modifier, "m_vRandomRotationMax", ctx);
                        var snap = SmartPropAttribute.GetVector3(modifier, "m_vSnapIncrement", ctx);
                        var angles = new Vector3(
                            Snap(ctx.Random.RandomFloat(min.X, max.X), snap.X),
                            Snap(ctx.Random.RandomFloat(min.Y, max.Y), snap.Y),
                            Snap(ctx.Random.RandomFloat(min.Z, max.Z), snap.Z));

                        state.Transform = EntityTransformHelper.CreateRotationMatrixFromEulerAngles(angles) * state.Transform;
                        return true;
                    }

                case "CSmartPropOperation_RandomScale":
                    {
                        var min = SmartPropAttribute.GetFloat(modifier, "m_flRandomScaleMin", ctx, 1f);
                        var max = SmartPropAttribute.GetFloat(modifier, "m_flRandomScaleMax", ctx, 1f);
                        var snap = SmartPropAttribute.GetFloat(modifier, "m_flSnapIncrement", ctx);

                        state.Scale *= Snap(ctx.Random.RandomFloat(min, max), snap);
                        return true;
                    }

                case "CSmartPropOperation_SetOrientation":
                    ApplySetOrientation(modifier, ref state, ctx);
                    return true;

                case "CSmartPropOperation_ResetRotation":
                    ApplyResetRotation(modifier, ref state, ctx);
                    return true;

                case "CSmartPropOperation_ResetScale":
                    state.Scale = Vector3.One;
                    return true;

                case "CSmartPropOperation_SaveState":
                    {
                        var stateName = modifier.GetStringProperty("m_StateName");

                        if (!string.IsNullOrEmpty(stateName))
                        {
                            ctx.SavedStates[stateName] = state;
                        }

                        return true;
                    }

                case "CSmartPropOperation_RestoreState":
                    {
                        var stateName = modifier.GetStringProperty("m_StateName");

                        if (!string.IsNullOrEmpty(stateName) && ctx.SavedStates.TryGetValue(stateName, out var saved))
                        {
                            state = saved;
                            return true;
                        }

                        // Valve's schema typo, kept verbatim
                        return !modifier.GetBooleanProperty("m_bDiscardIfUknown");
                    }

                case "CSmartPropOperation_SavePosition":
                    {
                        var variableName = modifier.GetStringProperty("m_VariableName");

                        if (!string.IsNullOrEmpty(variableName))
                        {
                            var space = GetSpace(modifier, "m_CoordinateSpace", ctx, SmartPropSpace.Object);
                            ctx.SetVariable(variableName, PointToSpace(state.Transform.Translation, space, state));
                        }

                        return true;
                    }

                case "CSmartPropOperation_SaveDirection":
                    {
                        var variableName = modifier.GetStringProperty("m_VariableName");

                        if (!string.IsNullOrEmpty(variableName))
                        {
                            var basis = modifier.GetStringProperty("m_DirectionVector", "FORWARD") switch
                            {
                                "LEFT" => Vector3.UnitY,
                                "UP" => Vector3.UnitZ,
                                _ => Vector3.UnitX,
                            };

                            var direction = Vector3.TransformNormal(basis, state.Transform);
                            var space = GetSpace(modifier, "m_CoordinateSpace", ctx, SmartPropSpace.Object);
                            ctx.SetVariable(variableName, DirectionToSpace(direction, space, state));
                        }

                        return true;
                    }

                case "CSmartPropOperation_SaveColor":
                    {
                        var variableName = modifier.GetStringProperty("m_VariableName");

                        if (!string.IsNullOrEmpty(variableName))
                        {
                            ctx.SetVariable(variableName, state.Tint);
                        }

                        return true;
                    }

                case "CSmartPropOperation_SetVariable":
                    {
                        var variableValue = modifier.GetSubCollection("m_VariableValue");

                        if (variableValue != null)
                        {
                            var target = variableValue.GetStringProperty("m_TargetName");

                            if (!string.IsNullOrEmpty(target))
                            {
                                ctx.SetVariable(target, ConvertDataTypedValue(variableValue, ctx));
                            }
                        }

                        return true;
                    }

                case "CSmartPropOperation_SetVariableBool":
                    {
                        var variableName = modifier.GetStringProperty("m_VariableName");

                        if (!string.IsNullOrEmpty(variableName))
                        {
                            ctx.SetVariable(variableName, SmartPropAttribute.GetBool(modifier, "m_VariableValue", ctx));
                        }

                        return true;
                    }

                case "CSmartPropOperation_SetVariableFloat":
                case "CSmartPropOperation_SetVariableInt":
                    {
                        var variableName = modifier.GetStringProperty("m_VariableName");

                        if (!string.IsNullOrEmpty(variableName))
                        {
                            ctx.SetVariable(variableName, (double)SmartPropAttribute.GetFloat(modifier, "m_VariableValue", ctx));
                        }

                        return true;
                    }

                case "CSmartPropOperation_SetTintColor":
                    ApplySetTintColor(modifier, ref state, ctx);
                    return true;

                case "CSmartPropOperation_RandomColorTintColor":
                    ApplyRandomColorTint(modifier, ref state, ctx);
                    return true;

                case "CSmartPropOperation_MaterialOverride":
                    {
                        var overrides = modifier.GetBooleanProperty("m_bClearCurrentOverrides")
                            ? []
                            : new List<(string, string)>(state.MaterialOverrides);

                        var replacements = modifier.GetArray("m_MaterialReplacements");

                        if (replacements != null)
                        {
                            foreach (var replacement in replacements)
                            {
                                var original = replacement.GetStringProperty("m_OriginalMaterial");
                                var updated = replacement.GetStringProperty("m_ReplacementMaterial");

                                if (!string.IsNullOrEmpty(original) && !string.IsNullOrEmpty(updated))
                                {
                                    overrides.Add((original, updated));
                                }
                            }
                        }

                        state.MaterialOverrides = overrides;
                        return true;
                    }

                case "CSmartPropOperation_CreateSizer":
                    ApplyCreateSizer(modifier, ctx);
                    return true;

                case "CSmartPropOperation_CreateRotator":
                    {
                        var outputVariable = modifier.GetStringProperty("m_OutputVariable");
                        var initialAngle = SmartPropAttribute.GetFloat(modifier, "m_flInitialAngle", ctx);
                        var angle = initialAngle;

                        if (!string.IsNullOrEmpty(outputVariable))
                        {
                            ctx.SetVariable(outputVariable, (double)initialAngle);

                            // The variable may be pinned by UI or defaults; the transform follows the actual value
                            if (ctx.GetVariable(outputVariable) is object current)
                            {
                                angle = (float)SmartPropExpression.ToNumber(current);
                            }

                            if (ctx.Depth == 1 && ctx.ReportedGizmos.Add($"rotator:{outputVariable}"))
                            {
                                var enforceLimits = modifier.GetBooleanProperty("m_bEnforceLimits");
                                var gizmoName = modifier.GetStringProperty("m_Name");

                                ctx.Result.GizmoOutputs.Add(new SmartPropGizmoOutput
                                {
                                    VariableName = outputVariable,
                                    Label = string.IsNullOrEmpty(gizmoName) ? outputVariable : $"{gizmoName}: {outputVariable}",
                                    InitialValue = (double)angle,
                                    MinValue = enforceLimits ? modifier.GetDoubleProperty("m_flMinAngle") : -360.0,
                                    MaxValue = enforceLimits ? modifier.GetDoubleProperty("m_flMaxAngle") : 360.0,
                                });
                            }
                        }

                        if (angle != 0f && modifier.GetBooleanProperty("m_bApplyToCurrentTransform", true))
                        {
                            var axis = SmartPropAttribute.GetVector3(modifier, "m_vRotationAxis", ctx, Vector3.UnitZ);

                            if (axis != Vector3.Zero)
                            {
                                var rotation = Matrix4x4.CreateFromAxisAngle(Vector3.Normalize(axis), float.DegreesToRadians(angle));
                                state.Transform = rotation * state.Transform;
                            }
                        }

                        return true;
                    }

                case "CSmartPropOperation_CreateLocator":
                    // Editor gizmo; at its default (undragged) value it doesn't change the transform
                    return true;

                case "Hammer5Tools_Comment":
                    return true;

                case "CSmartPropOperation_RigidDeformation":
                    // Marker for how deformers move this element; no effect without a deformer
                    return true;

                case "CSmartPropOperation_TraceInDirection":
                case "CSmartPropOperation_Trace":
                case "CSmartPropOperation_TraceToPoint":
                case "CSmartPropOperation_TraceToLine":
                    // There is no world geometry to trace against, so every trace misses
                    ctx.Warn(SmartPropDiagnosticCode.NeedsWorldContext, className, $"{className} has no world geometry to trace against, applying its no-hit behavior");
                    return ApplyTraceNoHit(modifier, className, ref state, ctx);

                case "CSmartPropOperation_ComputeDistance3D":
                case "CSmartPropOperation_ComputeDotProduct3D":
                case "CSmartPropOperation_ComputeCrossProduct3D":
                case "CSmartPropOperation_ComputeVectorBetweenPoints3D":
                case "CSmartPropOperation_ComputeNormalizedVector3D":
                case "CSmartPropOperation_ComputeProjectVector3D":
                    ApplyComputeOperation(modifier, className, state, ctx);
                    return true;

                case "CSmartPropOperation_RotateTowards":
                    ApplyRotateTowards(modifier, ref state, ctx);
                    return true;

                case "CSmartPropOperation_MaterialTint":
                    // Tints a single material; the renderer has no per-material tint, so this stays unapplied
                    ctx.Warn(SmartPropDiagnosticCode.UnsupportedOperation, className, $"{className} is not supported, skipped");
                    return true;

                default:
                    ctx.Warn(SmartPropDiagnosticCode.UnhandledModifier, className, $"Unhandled smart prop modifier {className}");
                    return true;
            }
        }

        private static bool CompareValues(string op, object? current, object? target) => op.ToUpperInvariant() switch
        {
            "NOT_EQUAL" => !SmartPropExpression.ValuesEqual(current, target),
            "GREATER" => SmartPropExpression.ToNumber(current) > SmartPropExpression.ToNumber(target),
            "GREATER_OR_EQUAL" => SmartPropExpression.ToNumber(current) >= SmartPropExpression.ToNumber(target),
            "LESS" => SmartPropExpression.ToNumber(current) < SmartPropExpression.ToNumber(target),
            "LESS_OR_EQUAL" => SmartPropExpression.ToNumber(current) <= SmartPropExpression.ToNumber(target),
            _ => SmartPropExpression.ValuesEqual(current, target),
        };

        private static float Snap(float value, float increment)
            => increment > 0f ? MathF.Round(value / increment) * increment : value;

        private static void ApplySetOrientation(KVObject modifier, ref SmartPropState state, SmartPropEvaluationContext ctx)
        {
            var forward = SmartPropAttribute.GetVector3(modifier, "m_vForwardVector", ctx, Vector3.UnitX);
            var forwardSpace = GetSpace(modifier, "m_ForwardDirectionSpace", ctx, SmartPropSpace.Object);
            var up = SmartPropAttribute.GetVector3(modifier, "m_vUpVector", ctx, Vector3.UnitZ);
            var upSpace = GetSpace(modifier, "m_UpDirectionSpace", ctx, SmartPropSpace.Object);
            var prioritizeUp = SmartPropAttribute.GetBool(modifier, "m_bPrioritizeUp", ctx);

            forward = DirectionToWorld(forward, forwardSpace, state);
            up = DirectionToWorld(up, upSpace, state);

            if (BuildBasis(forward, up, prioritizeUp) is not Matrix4x4 rotation)
            {
                return;
            }

            rotation.Translation = state.Transform.Translation;
            state.Transform = rotation;
        }

        private static void ApplyResetRotation(KVObject modifier, ref SmartPropState state, SmartPropEvaluationContext ctx)
        {
            var resetPitch = SmartPropAttribute.GetBool(modifier, "m_bResetPitch", ctx, true);
            var resetYaw = SmartPropAttribute.GetBool(modifier, "m_bResetYaw", ctx, true);
            var resetRoll = SmartPropAttribute.GetBool(modifier, "m_bResetRoll", ctx, true);

            var forward = Vector3.TransformNormal(Vector3.UnitX, state.Transform);
            var left = Vector3.TransformNormal(Vector3.UnitY, state.Transform);
            var up = Vector3.TransformNormal(Vector3.UnitZ, state.Transform);

            var yaw = float.RadiansToDegrees(MathF.Atan2(forward.Y, forward.X));
            var pitch = float.RadiansToDegrees(MathF.Atan2(-forward.Z, MathF.Sqrt(forward.X * forward.X + forward.Y * forward.Y)));
            var roll = float.RadiansToDegrees(MathF.Atan2(left.Z, up.Z));

            var angles = new Vector3(resetPitch ? 0f : pitch, resetYaw ? 0f : yaw, resetRoll ? 0f : roll);
            var rotation = EntityTransformHelper.CreateRotationMatrixFromEulerAngles(angles);
            rotation.Translation = state.Transform.Translation;
            state.Transform = rotation;
        }

        private static void ApplySetTintColor(KVObject modifier, ref SmartPropState state, SmartPropEvaluationContext ctx)
        {
            var colorChoices = modifier.GetArray("m_ColorChoices");

            if (colorChoices == null || colorChoices.Count == 0)
            {
                return;
            }

            var selectionMode = SmartPropAttribute.GetString(modifier, "m_SelectionMode", ctx, "RANDOM")!;
            int index;

            if (selectionMode.Equals("SPECIFIC", StringComparison.OrdinalIgnoreCase))
            {
                index = Math.Clamp(SmartPropAttribute.GetInt(modifier, "m_ColorSelection", ctx), 0, colorChoices.Count - 1);
            }
            else if (selectionMode.Equals("FIRST", StringComparison.OrdinalIgnoreCase))
            {
                index = 0;
            }
            else
            {
                index = PickWeightedIndex(
                    colorChoices.Count,
                    i => SmartPropAttribute.GetFloat(colorChoices[i], "m_flWeight", ctx, 1f),
                    ctx.Random);
            }

            var color = SmartPropAttribute.GetColor(colorChoices[index], "m_Color", ctx, Vector4.One);
            ApplyTint(ref state, color, SmartPropAttribute.GetString(modifier, "m_Mode", ctx, "MULTIPLY_OBJECT")!);
        }

        private static void ApplyRandomColorTint(KVObject modifier, ref SmartPropState state, SmartPropEvaluationContext ctx)
        {
            var gradient = modifier.GetSubCollection("m_Gradient");
            var stops = gradient?.GetArray("m_Stops");

            if (stops == null || stops.Count == 0)
            {
                return;
            }

            var selectionMode = SmartPropAttribute.GetString(modifier, "m_SelectionMode", ctx, "RANDOM")!.ToUpperInvariant();
            Vector4 color;

            if (selectionMode is "SPECIFIC" or "SPECIFIC_COLOR")
            {
                color = SampleGradient(stops, SmartPropAttribute.GetFloat(modifier, "m_ColorPosition", ctx), ctx);
            }
            else if (selectionMode == "GRADIENT_RANDOM_STOP")
            {
                var stop = stops[ctx.Random.RandomInt(0, stops.Count - 1)];
                color = SmartPropAttribute.GetColor(stop, "m_Color", ctx, Vector4.One);
            }
            else
            {
                color = SampleGradient(stops, ctx.Random.RandomFloat(), ctx);
            }

            ApplyTint(ref state, color, SmartPropAttribute.GetString(modifier, "m_Mode", ctx, "MULTIPLY_OBJECT")!);
        }

        private static Vector4 SampleGradient(IReadOnlyList<KVObject> stops, float position, SmartPropEvaluationContext ctx)
        {
            var previousPosition = float.MinValue;
            var previousColor = SmartPropAttribute.GetColor(stops[0], "m_Color", ctx, Vector4.One);

            foreach (var stop in stops)
            {
                var stopPosition = stop.GetFloatProperty("m_flPosition");
                var stopColor = SmartPropAttribute.GetColor(stop, "m_Color", ctx, Vector4.One);

                if (position <= stopPosition)
                {
                    if (previousPosition == float.MinValue || stopPosition <= previousPosition)
                    {
                        return stopColor;
                    }

                    var t = (position - previousPosition) / (stopPosition - previousPosition);
                    return Vector4.Lerp(previousColor, stopColor, t);
                }

                previousPosition = stopPosition;
                previousColor = stopColor;
            }

            return previousColor;
        }

        private static void ApplyTint(ref SmartPropState state, Vector4 color, string mode)
        {
            state.Tint = mode.ToUpperInvariant() switch
            {
                "REPLACE" => color,
                "MULTIPLY_CURRENT" => state.Tint * color,
                _ => state.ObjectTint * color,
            };
        }

        private static void ApplyCreateSizer(KVObject modifier, SmartPropEvaluationContext ctx)
        {
            var gizmoName = modifier.GetStringProperty("m_Name");

            Set("m_OutputVariableMinX", "m_flInitialMinX", "m_flInitialMaxX", "m_flConstraintMinX", "m_flConstraintMaxX", isMaxSide: false);
            Set("m_OutputVariableMaxX", "m_flInitialMaxX", "m_flInitialMinX", "m_flConstraintMinX", "m_flConstraintMaxX", isMaxSide: true);
            Set("m_OutputVariableMinY", "m_flInitialMinY", "m_flInitialMaxY", "m_flConstraintMinY", "m_flConstraintMaxY", isMaxSide: false);
            Set("m_OutputVariableMaxY", "m_flInitialMaxY", "m_flInitialMinY", "m_flConstraintMinY", "m_flConstraintMaxY", isMaxSide: true);
            Set("m_OutputVariableMinZ", "m_flInitialMinZ", "m_flInitialMaxZ", "m_flConstraintMinZ", "m_flConstraintMaxZ", isMaxSide: false);
            Set("m_OutputVariableMaxZ", "m_flInitialMaxZ", "m_flInitialMinZ", "m_flConstraintMinZ", "m_flConstraintMaxZ", isMaxSide: true);

            void Set(string outputField, string initialField, string pairedInitialField, string constraintMinField, string constraintMaxField, bool isMaxSide)
            {
                var variableName = modifier.GetStringProperty(outputField);

                if (string.IsNullOrEmpty(variableName))
                {
                    return;
                }

                var initial = SmartPropAttribute.GetFloat(modifier, initialField, ctx);
                ctx.SetVariable(variableName, (double)initial);

                if (ctx.Depth == 1 && ctx.ReportedGizmos.Add($"sizer:{variableName}"))
                {
                    // The authored constraints bound the axis span relative to the opposite handle
                    double? min = null;
                    double? max = null;
                    var constraintMin = SmartPropAttribute.GetFloat(modifier, constraintMinField, ctx);
                    var constraintMax = SmartPropAttribute.GetFloat(modifier, constraintMaxField, ctx);

                    if (constraintMax > constraintMin)
                    {
                        var paired = SmartPropAttribute.GetFloat(modifier, pairedInitialField, ctx);

                        if (isMaxSide)
                        {
                            min = paired + constraintMin;
                            max = paired + constraintMax;
                        }
                        else
                        {
                            min = paired - constraintMax;
                            max = paired - constraintMin;
                        }
                    }

                    ctx.Result.GizmoOutputs.Add(new SmartPropGizmoOutput
                    {
                        VariableName = variableName,
                        Label = string.IsNullOrEmpty(gizmoName) ? variableName : $"{gizmoName}: {variableName}",
                        InitialValue = SmartPropExpression.ToNumber(ctx.GetVariable(variableName)),
                        MinValue = min,
                        MaxValue = max,
                    });
                }
            }
        }
    }
}
