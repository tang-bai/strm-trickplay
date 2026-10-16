using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.StrmTrickplay.Configuration;

/// <summary>
/// 插件配置。
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether the plugin is enabled as a whole.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the candidate sidecar folder names relative to the media folder.
    /// {name} is replaced with the media file name without extension.
    /// </summary>
    public string[] SidecarFolderNames { get; set; } = ["{name}.trickplay", "trickplay"];

    /// <summary>
    /// Gets or sets a value indicating whether any sibling folder ending with .trickplay is accepted as a fallback.
    /// </summary>
    public bool AllowAnySiblingTrickplayFolder { get; set; }

    /// <summary>
    /// Gets or sets the width used when the tile folder carries no width information.
    /// </summary>
    public int DefaultWidth { get; set; } = 320;

    /// <summary>
    /// Gets or sets the tile grid width used when the sub-folder name has no "- WxH" suffix.
    /// </summary>
    public int DefaultTileWidth { get; set; } = 10;

    /// <summary>
    /// Gets or sets the tile grid height used when the sub-folder name has no "- WxH" suffix.
    /// </summary>
    public int DefaultTileHeight { get; set; } = 10;

    /// <summary>
    /// Gets or sets an explicit interval in milliseconds. 0 = auto (use server TrickplayOptions.Interval with bounds snapping).
    /// </summary>
    public int IntervalOverrideMs { get; set; }

    /// <summary>
    /// Gets or sets the cache lifetime in seconds for items that have trickplay tiles.
    /// </summary>
    public int CacheSeconds { get; set; } = 600;

    /// <summary>
    /// Gets or sets the cache lifetime in seconds for items without trickplay tiles (negative cache).
    /// </summary>
    public int EmptyCacheSeconds { get; set; } = 60;

    /// <summary>
    /// Gets or sets the cache lifetime in seconds used when the item has trickplay tiles but no known runtime.
    /// strm 条目的远端时长通常探测不到，此时只能取服务器配置的 interval；用较短 TTL 让时长一旦可知就自动重算。
    /// </summary>
    public int UnknownRuntimeCacheSeconds { get; set; } = 300;

    /// <summary>
    /// Gets or sets a value indicating whether to log every resolved tile set.
    /// </summary>
    public bool VerboseLogging { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the (future) ffmpeg generator is allowed to run for strm items.
    /// </summary>
    public bool EnableGeneration { get; set; }
}
