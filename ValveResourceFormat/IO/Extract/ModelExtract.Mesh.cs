using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Datamodel;
using ValveKeyValue;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.IO.ContentFormats.DmxModel;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.ResourceTypes.ModelAnimation;
using ValveResourceFormat.ResourceTypes.RubikonPhysics;
using ValveResourceFormat.Serialization.KeyValues;
using RnShapes = ValveResourceFormat.ResourceTypes.RubikonPhysics.Shapes;

namespace ValveResourceFormat.IO;

partial class ModelExtract
{

    /// <summary>
    /// Gets the list of render meshes to be extracted.
    /// </summary>
    public List<RenderMeshExtractConfiguration> RenderMeshesToExtract { get; } = [];

    /// <summary>
    /// Gets the material input signatures for mapping DirectX semantic names.
    /// </summary>
    public Dictionary<string, Material.VsInputSignature> MaterialInputSignatures { get; } = [];

    /// <summary>
    /// Gets or sets the translation offset for the model.
    /// </summary>
    public Vector3 Translation { get; set; }

    /// <summary>
    /// Options for extracting a render mesh to datamodel format.
    /// </summary>
    public readonly struct DatamodelRenderMeshExtractOptions
    {
        /// <summary>
        /// Split draw calls into sub-meshes named draw0, draw1, draw2...
        /// </summary>
        public bool SplitDrawCallsIntoSeparateSubmeshes { get; init; }

        /// <summary>
        /// When set together with <see cref="SplitDrawCallsIntoSeparateSubmeshes"/>, receives each sub-mesh
        /// paired with the draw call it was made from, in draw call order.
        /// </summary>
        public List<(DmeDag Dag, KVObject DrawCall)>? SubmeshDrawCalls { get; init; }

        /// <summary>
        /// Pre-parsed input signatures used to map DirectX semantic names to engine semantic names.
        /// </summary>
        public Dictionary<string, Material.VsInputSignature> MaterialInputSignatures { get; init; }

        /// <summary>
        /// Remap table for the mesh bone indices.
        /// </summary>
        public int[]? BoneRemapTable { get; init; }

        /// <summary>
        /// Skeleton whose bones the mesh's BLENDINDICES reference (post-remap, in <see cref="Bone.Index"/> order).
        /// When provided, bones are emitted into the DMX <c>jointList</c> so ModelDoc can resolve indices.
        /// </summary>
        public Skeleton? Skeleton { get; init; }
    }

    /// <summary>
    /// Configuration for extracting a render mesh.
    /// </summary>
    public record struct RenderMeshExtractConfiguration(
        Mesh Mesh,
        string Name,
        int Index,
        string FileName,
        int[]? BoneRemapTable = null,
        Skeleton? Skeleton = null,
        ImportFilter ImportFilter = default
    );

    string GetDmxFileName_ForEmbeddedMesh(string subString, int number = 0)
    {
        var fileName = ModelName;
        return (Path.GetDirectoryName(fileName)
            + Path.DirectorySeparatorChar
            + Path.GetFileNameWithoutExtension(fileName)
            + "_"
            + subString
            + (number > 0 ? number : string.Empty)
            + ".dmx")
            .Replace('\\', '/');
    }

    static string GetDmxFileName_ForReferenceMesh(string fileName)
        => Path.ChangeExtension(fileName, ".dmx").Replace('\\', '/');

    private void EnqueueMeshes()
    {
        if (fileLoader is not null) // May be null for mesh-only constructor
        {
            FileExtract.EnsurePopulatedStringToken(fileLoader);
        }
        EnqueueRenderMeshes();
        EnqueuePhysMeshes();
    }

