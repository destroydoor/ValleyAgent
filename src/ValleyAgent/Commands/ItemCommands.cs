using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.Brain;
using ValleyAgent.Services;

namespace ValleyAgent.Commands;

public class GiveItemCommand : AgentCommandBase
{
    public GiveItemCommand(IMonitor monitor) : base(monitor)
    {
    }

    public override string CommandName
    {
        get => "give_item";
    }

    public override void Execute(NPC npc, AgentInstance agent, Dictionary<string, object> parameters,
        Func<string, string, bool, Dictionary<string, object>, Task> sendResult)
    {
        var itemId = GetParamAny(parameters, "item_id", "itemId") as string;
        var rawQty = GetParamAny(parameters, "quantity", "count");
        var quantity = rawQty != null ? Convert.ToInt32(rawQty) : 1;

        if (!string.IsNullOrWhiteSpace(itemId))
        {
            if (agent.Inventory != null && !agent.Inventory.Contains(itemId))
            {
                Monitor.Log(
                    $"[CommandExecutor] {npc.Name} tried to give {quantity}x {itemId} but doesn't have it in inventory",
                    LogLevel.Warn);
                _ = sendResult(npc.Name, "give_item", false,
                    new Dictionary<string, object> { ["error"] = "Item not in inventory" });
                return;
            }

            Monitor.Log($"[CommandExecutor] {npc.Name} giving {quantity}x {itemId} to player", LogLevel.Debug);

            var item = ItemRegistry.Create(itemId, allowNull: true);
            item.Stack = quantity;
            var added = Game1.player.addItemToInventoryBool(item);
            if (added)
            {
                npc.showTextAboveHead($"Here, take this {item.DisplayName}!");
            }
            else
            {
                _ = Game1.createItemDebris(item, npc.Position, 0, npc.currentLocation);
                npc.showTextAboveHead("Your bag is full! I dropped it nearby.");
            }

            _ = sendResult(npc.Name, "give_item", true,
                new Dictionary<string, object>
                {
                    ["itemId"] = itemId,
                    ["count"] = quantity,
                    ["addedToInventory"] = added
                });
        }
        else
        {
            _ = sendResult(npc.Name, "give_item", false,
                new Dictionary<string, object> { ["error"] = "Missing item_id/itemId parameter" });
        }
    }
}

public class GiveGiftCommand : AgentCommandBase
{
    public GiveGiftCommand(IMonitor monitor) : base(monitor)
    {
    }

    public override string CommandName
    {
        get => "give_gift";
    }

    public override void Execute(NPC npc, AgentInstance agent, Dictionary<string, object> parameters,
        Func<string, string, bool, Dictionary<string, object>, Task> sendResult)
    {
        var itemId = GetParamAny(parameters, "item_id", "itemId") as string;
        var rawQty = GetParamAny(parameters, "quantity", "count");
        var quantity = rawQty != null ? Convert.ToInt32(rawQty) : 1;

        if (string.IsNullOrWhiteSpace(itemId))
        {
            _ = sendResult(npc.Name, "give_gift", false,
                new Dictionary<string, object> { ["error"] = "Missing 'item_id' parameter" });
            return;
        }

        // Issue 8: Verify NPC has the item in inventory before giving
        if (agent.Inventory != null && !agent.Inventory.Contains(itemId))
        {
            Monitor.Log(
                $"[CommandExecutor] {npc.Name} tried to give {quantity}x {itemId} but doesn't have it in inventory",
                LogLevel.Warn);
            _ = sendResult(npc.Name, "give_gift", false,
                new Dictionary<string, object> { ["error"] = $"NPC doesn't have {itemId} in inventory" });
            return;
        }

        Monitor.Log($"[CommandExecutor] {npc.Name} giving gift {quantity}x {itemId} to player", LogLevel.Debug);

        var item = ItemRegistry.Create(itemId, allowNull: true);

        item.Stack = quantity;

        npc.faceGeneralDirection(Game1.player.Position);

        npc.doEmote(8);

        var giftText = $"这个 {item.DisplayName} 送给你！";
        Game1.delayedActions.Add(new DelayedAction(400, () => { npc.showTextAboveHead(giftText); }));

        Game1.delayedActions.Add(new DelayedAction(1000, () =>
        {
            var added = Game1.player.addItemToInventoryBool(item);
            if (added)
            {
                Game1.chatBox?.addMessage($"{npc.Name} 送给了你 {item.DisplayName}！", Color.LightGreen);
            }
            else
            {
                _ = Game1.createItemDebris(item, npc.Position, 0, npc.currentLocation);
                Game1.chatBox?.addMessage($"{npc.Name} 想送你 {item.DisplayName}，但你的背包满了！物品放在了地上。", Color.Orange);
            }

            agent.Brain.AddMemory($"我送给了玩家 {item.DisplayName}");

            // Issue 8: Remove item from NPC inventory after giving
            agent.Inventory?.RemoveItem(itemId, quantity);

            var emoteId = added ? 20 : 28;
            npc.doEmote(emoteId);
            agent.Brain.SyncEmotion(NpcEmotion.Grateful, 0.5f, "GiftGiven");

            _ = sendResult(npc.Name, "give_gift", true,
                new Dictionary<string, object>
                {
                    ["itemId"] = itemId,
                    ["count"] = quantity,
                    ["addedToInventory"] = added,
                    ["displayName"] = item.DisplayName ?? itemId
                });
        }));
    }
}

