using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using ValveKeyValue;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.ResourceTypes.ModelAnimation;
using ValveResourceFormat.ResourceTypes.ModelData;
using ValveResourceFormat.ResourceTypes.ModelFlex;
using ValveResourceFormat.ResourceTypes.RubikonPhysics;
using ValveResourceFormat.Serialization.KeyValues;
using static ValveResourceFormat.IO.KVHelpers;

namespace ValveResourceFormat.IO;

partial class ModelExtract
{
    /// <summary>
    /// Converts the model to Valve model format as a string.
    /// </summary>
    public string ToValveModel()
    {
        var kv = KVObject.Collection();

        var root = MakeListNode("RootNode");
        kv.Add("rootNode", root.Node);

        Lazy<KVObject> MakeLazyList(string className)
        {
            return new Lazy<KVObject>(() =>
            {
                var list = MakeListNode(className);
                root.Children.Add(list.Node);

                return list.Children;
            });
        }

        var materialGroupList = MakeLazyList("MaterialGroupList");
        var renderMeshList = MakeLazyList("RenderMeshList");
        var bodyGroupList = MakeLazyList("BodyGroupList");
        var lodGroupList = MakeLazyList("LODGroupList");
        var animationList = MakeLazyList("AnimationList");
        var physicsShapeList = MakeLazyList("PhysicsShapeList");
        var physicsBodyMarkupList = MakeLazyList("PhysicsBodyMarkupList");
        var physicsJointList = MakeLazyList("PhysicsJointList");
        var attachmentList = MakeLazyList("AttachmentList");
        var skeleton = MakeLazyList("Skeleton");
        var modelModifierList = MakeLazyList("ModelModifierList");
        var weightLists = MakeLazyList("WeightListList");
        var scaleSetList = MakeLazyList("ScaleSetList");
        var hitboxSetList = MakeLazyList("HitboxSetList");
        var poseParamList = MakeLazyList("PoseParamList");

        var nmskelList = MakeLazyList("NmSkeletonList");
        var animGraph2List = MakeLazyList("AnimGraph2List");
        var vsnapList = MakeLazyList("VSNAPList");

        var boneMarkupList = MakeListNode("BoneMarkupList");
        root.Children.Add(boneMarkupList.Node);
        boneMarkupList.Node.Add("bone_cull_type", "None");

        if (RenderMeshesToExtract.Count != 0)
        {
            foreach (var renderMesh in RenderMeshesToExtract)
            {
                var renderMeshFile = MakeNode(
                    "RenderMeshFile",
                    ("name", renderMesh.Name),
                    ("filename", renderMesh.FileName)
                );

                if (renderMesh.ImportFilter != default)
                {
                    var importFilter = KVObject.Collection();
                    {
                        importFilter.Add("exclude_by_default", renderMesh.ImportFilter.ExcludeByDefault);
                        importFilter.Add("exception_list", MakeArray([.. renderMesh.ImportFilter.Filter.Select(s => (KVObject)s)]));
                    }

                    renderMeshFile.Add("import_filter", importFilter);
                }

                renderMeshList.Value.Add(renderMeshFile);
            }

            if (model != null)
            {
                // Mesh/Body Groups
                var meshGroups = model.Data.GetArray<string>("m_meshGroups");
                var meshGroupMasks = model.Data.GetUnsignedIntegerArray("m_refMeshGroupMasks");
                var hideInTools = Array.Empty<string>();
                if (model.Data.GetArray<string>("m_BodyGroupsHiddenInTools") is string[] hideBodyGroups)
                {
                    hideInTools = hideBodyGroups;
                }

                var groupedChoices = new Dictionary<string, List<(int ChoiceIndex, string FullName, string ChoiceName)>>();

                for (var i = 0; i < meshGroups!.Length; i++)
                {
                    var fullName = meshGroups[i];
                    var split = fullName.Split("_@");

                    if (split.Length < 2)
                    {
                        continue;
                    }

                    var groupName = split[0];
                    var choiceName = split[1];

                    groupedChoices.TryAdd(groupName, []);
                    groupedChoices[groupName].Add((i, fullName, choiceName));
                }

                foreach (var (groupName, choices) in groupedChoices)
                {
                    var choiceList = KVObject.Array();
                    var bodyGroup = MakeNode("BodyGroup",
                        ("name", groupName),
                        ("children", choiceList)
                    );

                    if (hideInTools.Contains(groupName))
                    {
                        bodyGroup.Add("hidden_in_tools", true);
                    }

                    var i = 0;
                    foreach (var (index, key, name) in choices)
                    {
                        var meshGroupChoice = MakeNode("BodyGroupChoice");

                        var choiceName = name;

                        // Fix up weird substring added to newer models
                        const string indexMarker = "#&";
                        var markerIndex = name.IndexOf(indexMarker, StringComparison.Ordinal);
                        if (markerIndex >= 0)
                        {
                            var start = markerIndex + indexMarker.Length;
                            if (start < name.Length)
                            {
                                choiceName = name[start..];
                            }
                        }

                        // Every choice needs a name to recompile, even one that only repeats its index.
                        meshGroupChoice.Add("name", string.IsNullOrEmpty(choiceName)
                            ? i.ToString(CultureInfo.InvariantCulture)
                            : choiceName);

                        if (hideInTools.Contains(key))
                        {
                            meshGroupChoice.Add("hide_in_tools", true);
                        }

                        var meshes = KVObject.Array();
                        meshGroupChoice.Add("meshes", meshes);

                        foreach (var renderMesh in RenderMeshesToExtract)
                        {
                            // No mask will show up as 'Empty' in editor
                            var mask = renderMesh.Index < meshGroupMasks.Length ? meshGroupMasks[renderMesh.Index] : 0UL;

                            if ((mask & 1UL << index) == 0)
                            {
                                continue;
                            }

                            meshes.Add(renderMesh.Name);
                        }

                        choiceList.Add(meshGroupChoice);
                        i++;
                    }

                    bodyGroupList.Value.Add(bodyGroup);
                }
            }

            if (model != null)
            {
                // LOD groups. m_refLODGroupMasks says which level each mesh belongs to (bit N => level N) and
                // m_lodGroupSwitchDistances gives each level's switch value. Emit one LODGroup per declared
                // level so a recompile rebuilds the original switch distances, and collect meshes that live in
                // every level into a single LODGroupAll rather than repeating them in each group. A level can
                // legitimately end up with no unique mesh references (every mesh at that level also lives in
                // every other level, so it moved to LODGroupAll) - the group itself still has to be written or
                // the compiler drops the switch distance entirely.
                var lodInfo = model.LodInfo;

                for (var lodLevel = 0; lodLevel < lodInfo.SwitchDistances.Count; lodLevel++)
                {
                    var meshReferences = KVObject.Array();

                    foreach (var renderMesh in RenderMeshesToExtract)
                    {
                        if (!lodInfo.IsMeshInLevel(renderMesh.Index, lodLevel) || lodInfo.IsMeshInAllLevels(renderMesh.Index))
                        {
                            continue;
                        }

                        var meshReference = KVObject.Collection();
                        meshReference.Add("mesh_name", renderMesh.Name);
                        meshReferences.Add(meshReference);
                    }

                    lodGroupList.Value.Add(MakeNode("LODGroup",
                        ("switch_threshold", lodInfo.SwitchDistances[lodLevel]),
                        ("mesh_references", meshReferences)
                    ));
                }

                if (lodInfo.SwitchDistances.Count > 0)
                {
                    var allLevelReferences = KVObject.Array();

                    foreach (var renderMesh in RenderMeshesToExtract)
                    {
                        if (!lodInfo.IsMeshInAllLevels(renderMesh.Index))
                        {
                            continue;
                        }

                        var meshReference = KVObject.Collection();
                        meshReference.Add("mesh_name", renderMesh.Name);
                        allLevelReferences.Add(meshReference);
                    }

                    if (allLevelReferences.Count > 0)
                    {
                        lodGroupList.Value.Add(MakeNode("LODGroupAll",
                            ("mesh_references", allLevelReferences)
                        ));
                    }
                }
            }

            var mesh = RenderMeshesToExtract.First();
            var attachments = mesh.Mesh.Attachments;

            foreach (var attachment in attachments.Values)
            {
                var mainInfluence = attachment[^1];

                var node = MakeNode("Attachment",
                    ("name", attachment.Name),
                    ("ignore_rotation", attachment.IgnoreRotation),
                    ("parent_bone", mainInfluence.Name),
                    ("relative_origin", ToKVArray(mainInfluence.Offset)),
                    ("relative_angles", ToKVArray(EntityTransformHelper.ToEulerAngles(mainInfluence.Rotation))),
                    ("weight", mainInfluence.Weight)
                );

                if (attachment.Length > 1)
                {
                    var children = KVObject.Array();
                    for (var i = 0; i < attachment.Length - 1; i++)
                    {
                        var influence = attachment[i];
                        var childNode = MakeNode("AttachmentInfluence",
                            ("parent_bone", influence.Name),
                            ("relative_origin", ToKVArray(influence.Offset)),
                            ("relative_angles", ToKVArray(EntityTransformHelper.ToEulerAngles(influence.Rotation))),
                            ("weight", influence.Weight)
                        );

                        children.Add(childNode);
                    }
                    node.Add("children", children);
                }

                attachmentList.Value.Add(node);
            }
        }

        // Material groups / skins.
        if (model?.GetMaterialGroups().ToList() is { Count: > 0 } materialGroups)
        {
            var defaultMaterials = materialGroups[0].Materials;

            materialGroupList.Value.Add(MakeNode("DefaultMaterialGroup",
                ("name", materialGroups[0].Name ?? "default"),
                ("remaps", KVObject.Array())
            ));

            for (var groupIndex = 1; groupIndex < materialGroups.Count; groupIndex++)
            {
                var variantMaterials = materialGroups[groupIndex].Materials;
                if (variantMaterials.Length == 0)
                {
                    continue;
                }

                var remaps = KVObject.Array();
                var pairCount = Math.Min(defaultMaterials.Length, variantMaterials.Length);
                for (var i = 0; i < pairCount; i++)
                {
                    var fromMaterial = defaultMaterials[i];
                    var toMaterial = variantMaterials[i];

                    // A null slot carries no remap for that material.
                    if (fromMaterial == null || toMaterial == null
                        || string.Equals(fromMaterial, toMaterial, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    remaps.Add(MakeNode("BaseMaterialRemap",
                        ("from", fromMaterial),
                        ("to", toMaterial)
                    ));
                }

                materialGroupList.Value.Add(MakeNode("MaterialGroup",
                    ("name", materialGroups[groupIndex].Name ?? groupIndex.ToString(CultureInfo.InvariantCulture)),
                    ("remaps", remaps)
                ));
            }
        }

        var modelSequenceData = model?.Resource?.GetBlockByType(BlockType.ASEQ) as KeyValuesOrNTRO;
        var additionalSequenceData = new Dictionary<string, KVObject>();
        string[]? sequenceLocalReferenceArray = null;
        string[]? poseParamNames = null;
        string[]? boneMaskNames = null;

        if (modelSequenceData?.Data is KVObject sequenceData)
        {
            ExtractSequenceData(modelSequenceData);
            ExtractScaleSets(modelSequenceData);

            foreach (var data in sequenceData.GetArray("m_localS1SeqDescArray"))
            {
                additionalSequenceData.Add(data.GetStringProperty("m_sName"), data);
            }

            var poseParams = sequenceData.GetArray("m_localPoseParamArray");
            ExtractPoseParams(poseParams);

            poseParamNames = [.. poseParams.Select(x => x.GetStringProperty("m_sName"))];
            sequenceLocalReferenceArray = sequenceData.GetArray<string>("m_localSequenceNameArray");
            boneMaskNames = [.. sequenceData.GetArray("m_localBoneMaskArray").Select(x => x.GetStringProperty("m_sName"))];
        }

        if (AnimationsToExtract.Count > 0 || additionalSequenceData.Count > 0)
        {
            var animationToFolder = new Dictionary<string, KVObject>(AnimationsToExtract.Count);
            if (modelSequenceData?.Data.GetSubCollection("m_keyValues") is KVObject sequenceKeyValues)
            {
                if (sequenceKeyValues.GetSubCollection("faceposer_folders") is KVObject faceposerFolders)
                {
                    foreach (var (folderName, _) in faceposerFolders)
                    {
                        var animationNames = faceposerFolders.GetArray<string>(folderName);

                        var (folderNode, children) = MakeListNode("Folder");
                        folderNode.Add("name", folderName);
                        animationList.Value.Add(folderNode);

                        foreach (var animationName in animationNames!)
                        {
                            animationToFolder.Add(animationName, children);
                        }
                    }
                }
            }

            void AddToFolderOrRoot(string name, KVObject node)
            {
                var folderOrRoot = animationToFolder.GetValueOrDefault(name, animationList.Value);
                folderOrRoot.Add(node);
            }

            var nodeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var animation in AnimationsToExtract)
            {
                nodeNames.Add(animation.Anim.Name);
            }

            foreach (var name in additionalSequenceData.Keys)
            {
                nodeNames.Add(name);
            }

            foreach (var (name, aseq) in additionalSequenceData)
            {
                // A sequence that plays no animation directly is either the true bind pose (compiled
                // with a "bind_pose" flag) or an EmptyAnim: a synthetic base of a declared length that
                // exists only to carry auto layers, compiled with an explicit frame count and rate
                // instead. Both compile from a node with no source_filename, so a decompile can only
                // tell them apart by which one of those two the compiler already wrote back.
                var playsNothing = aseq.GetSubCollection("m_fetch").GetIntegerArray("m_localReferenceArray").Length == 0;
                var sequenceKeys = aseq.GetSubCollection("m_SequenceKeys");

                if (playsNothing || sequenceKeys?.GetBooleanProperty("bind_pose") == true)
                {
                    var emptyAnimKeys = sequenceKeys?.GetSubCollection("keyvalues");
                    var isEmptyAnim = emptyAnimKeys != null && emptyAnimKeys.TryGetValue("numframes", out _);

                    var transition = aseq.GetSubCollection("m_transition");
                    var bindPoseFlags = aseq.GetSubCollection("m_flags");
                    var bindPose = MakeNode(isEmptyAnim ? "EmptyAnim" : "AnimBindPose",
                        ("name", name),
                        ("fade_in_time", transition.GetFloatProperty("m_flFadeInTime")),
                        ("fade_out_time", transition.GetFloatProperty("m_flFadeOutTime")),
                        ("looping", bindPoseFlags.GetBooleanProperty("m_bLooping")),
                        ("delta", bindPoseFlags.GetBooleanProperty("m_bLegacyDelta")),
                        ("worldSpace", bindPoseFlags.GetBooleanProperty("m_bLegacyWorldspace")),
                        ("hidden", bindPoseFlags.GetBooleanProperty("m_bHidden"))
                    );

                    var frameCount = 0;

                    if (isEmptyAnim)
                    {
                        frameCount = emptyAnimKeys!.GetInt32Property("numframes");
                        bindPose.Add("frame_count", frameCount);
                        bindPose.Add("frame_rate", emptyAnimKeys!.GetFloatProperty("fps"));
                    }

                    var bindPoseWeightList = GetWeightListName(name, additionalSequenceData, boneMaskNames);

                    if (bindPoseWeightList != null)
                    {
                        bindPose.Add("weight_list_name", bindPoseWeightList);
                    }

                    var bindPoseChildren = KVObject.Array();

                    AddActivities(bindPose, bindPoseChildren, [.. aseq.GetArray("m_activityArray")
                        .Select(activity => (activity.GetStringProperty("m_name"), activity.GetInt32Property("m_nWeight")))]);

                    if (isEmptyAnim && sequenceLocalReferenceArray != null)
                    {
                        foreach (var autoLayerKV in aseq.GetArray("m_autoLayerArray"))
                        {
                            var autoLayer = new AnimationAutoLayer(autoLayerKV);
                            bindPoseChildren.Add(ProcessAnimationAutoLayer(frameCount, autoLayer, sequenceLocalReferenceArray, poseParamNames ?? [], nodeNames));
                        }
                    }

                    if (ProcessFaceposerKeys(sequenceKeys) is KVObject bindPoseFaceposerKeys)
                    {
                        bindPoseChildren.Add(bindPoseFaceposerKeys);
                    }

                    if (bindPoseChildren.Count > 0)
                    {
                        bindPose.Add("children", bindPoseChildren);
                    }

                    AddToFolderOrRoot(name, bindPose);
                }
            }

            var sequences = AnimationsToExtract.Where(x => x.Anim.FromSequence);
            foreach (var animation in sequences)
            {
                if (animation.Anim.IsBlend && sequenceLocalReferenceArray != null && poseParamNames != null)
                {
                    var blendAnimEvents = additionalSequenceData.TryGetValue(animation.Anim.Name, out var blendSequenceData)
                        && blendSequenceData.GetSubCollection("m_SequenceKeys")?.GetBooleanProperty("blend_anim_events") == true;

                    var blendNode = ProcessBlendSequence(animation.Anim, sequenceLocalReferenceArray, poseParamNames, nodeNames, blendAnimEvents);
                    var blendWeightList = GetWeightListName(animation.Anim.Name, additionalSequenceData, boneMaskNames);

                    if (blendWeightList != null)
                    {
                        blendNode.Add("weight_list_name", blendWeightList);
                    }

                    AddToFolderOrRoot(animation.Anim.Name, blendNode);
                    continue;
                }

                var animationFile = MakeNode(
                    "AnimFile",
                    ("name", animation.Anim.Name),
                    ("source_filename", animation.FileName),
                    ("fade_in_time", animation.Anim.SequenceParams.FadeInTime),
                    ("fade_out_time", animation.Anim.SequenceParams.FadeOutTime),
                    ("looping", animation.Anim.IsLooping),
                    ("delta", animation.Anim.Delta),
                    ("worldSpace", animation.Anim.Worldspace),
                    ("hidden", animation.Anim.Hidden)
                );

                var childrenKV = KVObject.Array();

                AddActivities(animationFile, childrenKV, animation.Anim);

                var weightList = GetWeightListName(animation.Anim.Name, additionalSequenceData, boneMaskNames);

                if (weightList != null)
                {
                    animationFile.Add("weight_list_name", weightList);
                }

                foreach (var localHierarchy in animation.Anim.LocalHierarchy)
                {
                    childrenKV.Add(MakeNode("LocalHierarchy",
                        ("bone_name", localHierarchy.Bone),
                        ("new_parent_bone_name", localHierarchy.NewParent),
                        ("start_frame", localHierarchy.StartFrame),
                        ("peak_frame", localHierarchy.PeakFrame),
                        ("tail_frame", localHierarchy.TailFrame),
                        ("end_frame", localHierarchy.EndFrame)
                    ));
                }

                if (model != null)
                {
                    foreach (var boneScale in ProcessBoneScales(model.Skeleton, model.FlexControllers, animation.Anim))
                    {
                        childrenKV.Add(boneScale);
                    }
                }

                if (animation.Anim.HasMovementData())
                {
                    var flags = animation.Anim.Movements[0].MotionFlags;
                    var extractMotion = MakeNode("ExtractMotion",
                        ("extract_tx", flags.HasFlag(ModelAnimationMotionFlags.TX)),
                        ("extract_ty", flags.HasFlag(ModelAnimationMotionFlags.TY)),
                        // never extract vertical. on recompile it makes the compiler counter-bake the root
                        // and float the whole model up. the engine doesn't apply vertical root motion.
                        ("extract_tz", false),
                        ("extract_rz", flags.HasFlag(ModelAnimationMotionFlags.RZ)),
                        ("linear", flags.HasFlag(ModelAnimationMotionFlags.Linear)),
                        ("quadratic", false),
                        ("motion_type", "uniform")
                    );

                    childrenKV.Add(extractMotion);
                }
                foreach (var animEvent in animation.Anim.Events)
                {
                    var animEventNode = MakeNode("AnimEvent",
                        ("event_class", animEvent.Name),
                        ("event_frame", animEvent.Frame)
                    );

                    if (animEvent.EndFrame != -1)
                    {
                        animEventNode.Add("event_end_frame", animEvent.EndFrame);
                    }

                    if (animEvent.Duration != 0f)
                    {
                        animEventNode.Add("event_duration", animEvent.Duration);
                    }

                    if (animEvent.EventData != null)
                    {
                        animEventNode.Add("event_keys", animEvent.EventData);
                    }
                    childrenKV.Add(animEventNode);
                }

                if (sequenceLocalReferenceArray != null && poseParamNames != null)
                {
                    foreach (var autoLayer in animation.Anim.AutoLayers)
                    {
                        var layerNode = ProcessAnimationAutoLayer(animation.Anim.CycleFrames, autoLayer, sequenceLocalReferenceArray, poseParamNames, nodeNames);
                        childrenKV.Add(layerNode);
                    }
                }

                if (animation.Anim.Autoplay)
                {
                    var autoLayer = MakeNode("AnimAutoLayer");
                    childrenKV.Add(autoLayer);
                }

                if (poseParamNames != null && animation.Anim.Fetch != null && animation.Anim.Fetch.Value.LocalCyclePoseParameter != -1)
                {
                    var poseParamIndex = animation.Anim.Fetch.Value.LocalCyclePoseParameter;
                    var poseParam = poseParamNames[poseParamIndex];

                    var autoLayer = MakeNode("AnimCycleOverride", [
                        ("cycle_type", "Pose To Cycle"),
                        ("pose_param_name", poseParam),
                    ]);
                    childrenKV.Add(autoLayer);
                }

                if (animation.Anim.Realtime)
                {
                    var autoLayer = MakeNode("AnimCycleOverride", [
                        ("cycle_type", "Auto Cycle"),
                        ("pose_param_name", ""),
                    ]);
                    childrenKV.Add(autoLayer);
                }

                if (additionalSequenceData.TryGetValue(animation.Anim.Name, out var animSequenceData))
                {
                    var sequenceKeys = animSequenceData.GetSubCollection("m_SequenceKeys");
                    if (sequenceKeys != null)
                    {
                        // other keys seen:
                        // bind_pose = true

                        if (sequenceKeys.GetSubCollection("AnimGameplayTiming") is KVObject animGameplayTiming)
                        {
                            childrenKV.Add(MakeNode("AnimGameplayTiming", animGameplayTiming));
                        }

                        if (ProcessFaceposerKeys(sequenceKeys) is KVObject faceposerKeys)
                        {
                            childrenKV.Add(faceposerKeys);
                        }
                    }
                }

                if (childrenKV.Count > 0)
                {
                    animationFile.Add("children", childrenKV);
                }

                AddToFolderOrRoot(animation.Anim.Name, animationFile);
            }
        }

        if (PhysHullsToExtract.Count > 0 || PhysMeshesToExtract.Count > 0)
        {
            if (Type == ModelExtractType.Map_PhysicsToRenderMesh)
            {
                if (PhysicsToRenderMaterialNameProvider is null)
                {
                    RemapMaterials(null, globalReplace: true);
                }
                else
                {
                    var remapTable = SurfaceTagCombos.ToDictionary(
                        combo => combo.StringMaterial,
                        combo => PhysicsToRenderMaterialNameProvider(combo)
                    );
                    RemapMaterials(remapTable, globalReplace: false);
                }
            }

            foreach (var (physHull, fileName, parentBone, _) in PhysHullsToExtract)
            {
                HandlePhysMeshNode(physHull, fileName, parentBone);
            }

            foreach (var (physMesh, fileName, parentBone, _) in PhysMeshesToExtract)
            {
                HandlePhysMeshNode(physMesh, fileName, parentBone);
            }
        }

        if (model != null)
        {
            ExtractModelKeyValues(model, root.Node);
            ExtractHitboxSets(model);

            if (model.Skeleton.Roots.Length > 0)
            {
                AddBonesRecursive(model.Skeleton.Roots, skeleton.Value);
            }
        }

        if (physAggregateData is not null)
        {
            // Bones that already carry body markup as game data round-trip their mass through it, and the
            // compiler rejects a second markup for the same body. The lookup is case-insensitive because
            // resourcecompiler matches target_body to the existing markup's bone name that way.
            var existingMarkupBones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var physicsBodyMarkupData = model?.KeyValues.GetSubCollection("CPhysicsBodyGameMarkupData");
            var physicsBodyMarkupByBoneName = physicsBodyMarkupData?.GetSubCollection("m_PhysicsBodyMarkupByBoneName");

            if (physicsBodyMarkupByBoneName != null)
            {
                foreach (var (boneName, _) in physicsBodyMarkupByBoneName)
                {
                    existingMarkupBones.Add(boneName);
                }
            }

            for (var i = 0; i < physAggregateData.Parts.Length; i++)
            {
                var physicsPart = physAggregateData.Parts[i];
                var parentBone = physAggregateData.GetParentBoneName(i);

                var hasOverrides = physicsPart.Mass != 0f
                    || physicsPart.InertiaScale != 1f
                    || physicsPart.LinearDamping != 0f
                    || physicsPart.AngularDamping != 0f
                    || physicsPart.OverrideMassCenter;

                if (hasOverrides && !existingMarkupBones.Contains(parentBone))
                {
                    var bodyMarkup = MakeNode("PhysicsBodyMarkup", ("target_body", parentBone));

                    if (physicsPart.Mass != 0f)
                    {
                        bodyMarkup.Add("mass_override", physicsPart.Mass);
                    }

                    if (physicsPart.InertiaScale != 1f)
                    {
                        bodyMarkup.Add("inertia_scale", physicsPart.InertiaScale);
                    }

                    if (physicsPart.LinearDamping != 0f)
                    {
                        bodyMarkup.Add("linear_damping", physicsPart.LinearDamping);
                    }

                    if (physicsPart.AngularDamping != 0f)
                    {
                        bodyMarkup.Add("angular_damping", physicsPart.AngularDamping);
                    }

                    if (physicsPart.OverrideMassCenter)
                    {
                        bodyMarkup.Add("use_mass_center_override", true);
                        bodyMarkup.Add("mass_center_override", ToKVArray(physicsPart.MassCenterOverride));
                    }

                    physicsBodyMarkupList.Value.Add(bodyMarkup);
                }

                foreach (var sphere in physicsPart.Shape.Spheres)
                {
                    var physicsShapeSphere = MakeNode(
                        "PhysicsShapeSphere",
                        ("parent_bone", parentBone),
                        ("surface_prop", PhysicsSurfaceNames[sphere.SurfacePropertyIndex]),
                        ("collision_tags", string.Join(" ", PhysicsCollisionTags[sphere.CollisionAttributeIndex])),
                        ("radius", sphere.Shape.Radius),
                        ("center", ToKVArray(sphere.Shape.Center)),
                        ("name", sphere.UserFriendlyName ?? string.Empty)
                    );

                    AddHitGroup(physicsShapeSphere, sphere);

                    physicsShapeList.Value.Add(physicsShapeSphere);
                }

                foreach (var capsule in physicsPart.Shape.Capsules)
                {
                    var physicsShapeCapsule = MakeNode(
                        "PhysicsShapeCapsule",
                        ("parent_bone", parentBone),
                        ("surface_prop", PhysicsSurfaceNames[capsule.SurfacePropertyIndex]),
                        ("collision_tags", string.Join(" ", PhysicsCollisionTags[capsule.CollisionAttributeIndex])),
                        ("radius", capsule.Shape.Radius),
                        ("point0", ToKVArray(capsule.Shape.Center[0])),
                        ("point1", ToKVArray(capsule.Shape.Center[1])),
                        ("name", capsule.UserFriendlyName ?? string.Empty)
                    );

                    AddHitGroup(physicsShapeCapsule, capsule);

                    physicsShapeList.Value.Add(physicsShapeCapsule);
                }
            }

            foreach (var joint in physAggregateData.Joints)
            {
                var jointNode = BuildPhysicsJoint(physAggregateData, joint);

                if (jointNode is not null)
                {
                    physicsJointList.Value.Add(jointNode);
                }
            }
        }

        if (Translation != Vector3.Zero)
        {
            modelModifierList.Value.Add(MakeNode("ModelModifier_Translate", ("translation", ToKVArray(Translation))));
        }

        ExtractVsnapReferences();

        return kv.ToKV3String(format: KV3IDLookup.Get("modeldoc28"));

        void ExtractVsnapReferences()
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

                vsnapList.Value.Add(vsnapNode);
            }
        }