    private void EnqueueRenderMeshes()
    {
        if (model == null)
        {
            return;
        }

        GrabMaterialInputSignatures(modelResource);

        var i = 0;
        foreach (var embedded in model.GetEmbeddedMeshes())
        {
            var remapTable = model.GetRemapTable(embedded.MeshIndex);
            RenderMeshesToExtract.Add(new(embedded.Mesh, embedded.Name, embedded.MeshIndex, GetDmxFileName_ForEmbeddedMesh(embedded.Name, i++), remapTable, model.Skeleton));
        }

        foreach (var reference in model.GetReferenceMeshNamesAndLoD())
        {
            Debug.Assert(fileLoader is not null, "fileLoader should not be null when loading reference meshes");

            using var resource = fileLoader.LoadFileCompiled(reference.MeshName);

            if (resource is null)
            {
                continue;
            }

            GrabMaterialInputSignatures(resource);

            if (resource.DataBlock is not Mesh mesh)
            {
                continue;
            }

            model.SetExternalMeshData(mesh);

            var remapTable = model.GetRemapTable(reference.MeshIndex);
            var meshKey = Path.GetFileNameWithoutExtension(reference.MeshName);

            RenderMeshesToExtract.Add(new(mesh, meshKey, reference.MeshIndex, GetDmxFileName_ForReferenceMesh(reference.MeshName), remapTable, model.Skeleton));
        }
    }

    internal void GrabMaterialInputSignatures(Resource? resource)
    {
        Debug.Assert(fileLoader is not null, "fileLoader should not be null when grabbing material signatures");

        var materialReferences = resource?.ExternalReferences?.ResourceRefInfoList.Where(static r => r.Name[^4..] == "vmat");
        foreach (var material in materialReferences ?? [])
        {
            MaterialInputSignatures[material.Name] = Material.LoadInputSignature(fileLoader, material.Name);
        }
    }

    /// <summary>
    /// Extracts content files from an aggregate model resource, splitting by draw calls.
    /// </summary>
    public static IEnumerable<ContentFile> GetContentFiles_DrawCallSplit(Resource aggregateModelResource, IFileLoader fileLoader, Vector3[] drawOrigins, int drawCallCount)
    {
        var extract = new ModelExtract(aggregateModelResource, fileLoader) { Type = ModelExtractType.Map_AggregateSplit };
        Debug.Assert(extract.RenderMeshesToExtract.Count == 1);

        if (extract.RenderMeshesToExtract.Count == 0)
        {
            yield break;
        }

        var (mesh, name, index, fileName, boneRemapTable, skeleton, _) = extract.RenderMeshesToExtract[0];

        var options = new DatamodelRenderMeshExtractOptions
        {
            MaterialInputSignatures = extract.MaterialInputSignatures,
            SplitDrawCallsIntoSeparateSubmeshes = true,
            BoneRemapTable = boneRemapTable,
            Skeleton = skeleton,
        };

        byte[] sharedDmxExtractMethod() => ToDmxMesh(
            mesh,
            Path.GetFileNameWithoutExtension(fileName),
            options
        );

        var sharedMeshExtractConfiguration = new RenderMeshExtractConfiguration(mesh, name, index, fileName, boneRemapTable, skeleton, new(true, new(1)));
        extract.RenderMeshesToExtract.Clear();
        extract.RenderMeshesToExtract.Add(sharedMeshExtractConfiguration);

        for (var i = 0; i < drawCallCount; i++)
        {
            sharedMeshExtractConfiguration.ImportFilter.Filter.Clear();
            sharedMeshExtractConfiguration.ImportFilter.Filter.Add("draw" + i);

            extract.Translation = drawOrigins.Length > i
                ? -1 * drawOrigins[i]
                : Vector3.Zero;

            var vmdl = new ContentFile
            {
                Data = Encoding.UTF8.GetBytes(extract.ToValveModel()),
                FileName = GetFragmentModelName(extract.ModelName, i),
            };

            if (i == 0)
            {
                vmdl.AddSubFile(Path.GetFileName(fileName), sharedDmxExtractMethod);
            }

            yield return vmdl;
        }
    }

    /// <summary>
    /// Gets the fragment model name for a draw call index.
    /// </summary>
    public static string GetFragmentModelName(string aggModelName, int drawCallIndex)
    {
        const string vmdlExt = ".vmdl";
        return aggModelName[..^vmdlExt.Length] + "_draw" + drawCallIndex + vmdlExt;
    }

