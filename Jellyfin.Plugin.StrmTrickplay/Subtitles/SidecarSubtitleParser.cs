using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.StrmTrickplay.Subtitles;

/// <summary>
/// 外挂字幕文件名解析：把 `影片.chs.forced.ass` 这种名字拆成语言 / 标志 / 标题 / 编码。
/// 规则与 Jellyfin 核心一致（文件名必须以媒体主文件名为前缀，后面用点分隔）。
/// </summary>
public static class SidecarSubtitleParser
{
    /// <summary>
    /// 支持的文本 / 图形字幕扩展名 → 存储用的 codec 名。
    /// </summary>
    private static readonly Dictionary<string, (string Codec, bool IsText)> Codecs = new(StringComparer.OrdinalIgnoreCase)
    {
        [".ass"] = ("ass", true),
        [".ssa"] = ("ass", true),
        [".srt"] = ("subrip", true),
        [".vtt"] = ("webvtt", true),
        [".smi"] = ("smi", true),
        [".ttml"] = ("ttml", true),
        [".dfxp"] = ("ttml", true),
        [".sub"] = ("dvdsub", false),
        [".idx"] = ("dvdsub", false),
        [".sup"] = ("pgssub", false)
    };

    /// <summary>
    /// 语言别名（两字母 / 三字母 / 中文习惯写法）→ ISO 639-2 三字母代码。
    /// </summary>
    private static readonly Dictionary<string, string> Languages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zh"] = "chi", ["zho"] = "chi", ["chi"] = "chi", ["cmn"] = "chi", ["chs"] = "chi", ["cht"] = "chi",
        ["zh-cn"] = "chi", ["zh-tw"] = "chi", ["zh-hk"] = "chi", ["zh-hans"] = "chi", ["zh-hant"] = "chi",
        ["简体"] = "chi", ["简中"] = "chi", ["简"] = "chi", ["繁体"] = "chi", ["繁中"] = "chi", ["繁"] = "chi",
        ["中文"] = "chi", ["中字"] = "chi", ["国配"] = "chi",
        ["简体中文"] = "chi", ["繁体中文"] = "chi", ["双语"] = "chi", ["中英"] = "chi",
        ["简英"] = "chi", ["繁英"] = "chi", ["简日"] = "chi", ["简韩"] = "chi",
        ["en"] = "eng", ["eng"] = "eng", ["英文"] = "eng", ["英语"] = "eng",
        ["ja"] = "jpn", ["jp"] = "jpn", ["jpn"] = "jpn", ["日文"] = "jpn", ["日语"] = "jpn",
        ["ko"] = "kor", ["kr"] = "kor", ["kor"] = "kor", ["韩文"] = "kor", ["韩语"] = "kor",
        ["fr"] = "fre", ["fra"] = "fre", ["fre"] = "fre", ["法文"] = "fre",
        ["de"] = "ger", ["deu"] = "ger", ["ger"] = "ger", ["德文"] = "ger",
        ["es"] = "spa", ["spa"] = "spa", ["西文"] = "spa",
        ["ru"] = "rus", ["rus"] = "rus", ["俄文"] = "rus",
        ["pt"] = "por", ["por"] = "por", ["it"] = "ita", ["ita"] = "ita",
        ["th"] = "tha", ["tha"] = "tha", ["vi"] = "vie", ["vie"] = "vie",
        ["ar"] = "ara", ["ara"] = "ara", ["hi"] = "hin", ["hin"] = "hin",
        ["id"] = "ind", ["ind"] = "ind", ["ms"] = "may", ["may"] = "may",
        ["tr"] = "tur", ["tur"] = "tur", ["pl"] = "pol", ["pol"] = "pol",
        ["nl"] = "dut", ["dut"] = "dut", ["swe"] = "swe", ["sv"] = "swe",
        ["da"] = "dan", ["dan"] = "dan", ["fi"] = "fin", ["fin"] = "fin",
        ["no"] = "nor", ["nor"] = "nor", ["cs"] = "cze", ["cze"] = "cze",
        ["hu"] = "hun", ["hun"] = "hun", ["ro"] = "rum", ["rum"] = "rum",
        ["uk"] = "ukr", ["ukr"] = "ukr", ["el"] = "gre", ["gre"] = "gre",
        ["he"] = "heb", ["heb"] = "heb", ["fa"] = "per", ["per"] = "per"
    };

    /// <summary>
    /// 特殊标志词。
    /// </summary>
    private static readonly Dictionary<string, string> Flags = new(StringComparer.OrdinalIgnoreCase)
    {
        ["forced"] = "forced",
        ["default"] = "default",
        ["sdh"] = "sdh",
        ["cc"] = "sdh",
        ["hi"] = "sdh",
        ["hearing"] = "sdh",
        ["foreign"] = "forced",
        ["signs"] = "forced",
        ["songs"] = "forced"
    };

    /// <summary>
    /// 是否是本插件会处理的外挂字幕扩展名。
    /// </summary>
    /// <param name="path">File path.</param>
    /// <returns>True when supported.</returns>
    public static bool IsSupported(string path)
    {
        return Codecs.ContainsKey(Path.GetExtension(path));
    }

    /// <summary>
    /// 文件名是否属于给定的媒体主文件名。
    /// </summary>
    /// <param name="fileNameWithoutExtension">候选文件的主文件名（不含扩展名）。</param>
    /// <param name="mediaStem">媒体主文件名（如 `流浪地球2`）。</param>
    /// <returns>True when the file belongs to the media.</returns>
    public static bool MatchesMedia(string fileNameWithoutExtension, string mediaStem)
    {
        if (string.IsNullOrEmpty(fileNameWithoutExtension) || string.IsNullOrEmpty(mediaStem))
        {
            return false;
        }

        if (!fileNameWithoutExtension.StartsWith(mediaStem, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (fileNameWithoutExtension.Length == mediaStem.Length)
        {
            return true;
        }

        // 后面必须紧跟分隔符，避免 `影片2.ass` 被当成 `影片` 的字幕
        var next = fileNameWithoutExtension[mediaStem.Length];
        return next == '.' || next == '_' || next == '-' || next == ' ';
    }

    /// <summary>
    /// 把一个外挂字幕文件解析成 <see cref="MediaStream"/>（不含 Index）。
    /// </summary>
    /// <param name="path">字幕文件完整路径。</param>
    /// <param name="mediaStem">媒体主文件名。</param>
    /// <returns>解析结果；扩展名不支持时返回 null。</returns>
    public static MediaStream? Parse(string path, string mediaStem)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var extension = Path.GetExtension(path);
        if (!Codecs.TryGetValue(extension, out var codec))
        {
            return null;
        }

        var fileName = Path.GetFileNameWithoutExtension(path);
        var remainder = fileName.Length > mediaStem.Length && mediaStem.Length > 0
            ? fileName.Substring(mediaStem.Length)
            : string.Empty;

        var language = string.Empty;
        var titleParts = new List<string>();
        var isForced = false;
        var isDefault = false;
        var isSdh = false;

        foreach (var raw in remainder.Split(new[] { '.', '_', '-', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim();
            if (token.Length == 0)
            {
                continue;
            }

            if (Flags.TryGetValue(token, out var flag))
            {
                switch (flag)
                {
                    case "forced":
                        isForced = true;
                        break;
                    case "default":
                        isDefault = true;
                        break;
                    case "sdh":
                        isSdh = true;
                        break;
                }

                continue;
            }

            if (language.Length == 0 && Languages.TryGetValue(token, out var code))
            {
                language = code;
                continue;
            }

            // `chs&eng`、`srt+ass` 这类组合：只要含有已知语言就取第一个
            if (language.Length == 0)
            {
                var parts = token.Split(new[] { '&', '+', ',', '/', '|' }, StringSplitOptions.RemoveEmptyEntries);
                var matched = parts.Select(p => Languages.TryGetValue(p.Trim(), out var c) ? c : null)
                    .FirstOrDefault(c => c is not null);
                if (matched is not null)
                {
                    language = matched;
                    continue;
                }
            }

            titleParts.Add(token);
        }

        var title = titleParts.Count > 0 ? string.Join(" ", titleParts) : string.Empty;

        return new MediaStream
        {
            Type = MediaStreamType.Subtitle,
            Codec = codec.Codec,
            Language = language,
            Title = title,
            IsExternal = true,
            IsDefault = isDefault,
            IsForced = isForced,
            IsHearingImpaired = isSdh,
            Path = path,
            SupportsExternalStream = true
        };
    }

    /// <summary>
    /// 生成客户端显示用的默认标题（语言名 + 标志）。
    /// </summary>
    /// <param name="stream">Subtitle stream.</param>
    /// <returns>Display title.</returns>
    public static string BuildTitle(MediaStream stream)
    {
        var flags = new List<string>();
        if (stream.IsForced)
        {
            flags.Add("Forced");
        }

        if (stream.IsHearingImpaired)
        {
            flags.Add("SDH");
        }

        var language = string.IsNullOrEmpty(stream.Language) ? string.Empty : ToDisplayLanguage(stream.Language);
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(language))
        {
            parts.Add(language);
        }

        if (!string.IsNullOrEmpty(stream.Title))
        {
            parts.Add(stream.Title);
        }

        if (flags.Count > 0)
        {
            parts.Add(string.Join(" ", flags));
        }

        return parts.Count > 0 ? string.Join(" · ", parts) : Path.GetFileName(stream.Path ?? string.Empty);
    }

    private static string ToDisplayLanguage(string code)
    {
        return code.ToUpperInvariant() switch
        {
            "CHI" => "中文",
            "ENG" => "English",
            "JPN" => "日本語",
            "KOR" => "한국어",
            _ => TryCulture(code) ?? code.ToUpperInvariant()
        };
    }

    private static string? TryCulture(string code)
    {
        try
        {
            var culture = CultureInfo.GetCultures(CultureTypes.NeutralCultures)
                .FirstOrDefault(c => string.Equals(c.ThreeLetterISOLanguageName, code, StringComparison.OrdinalIgnoreCase));
            return culture?.DisplayName;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
