using System.IO;
using System.Linq;
using ValveKeyValue;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;
using static ValveResourceFormat.IO.KVHelpers;

namespace ValveResourceFormat.IO;

partial class ModelExtract
{
    /// <summary>
    /// The list nodes a model doc is assembled from. Each one is created and appended to the root the
    /// first time a section writes into it, so the reader sees them in the order the sections ran and a
    /// list nothing wrote is absent rather than empty.
    /// </summary>
    private sealed class ModelDocLists(KVObject rootChildren)
    {
        private readonly Dictionary<string, KVObject> lists = [];

        /// <summary>The root node's own children, for the nodes that are not themselves list entries.</summary>
        public KVObject RootChildren { get; } = rootChildren;

        public KVObject MaterialGroups => Get("MaterialGroupList");
        public KVObject RenderMeshes => Get("RenderMeshList");
        public KVObject BodyGroups => Get("BodyGroupList");
        public KVObject LodGroups => Get("LODGroupList");
        public KVObject Animations => Get("AnimationList");
        public KVObject PhysicsShapes => Get("PhysicsShapeList");
        public KVObject PhysicsBodyMarkup => Get("PhysicsBodyMarkupList");
        public KVObject PhysicsJoints => Get("PhysicsJointList");
        public KVObject Attachments => Get("AttachmentList");
        public KVObject Skeleton => Get("Skeleton");
        public KVObject ModelModifiers => Get("ModelModifierList");
        public KVObject WeightLists => Get("WeightListList");
        public KVObject ScaleSets => Get("ScaleSetList");
        public KVObject HitboxSets => Get("HitboxSetList");
        public KVObject PoseParams => Get("PoseParamList");
        public KVObject NmSkeletons => Get("NmSkeletonList");
        public KVObject AnimGraph2 => Get("AnimGraph2List");
        public KVObject Vsnaps => Get("VSNAPList");
        public KVObject BreakPieces => Get("BreakPieceList");
        public KVObject GameData => Get("GameDataList");

        private KVObject Get(string className)
        {
            if (!lists.TryGetValue(className, out var children))
            {
                var list = MakeListNode(className);
                RootChildren.Add(list.Node);
                lists[className] = children = list.Children;
            }

            return children;
        }
    }

    /// <summary>
    /// Converts the model to Valve model format as a string.
    /// </summary>
    public string ToValveModel()
    {
        var kv = KVObject.Collection();

        var root = MakeListNode("RootNode");
        kv.Add("rootNode", root.Node);

        var lists = new ModelDocLists(root.Children);

        var boneMarkupList = MakeListNode("BoneMarkupList");
        root.Children.Add(boneMarkupList.Node);
        boneMarkupList.Node.Add("bone_cull_type", "None");

        AddRenderMeshNodes(lists);
        AddMaterialGroupNodes(lists);
        AddAnimationNodes(lists, ReadSequenceTables(lists));
        AddPhysicsShapeFileNodes(lists);

        if (model != null)
        {
            ExtractModelKeyValues(model, lists, root.Node);
            AddHitboxSetNodes(model, lists);

            if (model.Skeleton.Roots.Length > 0)
            {
                AddBonesRecursive(model.Skeleton.Roots, lists.Skeleton);
            }
        }

        AddPhysicsBodyNodes(lists);

        if (Translation != Vector3.Zero)
        {
            lists.ModelModifiers.Add(MakeNode("ModelModifier_Translate", ("translation", ToKVArray(Translation))));
        }

        AddVsnapNodes(lists);

        return kv.ToKV3String(format: KV3IDLookup.Get("modeldoc28"));
    }

    /// <summary>
    /// Rebuilds a VSNAPEmpty node for every particle snapshot the model references, so a recompile
    /// regenerates the snapshot rather than leaving a dangling reference.
    /// </summary>
    private void AddVsnapNodes(ModelDocLists lists)
    {
        if (modelResource is null || fileLoader is null)
        {
            return;
        }

        var externalReferences = modelResource.ExternalReferences?.ResourceRefInfoList;

        if (externalReferences is null)
        {
            return;
        }

        foreach (var reference in externalReferences)
        {
            if (!reference.Name.EndsWith(".vsnap", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var vsnapResource = fileLoader.LoadFileCompiled(reference.Name);

            if (vsnapResource?.GetBlockByType(BlockType.SNAP) is not ParticleSnapshot snapshot)
            {
                continue;
            }

            snapshot.AttributeData.TryGetValue(("position", "float3"), out var positionData);
            snapshot.AttributeData.TryGetValue(("skinning", "skinning"), out var skinningData);

            var positions = positionData as Vector3[];
            var skinning = skinningData as ParticleSnapshot.SkinningData[];
            var particles = KVObject.Array();

            for (var i = 0; i < snapshot.NumParticles; i++)
            {
                var particle = MakeNode("VSNAPParticle", ("origin", ToKVArray(positions?[i] ?? Vector3.Zero)));

                if (skinning is not null)
                {
                    var boneSlot = 0;
                    var bones = skinning[i];

                    for (var j = 0; j < bones.Weights.Length && boneSlot < 4; j++)
                    {
                        if (bones.Weights[j] <= 0f || string.IsNullOrEmpty(bones.JointNames[j]))
                        {
                            continue;
                        }

                        particle.Add($"bone_{boneSlot}", bones.JointNames[j]);
                        particle.Add($"bone_weight_{boneSlot}", bones.Weights[j]);
                        boneSlot++;
                    }
                }

                particles.Add(particle);
            }

            if (particles.Count == 0)
            {
                // An empty VSNAPEmpty makes the compiler fail with "No Vertex Particles to Write"
                // rather than emitting an empty snapshot.
                continue;
            }

            var vsnapNode = MakeNode("VSNAPEmpty",
                ("name", Path.GetFileNameWithoutExtension(reference.Name)),
                ("children", particles)
            );
            vsnapNode.Add("output_vsnap", new KVObject(reference.Name) { Flag = KVFlag.Resource });

            lists.Vsnaps.Add(vsnapNode);
        }
    }
}
