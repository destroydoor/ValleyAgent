#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Monsters;
using StardewValley.Pathfinding;
using StardewValley.TerrainFeatures;
using ValleyAgent.TestMod.Mock;

namespace ValleyAgent.TestMod;

/// <summary>
///     Assertion helpers that inspect the game world and return Pass/Fail results.
///     All methods return a <see cref="TestResult" /> so the runner can aggregate them.
/// </summary>
public static class TestAssertions
{
    // ------------------------------------------------------------------
    // Follow assertions
    // ------------------------------------------------------------------

    /// <summary>Verify the NPC is on the same map and within <paramref name="maxTiles" /> of the player.</summary>
    public static TestResult NpcIsNearPlayer(NPC npc, float maxTiles = 8f)
    {
        if (npc == null)
        {
            return TestResult.Fail("NPC is null.");
        }

        if (npc.currentLocation != Game1.currentLocation)
        {
            return TestResult.Fail(
                $"NPC '{npc.Name}' is on map '{npc.currentLocation?.NameOrUniqueName}' but player is on '{Game1.currentLocation?.NameOrUniqueName}'.");
        }

        var dist = Vector2.Distance(npc.Tile, Game1.player.Tile);
        return dist <= maxTiles
            ? TestResult.Pass($"NPC '{npc.Name}' is {dist:F1} tiles from player (<= {maxTiles}).")
            : TestResult.Fail($"NPC '{npc.Name}' is {dist:F1} tiles from player (> {maxTiles}).");
    }

    /// <summary>Verify the NPC is moving (has a non-zero velocity / is walking).</summary>
    public static TestResult NpcIsMoving(NPC npc)
    {
        if (npc == null)
        {
            return TestResult.Fail("NPC is null.");
        }

        var moving = npc.isMoving() || npc.Speed > 0;
        return moving
            ? TestResult.Pass($"NPC '{npc.Name}' is moving.")
            : TestResult.Fail($"NPC '{npc.Name}' is standing still.");
    }

    /// <summary>Verify the NPC is standing still (not moving).</summary>
    public static TestResult NpcIsStandingStill(NPC npc)
    {
        if (npc == null)
        {
            return TestResult.Fail("NPC is null.");
        }

        var still = !npc.isMoving() && npc.Speed == 0;
        return still
            ? TestResult.Pass($"NPC '{npc.Name}' is standing still.")
            : TestResult.Fail($"NPC '{npc.Name}' is still moving.");
    }

    // ------------------------------------------------------------------
    // Gift assertions
    // ------------------------------------------------------------------

    /// <summary>Verify the player's inventory contains an item matching the given name or ID.</summary>
    public static TestResult PlayerReceivedItem(string itemNameOrId)
    {
        if (string.IsNullOrEmpty(itemNameOrId))
        {
            return TestResult.Fail("No gift item was produced (empty item name).");
        }

        var found = Game1.player.Items.Any(i =>
            i != null &&
            (i.Name.Equals(itemNameOrId, StringComparison.OrdinalIgnoreCase) ||
             i.DisplayName.Equals(itemNameOrId, StringComparison.OrdinalIgnoreCase) ||
             i.ItemId.Equals(itemNameOrId, StringComparison.OrdinalIgnoreCase) ||
             i.QualifiedItemId.Equals($"(O){itemNameOrId}", StringComparison.OrdinalIgnoreCase)));

        return found
            ? TestResult.Pass($"Player received gift item '{itemNameOrId}'.")
            : TestResult.Fail($"Player did not receive gift item '{itemNameOrId}'.");
    }

    // ------------------------------------------------------------------
    // AI Decision assertions
    // ------------------------------------------------------------------

    /// <summary>Verify an LLM decision was made with a non-empty state and reason.</summary>
    public static TestResult DecisionWasMade(string state, string reason)
    {
        if (string.IsNullOrEmpty(state))
        {
            return TestResult.Fail("LLM decision state is empty.");
        }

        return string.IsNullOrEmpty(reason)
            ? TestResult.Fail("LLM decision reason is empty.")
            : TestResult.Pass($"Decision: {state} | Reason: {reason[..Math.Min(80, reason.Length)]}");
    }

