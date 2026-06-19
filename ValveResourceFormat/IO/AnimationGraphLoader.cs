using System.Runtime.CompilerServices;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.IO
{
    /// <summary>
    /// Resolves the animation clips (animgraph2) belonging to a model - both the clips referenced by its
    /// animation graphs and any other clips authored against the model's skeleton(s).
    /// </summary>
    public static class AnimationGraphLoader
    {
        private static readonly string NmClipExtension = ResourceType.NmClip.GetExtension()!;
        private static readonly string NmGraphExtension = ResourceType.NmGraph.GetExtension()!;
        private static readonly string NmSkeletonExtension = ResourceType.NmSkeleton.GetExtension()!;

        // Maps each skeleton (.vnmskel) to the clips authored against it, built once per loader by scanning
        // every clip's external references (cheap - no frame data parsed) and kept alive with the loader.
        private static readonly ConditionalWeakTable<IFileLoader, IReadOnlyDictionary<string, List<string>>> SkeletonClipIndexCache = new();

        /// <summary>
        /// Gets every animgraph2 clip (.vnmclip) belonging to the model, de-duplicated and in a stable order:
        /// first the clips referenced by the model's animation graphs (recursing into nested graphs), then any
        /// other clips authored against the model's skeleton(s) (<c>m_vecNmSkeletonRefs</c>) - which surfaces
        /// clips the graph leaves out, e.g. death poses or unused ability animations. None of these are part
        /// of <see cref="Model.GetAllAnimations"/>. The skeleton-matched clips require a loader that can
        /// enumerate files; without one, only the graph-referenced clips are returned.
        /// </summary>
        public static IReadOnlyList<string> GetClipNames(Model model, IFileLoader fileLoader)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var clipNames = new List<string>();

            var graphRefs = model.Data.GetArray("m_animGraph2Refs");
            if (graphRefs != null)
            {
                foreach (var graphRef in graphRefs)
                {
                    CollectGraphClips(graphRef.GetStringProperty("m_hGraph"), fileLoader, visited, clipNames);
                }
            }

            CollectSkeletonClips(model, fileLoader, visited, clipNames);

            return clipNames;
        }

        // Adds clips authored against the model's skeleton(s) that the graphs did not already reference.
        private static void CollectSkeletonClips(Model model, IFileLoader fileLoader, HashSet<string> visited, List<string> clipNames)
        {
            if (!model.Data.ContainsKey("m_vecNmSkeletonRefs"))
            {
                return;
            }

            var skeletonRefs = model.Data.GetArray<string>("m_vecNmSkeletonRefs");
            if (skeletonRefs == null || skeletonRefs.Length == 0)
            {
                return;
            }

            var index = SkeletonClipIndexCache.GetValue(fileLoader, BuildSkeletonClipIndex);

            foreach (var skeletonRef in skeletonRefs)
            {
                if (!index.TryGetValue(skeletonRef, out var clips))
                {
                    continue;
                }

                foreach (var clip in clips)
                {
                    if (visited.Add(clip))
                    {
                        clipNames.Add(clip);
                    }
                }
            }
        }

        private static IReadOnlyDictionary<string, List<string>> BuildSkeletonClipIndex(IFileLoader fileLoader)
        {
            var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var clipName in fileLoader.FindFiles(NmClipExtension))
            {
                var externalRefs = fileLoader.LoadFileExternalRefs(clipName);
                if (externalRefs == null)
                {
                    continue;
                }

                foreach (var reference in externalRefs.ResourceRefInfoList)
                {
                    if (!reference.Name.EndsWith(NmSkeletonExtension, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!index.TryGetValue(reference.Name, out var clips))
                    {
                        index[reference.Name] = clips = [];
                    }

                    clips.Add(clipName);
                }
            }

            return index;
        }

        private static void CollectGraphClips(string graphName, IFileLoader fileLoader, HashSet<string> visited, List<string> clipNames)
        {
            if (!visited.Add(graphName) || fileLoader.LoadFileCompiled(graphName)?.DataBlock is not BinaryKV3 graph)
            {
                return;
            }

            var resources = graph.Data.Root.GetArray<string>("m_resources");
            if (resources == null)
            {
                return;
            }

            foreach (var resource in resources)
            {
                if (resource.EndsWith(NmClipExtension, StringComparison.OrdinalIgnoreCase))
                {
                    if (visited.Add(resource))
                    {
                        clipNames.Add(resource);
                    }
                }
                else if (resource.EndsWith(NmGraphExtension, StringComparison.OrdinalIgnoreCase))
                {
                    CollectGraphClips(resource, fileLoader, visited, clipNames);
                }
            }
        }
    }
}
