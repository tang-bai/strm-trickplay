using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.StrmTrickplay.Trickplay;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StrmTrickplay.Tasks;

/// <summary>
/// 计划任务：遍历所有 .strm 条目，重建侧车 trickplay 索引缓存。
/// </summary>
public sealed class RefreshStrmTrickplayIndexTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly ISidecarTrickplayIndex _index;
    private readonly ILogger<RefreshStrmTrickplayIndexTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RefreshStrmTrickplayIndexTask"/> class.
    /// </summary>
    public RefreshStrmTrickplayIndexTask(
        ILibraryManager libraryManager,
        ISidecarTrickplayIndex index,
        ILogger<RefreshStrmTrickplayIndexTask> logger)
    {
        _libraryManager = libraryManager;
        _index = index;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "刷新 STRM Trickplay 索引";

    /// <inheritdoc />
    public string Key => "StrmTrickplayRefreshIndex";

    /// <inheritdoc />
    public string Description => "遍历所有 .strm 条目，重新探测媒体目录下已生成的 .trickplay 瓦片目录并刷新缓存。";

    /// <inheritdoc />
    public string Category => "STRM Trickplay";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        progress?.Report(0);

        var items = new List<Video>();
        try
        {
            var query = new InternalItemsQuery
            {
                Recursive = true,
                IsFolder = false,
                MediaTypes = [MediaType.Video]
            };

            items = _libraryManager.GetItemList(query).OfType<Video>().Where(v => v.IsShortcut).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[StrmTrickplay] 枚举 strm 条目失败");
        }

        _index.Clear();

        for (var i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _index.WarmUp(items[i]);
            progress?.Report(100.0 * (i + 1) / items.Count);
        }

        _logger.LogInformation("[StrmTrickplay] 索引刷新完成，共处理 {Count} 个 strm 条目", items.Count);
        progress?.Report(100);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
            }
        ];
    }
}
