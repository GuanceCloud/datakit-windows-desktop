using System.IO;

namespace Guance.Windows.Queue;

internal static class CachePath
{
    public static string GetRoot(GuanceConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.CacheDirectory))
        {
            return Path.GetFullPath(config.CacheDirectory);
        }

        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.Combine(Path.GetTempPath(), "Guance");
        }

        return Path.Combine(root, "Guance", "Rum");
    }
}
