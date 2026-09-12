#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;

namespace ValleyAgent.TestMod.Infrastructure;

/// <summary>
///     标记一个测试类可被 TestRegistry 自动发现和注册。
///     支持分组、标签、描述等元数据，供 AI 和控制台命令查询。
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RegisteredTestAttribute : Attribute
{
    /// <summary>
    ///     标记测试类。
    /// </summary>
    /// <param name="group">测试分组（可组合多个标志）</param>
    /// <param name="description">测试描述，供 AI 和人类阅读</param>
    /// <param name="tags">额外标签，用于细粒度筛选（如 "combat", "pathfinding", "slow"）</param>
    public RegisteredTestAttribute(TestGroup group, string description = "", params string[] tags)
    {
        Group = group;
        Description = description;
        Tags = tags ?? Array.Empty<string>();
    }

    public TestGroup Group { get; }
    public string[] Tags { get; }
    public string Description { get; }
}

/// <summary>
///     测试注册信息，包含测试的元数据和工厂方法。
///     支持两种工厂签名：2参数（标准）和3参数（带 ScreenshotCapture）。
/// </summary>
public sealed class TestDescriptor
{
    public TestDescriptor(string name, TestGroup group, string description,
        IReadOnlyList<string> tags, Func<IModHelper, IMonitor, V3TestBase> factory)
    {
        Name = name;
        Group = group;
        Description = description;
        Tags = tags;
        Factory = factory;
        RequiresScreenshotCapture = false;
    }

    public TestDescriptor(string name, TestGroup group, string description,
        IReadOnlyList<string> tags, Func<IModHelper, IMonitor, ScreenshotCapture, V3TestBase> factory)
    {
        Name = name;
        Group = group;
        Description = description;
        Tags = tags;
        FactoryWithCapture = factory;
        RequiresScreenshotCapture = true;
    }

    public string Name { get; }
    public TestGroup Group { get; }
    public string Description { get; }
    public IReadOnlyList<string> Tags { get; }
    public bool RequiresScreenshotCapture { get; }

    /// <summary>2参数工厂（IModHelper, IMonitor）。</summary>
    public Func<IModHelper, IMonitor, V3TestBase>? Factory { get; }

    /// <summary>3参数工厂（IModHelper, IMonitor, ScreenshotCapture）。</summary>
    public Func<IModHelper, IMonitor, ScreenshotCapture, V3TestBase>? FactoryWithCapture { get; }

    /// <summary>创建测试实例。如果需要 ScreenshotCapture，必须传入 capture 参数。</summary>
    public V3TestBase CreateInstance(IModHelper helper, IMonitor monitor, ScreenshotCapture? capture = null)
    {
        if (RequiresScreenshotCapture)
        {
            ArgumentNullException.ThrowIfNull(capture);
            return FactoryWithCapture!(helper, monitor, capture);
        }

        return Factory!(helper, monitor);
    }
}

/// <summary>
///     测试注册中心：自动发现、注册、查询测试。
///     替代 V3TestRunner.InitializeTests() 中的硬编码注册。
///     支持按分组/标签/名称查询，供 AI 和控制台命令使用。
/// </summary>
public static class TestRegistry
{
    private static readonly List<TestDescriptor> s_descriptors = new();
    private static bool s_scanned;

    /// <summary>获取所有已注册的测试描述符。</summary>
    public static IReadOnlyList<TestDescriptor> All
    {
        get => s_descriptors;
    }

    /// <summary>手动注册一个测试（用于无法使用 Attribute 的场景）。</summary>
    public static void Register(TestDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (s_descriptors.Any(d => d.Name == descriptor.Name))
        {
            return; // 去重
        }

        s_descriptors.Add(descriptor);
    }

    /// <summary>按分组筛选测试（支持位标志组合）。</summary>
    public static IEnumerable<TestDescriptor> ByGroup(TestGroup group)
    {
        EnsureScanned();
        return s_descriptors.Where(d => (d.Group & group) != 0);
    }

