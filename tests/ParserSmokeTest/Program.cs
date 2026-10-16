using Jellyfin.Plugin.StrmTrickplay.Configuration;
using Jellyfin.Plugin.StrmTrickplay.Trickplay;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Drawing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

var root = Path.Combine(Path.GetTempPath(), "strm-trickplay-smoketest-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(root);
Console.WriteLine("测试根目录: " + root);

var failures = new List<string>();

// 1) 标准布局：Sample.trickplay/320 - 10x10/{0,1,2}.jpg
WriteFiles(Path.Combine(root, "Standard", "Sample.trickplay", "320 - 10x10"), "0.jpg", "1.jpg", "2.jpg");
WriteFiles(Path.Combine(root, "Standard"), "Sample.strm");

// 2) 只有宽度：Sample.trickplay/480/...
WriteFiles(Path.Combine(root, "WidthOnly", "Sample.trickplay", "480"), "0.jpg", "1.jpg");
WriteFiles(Path.Combine(root, "WidthOnly"), "Sample.strm");

// 3) 瓦片直接躺在侧车根目录
WriteFiles(Path.Combine(root, "Flat", "Sample.trickplay"), "0.jpg", "1.jpg", "2.jpg", "3.jpg");
WriteFiles(Path.Combine(root, "Flat"), "Sample.strm");

// 4) 非顺序命名的瓦片（验证按数字排序）
WriteFiles(Path.Combine(root, "Unsorted", "Sample.trickplay", "320 - 10x10"), "10.jpg", "2.jpg", "1.jpg");
WriteFiles(Path.Combine(root, "Unsorted"), "Sample.strm");

// 5) 没有瓦片
WriteFiles(Path.Combine(root, "Missing"), "Sample.strm");

// 6) 名为 trickplay 的兜底目录
WriteFiles(Path.Combine(root, "Fallback", "trickplay", "320 - 10x10"), "0.jpg");
WriteFiles(Path.Combine(root, "Fallback"), "Sample.strm");

var encoder = new Mock<IImageEncoder>();
encoder.Setup(e => e.GetImageSize(It.IsAny<string>())).Returns(new ImageDimensions(320, 180));

var serverConfig = new ServerConfiguration { TrickplayOptions = new TrickplayOptions { Interval = 10000 } };
var configManager = new Mock<MediaBrowser.Controller.Configuration.IServerConfigurationManager>();
configManager.SetupGet(c => c.Configuration).Returns(serverConfig);

var logger = NullLogger<SidecarTrickplayParser>.Instance;
var parser = new SidecarTrickplayParser(encoder.Object, configManager.Object, logger);
var config = new PluginConfiguration();

void Check(string label, bool condition, string detail)
{
    Console.WriteLine((condition ? "  [PASS] " : "  [FAIL] ") + label + " -> " + detail);
    if (!condition)
    {
        failures.Add(label + " -> " + detail);
    }
}

SidecarTileSet Single(string sub, long? ticks, string media = "Sample")
{
    var dir = Path.Combine(root, sub);
    var video = new Movie
    {
        Id = Guid.NewGuid(),
        Path = Path.Combine(dir, media + ".strm"),
        RunTimeTicks = ticks,
        IsShortcut = true
    };

    var sets = parser.Parse(video, config);
    if (sets.Count != 1)
    {
        throw new InvalidOperationException(sub + " 期望 1 套瓦片集，实际 " + sets.Count);
    }

    return sets[0];
}

Console.WriteLine("\n=== 用例 1：标准布局 (320 - 10x10, 3 张瓦片, 时长 10 分钟) ===");
var s1 = Single("Standard", TimeSpan.FromMinutes(10).Ticks);
Console.WriteLine("  " + s1);
Check("width", s1.Width == 320, s1.Width.ToString());
Check("tileGrid", s1.TileWidth == 10 && s1.TileHeight == 10, s1.TileWidth + "x" + s1.TileHeight);
Check("tileCount", s1.Files.Count == 3, s1.Files.Count.ToString());
Check("height(180/10)", s1.Height == 18, s1.Height.ToString());
Check("interval", s1.Interval == 2000, s1.Interval.ToString());
Check("thumbnailCount", s1.ThumbnailCount == 300, s1.ThumbnailCount.ToString());
Check("bandwidth>0", s1.Bandwidth > 0, s1.Bandwidth.ToString());

Console.WriteLine("\n=== 用例 2：目录名只有宽度 (480) ===");
var s2 = Single("WidthOnly", TimeSpan.FromMinutes(10).Ticks);
Console.WriteLine("  " + s2);
Check("width", s2.Width == 480, s2.Width.ToString());
Check("defaultGrid", s2.TileWidth == 10 && s2.TileHeight == 10, s2.TileWidth + "x" + s2.TileHeight);
Check("tileCount", s2.Files.Count == 2, s2.Files.Count.ToString());

Console.WriteLine("\n=== 用例 3：瓦片直接位于 <name>.trickplay 根目录 ===");
var s3 = Single("Flat", TimeSpan.FromMinutes(10).Ticks);
Console.WriteLine("  " + s3);
Check("defaultWidth", s3.Width == 320, s3.Width.ToString());
Check("tileCount", s3.Files.Count == 4, s3.Files.Count.ToString());

Console.WriteLine("\n=== 用例 4：瓦片文件名为 10.jpg / 2.jpg / 1.jpg（数字排序） ===");
var s4 = Single("Unsorted", TimeSpan.FromMinutes(10).Ticks);
var order = string.Join(",", s4.Files.Select(Path.GetFileName));
Console.WriteLine("  顺序: " + order);
Check("numericOrder", order == "1.jpg,2.jpg,10.jpg", order);
Check("GetTilePath(1)", Path.GetFileName(s4.GetTilePath(1) ?? "") == "2.jpg", s4.GetTilePath(1) ?? "null");
Check("GetTilePath(9)=null", s4.GetTilePath(9) is null, "ok");

Console.WriteLine("\n=== 用例 5：没有 trickplay 目录 ===");
var missing = parser.Parse(new Movie { Id = Guid.NewGuid(), Path = Path.Combine(root, "Missing", "Sample.strm"), RunTimeTicks = TimeSpan.FromMinutes(10).Ticks, IsShortcut = true }, config);
Check("empty", missing.Count == 0, missing.Count.ToString());

Console.WriteLine("\n=== 用例 6：兜底目录名 trickplay ===");
var s6 = Single("Fallback", TimeSpan.FromMinutes(10).Ticks);
Console.WriteLine("  " + s6);
Check("width", s6.Width == 320, s6.Width.ToString());

Console.WriteLine("\n=== 用例 7：无时长信息（RunTimeTicks=null） ===");
var s7 = Single("Standard", null);
Console.WriteLine("  " + s7);
Check("interval=configured", s7.Interval == 10000, s7.Interval.ToString());

Console.WriteLine("\n=== 用例 8：强制 interval 覆盖 (IntervalOverrideMs=3000) ===");
var cfg2 = new PluginConfiguration { IntervalOverrideMs = 3000 };
var video8 = new Movie { Id = Guid.NewGuid(), Path = Path.Combine(root, "Standard", "Sample.strm"), RunTimeTicks = TimeSpan.FromMinutes(10).Ticks, IsShortcut = true };
var s8 = parser.Parse(video8, cfg2).Single();
Console.WriteLine("  " + s8);
Check("interval=3000", s8.Interval == 3000, s8.Interval.ToString());
Check("countFromOverride", s8.ThumbnailCount == 201, s8.ThumbnailCount.ToString());

Console.WriteLine("\n=== 用例 9：宽高比超过 1 张瓦片（runtime 很长） ===");
var s9 = Single("Standard", TimeSpan.FromHours(2).Ticks);
Console.WriteLine("  " + s9);
Check("intervalInRange", s9.Interval >= 1000, s9.Interval.ToString());
Check("countClamped", s9.ThumbnailCount >= 201 && s9.ThumbnailCount <= 300, s9.ThumbnailCount.ToString());

Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine("全部用例通过 ✅");
}
else
{
    Console.WriteLine("失败用例 " + failures.Count + " 个 ❌");
    foreach (var f in failures)
    {
        Console.WriteLine("  - " + f);
    }
}

try
{
    Directory.Delete(root, true);
}
catch
{
    // ignore
}

return failures.Count == 0 ? 0 : 1;

static void WriteFiles(string dir, params string[] names)
{
    Directory.CreateDirectory(dir);
    foreach (var name in names)
    {
        File.WriteAllBytes(Path.Combine(dir, name), [0xFF, 0xD8, 0xFF, 0xD9]);
    }
}
