using System.Linq;
using ValveKeyValue;
using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.ResourceTypes.ModelAnimation
{
    /// <summary>
    /// Represents a model skeleton with bones arranged in a hierarchy.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/modellib/ModelSkeletonData_t">ModelSkeletonData_t</seealso>
    public class Skeleton
    {
        /// <summary>
        /// Name of the dedicated bone that carries root motion.
        /// </summary>
        public const string RootMotionBoneName = "root_motion";

        /// <summary>
        /// Gets the name of the skeleton.
        /// </summary>
        public string Name { get; private set; } = string.Empty;

        /// <summary>
        /// Gets the root bones of the skeleton.
        /// </summary>
        public Bone[] Roots { get; private set; } = [];

        /// <summary>
        /// Gets all bones in the skeleton.
        /// </summary>
        public Bone[] Bones { get; private set; } = [];

        /// <summary>
        /// Gets the root bone for cloth simulation, if present.
        /// </summary>
        public Bone? ClothSimulationRoot { get; private set; }

        /// <summary>
        /// Gets a bone by its StringToken hash.
        /// </summary>
        public Bone? this[uint hash]
        {
            get
            {
                var index = GetBoneIndex(hash);
                return index != -1 ? Bones[index] : null;
            }
        }

        /// <summary>
        /// Gets a bone by its name.
        /// </summary>
        public Bone? this[string name] => this[StringToken.Get(name)];


        /// <summary>
        /// Gets the index of a bone by its StringToken hash, or -1 if not found.
        /// </summary>
        public int GetBoneIndex(uint hash) => boneHashToIndex.TryGetValue(hash, out var index) ? index : -1;

        /// <summary>
        /// Gets the index of a bone by its name, or -1 if not found.
        /// </summary>
        public int GetBoneIndex(string name) => GetBoneIndex(StringToken.Get(name));

        /// <summary>
        /// Whether this skeleton is compatible with <paramref name="other"/>: every bone they share (by
        /// name) has the same parent bone name in both, so this skeleton's parent-relative transforms stay
        /// valid when retargeted onto <paramref name="other"/>. The first-person viewmodel rig fails this
        /// (its arm bones hang off weapon/shoulder bones the body lacks), so callers can give it a separate
        /// armature instead.
        /// </summary>
        public bool IsCompatibleWith(Skeleton other)
        {
            foreach (var bone in Bones)
            {
                if (bone.Name == RootMotionBoneName)
                {
                    continue;
                }

                var otherBone = other[bone.Name];
                if (otherBone == null)
                {
                    continue;
                }

                var parent = bone.Parent?.Name;
                var otherParent = otherBone.Parent?.Name;

                if (parent != null && otherParent != null && parent != otherParent)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Builds a remap table mapping each bone in this skeleton to the index of the same-named bone in
        /// <paramref name="target"/>, or -1 when <paramref name="target"/> has no such bone.
        /// </summary>
        public int[] BuildBoneRemapTable(Skeleton target)
        {
            var remap = new int[Bones.Length];
            for (var i = 0; i < remap.Length; i++)
            {
                remap[i] = target.GetBoneIndex(Bones[i].Name);
            }

            return remap;
        }

        /// <summary>
        /// Creates a skeleton from model data.
        /// </summary>
        public static Skeleton FromModelData(KVObject modelData)
        {
            // Check if there is any skeleton data present at all
            if (!modelData.ContainsKey("m_modelSkeleton"))
            {
                Console.WriteLine("No skeleton data found.");
            }

            // Construct the armature from the skeleton KV
            return new Skeleton(modelData.GetSubCollection("m_modelSkeleton"))
            {
                Name = modelData.GetStringProperty("m_name"),
            };
        }

        readonly Dictionary<uint, int> boneHashToIndex = [];

        /// <summary>
        /// Creates a skeleton from skeleton-specific data.
        /// </summary>
        public static Skeleton FromSkeletonData(KVObject nmSkeleton)
        {
            var boneNames = nmSkeleton.GetArray<string>("m_boneIDs");
            var boneParents = nmSkeleton.GetIntegerArray("m_parentIndices");
            var boneTransforms = nmSkeleton.GetArray("m_parentSpaceReferencePose");

            var boneCount = boneNames.Length;

            var s = new Skeleton
            {
                Name = nmSkeleton.GetStringProperty("m_ID"),
                Bones = new Bone[boneCount],
            };

            for (var i = 0; i < boneCount; i++)
            {
                var transform = boneTransforms[i].ToTransform();

                var bone = new Bone(i, boneNames[i], transform.Position, transform.Rotation, ModelSkeletonBoneFlags.NoBoneFlags);
                s.Bones[i] = bone;
            }

            s.SetBoneParents(boneParents);
            return s;
        }

        private Skeleton()
        {
        }

        /// <summary>
        /// Construct the Armature object from mesh skeleton KV data.
        /// </summary>
        private Skeleton(KVObject skeletonData)
        {
            var boneNames = skeletonData.GetArray<string>("m_boneName");
            var boneParents = skeletonData.GetIntegerArray("m_nParent");
            var boneFlags = skeletonData.GetIntegerArray("m_nFlag")
                .Select(flags => (ModelSkeletonBoneFlags)flags)
                .ToArray();
            var bonePositions = skeletonData.GetArray("m_bonePosParent").Select(v => v.ToVector3()).ToArray();
            var boneRotations = skeletonData.GetArray("m_boneRotParent").Select(v => v.ToQuaternion()).ToArray();

            var boneCount = boneNames.Length;
            Bones = new Bone[boneCount];

            for (var i = 0; i < boneCount; i++)
            {
                var bone = new Bone(i, boneNames[i], bonePositions[i], boneRotations[i], boneFlags[i]);
                Bones[i] = bone;

                if ((bone.Flags & ModelSkeletonBoneFlags.ProceduralCloth) == ModelSkeletonBoneFlags.Cloth
                && ClothSimulationRoot == null)
                {
                    ClothSimulationRoot = bone;
                }
            }

            SetBoneParents(boneParents);
        }

        private void SetBoneParents(long[] boneParents)
        {
            var roots = new List<Bone>();
            foreach (var bone in Bones)
            {
                var parentId = boneParents[bone.Index];
                if (parentId != -1)
                {
                    bone.SetParent(Bones[parentId]);
                    continue;
                }

                roots.Add(bone);
            }

            Roots = [.. roots];

            for (var i = 0; i < Bones.Length; i++)
            {
                var name = Bones[i].Name;
                var hash = StringToken.Store(name);
                boneHashToIndex[hash] = i;
            }
        }
    }
}
