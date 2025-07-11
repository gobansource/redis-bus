namespace GobanSource.Bus.Redis
{
    /// <summary>
    /// Supported compression algorithms for RedisSyncBus payloads.
    /// </summary>
    public enum CompressionAlgo
    {
        /// <summary>No compression.</summary>
        None = 0,
        /// <summary>LZ4 frame compression (default pre-existing behaviour).</summary>
        LZ4 = 1,
        /// <summary>Zstandard compression via ZstdSharp.Port.</summary>
        Zstd = 2
    }
}