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

    // —————— STRM 媒体信息探测（补齐时长 / 编码 / 分辨率 / 音轨）——————

    /// <summary>
    /// Gets or sets a value indicating whether STRM media info probing is enabled at all.
    /// </summary>
    public bool EnableMediaProbe { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether newly added strm items are probed automatically during library scans.
    /// </summary>
    public bool EnableProbeOnItemAdded { get; set; } = true;

    /// <summary>
    /// Gets or sets the debounce delay (seconds) before a catch-up batch is probed, so a scan's burst of ItemAdded events is batched.
    /// </summary>
    public int ProbeDebounceSeconds { get; set; } = 30;

    /// <summary>
    /// Gets or sets the number of parallel probe operations (1-20).
    /// </summary>
    public int ProbeParallelism { get; set; } = 3;

    /// <summary>
    /// Gets or sets the per-item probe timeout in seconds (5-600).
    /// </summary>
    public int ProbeTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Gets or sets the cooldown delay in milliseconds between probes, to avoid hammering the upstream storage (0-10000).
    /// </summary>
    public int ProbeCooldownMs { get; set; } = 200;

    /// <summary>
    /// Gets or sets the library names to probe. Empty means all libraries.
    /// </summary>
    public string[] ProbeLibraryNames { get; set; } = [];

    // —————— 起播加速 ——————

    /// <summary>
    /// Gets or sets a value indicating whether .strm items whose media info is already complete skip the
    /// playback-time remote probe that Jellyfin forces for every strm item.
    /// </summary>
    public bool EnableFastStartup { get; set; } = true;
}
