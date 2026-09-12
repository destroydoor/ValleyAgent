using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using ValleyAgent.StateMachine;

namespace ValleyAgent.UI;

/// <summary>
///     SMAPI implementation of <see cref="IHUDRenderer" />.
///     Draws agent HUD cards (name, state, health bar, friendship hearts) on screen
///     using SpriteBatch during the RenderedHud event.
/// </summary>
public class SmaHUDRenderer : IHUDRenderer
{
    private const int HeartSize = 10;
    private const int HeartSpacing = 2;
    private const int CardPadding = 8;
    private const int TextTopMargin = 4;
    private const int ProgressBarHeight = 4;
    private const int ProgressBarMargin = 2;

    private static readonly Color CardBgColor = new(20, 20, 40, 180);
    private static readonly Color CardBorderColor = new(80, 80, 120, 200);
    private static readonly Color NameColor = Color.White;
    private static readonly Color HealthBgColor = new(40, 40, 40, 200);

    // State label colors
    private static readonly Color StateIdleColor = new(150, 150, 150);
    private static readonly Color StateFollowColor = new(100, 180, 255);
    private static readonly Color StateFightColor = new(255, 80, 80);
    private static readonly Color StateFarmColor = new(80, 200, 80);
    private static readonly Color StateMineColor = new(200, 160, 80);
    private static readonly Color StateForageColor = new(120, 200, 120);
    private static readonly Color StateTalkColor = new(255, 220, 80);
    private static readonly Color StateDefaultColor = new(180, 180, 180);

    public void Render(HUDRenderData hudData)
    {
        if (!hudData.IsVisible || hudData.AgentCards.Count == 0)
        {
            return;
        }

        var b = Game1.spriteBatch;
        var scale = hudData.Scale;

        foreach (var card in hudData.AgentCards)
        {
            DrawCard(b, card, scale, hudData.Opacity);
        }

        // Draw token usage bar if enabled
        if (hudData.TotalTokenUsage.HasValue)
        {
            DrawTokenUsage(b, hudData);
        }
    }

    private static void DrawCard(SpriteBatch b, AgentCardData card, float scale, float opacity)
    {
        var x = card.ScreenX;
        var y = card.ScreenY;
        var w = card.Width;
        var h = card.Height;

        // Card background
        var bgRect = new Rectangle(x, y, w, h);
        b.Draw(Game1.staminaRect, bgRect, CardBgColor * opacity);

        // Card border
        DrawBorder(b, bgRect, 1, CardBorderColor * opacity);

        var textX = x + (int)(CardPadding * scale);
        var textY = y + (int)(TextTopMargin * scale);

        // NPC name
        if (!string.IsNullOrEmpty(card.NpcName))
        {
            var nameStr = card.NpcName;
            if (card.IsThinking)
            {
                nameStr += " ...";
            }

            b.DrawString(Game1.smallFont, nameStr,
                new Vector2(textX, textY), NameColor * opacity);
            textY += (int)(Game1.smallFont.LineSpacing * scale);
        }

        // State label
        var stateStr = card.State.ToString();
        var stateColor = GetStateColor(card.State);
        b.DrawString(Game1.smallFont, stateStr,
            new Vector2(textX, textY), stateColor * opacity);
        textY += (int)(Game1.smallFont.LineSpacing * scale);

        // Friendship hearts (if enabled and > 0)
        if (card.FriendshipHearts > 0)
        {
            DrawHearts(b, textX, textY, card.FriendshipHearts, scale, opacity);
            textY += (int)((HeartSize + HeartSpacing) * scale);
        }

        // Health bar placeholder space (actual health drawn by AgentRenderer in world space)
        var barY = y + h - (int)((ProgressBarHeight + ProgressBarMargin * 2) * scale);
        var barX = x + (int)(CardPadding * scale);
        var barW = w - (int)(CardPadding * 2 * scale);

        // Background bar
        b.Draw(Game1.staminaRect,
            new Rectangle(barX, barY, barW, (int)(ProgressBarHeight * scale)),
            HealthBgColor * opacity);
    }

    private static void DrawHearts(SpriteBatch b, int x, int y, int count, float scale, float opacity)
    {
        for (var i = 0; i < count; i++)
        {
            var hx = x + (int)(i * (HeartSize + HeartSpacing) * scale);
            var hy = y;

            // Draw heart using Game1.mouseCursors (standard heart sprite)
            b.Draw(Game1.mouseCursors,
                new Vector2(hx, hy),
                new Rectangle(211, 344, HeartSize, HeartSize),
                Color.White * opacity,
                0f,
                Vector2.Zero,
                scale,
                SpriteEffects.None,
                1f);
        }
    }

    private static void DrawBorder(SpriteBatch b, Rectangle rect, int thickness, Color color)
    {
        // Top
        b.Draw(Game1.staminaRect, new Rectangle(rect.X, rect.Y, rect.Width, thickness), color);
        // Bottom
        b.Draw(Game1.staminaRect, new Rectangle(rect.X, rect.Y + rect.Height - thickness, rect.Width, thickness),
            color);
        // Left
        b.Draw(Game1.staminaRect, new Rectangle(rect.X, rect.Y, thickness, rect.Height), color);
        // Right
        b.Draw(Game1.staminaRect, new Rectangle(rect.X + rect.Width - thickness, rect.Y, thickness, rect.Height),
            color);
    }

    private static Color GetStateColor(AgentState state)
    {
        return state switch
        {
            AgentState.IDLE => StateIdleColor,
            AgentState.FOLLOW => StateFollowColor,
            AgentState.FIGHT => StateFightColor,
            AgentState.FARM => StateFarmColor,
            AgentState.MINE => StateMineColor,
            AgentState.FORAGE => StateForageColor,
            AgentState.TALK => StateTalkColor,
            _ => StateDefaultColor
        };
    }

    private static void DrawTokenUsage(SpriteBatch b, HUDRenderData hudData)
    {
        var tokenStr = $"Tokens: {hudData.TotalTokenUsage}";
        var tokenY = hudData.BoundsY + hudData.BoundsHeight + 4;
        b.DrawString(Game1.smallFont, tokenStr,
            new Vector2(hudData.BoundsX + 8, tokenY),
            new Color(180, 180, 180, 200) * hudData.Opacity);
    }
}