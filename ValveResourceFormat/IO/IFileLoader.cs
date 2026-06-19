using ValveResourceFormat.Blocks;
using ValveResourceFormat.CompiledShader;

namespace ValveResourceFormat.IO
{
    /// <summary>
    /// Interface for loading compiled game resources.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This interface is intended for loading compiled resources (such as models, textures, materials)
    /// that need to be parsed as <see cref="Resource"/> objects. Implementations handle resource lookup
    /// across VPK packages and loose files.
    /// </para>
    /// </remarks>
    public interface IFileLoader
    {
        /// <summary>
        /// Loads a compiled resource file.
        /// </summary>
        /// <remarks>
        /// To read raw file bytes from a VPK package, use <c>Package.ReadEntry</c> instead.
        /// </remarks>
        /// <param name="file">Path to the resource file to load.</param>
        /// <returns>Loaded resource, or <c>null</c> if not found.</returns>
        public Resource? LoadFile(string file);

        /// <summary>
        /// Same as <see cref="LoadFile"/> but appends <b>"_c"</b> to the end of the string.
        /// </summary>
        /// <param name="file">Path to the file to load (without _c suffix).</param>
        /// <returns>Loaded compiled resource, or null if not found.</returns>
        public Resource? LoadFileCompiled(string file);

        /// <summary>
        /// Loads a shader collection by name.
        /// </summary>
        /// <param name="shaderName">Name of the shader to load.</param>
        /// <returns>Loaded shader collection, or null if not found.</returns>
        public ShaderCollection? LoadShader(string shaderName);

        /// <summary>
        /// Enumerates the resource names (without the <c>"_c"</c> suffix) of every compiled file of the
        /// given resource extension known to this loader.
        /// </summary>
        /// <param name="extension">Resource extension without a leading dot, e.g. <c>"vnmclip"</c>.</param>
        /// <returns>The matching resource names; empty when none are found or enumeration is unsupported.</returns>
        public IEnumerable<string> FindFiles(string extension);

        /// <summary>
        /// Reads only a compiled file's external reference list (<c>RERL</c>), skipping its data blocks,
        /// for cheap dependency inspection over many files. Appends <c>"_c"</c> like <see cref="LoadFileCompiled"/>.
        /// </summary>
        /// <param name="file">Path to the file to load (without the _c suffix).</param>
        /// <returns>The external references, or <c>null</c> if not found.</returns>
        public ResourceExtRefList? LoadFileExternalRefs(string file);
    }
}
