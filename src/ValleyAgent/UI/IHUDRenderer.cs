using System.Collections.Generic;
using ValleyAgent.StateMachine;

namespace ValleyAgent.UI;

/// <summary>
///     Represents the layout and content of a single agent card on the HUD.
///     This is pure data 鈥?no rendering logic.
/// </summary>
public class AgentCardData
{
    /// <summary>Name of the NPC agent.</summary>
    public string NpcName { get; set; } = string.Empty;

    /// <summary>Current behaviour state (IDLE, FOLLOW, 鈥?.</summary>
    public AgentState State { get; set; } = AgentState.IDLE;

    /// <summary>True while the LLM is deciding / the agent is 鈥渢hinking鈥?</summary>
    public bool IsThinking { get; set; }

    /// <summary>
    ///     Number of friendship hearts to display (0鈥?0).
    ///     The caller is responsible for converting game-specific friendship points.
    /// </summary>
    public int FriendshipHearts { get; set; }

    /// <summary>Whether this allocation was forced by the player.</summary>
    public bool IsManuallyOverridden { get; set; }

    /// <summary>
    ///     Screen-space bounding box of this card (computed by AgentHUD).
    ///     The renderer uses this for hit-testing and drawing.
    /// </summary>
    public int ScreenX { get; set; }

    /// <summary>Screen Y coordinate of the card鈥檚 top-left corner.</summary>
    public int ScreenY { get; set; }

    /// <summary>Width of the card in screen pixels (already scaled).</summary>
    public int Width { get; set; }

    /// <summary>Height of the card in screen pixels (already scaled).</summary>
    public int Height { get; set; }
}

/// <summary>
///     Snapshot of everything the HUD wants drawn this frame.
///     Passed to <see cref="IHUDRenderer.Render" />.
/// </summary>
public class HUDRenderData
{
    /// <summary>False when the HUD has been hidden.</summary>
    public bool IsVisible { get; set; }

    /// <summary>Configured UI scale.</summary>
    public float Scale { get; set; } = 1.0f;

    /// <summary>Configured opacity (0鈥?).</summary>
    public float Opacity { get; set; } = 0.9f;

    /// <summary>Cards to draw, top-to-bottom.</summary>
    public IReadOnlyList<AgentCardData> AgentCards { get; set; } = new List<AgentCardData>();

    /// <summary>
    ///     Total token usage to display when <see cref="HUDConfig.ShowTokenUsage" /> is enabled.
    ///     Null when disabled or not set.
    /// </summary>
    public int? TotalTokenUsage { get; set; }

    /// <summary>
    ///     Pixel bounds of the whole HUD panel (union of all cards).
    ///     Useful for the integration layer to know whether the mouse is over the HUD.
    /// </summary>
    public int BoundsX { get; set; }

    public int BoundsY { get; set; }
    public int BoundsWidth { get; set; }
    public int BoundsHeight { get; set; }
}

/// <summary>
///     Game-agnostic renderer interface.
///     The SMAPI integration layer implements this using SpriteBatch.
/// </summary>
public interface IHUDRenderer
{
    /// <summary>
    ///     Render the HUD described by <paramref name="hudData" />.
    ///     Called once per frame while the HUD is visible and has data.
    /// </summary>
    /// <param name="hudData">Snapshot of current HUD state.</param>
    public void Render(HUDRenderData hudData);
}