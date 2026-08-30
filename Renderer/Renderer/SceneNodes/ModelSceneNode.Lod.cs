using System.Linq;
using ValveResourceFormat.ResourceTypes;

namespace ValveResourceFormat.Renderer.SceneNodes
{
    /// <summary>
    /// Picks which of the model's meshes are drawn: the LOD level, chosen by screen size or forced, and
    /// the active mesh groups.
    /// </summary>
    public partial class ModelSceneNode
    {
        private HashSet<string> activeMeshGroups = [];

        private readonly ModelMeshGroups meshGroups;

        private readonly ModelLodInfo lodInfo;

        private readonly List<ModelMeshReference> referenceMeshes;

        private int? lodOverride;

        private int resolvedLod;

#pragma warning disable CA1024 // Use properties where appropriate
        /// <summary>Returns every external reference mesh name and its LoD mask, across all levels.</summary>
        public IEnumerable<ModelMeshReference> GetReferenceMeshes()
            => referenceMeshes;

        /// <summary>Returns all mesh group names defined by this model.</summary>
        public IEnumerable<string> GetMeshGroups()
            => meshGroups.Names;

        /// <summary>Returns the set of currently active mesh group names.</summary>
        public ICollection<string> GetActiveMeshGroups()
            => activeMeshGroups;
#pragma warning restore CA1024 // Use properties where appropriate

        /// <summary>
        /// Sets which mesh groups are active, rebuilding the renderable mesh list accordingly.
        /// </summary>
        public void SetActiveMeshGroups(IEnumerable<string> setMeshGroups)
        {
            activeMeshGroups = new HashSet<string>(meshGroups.Names.Intersect(setMeshGroups));
            RebuildRenderableMeshes();
        }

        /// <summary>Gets the LoD level currently being rendered (auto-selected or forced).</summary>
        public int ActiveLod => resolvedLod;

        /// <summary>Gets whether the LoD level is being chosen automatically by distance, rather than forced.</summary>
        public bool IsAutoLod => lodOverride == null;

        /// <summary>
        /// Sets the LoD level to display, rebuilding the renderable mesh list accordingly.
        /// Pass <see langword="null"/> to enable automatic distance-based selection.
        /// </summary>
        public void SetOverrideLod(int? lod)
        {
            // A forced level is clamped to the model's populated range.
            if (lod.HasValue)
            {
                var highestLevel = lodInfo.AvailableLevels.Count > 0 ? lodInfo.AvailableLevels[^1] : lodInfo.LowestLevel;
                lod = Math.Clamp(lod.Value, lodInfo.LowestLevel, highestLevel);
            }

            lodOverride = lod;

            // A forced level stays put; Auto starts at the lowest populated level and UpdateAutoLod takes over.
            resolvedLod = lod ?? lodInfo.LowestLevel;
            RebuildRenderableMeshes();
        }

        /// <summary>
        /// In automatic mode, picks the LoD level from the screen-size metric: the model drops to LoD
        /// <c>n</c> once the metric passes <c>m_lodGroupSwitchDistances[n]</c>.
        /// </summary>
        private void UpdateAutoLod(Camera camera)
        {
            if (lodOverride != null || lodInfo.AvailableLevels.Count <= 1 || lodInfo.SwitchDistances.Count <= 1)
            {
                return;
            }

            var target = lodInfo.SelectLevel(ComputeLodMetric(camera));

            if (target != resolvedLod)
            {
                resolvedLod = target;
                RebuildRenderableMeshes();
            }
        }

        /// <summary>
        /// Computes the LoD metric: <c>100 / on-screen size of a unit sphere at the model origin</c>,
        /// scaled by the node's transform. It depends on camera distance, FOV/viewport height and the
        /// model's scale, so where the model sits on screen doesn't matter and looking around won't flip LoDs.
        /// </summary>
        private float ComputeLodMetric(Camera camera)
            => ComputeLodMetric(
                MathF.Sqrt(GetCameraDistance(camera)),
                GetLargestAxisScale(Transform),
                camera.WindowSize.Y,
                camera.ProjectionMatrix.M22);

        /// <summary>
        /// The LoD metric for a model at <paramref name="distance"/> from the camera: <c>100 / the
        /// on-screen height of a unit sphere there</c>. It grows as the model gets smaller on screen, so a
        /// higher metric selects a higher, lower-detail level.
        /// </summary>
        /// <param name="distance">Distance from the camera to the model, in world units.</param>
        /// <param name="scale">The largest per-axis scale the model's transform bakes in.</param>
        /// <param name="windowHeight">Viewport height in pixels.</param>
        /// <param name="projectionYScale">
        /// The projection's <c>M22</c>, which is <c>1 / tan(vFov / 2)</c>, so the pixel height of a unit
        /// sphere is <c>windowHeight * projectionYScale * scale / distance</c>.
        /// </param>
        /// <returns>
        /// The metric, or zero for a model that covers no pixels at all. A model at or behind the camera
        /// takes the lowest metric, which selects the most detailed level.
        /// </returns>
        public static float ComputeLodMetric(float distance, float scale, float windowHeight, float projectionYScale)
        {
            var unitSphereSize = distance > 0f
                ? windowHeight * projectionYScale * scale / distance
                : float.MaxValue;

            return unitSphereSize > 0f ? 100f / unitSphereSize : 0f;
        }

        /// <summary>
        /// The largest per-axis scale a transform bakes in, which is what decides how big the model
        /// actually draws regardless of which axis was scaled.
        /// </summary>
        public static float GetLargestAxisScale(Matrix4x4 transform)
        {
            var scaleX = new Vector3(transform.M11, transform.M12, transform.M13).Length();
            var scaleY = new Vector3(transform.M21, transform.M22, transform.M23).Length();
            var scaleZ = new Vector3(transform.M31, transform.M32, transform.M33).Length();

            return MathF.Max(scaleX, MathF.Max(scaleY, scaleZ));
        }

        private bool IsMeshInActiveLod(int meshIndex)
            => lodInfo.IsMeshInLevel(meshIndex, resolvedLod);

        private bool IsMeshInActiveGroup(int meshIndex)
            => meshGroups.IsMeshInAnyGroup(meshIndex, activeMeshGroups);

        private void RebuildRenderableMeshes()
        {
            RenderableMeshes.Clear();

            foreach (var meshRenderer in meshRenderers)
            {
                if (IsMeshInActiveLod(meshRenderer.MeshIndex) && IsMeshInActiveGroup(meshRenderer.MeshIndex))
                {
                    RenderableMeshes.Add(meshRenderer);
                }
            }
        }
    }
}
