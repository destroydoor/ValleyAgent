using System;
using System.Globalization;
using System.IO;

namespace ValleyAgent.Infrastructure;

/// <summary>
///     损坏数据文件隔离（issue #26 批③）：解析失败的 mod 自有数据文件改名为
///     <c>&lt;原名&gt;.corrupt-yyyyMMdd-HHmmss-fff</c>，让用户/开发者能取回原始字节，
///     且下次装载不再反复撞同一个坏文件；隔离失败（文件锁/权限）时保留原文件，
///     由调用方日志明确写出 "rename failed, file left in place"。
///     适用边界：仅 mod 自有文件（经济档案 / RAG bio / GameSummary / npc-configs）。
///     SMAPI 托管的存档数据（helper.Data.ReadSaveData）不是我们拥有的文件，不适用。
/// </summary>
internal static class CorruptFileQuarantine
{
    /// <summary>
    ///     尝试把坏文件改名隔离。返回隔离后的新路径；失败返回 null（原文件保留）。
    ///     本方法的 catch 是契约的一部分：失败结果由调用方日志承载，不在此处重复留痕。
    /// </summary>
    public static string? TryQuarantine(string path)
    {
        try
        {
            var stamped = path + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            File.Move(path, stamped);
            return stamped;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
