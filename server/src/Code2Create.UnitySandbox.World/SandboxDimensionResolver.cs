namespace Code2Create.UnitySandbox.World
{
    /// <summary>
    /// Validates and resolves DimensionRef values per wire-format-design_ja.md §5.0.2 / §5.1 and 2026-11-14-01.
    /// Unity Sandbox only loads minecraft:overworld.
    /// </summary>
    public static class SandboxDimensionResolver
    {
        public static string ResolveDimension(string? dimensionRef)
        {
            if (string.IsNullOrEmpty(dimensionRef) ||
                !SandboxBlockRegistry.TryNormalizeResourceId(dimensionRef, out string canonicalKey))
            {
                throw new SandboxOperationException(SandboxProtocolError.InvalidParams());
            }

            if (!string.Equals(canonicalKey, WorldConstants.DefaultDimension, System.StringComparison.Ordinal))
            {
                throw new SandboxOperationException(SandboxProtocolError.UnknownDimension(canonicalKey));
            }

            return canonicalKey;
        }
    }
}
