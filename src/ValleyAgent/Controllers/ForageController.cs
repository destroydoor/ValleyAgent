using System;
using System.Collections.Generic;
using System.Linq;
using ValleyAgent.StateMachine;

namespace ValleyAgent.Controllers;

/// <summary>
///     Represents a position on the game map in tile coordinates.
/// </summary>
public readonly record struct TilePosition(int X, int Y)
{
    /// <summary>
    ///     Calculates the Euclidean distance in tiles to another position.
    /// </summary>
    public int DistanceTo(TilePosition other)
    {
        var dx = X - other.X;
        var dy = Y - other.Y;
        return (int)Math.Sqrt(dx * dx + dy * dy);
    }
}

/// <summary>
///     Represents a forageable item in the game world.
///     Game-agnostic: uses string identifiers, not SMAPI types.
/// </summary>
public class ForageableItem
{
    /// <summary>Unique identifier for this item instance.</summary>
    public string InstanceId { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>Item type identifier (e.g., "WildHorseradish", "Salmonberry").</summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>Display name of the item.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     Category of forageable: "GroundItem", "BerryBush", or "MushroomTree".
    /// </summary>
    public string ForageType { get; set; } = string.Empty;

    /// <summary>Position in the game world.</summary>
    public TilePosition Position { get; set; }

    /// <summary>Seasons this item can be foraged (empty = all seasons).</summary>
    public List<string> ValidSeasons { get; set; } = new();

    /// <summary>Whether this was placed by a player (should not be collected).</summary>
    public bool IsPlayerPlaced { get; set; }

    /// <summary>Whether this is a quest item (should not be collected).</summary>
    public bool IsQuestItem { get; set; }

    /// <summary>Whether this is a museum piece (should not be collected).</summary>
    public bool IsMuseumPiece { get; set; }
}

/// <summary>
///     NPC foraging behavior controller. Handles the <see cref="AgentState.FORAGE" /> state.
///     Game-agnostic: uses abstract types and delegates for world interaction.
///     The game layer provides callbacks for scanning, movement, and season queries.
///     Behavior:
///     - On <see cref="Start" />: Begins scanning the area for forageable items.
///     - On <see cref="Update" />: Finds the nearest eligible forageable, moves to it,
///     simulates pickup (configurable duration), and adds it to a virtual inventory.
///     - Stops after collecting the maximum number of items or reaching the time limit.
///     - Fires <see cref="OnActionFailed" /> if no forageables are found within the search timeout.
/// </summary>
public class ForageController : IAgentController
{
    private readonly Func<TilePosition> _getAgentPosition;
    private readonly Func<string> _getCurrentSeason;
    private readonly List<ForageableItem> _inventory;
    private readonly Func<TilePosition, bool> _isInDesignatedArea;

    private readonly int _maxItemsPerSession;

    // --- Configuration ---
    private readonly int _maxRangeTiles;
    private readonly int _maxSessionTicks;
    private readonly int _pickupDurationTicks;

    // --- Game layer delegates ---
    private readonly Func<IEnumerable<ForageableItem>> _scanForageables;
    private readonly int _searchTimeoutTicks;
    private readonly Func<TilePosition, bool> _tryMoveTo;
    private ForageableItem? _currentTarget;
    private int _elapsedTicks;
    private bool _hasFiredCompletedEvent;
    private bool _hasFiredFailedEvent;
    private bool _hasFiredStartedEvent;

    private ForagePhase _phase;
    private int _ticksInCurrentPhase;

