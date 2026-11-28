using System;
using System.IO;
using System.Reflection;

namespace Code2Create.UnitySandbox.World
{
    /// <summary>
    /// Minecraft 1.21.11 (overworld.modern) and Unity Sandbox world bounds and plot definitions.
    /// </summary>
    public static class WorldConstants
    {
        public const string ProtocolVersion = "24.0.0";
        public const int ProtocolMajor = 24;
        public const int ProtocolMinor = 0;
        public const int ProtocolPatch = 0;

        public const string McVersion = "1.21.11";
        public const string DefaultDimension = "minecraft:overworld";
        public const string DefaultServerName = "mc-remote-unity-sandbox";
        public const string DefaultServerVersion = "2400.0.0";
        public const int DefaultTcpPort = 25585;
        public const int DefaultBuildRange = 1000;
        public const int DefaultPairCodeTtlSeconds = 120;
        public const int DefaultSessionTokenTtlSeconds = 7200;

        public const int DefaultEventRingMaxEvents = 256;
        public const int DefaultEventRingMaxBytes = 262_144;
        public const int MaxEventPollResponseBytes = 61_440;
        public const int DefaultEventPollLimit = 64;
        public const int MaxEventPollLimit = 256;

        public const double DefaultSpawnX = 100.5;
        public const double DefaultSpawnY = 63.04;
        public const double DefaultSpawnZ = 114.5;
        public const double DefaultSpawnYaw = -180.0;
        public const double DefaultSpawnPitch = 8.0;

        public const int MinX = -200;
        public const int MaxX = 199;
        public const int MinZ = -200;
        public const int MaxZ = 199;
        public const double WorldBorderCenterX = 0.0;
        public const double WorldBorderCenterZ = 0.0;
        public const double WorldBorderSize = 400.0;

        public const int YMin = -64;
        public const int YMax = 319;
        public const int YSea = 62;
        public const int YGround = 63;
        public const int YCloud = 192;
        public const int YSteveMin = -64;
        public const int YSteveMax = 320;

        public const int PlotCount = 9;
        public const int PlotSize = 100;
        public const int PlotGrassHalfMin = -48;
        public const int PlotGrassHalfMax = 48;
        public const int DefaultIntroPlotIndex = 8;
        public static readonly Int3 DefaultIntroOrigin = new Int3(100, 0, 100);

        /// <summary>
        /// Proposed default limit for a single world.setBlocks volume (32 * 32 * 32 = 32,768 cells),
        /// matching McRemote distribution defaults.
        /// </summary>
        public const int DefaultMaxSetBlocksVolume = 32_768;

        public static Int3 PlotOrigin(int plotIndex)
        {
            if (plotIndex < 0 || plotIndex >= PlotCount)
            {
                throw new ArgumentOutOfRangeException(nameof(plotIndex), plotIndex, "Plot index must be between 0 and 8.");
            }

            int col = plotIndex % 3 - 1;
            int row = plotIndex / 3 - 1;
            return new Int3(col * PlotSize, 0, row * PlotSize);
        }

        public static bool IsInAnyPlotGrass(int absX, int absZ)
        {
            for (int i = 0; i < PlotCount; i++)
            {
                var origin = PlotOrigin(i);
                int dx = absX - origin.X;
                int dz = absZ - origin.Z;
                if (dx >= PlotGrassHalfMin && dx <= PlotGrassHalfMax &&
                    dz >= PlotGrassHalfMin && dz <= PlotGrassHalfMax)
                {
                    return true;
                }
            }

            return false;
        }

        public static bool IsInHorizontalBounds(int x, int z) =>
            x >= MinX && x <= MaxX && z >= MinZ && z <= MaxZ;

        public static bool IsInVerticalBounds(int y) =>
            y >= YMin && y <= YMax;

        public static bool IsInBounds(Int3 p) =>
            IsInHorizontalBounds(p.X, p.Z) && IsInVerticalBounds(p.Y);

        public static bool IsInPlayerHorizontalBounds(double x, double z) =>
            !double.IsNaN(x) && !double.IsInfinity(x) &&
            !double.IsNaN(z) && !double.IsInfinity(z) &&
            x >= MinX && x < MaxX + 1 &&
            z >= MinZ && z < MaxZ + 1;

        public static bool IsInPlayerVerticalBounds(double y) =>
            !double.IsNaN(y) && !double.IsInfinity(y) &&
            y >= YSteveMin && y <= YSteveMax;

        public static bool IsInPlayerBounds(double x, double y, double z) =>
            IsInPlayerHorizontalBounds(x, z) && IsInPlayerVerticalBounds(y);

        /// <summary>
        /// Resolves the server version from a single source of truth:
        /// explicit CLI/option value, UNITY_SANDBOX_VERSION env var, repository VERSION file,
        /// configured assembly informational version, or DefaultServerVersion.
        /// </summary>
        public static string ResolveServerVersion(string? explicitVersion = null)
        {
            if (!string.IsNullOrWhiteSpace(explicitVersion))
            {
                return explicitVersion.Trim();
            }

            string? envVersion = Environment.GetEnvironmentVariable("UNITY_SANDBOX_VERSION");
            if (!string.IsNullOrWhiteSpace(envVersion))
            {
                return envVersion.Trim();
            }

            string? assemblyVersion = TryReadConfiguredAssemblyVersion();
            if (!string.IsNullOrWhiteSpace(assemblyVersion))
            {
                return assemblyVersion;
            }

            string? fileVersion = TryReadVersionFile();
            if (!string.IsNullOrWhiteSpace(fileVersion))
            {
                return fileVersion;
            }

            return DefaultServerVersion;
        }

        private static string? TryReadConfiguredAssemblyVersion()
        {
            var assembly = typeof(WorldConstants).Assembly;
            var infoAttr = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            if (infoAttr == null || string.IsNullOrWhiteSpace(infoAttr.InformationalVersion))
            {
                return null;
            }

            string version = infoAttr.InformationalVersion.Trim();
            int plusIndex = version.IndexOf('+');
            if (plusIndex >= 0)
            {
                version = version.Substring(0, plusIndex).Trim();
            }

            // Ignore unconfigured SDK default "1.0.0" so local builds fall through to VERSION / DefaultServerVersion.
            if (string.IsNullOrEmpty(version) || string.Equals(version, "1.0.0", StringComparison.Ordinal))
            {
                return null;
            }

            return version;
        }

        private static string? TryReadVersionFile()
        {
            foreach (string root in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            {
                if (string.IsNullOrWhiteSpace(root)) continue;
                DirectoryInfo? dir;
                try
                {
                    dir = new DirectoryInfo(root);
                }
                catch
                {
                    continue;
                }

                for (int depth = 0; dir != null && depth < 8; depth++, dir = dir.Parent)
                {
                    string candidate = Path.Combine(dir.FullName, "VERSION");
                    if (!File.Exists(candidate)) continue;
                    try
                    {
                        string content = File.ReadAllText(candidate).Trim();
                        if (!string.IsNullOrEmpty(content))
                        {
                            return content;
                        }
                    }
                    catch
                    {
                    }
                }
            }

            return null;
        }
    }
}