        #region Local Functions
        void HandlePhysMeshNode<TShape>(ShapeDescriptor<TShape> shapeDesc, string fileName, string parentBone)
            where TShape : struct
        {
            var surfacePropName = PhysicsSurfaceNames[shapeDesc.SurfacePropertyIndex];
            var collisionTags = PhysicsCollisionTags[shapeDesc.CollisionAttributeIndex];

            if (Type == ModelExtractType.Map_PhysicsToRenderMesh)
            {
                renderMeshList.Value.Add(MakeNode("RenderMeshFile", ("filename", fileName)));
                return;
            }

            var className = shapeDesc switch
            {
                HullDescriptor => "PhysicsHullFile",
                MeshDescriptor => "PhysicsMeshFile",
                _ => throw new NotImplementedException()
            };

            var shapeName = shapeDesc.UserFriendlyName ?? Path.GetFileNameWithoutExtension(fileName);

            // TODO: per faceSet surface_prop
            var physicsShapeFile = MakeNode(
                className,
                ("filename", fileName),
                ("parent_bone", parentBone),
                ("surface_prop", surfacePropName),
                ("collision_tags", string.Join(" ", collisionTags)),
                ("name", shapeName)
            );

            AddHitGroup(physicsShapeFile, shapeDesc);

            physicsShapeList.Value.Add(physicsShapeFile);
        }

