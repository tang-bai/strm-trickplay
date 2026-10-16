using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Trickplay;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StrmTrickplay.Trickplay;

/// <summary>
/// ITrickplayManager 装饰器：对 .strm（shortcut）条目补齐侧车 trickplay 数据，
/// 其余全部透传核心实现。核心 GetTrickplayManifest 会跳过 IsRemote 的媒体源，
/// 而 strm 的媒体源一定 IsRemote，这正是本插件存在的唯一理由。
/// </summary>
public sealed class StrmTrickplayManager : ITrickplayManager
{
    private readonly ITrickplayManager _fallback;
    private readonly ISidecarTrickplayIndex _index;
    private readonly ITrickplayGenerator _generator;
    private readonly ILogger<StrmTrickplayManager> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StrmTrickplayManager"/> class.
    /// </summary>
    /// <param name="fallback">核心实现（或链上的前一个实现）。</param>
    /// <param name="index">侧车索引。</param>
    /// <param name="generator">生成器扩展点。</param>
    /// <param name="logger">Logger.</param>
    public StrmTrickplayManager(
        ITrickplayManager fallback,
        ISidecarTrickplayIndex index,
        ITrickplayGenerator generator,
        ILogger<StrmTrickplayManager> logger)
    {
        _fallback = fallback;
        _index = index;
        _generator = generator;
        _logger = logger;
        _logger.LogInformation("[StrmTrickplay] ITrickplayManager 装饰器已生效，回退实现: {Fallback}", fallback.GetType().FullName);
    }

    private static bool Enabled => Plugin.CurrentConfiguration.Enabled;

    /// <inheritdoc />
    public async Task<Dictionary<string, Dictionary<int, TrickplayInfo>>> GetTrickplayManifest(BaseItem item)
    {
        var manifest = await _fallback.GetTrickplayManifest(item).ConfigureAwait(false);

        if (!Enabled || item is not Video video || !video.IsShortcut)
        {
            return manifest;
        }

        var resolutions = _index.GetResolutions(video);
        if (resolutions.Count == 0)
        {
            return manifest;
        }

        foreach (var mediaSource in video.GetMediaSources(false))
        {
            if (!Guid.TryParse(mediaSource.Id, out _))
            {
                continue;
            }

            // 刻意不跳过 mediaSource.IsRemote —— 这就是本插件要修的那一行
            manifest[mediaSource.Id] = resolutions.ToDictionary(
                pair => pair.Key,
                pair => Clone(pair.Value));
        }

        return manifest;
    }

    /// <inheritdoc />
    public Task<Dictionary<int, TrickplayInfo>> GetTrickplayResolutions(Guid itemId)
    {
        if (Enabled)
        {
            var resolutions = _index.GetResolutions(itemId);
            if (resolutions.Count > 0)
            {
                return Task.FromResult(resolutions.ToDictionary(pair => pair.Key, pair => Clone(pair.Value)));
            }
        }

        return _fallback.GetTrickplayResolutions(itemId);
    }

    /// <inheritdoc />
    public async Task<string> GetTrickplayTilePathAsync(BaseItem item, int width, int index, bool saveWithMedia)
    {
        if (Enabled && item is Video video && video.IsShortcut)
        {
            var path = _index.GetTilePath(video, width, index);
            if (!string.IsNullOrEmpty(path))
            {
                return path;
            }
        }

        return await _fallback.GetTrickplayTilePathAsync(item, width, index, saveWithMedia).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public string GetTrickplayDirectory(BaseItem item, int tileWidth, int tileHeight, int width, bool saveWithMedia = false)
    {
        if (Enabled && item is Video video && video.IsShortcut)
        {
            var directory = _index.GetTileDirectory(video, width);
            if (!string.IsNullOrEmpty(directory))
            {
                return directory;
            }
        }

        return _fallback.GetTrickplayDirectory(item, tileWidth, tileHeight, width, saveWithMedia);
    }

    /// <inheritdoc />
    public async Task<string?> GetHlsPlaylist(Guid itemId, int width, string? apiKey)
    {
        if (Enabled)
        {
            var resolutions = _index.GetResolutions(itemId);
            if (resolutions.TryGetValue(width, out var info))
            {
                var playlist = HlsPlaylistBuilder.Build(itemId, info, apiKey);
                if (!string.IsNullOrEmpty(playlist))
                {
                    return playlist;
                }
            }
        }

        return await _fallback.GetHlsPlaylist(itemId, width, apiKey).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RefreshTrickplayDataAsync(Video video, bool replace, LibraryOptions libraryOptions, CancellationToken cancellationToken)
    {
        if (Enabled && Plugin.CurrentConfiguration.EnableGeneration && video.IsShortcut && _generator.IsAvailable)
        {
            if (await _generator.TryGenerateAsync(video, cancellationToken).ConfigureAwait(false))
            {
                _index.Invalidate(video.Id);
                return;
            }
        }

        await _fallback.RefreshTrickplayDataAsync(video, replace, libraryOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public TrickplayInfo CreateTiles(IReadOnlyList<string> images, int width, TrickplayOptions options, string outputDir)
    {
        return _fallback.CreateTiles(images, width, options, outputDir);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TrickplayInfo>> GetTrickplayItemsAsync(int limit, int offset)
    {
        return _fallback.GetTrickplayItemsAsync(limit, offset);
    }

    /// <inheritdoc />
    public Task SaveTrickplayInfo(TrickplayInfo info)
    {
        return _fallback.SaveTrickplayInfo(info);
    }

    /// <inheritdoc />
    public async Task DeleteTrickplayDataAsync(Guid itemId, CancellationToken cancellationToken)
    {
        await _fallback.DeleteTrickplayDataAsync(itemId, cancellationToken).ConfigureAwait(false);
        _index.Invalidate(itemId);
    }

    /// <inheritdoc />
    public async Task MoveGeneratedTrickplayDataAsync(Video video, LibraryOptions libraryOptions, CancellationToken cancellationToken)
    {
        await _fallback.MoveGeneratedTrickplayDataAsync(video, libraryOptions, cancellationToken).ConfigureAwait(false);
        _index.Invalidate(video.Id);
    }

    private static TrickplayInfo Clone(TrickplayInfo info)
    {
        return new TrickplayInfo
        {
            ItemId = info.ItemId,
            Width = info.Width,
            Height = info.Height,
            TileWidth = info.TileWidth,
            TileHeight = info.TileHeight,
            ThumbnailCount = info.ThumbnailCount,
            Interval = info.Interval,
            Bandwidth = info.Bandwidth
        };
    }
}