    /// <summary>
    /// A mesh's vertex buffers concatenated into one, and the vertex each original buffer starts at.
    /// </summary>
    private readonly record struct MergedVertexBuffers(VBIB.OnDiskBufferData Buffer, Dictionary<int, int> VertexOffsets);

    /// <summary>
    /// Concatenates the vertex buffers of a mesh when every draw call reads a single buffer and all of
    /// them share one layout. Returns null when they cannot be merged, in which case each buffer keeps
    /// its own dme mesh.
    /// </summary>
    private static MergedVertexBuffers? TryMergeVertexBuffers(KVObject mdat, VBIB mbuf)
    {
        var usedBuffers = new List<int>();

        foreach (var sceneObject in mdat.GetArray("m_sceneObjects"))
        {
            foreach (var drawCall in sceneObject.GetArray("m_drawCalls"))
            {
                var vertexBuffers = drawCall.GetArray("m_vertexBuffers");

                if (vertexBuffers.Count != 1)
                {
                    return null;
                }

                var bufferIndex = vertexBuffers[0].GetInt32Property("m_hBuffer");

                if (!usedBuffers.Contains(bufferIndex))
                {
                    usedBuffers.Add(bufferIndex);
                }
            }
        }

        if (usedBuffers.Count < 2)
        {
            return null;
        }

        var buffers = usedBuffers.ConvertAll(index => mbuf.VertexBuffers[index]);

        if (buffers.Exists(buffer => !VBIB.HasSameLayout(buffers[0], buffer)))
        {
            return null;
        }

        var offsets = new Dictionary<int, int>(usedBuffers.Count);
        var vertexOffset = 0u;

        for (var i = 0; i < usedBuffers.Count; i++)
        {
            offsets[usedBuffers[i]] = (int)vertexOffset;
            vertexOffset += buffers[i].ElementCount;
        }

        return new MergedVertexBuffers(VBIB.Concatenate(buffers), offsets);
    }