        void RemapMaterials(
            IReadOnlyDictionary<string, string>? remapTable = null,
            bool globalReplace = false,
            string globalDefault = "materials/tools/toolsnodraw.vmat")
        {
            var remaps = KVObject.Array();
            materialGroupList.Value.Add(
                MakeNode(
                    "DefaultMaterialGroup",
                    ("remaps", remaps),
                    ("use_global_default", globalReplace),
                    ("global_default_material", globalDefault)
                )
            );

            if (globalReplace || remapTable == null)
            {
                return;
            }

            foreach (var (from, to) in remapTable)
            {
                var remap = KVObject.Collection();
                remap.Add("from", from);
                remap.Add("to", to);
                remaps.Add(remap);
            }
        }

        KVObject GetHitboxNode(Hitbox hitbox)
        {
            var node = hitbox.ShapeType switch
            {
                Hitbox.HitboxShape.Box => MakeNode("Hitbox",
                    ("hitbox_mins", ToKVArray(hitbox.MinBounds)),
                    ("hitbox_maxs", ToKVArray(hitbox.MaxBounds))
                ),
                Hitbox.HitboxShape.Capsule => MakeNode("HitboxCapsule",
                    ("radius", hitbox.ShapeRadius),
                    ("point0", ToKVArray(hitbox.MinBounds)),
                    ("point1", ToKVArray(hitbox.MaxBounds))
                ),
                Hitbox.HitboxShape.Sphere => MakeNode("HitboxSphere",
                    ("center", ToKVArray(hitbox.MinBounds)),
                    ("radius", hitbox.ShapeRadius)
                ),
                _ => throw new NotImplementedException($"Unknown hitbox shape type: {hitbox.ShapeType}")
            };

            node.Add("name", hitbox.Name);
            node.Add("parent_bone", hitbox.BoneName);
            node.Add("surface_property", hitbox.SurfaceProperty);
            node.Add("translation_only", hitbox.TranslationOnly);
            node.Add("group_id", hitbox.GroupId);

            return node;
        }

