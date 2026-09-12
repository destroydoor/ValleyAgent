namespace ValleyAgent.Multiplayer;

/// <summary>
///     Mod 运行时模式。在 ModEntry.Entry 最早时机判定，决定后续初始化路径。
/// </summary>
public enum AgentRuntimeMode
{
    /// <summary>主机模式（含单机）：完整服务 + TS 服务器 + 全部事件/补丁 + 联机广播层。</summary>
    Host,

    /// <summary>薄客户端模式（联机 farmhand 且主机装有兼容版本本 mod）：仅渲染 + 消息收发 + 交互代理。</summary>
    ThinClient,

    /// <summary>惰性模式（联机 farmhand 但主机未装 mod/版本不兼容/分屏副屏）：完全惰性，原版体验。</summary>
    Inert
}