using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.StrmTrickplay.Subtitles;
using Jellyfin.Plugin.StrmTrickplay.Trickplay;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StrmTrickplay.Probe;

/// <summary>
/// STRM 媒体信息探测实现。
///
/// Jellyfin 把 .strm 当作 shortcut，库扫描时不会对它跑 ffprobe，所以条目没有时长/编码/分辨率，
/// 表现为：UI 里没有时长、几秒钟就被标记为已看、无法按分辨率/编码筛选。
///
/// 做法与 Jellyfin 播放时刷新元数据一致：把 <see cref="MetadataRefreshOptions.EnableRemoteContentProbe"/>
/// 置为 true 再刷新该条目，就会对远端直链跑一次 ffprobe。
/// 优先直接调用探测提供程序（快，且不会顺带重跑 TMDb 等远程刮削），失败再回退到完整刷新流程。
/// </summary>
public sealed class StrmProbeService : IStrmProbeService
{
    private readonly IProviderManager _providerManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IDirectoryService _directoryService;
    private readonly ISidecarTrickplayIndex _trickplayIndex;
    private readonly IStrmSubtitleService _subtitleService;
    private readonly ILogger<StrmProbeService> _logger;

    private readonly ConcurrentQueue<BaseItem> _catchUpQueue = new();
    private readonly object _runLock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="StrmProbeService"/> class.
    /// </summary>
    public StrmProbeService(
        IProviderManager providerManager,
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        IDirectoryService directoryService,
        ISidecarTrickplayIndex trickplayIndex,
        IStrmSubtitleService subtitleService,
        ILogger<StrmProbeService> logger)
    {
        _providerManager = providerManager;
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _directoryService = directoryService;
        _trickplayIndex = trickplayIndex;
        _subtitleService = subtitleService;
        _logger = logger;
    }

    /// <inheritdoc />
    public int QueuedCount => _catchUpQueue.Count;

    /// <inheritdoc />
    public ProbeRunStats? LastRun { get; private set; }