        void ExtractHitboxSets(Model model)
        {
            if (model.HitboxSets == null)
            {
                return;
            }

            foreach (var pair in model.HitboxSets)
            {
                var children = KVObject.Array();
                var hitboxSet = MakeNode("HitboxSet", ("name", pair.Key), ("children", children));

                foreach (var hitbox in pair.Value)
                {
                    var hitboxNode = GetHitboxNode(hitbox);
                    children.Add(hitboxNode);
                }

                hitboxSetList.Value.Add(hitboxSet);
            }
        }

        void ExtractSequenceData(KeyValuesOrNTRO sequenceData)
        {
            var boneMasks = sequenceData.Data.GetArray("m_localBoneMaskArray");
            var boneNames = sequenceData.Data.GetArray<string>("m_localBoneNameArray");

            foreach (var boneMask in boneMasks!)
            {
                var name = boneMask.GetStringProperty("m_sName");
                var boneArray = boneMask.GetIntegerArray("m_nLocalBoneArray");
                var boneWeights = boneMask.GetFloatArray("m_flBoneWeightArray");
                var masterMorphWeight = boneMask.GetFloatProperty("m_flDefaultMorphCtrlWeight", 1f);
                var morphCtrlWeightArray = boneMask.GetArray("m_morphCtrlWeightArray");

                // skip a default mask that carries nothing but its schema defaults
                if (name == "default" && boneArray.Length == 0 && masterMorphWeight == 1f
                    && (morphCtrlWeightArray == null || morphCtrlWeightArray.Count == 0))
                {
                    continue;
                }

                var weights = KVObject.Array();
                var morphWeights = KVObject.Array();
                var weightListNode = MakeNode("WeightList",
                    ("name", name),
                    ("weights", weights),
                    ("master_morph_weight", masterMorphWeight),
                    ("morph_weights", morphWeights)
                );

                foreach (var (boneIndex, boneWeight) in boneArray.Zip(boneWeights))
                {
                    var weightDefinition = KVObject.Collection();
                    var boneName = boneNames![boneIndex];

                    weightDefinition.Add("bone", boneName);
                    weightDefinition.Add("weight", boneWeight);
                    weights.Add(weightDefinition);
                }

                foreach (var morphWeightPair in morphCtrlWeightArray ?? [])
                {
                    var morphWeightDefinition = KVObject.Collection();

                    morphWeightDefinition.Add("morph", (string)morphWeightPair[0]);
                    morphWeightDefinition.Add("weight", (float)morphWeightPair[1]);
                    morphWeights.Add(morphWeightDefinition);
                }

                weightLists.Value.Add(weightListNode);
            }
        }