    /// <summary>按标签筛选测试。</summary>
    public static IEnumerable<TestDescriptor> ByTag(string tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        EnsureScanned();
        return s_descriptors.Where(d =>
            d.Tags.Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>按名称精确匹配测试。</summary>
    public static TestDescriptor? ByName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        EnsureScanned();
        return s_descriptors.FirstOrDefault(d =>
            d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>按名称模糊搜索测试。</summary>
    public static IEnumerable<TestDescriptor> Search(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        EnsureScanned();
        return s_descriptors.Where(d =>
            d.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            d.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            d.Tags.Any(t => t.Contains(query, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>获取所有可用分组及其测试数量。</summary>
    public static Dictionary<string, int> GetGroupSummary()
    {
        EnsureScanned();
        var result = new Dictionary<string, int>();
        foreach (var g in Enum.GetValues<TestGroup>())
        {
            if (g == TestGroup.All || g == 0)
            {
                continue;
            }

            var count = s_descriptors.Count(d => (d.Group & g) != 0);
            if (count > 0)
            {
                result[g.ToString()] = count;
            }
        }

        return result;
    }

    /// <summary>获取所有可用标签。</summary>
    public static HashSet<string> GetAllTags()
    {
        EnsureScanned();
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in s_descriptors)
        {
            foreach (var t in d.Tags)
            {
                _ = tags.Add(t);
            }
        }

        return tags;
    }

    /// <summary>确保已扫描注册。调用多次是安全的（幂等）。</summary>
    private static void EnsureScanned()
    {
        if (s_scanned)
        {
            return;
        }

        s_scanned = true;
        // 反射扫描在 ScanAssembly 中完成
    }

    /// <summary>
    ///     通过反射扫描程序集中所有带 [RegisteredTest] 的类，自动注册。
    ///     由 TestOrchestrator 在初始化时调用一次。
    /// </summary>
    public static void ScanAssembly(IModHelper helper, IMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(helper);
        ArgumentNullException.ThrowIfNull(monitor);

        if (s_scanned)
        {
            return;
        }

        s_scanned = true;

        var asm = typeof(TestRegistry).Assembly;
        var baseType = typeof(V3TestBase);

        foreach (var type in asm.GetTypes())
        {
            if (!baseType.IsAssignableFrom(type) || type.IsAbstract)
            {
                continue;
            }

            var attr = (RegisteredTestAttribute?)Attribute.GetCustomAttribute(type, typeof(RegisteredTestAttribute));
            if (attr == null)
            {
                continue;
            }

            // 查找构造函数：优先3参数 (IModHelper, IMonitor, ScreenshotCapture)，其次2参数
            var ctor3 = type.GetConstructor(new[] { typeof(IModHelper), typeof(IMonitor), typeof(ScreenshotCapture) });
            var ctor2 = type.GetConstructor(new[] { typeof(IModHelper), typeof(IMonitor) });

            if (ctor3 == null && ctor2 == null)
            {
                monitor.Log(
                    $"[TestRegistry] 跳过 {type.Name}: 缺少 (IModHelper, IMonitor) 或 (IModHelper, IMonitor, ScreenshotCapture) 构造函数",
                    LogLevel.Warn);
                continue;
            }

            var name = type.Name;
            TestDescriptor descriptor;
            if (ctor3 != null)
            {
                descriptor = new TestDescriptor(
                    name,
                    attr.Group,
                    attr.Description,
                    attr.Tags,
                    (Func<IModHelper, IMonitor, ScreenshotCapture, V3TestBase>)((h, m, c) =>
                        (V3TestBase)ctor3.Invoke(new object[] { h, m, c }))
                );
            }
            else
            {
                descriptor = new TestDescriptor(
                    name,
                    attr.Group,
                    attr.Description,
                    attr.Tags,
                    (Func<IModHelper, IMonitor, V3TestBase>)((h, m) =>
                        (V3TestBase)ctor2!.Invoke(new object[] { h, m }))
                );
            }

            Register(descriptor);
            monitor.Log($"[TestRegistry] 注册测试: {name} [{attr.Group}] {attr.Description}",
                LogLevel.Debug);
        }

        monitor.Log($"[TestRegistry] 扫描完成，共注册 {s_descriptors.Count} 个测试", LogLevel.Info);
    }

    /// <summary>重置注册表（仅用于测试）。</summary>
    internal static void Reset()
    {
        s_descriptors.Clear();
        s_scanned = false;
    }
}