using ValveKeyValue;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.ResourceTypes.RubikonPhysics;
using ValveResourceFormat.Serialization.KeyValues;
using static ValveResourceFormat.IO.KVHelpers;

namespace ValveResourceFormat.IO;

/// <summary>
/// Rebuilds the model doc nodes for the collision model: physics joints and the markup carried by the
/// shapes they connect.
/// </summary>
partial class ModelExtract
{
    /// <summary>
    /// Builds a PhysicsJointList child node for one joint, or <see langword="null"/> for a joint type
    /// with no known ModelDoc node class. The limit attributes are recovered per joint type: only
    /// <see cref="JointType.Conical"/>, <see cref="JointType.Revolute"/> and
    /// <see cref="JointType.Prismatic"/> have confirmed motion-limit authoring keys.
    /// </summary>
    static KVObject? BuildPhysicsJoint(PhysAggregateData physAggregateData, Joint joint)
    {
        var className = joint.Type switch
        {
            JointType.Null => "PhysicsJointNull",
            JointType.Spherical => "PhysicsJointSpherical",
            JointType.Prismatic => "PhysicsJointPrismatic",
            JointType.Revolute => "PhysicsJointRevolute",
            JointType.Conical => "PhysicsJointConical",
            JointType.Weld => "PhysicsJointWeld",
            JointType.Wheel => "PhysicsJointWheel",
            _ => null,
        };

        if (className is null)
        {
            return null;
        }

        var jointNode = MakeNode(
            className,
            ("parent_body", physAggregateData.GetParentBoneName(joint.Body1)),
            ("child_body", physAggregateData.GetParentBoneName(joint.Body2)),
            ("anchor_origin", ToKVArray(joint.Frame1.Position)),
            ("anchor_angles", ToKVArray(EntityTransformHelper.ToEulerAngles(joint.Frame1.Rotation))),
            ("collision_enabled", joint.EnableCollision),
            ("friction", joint.Friction)
        );

        switch (joint.Type)
        {
            case JointType.Conical:
                jointNode.Add("enable_swing_limit", joint.EnableSwingLimit);
                jointNode.Add("swing_limit", float.RadiansToDegrees(joint.SwingLimit.Max));
                jointNode.Add("enable_twist_limit", joint.EnableTwistLimit);
                jointNode.Add("min_twist_angle", float.RadiansToDegrees(joint.TwistLimit.Min));
                jointNode.Add("max_twist_angle", float.RadiansToDegrees(joint.TwistLimit.Max));
                break;
            case JointType.Revolute:
                jointNode.Add("enable_limit", joint.EnableTwistLimit);
                jointNode.Add("min_angle", float.RadiansToDegrees(joint.TwistLimit.Min));
                jointNode.Add("max_angle", float.RadiansToDegrees(joint.TwistLimit.Max));
                break;
            case JointType.Prismatic:
                jointNode.Add("enable_limit", joint.EnableLinearLimit);
                jointNode.Add("min_offset", joint.LinearLimit.Min);
                jointNode.Add("max_offset", joint.LinearLimit.Max);
                break;
        }

        return jointNode;
    }

    /// <summary>
    /// Writes the hit group a physics shape belongs to. Shipped content leaves this at the invalid
    /// placeholder, which the compiler does not write back, so only a real group is emitted.
    /// </summary>
    static void AddHitGroup<TShape>(KVObject node, ShapeDescriptor<TShape> shape) where TShape : struct
    {
        if (!string.IsNullOrEmpty(shape.HitGroupName) && shape.HitGroupName != "HITGROUP_INVALID")
        {
            node.Add("hitgroupname", shape.HitGroupName);
        }
    }
}