    private static void FillDatamodelVertexData(VBIB.OnDiskBufferData vertexBuffer, DmeVertexData vertexData, Material.VsInputSignature materialInputSignature,
        int boneWeightCount, int[]? boneRemapTable)
    {
        var indices = Enumerable.Range(0, (int)vertexBuffer.ElementCount).ToArray(); // May break with non-unit strides, non-tri faces

        var boneArrayComponents = boneWeightCount > 4 ? 8 : 4;

        foreach (var attribute in vertexBuffer.InputLayoutFields)
        {
            var attributeFormat = VBIB.GetFormatInfo(attribute);
            var semantic = attribute.SemanticName.ToLowerInvariant() + "$" + attribute.SemanticIndex;

            if (attribute.SemanticName is "NORMAL")
            {
                var (normals, tangents) = VBIB.GetNormalTangentArray(vertexBuffer, attribute);
                vertexData.AddIndexedStream(semantic, normals, indices);

                if (tangents.Length > 0)
                {
                    vertexData.AddIndexedStream("tangent$" + attribute.SemanticIndex, tangents, indices);
                }

                continue;
            }
            else if (attribute.SemanticName is "BLENDINDICES")
            {
                vertexData.JointCount = boneWeightCount;

                // An unskinned mesh can still carry the attribute, because the vertex format is shared
                // with skinned ones, and then the indices reference nothing.
                if (boneWeightCount == 0)
                {
                    continue;
                }

                var boneIndices = VBIB.GetBlendIndicesArray(vertexBuffer, attribute, boneRemapTable);
                var compactedLength = boneIndices.Length / boneArrayComponents * boneWeightCount;

                var compactIndices = new int[compactedLength];
                for (var i = 0; i < boneIndices.Length; i += boneArrayComponents)
                {
                    for (var j = 0; j < boneWeightCount; j++)
                    {
                        compactIndices[i / boneArrayComponents * boneWeightCount + j] = boneIndices[i + j];
                    }
                }

                vertexData.AddStream(semantic, compactIndices);
                continue;
            }
            else if (attribute.SemanticName is "BLENDWEIGHT" or "BLENDWEIGHTS")
            {
                if (boneWeightCount == 0)
                {
                    continue;
                }

                var vectorWeights = VBIB.GetBlendWeightsArray(vertexBuffer, attribute);
                var flatWeights = MemoryMarshal.Cast<Vector4, float>(vectorWeights).ToArray();

                var compactWeights = new float[flatWeights.Length / boneArrayComponents * boneWeightCount];
                for (var i = 0; i < flatWeights.Length; i += boneArrayComponents)
                {
                    for (var j = 0; j < boneWeightCount; j++)
                    {
                        compactWeights[i / boneArrayComponents * boneWeightCount + j] = flatWeights[i + j];
                    }
                }

                vertexData.AddStream("blendweights$" + attribute.SemanticIndex, compactWeights);
                continue;
            }

            if (materialInputSignature.Elements is { Length: > 0 })
            {
                var insgElement = Material.FindD3DInputSignatureElement(materialInputSignature, attribute.SemanticName, attribute.SemanticIndex);

                // Use engine semantics for attributes that need them
                if (insgElement.Semantic is "VertexPaintBlendParams" or "VertexPaintTintColor")
                {
                    semantic = insgElement.Semantic + "$0";
                }
            }

            switch (attributeFormat.ElementCount)
            {
                case 1:
                    var scalar = VBIB.GetScalarAttributeArray(vertexBuffer, attribute);
                    vertexData.AddIndexedStream(semantic, scalar, indices);
                    break;
                case 2:
                    var vec2 = VBIB.GetVector2AttributeArray(vertexBuffer, attribute);
                    vertexData.AddIndexedStream(semantic, vec2, indices);
                    break;
                case 3:
                    var vec3 = VBIB.GetVector3AttributeArray(vertexBuffer, attribute);
                    vertexData.AddIndexedStream(semantic, vec3, indices);
                    break;
                case 4:
                    var vec4 = VBIB.GetVector4AttributeArray(vertexBuffer, attribute);
                    vertexData.AddIndexedStream(semantic, vec4, indices);
                    break;
                default:
                    throw new NotImplementedException($"Stream {semantic} has an unexpected number of components: {attributeFormat.ElementCount}.");
            }
        }

        if (vertexData.VertexFormat.Contains("blendindices$0") && !vertexData.VertexFormat.Contains("blendweights$0"))
        {
            if (!vertexData.TryGetValue("blendindices$0", out var blendIndices) || blendIndices is not ICollection<int> collection)
            {
                throw new InvalidOperationException("blendindices$0 stream not found");
            }

            vertexData.AddStream("blendweights$0", Enumerable.Repeat(1f, collection.Count).ToArray());
        }
    }

    /// <summary>
    /// Gives a mesh the normal and texture coordinate streams the model compiler requires. Shipped
    /// content includes meshes authored with position alone, and the compiler faults on those.
    /// </summary>
    private static void AddCompilerRequiredStreams(DmeVertexData vertexData, int elementCount)
    {
        var indices = Enumerable.Range(0, elementCount).ToArray();

        if (!vertexData.VertexFormat.Contains("normal$0"))
        {
            vertexData.AddIndexedStream("normal$0", Enumerable.Repeat(Vector3.UnitZ, elementCount).ToArray(), indices);
        }

        if (!vertexData.VertexFormat.Contains("texcoord$0"))
        {
            vertexData.AddIndexedStream("texcoord$0", Enumerable.Repeat(Vector2.Zero, elementCount).ToArray(), indices);
        }
    }

    /// <summary>
    /// Converts a mesh to DMX format.
    /// </summary>
    public static byte[] ToDmxMesh(Mesh mesh, string name, DatamodelRenderMeshExtractOptions options = default)
    {
        using var dmx = ConvertMeshToDatamodelMesh(mesh, name, options);
        using var stream = new MemoryStream();
        dmx.Save(stream, "binary", 9);

        return stream.ToArray();
    }