public class EatFoodCommand : AgentCommandBase
{
    public EatFoodCommand(IMonitor monitor) : base(monitor)
    {
    }

    public override string CommandName
    {
        get => "eat_food";
    }

    public override void Execute(NPC npc, AgentInstance agent, Dictionary<string, object> parameters,
        Func<string, string, bool, Dictionary<string, object>, Task> sendResult)
    {
        var itemId = GetParamAny(parameters, "item_id", "itemId") as string;
        var healAmount = Convert.ToInt32(GetParamAny(parameters, "heal", "health", 30));

        Monitor.Log($"[CommandExecutor] {npc.Name} eating food (heal={healAmount})", LogLevel.Debug);

        npc.doEmote(12);
        npc.showTextAboveHead("Mmm~");

        if (agent.Health != null && agent.Health.Health < agent.Health.MaxHealth)
        {
            agent.Health.Heal(healAmount);
        }

        if (!string.IsNullOrWhiteSpace(itemId) && agent.Inventory != null)
        {
            _ = agent.Inventory.RemoveItem(itemId, 1);
        }

        _ = sendResult(npc.Name, "eat_food", true,
            new Dictionary<string, object> { ["healed"] = healAmount, ["itemId"] = itemId ?? "" });
    }
}

public class DropItemCommand : AgentCommandBase
{
    public DropItemCommand(IMonitor monitor) : base(monitor)
    {
    }

    public override string CommandName
    {
        get => "drop_item";
    }

    public override void Execute(NPC npc, AgentInstance agent, Dictionary<string, object> parameters,
        Func<string, string, bool, Dictionary<string, object>, Task> sendResult)
    {
        var itemId = GetParamAny(parameters, "item_id", "itemId") as string;
        var rawQty = GetParamAny(parameters, "quantity", "count");
        var quantity = rawQty != null ? Convert.ToInt32(rawQty) : 1;

        if (string.IsNullOrWhiteSpace(itemId))
        {
            _ = sendResult(npc.Name, "drop_item", false,
                new Dictionary<string, object> { ["error"] = "Missing 'item_id' parameter" });
            return;
        }

        Monitor.Log($"[CommandExecutor] {npc.Name} dropping {quantity}x {itemId}", LogLevel.Debug);

        var item = ItemRegistry.Create(itemId, allowNull: true);

        item.Stack = quantity;
        _ = Game1.createItemDebris(item, npc.Position, 0, npc.currentLocation);

        _ = agent.Inventory?.RemoveItem(itemId, quantity);

        _ = sendResult(npc.Name, "drop_item", true,
            new Dictionary<string, object> { ["itemId"] = itemId, ["count"] = quantity });
    }
}

public class UseItemCommand : AgentCommandBase
{
    public UseItemCommand(IMonitor monitor) : base(monitor)
    {
    }

    public override string CommandName
    {
        get => "use_item";
    }

    public override void Execute(NPC npc, AgentInstance agent, Dictionary<string, object> parameters,
        Func<string, string, bool, Dictionary<string, object>, Task> sendResult)
    {
        var itemId = GetParamAny(parameters, "item_id", "itemId") as string;
        var action = (GetParamAny(parameters, "action") as string)?.ToLowerInvariant() ?? "consume";

        if (string.IsNullOrWhiteSpace(itemId))
        {
            _ = sendResult(npc.Name, "use_item", false,
                new Dictionary<string, object> { ["error"] = "Missing 'item_id' parameter" });
            return;
        }

        Monitor.Log($"[CommandExecutor] {npc.Name} using item {itemId} (action={action})", LogLevel.Debug);

        switch (action)
        {
            case "consume":
                npc.doEmote(12);
                _ = agent.Inventory?.RemoveItem(itemId, 1);
                break;

            case "throw_":
                var throwItem = ItemRegistry.Create(itemId, allowNull: true);
                _ = Game1.createItemDebris(throwItem, npc.Position, -1, npc.currentLocation);
                _ = agent.Inventory?.RemoveItem(itemId, 1);
                break;

            default:
                _ = agent.Inventory?.RemoveItem(itemId, 1);
                break;
        }

        _ = sendResult(npc.Name, "use_item", true,
            new Dictionary<string, object> { ["itemId"] = itemId, ["action"] = action });
    }
}