using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StrmTrickplay.Subtitles;

/// <summary>
/// .strm 外挂字幕挂载实现。
/// </summary>
public sealed class StrmSubtitleService : IStrmSubtitleService
{
    private readonly IMediaStreamRepository _mediaStreamRepository;
    private readonly ILogger<StrmSubtitleService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StrmSubtitleService"/> class.
    /// </summary>
    /// <param name="mediaStreamRepository">媒体流仓储.</param>
    /// <param name="logger">Logger.</param>
    public StrmSubtitleService(IMediaStreamRepository mediaStreamRepository, ILogger<StrmSubtitleService> logger)
    {
        _mediaStreamRepository = mediaStreamRepository;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsHandled(BaseItem item)
    {
        return item is Video
            && !string.IsNullOrEmpty(item.Path)
            && item.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public IReadOnlyList<MediaStream> Scan(BaseItem item)
    {
        if (!IsHandled(item) || !Plugin.CurrentConfiguration.EnableSidecarSubtitles)
        {
            return [];
        }

        var result = new List<MediaStream>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in EnumerateFolders(item))
        {
            IEnumerable<string> files;
            try
            {
                if (!Directory.Exists(folder))
                {
                    continue;
                }

                files = Directory.EnumerateFiles(folder);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[StrmTrickplay] 读取字幕目录失败: {Folder}", folder);
                continue;
            }

            foreach (var file in files)
            {
                if (!SidecarSubtitleParser.IsSupported(file))
                {
                    continue;
                }

                var stem = Path.GetFileNameWithoutExtension(file);
                var mediaStem = item.FileNameWithoutExtension;
                if (string.IsNullOrEmpty(mediaStem) || !SidecarSubtitleParser.MatchesMedia(stem, mediaStem))
                {
                    continue;
                }

                var stream = SidecarSubtitleParser.Parse(file, mediaStem);
                if (stream is null || !seen.Add(stream.Path ?? file))
                {
                    continue;
                }

                if (string.IsNullOrEmpty(stream.Title))
                {
                    stream.Title = SidecarSubtitleParser.BuildTitle(stream);
                }

                result.Add(stream);
            }
        }

        // 稳定的顺序：按文件名排序，保证一次扫描多次结果一致
        return result
            .OrderBy(s => s.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<SubtitleAttachResult> AttachAsync(BaseItem item, CancellationToken cancellationToken)
    {
        if (!IsHandled(item))
        {
            return new SubtitleAttachResult(0, 0, 0, 0);
        }

        var config = Plugin.CurrentConfiguration;
        if (!config.EnableSidecarSubtitles)
        {
            return new SubtitleAttachResult(0, 0, 0, 0);
        }

        var discovered = Scan(item);
        var filter = config.SubtitleLanguageFilter;
        if (filter.Length > 0)
        {
            discovered = discovered
                .Where(s => string.IsNullOrEmpty(s.Language) || filter.Contains(s.Language, StringComparer.OrdinalIgnoreCase))
                .ToList();
        }

        var existing = _mediaStreamRepository.GetMediaStreams(new MediaStreamQuery { ItemId = item.Id }) ?? [];
        var kept = existing
            .Where(s => !(s.Type == MediaStreamType.Subtitle && s.IsExternal))
            .ToList();
        var previousExternal = existing.Count(s => s.Type == MediaStreamType.Subtitle && s.IsExternal);

        var nextIndex = kept.Count == 0 ? 0 : kept.Max(s => s.Index) + 1;
        var defaultLanguage = config.SubtitleDefaultLanguage;
        var defaultAssigned = false;

        foreach (var stream in discovered)
        {
            stream.Index = nextIndex++;
            if (string.IsNullOrEmpty(defaultLanguage))
            {
                continue;
            }

            // 指定语言的第一条外部字幕设为默认，方便客户端自动选中
            if (!defaultAssigned && string.Equals(stream.Language, defaultLanguage, StringComparison.OrdinalIgnoreCase))
            {
                stream.IsDefault = true;
                defaultAssigned = true;
            }
            else
            {
                stream.IsDefault = false;
            }
        }

        var merged = kept.Concat(discovered).ToList();
        var removed = previousExternal - discovered.Count;
        if (removed < 0)
        {
            removed = 0;
        }

        if (merged.Count != existing.Count || discovered.Count != previousExternal)
        {
            _mediaStreamRepository.SaveMediaStreams(item.Id, merged, cancellationToken);
            _logger.LogInformation(
                "[StrmTrickplay] 外挂字幕已挂载: {Name}（扫描 {Found}，挂载 {Mounted}，清理 {Removed}）",
                item.Name ?? item.Path,
                discovered.Count,
                discovered.Count,
                removed);
        }
        else if (config.VerboseLogging)
        {
            _logger.LogInformation("[StrmTrickplay] 外挂字幕无变化: {Name}（{Found} 条）", item.Name ?? item.Path, discovered.Count);
        }

        return new SubtitleAttachResult(discovered.Count, discovered.Count, removed, discovered.Count);
    }

    /// <inheritdoc />
    public async Task<(int Items, int Added)> AttachManyAsync(IReadOnlyList<BaseItem> items, CancellationToken cancellationToken)
    {
        var handled = 0;
        var added = 0;
        foreach (var item in items)
        {
            if (!IsHandled(item))
            {
                continue;
            }

            try
            {
                var result = await AttachAsync(item, cancellationToken).ConfigureAwait(false);
                handled++;
                added += result.Added;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[StrmTrickplay] 挂载外挂字幕失败: {Path}", item.Path);
            }
        }

        return (handled, added);
    }

    private IEnumerable<string> EnumerateFolders(BaseItem item)
    {
        var mediaFolder = Path.GetDirectoryName(item.Path);
        if (string.IsNullOrEmpty(mediaFolder))
        {
            yield break;
        }

        yield return mediaFolder;

        var extra = Plugin.CurrentConfiguration.SubtitleAdditionalFolders ?? [];
        foreach (var name in extra)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            yield return Path.Combine(mediaFolder, name.Trim());
        }

        // Jellyfin 的 .nfo / 元数据目录里也常常放字幕
        string internalMetadataPath;
        try
        {
            internalMetadataPath = item.GetInternalMetadataPath();
        }
        catch (Exception)
        {
            internalMetadataPath = string.Empty;
        }

        if (!string.IsNullOrEmpty(internalMetadataPath))
        {
            yield return internalMetadataPath;
        }
    }
}
