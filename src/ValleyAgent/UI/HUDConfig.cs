namespace ValleyAgent.UI;

/// <summary>
///     Configuration for the Agent HUD display.
/// </summary>
public class HUDConfig
{
    /// <summary>
    ///     Horizontal screen position (top-left corner of the HUD).
    /// </summary>
    public int PositionX { get; set; } = 16;

    /// <summary>
    ///     Vertical screen position (top-left corner of the HUD).
    /// </summary>
    public int PositionY { get; set; } = 16;

    /// <summary>
    ///     UI scale multiplier. 1.0 = default size.
    /// </summary>
    public float Scale { get; set; } = 1.0f;

    /// <summary>
    ///     Opacity from 0.0 (fully transparent) to 1.0 (fully opaque).
    /// </summary>
    public float Opacity { get; set; } = 0.9f;

    /// <summary>
    ///     Whether to display agent names on the HUD cards.
    /// </summary>
    public bool ShowAgentNames { get; set; } = true;

    /// <summary>
    ///     Whether to display the current state label (IDLE, FOLLOW, etc.).
    /// </summary>
    public bool ShowAgentStates { get; set; } = true;

    /// <summary>
    ///     Whether to show the thinking spinner when an agent is processing an LLM decision.
    /// </summary>
    public bool ShowThinkingIndicator { get; set; } = true;

    /// <summary>
    ///     Whether to display total token usage (debug only).
    /// </summary>
    public bool ShowTokenUsage { get; set; }

    /// <summary>
    ///     Whether to display friendship hearts for each agent.
    /// </summary>
    public bool ShowFriendshipHearts { get; set; } = true;
}