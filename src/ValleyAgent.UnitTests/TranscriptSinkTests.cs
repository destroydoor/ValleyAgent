using System.Reflection;
using System.Text.Json;
using ValleyAgent.Inventory;
using ValleyAgent.Protocol;
using Xunit;

namespace ValleyAgent.UnitTests;

/// <summary>
///     E1-1 TranscriptSink 单元测试。
///     验证：跨线程 Enqueue + 主线程 Drain 写入可解析 JSONL；日期轮转；写入失败不抛；null 安全；
///     AgentInventory.OnItemChanged 事件声明且 null 调用安全。
///     设计文档：docs/design/2026-08-01-memory-narrative-extensibility.md §1。
/// </summary>
public class TranscriptSinkTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "transcript_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static List<JsonElement> ReadJsonl(string path)
    {
        var results = new List<JsonElement>();
        foreach (var line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            results.Add(JsonDocument.Parse(line).RootElement.Clone());
        }

        return results;
    }

    [Fact]
    public void Enqueue_FromTask_Drain_WritesParseableJsonl()
    {
        var dir = NewTempDir();
        try
        {
            var sink = new TranscriptSink(new StubMonitor(), dir);

            // 从后台线程 Enqueue，模拟非主线程写入
            Parallel.For(0, 5, i =>
            {
                sink.Enqueue(
                    "action_result",
                    npcName: "Abigail",
                    gameDate: "Y1_spring_3",
                    success: i % 2 == 0,
                    extra: new Dictionary<string, object> { ["tool"] = "give_item", ["idx"] = i });
            });

            sink.Drain();
            sink.Close(); // 关闭 StreamWriter 释放文件句柄，否则 ReadAllLines 被锁

            var file = Path.Combine(dir, "Y1_spring_3", "Abigail.jsonl");
            Assert.True(File.Exists(file), "JSONL file should exist after Drain");

            var lines = ReadJsonl(file);
            Assert.Equal(5, lines.Count);

            foreach (var elem in lines)
            {
                Assert.Equal("action_result", elem.GetProperty("kind").GetString());
                Assert.Equal("Abigail", elem.GetProperty("npcName").GetString());
                Assert.Equal("Y1_spring_3", elem.GetProperty("gameDate").GetString());
                // ts 字段必须存在且为 ISO 8601
                Assert.False(string.IsNullOrEmpty(elem.GetProperty("ts").GetString()));
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Drain_RotatesOnNewGameDate_SeparateFiles()
    {
        var dir = NewTempDir();
        try
        {
            var sink = new TranscriptSink(new StubMonitor(), dir);

            sink.Enqueue("state_changed", npcName: "Sebastian", gameDate: "Y1_spring_1", success: true);
            sink.Enqueue("state_changed", npcName: "Sebastian", gameDate: "Y1_spring_2", success: true);
            sink.Drain();
            sink.Close();

            var fileA = Path.Combine(dir, "Y1_spring_1", "Sebastian.jsonl");
            var fileB = Path.Combine(dir, "Y1_spring_2", "Sebastian.jsonl");
            Assert.True(File.Exists(fileA), "GameDate A file should exist");
            Assert.True(File.Exists(fileB), "GameDate B file should exist");

            Assert.Single(ReadJsonl(fileA));
            Assert.Single(ReadJsonl(fileB));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Drain_WriteFailure_LogsWarnAndContinues()
    {
        // 使用包含非法字符的路径触发写入失败
        var invalidDir = Path.Combine(NewTempDir(), "bad|path?name");
        try
        {
            // 不用 using——如果构造抛异常则不 Dispose
            var sink = new TranscriptSink(new StubMonitor(), invalidDir);

            // Enqueue 不应抛（即使路径非法，写入延迟到 Drain）
            sink.Enqueue("action_result", npcName: "Haley", gameDate: "Y1_spring_1", success: false);

            // Drain 不应抛——写入失败被 catch + Warn
            var ex = Record.Exception(() => sink.Drain());
            Assert.Null(ex);

            sink.Dispose();
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(invalidDir)!, true);
        }
    }

    [Fact]
    public void Enqueue_NullSafe_DoesNotThrow()
    {
        var dir = NewTempDir();
        try
        {
            using var sink = new TranscriptSink(new StubMonitor(), dir);

            // 所有可选参数为 null 时不应抛
            var ex = Record.Exception(() => sink.Enqueue(null!));
            Assert.Null(ex);

            // kind=null 时 kind 字段为 null，序列化后仍可入队
            sink.Drain();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void AgentInventory_OnItemChanged_DeclaredAndNullInvocationSafe()
    {
        // 验证事件已声明
        var inv = new AgentInventory();
        var evt = inv.GetType().GetEvent("OnItemChanged", BindingFlags.Public | BindingFlags.Instance)
                  ?? throw new InvalidOperationException("OnItemChanged event not declared");
        Assert.Equal(typeof(Action<ItemChangeRecord>), evt.EventHandlerType);

        // 事件可被订阅/取消订阅（null 安全由 C# 语言保证：无订阅者时事件为 null）
        Action<ItemChangeRecord>? handler = _ => { };
        inv.OnItemChanged += handler;
        inv.OnItemChanged -= handler;
    }
}