using System.Linq;
using ValveKeyValue;
using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.ResourceTypes
{
    /// <summary>
    /// One choice of a body group: a mesh group whose name encodes the group it belongs to.
    /// </summary>
    /// <param name="GroupIndex">Index of this choice in <see cref="ModelMeshGroups.Names"/>, which is the bit it occupies in a mesh's group mask.</param>
    /// <param name="FullName">The mesh group name as compiled, e.g. <c>head_@bald</c>.</param>
    /// <param name="Name">The authored choice name, e.g. <c>bald</c>.</param>
    public sealed record BodyGroupChoice(int GroupIndex, string FullName, string Name);

    /// <summary>
    /// A body group: a set of mesh groups the model switches between, recovered from the mesh group
    /// names that share a prefix.
    /// </summary>
    /// <param name="Name">The body group name, the part before the separator.</param>
    /// <param name="Choices">Its choices, in the order their mesh groups are declared.</param>
    public sealed record BodyGroup(string Name, IReadOnlyList<BodyGroupChoice> Choices);

    /// <summary>
    /// Describes a model's mesh groups: which groups exist, which meshes belong to each, which are on
    /// by default, and which the tools hide. Built once from the compiled model's <c>m_meshGroups</c>,
    /// <c>m_refMeshGroupMasks</c> (bit N set =&gt; mesh is in group N), <c>m_nDefaultMeshGroupMask</c>
    /// and <c>m_BodyGroupsHiddenInTools</c>.
    /// </summary>
    public sealed class ModelMeshGroups
    {
        /// <summary>A mask has one bit per group, so nothing past the 64th group is addressable.</summary>
        private const int MaxGroups = 64;

        private readonly ulong[] meshMasks;
        private readonly ulong defaultMask;
        private readonly HashSet<string> hiddenInTools;

        /// <summary>Gets the mesh group names, in declaration order. Index N is bit N of a mesh's mask.</summary>
        public IReadOnlyList<string> Names { get; }

        /// <summary>
        /// Gets the body groups the mesh group names encode. A name of the form <c>group_@choice</c>
        /// declares one choice of a body group; a name without the separator belongs to no body group.
        /// Groups and their choices keep the order their mesh groups were declared in.
        /// </summary>
        public IReadOnlyList<BodyGroup> BodyGroups { get; }

        /// <summary>Gets the group names that are on by default.</summary>
        public IEnumerable<string> Defaults
            => Names.Where((_, index) => index < MaxGroups && (defaultMask & 1UL << index) != 0);

        /// <summary>
        /// Initializes mesh group info from a compiled model's data block.
        /// </summary>
        public ModelMeshGroups(KVObject data)
        {
            ArgumentNullException.ThrowIfNull(data);

            Names = data.GetArray<string>("m_meshGroups") ?? [];
            meshMasks = data.GetUnsignedIntegerArray("m_refMeshGroupMasks") ?? [];
            defaultMask = data.GetUnsignedIntegerProperty("m_nDefaultMeshGroupMask");
            hiddenInTools = [.. data.GetArray<string>("m_BodyGroupsHiddenInTools") ?? []];

            BodyGroups = BuildBodyGroups(Names);
        }

        /// <summary>Gets the group mask of a mesh, zero when the mesh has no entry.</summary>
        public ulong GetMeshMask(int meshIndex)
            => meshIndex >= 0 && meshIndex < meshMasks.Length ? meshMasks[meshIndex] : 0UL;

        /// <summary>Determines whether a mesh belongs to the group at <paramref name="groupIndex"/>.</summary>
        public bool IsMeshInGroup(int meshIndex, int groupIndex)
            => groupIndex >= 0 && groupIndex < MaxGroups && (GetMeshMask(meshIndex) & 1UL << groupIndex) != 0;

        /// <summary>
        /// Determines whether a mesh belongs to any of the named groups. A model with no groups declared
        /// draws every mesh, so it is treated as belonging to whatever is asked for.
        /// </summary>
        public bool IsMeshInAnyGroup(int meshIndex, ICollection<string> groupNames)
        {
            if (Names.Count <= 1 || meshMasks.Length == 0)
            {
                return true;
            }

            foreach (var groupName in groupNames)
            {
                if (IsMeshInGroup(meshIndex, IndexOf(groupName)))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Gets the index of a group name, or -1 when the model declares no such group.</summary>
        public int IndexOf(string groupName)
        {
            for (var i = 0; i < Names.Count; i++)
            {
                if (Names[i] == groupName)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// Determines whether a body group or one of its choices is hidden in the tools. Choices are
        /// listed by their full mesh group name.
        /// </summary>
        public bool IsHiddenInTools(string name) => hiddenInTools.Contains(name);

        /// <summary>The separator a mesh group name puts between its body group and its choice.</summary>
        private const string ChoiceSeparator = "_@";

        /// <summary>Newer models bury the authored choice name behind this marker.</summary>
        private const string ChoiceNameMarker = "#&";

        private static BodyGroup[] BuildBodyGroups(IReadOnlyList<string> names)
        {
            var groups = new List<BodyGroup>();
            var choicesByGroup = new Dictionary<string, List<BodyGroupChoice>>();

            for (var index = 0; index < names.Count; index++)
            {
                var fullName = names[index];
                var split = fullName.Split(ChoiceSeparator);

                if (split.Length < 2)
                {
                    continue;
                }

                var groupName = split[0];

                if (!choicesByGroup.TryGetValue(groupName, out var choices))
                {
                    choicesByGroup[groupName] = choices = [];
                    groups.Add(new BodyGroup(groupName, choices));
                }

                choices.Add(new BodyGroupChoice(index, fullName, ReadChoiceName(split[1])));
            }

            return [.. groups];
        }

        private static string ReadChoiceName(string name)
        {
            var markerIndex = name.IndexOf(ChoiceNameMarker, StringComparison.Ordinal);

            if (markerIndex < 0)
            {
                return name;
            }

            var start = markerIndex + ChoiceNameMarker.Length;

            return start < name.Length ? name[start..] : name;
        }
    }
}