        void ExtractScaleSets(KeyValuesOrNTRO sequenceData)
        {
            var scaleSets = sequenceData.Data.GetArray("m_localScaleSetArray");

            if (scaleSets == null || scaleSets.Count == 0)
            {
                return;
            }

            var boneNames = sequenceData.Data.GetArray<string>("m_localBoneNameArray");
            var bonesByName = model?.Skeleton.Bones.ToDictionary(static bone => bone.Name);

            foreach (var scaleSet in scaleSets)
            {
                var boneArray = scaleSet.GetIntegerArray("m_nLocalBoneArray");
                var boneScaleArray = scaleSet.GetFloatArray("m_flBoneScaleArray");
                var rootOffsetArray = scaleSet.GetFloatArray("m_vRootOffset");
                var rootOffset = new Vector3(rootOffsetArray[0], rootOffsetArray[1], rootOffsetArray[2]);

                // The compiler divides each bone's authored scale by its nearest ancestor's authored
                // scale (within this same scale set, defaulting to 1 with no such ancestor), so the
                // compiled value is a scale relative to the set's own nearest scaled ancestor rather
                // than an independent per-bone multiplier. Recover the authored value by inverting that
                // walk up the skeleton, memoized since a deep chain revisits the same ancestors.
                var compiledScaleByBone = new Dictionary<string, float>(boneArray.Length);

                for (var i = 0; i < boneArray.Length; i++)
                {
                    compiledScaleByBone[boneNames![boneArray[i]]] = boneScaleArray[i];
                }

                var authoredScaleByBone = new Dictionary<string, float>(boneArray.Length);

                float GetAuthoredScale(string boneName)
                {
                    if (authoredScaleByBone.TryGetValue(boneName, out var cached))
                    {
                        return cached;
                    }

                    var parentScale = 1f;
                    var ancestor = bonesByName?.GetValueOrDefault(boneName)?.Parent;

                    while (ancestor != null)
                    {
                        if (compiledScaleByBone.ContainsKey(ancestor.Name))
                        {
                            parentScale = GetAuthoredScale(ancestor.Name);
                            break;
                        }

                        ancestor = ancestor.Parent;
                    }

                    var authored = compiledScaleByBone[boneName] * parentScale;
                    authoredScaleByBone[boneName] = authored;

                    return authored;
                }

                var scales = KVObject.Array();

                foreach (var boneIndex in boneArray)
                {
                    var boneName = boneNames![boneIndex];
                    var scaleDefinition = KVObject.Collection();
                    scaleDefinition.Add("bone", boneName);
                    scaleDefinition.Add("scale", GetAuthoredScale(boneName));
                    scales.Add(scaleDefinition);
                }

                scaleSetList.Value.Add(MakeNode("ScaleSet",
                    ("name", scaleSet.GetStringProperty("m_sName")),
                    ("root_offset", ToKVArray(rootOffset)),
                    ("scales", scales)
                ));
            }
        }

