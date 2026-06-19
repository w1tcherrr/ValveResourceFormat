using ValveResourceFormat.Blocks;
using ValveResourceFormat.CompiledShader;

namespace ValveResourceFormat.IO
{
    /// <summary>
    /// This file loader is primarily for testing, always returns null for any file load.
    /// </summary>
    public class NullFileLoader : IFileLoader
    {
        /// <inheritdoc/>
        /// <remarks>
        /// Always returns null.
        /// </remarks>
        public Resource? LoadFile(string file) => null;

        /// <inheritdoc/>
        /// <remarks>
        /// Always returns null.
        /// </remarks>
        public Resource? LoadFileCompiled(string file) => null;

        /// <inheritdoc/>
        /// <remarks>
        /// Always returns null.
        /// </remarks>
        public ShaderCollection? LoadShader(string shaderName) => null;

        /// <inheritdoc/>
        /// <remarks>
        /// Always returns an empty enumeration.
        /// </remarks>
        public IEnumerable<string> FindFiles(string extension) => [];

        /// <inheritdoc/>
        /// <remarks>
        /// Always returns null.
        /// </remarks>
        public ResourceExtRefList? LoadFileExternalRefs(string file) => null;
    }
}
