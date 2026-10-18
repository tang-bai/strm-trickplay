using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.StrmTrickplay.Subtitles;

/// <summary>
/// .strm 外挂字幕：核心对非 file 协议的条目不做外挂字幕扫描
/// （`MediaInfoResolver.GetExternalSubtitleFiles` 开头就 `if (!video.IsFileProtocol) return ...`），
/// 所以这里由插件自己扫描 .strm 同级目录并挂载（含 ASS）。
/// </summary>
public interface IStrmSubtitleService
{
    /// <summary>
    /// 判断条目是否需要本插件处理（.strm 视频）。
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>True when handled.</returns>
    bool IsHandled(BaseItem item);

    /// <summary>
    /// 扫描出候选字幕文件（不写库）。
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>Resolved streams (Index 未分配).</returns>
    IReadOnlyList<MediaStream> Scan(BaseItem item);

    /// <summary>
    /// 扫描并挂载到条目（写库，幂等）。
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Attachment result.</returns>
    Task<SubtitleAttachResult> AttachAsync(BaseItem item, CancellationToken cancellationToken);

    /// <summary>
    /// 批量挂载。
    /// </summary>
    /// <param name="items">Items.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>处理的条目数与新增字幕数.</returns>
    Task<(int Items, int Added)> AttachManyAsync(IReadOnlyList<BaseItem> items, CancellationToken cancellationToken);
}

/// <summary>
/// 挂载结果。
/// </summary>
/// <param name="Discovered">扫描到的字幕文件数.</param>
/// <param name="Added">实际新增（或更新）的外挂字幕数.</param>
/// <param name="Removed">被移除的失效外挂字幕数.</param>
/// <param name="TotalExternal">挂载后该条目的外挂字幕总数.</param>
public sealed record SubtitleAttachResult(int Discovered, int Added, int Removed, int TotalExternal);
