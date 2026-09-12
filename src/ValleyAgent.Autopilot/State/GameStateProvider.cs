using System.Collections.Generic;
using System.Text.Json;
using StardewValley;
using StardewValley.Characters;

namespace ValleyAgent.Autopilot.State
{
    /// <summary>
    /// 提供游戏状态查询，返回结构化 JSON 数据。
    /// </summary>
    public sealed class GameStateProvider
    {
        public static string GetPlayerState()
        {
            var farmer = Game1.player;
            if (farmer == null) return "{}";

            var state = new Dictionary<string, object>
            {
                ["name"] = farmer.Name ?? "",
                ["position"] = new Dictionary<string, object>
                {
                    ["x"] = (int)farmer.Position.X,
                    ["y"] = (int)farmer.Position.Y,
                    ["map"] = farmer.currentLocation?.Name ?? ""
                },
                ["facing"] = farmer.FacingDirection,
                ["health"] = farmer.health,
                ["stamina"] = (int)farmer.stamina,
                ["money"] = farmer.Money,
                ["tileX"] = farmer.TilePoint.X,
                ["tileY"] = farmer.TilePoint.Y
            };
            return JsonSerializer.Serialize(state);
        }

        public static string GetNpcsState()
        {
            var npcs = new List<Dictionary<string, object>>();
            var location = Game1.player?.currentLocation;
            if (location == null) return "[]";

            foreach (var npc in location.characters)
            {
                if (npc is Child or Pet or Horse) continue;
                npcs.Add(new Dictionary<string, object>
                {
                    ["name"] = npc.Name ?? "",
                    ["x"] = (int)npc.Position.X,
                    ["y"] = (int)npc.Position.Y,
                    ["facing"] = npc.FacingDirection,
                    ["isTalking"] = Game1.currentSpeaker == npc
                });
            }
            return JsonSerializer.Serialize(npcs);
        }

        public static string GetFullState()
        {
            var state = new Dictionary<string, object>
            {
                ["player"] = JsonSerializer.Deserialize<Dictionary<string, object>>(GetPlayerState())!,
                ["time"] = new Dictionary<string, object>
                {
                    ["day"] = Game1.dayOfMonth,
                    ["season"] = Game1.currentSeason ?? "",
                    ["year"] = Game1.year,
                    ["timeOfDay"] = Game1.timeOfDay
                },
                ["currentLocation"] = Game1.player?.currentLocation?.Name ?? "",
                ["menu"] = Game1.activeClickableMenu?.GetType().Name ?? "",
                ["dialogueUp"] = Game1.dialogueUp,
                ["currentSpeaker"] = Game1.currentSpeaker?.Name ?? "",
                ["viewport"] = new Dictionary<string, object>
                {
                    ["x"] = Game1.viewport.X,
                    ["y"] = Game1.viewport.Y,
                    ["w"] = Game1.viewport.Width,
                    ["h"] = Game1.viewport.Height
                },
                ["zoom"] = Game1.options?.zoomLevel ?? 1.0f
            };
            return JsonSerializer.Serialize(state);
        }
    }
}