    /// <inheritdoc />
    public bool NeedsProbe(BaseItem item)
    {
        if (item is not Video || string.IsNullOrEmpty(item.Path))
        {
            return false;
        }

        if (!item.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 已有媒体流也已有时长 => 不用再探测
        var streams = _mediaSourceManager.GetMediaStreams(item.Id);
        return streams.Count == 0 || item.RunTimeTicks is null or 0;
    }

    /// <inheritdoc />
    public IReadOnlyList<BaseItem> GetUnprobedItems()
    {
        var names = Plugin.CurrentConfiguration.ProbeLibraryNames ?? [];
        var libraryIds = ResolveSelectedLibraryIds(names);

        // 选了媒体库但拿不到 id 时，退化为逐个条目按所属媒体库名过滤
        var needNameFilter = names.Length > 0 && libraryIds.Length == 0;

        var query = new InternalItemsQuery
        {
            Recursive = true,
            IsFolder = false
        };

        if (libraryIds.Length > 0)
        {
            query.AncestorIds = libraryIds;
        }

        IReadOnlyList<Guid> ids;
        try
        {
            ids = _libraryManager.GetItemIds(query);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[StrmTrickplay] 枚举媒体库条目失败");
            return [];
        }

        var result = new List<BaseItem>();
        foreach (var id in ids)
        {
            var item = _libraryManager.GetItemById(id);
            if (item is null || !NeedsProbe(item))
            {
                continue;
            }

            if (needNameFilter && !IsInSelectedLibraries(item, names))
            {
                continue;
            }

            result.Add(item);
        }

        _logger.LogInformation(
            "[StrmTrickplay] 扫描 {Total} 个条目，其中 {Count} 个 .strm 需要探测媒体信息",
            ids.Count,
            result.Count);

        return result;
    }

    /// <inheritdoc />
    public void EnqueueForCatchUp(BaseItem item)
    {
        _catchUpQueue.Enqueue(item);
    }

    /// <inheritdoc />
    public async Task<ProbeBatchResult> DrainCatchUpAsync(CancellationToken cancellationToken)
    {
        var items = new List<BaseItem>();
        while (_catchUpQueue.TryDequeue(out var item))
        {
            items.Add(item);
        }

        if (items.Count == 0)
        {
            return new ProbeBatchResult(0, 0, 0, TimeSpan.Zero, []);
        }

        return await ProbeAsync(items, "新增条目", null, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ProbeBatchResult> ProbeAllUnprobedAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(0);

        var items = GetUnprobedItems();
        if (items.Count == 0)
        {
            progress?.Report(100);
            return new ProbeBatchResult(0, 0, 0, TimeSpan.Zero, []);
        }

        return await ProbeAsync(items, "计划任务", progress, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ProbeItemResult> ProbeItemAsync(BaseItem item, CancellationToken cancellationToken)
    {
        var config = Plugin.CurrentConfiguration;
        var runtimeBefore = item.RunTimeTicks;
        var streamsBefore = _mediaSourceManager.GetMediaStreams(item.Id).Count;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(config.ProbeTimeoutSeconds, 5, 600)));

        var options = new MetadataRefreshOptions(_directoryService)
        {
            EnableRemoteContentProbe = true,
            MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
            ImageRefreshMode = MetadataRefreshMode.ValidationOnly,
            ReplaceAllMetadata = false,
            ReplaceAllImages = false
        };

        try
        {
            if (!await TryDirectProbeAsync(item, options, timeoutCts.Token).ConfigureAwait(false))
            {
                await _providerManager.RefreshSingleItem(item, options, timeoutCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "[StrmTrickplay] 探测超时（{Timeout} 秒）: {Path}",
                config.ProbeTimeoutSeconds,
                item.Path);

            return new ProbeItemResult(false, "timeout", runtimeBefore, item.RunTimeTicks, streamsBefore, _mediaSourceManager.GetMediaStreams(item.Id).Count);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[StrmTrickplay] 探测失败: {Path}", item.Path);
            return new ProbeItemResult(false, ex.Message, runtimeBefore, item.RunTimeTicks, streamsBefore, _mediaSourceManager.GetMediaStreams(item.Id).Count);
        }

        // 探测成功后让 trickplay 索引重算：拿到时长后，缩略图间隔就能按官方算法精确推断
        _trickplayIndex.Invalidate(item.Id);

        // 顺带挂载同级目录的外挂字幕（核心对非 file 协议的 strm 不做这一步）
        try
        {
            await _subtitleService.AttachAsync(item, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[StrmTrickplay] 探测后挂载外挂字幕失败: {Path}", item.Path);
        }

        var streamsAfter = _mediaSourceManager.GetMediaStreams(item.Id).Count;
        var runtimeAfter = item.RunTimeTicks;
        var success = streamsAfter > 0 || runtimeAfter is > 0;

        return new ProbeItemResult(
            success,
            success ? "ok" : "no-data",
            runtimeBefore,
            runtimeAfter,
            streamsBefore,
            streamsAfter);
    }

    private async Task<ProbeBatchResult> ProbeAsync(
        IReadOnlyList<BaseItem> items,
        string trigger,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var config = Plugin.CurrentConfiguration;
        var parallelism = Math.Clamp(config.ProbeParallelism, 1, 20);
        var cooldownMs = Math.Clamp(config.ProbeCooldownMs, 0, 10000);

        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var probed = 0;
        var failed = 0;
        var processed = 0;
        var failedNames = new ConcurrentBag<string>();

        _logger.LogInformation(
            "[StrmTrickplay] 开始探测 {Count} 个条目（{Trigger}，并发 {Parallelism}）",
            items.Count,
            trigger,
            parallelism);

        await Parallel.ForEachAsync(
            items,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = parallelism,
                CancellationToken = cancellationToken
            },
            async (item, ct) =>
            {
                var result = await ProbeItemAsync(item, ct).ConfigureAwait(false);

                if (result.Success)
                {
                    Interlocked.Increment(ref probed);
                }
                else
                {
                    Interlocked.Increment(ref failed);
                    failedNames.Add(item.Name ?? item.Path ?? item.Id.ToString("N", System.Globalization.CultureInfo.InvariantCulture));
                }

                var current = Interlocked.Increment(ref processed);
                progress?.Report((double)current / items.Count * 100);

                if (cooldownMs > 0)
                {
                    await Task.Delay(cooldownMs, ct).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);

        stopwatch.Stop();

        var result2 = new ProbeBatchResult(
            items.Count,
            probed,
            failed,
            stopwatch.Elapsed,
            failedNames.Take(20).ToArray());

        lock (_runLock)
        {
            LastRun = new ProbeRunStats(trigger, startedAt, DateTimeOffset.UtcNow, items.Count, probed, failed);
        }

        _logger.LogInformation(
            "[StrmTrickplay] 探测完成：成功 {Probed}，失败 {Failed}，总 {Total}，耗时 {Duration}",
            probed,
            failed,
            items.Count,
            stopwatch.Elapsed);

        progress?.Report(100);
        return result2;
    }

    private async Task<bool> TryDirectProbeAsync(BaseItem item, MetadataRefreshOptions options, CancellationToken cancellationToken)
    {
        if (item is not Video video)
        {
            return false;
        }

        var libraryOptions = _libraryManager.GetLibraryOptions(item);

        IMetadataProvider? provider = item switch
        {
            Movie => FindProbeProvider(_providerManager.GetMetadataProviders<Movie>(item, libraryOptions)),
            Episode => FindProbeProvider(_providerManager.GetMetadataProviders<Episode>(item, libraryOptions)),
            _ => FindProbeProvider(_providerManager.GetMetadataProviders<Video>(item, libraryOptions))
        };

        if (provider is null)
        {
            return false;
        }

        ItemUpdateType updateType;
        switch (item)
        {
            case Movie movie when provider is ICustomMetadataProvider<Movie> movieProvider:
                updateType = await movieProvider.FetchAsync(movie, options, cancellationToken).ConfigureAwait(false);
                break;

            case Episode episode when provider is ICustomMetadataProvider<Episode> episodeProvider:
                updateType = await episodeProvider.FetchAsync(episode, options, cancellationToken).ConfigureAwait(false);
                break;

            default:
                if (provider is not ICustomMetadataProvider<Video> videoProvider)
                {
                    return false;
                }

                updateType = await videoProvider.FetchAsync(video, options, cancellationToken).ConfigureAwait(false);
                break;
        }

        if (updateType > ItemUpdateType.None)
        {
            await video.UpdateToRepositoryAsync(updateType, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>
    /// 在已启用的元数据提供程序里找出探测提供程序（Jellyfin 内部的 ProbeProvider）。
    /// 按类型名匹配，避免插件直接依赖宿主程序集 MediaBrowser.Providers。
    /// </summary>
    private static IMetadataProvider? FindProbeProvider<T>(IEnumerable<T> providers)
        where T : IMetadataProvider
    {
        return providers.FirstOrDefault(p => p.GetType().Name.Contains("Probe", StringComparison.OrdinalIgnoreCase));
    }

    private Guid[] ResolveSelectedLibraryIds(string[] names)
    {
        if (names.Length == 0)
        {
            return [];
        }

        try
        {
            var ids = new List<Guid>();
            foreach (var folder in _libraryManager.GetVirtualFolders())
            {
                if (folder is null || !names.Contains(folder.Name, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (Guid.TryParse(folder.ItemId, out var id))
                {
                    ids.Add(id);
                }
            }

            if (ids.Count == 0)
            {
                _logger.LogWarning("[StrmTrickplay] 未能把所选媒体库名解析成 id，改为按名称逐个过滤");
            }

            return ids.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[StrmTrickplay] 读取媒体库列表失败，改为探测全部媒体库");
            return [];
        }
    }

    private bool IsInSelectedLibraries(BaseItem item, string[] names)
    {
        try
        {
            return _libraryManager.GetCollectionFolders(item)
                .Any(folder => folder is not null && names.Contains(folder.Name, StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[StrmTrickplay] 判断条目所属媒体库失败，按通过处理");
            return true;
        }
    }
}
