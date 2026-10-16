using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.StrmTrickplay.Trickplay;

/// <summary>
/// 生成器扩展点：本期只读取现成文件，不实现 ffmpeg 抽帧。
/// 将来接入时只需实现本接口并替换 DI 注册，读路径无需改动。
/// </summary>
public interface ITrickplayGenerator
{
    /// <summary>
    /// Gets a value indicating whether a usable generator implementation is present.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// 尝试为某个 strm 条目生成 trickplay 瓦片。
    /// </summary>
    /// <param name="video">The video.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when tiles were generated.</returns>
    Task<bool> TryGenerateAsync(Video video, CancellationToken cancellationToken);
}

/// <summary>
/// 空实现（默认注册）。
/// </summary>
public sealed class NoopTrickplayGenerator : ITrickplayGenerator
{
    /// <inheritdoc />
    public bool IsAvailable => false;

    /// <inheritdoc />
    public Task<bool> TryGenerateAsync(Video video, CancellationToken cancellationToken)
    {
        return Task.FromResult(false);
    }
}
