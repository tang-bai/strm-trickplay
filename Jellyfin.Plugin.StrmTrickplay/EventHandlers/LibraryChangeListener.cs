using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.StrmTrickplay.Probe;
using Jellyfin.Plugin.StrmTrickplay.Trickplay;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StrmTrickplay.EventHandlers;

/// <summary>
/// 媒体库变更监听：
/// 1) .strm 条目新增/更新/删除时失效 trickplay 索引缓存（回调里只做失效，不扫盘）；
/// 2) 新条目按"防抖批次"排队，稍后自动探测媒体信息（补齐时长/编码/分辨率）。
/// </summary>
public sealed class LibraryChangeListener : IHostedService, IDisposable
{
    private readonly ILibraryManager _libraryManager;
    private readonly ISidecarTrickplayIndex _index;
    private readonly IStrmProbeService _probeService;
    private readonly ILogger<LibraryChangeListener> _logger;

    private readonly SemaphoreSlim _drainLock = new(1, 1);
    private Timer? _debounceTimer;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryChangeListener"/> class.
    /// </summary>
    public LibraryChangeListener(
        ILibraryManager libraryManager,
        ISidecarTrickplayIndex index,
        IStrmProbeService probeService,
        ILogger<LibraryChangeListener> logger)
    {
        _libraryManager = libraryManager;
        _index = index;
        _probeService = probeService;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemAdded;
        _libraryManager.ItemUpdated += OnItemChanged;
        _libraryManager.ItemRemoved += OnItemChanged;

        _debounceTimer = new Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);

        _logger.LogInformation("[StrmTrickplay] 已挂接媒体库变更事件（ItemAdded / ItemUpdated / ItemRemoved）");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        _libraryManager.ItemUpdated -= OnItemChanged;
        _libraryManager.ItemRemoved -= OnItemChanged;

        _debounceTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _debounceTimer?.Dispose();
        _debounceTimer = null;

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _debounceTimer?.Dispose();
        _drainLock.Dispose();
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        Invalidate(e);

        try
        {
            var config = Plugin.CurrentConfiguration;
            if (!config.EnableMediaProbe || !config.EnableProbeOnItemAdded)
            {
                return;
            }

            if (e.Item is not Video video || !video.IsShortcut)
            {
                return;
            }

            _probeService.EnqueueForCatchUp(video);

            var debounceSeconds = Math.Clamp(config.ProbeDebounceSeconds, 1, 3600);
            _debounceTimer?.Change(TimeSpan.FromSeconds(debounceSeconds), Timeout.InfiniteTimeSpan);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[StrmTrickplay] 处理新增条目事件失败");
        }
    }

    private void OnItemChanged(object? sender, ItemChangeEventArgs e)
    {
        Invalidate(e);
    }

    private void Invalidate(ItemChangeEventArgs e)
    {
        try
        {
            if (e.Item is not Video video || !video.IsShortcut)
            {
                return;
            }

            _index.Invalidate(video.Id);

            if (Plugin.CurrentConfiguration.VerboseLogging)
            {
                _logger.LogInformation("[StrmTrickplay] 缓存失效: {Path}", video.Path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[StrmTrickplay] 处理媒体库事件失败");
        }
    }

    private async void OnDebounceElapsed(object? state)
    {
        if (_disposed || _probeService.QueuedCount == 0)
        {
            return;
        }

        if (!await _drainLock.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false))
        {
            // 上一次批次还在跑，下一轮定时器会继续处理
            return;
        }

        try
        {
            var result = await _probeService.DrainCatchUpAsync(CancellationToken.None).ConfigureAwait(false);
            if (result.Total > 0)
            {
                _logger.LogInformation(
                    "[StrmTrickplay] 新增条目自动探测完成：共 {Total}，成功 {Probed}，失败 {Failed}",
                    result.Total,
                    result.Probed,
                    result.Failed);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[StrmTrickplay] 新增条目自动探测失败");
        }
        finally
        {
            _drainLock.Release();
        }
    }
}
