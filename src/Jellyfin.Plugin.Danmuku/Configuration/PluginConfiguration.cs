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
    private int low = 100, medium = 200, high = 400, overlap = 600;
    private byte supplied;
    public int LowRenderLimit { get => low; set { low = value; supplied |= 1; } }
    public int MediumRenderLimit { get => medium; set { medium = value; supplied |= 2; } }
    public int HighRenderLimit { get => high; set { high = value; supplied |= 4; } }
    public int OverlapRenderLimit { get => overlap; set { overlap = value; supplied |= 8; } }

    public void Validate(bool requireAllRenderLimits = false)
    {
        if (requireAllRenderLimits && supplied != 15)
            throw new ArgumentException("All four render limits must be supplied.");
        if (ShortLoadLimit < 1 || ShortLoadLimit > MediumLoadLimit || MediumLoadLimit > LongLoadLimit || LongLoadLimit > 20000
            || low < 1 || low > medium || medium > high || high > overlap || overlap > 600)
            throw new ArgumentException("Load limits must be 1–20000 and render limits 1–600, both in nondecreasing order.");
    }

    public int LoadLimit(long? durationMs) => durationMs is null or < 1800000 ? ShortLoadLimit : durationMs <= 3600000 ? MediumLoadLimit : LongLoadLimit;
}
