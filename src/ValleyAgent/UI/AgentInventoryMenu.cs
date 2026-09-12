using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;
using ValleyAgent.Services;

namespace ValleyAgent.UI;

/// <summary>
///     Displays an Agent NPC's 12-slot backpack inventory.
/// </summary>
public class AgentInventoryMenu : IClickableMenu
{
    private const int SlotsPerRow = 4;
    private const int SlotSize = 64;
    private const int SlotPadding = 16;
    private const int TitleHeight = 64;
    private const int BottomPadding = 32;

    private static readonly Color WoodBackground = new(245, 222, 179);
    private static readonly Color WoodBorder = new(139, 105, 20);
    private static readonly Color DarkText = new(58, 42, 26);
    private readonly AgentService _agentService;
    private readonly Item?[] _items = new Item?[12];
    private readonly string _npcName;
    private readonly Rectangle[] _slotBounds = new Rectangle[12];

    public AgentInventoryMenu(string npcName, AgentService agentService)
        : base(0, 0, 0, 0, true)
    {
        _npcName = npcName;
        _agentService = agentService;

        var rows = 3;
        var cols = 4;
        var contentWidth = cols * SlotSize + (cols - 1) * SlotPadding + 48;
        var contentHeight = TitleHeight + rows * SlotSize + (rows - 1) * SlotPadding + BottomPadding;

        width = contentWidth;
        height = contentHeight;
        xPositionOnScreen = (Game1.uiViewport.Width - width) / 2;
        yPositionOnScreen = (Game1.uiViewport.Height - height) / 2;

        RefreshItems();
    }

    private void RefreshItems()
    {
        Array.Clear(_items, 0, _items.Length);
        if (_agentService.TryGetAgent(_npcName, out var agent) && agent != null)
        {
            var items = agent.Inventory.GetAllItems();
            for (var i = 0; i < Math.Min(items.Length, 12); i++)
            {
                _items[i] = items[i];
            }
        }
    }

    public override void draw(SpriteBatch b)
    {
        if (!Game1.options.showMenuBackground)
        {
            b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.4f);
        }

        DrawWoodPanel(b, xPositionOnScreen, yPositionOnScreen, width, height);

        var title = $"{_npcName}'s Backpack";
        var titleSize = Game1.dialogueFont.MeasureString(title);
        b.DrawString(Game1.dialogueFont, title,
            new Vector2(xPositionOnScreen + (width - titleSize.X) / 2, yPositionOnScreen + 12),
            DarkText);

        var startX = xPositionOnScreen + (width - (SlotsPerRow * SlotSize + (SlotsPerRow - 1) * SlotPadding)) / 2;
        var startY = yPositionOnScreen + TitleHeight;

        for (var i = 0; i < 12; i++)
        {
            var row = i / SlotsPerRow;
            var col = i % SlotsPerRow;
            var x = startX + col * (SlotSize + SlotPadding);
            var y = startY + row * (SlotSize + SlotPadding);
            _slotBounds[i] = new Rectangle(x, y, SlotSize, SlotSize);

            b.Draw(Game1.staminaRect, _slotBounds[i],
                WoodBackground * 0.5f);

            var item = _items[i];
            if (item != null)
            {
                item.drawInMenu(b, new Vector2(x, y), 1f, 1f, 0.86f,
                    StackDrawType.Draw, Color.White, false);

                if (_slotBounds[i].Contains(Game1.getMouseX(), Game1.getMouseY()))
                {
                    var tooltip = item.DisplayName;
                    if (item.Stack > 1)
                    {
                        tooltip += $" x{item.Stack}";
                    }

                    drawHoverText(b, tooltip, Game1.smallFont);
                }
            }
        }

        base.draw(b);
        drawMouse(b);
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        base.receiveLeftClick(x, y, playSound);
        if (upperRightCloseButton != null && upperRightCloseButton.containsPoint(x, y))
        {
            exitThisMenu(playSound);
        }
    }

    public override void update(GameTime time)
    {
        base.update(time);
        RefreshItems();
    }

    private static void DrawWoodPanel(SpriteBatch b, int x, int y, int w, int h)
    {
        b.Draw(Game1.staminaRect, new Rectangle(x, y, w, h), WoodBackground);
        DrawWoodBorder(b, x, y, w, h);
    }

    private static void DrawWoodBorder(SpriteBatch b, int x, int y, int w, int h)
    {
        var thickness = 4;
        b.Draw(Game1.staminaRect, new Rectangle(x, y, w, thickness), WoodBorder);
        b.Draw(Game1.staminaRect, new Rectangle(x, y + h - thickness, w, thickness), WoodBorder);
        b.Draw(Game1.staminaRect, new Rectangle(x, y, thickness, h), WoodBorder);
        b.Draw(Game1.staminaRect, new Rectangle(x + w - thickness, y, thickness, h), WoodBorder);
    }
}