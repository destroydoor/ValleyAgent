namespace ValleyAgent.Brain
{
    /// <summary>
    /// 任务5.1：对话响应来源标识，用于测试系统断言"LLM 真的被调用了"。
    /// </summary>
    public enum DialogueResponseSource
    {
        /// <summary>尚未产生任何对话响应。</summary>
        None,
        /// <summary>来自 TS Agent Server 的 LLM 响应。</summary>
        LLM,
        /// <summary>熔断器 OPEN 或本地关键词回退产生的响应。</summary>
        Fallback,
        /// <summary>异常路径产生的响应（如请求失败后的降级文本）。</summary>
        Error
    }
}
