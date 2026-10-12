namespace ValveResourceFormat
{
    /// <summary>
    /// Which blocks <see cref="Resource.Read(System.IO.Stream, bool, BlockLoading)"/> parses before it returns.
    /// Every other block parses on first access.
    /// </summary>
    public enum BlockLoading
    {
        /// <summary>
        /// Every block the resource type uses at runtime parses in Read. Data only tools use, such as the edit info
        /// of most types, parses on first access.
        /// </summary>
        Runtime,

        /// <summary>
        /// No block parses in Read, so a block that fails to parse throws on first access instead. The edit info is
        /// the exception when the file name does not give the resource type, because the type is read from it.
        /// </summary>
        Deferred,
    }
}