    /// <summary>
    /// Converts a mesh to a datamodel mesh representation.
    /// </summary>
    public static Datamodel.Datamodel ConvertMeshToDatamodelMesh(Mesh mesh, string name, DatamodelRenderMeshExtractOptions options)
    {
        var mdat = mesh.Data;
        var mbuf = mesh.VBIB;
        var indexBuffers = mbuf.IndexBuffers.Select(ib => new Lazy<int[]>(() => GltfModelExporter.ReadIndices(ib, 0, (int)ib.ElementCount, 0))).ToArray();

        var datamodel = new Datamodel.Datamodel("model", 22);
        var dmeModel = new DmeModel() { Name = name };
        var dmeVertexBuffers = new Dictionary<(int, int), (DmeDag Dag, DmeVertexData VertexData)>(mbuf.VertexBuffers.Count);

        // Populate the joint list with bones up-front so DMX BLENDINDICES line up with Bone.Index.
        // ModelDoc resolves mesh skinning indices through this list; without it the mesh is bound to "no skeleton".
        if (options.Skeleton is { Bones.Length: > 0 } skeleton)
        {
            dmeModel = BuildDmeDagSkeleton(skeleton, out _);
            dmeModel.Name = name;
        }

        var materialInputSignature = Material.VsInputSignature.Empty;
        var drawCallIndex = 0;

        // One mesh whose draw calls sit in separate but identically laid out vertex buffers is a single
        // mesh in the source art, so the buffers are concatenated back into one and the draw calls
        // become face sets of it. Morph vertex ids run across the whole mesh, so this is also what
        // makes the deltas line up.
        var merged = TryMergeVertexBuffers(mdat, mbuf);

        var morphVertexOffsets = new Dictionary<(int, int), int>(mbuf.VertexBuffers.Count);
        var morphVertexOffset = 0;

        foreach (var sceneObject in mdat.GetArray("m_sceneObjects"))
        {
            foreach (var drawCall in sceneObject.GetArray("m_drawCalls"))
            {
                var vertexBuffers = drawCall.GetArray("m_vertexBuffers");

                Debug.Assert(vertexBuffers.Count <= 2); // Hello traveler, if you are here to update this code to support more than 2 buffers!

                var bufferIndex = vertexBuffers[0].GetInt32Property("m_hBuffer");

                var dmeVertexBufferKey = merged != null
                    ? (0, -1)
                    : (bufferIndex, vertexBuffers.Count > 1 ? vertexBuffers[1].GetInt32Property("m_hBuffer") : -1);

                if (!dmeVertexBuffers.TryGetValue(dmeVertexBufferKey, out var dmeVertexBuffer))
                {
                    dmeVertexBuffer = CreateDmxDagVertexData(dmeModel, name);
                    dmeVertexBuffers[dmeVertexBufferKey] = dmeVertexBuffer;
                    morphVertexOffsets[dmeVertexBufferKey] = morphVertexOffset;
                }

                var mergedVertexOffset = merged?.VertexOffsets[bufferIndex] ?? 0;
                morphVertexOffset += drawCall.GetInt32Property("m_nVertexCount");

                var indexBufferInfo = drawCall.GetSubCollection("m_indexBuffer");
                var indexBufferIndex = indexBufferInfo.GetInt32Property("m_hBuffer");
                ReadOnlySpan<int> indexBuffer = indexBuffers[indexBufferIndex].Value;

                var material = Mesh.GetMaterialName(drawCall);

                if (material != null && options.MaterialInputSignatures != null && materialInputSignature.Elements is not { Length: > 0 })
                {
                    materialInputSignature = options.MaterialInputSignatures.GetValueOrDefault(material, Material.VsInputSignature.Empty);
                }

                if (material == null && Mesh.IsOccluder(drawCall))
                {
                    material = "materials/tools/toolsoccluder.vmat";
                }

                material ??= "materials/default.vmat";

                var baseVertex = drawCall.GetInt32Property("m_nBaseVertex") + mergedVertexOffset;
                var startIndex = drawCall.GetInt32Property("m_nStartIndex");
                var indexCount = drawCall.GetInt32Property("m_nIndexCount");

                var dag = dmeVertexBuffer.Dag;

                if (options.SplitDrawCallsIntoSeparateSubmeshes)
                {
                    var subMeshName = "draw" + drawCallIndex;

                    if (drawCallIndex > 0)
                    {
                        // new submesh with same vertex buffer as first submesh
                        dag = CreateDmxDag(dmeModel, dmeVertexBuffer.VertexData, subMeshName);
                    }

                    dag.Shape!.Name = subMeshName;
                    options.SubmeshDrawCalls?.Add((dag, drawCall));
                }

                GenerateTriangleFaceSetFromIndexBuffer(
                    dag,
                    indexBuffer[startIndex..(startIndex + indexCount)],
                    baseVertex,
                    material,
                    $"{startIndex}..{startIndex + indexCount}"
                );

                drawCallIndex++;
            }
        }

        var boneWeightCount = mesh.BoneWeightCount;

        foreach (var (vertexBufferIndices, dmeObjects) in dmeVertexBuffers)
        {
            if (merged != null)
            {
                FillDatamodelVertexData(merged.Value.Buffer, dmeObjects.VertexData, materialInputSignature, boneWeightCount, options.BoneRemapTable);
                AddCompilerRequiredStreams(dmeObjects.VertexData, (int)merged.Value.Buffer.ElementCount);
                continue;
            }

            FillDatamodelVertexData(mbuf.VertexBuffers[vertexBufferIndices.Item1], dmeObjects.VertexData, materialInputSignature, boneWeightCount, options.BoneRemapTable);

            if (vertexBufferIndices.Item2 != -1)
            {
                FillDatamodelVertexData(mbuf.VertexBuffers[vertexBufferIndices.Item2], dmeObjects.VertexData, materialInputSignature, boneWeightCount, options.BoneRemapTable);
            }

            AddCompilerRequiredStreams(dmeObjects.VertexData, (int)mbuf.VertexBuffers[vertexBufferIndices.Item1].ElementCount);
        }

        TieElementRoot(datamodel, dmeModel);

        if (mesh.MorphData != null)
        {
            var morphTargets = dmeVertexBuffers
                .Select(pair => ((DmeMesh)pair.Value.Dag.Shape!, morphVertexOffsets[pair.Key],
                    (int)(merged?.Buffer.ElementCount ?? mbuf.VertexBuffers[pair.Key.Item1].ElementCount)))
                .ToList();

            AddMorphData(datamodel, mesh.MorphData, morphTargets);
        }

        return datamodel;
    }

