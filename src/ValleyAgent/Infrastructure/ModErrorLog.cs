using System;
using System.IO;
using System.Text;

namespace ValleyAgent.Infrastructure;

/// <summary>
///     模组错误/服务器日志落盘：把运行时错误写入游戏 Mods 目录下的文件，
///     让玩家（无需翻 %APPDATA%\SMAPI\ErrorLogs）能直接把日志发回来排障。
///     两个文件：
///       - ValleyAgent-error.log  ：C# 侧未捕获异常 + 服务器进程崩溃/启动失败等关键错误
///       - ValleyAgent-server.log ：TS Agent Server 的完整控制台输出（LLM 调用/决策留痕）
///     所有写入必须吞掉自身异常（日志器绝不能把游戏搞崩），带大小上限轮转防无限膨胀。
/// </summary>
public static class ModErrorLog
{
    private const int MaxBytes = 5 * 1024 * 1024;

    private static readonly object s_lock = new();
    private static string? _errorLogPath;
    private static string? _serverLogPath;

    /// <summary>
    ///     初始化日志路径。必须在 Mod Entry 时调用一次；重复调用无副作用。
    /// </summary>
    /// <param name="modDir">mod 根目录（IModHelper.DirectoryPath，位于游戏 Mods 文件夹内）。</param>
    public static void Initialize(string modDir)
    {
        if (string.IsNullOrWhiteSpace(modDir))
        {
            return;
        }

        lock (s_lock)
        {
            _errorLogPath = Path.Combine(modDir, "ValleyAgent-error.log");
            _serverLogPath = Path.Combine(modDir, "ValleyAgent-server.log");
        }
    }

    /// <summary>写入一条关键错误（时间戳 + 来源 + 消息 + 异常堆栈）。</summary>
    /// <param name="source">错误来源标识（如 "Unhandled"/"ServerProcess"/"WebSocket"）。</param>
    /// <param name="message">错误描述。</param>
    /// <param name="exception">可选异常（附加堆栈）。</param>
    public static void LogError(string source, string message, Exception? exception = null)
    {
        var sb = new StringBuilder();
        sb.Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append("] [").Append(source)
            .Append("] ").AppendLine(message);
        if (exception != null)
        {
            sb.AppendLine(exception.ToString());
        }

        AppendFile(_errorLogPath, sb.ToString());
    }

    /// <summary>写入 TS Agent Server 控制台输出行（完整留痕，供事后排障）。</summary>
    /// <param name="line">服务器输出行（原样，不含时间戳——服务器日志自带时间戳）。</param>
    public static void LogServerLine(string line)
    {
        AppendFile(_serverLogPath, line + Environment.NewLine);
    }

    /// <summary>写入分隔事件（如服务器启动/停止标记）到服务器日志。</summary>
    /// <param name="marker">事件描述。</param>
    public static void LogServerEvent(string marker)
    {
        AppendFile(_serverLogPath,
            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] === {marker} ==={Environment.NewLine}");
    }

    /// <summary>追加写入并按上限轮转（超限时把当前文件改名 .old 后重新开始）。自身异常全部吞掉。</summary>
    private static void AppendFile(string? path, string content)
    {
        if (path == null)
        {
            return;
        }

        try
        {
            lock (s_lock)
            {
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                {
                    var old = path + ".old";
                    if (File.Exists(old))
                    {
                        File.Delete(old);
                    }

                    File.Move(path, old);
                }

                File.AppendAllText(path, content, Encoding.UTF8);
            }
        }
        catch
        {
            // 日志器自身故障（磁盘满/权限/文件被占用）必须静默——绝不能因写日志崩溃游戏
        }
    }
}
