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
    /// Math and selection helpers shared by elements and operations.
    /// </summary>
    internal static class SmartPropHelpers
    {
        public static SmartPropSpace ParseSpace(string? text, SmartPropSpace defaultSpace)
        {
            if (string.IsNullOrEmpty(text))
            {
                return defaultSpace;
            }

            // Evaluation happens in the prop's own space, so WORLD and OBJECT both refer to the
            // frame the current document was placed in; only ELEMENT differs
            return text.Equals("ELEMENT", StringComparison.OrdinalIgnoreCase) ? SmartPropSpace.Element : SmartPropSpace.Object;
        }

        public static void ApplyTranslate(ref SmartPropState state, Vector3 offset, SmartPropSpace space)
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

        public static Vector3 PointToWorld(Vector3 point, SmartPropSpace space, in SmartPropState state)
            => space == SmartPropSpace.Element
                ? Vector3.Transform(point * state.Scale, state.Transform)
                : Vector3.Transform(point * state.ObjectScale, state.ObjectTransform);

        public static Vector3 DirectionToWorld(Vector3 direction, SmartPropSpace space, in SmartPropState state)
            => space == SmartPropSpace.Element
                ? Vector3.TransformNormal(direction, state.Transform)
                : Vector3.TransformNormal(direction, state.ObjectTransform);

        public static Vector3 PointToSpace(Vector3 worldPoint, SmartPropSpace space, in SmartPropState state)
        {
            var frame = space == SmartPropSpace.Element ? state.Transform : state.ObjectTransform;

            return Matrix4x4.Invert(frame, out var inverse)
                ? Vector3.Transform(worldPoint, inverse)
                : worldPoint;
        }

        public static Vector3 DirectionToSpace(Vector3 worldDirection, SmartPropSpace space, in SmartPropState state)
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
        public static Matrix4x4? BuildBasis(Vector3 forward, Vector3 up, bool prioritizeUp = false)
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

        public static Matrix4x4 FromToRotation(Vector3 from, Vector3 to)
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

        /// <summary>
        /// Weighted random selection: roll in [0, total], walk the buckets, default to the last.
        /// </summary>
        public static int PickWeightedIndex(int count, Func<int, float> weightAt, UniformRandomStream random)
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

        public static float Snap(float value, float increment)
            => increment > 0f ? MathF.Round(value / increment) * increment : value;

        public static bool CompareValues(string op, object? current, object? target) => op.ToUpperInvariant() switch
        {
            "NOT_EQUAL" => !SmartPropExpression.ValuesEqual(current, target),
            "GREATER" => SmartPropExpression.ToNumber(current) > SmartPropExpression.ToNumber(target),
            "GREATER_OR_EQUAL" => SmartPropExpression.ToNumber(current) >= SmartPropExpression.ToNumber(target),
            "LESS" => SmartPropExpression.ToNumber(current) < SmartPropExpression.ToNumber(target),
            "LESS_OR_EQUAL" => SmartPropExpression.ToNumber(current) <= SmartPropExpression.ToNumber(target),
            _ => SmartPropExpression.ValuesEqual(current, target),
        };

        public static void ApplyTint(ref SmartPropState state, Vector4 color, string? mode)
        {
            state.Tint = mode?.ToUpperInvariant() switch
            {
                "REPLACE" => color,
                "MULTIPLY_CURRENT" => state.Tint * color,
                _ => state.ObjectTint * color,
            };
        }
    }
}
