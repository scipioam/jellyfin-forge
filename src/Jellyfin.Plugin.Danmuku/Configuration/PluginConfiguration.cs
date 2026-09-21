using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Danmuku.Configuration;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public string InstanceLabel { get; set; } = "Danmuku";

    /// <summary>
    /// Gets or sets a value indicating whether Jellyfin Web danmuku support is enabled.
    /// Defaults to <see langword="false"/> so the first Web exposure stays off until an
    /// administrator enables it.
    /// </summary>
    public bool EnableWebSupport { get => WebEnabled; set => WebEnabled = value; }

    public bool WebEnabled { get; set; }
    public int ShortLoadLimit { get; set; } = 5000;
    public int MediumLoadLimit { get; set; } = 8000;
    public int LongLoadLimit { get; set; } = 10000;
    public int LowDensity { get; set; } = 15;
    public int MediumDensity { get; set; } = 30;
    public int HighDensity { get; set; } = 50;

    public void Validate()
    {
        if (ShortLoadLimit < 1 || ShortLoadLimit > MediumLoadLimit || MediumLoadLimit > LongLoadLimit || LongLoadLimit > 20000
            || LowDensity < 1 || LowDensity > MediumDensity || MediumDensity > HighDensity || HighDensity > 100)
            throw new ArgumentException("Load limits must be 1–20000 and densities 1–100, both in nondecreasing order.");
    }

    public int LoadLimit(long? durationMs) => durationMs is null or < 1800000 ? ShortLoadLimit : durationMs <= 3600000 ? MediumLoadLimit : LongLoadLimit;
}