    /// <summary>
    ///     Creates a new <see cref="ForageController" />.
    /// </summary>
    /// <param name="scanForageables">
    ///     Callback to scan the world for forageable items. Called each tick while searching.
    /// </param>
    /// <param name="getAgentPosition">Callback to get the agent's current tile position.</param>
    /// <param name="tryMoveTo">
    ///     Callback to move the agent toward a target tile. Returns <c>true</c> when the agent has arrived.
    /// </param>
    /// <param name="getCurrentSeason">Callback to get the current season name (e.g., "Spring").</param>
    /// <param name="isInDesignatedArea">
    ///     Callback to check whether a tile position is within the allowed foraging area.
    /// </param>
    /// <param name="maxRangeTiles">
    ///     Maximum distance in tiles to search for forageables. Default is 15.
    /// </param>
    /// <param name="maxItemsPerSession">
    ///     Maximum number of items to collect before auto-completing. Default is 5.
    /// </param>
    /// <param name="maxSessionTicks">
    ///     Maximum duration of a foraging session in game ticks. Default is 2520 (~1 in-game hour at 60 ticks/sec).
    /// </param>
    /// <param name="pickupDurationTicks">
    ///     Number of ticks to simulate item pickup. Default is 60 (~1 second at 60 ticks/sec).
    /// </param>
    /// <param name="searchTimeoutTicks">
    ///     Number of ticks to wait before failing if no forageables are found. Default is 600 (~10 seconds).
    /// </param>
    public ForageController(
        Func<IEnumerable<ForageableItem>> scanForageables,
        Func<TilePosition> getAgentPosition,
        Func<TilePosition, bool> tryMoveTo,
        Func<string> getCurrentSeason,
        Func<TilePosition, bool> isInDesignatedArea,
        int maxRangeTiles = 15,
        int maxItemsPerSession = 5,
        int maxSessionTicks = 2520,
        int pickupDurationTicks = 60,
        int searchTimeoutTicks = 600)
    {
        _scanForageables = scanForageables ?? throw new ArgumentNullException(nameof(scanForageables));
        _getAgentPosition = getAgentPosition ?? throw new ArgumentNullException(nameof(getAgentPosition));
        _tryMoveTo = tryMoveTo ?? throw new ArgumentNullException(nameof(tryMoveTo));
        _getCurrentSeason = getCurrentSeason ?? throw new ArgumentNullException(nameof(getCurrentSeason));
        _isInDesignatedArea = isInDesignatedArea ?? throw new ArgumentNullException(nameof(isInDesignatedArea));

        _maxRangeTiles = maxRangeTiles;
        _maxItemsPerSession = maxItemsPerSession;
        _maxSessionTicks = maxSessionTicks;
        _pickupDurationTicks = pickupDurationTicks;
        _searchTimeoutTicks = searchTimeoutTicks;

        _inventory = new List<ForageableItem>();
        _phase = ForagePhase.Idle;
    }

    /// <summary>
    ///     Read-only view of the virtual inventory (items collected during the current or last session).
    /// </summary>
    public IReadOnlyList<ForageableItem> Inventory
    {
        get => _inventory.AsReadOnly();
    }

    /// <summary>
    ///     Number of items collected in the current session.
    /// </summary>
    public int ItemsCollected { get; private set; }

    // --- IAgentController Implementation ---

    /// <inheritdoc />
    public string ControllerName
    {
        get => "ForageController";
    }

    /// <inheritdoc />
    public bool IsActive
    {
        get => _phase is not ForagePhase.Idle and not ForagePhase.Completed and not ForagePhase.Failed;
    }

    /// <inheritdoc />
    public event EventHandler<string>? OnActionStarted;

    /// <inheritdoc />
    public event EventHandler<string>? OnActionCompleted;

    /// <inheritdoc />
    public event EventHandler<string>? OnActionFailed;

    /// <inheritdoc />
    public bool CanHandle(AgentState state) => state == AgentState.FORAGE;

    /// <summary>
    ///     Starts the foraging session. Clears any previous inventory and begins scanning.
    /// </summary>
    public void Start()
    {
        if (_phase != ForagePhase.Idle)
        {
            Stop();
        }

        _inventory.Clear();
        _currentTarget = null;
        _elapsedTicks = 0;
        _ticksInCurrentPhase = 0;
        ItemsCollected = 0;
        _hasFiredStartedEvent = false;
        _hasFiredCompletedEvent = false;
        _hasFiredFailedEvent = false;

        _phase = ForagePhase.Searching;
    }

    /// <summary>
    ///     Stops the controller, clears the current target, and returns to idle.
    /// </summary>
    public void Stop()
    {
        _phase = ForagePhase.Idle;
        _currentTarget = null;
        _ticksInCurrentPhase = 0;
    }

    /// <summary>
    ///     Called every game tick while this controller is active.
    ///     Manages the foraging state machine: Searching -> Moving -> PickingUp -> repeat.
    /// </summary>
    /// <param name="ticks">Number of ticks since the game started.</param>
    public void Update(int ticks)
    {
        if (!IsActive)
        {
            return;
        }

        _elapsedTicks++;

        // Fire started event on the first active update
        if (!_hasFiredStartedEvent)
        {
            _hasFiredStartedEvent = true;
            OnActionStarted?.Invoke(this, "Foraging session started");
        }

        // Check session time limit
        if (_elapsedTicks >= _maxSessionTicks)
        {
            CompleteSession("Time limit reached");
            return;
        }

        switch (_phase)
        {
            case ForagePhase.Searching:
                UpdateSearching();
                break;
            case ForagePhase.Moving:
                UpdateMoving();
                break;
            case ForagePhase.PickingUp:
                UpdatePickingUp();
                break;
            // 非活跃阶段无需更新
            case ForagePhase.Idle:
            case ForagePhase.Completed:
            case ForagePhase.Failed:
            default:
                break;
        }
    }