    /// <summary>
    /// Writes the morph targets of a mesh as delta states, and the flex controllers that drive them as
    /// a combination operator. ModelDoc derives the compiled flex rules from this.
    /// </summary>
    private static void AddMorphData(Datamodel.Datamodel datamodel, Morph morph,
        List<(DmeMesh Mesh, int BaseVertex, int VertexCount)> targets)
    {
        var flexNames = morph.GetFlexDescriptors();
        if (flexNames.Count == 0 || targets.Count == 0)
        {
            return;
        }

        var positionData = morph.GetFlexVertexData(MorphBundleType.PositionSpeed);
        var normalData = morph.GetFlexVertexData(MorphBundleType.NormalWrinkle);
        var coverage = morph.GetFlexVertexCoverage();
        var recovery = new FlexRecovery(morph);

        var combination = new DmeCombinationOperator { Name = "combinationOperator" };

        foreach (var (dmeMesh, baseVertex, vertexCount) in targets)
        {
            foreach (var flexName in flexNames)
            {
                // A morph target with no deltas at all still needs its delta state, or the compiler
                // appends it after the ones that have data and the whole flex order shifts.
                positionData.TryGetValue(flexName, out var deltas);
                deltas ??= [];

                normalData.TryGetValue(flexName, out var normalDeltas);
                coverage.TryGetValue(flexName, out var covered);

                var positions = new List<Vector3>();
                var positionIndices = new List<int>();
                var normals = new List<Vector3>();
                var normalIndices = new List<int>();
                var wrinkles = new List<float>();
                var wrinkleIndices = new List<int>();

                for (var i = 0; i < vertexCount; i++)
                {
                    var vertexId = baseVertex + i;
                    if (vertexId >= deltas.Length)
                    {
                        break;
                    }

                    var inRect = covered == null || (vertexId < covered.Length && covered[vertexId]);
                    var delta = deltas[vertexId];

                    if (inRect || delta.X != 0f || delta.Y != 0f || delta.Z != 0f)
                    {
                        positions.Add(new Vector3(delta.X, delta.Y, delta.Z));
                        positionIndices.Add(i);
                    }

                    if (normalDeltas == null || vertexId >= normalDeltas.Length)
                    {
                        continue;
                    }

                    var normal = normalDeltas[vertexId];

                    if (inRect || normal.X != 0f || normal.Y != 0f || normal.Z != 0f)
                    {
                        normals.Add(new Vector3(normal.X, normal.Y, normal.Z));
                        normalIndices.Add(i);
                    }

                    if (normal.W != 0f)
                    {
                        wrinkles.Add(normal.W);
                        wrinkleIndices.Add(i);
                    }
                }

                // A morph target that carries no geometry at all still has to look like one, or the
                // compiler sorts it behind the targets that do and the flex order shifts.
                if (positions.Count == 0 && vertexCount > 0)
                {
                    positions.Add(Vector3.Zero);
                    positionIndices.Add(0);
                }

                var deltaState = new DmeVertexDeltaData { Name = FlexRecovery.Identifier(flexName) };
                deltaState.AddIndexedStream("position$0", positions.ToArray(), positionIndices.ToArray());

                if (normals.Count > 0)
                {
                    deltaState.AddIndexedStream("normal$0", normals.ToArray(), normalIndices.ToArray());
                }

                if (wrinkles.Count > 0)
                {
                    deltaState.AddIndexedStream("wrinkle$0", wrinkles.ToArray(), wrinkleIndices.ToArray());
                }

                dmeMesh.DeltaStates.Add(deltaState);
                dmeMesh.DeltaStateWeights.Add(Vector2.Zero);
                dmeMesh.DeltaStateWeightsLagged.Add(Vector2.Zero);
            }

            // Targeting a rule set rather than the mesh is what makes the compiler take its flex rules
            // from the expressions below instead of giving every morph target its own controller.
            var flexRules = new DmeFlexRules { Name = dmeMesh.Name, Target = dmeMesh };

            foreach (var flexName in flexNames)
            {
                if (!recovery.Expressions.TryGetValue(flexName, out var expression))
                {
                    continue;
                }

                flexRules.DeltaStates.Add(new DmeFlexRuleExpression { Name = FlexRecovery.Identifier(flexName), Expression = expression });
                flexRules.DeltaStateWeights.Add(Vector2.Zero);
            }

            combination.Targets.Add(flexRules);
        }

        foreach (var control in recovery.Controls)
        {
            var inputControl = new DmeCombinationInputControl
            {
                // The compiler rewrites a name that is not a plain identifier, so the names have to be
                // rewritten the same way on both sides of a reference or it stops resolving.
                Name = FlexRecovery.Identifier(control.Name),
                FlexMin = control.Min,
                FlexMax = control.Max,
            };

            foreach (var rawControlName in control.RawControlNames)
            {
                inputControl.RawControlNames.Add(FlexRecovery.Identifier(rawControlName));
                inputControl.WrinkleScales.Add(0f);
            }

            combination.Controls.Add(inputControl);
            combination.ControlValues.Add(new Vector3(0f, 0f, 0.5f));
            combination.ControlValuesLagged.Add(new Vector3(0f, 0f, 0.5f));
        }

        datamodel.Root!["combinationOperator"] = combination;
    }

}
