using System.IO;
using System.Text;

namespace PackagingApp.Services;

/// <summary>
/// 把当前状态写成三行固定顺序的 CSV：赛事名 / 项目名 / 切台状态。
///
/// 这不是给人看的表格，是给现场的视频切换台（芯象）读的：解说端出问题时，
/// 包装人员改用芯象自带的包装顶上，而它只能从外部文件拿「现在播的是什么」。
/// 那边按**行号**映射字段，所以格式上的约束全写在这个类里——改格式等于
/// 让现场所有标题一起错位，比改任何一行代码都要危险。
/// </summary>
public sealed class CsvWriter : IDisposable
{
    /// <summary>
    /// 行分隔符。
    ///
    /// 只用 LF 而不是 Windows 惯用的 CRLF：换成「按 '\n' 切分」的读取器
    /// （很多自制的现场工具就是这么写的）时，每个值就不会多带一个看不见的
    /// '\r' 尾巴。标题上多一个字符比少一个字更难查——它不报错，只是不对。
    /// 而几乎没有哪个读取器会**要求** CRLF，所以这是安全的那一侧。
    /// 空字段因此正好是一个长度为 0 的行。
    /// </summary>
    private const string LineBreak = "\n";

    /// <summary>
    /// 无 BOM 的 UTF-8。BOM 会被当成标题最前面的一个字符显示出来，
    /// 而这类文件基本都是按字节读的，加不加 BOM 差别很大。
    /// </summary>
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    /// <summary>覆盖移动的重试次数。</summary>
    private const int MoveAttempts = 3;

    /// <summary>
    /// 串行化写入。
    ///
    /// 写入有两个来源：WebSocket 的接收线程（切台时触发）与界面线程（连上时
    /// 补写一次），两者可能撞在一起。临时文件名是固定的，并发写会互相覆盖，
    /// 现场看到的就是「标题偶尔错一次」，极难复现。
    /// </summary>
    private readonly object _gate = new();

    private string _path;

    /// <summary>
    /// <paramref name="path"/> 为空时视为未配置，写入一律失败并返回原因。
    /// </summary>
    public CsvWriter(string path)
    {
        _path = NormalizePath(path);
    }

    /// <summary>CSV 的绝对路径。未配置（或已释放）时是空串。</summary>
    public string Path => _path;

    /// <summary>
    /// 写一次。成功返回 null；失败返回一句能直接显示给用户的中文原因。
    /// </summary>
    public string? Write(string eventName, string programName, string shotState)
    {
        lock (_gate)
        {
            if (_path.Length == 0) return "未配置 CSV 输出路径（config.json 的 CsvPath）";

            var tmp = _path + ".tmp";
            try
            {
                // 每次现取目录，不缓存 FileInfo 之类的对象：那个文件随时可能被
                // 换掉（人手工改过、被同步盘重写），拿着旧对象写就会写到
                // 一个已经不是路径的东西上。
                var dir = System.IO.Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var content = string.Join(LineBreak, new[] { Clean(eventName), Clean(programName), Clean(shotState) });

                // 「写临时文件 → 关闭 → 移动」而不是直接开 xxx.csv 写。
                // 现场那个视频切换台要能随时单独打开这个 CSV；包装端只要一直占着
                // 句柄（哪怕是 FileShare.Read 之外的任何模式），芯象就会读不到。
                // 移动那一步天然是原子的，读者要么看到旧的完整三行，要么看到新的
                // 完整三行，不会读到写了一半的文件。
                File.WriteAllText(tmp, content, Utf8NoBom);
                MoveIntoPlace(tmp);
                return null;
            }
            catch (Exception ex)
            {
                // 失败时把半截的临时文件清掉：它留着不影响功能，但下次写成功之前
                // 现场会在目录里看到两个文件，以为是写坏了。
                try { File.Delete(tmp); } catch { }
                return $"写入 CSV 失败：{ex.Message}";
            }
        }
    }

    /// <summary>
    /// 把临时文件移到最终位置。
    ///
    /// 覆盖移动在 Windows 上要求目标没有被独占打开。芯象可能正好读着这个文件，
    /// 撞上就是一次 IOException，于是重试几次——这种冲突通常只持续几毫秒。
    /// 不重试的话，标题会一直停在旧值，而日志里只有一句看不出所以然的 IO 错误。
    /// </summary>
    private void MoveIntoPlace(string tmp)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(tmp, _path, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < MoveAttempts)
            {
                Thread.Sleep(150);
            }
        }
    }

    /// <summary>
    /// 去掉值里的换行与首尾空白。
    ///
    /// 换行是**压平**而不是简单 trim：这个文件永远只有三行，一旦某个值带了
    /// 换行（比如项目名里手滑按了回车），芯象按行号映射就会整体错位，
    /// 后面两个字段全部读到别的东西——而且现场只会看到「标题莫名其妙」。
    ///
    /// CRLF 要当成**一个**换行：先把它整体换掉，否则 \r 和 \n 各出一个空格，
    /// 标题里就凭空多出两格空白。
    /// </summary>
    private static string Clean(string? value)
    {
        return (value ?? "")
            .Replace("\r\n", " ")
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Trim();
    }

    /// <summary>
    /// 存绝对路径：界面要把它显示出来让人确认写到了哪里，而相对路径的基准
    /// 是进程的工作目录，从资源管理器双击启动和从命令行启动时它并不一样。
    /// </summary>
    private static string NormalizePath(string? path)
    {
        var trimmed = (path ?? "").Trim();
        if (trimmed.Length == 0) return "";

        try
        {
            return System.IO.Path.GetFullPath(trimmed);
        }
        catch
        {
            // 非法路径不是启动期该崩的事，交给 Write 去把原因显示出来。
            return trimmed;
        }
    }

    /// <summary>
    /// 只把路径清空。
    ///
    /// 这里没有常驻资源可释放：每次写入都是「写临时文件 → 立刻关闭 → 移动」，
    /// 句柄在 Write 返回之前就已经不在了——**这正是这个类存在的意义**，
    /// 所以这里绝不能改成常驻 FileStream。
    ///
    /// 留一个 Dispose 只是为了让调用方能用统一的 using 写法，将来万一真的
    /// 引入了需要释放的东西，不用改调用点。顺带清掉路径：释放之后再调 Write
    /// 得到的是「未配置」，而不是继续往一个已经交出去的目录里写。
    /// </summary>
    public void Dispose()
    {
        lock (_gate) _path = "";
    }
}