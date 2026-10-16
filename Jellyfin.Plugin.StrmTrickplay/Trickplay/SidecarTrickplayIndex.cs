using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.StrmTrickplay.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StrmTrickplay.Trickplay;

/// <summary>
/// 线程安全的侧车瓦片索引。探测成本很低（几次目录枚举），但仍做缓存，
/// 因为 DtoService 会以同步阻塞的方式调用 GetTrickplayManifest。
/// </summary>
public sealed class SidecarTrickplayIndex : ISidecarTrickplayIndex
{
    private static readonly IReadOnlyList<SidecarTileSet> EmptyTileSets = [];

    private readonly ConcurrentDictionary<Guid, CacheEntry> _cache = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _probeLocks = new();
    private readonly SidecarTrickplayParser _parser;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<SidecarTrickplayIndex> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SidecarTrickplayIndex"/> class.
    /// </summary>
    public SidecarTrickplayIndex(
        SidecarTrickplayParser parser,
        ILibraryManager libraryManager,
        ILogger<SidecarTrickplayIndex> logger)
    {
        _parser = parser;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public int CachedItemCount => _cache.Count;

    /// <inheritdoc />
    public void Invalidate(Guid itemId)
    {
        _cache.TryRemove(itemId, out _);
    }

    /// <inheritdoc />
    public void Clear()
    {
        _cache.Clear();
    }

    /// <inheritdoc />
    public void WarmUp(Video video)
    {
        _ = GetTileSets(video);
    }

    /// <inheritdoc />
    public IReadOnlyList<SidecarTileSet> GetTileSets(BaseItem item)
    {
        return GetEntry(item)?.TileSets ?? EmptyTileSets;
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<int, TrickplayInfo> GetResolutions(BaseItem item)
    {
        var entry = GetEntry(item);
        if (entry is null || entry.TileSets.Count == 0)
        {
            return EmptyResolutions;
        }

        // 返回新对象，避免调用方持有缓存内的可变实例
        return entry.TileSets.ToDictionary(
            set => set.Width,
            set => set.ToTrickplayInfo(item.Id));
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<int, TrickplayInfo> GetResolutions(Guid itemId)
    {
        if (_cache.TryGetValue(itemId, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return cached.TileSets.Count == 0
                ? EmptyResolutions
                : cached.TileSets.ToDictionary(set => set.Width, set => set.ToTrickplayInfo(itemId));
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return EmptyResolutions;
        }

        return GetResolutions(item);
    }

    /// <inheritdoc />
    public string? GetTilePath(BaseItem item, int width, int index)
    {
        var entry = GetEntry(item);
        if (entry is null)
        {
            return null;
        }

        var set = entry.TileSets.FirstOrDefault(s => s.Width == width);
        return set?.GetTilePath(index);
    }

    /// <inheritdoc />
    public string? GetTileDirectory(BaseItem item, int width)
    {
        var entry = GetEntry(item);
        return entry?.TileSets.FirstOrDefault(s => s.Width == width)?.Directory;
    }

    private static IReadOnlyDictionary<int, TrickplayInfo> EmptyResolutions => new Dictionary<int, TrickplayInfo>();

    private CacheEntry? GetEntry(BaseItem item)
    {
        var config = Plugin.CurrentConfiguration;
        if (!config.Enabled || item is not Video video || !video.IsShortcut)
        {
            return null;
        }

        if (TryGetValid(video.Id, out var cached))
        {
            return cached;
        }

        var gate = _probeLocks.GetOrAdd(video.Id, _ => new SemaphoreSlim(1, 1));
        var acquired = false;
        try
        {
            acquired = gate.Wait(TimeSpan.FromSeconds(10));
            if (!acquired)
            {
                _logger.LogWarning("[StrmTrickplay] 探测 {Path} 超时，本次跳过", video.Path);
                return null;
            }

            if (TryGetValid(video.Id, out cached))
            {
                return cached;
            }

            var entry = Probe(video, config);
            _cache[video.Id] = entry;
            return entry;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[StrmTrickplay] 解析侧车 trickplay 失败: {Path}", video.Path);
            return null;
        }
        finally
        {
            if (acquired)
            {
                gate.Release();
            }
        }
    }

    private bool TryGetValid(Guid itemId, out CacheEntry entry)
    {
        if (_cache.TryGetValue(itemId, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
        {
            entry = cached;
            return true;
        }

        entry = null!;
        return false;
    }

    private CacheEntry Probe(Video video, PluginConfiguration config)
    {
        IReadOnlyList<SidecarTileSet> sets;
        try
        {
            sets = _parser.Parse(video, config);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[StrmTrickplay] 解析异常: {Path}", video.Path);
            sets = EmptyTileSets;
        }

        var hasRuntime = video.RunTimeTicks is long runtimeTicks && runtimeTicks > 0;

        var ttlSeconds = sets.Count > 0
            ? (hasRuntime
                ? Math.Max(1, config.CacheSeconds)
                : Math.Max(1, Math.Min(config.CacheSeconds, config.UnknownRuntimeCacheSeconds)))
            : Math.Max(1, config.EmptyCacheSeconds);

        var ttl = TimeSpan.FromSeconds(ttlSeconds);

        if (sets.Count > 0)
        {
            _logger.LogInformation(
                "[StrmTrickplay] {Name} 命中 {Count} 个分辨率（时长{Source}）: {Details}",
                Path.GetFileName(video.Path),
                sets.Count,
                hasRuntime ? "已知" : "未知，interval 取服务器配置值",
                string.Join(" | ", sets.Select(s => s.ToString())));
        }
        else if (config.VerboseLogging)
        {
            _logger.LogInformation("[StrmTrickplay] {Path} 未找到侧车 trickplay 目录", video.Path);
        }

        return new CacheEntry
        {
            ExpiresAt = DateTimeOffset.UtcNow.Add(ttl),
            TileSets = sets
        };
    }

    private sealed class CacheEntry
    {
        public DateTimeOffset ExpiresAt { get; init; }

        public required IReadOnlyList<SidecarTileSet> TileSets { get; init; }
    }
}
