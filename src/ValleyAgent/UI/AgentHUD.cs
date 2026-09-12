using System;
using System.Collections.Generic;
using System.Linq;
using ValleyAgent.Agents;
using ValleyAgent.StateMachine;

namespace ValleyAgent.UI;

/// <summary>
///     Event args for the <see cref="AgentHUD.OnHUDClicked" /> event.
/// </summary>
public class HUDClickEventArgs : EventArgs
{
    /// <summary>Name of the agent card that was clicked, or null if the click hit the panel background.</summary>
    public string? AgentName { get; set; }

    /// <summary>Screen X coordinate of the click.</summary>
    public int ScreenX { get; set; }

    /// <summary>Screen Y coordinate of the click.</summary>
    public int ScreenY { get; set; }
}

/// <summary>
///     Game-agnostic Agent HUD display system.
///     Responsibilities:
///     - Maintains a cached snapshot of agent data suitable for rendering.
///     - Computes card layouts (position, size) based on <see cref="HUDConfig" />.
///     - Exposes render data via <see cref="GetRenderData" /> for an <see cref="IHUDRenderer" />.
///     - Handles click hit-testing in a game-agnostic way (screen coords 鈫?card).
///     Design constraints:
///     - No SMAPI types in core logic.
///     - Actual SpriteBatch rendering lives in the integration layer.
///     - Does not render if hidden or no agents are active.
///     - Never blocks on render.
/// </summary>
public class AgentHUD
{
    // Layout constants (unscaled 鈥?the renderer or GetRenderData applies Scale)
    private const int BaseCardWidth = 220;
    private const int BaseCardHeight = 56;
    private const int BaseCardSpacing = 8;
    private const int BaseTokenBarHeight = 20;
    private readonly AgentAllocationManager _allocationManager;

    private readonly HUDConfig _config;

    private readonly object _lock = new();
    private readonly Dictionary<string, bool> _thinkingStates = new(StringComparer.OrdinalIgnoreCase);
    private List<AgentCardData> _cachedCards = new();
    private bool _isVisible = true;
    private int? _tokenUsage;

