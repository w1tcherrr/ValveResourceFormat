using Datamodel;
using ValveResourceFormat.IO.ContentFormats.DmxModel;

namespace ValveResourceFormat.IO;

/// <summary>
/// The DMX scaffolding every mesh export shares: the dag and vertex data a datamodel mesh hangs off, and
/// the face sets its geometry is written into.
/// </summary>
partial class ModelExtract
{
    private static void DmxModelBaseLayout(string name, out DmeModel dmeModel, out DmeDag dag, out DmeVertexData vertexData)
    {
        DmxModelMultiVertexBufferLayout(name, 1, out dmeModel, out var dags, out var dmeVertexBuffers);
        dag = dags[0];
        vertexData = dmeVertexBuffers[0];
    }

    private static void DmxModelMultiVertexBufferLayout(string name, int vertexBufferCount,
        out DmeModel dmeModel, out DmeDag[] dags, out DmeVertexData[] dmeVertexBuffers)
    {
        dmeModel = new DmeModel() { Name = name };
        dags = new DmeDag[vertexBufferCount];
        dmeVertexBuffers = new DmeVertexData[vertexBufferCount];

        for (var i = 0; i < vertexBufferCount; i++)
        {
            (dags[i], dmeVertexBuffers[i]) = CreateDmxDagVertexData(dmeModel, name);
        }
    }

    private static DmeDag CreateDmxDag(DmeModel dmeModel, DmeVertexData vertexData, string name)
    {
        var shape = new DmeMesh
        {
            Name = name,
            CurrentState = vertexData
        };
        shape.BaseStates.Add(vertexData);

        var dag = new DmeDag() { Name = name, Shape = shape };
        dmeModel.Children.Add(dag);
        dmeModel.JointList.Add(dag);

        var transformList = new DmeTransformsList();
        transformList.Transforms.Add(new DmeTransform());
        dmeModel.BaseStates.Add(transformList);

        return dag;
    }

    private static (DmeDag, DmeVertexData) CreateDmxDagVertexData(DmeModel dmeModel, string name)
    {
        // dmx requires one dag per vertex buffer
        var vertexData = new DmeVertexData { Name = "bind" };
        var dag = CreateDmxDag(dmeModel, vertexData, name);

        return (dag, vertexData);
    }

    private static void GenerateTriangleFaceSet(DmeDag dag, int triangleStart, int triangleEnd, string material)
    {
        var faceSet = new DmeFaceSet() { Name = triangleStart + "-" + triangleEnd };
        if (dag.Shape is DmeMesh dmeMesh)
        {
            dmeMesh.FaceSets.Add(faceSet);
        }

        for (var i = triangleStart; i < triangleEnd; i++)
        {
            faceSet.Faces.Add(i * 3);
            faceSet.Faces.Add(i * 3 + 1);
            faceSet.Faces.Add(i * 3 + 2);
            faceSet.Faces.Add(-1);
        }

        faceSet.Material.MaterialName = material;
    }

    private static void GenerateTriangleFaceSetFromIndexBuffer(DmeDag dag, ReadOnlySpan<int> indices, int baseVertex,
        string material, string name)
    {
        var faceSet = new DmeFaceSet() { Name = name };
        if (dag.Shape is DmeMesh dmeMesh)
        {
            dmeMesh.FaceSets.Add(faceSet);
        }

        for (var i = 0; i < indices.Length; i += 3)
        {
            faceSet.Faces.Add(baseVertex + indices[i]);
            faceSet.Faces.Add(baseVertex + indices[i + 1]);
            faceSet.Faces.Add(baseVertex + indices[i + 2]);
            faceSet.Faces.Add(-1);
        }

        faceSet.Material.MaterialName = material;
    }

    private static void TieElementRoot(Datamodel.Datamodel dmx, DmeModel dmeModel)
    {
        dmx.Root = new Element(dmx, "root", null, "DmElement")
        {
            ["skeleton"] = dmeModel,
            ["model"] = dmeModel,
            ["exportTags"] = new Element(dmx, "exportTags", null, "DmeExportTags")
            {
                ["source"] = $"Generated with {StringToken.VRF_GENERATOR}",
            }
        };
    }
}
