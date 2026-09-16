using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using ValleyAgent.Save.Models;
using ValleyAgent.Services;
using ValleyAgent.UI;

namespace ValleyAgent.Patches;

[HarmonyPatch(typeof(SocialPage))]
public static class SocialPagePatch
{
    private const int ButtonWidth = 150;
    private const int ButtonHeight = 36;
    private const int ButtonSpacing = 10;

    private static Rectangle _memoryPalaceButtonRect;
    private static Rectangle _dialogueHistoryButtonRect;
    private static string? _currentNpcName;

    private static readonly Color WoodBackground = new(245, 222, 179);
    private static readonly Color WoodBorder = new(139, 105, 20);
    private static readonly Color DarkText = new(58, 42, 26);
    public static AgentService? AgentService { get; set; }
    public static IMonitor? Monitor { get; set; }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(SocialPage.draw))]
    private static void DrawPostfix(SocialPage __instance, SpriteBatch b)
    {
        try
        {
            var npcName = GetCurrentNpcName(__instance);
            _currentNpcName = npcName;

            if (string.IsNullOrEmpty(npcName) || AgentService == null || !AgentService.HasAgent(npcName))
            {
                return;
            }

            var infoX = __instance.xPositionOnScreen + __instance.width / 2 + 64;
            var infoY = __instance.yPositionOnScreen + 256;

            _memoryPalaceButtonRect = new Rectangle(infoX, infoY, ButtonWidth, ButtonHeight);
            _dialogueHistoryButtonRect =
                new Rectangle(infoX + ButtonWidth + ButtonSpacing, infoY, ButtonWidth, ButtonHeight);

            DrawButton(b, _memoryPalaceButtonRect, "[宫] 记忆宫殿");
            DrawButton(b, _dialogueHistoryButtonRect, "[史] 对话历史");
        }
        catch (InvalidOperationException ex)
        {
            Monitor?.Log($"[SocialPagePatch] Draw error: {ex}", LogLevel.Warn);
        }
        catch (ArgumentException ex)
        {
            Monitor?.Log($"[SocialPagePatch] Draw error: {ex}", LogLevel.Warn);
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(SocialPage.receiveLeftClick))]
    private static void ReceiveLeftClickPostfix(SocialPage __instance, int x, int y)
    {
        try
        {
            if (string.IsNullOrEmpty(_currentNpcName) || AgentService == null ||
                !AgentService.HasAgent(_currentNpcName))
            {
                return;
            }

            if (_memoryPalaceButtonRect.Contains(x, y))
            {
                OpenMemoryPalace(_currentNpcName);
            }
            else if (_dialogueHistoryButtonRect.Contains(x, y))
            {
                OpenDialogueHistory(_currentNpcName);
            }
        }
        catch (InvalidOperationException ex)
        {
            Monitor?.Log($"[SocialPagePatch] Click error: {ex}", LogLevel.Warn);
        }
        catch (ArgumentException ex)
        {
            Monitor?.Log($"[SocialPagePatch] Click error: {ex}", LogLevel.Warn);
        }
    }

    private static string? GetCurrentNpcName(SocialPage socialPage)
    {
        var socialEntriesField =
            typeof(SocialPage).GetField("socialEntries", BindingFlags.Instance | BindingFlags.NonPublic);
        var slotPositionField =
            typeof(SocialPage).GetField("slotPosition", BindingFlags.Instance | BindingFlags.NonPublic);

        if (socialEntriesField == null || slotPositionField == null)
        {
            return null;
        }

        var entries = socialEntriesField.GetValue(socialPage);
        var slotPositionValue = slotPositionField.GetValue(socialPage);
        if (slotPositionValue == null)
        {
            return null;
        }

        var slotPosition = (int)slotPositionValue;

        if (entries == null)
        {
            return null;
        }

        var mouseY = Game1.getMouseY();
        var mouseX = Game1.getMouseX();

        var listStartY = socialPage.yPositionOnScreen + IClickableMenu.borderWidth + 80;
        var slotHeight = 80;

        if (mouseX < socialPage.xPositionOnScreen || mouseX > socialPage.xPositionOnScreen + socialPage.width / 2)
        {
            return null;
        }

        if (mouseY < listStartY)
        {
            return null;
        }

        var hoverIndex = (mouseY - listStartY) / slotHeight + slotPosition;

        if (hoverIndex < 0)
        {
            return null;
        }

        if (entries is IList<string> stringList)
        {
            return hoverIndex < stringList.Count ? stringList[hoverIndex] : null;
        }

        if (entries is IList list && hoverIndex < list.Count)
        {
            var item = list[hoverIndex];
            if (item == null)
            {
                return null;
            }

            var nameProp = item.GetType().GetProperty("Name")
                           ?? item.GetType().GetProperty("InternalName")
                           ?? item.GetType().GetProperty("DisplayName");
            var result = nameProp?.GetValue(item)?.ToString();
            if (!string.IsNullOrEmpty(result))
            {
                return result;
            }

            var nameField = item.GetType().GetField("name",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return nameField?.GetValue(item)?.ToString();
        }

        return null;
    }

    private static void DrawButton(SpriteBatch b, Rectangle rect, string text)
    {
        b.Draw(Game1.staminaRect, rect, WoodBackground);
        DrawRectBorder(b, rect, WoodBorder, 2);

        var textSize = Game1.smallFont.MeasureString(text);
        var textX = rect.X + (rect.Width - textSize.X) / 2;
        var textY = rect.Y + (rect.Height - textSize.Y) / 2;
        b.DrawString(Game1.smallFont, text, new Vector2(textX, textY), DarkText);
    }

    private static void DrawRectBorder(SpriteBatch b, Rectangle rect, Color color, int thickness)
    {
        b.Draw(Game1.staminaRect, new Rectangle(rect.X, rect.Y, rect.Width, thickness), color);
        b.Draw(Game1.staminaRect, new Rectangle(rect.X, rect.Bottom - thickness, rect.Width, thickness), color);
        b.Draw(Game1.staminaRect, new Rectangle(rect.X, rect.Y, thickness, rect.Height), color);
        b.Draw(Game1.staminaRect, new Rectangle(rect.Right - thickness, rect.Y, thickness, rect.Height), color);
    }

    private static void OpenMemoryPalace(string npcName)
    {
        if (AgentService == null || !AgentService.TryGetAgent(npcName, out var agent) || agent == null)
        {
            return;
        }

        var friendshipPoints = 0;
        if (Game1.player.friendshipData.TryGetValue(npcName, out var friendship))
        {
            friendshipPoints = friendship.Points;
        }

        var menu = new AgentMemoryPalaceMenu(
            npcName,
            agent.Brain.ShortTermMemories,
            agent.Brain.LongTermMemories,
            friendshipPoints);

        Game1.activeClickableMenu = menu;
    }

    private static void OpenDialogueHistory(string npcName)
    {
        var dialogueHistory = new List<DialogueExchangeData>();

        var modEntry = ModEntry.Instance;
        if (modEntry != null)
        {
            var saveData = modEntry.Helper.Data.ReadSaveData<SaveData>("ValleyAgent_Structured");
            if (saveData?.Memories != null && saveData.Memories.TryGetValue(npcName, out var memoryData))
            {
                dialogueHistory = memoryData.DialogueHistory ?? new List<DialogueExchangeData>();
            }
        }

        var menu = new AgentDialogueHistoryMenu(npcName, dialogueHistory);
        Game1.activeClickableMenu = menu;
    }
}