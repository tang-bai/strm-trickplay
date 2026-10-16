using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.StrmTrickplay.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StrmTrickplay.Trickplay;

/// <summary>
/// 解析媒体文件同级目录下已经生成好的 trickplay 瓦片目录。
/// 目录规范与 Jellyfin 原生 `SaveTrickplayWithMedia` 一致：
/// `&lt;媒体目录&gt;/&lt;文件名&gt;.trickplay/&lt;width&gt; - &lt;tileWidth&gt;x&lt;tileHeight&gt;/&lt;index&gt;.jpg`。
/// </summary>
public sealed class SidecarTrickplayParser
{
    private static readonly Regex GridPattern = new(
        "^([0-9]+)\\s*-\\s*([0-9]+)x([0-9]+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex WidthOnlyPattern = new(
        "^([0-9]+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] DefaultPatterns = ["{name}.trickplay", "trickplay"];

    private readonly IImageEncoder _imageEncoder;
    private readonly IServerConfigurationManager _configManager;
    private readonly ILogger<SidecarTrickplayParser> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SidecarTrickplayParser"/> class.
    /// </summary>
    public SidecarTrickplayParser(
        IImageEncoder imageEncoder,
        IServerConfigurationManager configManager,
        ILogger<SidecarTrickplayParser> logger)
    {
        _imageEncoder = imageEncoder;
        _configManager = configManager;
        _logger = logger;
    }

    /// <summary>
    /// 返回所有候选侧车根目录（按优先级，去重）。
    /// </summary>
    /// <param name="video">The video.</param>
    /// <param name="config">Plugin configuration.</param>
    /// <returns>Candidate absolute directories.</returns>
    public IEnumerable<string> GetCandidateRoots(Video video, PluginConfiguration config)
    {
        var folder = video.ContainingFolderPath;
        if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(video.Path))
        {
            yield break;
        }

        var name = Path.GetFileNameWithoutExtension(video.Path);
        var patterns = config.SidecarFolderNames is { Length: > 0 } ? config.SidecarFolderNames : DefaultPatterns;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                continue;
            }

            var dirName = pattern.Replace("{name}", name, StringComparison.OrdinalIgnoreCase);
            string full;
            try
            {
                full = Path.GetFullPath(Path.Combine(folder, dirName));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[StrmTrickplay] 无法拼接候选目录 {Folder}/{Dir}", folder, dirName);
                continue;
            }

