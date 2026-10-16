using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.StrmTrickplay.Trickplay;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StrmTrickplay.EventHandlers;

/// <summary>
/// 媒体库变更监听：.strm 条目新增/更新/删除时失效对应缓存。
/// 回调里只做失效（不做磁盘探测），避免拖慢库扫描。
/// </summary>
public sealed class LibraryChangeListener : IHostedService
{
    private readonly ILibraryManager _libraryManager;
    private readonly ISidecarTrickplayIndex _index;
    private readonly ILogger<LibraryChangeListener> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryChangeListener"/> class.
    /// </summary>
    public LibraryChangeListener(
        ILibraryManager libraryManager,
        ISidecarTrickplayIndex index,
        ILogger<LibraryChangeListener> logger)
    {
        _libraryManager = libraryManager;
        _index = index;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemChanged;
        _libraryManager.ItemUpdated += OnItemChanged;
        _libraryManager.ItemRemoved += OnItemChanged;
        _logger.LogInformation("[StrmTrickplay] 已挂接媒体库变更事件（ItemAdded / ItemUpdated / ItemRemoved）");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemChanged;
        _libraryManager.ItemUpdated -= OnItemChanged;
        _libraryManager.ItemRemoved -= OnItemChanged;
        return Task.CompletedTask;
    }

    private void OnItemChanged(object? sender, ItemChangeEventArgs e)
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
}