        void ExtractPoseParams(IReadOnlyList<KVObject> poseParamsData)
        {
            foreach (var poseParam in poseParamsData)
            {
                var name = poseParam.GetStringProperty("m_sName");
                var start = poseParam.GetFloatProperty("m_flStart");
                var end = poseParam.GetFloatProperty("m_flEnd");
                var loop = poseParam.GetFloatProperty("m_flLoop");
                var looping = poseParam.GetBooleanProperty("m_bLooping");

                var poseParamNode = MakeNode("PoseParam",
                    ("name", name),
                    ("poseparam_min", start),
                    ("poseparam_max", end),
                    ("poseparam_looping", looping),
                    ("poseparam_loop", loop)
                );

                poseParamList.Value.Add(poseParamNode);
            }
        }

        void ExtractModelKeyValues(Model model, KVObject rootNode)
        {
            if (model.Data.ContainsKey("m_refAnimIncludeModels"))
            {
                foreach (var animIncludeModel in model.Data.GetArray<string>("m_refAnimIncludeModels")!)
                {
                    animationList.Value.Add(MakeNode("AnimIncludeModel", ("model", animIncludeModel)));
                }
            }

            foreach (var (attributeName, numChannels) in model.GetAnimatedMaterialAttributes())
            {
                if (numChannels == 1)
                {
                    animationList.Value.Add(MakeNode("AnimatedMaterialAttributeValue",
                        ("material_attribute_name", attributeName),
                        ("target_value", 0f)
                    ));
                }
                else
                {
                    animationList.Value.Add(MakeNode("AnimatedMaterialAttributeColor",
                        ("material_attribute_name", attributeName),
                        ("target_color", MakeArray(255f, 255f, 255f, 255f))
                    ));
                }
            }

            if (model.Data.ContainsKey("m_vecNmSkeletonRefs"))
            {
                foreach (var skeletonRef in model.Data.GetArray<string>("m_vecNmSkeletonRefs"))
                {
                    nmskelList.Value.Add(MakeNode("NmSkeletonReference", ("filename", skeletonRef)));
                }
            }

            if (model.Data.ContainsKey("m_animGraph2Refs"))
            {
                var animGraph2Refs = model.Data.GetArray("m_animGraph2Refs");
                for (int i = 0; i < animGraph2Refs.Count; i++)
                {
                    var refObj = animGraph2Refs[i];
                    var identifier = refObj.GetStringProperty("m_sIdentifier");
                    var graphPath = refObj.GetStringProperty("m_hGraph");

                    if (i == 0)
                    {
                        animGraph2List.Value.Add(MakeNode("DefaultAnimGraph2", ("filename", graphPath)));
                    }
                    else
                    {
                        animGraph2List.Value.Add(MakeNode("AnimGraph2", ("name", identifier), ("filename", graphPath)));
                    }
                }
            }

            var breakPieceList = MakeLazyList("BreakPieceList");
            var gameDataList = MakeLazyList("GameDataList");

            var keyvalues = model.KeyValues;

            if (keyvalues.Count == 0)
            {
                return;
            }

            if (keyvalues.ContainsKey("anim_graph_resource"))
            {
                rootNode.Add("anim_graph_name", keyvalues.GetStringProperty("anim_graph_resource"));
            }

            if (keyvalues.ContainsKey("BoneConstraintList"))
            {
                var boneConstraintListData = keyvalues.GetArray("BoneConstraintList");
                var boneConstraintList = ExtractBoneConstraints(boneConstraintListData);
                root.Children.Add(boneConstraintList);
            }

            if (BuildIKData(model) is { } ikData)
            {
                root.Children.Add(ikData);
            }

            var genericDataClasses = new string[] {
                "prop_data",
                "character_arm_config",
                "vr_carry_type",
                "door_sounds",
                "nav_data",
                "npc_foot_sweep",
                "ai_model_info",
                "breakable_door_model",
                "dynamic_interactions",
                "explosion_behavior",
                "eye_occlusion_renderer",
                "fire_interactions",
                "gastank_markup",
                "hand_conform_data",
                "handpose_data",
                "physgun_interactions",
                "weapon_metadata",
                "glove_viewmodel_reference",
                "composite_material_order",
                "patch_camera_preset_list",
                "camera_settings",
                "scene_data_map",
                "particle_settings",
                "damage_number_settings",
                "CitadelCameraSettings_t",
                "CCitadelHeroModelGameData_t",
                "CCitadelNPCModelGameData_t",
                "CitadelUnitStatusSettings_t",
                "CitadelModelDamageNumberSettings_t",
                "CitadelModelParticleSettings_t",
                "CitadelTaggedSoundSettings_t",
                "CitadelModelSceneData_t",
                "CitadelMuzzleSettings_t",
                "CitadelTeamRelativeParticleSettings_t",
                "CitadelEventIDToBodyGroupMapping_t",
                //"AttachmentCameraData", - is autogenerated from AttachmentCameraPreview/ExporttoRuntimeModel modeldoc node/parameter
                "CDestructiblePart",
                "CDestructiblePartsSystemData",
                "DeformablePropModelGameData_t",
                "CPhysicsBodyGameMarkupData",
                "electrical_interactions",
                "world_interactions",
            };

            var genericDataClassesList = new (string ListKey, string Class)[] {
            ("ao_proxy_capsule_list", "ao_proxy_capsule"),
            ("ao_proxy_box_list", "ao_proxy_box"),
            ("particles_list", "particle"),
            ("hand_pose_list", "hand_pose_pair"),
            ("eye_data_list", "eye"),
            ("bodygroup_driven_morph_list", "bodygroup_driven_morph"),
            ("materialgroup_driven_morph_list", "materialgroup_driven_morph"),
            ("animating_breakable_stage_list", "animating_breakable_stage"),
            ("cables_list", "cable"),
            ("high_quality_shadows_region_list", "high_quality_shadows_region"),
            ("particle_cfg_list", "particle_cfg"),
            ("snapshot_weights_upperbody_list", "snapshot_weights_upperbody"),
            ("snapshot_weights_all_list", "snapshot_weights_all"),
            ("bodygroup_preset_list", "bodygroup_preset"),
            ("muzzle_desc_list", "muzzle_settings"),
            ("unit_status_settings_list", "unit_status_settings"),
            ("team_relative_particles_cfg_list", "team_relative_particle_settings"),
            ("CNPCPhysicsHull", "CNPCPhysicsHull"), // exports as list, needs m_sName changed to name near game_class
        };

            foreach (var genericDataClass in genericDataClasses)
            {
                if (!keyvalues.ContainsKey(genericDataClass))
                {
                    continue;
                }

                // Some of these classes hold one entry and others hold an array of them, and an
                // array wrapped in a single node compiles back to an unnamed member.
                if (keyvalues[genericDataClass].ValueType == KVValueType.Array)
                {
                    foreach (var entry in keyvalues.GetArray(genericDataClass))
                    {
                        AddGenericGameData(gameDataList.Value, genericDataClass, entry);
                    }

                    continue;
                }

                var genericData = keyvalues.GetSubCollection(genericDataClass);
                if (genericData != null)
                {
                    AddGenericGameData(gameDataList.Value, genericDataClass, genericData);
                }
            }

            foreach (var genericDataClass in genericDataClassesList)
            {
                var dataKey = genericDataClass.ListKey;
                if (keyvalues.ContainsKey(dataKey))
                {
                    var genericDataList = keyvalues.GetArray(dataKey);
                    foreach (var genericData in genericDataList!)
                    {
                        AddGenericGameData(gameDataList.Value, genericDataClass.Class, genericData);
                    }
                }
            }

            if (keyvalues.ContainsKey("LookAtList"))
            {
                var lookAtList = keyvalues.GetSubCollection("LookAtList");
                foreach (var (_, item) in lookAtList)
                {
                    if (item.ValueType == KVValueType.Collection)
                    {
                        AddGenericGameData(gameDataList.Value, "LookAtChain", item, "lookat_chain");
                    }
                }
            }

            if (keyvalues.ContainsKey("MovementSettings"))
            {
                var movementSettings = keyvalues.GetSubCollection("MovementSettings");
                AddGenericGameData(gameDataList.Value, "MovementSettings", movementSettings, "movementsettings");
            }

            if (keyvalues.ContainsKey("FeetSettings"))
            {
                var feetSettings = keyvalues.GetSubCollection("FeetSettings");
                var feetNode = ConvertFeetSettings(feetSettings!);
                if (feetNode != null)
                {
                    gameDataList.Value.Add(feetNode);
                }
            }

            if (keyvalues.ContainsKey("break_list"))
            {
                foreach (var breakPiece in keyvalues.GetArray("break_list")!)
                {
                    var breakPieceFile = MakeNode("BreakPieceExternal", breakPiece);
                    breakPieceList.Value.Add(breakPieceFile);
                }
            }

            static KVObject? ConvertFeetSettings(KVObject feetSettings)
            {
                var children = KVObject.Array();

                // Field mappings from compiled to source names
                var footFieldMappings = new (string CompiledName, string SourceName)[]
                {
                    ("m_name", "name"),
                    ("m_ankleBoneName", "anklebone"),
                    ("m_toeBoneName", "toebone"),
                    ("m_vBallOffset", "balloffset"),
                    ("m_vHeelOffset", "heeloffset"),
                    ("m_flTraceHeight", "traceheight"),
                    ("m_flTraceRadius", "traceradius"),
                };

                foreach (var (_, footEntry) in feetSettings.Children)
                {
                    if (footEntry.ValueType != KVValueType.Collection)
                    {
                        continue;
                    }

                    var footNode = MakeNode("Foot");

                    // Map compiled field names to source field names
                    foreach (var (compiledName, sourceName) in footFieldMappings)
                    {
                        if (footEntry.ContainsKey(compiledName))
                        {
                            footNode.Add(sourceName, footEntry[compiledName]);
                        }
                    }

                    // autolevel is typically true by default in source format
                    footNode.Add("autolevel", true);

                    children.Add(footNode);
                }

                if (children.Count == 0)
                {
                    return null;
                }

                var feetNode = MakeNode("Feet", ("children", children));

                // Parent-level field mappings
                var parentFieldMappings = new (string CompiledName, string SourceName)[]
                {
                    ("m_flLockTolerance", "locktolerance"),
                    ("m_flHeightTolerance", "heighttolerance"),
                    ("m_bSanitizeTrajectories", "sanitizetrajectories"),
                };

                // Add parent-level properties if they exist
                foreach (var (compiledName, sourceName) in parentFieldMappings)
                {
                    if (feetSettings.ContainsKey(compiledName))
                    {
                        feetNode.Add(sourceName, feetSettings[compiledName]);
                    }
                }

                return feetNode;
            }

            static void AddGenericGameData(KVObject gameDataList, string genericDataClass, KVObject? genericData, string? dataKey = null)
            {
                if (genericData is null)
                {
                    return;
                }

                // Remove quotes from keys by rebuilding the object
                var cleanedData = KVObject.Collection();
                foreach (var (key, value) in genericData.Children)
                {
                    var trimmed = key?.Trim('"') ?? string.Empty;
                    cleanedData.Add(trimmed, value);
                }

                var name = cleanedData.GetStringProperty("name", string.Empty);

                // The node name should not contain non identifier characters like / or .
                name = Path.GetFileNameWithoutExtension(name);

                KVObject genericGameData;
                if (dataKey == null)
                {
                    genericGameData = MakeNode("GenericGameData",
                        ("name", name),
                        ("game_class", genericDataClass),
                        ("game_keys", cleanedData)
                    );
                }
                else
                {
                    genericGameData = MakeNode(genericDataClass,
                        ("name", name),
                        (dataKey, cleanedData)
                    );
                }

                gameDataList.Add(genericGameData);
            }
        }
        #endregion
    }
}