            if (seen.Add(full))
            {
                yield return full;
            }
        }

        if (config.AllowAnySiblingTrickplayFolder)
        {
            foreach (var dir in SafeEnumerateDirectories(folder, "*.trickplay"))
            {
                var full = Path.GetFullPath(dir);
                if (seen.Add(full))
                {
                    yield return full;
                }
            }
        }
    }

    /// <summary>
    /// 解析出所有瓦片集。
    /// </summary>
    /// <param name="video">The video.</param>
    /// <param name="config">Plugin configuration.</param>
    /// <returns>Parsed tile sets, ordered by root priority then width.</returns>
    public IReadOnlyList<SidecarTileSet> Parse(Video video, PluginConfiguration config)
    {
        var results = new List<SidecarTileSet>();

        foreach (var root in GetCandidateRoots(video, config))
        {
            if (!SafeDirectoryExists(root))
            {
                continue;
            }

            var found = new List<SidecarTileSet>();

            foreach (var sub in SafeEnumerateDirectories(root, "*"))
            {
                var set = TryParseTileFolder(video, config, root, sub);
                if (set is not null)
                {
                    found.Add(set);
                }
            }

            // 兜底：瓦片直接躺在侧车根目录里
            if (found.Count == 0)
            {
                var flat = TryParseTileFolder(video, config, root, root, flat: true);
                if (flat is not null)
                {
                    found.Add(flat);
                }
            }

            if (found.Count > 0)
            {
                results.AddRange(found.OrderByDescending(s => s.Width));
                break; // 第一个有效的候选根目录即胜出
            }
        }

        return results;
    }

    private SidecarTileSet? TryParseTileFolder(Video video, PluginConfiguration config, string root, string dir, bool flat = false)
    {
        var dirName = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        int width;
        int tileWidth;
        int tileHeight;

        if (flat)
        {
            width = config.DefaultWidth > 0 ? config.DefaultWidth : 320;
            tileWidth = config.DefaultTileWidth > 0 ? config.DefaultTileWidth : 10;
            tileHeight = config.DefaultTileHeight > 0 ? config.DefaultTileHeight : 10;
        }
        else
        {
            var grid = GridPattern.Match(dirName);
            if (grid.Success)
            {
                width = ToInt(grid.Groups[1].Value);
                tileWidth = ToInt(grid.Groups[2].Value);
                tileHeight = ToInt(grid.Groups[3].Value);
            }
            else
            {
                var widthOnly = WidthOnlyPattern.Match(dirName);
                if (!widthOnly.Success)
                {
                    return null;
                }

                width = ToInt(widthOnly.Groups[1].Value);
                tileWidth = config.DefaultTileWidth > 0 ? config.DefaultTileWidth : 10;
                tileHeight = config.DefaultTileHeight > 0 ? config.DefaultTileHeight : 10;
            }
        }

        if (width <= 0 || tileWidth <= 0 || tileHeight <= 0)
        {
            return null;
        }

        var files = SafeEnumerateFiles(dir, "*.jpg")
            .OrderBy(f => TileIndex(f))
            .ThenBy(f => f, StringComparer.Ordinal)
            .ToList();

        if (files.Count == 0)
        {
            return null;
        }

        var (interval, thumbnailCount) = ComputeInterval(video, config, files.Count, tileWidth * tileHeight);
        var height = ComputeThumbnailHeight(dir, files[0], tileHeight, width, config);
        var bandwidth = ComputeBandwidth(files, tileWidth, tileHeight, interval);

        var set = new SidecarTileSet
        {
            Root = root,
            Directory = dir,
            Width = width,
            Height = height,
            TileWidth = tileWidth,
            TileHeight = tileHeight,
            ThumbnailCount = thumbnailCount,
            Interval = interval,
            Bandwidth = bandwidth,
            Files = files
        };

        if (config.VerboseLogging)
        {
            _logger.LogInformation("[StrmTrickplay] 解析到瓦片集 {Set} (media: {Path})", set, video.Path);
        }

        return set;
    }

    /// <summary>
    /// 与官方 Jellyfin.Server.Implementations.Trickplay.TrickplayManager.DiscoverExistingTrickplayAsync 相同的推断算法：
    /// 编码器会把最后一张瓦片补齐成完整网格，因此缩略图总数无法直接读出，只能由瓦片数与单瓦片容量上下界夹逼，
    /// 并优先采用服务器配置的 interval（若落在可行区间内）。
    /// </summary>
    private (int Interval, int ThumbnailCount) ComputeInterval(Video video, PluginConfiguration config, int tileCount, int thumbsPerTile)
    {
        var maxThumbs = tileCount * thumbsPerTile;
        var minThumbs = tileCount > 1 ? ((tileCount - 1) * thumbsPerTile) + 1 : 1;

        // 显式覆盖优先：这是用户手上的逃生开关（strm 的远端时长可能探测不准），不做区间夹逼
        if (config.IntervalOverrideMs > 0)
        {
            var forced = Math.Max(1000, config.IntervalOverrideMs);
            var forcedCount = maxThumbs;
            if (video.RunTimeTicks is long forcedTicks && forcedTicks > 0)
            {
                var forcedRuntimeMs = forcedTicks / TimeSpan.TicksPerMillisecond;
                forcedCount = Math.Clamp((int)Math.Round(forcedRuntimeMs / (double)forced), minThumbs, maxThumbs);
            }

            return (forced, forcedCount);
        }

        var configured = _configManager.Configuration.TrickplayOptions.Interval > 0
            ? _configManager.Configuration.TrickplayOptions.Interval
            : 10000;

        if (video.RunTimeTicks is long ticks && ticks > 0)
        {
            var runtimeMs = ticks / TimeSpan.TicksPerMillisecond;
            var minInterval = Math.Max(1000L, (long)Math.Ceiling(runtimeMs / (double)maxThumbs));
            var maxInterval = Math.Max(minInterval, (long)Math.Floor(runtimeMs / (double)minThumbs));

            int interval;
            if (configured >= minInterval && configured <= maxInterval)
            {
                interval = configured;
            }
            else
            {
                var midpoint = (minInterval + maxInterval) / 2.0;
                var snapped = (long)Math.Round(midpoint / 1000d) * 1000L;
                interval = (int)Math.Clamp(snapped, minInterval, maxInterval);
            }

            var thumbnailCount = Math.Clamp(
                (int)Math.Round(runtimeMs / (double)interval),
                minThumbs,
                maxThumbs);

            return (interval, thumbnailCount);
        }

        return (Math.Max(1000, configured), maxThumbs);
    }

    private int ComputeThumbnailHeight(string dir, string firstTile, int tileHeight, int width, PluginConfiguration config)
    {
        try
        {
            var size = _imageEncoder.GetImageSize(firstTile);
            if (size.Height > 0)
            {
                return Math.Max(1, (int)Math.Ceiling((double)size.Height / tileHeight));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[StrmTrickplay] 无法读取瓦片图尺寸 {File}", firstTile);
        }

        // 兜底：按 16:9 估算
        _ = dir;
        _ = config;
        return Math.Max(1, width * 9 / 16);
    }

    private static int ComputeBandwidth(List<string> files, int tileWidth, int tileHeight, int interval)
    {
        var bandwidth = 0;
        var seconds = interval / 1000m;
        if (seconds <= 0)
        {
            seconds = 10m;
        }

        foreach (var file in files)
        {
            try
            {
                var length = new FileInfo(file).Length;
                var bitrate = (int)Math.Ceiling((decimal)length * 8 / tileWidth / tileHeight / seconds);
                bandwidth = Math.Max(bandwidth, bitrate);
            }
            catch (Exception)
            {
                // 忽略无法读取的文件
            }
        }

        return bandwidth;
    }

    private static int TileIndex(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) ? index : int.MaxValue;
    }

    private static int ToInt(string value)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : -1;
    }

    private static bool SafeDirectoryExists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private IEnumerable<string> SafeEnumerateDirectories(string path, string pattern)
    {
        try
        {
            return Directory.EnumerateDirectories(path, pattern).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[StrmTrickplay] 枚举目录失败 {Path}", path);
            return [];
        }
    }

    private IEnumerable<string> SafeEnumerateFiles(string path, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(path, pattern).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[StrmTrickplay] 枚举文件失败 {Path}", path);
            return [];
        }
    }
}