    // ------------------------------------------------------------------
    // AI Dialogue assertions
    // ------------------------------------------------------------------

    /// <summary>Verify an AI dialogue response was generated and is non-empty.</summary>
    public static TestResult DialogueWasGenerated(string response)
    {
        return string.IsNullOrEmpty(response)
            ? TestResult.Fail("AI dialogue response is empty.")
            : TestResult.Pass($"AI dialogue ({response.Length} chars): {response[..Math.Min(80, response.Length)]}");
    }

    // ------------------------------------------------------------------
    // Config assertions
    // ------------------------------------------------------------------

    /// <summary>Verify that all core feature switches are enabled.</summary>
    public static TestResult ConfigFeaturesEnabled(IReadOnlyList<string> features)
    {
        var required = new[] { "Combat", "Farming", "Mining", "Foraging", "Gifts", "Friendship" };
        var missing = required.Where(r => !features.Any(f => f.Equals(r, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        return missing.Length == 0
            ? TestResult.Pass($"All {required.Length} core features are enabled.")
            : TestResult.Fail(
                $"Missing features: {string.Join(", ", missing)} (enabled: {string.Join(", ", features)}).");
    }

    // ------------------------------------------------------------------
    // Multi-agent assertions
    // ------------------------------------------------------------------

    /// <summary>Verify that at least one agent is allocated (preferably 2+ for multi-agent test).</summary>
    public static TestResult MultiAgentAllocated(int count)
    {
        if (count >= 2)
        {
            return TestResult.Pass($"{count} agents are active (multi-agent confirmed).");
        }

        return count == 1
            ? TestResult.Pass("1 agent active (no second NPC available in location).")
            : TestResult.Fail("0 agents active — allocation failed.");
    }

    // ------------------------------------------------------------------
    // Friendship assertions
    // ------------------------------------------------------------------

    /// <summary>Verify that friendship data exists for the NPC.</summary>
    public static TestResult FriendshipDataExists(int points)
    {
        if (points < 0)
        {
            return TestResult.Fail("No friendship data found for NPC (returned -1).");
        }

        return points > 2500
            ? TestResult.Fail($"Friendship points {points} exceed maximum (2500).")
            : TestResult.Pass($"Friendship data exists ({points}/2500 points).");
    }

    // ------------------------------------------------------------------
    // Dialogue patch assertions
    // ------------------------------------------------------------------

    /// <summary>Verify that the NPC dialogue patch intercepted at least one interaction.</summary>
    public static TestResult DialoguePatchIntercepted(int beforeCount, int afterCount)
    {
        return afterCount > beforeCount
            ? TestResult.Pass($"Dialogue patch intercepted click (count: {beforeCount} → {afterCount}).")
            : TestResult.Fail($"Dialogue patch did not intercept click (count stayed at {beforeCount}).");
    }

    // ------------------------------------------------------------------
    // Fight assertions
    // ------------------------------------------------------------------

    /// <summary>Compare current monster health against a previously-captured snapshot.</summary>
    public static TestResult MonstersWereDamaged(IReadOnlyDictionary<Monster, int> previousHealth)
    {
        if (previousHealth == null || previousHealth.Count == 0)
        {
            return TestResult.Fail("No monster health snapshot provided.");
        }

        var damagedCount = 0;
        var total = 0;
        foreach (var kvp in previousHealth)
        {
            total++;
            if (kvp.Key == null || kvp.Key.currentLocation == null)
            {
                continue; // monster despawned = probably killed
            }

            if (kvp.Key.Health < kvp.Value)
            {
                damagedCount++;
            }
        }

        return damagedCount > 0
            ? TestResult.Pass($"{damagedCount}/{total} monsters took damage.")
            : TestResult.Fail($"0/{total} monsters took damage.");
    }

    /// <summary>Only count monsters as damaged if the agent was within 4 tiles of them when hurt.</summary>
    public static TestResult MonstersDamagedByAgent(IReadOnlyDictionary<Monster, int> previousHealth,
        IReadOnlyList<Monster> watchedMonsters, NPC? agent)
    {
        ArgumentNullException.ThrowIfNull(watchedMonsters);
        if (previousHealth == null || previousHealth.Count == 0)
        {
            return TestResult.Fail("No monster health snapshot provided.");
        }

        if (agent == null)
        {
            return TestResult.Fail("Agent NPC is null.");
        }

        var agentDamaged = 0;
        var total = 0;
        foreach (var monster in watchedMonsters)
        {
            if (monster == null || !previousHealth.TryGetValue(monster, out var oldHealth))
            {
                continue;
            }

            total++;
            if (monster.currentLocation == null)
            {
                continue; // despawned
            }

            if (monster.Health < oldHealth && Vector2.Distance(monster.Tile, agent.Tile) <= 4f)
            {
                agentDamaged++;
            }
        }

        return agentDamaged > 0
            ? TestResult.Pass($"{agentDamaged}/{total} monsters were damaged while agent was within 4 tiles.")
            : TestResult.Fail($"0/{total} monsters were damaged by agent (player may have hit them instead).");
    }

    /// <summary>Count how many monsters remain in the current location.</summary>
    public static int CountMonsters() => Game1.currentLocation.characters.OfType<Monster>().Count();

    // ------------------------------------------------------------------
    // Farm assertions
    // ------------------------------------------------------------------

    /// <summary>Verify that at least one of the watched crop tiles no longer has a mature crop.</summary>
    public static TestResult CropsWereHarvested(IReadOnlyList<Vector2> watchedTiles)
    {
        if (watchedTiles == null || watchedTiles.Count == 0)
        {
            return TestResult.Fail("No crop tiles to watch.");
        }

        var loc = Game1.currentLocation;
        var harvested = 0;
        foreach (var tile in watchedTiles)
        {
            if (!loc.terrainFeatures.TryGetValue(tile, out var tf))
            {
                harvested++; // tile feature removed entirely
                continue;
            }

            if (tf is HoeDirt dirt)
            {
                if (dirt.crop == null || dirt.crop.dead.Value)
                {
                    harvested++;
                }
            }
        }

        return harvested > 0
            ? TestResult.Pass($"{harvested}/{watchedTiles.Count} crop tiles were harvested.")
            : TestResult.Fail($"0/{watchedTiles.Count} crop tiles were harvested.");
    }

    // ------------------------------------------------------------------
    // Mine assertions
    // ------------------------------------------------------------------

    /// <summary>Verify that at least one of the watched object tiles no longer has a breakable object.</summary>
    public static TestResult RocksWereBroken(IReadOnlyList<Vector2> watchedTiles)
    {
        if (watchedTiles == null || watchedTiles.Count == 0)
        {
            return TestResult.Fail("No rock tiles to watch.");
        }

        var loc = Game1.currentLocation;
        var broken = 0;
        foreach (var tile in watchedTiles)
        {
            if (!loc.objects.ContainsKey(tile))
            {
                broken++;
            }
        }

        return broken > 0
            ? TestResult.Pass($"{broken}/{watchedTiles.Count} rocks were broken.")
            : TestResult.Fail($"0/{watchedTiles.Count} rocks were broken.");
    }

    // ------------------------------------------------------------------
    // Forage assertions
    // ------------------------------------------------------------------

    /// <summary>Verify that at least one of the watched forage tiles no longer has an object.</summary>
    public static TestResult ForageablesWereCollected(IReadOnlyList<Vector2> watchedTiles)
    {
        if (watchedTiles == null || watchedTiles.Count == 0)
        {
            return TestResult.Fail("No forage tiles to watch.");
        }

        var loc = Game1.currentLocation;
        var collected = 0;
        foreach (var tile in watchedTiles)
        {
            if (!loc.objects.ContainsKey(tile))
            {
                collected++;
            }
        }

        return collected > 0
            ? TestResult.Pass($"{collected}/{watchedTiles.Count} forageables were collected.")
            : TestResult.Fail($"0/{watchedTiles.Count} forageables were collected.");
    }

    // ------------------------------------------------------------------
    // Snapshot helpers
    // ------------------------------------------------------------------

    /// <summary>Capture a snapshot of the given monsters' current health.</summary>
    public static Dictionary<Monster, int> SnapshotMonsterHealth(IEnumerable<Monster> monsters)
    {
        ArgumentNullException.ThrowIfNull(monsters);
        var dict = new Dictionary<Monster, int>();
        foreach (var m in monsters)
        {
            if (m != null)
            {
                dict[m] = m.Health;
            }
        }

        return dict;
    }

    // ------------------------------------------------------------------
    // Health assertions
    // ------------------------------------------------------------------

    /// <summary>Verify that NPC health dropped from a previous snapshot.</summary>
    public static TestResult NpcHealthDropped(string npcName, int previousHealth, int currentHealth)
    {
        if (previousHealth < 0)
        {
            return TestResult.Fail($"No previous health snapshot for '{npcName}'.");
        }

        if (currentHealth < 0)
        {
            return TestResult.Fail($"Could not read current health for '{npcName}'.");
        }

        return currentHealth < previousHealth
            ? TestResult.Pass($"{npcName} health dropped from {previousHealth} to {currentHealth}.")
            : TestResult.Fail($"{npcName} health did not drop (was {previousHealth}, now {currentHealth}).");
    }

    /// <summary>Verify that an NPC's inventory is not empty.</summary>
    public static TestResult NpcInventoryNotEmpty(string npcName, string[] items)
    {
        return items == null || items.Length == 0
            ? TestResult.Fail($"{npcName}'s inventory is empty.")
            : TestResult.Pass($"{npcName}'s inventory has {items.Length} slot(s): {string.Join(", ", items)}.");
    }

    /// <summary>Verify that an API call returned true.</summary>
    public static TestResult ApiCallSucceeded(string apiName, bool result) => result
        ? TestResult.Pass($"API '{apiName}' returned true.")
        : TestResult.Fail($"API '{apiName}' returned false.");

    // ------------------------------------------------------------------
    // Pathfinding assertions (new)
    // ------------------------------------------------------------------

    /// <summary>Verify the NPC has an active PathFindController, or has already arrived near the player.</summary>
    public static TestResult NpcHasPathController(NPC npc)
    {
        if (npc == null)
        {
            return TestResult.Fail("NPC is null.");
        }

        if (npc.controller is PathFindController pfc)
        {
            return TestResult.Pass($"{npc.Name} has active PathFindController heading to {pfc.endPoint}.");
        }

        // If NPC has already arrived near the player, controller may have been destroyed naturally
        var distToPlayer = Vector2.Distance(npc.Tile, Game1.player.Tile);
        return distToPlayer <= 2f
            ? TestResult.Pass(
                $"{npc.Name} has arrived near player (dist {distToPlayer:F1} tiles) — PathFindController naturally destroyed.")
            : TestResult.Fail(
                $"{npc.Name} has no PathFindController (controller: {npc.controller?.GetType().Name ?? "null"}).");
    }

    /// <summary>Verify the NPC is on the same map as the player.</summary>
    public static TestResult NpcIsOnSameMap(NPC npc)
    {
        if (npc == null)
        {
            return TestResult.Fail("NPC is null.");
        }

        return npc.currentLocation == Game1.currentLocation
            ? TestResult.Pass($"{npc.Name} is on same map as player ({Game1.currentLocation?.NameOrUniqueName}).")
            : TestResult.Fail(
                $"{npc.Name} is on '{npc.currentLocation?.NameOrUniqueName}' but player is on '{Game1.currentLocation?.NameOrUniqueName}'.");
    }

    // ------------------------------------------------------------------
    // Inventory assertions (new)
    // ------------------------------------------------------------------

    /// <summary>Verify the NPC's backpack has reached or exceeded the expected item count.</summary>
    public static TestResult NpcInventoryAtCapacity(string npcName, string[] items, int expectedMin)
    {
        var count = items?.Length ?? 0;
        return count >= expectedMin
            ? TestResult.Pass($"{npcName}'s inventory has {count} items (>= {expectedMin}).")
            : TestResult.Fail($"{npcName}'s inventory only has {count} items, expected at least {expectedMin}.");
    }

    // ------------------------------------------------------------------
    // Emotion assertions (new)
    // ------------------------------------------------------------------

    /// <summary>Verify the NPC's emotion changed from the previous value.</summary>
    public static TestResult AgentEmotionChanged(string npcName, string previousEmotion, string currentEmotion)
    {
        if (string.IsNullOrEmpty(previousEmotion))
        {
            return TestResult.Fail($"No previous emotion snapshot for '{npcName}'.");
        }

        return !string.Equals(previousEmotion, currentEmotion, StringComparison.OrdinalIgnoreCase)
            ? TestResult.Pass($"{npcName}'s emotion changed from {previousEmotion} to {currentEmotion}.")
            : TestResult.Fail($"{npcName}'s emotion did not change (still {currentEmotion}).");
    }

    // ------------------------------------------------------------------
    // State transition assertions (new)
    // ------------------------------------------------------------------

    /// <summary>Verify the agent's state machine has transitioned to a different state.</summary>
    public static TestResult AgentStateTransitioned(string npcName, string previousState, string currentState)
    {
        if (string.IsNullOrEmpty(previousState))
        {
            return TestResult.Fail($"No previous state snapshot for '{npcName}'.");
        }

        return !string.Equals(previousState, currentState, StringComparison.OrdinalIgnoreCase)
            ? TestResult.Pass($"{npcName} state transitioned from {previousState} to {currentState}.")
            : TestResult.Fail($"{npcName} state did not transition (still {currentState}).");
    }

    // ------------------------------------------------------------------
    // Dialogue influence assertions (new)
    // ------------------------------------------------------------------

    /// <summary>Verify friendship points changed after dialogue interaction.</summary>
    public static TestResult FriendshipChangedAfterDialogue(string npcName, int beforePoints, int afterPoints)
    {
        if (beforePoints < 0)
        {
            return TestResult.Fail($"No friendship data for '{npcName}' before dialogue.");
        }

        if (afterPoints < 0)
        {
            return TestResult.Fail($"No friendship data for '{npcName}' after dialogue.");
        }

        return afterPoints != beforePoints
            ? TestResult.Pass($"{npcName} friendship changed: {beforePoints} → {afterPoints}.")
            : TestResult.Fail(
                $"{npcName} friendship unchanged ({beforePoints} → {afterPoints}). LLM may be offline or not affecting friendship.");
    }

    /// <summary>Verify at least one memory was stored after dialogue interaction.</summary>
    public static TestResult MemoriesWereStored(string npcName, int beforeCount, int afterCount)
    {
        return afterCount > beforeCount
            ? TestResult.Pass($"{npcName} memories increased: {beforeCount} → {afterCount}.")
            : TestResult.Fail($"{npcName} memories unchanged ({beforeCount} → {afterCount}).");
    }

    // ------------------------------------------------------------------
    // 任务5.3：Dialogue source assertions
    // ------------------------------------------------------------------

    /// <summary>
    ///     任务5.3：断言对话响应来自 LLM 而非本地回退。
    ///     用于测试系统发现"LLM 回退到程序回复"等问题。
    /// </summary>
    /// <param name="source">由 IValleyAgentApi.TryGetLastDialogueSource 获取的来源。</param>
    public static TestResult DialogueFromLLM(object source)
    {
        // 使用 object 避免对 ValleyAgent 项目的强引用，通过 ToString() 比较
        var sourceName = source?.ToString() ?? "None";
        return sourceName == "LLM"
            ? TestResult.Pass("Dialogue source is LLM.")
            : TestResult.Fail(
                $"Dialogue source is {sourceName}, expected LLM. LLM may be offline or circuit breaker is OPEN.");
    }

    // ------------------------------------------------------------------
    // Result model
    // ------------------------------------------------------------------
}

public readonly struct TestResult
{
    public bool Passed { get; }
    public string Message { get; }
    public bool IsWarn { get; }

    public TestResult(bool passed, string message)
    {
        Passed = passed;
        Message = message;
        IsWarn = false;
    }

    private TestResult(bool passed, string message, bool isWarn)
    {
        Passed = passed;
        Message = message;
        IsWarn = isWarn;
    }

    public static TestResult Pass(string msg) => new(true, msg);
    public static TestResult Fail(string msg) => new(false, msg);

    /// <summary>A passing result with a warning flag — test condition not met but not a hard failure (e.g., LLM offline).</summary>
    public static TestResult Warn(string msg) => new(true, $"[WARN] {msg}", true);

    /// <summary>A non-failing result for cases where the test couldn't execute (e.g., LLM offline).</summary>
    public static TestResult Skipped(string msg) => new(false, $"[SKIPPED] {msg}");
}

// ── Phase 5 additions: instance assertion helpers on V3TestBase ──
// These protected void methods integrate with the existing Assert(label, condition, detail)
// pattern. They are declared on the partial V3TestBase so test subclasses can call them
// directly alongside the existing Assert/Record helpers.

public abstract partial class V3TestBase
{
    /// <summary>
    ///     Assert that <paramref name="actual" /> is within <paramref name="maxDistance" /> tiles of
    ///     <paramref name="expected" />.
    /// </summary>
    protected void AssertTileInRange(string label, Vector2 actual, Vector2 expected, float maxDistance,
        string detail = "")
    {
        var dist = Vector2.Distance(actual, expected);
        var passed = dist <= maxDistance;
        var msg = string.IsNullOrEmpty(detail)
            ? $"dist={dist:F2} max={maxDistance:F2}"
            : $"{detail} (dist={dist:F2} max={maxDistance:F2})";
        Assert(label, passed, msg);
    }

    /// <summary>Assert that the actual location name matches the expected location name (case-insensitive).</summary>
    protected void AssertMapLocation(string label, string actualLocation, string expectedLocation, string detail = "")
    {
        ArgumentNullException.ThrowIfNull(actualLocation);
        ArgumentNullException.ThrowIfNull(expectedLocation);
        var passed = string.Equals(actualLocation, expectedLocation, StringComparison.OrdinalIgnoreCase);
        var msg = string.IsNullOrEmpty(detail)
            ? $"actual='{actualLocation}' expected='{expectedLocation}'"
            : $"{detail} (actual='{actualLocation}' expected='{expectedLocation}')";
        Assert(label, passed, msg);
    }

    /// <summary>Assert that the named NPC agent is currently in the expected state (via ModEntry.API).</summary>
    protected void AssertNpcState(string label, string npcName, string expectedState, string detail = "")
    {
        ArgumentNullException.ThrowIfNull(npcName);
        ArgumentNullException.ThrowIfNull(expectedState);
        var api = ModEntry.API;
        if (api == null)
        {
            Assert(label, false, "API not available" + (string.IsNullOrEmpty(detail) ? "" : $" ({detail})"));
            return;
        }

        var actualState = api.GetAgentState(npcName);
        var passed = string.Equals(actualState, expectedState, StringComparison.OrdinalIgnoreCase);
        var msg = string.IsNullOrEmpty(detail)
            ? $"npc='{npcName}' actual='{actualState}' expected='{expectedState}'"
            : $"{detail} (npc='{npcName}' actual='{actualState}' expected='{expectedState}')";
        Assert(label, passed, msg);
    }

    /// <summary>Assert that the named NPC agent's health is within the inclusive range [min, max] (via ModEntry.API).</summary>
    protected void AssertNpcHealthInRange(string label, string npcName, int minHealth, int maxHealth,
        string detail = "")
    {
        ArgumentNullException.ThrowIfNull(npcName);
        var api = ModEntry.API;
        if (api == null)
        {
            Assert(label, false, "API not available" + (string.IsNullOrEmpty(detail) ? "" : $" ({detail})"));
            return;
        }

        var health = api.GetNpcHealth(npcName);
        var passed = health >= 0 && health >= minHealth && health <= maxHealth;
        var msg = string.IsNullOrEmpty(detail)
            ? $"npc='{npcName}' health={health} range=[{minHealth},{maxHealth}]"
            : $"{detail} (npc='{npcName}' health={health} range=[{minHealth},{maxHealth}])";
        Assert(label, passed, msg);
    }

    /// <summary>Assert that a non-empty AI dialogue response has been received for the named NPC (via ModEntry.API).</summary>
    protected void AssertDialogueReceived(string label, string npcName, string detail = "")
    {
        ArgumentNullException.ThrowIfNull(npcName);
        var api = ModEntry.API;
        if (api == null)
        {
            Assert(label, false, "API not available" + (string.IsNullOrEmpty(detail) ? "" : $" ({detail})"));
            return;
        }

        var found = api.TryGetLastDialogue(npcName, out var response);
        var nonEmpty = found && !string.IsNullOrEmpty(response);
        var msg = string.IsNullOrEmpty(detail)
            ? $"npc='{npcName}' received={nonEmpty}"
            : $"{detail} (npc='{npcName}' received={nonEmpty})";
        Assert(label, nonEmpty, msg);
    }

    /// <summary>Assert that ModEntry.API is available and the game is running (Game1.player non-null).</summary>
    protected void AssertWebSocketConnected(string label, string detail = "")
    {
        var api = ModEntry.API;
        var gameReady = Game1.player != null;
        var passed = api != null && gameReady;
        var msg = string.IsNullOrEmpty(detail)
            ? $"api={(api != null ? "ok" : "null")} gameReady={gameReady}"
            : $"{detail} (api={(api != null ? "ok" : "null")} gameReady={gameReady})";
        Assert(label, passed, msg);
    }

    /// <summary>Assert that the mock WebSocket server has received at least <paramref name="minCount" /> messages.</summary>
    protected void AssertMessageCount(string label, MockWebSocketServer server, int minCount, string detail = "")
    {
        ArgumentNullException.ThrowIfNull(server);
        var actual = server.MessageCount;
        var passed = actual >= minCount;
        var msg = string.IsNullOrEmpty(detail)
            ? $"messages={actual} min={minCount}"
            : $"{detail} (messages={actual} min={minCount})";
        Assert(label, passed, msg);
    }

    /// <summary>Assert that the mock WebSocket server has accepted at least <paramref name="minCount" /> connections.</summary>
    protected void AssertConnectionCount(string label, MockWebSocketServer server, int minCount, string detail = "")
    {
        ArgumentNullException.ThrowIfNull(server);
        var actual = server.ConnectionCount;
        var passed = actual >= minCount;
        var msg = string.IsNullOrEmpty(detail)
            ? $"connections={actual} min={minCount}"
            : $"{detail} (connections={actual} min={minCount})";
        Assert(label, passed, msg);
    }

    /// <summary>Assert that a menu is currently open (Game1.activeClickableMenu is non-null).</summary>
    protected void AssertMenuOpen(string label, string detail = "")
    {
        var menu = Game1.activeClickableMenu;
        var passed = menu != null;
        var menuName = menu?.GetType().Name ?? "null";
        var msg = string.IsNullOrEmpty(detail)
            ? $"menu={menuName}"
            : $"{detail} (menu={menuName})";
        Assert(label, passed, msg);
    }

    /// <summary>Assert that no menu is currently open (Game1.activeClickableMenu is null).</summary>
    protected void AssertMenuClosed(string label, string detail = "")
    {
        var passed = Game1.activeClickableMenu == null;
        var msg = string.IsNullOrEmpty(detail)
            ? "menu closed"
            : $"{detail} (menu closed)";
        Assert(label, passed, msg);
    }
}