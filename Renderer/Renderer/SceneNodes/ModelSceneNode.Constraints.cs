using System.Linq;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.ResourceTypes.ModelAnimation;
using ValveResourceFormat.Serialization.KeyValues;
using ValveResourceFormat.Utils;

namespace ValveResourceFormat.Renderer.SceneNodes
{
    /// <summary>
    /// The bone constraints this node simulates: the twist constraints the animation controller solves,
    /// and the morphs driven by where one bone points relative to another.
    /// </summary>
    public partial class ModelSceneNode
    {
        private DotToMorphConstraint[] dotToMorphConstraints = [];

        private float[] dotToMorphValues = [];

        /// <summary>
        /// Parses the constraints that drive a morph from a bone's facing, resolving the bones and the
        /// flex controller they name so the update does not have to search per frame.
        /// </summary>
        protected static DotToMorphConstraint[] ParseDotToMorphConstraints(Model model)
        {
            var bones = model.Skeleton.Bones;
            var controllers = model.FlexControllers;
            var constraints = new List<DotToMorphConstraint>();

            foreach (var constraintData in model.GetBoneConstraints("CBoneConstraintDotToMorph"))
            {
                var remap = constraintData.GetFloatArray("m_flRemap");
                if (remap == null || remap.Length < 4)
                {
                    continue;
                }

                var boneName = constraintData.GetStringProperty("m_sBoneName");
                var targetName = constraintData.GetStringProperty("m_sTargetBoneName");
                var channel = constraintData.GetStringProperty("m_sMorphChannelName");

                var constraint = new DotToMorphConstraint
                {
                    BoneName = boneName,
                    TargetBoneName = targetName,
                    MorphChannelName = channel,
                    InputMin = remap[0],
                    InputMax = remap[1],
                    OutputMin = remap[2],
                    OutputMax = remap[3],
                    BoneIndex = Array.FindIndex(bones, b => b.Name == boneName),
                    TargetBoneIndex = Array.FindIndex(bones, b => b.Name == targetName),
                    MorphChannelIndex = Array.FindIndex(controllers, c => c.Name == channel),
                };

                if (constraint.BoneIndex >= 0 && constraint.TargetBoneIndex >= 0 && constraint.MorphChannelIndex >= 0)
                {
                    constraints.Add(constraint);
                }
            }

            return [.. constraints];
        }

        /// <summary>
        /// Applies the bone driven morphs on top of the animated controller values.
        /// </summary>
        private void ApplyDotToMorphConstraints(DotToMorphConstraint[] constraints, float[] controllerValues)
        {
            var pose = AnimationController.Pose;

            foreach (var constraint in constraints)
            {
                if (constraint.MorphChannelIndex >= controllerValues.Length)
                {
                    continue;
                }

                var bone = pose[constraint.BoneIndex];
                var target = pose[constraint.TargetBoneIndex];

                // Measured against the bone's down axis: level with the target it reads a right angle,
                // which is what both remaps start from, and looking down opens the angle further.
                var facing = Vector3.Normalize(new Vector3(-bone.M31, -bone.M32, -bone.M33));
                var toTarget = target.Translation - bone.Translation;

                if (toTarget.LengthSquared() < 1e-12f)
                {
                    continue;
                }

                var dot = Math.Clamp(Vector3.Dot(facing, Vector3.Normalize(toTarget)), -1f, 1f);
                var degrees = MathF.Acos(dot) * (180f / MathF.PI);

                controllerValues[constraint.MorphChannelIndex] = MathUtils.RemapValClamped(
                    degrees, constraint.InputMin, constraint.InputMax, constraint.OutputMin, constraint.OutputMax);
            }
        }

        /// <summary>
        /// Parses tilt-twist constraints from the model's keyvalues.
        /// </summary>
        protected static TiltTwistConstraint[] ParseTwistConstraints(Model model)
        {
            var constraints = new List<TiltTwistConstraint>();

            foreach (var constraintData in model.GetBoneConstraints("CTiltTwistConstraint"))
            {
                var upVec = constraintData.GetFloatArray("m_vUpVector");

                var constraint = new TiltTwistConstraint
                {
                    Name = constraintData.GetStringProperty("m_name"),
                    UpVector = new Vector3(upVec[0], upVec[1], upVec[2]),
                    TargetAxis = (int)constraintData.GetIntegerProperty("m_nTargetAxis"),
                    SlaveAxis = (int)constraintData.GetIntegerProperty("m_nSlaveAxis"),
                };

                var slaves = constraintData.GetArray("m_slaves");
                constraint.Slaves = slaves.Select(s =>
                {
                    var quat = s.GetFloatArray("m_qBaseOrientation");
                    var pos = s.GetFloatArray("m_vBasePosition");

                    return new TiltTwistConstraintSlave
                    {
                        BaseOrientation = new Quaternion(quat[0], quat[1], quat[2], quat[3]),
                        BasePosition = new Vector3(pos[0], pos[1], pos[2]),
                        BoneHash = s.GetUInt32Property("m_nBoneHash"),
                        Weight = s.GetFloatProperty("m_flWeight"),
                        Name = s.GetStringProperty("m_sName"),
                    };
                }).ToArray();

                var targets = constraintData.GetArray("m_targets");
                constraint.Targets = targets.Select(t =>
                {
                    var quat = t.GetFloatArray("m_qOffset");
                    var pos = t.GetFloatArray("m_vOffset");

                    return new TiltTwistConstraintTarget
                    {
                        Offset = new Quaternion(quat[0], quat[1], quat[2], quat[3]),
                        PositionOffset = new Vector3(pos[0], pos[1], pos[2]),
                        BoneHash = t.GetUInt32Property("m_nBoneHash"),
                        Name = t.GetStringProperty("m_sName"),
                        Weight = t.GetFloatProperty("m_flWeight"),
                        IsAttachment = t.GetBooleanProperty("m_bIsAttachment"),
                    };
                }).ToArray();

                constraints.Add(constraint);
            }

            return [.. constraints];
        }
    }
}
