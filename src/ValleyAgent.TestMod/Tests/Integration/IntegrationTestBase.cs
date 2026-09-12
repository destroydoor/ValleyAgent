#nullable enable
using System;
using System.Linq;
using System.Reflection;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.Agents;
using ValleyAgent.Infrastructure;
using ValleyAgent.Navigation;

namespace ValleyAgent.TestMod.Tests.Integration;

/// <summary>
///     集成测试基类：提供访问 ValleyAgent 内部服务（AgentNavigator/CommandExecutor 等）
///     的便捷 helper，以及反射操作 private 字段的工具方法。所有 IT0x 集成测试继承此类。
///     通过 InternalsVisibleTo("ValleyAgent.TestMod") 拿到 ModEntry.Container。
/// </summary>
public abstract class IntegrationTestBase : V3TestBase
{
    protected IntegrationTestBase(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    protected IValleyAgentApi? Api { get; private set; }
    protected IServiceContainer? Container { get; private set; }
    protected AgentNavigator? Navigator { get; private set; }

    protected string NpcName
    {
        get => ConfigNpcName;
    }

    /// <summary>通用 Setup：获取 API + Container + Navigator，warp 玩家到 Farm，分配 NPC。</summary>
    protected bool TryCommonSetup()
    {
        // 不用 TestMod 缓存的 ModEntry.API（GameLaunched 时机 ValleyAgent._container 未初始化，
        // ModRegistry.GetApi 返回 fallback ValleyAgentApi(null,null,null) 其 _agentApi=null，
        // 导致 TryAllocateAgent 等方法永远返回 false）。
        // 直接从 ValleyAgent.ModEntry.Instance.API 获取——此时 _container 已在 SaveLoaded 后初始化，
        // API 属性会从 Container 取 AgentService/AllocationManager 构造正确的 ValleyAgentApi。
        Api = ValleyAgent.ModEntry.Instance?.API;
        if (Api == null)
        {
            Skip("ValleyAgent.ModEntry.Instance.API is null (Host mode not initialized?)");
            return false;
        }

        Container = ValleyAgent.ModEntry.Instance?.Container;
        if (Container == null)
        {
            Skip("ValleyAgent.ModEntry.Instance.Container is null (Host mode not initialized?)");
            return false;
        }

        Navigator = Container.GetService<AgentNavigator>();

        // 清理存档中已加载的 agents：MaxAgentNpcs 默认为 1，若存档已占满则新分配会失败。
        // 测试间状态隔离：每个测试进入时先释放所有活跃 agent，确保 TryAllocateAgent 必定成功。
        // 注意：必须同时清理 AllocationManager（槽位持有者）和 AgentService（实例持有者），
        // 因为两者可能不同步——GetActiveAgentNames() 只反映 AgentService，不反映 AllocationManager。
        // 当 Api.TryDeallocateAgent 因 AgentService 内部异常失败时，直接操作 AllocationManager 强制释放槽位。
        var allocationManager = Container.GetService<AgentAllocationManager>();
        if (allocationManager != null)
        {
            foreach (var existingAgentName in allocationManager.AllocatedAgentNames.ToList())
            {
                if (!Api.TryDeallocateAgent(existingAgentName))
                {
                    // API 清理失败（AgentService.RemoveAgent 内部 CleanupAll/Reset 可能抛异常），
                    // 直接操作 AllocationManager 释放槽位，确保后续 TryAllocateAgent 有空位。
                    _ = allocationManager.Deallocate(existingAgentName);
                    Monitor.Log(
                        $"[{TestName}] Force-deallocated '{existingAgentName}' from AllocationManager (API cleanup failed)",
                        LogLevel.Warn);
                }
            }
        }

        // 兜底：清理 AgentService 中可能残留但 AllocationManager 已不持有的实例
        foreach (var existingAgentName in Api.GetActiveAgentNames())
        {
            if (!Api.TryDeallocateAgent(existingAgentName))
            {
                Monitor.Log(
                    $"[{TestName}] Failed to deallocate orphan agent '{existingAgentName}' during setup cleanup",
                    LogLevel.Warn);
            }
        }

        SafeWarp.Farmer(Monitor, "Farm", 54, 15);

        if (!Api.TryAllocateAgent(NpcName))
        {
            // 分配失败必然导致后续 TrySetAgentState/GetAgentState 返回 false/UNKNOWN，
            // 继续运行只会产生误导性 FAIL。直接 Skip 让结果可解释。
            Skip(
                $"TryAllocateAgent({NpcName}) returned false (MaxAgents={allocationManager?.MaxAgents}, Count={allocationManager?.CurrentAgentCount})");
            return false;
        }

        var npc = Game1.getCharacterFromName(NpcName);
        if (npc == null)
        {
            Skip($"{NpcName} not found in game world");
            return false;
        }

        npc.setTileLocation(new Vector2(54, 16));
        npc.Halt();
        npc.controller = null;

        // 降低对话冷却时间，避免测试中请求被拒绝
        Api.DialogueCooldownMs = 0;

        return true;
    }

    /// <summary>通用 Teardown：恢复对话冷却，把 NPC 移出玩家视野。</summary>
    protected void CommonTeardown()
    {
        if (Api != null)
        {
            Api.DialogueCooldownMs = 30000;
        }

        TestScenes.ClearAll(Helper, Monitor);
    }

    /// <summary>反射设置实例的 private/instance 字段。</summary>
    protected static bool TrySetField<T>(object instance, string fieldName, T value)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var field = instance.GetType().GetField(fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (field == null)
        {
            return false;
        }

        try
        {
            field.SetValue(instance, value);
            return true;
        }
        catch (TargetException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>反射设置静态类的 private static 字段。</summary>
    protected static bool TrySetStaticField<T>(Type type, string fieldName, T value)
    {
        ArgumentNullException.ThrowIfNull(type);
        var field = type.GetField(fieldName,
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        if (field == null)
        {
            return false;
        }

        try
        {
            field.SetValue(null, value);
            return true;
        }
        catch (TargetException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>反射读取实例的 private/instance 字段。</summary>
    protected static T? ReadField<T>(object instance, string fieldName) where T : class
    {
        ArgumentNullException.ThrowIfNull(instance);
        var field = instance.GetType().GetField(fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        return field?.GetValue(instance) as T;
    }

    /// <summary>反射调用实例的 private 方法。</summary>
    protected static object? InvokePrivate(object instance, string methodName, params object[] args)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var method = instance.GetType().GetMethod(methodName,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (method == null)
        {
            return null;
        }

        try
        {
            return method.Invoke(instance, args);
        }
        catch (TargetInvocationException ex)
        {
            throw ex.InnerException ?? ex;
        }
    }

    /// <summary>反射调用静态类的 private static 方法。</summary>
    protected static object? InvokeStaticPrivate(Type type, string methodName, params object[] args)
    {
        ArgumentNullException.ThrowIfNull(type);
        var method = type.GetMethod(methodName,
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        if (method == null)
        {
            return null;
        }

        try
        {
            return method.Invoke(null, args);
        }
        catch (TargetInvocationException ex)
        {
            throw ex.InnerException ?? ex;
        }
    }
}