using System.Globalization;
using static ValveResourceFormat.ResourceTypes.EntityLump;

namespace ValveResourceFormat.Utils
{
    /// <summary>
    /// Helper methods for entity transformations.
    /// </summary>
    public static class EntityTransformHelper
    {
        /// <summary>
        /// Extracts scale, rotation, and position components from an entity's transformation.
        /// </summary>
        /// <param name="entity">The entity to extract from.</param>
        /// <param name="scaleVector">The scale vector.</param>
        /// <param name="rotationMatrix">The rotation matrix.</param>
        /// <param name="positionVector">The position vector.</param>
        public static void DecomposeTransformationMatrix(Entity entity, out Vector3 scaleVector, out Matrix4x4 rotationMatrix, out Vector3 positionVector)
        {
            scaleVector = entity.GetVector3Property("scales");
            positionVector = entity.GetVector3Property("origin");
            var pitchYawRoll = entity.GetVector3Property("angles");

            rotationMatrix = CreateRotationMatrixFromEulerAngles(pitchYawRoll);
        }

        /// <summary>
        /// Creates a rotation matrix from Euler angles (pitch, yaw, roll).
        /// </summary>
        /// <param name="pitchYawRoll">The Euler angles.</param>
        /// <returns>The rotation matrix.</returns>
        public static Matrix4x4 CreateRotationMatrixFromEulerAngles(Vector3 pitchYawRoll)
        {
            Matrix4x4 rotationMatrix;
            var rollMatrix = Matrix4x4.CreateRotationX(float.DegreesToRadians(pitchYawRoll.Z));
            var pitchMatrix = Matrix4x4.CreateRotationY(float.DegreesToRadians(pitchYawRoll.X));
            var yawMatrix = Matrix4x4.CreateRotationZ(float.DegreesToRadians(pitchYawRoll.Y));

            rotationMatrix = rollMatrix * pitchMatrix * yawMatrix;
            return rotationMatrix;
        }

        /// <summary>
        /// Converts a quaternion to Euler angles (pitch, yaw, roll) in degrees.
        /// </summary>
        /// <param name="q">The quaternion.</param>
        /// <returns>The Euler angles in degrees.</returns>
        public static Vector3 ToEulerAngles(Quaternion q)
        {
            Vector3 angles = new();

            // pitch / x
            var sinp = 2 * (q.W * q.Y - q.Z * q.X);
            if (Math.Abs(sinp) >= 1)
            {
                angles.X = MathF.CopySign(MathF.PI / 2, sinp);
            }
            else
            {
                angles.X = MathF.Asin(sinp);
            }

            // yaw / y
            var siny_cosp = 2 * (q.W * q.Z + q.X * q.Y);
            var cosy_cosp = 1 - 2 * (q.Y * q.Y + q.Z * q.Z);
            angles.Y = MathF.Atan2(siny_cosp, cosy_cosp);

            // roll / z
            var sinr_cosp = 2 * (q.W * q.X + q.Y * q.Z);
            var cosr_cosp = 1 - 2 * (q.X * q.X + q.Y * q.Y);
            angles.Z = MathF.Atan2(sinr_cosp, cosr_cosp);

            return Vector3.RadiansToDegrees(angles);
        }

        /// <summary>
        /// Calculates the full transformation matrix for an entity.
        /// </summary>
        /// <param name="entity">The entity.</param>
        /// <returns>The transformation matrix.</returns>
        public static Matrix4x4 CalculateTransformationMatrix(Entity entity)
        {
            DecomposeTransformationMatrix(entity, out var scaleVector, out var rotationMatrix, out var positionVector);

            var scaleMatrix = Matrix4x4.CreateScale(scaleVector);
            var positionMatrix = Matrix4x4.CreateTranslation(positionVector);

            return scaleMatrix * rotationMatrix * positionMatrix;
        }

        /// <summary>
        /// Parses a string representation of a Vector2.
        /// </summary>
        /// <param name="input">The input string.</param>
        /// <returns>The parsed vector.</returns>
        public static Vector2 ParseVector2(string input)
        {
            if (string.IsNullOrEmpty(input))
            {
                return default;
            }
            var split = input.Split(' ');

            if (split.Length != 2)
            {
                return default;
            }

            return new Vector2(
                float.Parse(split[0], CultureInfo.InvariantCulture),
                float.Parse(split[1], CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Parses a string representation of a Vector3.
        /// </summary>
        /// <param name="input">The input string.</param>
        /// <returns>The parsed vector.</returns>
        public static Vector3 ParseVector(string input)
        {
            if (string.IsNullOrEmpty(input))
            {
                return default;
            }
            var split = input.Split(' ');

            if (split.Length != 3)
            {
                return default;
            }

            return new Vector3(
                float.Parse(split[0], CultureInfo.InvariantCulture),
                float.Parse(split[1], CultureInfo.InvariantCulture),
                float.Parse(split[2], CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Builds a rigid transform matrix (rotation + translation, no scale) from a world origin and
        /// pitch/yaw/roll angles. This is exactly what a prefab/instance placement is — placement scale
        /// is inert — so it is the natural unit for recovering placements from baked world transforms.
        /// A point is mapped by <c>Vector3.Transform(point, matrix)</c>.
        /// </summary>
        public static Matrix4x4 CreateRigidTransform(Vector3 origin, Vector3 pitchYawRoll)
        {
            var matrix = CreateRotationMatrixFromEulerAngles(pitchYawRoll);
            matrix.Translation = origin;
            return matrix;
        }

        /// <summary>
        /// Decomposes a rigid transform matrix back into a world origin and pitch/yaw/roll angles,
        /// ignoring any scale component.
        /// </summary>
        public static void DecomposeRigidTransform(Matrix4x4 matrix, out Vector3 origin, out Vector3 pitchYawRoll)
        {
            origin = matrix.Translation;
            pitchYawRoll = ToEulerAngles(Quaternion.CreateFromRotationMatrix(matrix));
        }
    }
}
