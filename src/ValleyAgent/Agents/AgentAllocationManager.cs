using System;
using System.Collections.Generic;
using System.Linq;
using ValleyAgent.StateMachine;

namespace ValleyAgent.Agents;

/// <summary>
///     Manages which NPCs are allocated as AI Agents.
///     Features:
///     - Concurrent Agent count maintained within a configurable [MinAgents, MaxAgents] range
///     - Priority-based allocation using conversation frequency + gift frequency + friendship level
///     - Manual override support for player-forced allocations
///     - Graceful degradation: lowest-priority Agent is deallocated when max is exceeded
///     - Reevaluation never trims below MinAgents (guaranteed floor)
///     - Thread-safe operations
///     - Game-agnostic design (no SMAPI dependencies)
/// </summary>
public class AgentAllocationManager
{
    private readonly Dictionary<string, AgentAllocationInfo> _allocations;
    private readonly object _lock = new();

    /// <summary>
    ///     Creates a new AgentAllocationManager.
    /// </summary>
    /// <param name="minAgents">Minimum concurrent Agents (0 .. maxAgents). Defaults to 0.</param>
    /// <param name="maxAgents">Maximum concurrent Agents (minAgents .. 10). Defaults to 2.</param>
    public AgentAllocationManager(int minAgents = 0, int maxAgents = 2)
    {
        if (minAgents < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minAgents), "MinAgents cannot be negative.");
        }

        if (maxAgents < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAgents), "MaxAgents cannot be negative.");
        }

        if (minAgents > maxAgents)
        {
            throw new ArgumentOutOfRangeException(nameof(minAgents), "MinAgents cannot exceed MaxAgents.");
        }

        if (maxAgents > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAgents), "MaxAgents cannot exceed 10.");
        }

        MinAgents = minAgents;
        MaxAgents = maxAgents;
        _allocations = new Dictionary<string, AgentAllocationInfo>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     The minimum number of concurrent Agents the auto-allocator should maintain.
    ///     Auto-allocation is expected to top up to at least this count when candidates exist.
    /// </summary>
    public int MinAgents { get; }

    /// <summary>
    ///     The maximum number of concurrent Agents allowed.
    /// </summary>
    public int MaxAgents { get; }

    /// <summary>
    ///     The current number of allocated Agents.
    /// </summary>
    public int CurrentAgentCount
    {
        get
        {
            lock (_lock)
            {
                return _allocations.Count;
            }
        }
    }

    /// <summary>
    ///     Returns a snapshot of currently allocated NPC names.
    /// </summary>
    public IReadOnlyList<string> AllocatedAgentNames
    {
        get
        {
            lock (_lock)
            {
                return _allocations.Keys.ToList().AsReadOnly();
            }
        }
    }

    /// <summary>
    ///     Fired when an NPC is allocated as an Agent.
    /// </summary>
    public event EventHandler<AgentAllocationEventArgs>? OnAgentAllocated;

    /// <summary>
    ///     Fired when an NPC loses its Agent allocation.
    /// </summary>
    public event EventHandler<AgentAllocationEventArgs>? OnAgentDeallocated;

    /// <summary>
    ///     Attempts to allocate an NPC as an Agent based on priority.
    ///     If max Agents is reached, the lowest-priority non-manual Agent is deallocated.
    /// </summary>
    /// <param name="npcName">The NPC name.</param>
    /// <param name="conversationFrequency">How often the player talks to this NPC.</param>
    /// <param name="giftFrequency">How often the player gifts this NPC.</param>
    /// <param name="friendshipLevel">Current friendship level with this NPC.</param>
    /// <returns>True if the NPC was allocated (or already allocated), false otherwise.</returns>
    public bool TryAllocate(string npcName, double conversationFrequency, double giftFrequency, double friendshipLevel)
    {
        if (string.IsNullOrWhiteSpace(npcName))
        {
            throw new ArgumentException("NPC name cannot be null or empty.", nameof(npcName));
        }

        lock (_lock)
        {
            // Already allocated
            if (_allocations.TryGetValue(npcName, out var existing))
            {
                // Update priority metrics even if already allocated
                existing.ConversationFrequency = conversationFrequency;
                existing.GiftFrequency = giftFrequency;
                existing.FriendshipLevel = friendshipLevel;
                existing.LastUpdated = DateTime.UtcNow;
                return true;
            }

            // Calculate priority score
            var priorityScore = CalculatePriority(conversationFrequency, giftFrequency, friendshipLevel);

            // If we have room, allocate immediately
            if (_allocations.Count < MaxAgents)
            {
                var info = new AgentAllocationInfo(npcName, conversationFrequency, giftFrequency, friendshipLevel,
                    priorityScore);
                _allocations[npcName] = info;
                OnAgentAllocated?.Invoke(this,
                    new AgentAllocationEventArgs(npcName, AgentState.IDLE, false, "Priority-based allocation"));
                return true;
            }

            // No room - find lowest-priority non-manual Agent to replace
            var candidate = _allocations
                .Where(IsForceReplaceable)
                .OrderBy(kvp => kvp.Value.PriorityScore)
                .ThenBy(kvp => kvp.Value.LastUpdated)
                .FirstOrDefault();

            if (candidate.Key == null)
            {
                // All current Agents are manual overrides, cannot replace
                return false;
            }

            if (candidate.Value.PriorityScore >= priorityScore)
            {
                // Current lowest-priority Agent has higher or equal priority than candidate
                return false;
            }

            // Deallocate the lowest-priority Agent
            var removedName = candidate.Key;
            var removedState = candidate.Value.CurrentState;
            _ = _allocations.Remove(removedName);
            OnAgentDeallocated?.Invoke(this,
                new AgentAllocationEventArgs(removedName, removedState, false, "Replaced by higher-priority NPC"));

            // Allocate the new Agent
            var newInfo = new AgentAllocationInfo(npcName, conversationFrequency, giftFrequency, friendshipLevel,
                priorityScore);
            _allocations[npcName] = newInfo;
            OnAgentAllocated?.Invoke(this,
                new AgentAllocationEventArgs(npcName, AgentState.IDLE, false,
                    "Priority-based allocation (replaced lower-priority Agent)"));
            return true;
        }
    }

    /// <summary>
    ///     可被强制替换的槽位候选：非手动覆盖，且不在导演 KeepUntil 豁免期内。
    ///     2026-08-23 审计 P1：KeepUntil 此前只豁免空闲淘汰——beat 进行中的 NPC 仍会被
    ///     优先级替换/ForceAllocate 挤掉，导致导演编排中断；这里与 IdleEviction 对齐。
    /// </summary>
    private static bool IsForceReplaceable(KeyValuePair<string, AgentAllocationInfo> kvp)
    {
        var info = kvp.Value;
        return !info.IsManuallyOverridden
            && !(info.KeepUntil.HasValue && DateTime.UtcNow < info.KeepUntil.Value);
    }

    /// <summary>
    ///     Manually forces an NPC to be allocated as an Agent.
    ///     If max is exceeded, the lowest-priority non-manual Agent is deallocated.
    /// </summary>
    /// <param name="npcName">The NPC name to force-allocate.</param>
    /// <returns>True if allocated (or already allocated as manual), false if cannot allocate.</returns>
    public bool ForceAllocate(string npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName))
        {
            throw new ArgumentException("NPC name cannot be null or empty.", nameof(npcName));
        }

        lock (_lock)
        {
            // Already allocated as manual
            if (_allocations.TryGetValue(npcName, out var existing))
            {
                if (!existing.IsManuallyOverridden)
                {
                    existing.IsManuallyOverridden = true;
                    existing.LastUpdated = DateTime.UtcNow;
                }

                return true;
            }

            // If we have room, allocate immediately
            if (_allocations.Count < MaxAgents)
            {
                var info = new AgentAllocationInfo(npcName, 0, 0, 0, 0)
                {
                    IsManuallyOverridden = true
                };
                _allocations[npcName] = info;
                OnAgentAllocated?.Invoke(this,
                    new AgentAllocationEventArgs(npcName, AgentState.IDLE, true, "Manual override allocation"));
                return true;
            }

            // No room - find lowest-priority non-manual Agent to replace
            var candidate = _allocations
                .Where(IsForceReplaceable)
                .OrderBy(kvp => kvp.Value.PriorityScore)
                .ThenBy(kvp => kvp.Value.LastUpdated)
                .FirstOrDefault();

            if (candidate.Key == null)
            {
                // All current Agents are manual overrides, cannot replace
                return false;
            }

            // Deallocate the lowest-priority Agent
            var removedName = candidate.Key;
            var removedState = candidate.Value.CurrentState;
            _ = _allocations.Remove(removedName);
            OnAgentDeallocated?.Invoke(this,
                new AgentAllocationEventArgs(removedName, removedState, false, "Replaced by manual override"));

            // Allocate the manually overridden Agent
            var newInfo = new AgentAllocationInfo(npcName, 0, 0, 0, 0)
            {
                IsManuallyOverridden = true
            };
            _allocations[npcName] = newInfo;
            OnAgentAllocated?.Invoke(this,
                new AgentAllocationEventArgs(npcName, AgentState.IDLE, true,
                    "Manual override allocation (replaced lower-priority Agent)"));
            return true;
        }
    }

    /// <summary>
    ///     Releases a manual override, allowing the NPC to be deallocated by priority rules.
    /// </summary>
    /// <param name="npcName">The NPC name.</param>
    /// <returns>True if the manual override was removed, false if not found or not manual.</returns>
    public bool ReleaseManualOverride(string npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName))
        {
            throw new ArgumentException("NPC name cannot be null or empty.", nameof(npcName));
        }

        lock (_lock)
        {
            if (!_allocations.TryGetValue(npcName, out var info))
            {
                return false;
            }

            if (!info.IsManuallyOverridden)
            {
                return false;
            }

            info.IsManuallyOverridden = false;
            info.LastUpdated = DateTime.UtcNow;
            return true;
        }
    }

    /// <summary>
    ///     Deallocates an NPC, removing its Agent status.
    /// </summary>
    /// <param name="npcName">The NPC name to deallocate.</param>
    /// <returns>True if deallocated, false if not found.</returns>
    public bool Deallocate(string npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName))
        {
            throw new ArgumentException("NPC name cannot be null or empty.", nameof(npcName));
        }

        lock (_lock)
        {
            if (!_allocations.TryGetValue(npcName, out var info))
            {
                return false;
            }

            var state = info.CurrentState;
            var wasManual = info.IsManuallyOverridden;
            _ = _allocations.Remove(npcName);
            OnAgentDeallocated?.Invoke(this,
                new AgentAllocationEventArgs(npcName, state, wasManual, "Explicit deallocation"));
            return true;
        }
    }

    /// <summary>
    ///     Updates the priority metrics for an allocated NPC.
    ///     If the NPC is not allocated, this has no effect.
    /// </summary>
    /// <param name="npcName">The NPC name.</param>
    /// <param name="conversationFrequency">Updated conversation frequency.</param>
    /// <param name="giftFrequency">Updated gift frequency.</param>
    /// <param name="friendshipLevel">Updated friendship level.</param>
    /// <returns>True if the NPC was found and updated, false otherwise.</returns>
    public bool UpdatePriority(string npcName, double conversationFrequency, double giftFrequency,
        double friendshipLevel)
    {
        if (string.IsNullOrWhiteSpace(npcName))
        {
            throw new ArgumentException("NPC name cannot be null or empty.", nameof(npcName));
        }

        lock (_lock)
        {
            if (!_allocations.TryGetValue(npcName, out var info))
            {
                return false;
            }

            info.ConversationFrequency = conversationFrequency;
            info.GiftFrequency = giftFrequency;
            info.FriendshipLevel = friendshipLevel;
            info.PriorityScore = CalculatePriority(conversationFrequency, giftFrequency, friendshipLevel);
            info.LastUpdated = DateTime.UtcNow;
            return true;
        }
    }

    /// <summary>
    ///     Sets the current AgentState for an allocated NPC.
    /// </summary>
    /// <param name="npcName">The NPC name.</param>
    /// <param name="state">The new state.</param>
    /// <returns>True if the state was set, false if NPC is not allocated.</returns>
    public bool SetAgentState(string npcName, AgentState state)
    {
        if (string.IsNullOrWhiteSpace(npcName))
        {
            throw new ArgumentException("NPC name cannot be null or empty.", nameof(npcName));
        }

        lock (_lock)
        {
            if (!_allocations.TryGetValue(npcName, out var info))
            {
                return false;
            }

            info.CurrentState = state;
            info.LastUpdated = DateTime.UtcNow;
            return true;
        }
    }

    /// <summary>
    ///     Gets the current AgentState for an allocated NPC.
    /// </summary>
    /// <param name="npcName">The NPC name.</param>
    /// <param name="state">The current state, if found.</param>
    /// <returns>True if the NPC is allocated and state was retrieved.</returns>
    public bool GetAgentState(string npcName, out AgentState state)
    {
        if (string.IsNullOrWhiteSpace(npcName))
        {
            throw new ArgumentException("NPC name cannot be null or empty.", nameof(npcName));
        }

        lock (_lock)
        {
            if (_allocations.TryGetValue(npcName, out var info))
            {
                state = info.CurrentState;
                return true;
            }

            state = AgentState.IDLE;
            return false;
        }
    }

    /// <summary>
    ///     Checks if an NPC is currently allocated as an Agent.
    /// </summary>
    /// <param name="npcName">The NPC name.</param>
    /// <returns>True if allocated, false otherwise.</returns>
    public bool IsAllocated(string npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName))
        {
            return false;
        }

        lock (_lock)
        {
            return _allocations.ContainsKey(npcName);
        }
    }

    /// <summary>
    ///     Checks if an NPC allocation is a manual override.
    /// </summary>
    /// <param name="npcName">The NPC name.</param>
    /// <returns>True if allocated and manually overridden, false otherwise.</returns>
    public bool IsManuallyOverridden(string npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName))
        {
            return false;
        }

        lock (_lock)
        {
            return _allocations.TryGetValue(npcName, out var info) && info.IsManuallyOverridden;
        }
    }

    /// <summary>
    ///     Gets a snapshot of all currently allocated Agents with their info.
    /// </summary>
    /// <returns>List of allocated Agent information.</returns>
    public IReadOnlyList<AgentAllocationInfo> GetAllAllocatedAgents()
    {
        lock (_lock)
        {
            return _allocations.Values.ToList().AsReadOnly();
        }
    }

    public List<string> GetAllocatedNames()
    {
        lock (_lock)
        {
            return _allocations.Keys.ToList();
        }
    }

    public Dictionary<string, bool> GetAllManualOverrides()
    {
        lock (_lock)
        {
            return _allocations
                .Where(kvp => kvp.Value.IsManuallyOverridden)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.IsManuallyOverridden, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    ///     Clears all allocations.
    /// </summary>
    public void ClearAllAllocations()
    {
        lock (_lock)
        {
            foreach (var kvp in _allocations)
            {
                OnAgentDeallocated?.Invoke(this, new AgentAllocationEventArgs(
                    kvp.Key, kvp.Value.CurrentState, kvp.Value.IsManuallyOverridden, "Clear all allocations"));
            }

            _allocations.Clear();
        }
    }

    /// <summary>
    ///     Re-evaluates all allocations and deallocates any non-manual Agents
    ///     that no longer meet priority requirements if the pool is over capacity.
    ///     The trim target is <see cref="MaxAgents" />; since <c>MinAgents &lt;= MaxAgents</c>
    ///     is enforced by the constructor, the floor is implicitly preserved.
    ///     This can be called periodically to clean up stale allocations.
    /// </summary>
    public void ReevaluateAllocations()
    {
        lock (_lock)
        {
            if (_allocations.Count <= MaxAgents)
            {
                return;
            }

            // Sort by priority ascending, manual overrides last
            var sorted = _allocations
                .OrderBy(kvp => kvp.Value.IsManuallyOverridden ? 1 : 0)
                .ThenBy(kvp => kvp.Value.PriorityScore)
                .ThenBy(kvp => kvp.Value.LastUpdated)
                .ToList();

            var toRemove = sorted.Take(_allocations.Count - MaxAgents);

            foreach (var kvp in toRemove)
            {
                _ = _allocations.Remove(kvp.Key);
                OnAgentDeallocated?.Invoke(this, new AgentAllocationEventArgs(
                    kvp.Key, kvp.Value.CurrentState, kvp.Value.IsManuallyOverridden,
                    "Reevaluation: exceeded max Agents"));
            }
        }
    }

    /// <summary>
    ///     Calculates the priority score from interaction metrics.
    ///     Higher score = higher priority for allocation.
    /// </summary>
    private static double CalculatePriority(double conversationFrequency, double giftFrequency,
        double friendshipLevel) =>
        // Simple additive strategy: all metrics weighted equally
        // Can be adjusted later with configurable weights
        conversationFrequency + giftFrequency + friendshipLevel;
}