    /// <summary>
    ///     Creates a new AgentHUD.
    /// </summary>
    /// <param name="config">HUD layout and visibility settings.</param>
    /// <param name="allocationManager">Source of truth for allocated agents and their states.</param>
    public AgentHUD(HUDConfig config, AgentAllocationManager allocationManager)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _allocationManager = allocationManager ?? throw new ArgumentNullException(nameof(allocationManager));
    }

    /// <summary>
    ///     Whether the HUD is currently visible.
    /// </summary>
    public bool IsVisible
    {
        get
        {
            lock (_lock)
            {
                return _isVisible;
            }
        }
    }

    /// <summary>
    ///     Number of agent cards currently cached.
    /// </summary>
    public int ActiveAgentCount
    {
        get
        {
            lock (_lock)
            {
                return _cachedCards.Count;
            }
        }
    }

    /// <summary>
    ///     Fired when the player clicks inside the HUD bounds.
    ///     The integration layer (SMAPI) is responsible for translating mouse events to screen coordinates and calling
    ///     <see cref="HandleClick" />.
    /// </summary>
    public event EventHandler<HUDClickEventArgs>? OnHUDClicked;

    /// <summary>
    ///     Refreshes the cached agent data from the <see cref="AgentAllocationManager" />.
    ///     Call this each frame (or tick) before retrieving render data.
    /// </summary>
    public void Update()
    {
        var agents = _allocationManager.GetAllAllocatedAgents();
        var cards = new List<AgentCardData>(agents.Count);

        var scale = _config.Scale;
        var cardWidth = (int)(BaseCardWidth * scale);
        var cardHeight = (int)(BaseCardHeight * scale);
        var spacing = (int)(BaseCardSpacing * scale);

        var currentX = _config.PositionX;
        var currentY = _config.PositionY;

        foreach (var agent in agents)
        {
            var card = new AgentCardData
            {
                NpcName = _config.ShowAgentNames ? agent.NpcName : string.Empty,
                State = agent.CurrentState,
                IsThinking = _config.ShowThinkingIndicator && IsAgentThinking(agent.NpcName),
                FriendshipHearts = CalculateHearts(agent.FriendshipLevel),
                IsManuallyOverridden = agent.IsManuallyOverridden,
                ScreenX = currentX,
                ScreenY = currentY,
                Width = cardWidth,
                Height = cardHeight
            };

            cards.Add(card);
            currentY += cardHeight + spacing;
        }

        lock (_lock)
        {
            _cachedCards = cards;
        }
    }

    /// <summary>
    ///     Shows the HUD. Subsequent <see cref="GetRenderData" /> calls will return visible data.
    /// </summary>
    public void Show()
    {
        lock (_lock)
        {
            _isVisible = true;
        }
    }

    /// <summary>
    ///     Hides the HUD. <see cref="GetRenderData" /> will return IsVisible=false.
    /// </summary>
    public void Hide()
    {
        lock (_lock)
        {
            _isVisible = false;
        }
    }

    /// <summary>
    ///     Sets the thinking indicator for a specific NPC.
    ///     The indicator is independent of the agent鈥檚 <see cref="AgentState" /> so that
    ///     the spinner can be shown before the state machine transitions.
    /// </summary>
    /// <param name="npcName">Name of the NPC.</param>
    /// <param name="isThinking">True to show the spinner, false to hide it.</param>
    public void SetThinking(string npcName, bool isThinking)
    {
        if (string.IsNullOrWhiteSpace(npcName))
        {
            return;
        }

        lock (_lock)
        {
            _thinkingStates[npcName] = isThinking;
        }
    }

    /// <summary>
    ///     Updates the global token-usage counter shown when <see cref="HUDConfig.ShowTokenUsage" /> is enabled.
    /// </summary>
    /// <param name="tokens">Total tokens consumed, or null to clear.</param>
    public void SetTokenUsage(int? tokens)
    {
        lock (_lock)
        {
            _tokenUsage = tokens;
        }
    }

    /// <summary>
    ///     Builds a snapshot of everything that should be drawn this frame.
    ///     The SMAPI integration layer calls this inside <code>Display.RenderedHud</code>
    ///     and passes the result to its <see cref="IHUDRenderer" /> implementation.
    /// </summary>
    /// <returns>Render data, or a struct with IsVisible=false when hidden.</returns>
    public HUDRenderData GetRenderData()
    {
        lock (_lock)
        {
            if (!_isVisible || _cachedCards.Count == 0)
            {
                return new HUDRenderData { IsVisible = false };
            }

            var tokens = _config.ShowTokenUsage ? _tokenUsage : null;

            // Compute union bounds of all cards
            var minX = _cachedCards.Min(c => c.ScreenX);
            var minY = _cachedCards.Min(c => c.ScreenY);
            var maxX = _cachedCards.Max(c => c.ScreenX + c.Width);
            var maxY = _cachedCards.Max(c => c.ScreenY + c.Height);

            // If token bar is shown, extend bounds
            if (tokens.HasValue)
            {
                var tokenBarH = (int)(BaseTokenBarHeight * _config.Scale);
                maxY += tokenBarH + (int)(BaseCardSpacing * _config.Scale);
            }

            return new HUDRenderData
            {
                IsVisible = true,
                Scale = _config.Scale,
                Opacity = _config.Opacity,
                AgentCards = _cachedCards.AsReadOnly(),
                TotalTokenUsage = tokens,
                BoundsX = minX,
                BoundsY = minY,
                BoundsWidth = maxX - minX,
                BoundsHeight = maxY - minY
            };
        }
    }

    /// <summary>
    ///     Hit-tests a screen coordinate against the cached card layout.
    ///     Call this from the integration layer when a mouse click occurs.
    ///     Returns true if the click landed inside the HUD bounds and raises <see cref="OnHUDClicked" />.
    /// </summary>
    /// <param name="screenX">Screen X coordinate.</param>
    /// <param name="screenY">Screen Y coordinate.</param>
    /// <returns>True if the click was inside the HUD bounds.</returns>
    public bool HandleClick(int screenX, int screenY)
    {
        List<AgentCardData> snapshot;
        bool visible;

        lock (_lock)
        {
            visible = _isVisible;
            snapshot = _cachedCards;
        }

        if (!visible || snapshot.Count == 0)
        {
            return false;
        }

        // Find the clicked card (front-to-back order doesn鈥檛 matter here 鈥?they don鈥檛 overlap)
        var clicked = snapshot.FirstOrDefault(c =>
            screenX >= c.ScreenX &&
            screenX < c.ScreenX + c.Width &&
            screenY >= c.ScreenY &&
            screenY < c.ScreenY + c.Height);

        var args = new HUDClickEventArgs
        {
            AgentName = clicked?.NpcName,
            ScreenX = screenX,
            ScreenY = screenY
        };

        OnHUDClicked?.Invoke(this, args);
        return true;
    }

    // --------------------------------------------------------------------
    // Helpers
    // --------------------------------------------------------------------

    private bool IsAgentThinking(string npcName)
    {
        lock (_lock)
        {
            return _thinkingStates.TryGetValue(npcName, out var thinking) && thinking;
        }
    }

    /// <summary>
    ///     Converts a raw friendship value into a 0鈥?0 heart count.
    ///     Stardew Valley uses 250 points per heart (max 2500 = 10 hearts).
    ///     If the value is already 鈮?10 we assume it is already in hearts.
    /// </summary>
    private static int CalculateHearts(double friendshipValue)
    {
        return friendshipValue <= 10
            ? Math.Clamp((int)Math.Round(friendshipValue), 0, 10)
            : Math.Clamp((int)Math.Round(friendshipValue / 250.0), 0, 10);
    }
}