    private void UpdateSearching()
    {
        _ticksInCurrentPhase++;

        // Graceful failure: no forageables found within search timeout
        if (_ticksInCurrentPhase >= _searchTimeoutTicks)
        {
            FailSession("No forageable items found within search timeout");
            return;
        }

        // Scan for forageables
        var agentPos = _getAgentPosition();
        var allForageables = _scanForageables();
        var currentSeason = _getCurrentSeason();

        var eligible = allForageables
            .Where(f => IsEligibleForageable(f, agentPos, currentSeason))
            .OrderBy(f => f.Position.DistanceTo(agentPos))
            .ToList();

        if (eligible.Count == 0)
        {
            // Still searching 鈥?will try again on the next tick
            return;
        }

        // Target acquired
        _currentTarget = eligible[0];
        _ticksInCurrentPhase = 0;
        _phase = ForagePhase.Moving;
    }

    private void UpdateMoving()
    {
        if (_currentTarget == null)
        {
            _phase = ForagePhase.Searching;
            return;
        }

        // Ensure the target has not left the designated area
        if (!_isInDesignatedArea(_currentTarget.Position))
        {
            _currentTarget = null;
            _phase = ForagePhase.Searching;
            return;
        }

        var agentPos = _getAgentPosition();
        var distance = _currentTarget.Position.DistanceTo(agentPos);

        if (distance <= 1) // Adjacent or on the same tile
        {
            _ticksInCurrentPhase = 0;
            _phase = ForagePhase.PickingUp;
            return;
        }

        var arrived = _tryMoveTo(_currentTarget.Position);
        if (arrived)
        {
            _ticksInCurrentPhase = 0;
            _phase = ForagePhase.PickingUp;
        }
    }

    private void UpdatePickingUp()
    {
        _ticksInCurrentPhase++;

        if (_ticksInCurrentPhase >= _pickupDurationTicks)
        {
            // Pickup simulation complete
            if (_currentTarget != null)
            {
                _inventory.Add(_currentTarget);
                ItemsCollected++;
            }

            _currentTarget = null;
            _ticksInCurrentPhase = 0;

            // Check if we've reached the item limit
            if (ItemsCollected >= _maxItemsPerSession)
            {
                CompleteSession($"Collected maximum of {_maxItemsPerSession} items");
            }
            else
            {
                _phase = ForagePhase.Searching;
            }
        }
    }

    /// <summary>
    ///     Determines whether a forageable item is eligible for collection.
    /// </summary>
    private bool IsEligibleForageable(ForageableItem item, TilePosition agentPos, string currentSeason)
    {
        // Must be within search range
        if (item.Position.DistanceTo(agentPos) > _maxRangeTiles)
        {
            return false;
        }

        // Must remain inside the designated area
        if (!_isInDesignatedArea(item.Position))
        {
            return false;
        }

        // Do NOT collect player-placed items
        if (item.IsPlayerPlaced)
        {
            return false;
        }

        // Do NOT collect quest items
        if (item.IsQuestItem)
        {
            return false;
        }

        // Do NOT collect museum pieces
        if (item.IsMuseumPiece)
        {
            return false;
        }

        // Season filter: only collect items valid for the current season
        if (item.ValidSeasons.Count > 0 &&
            !item.ValidSeasons.Contains(currentSeason, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        // Allowed abstract forage types
        if (item.ForageType is not "GroundItem" and
            not "BerryBush" and
            not "MushroomTree")
        {
            return false;
        }

        // Do not collect items already in our inventory this session
        return !_inventory.Any(i => i.InstanceId == item.InstanceId);
    }

    private void CompleteSession(string reason)
    {
        if (_hasFiredCompletedEvent)
        {
            return;
        }

        _hasFiredCompletedEvent = true;
        _phase = ForagePhase.Completed;

        var itemList = _inventory.Count > 0
            ? string.Join(", ", _inventory.Select(i => i.Name))
            : "none";

        var message = $"Foraging completed: {reason}. Collected {ItemsCollected} item(s): {itemList}";
        OnActionCompleted?.Invoke(this, message);
    }

    private void FailSession(string reason)
    {
        if (_hasFiredFailedEvent)
        {
            return;
        }

        _hasFiredFailedEvent = true;
        _phase = ForagePhase.Failed;

        OnActionFailed?.Invoke(this, $"Foraging failed: {reason}");
    }

    // --- Internal state ---
    private enum ForagePhase
    {
        Idle,
        Searching,
        Moving,
        PickingUp,
        Completed,
        Failed
    }
}