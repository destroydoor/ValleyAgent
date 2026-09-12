using System;
using System.Collections.Generic;
using System.Linq;
using ValleyAgent.StateMachine;

namespace ValleyAgent.Controllers;

/// <summary>
///     Represents a crop in the game world in a game-agnostic way.
/// </summary>
public sealed class FarmCrop
{
    /// <summary>
    ///     Unique identifier for this crop instance.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    ///     Display name of the crop type.
    /// </summary>
    public string CropName { get; set; } = string.Empty;

    /// <summary>
    ///     X coordinate in the game world.
    /// </summary>
    public int X { get; set; }

    /// <summary>
    ///     Y coordinate in the game world.
    /// </summary>
    public int Y { get; set; }

    /// <summary>
    ///     Whether the crop needs water.
    /// </summary>
    public bool NeedsWater { get; set; }

    /// <summary>
    ///     Whether the crop is ready to harvest.
    /// </summary>
    public bool IsMature { get; set; }

    /// <summary>
    ///     Whether the crop is in season and can grow.
    /// </summary>
    public bool IsInSeason { get; set; }

    /// <summary>
    ///     Whether the crop is a player-placed decorative item (should not be harvested).
    /// </summary>
    public bool IsDecorative { get; set; }

    /// <summary>
    ///     The name of the item produced when harvested.
    /// </summary>
    public string? HarvestItemName { get; set; }

    /// <summary>
    ///     Quantity of items produced when harvested.
    /// </summary>
    public int HarvestQuantity { get; set; } = 1;
}

/// <summary>
///     Provides game-world data to the <see cref="FarmController" /> in a game-agnostic way.
///     The game layer implements this interface to bridge between the controller and the actual game state.
/// </summary>
public interface IFarmWorldProvider
{
    /// <summary>
    ///     Gets all crops within the specified work area.
    /// </summary>
    public IEnumerable<FarmCrop> GetCropsInArea(int centerX, int centerY, int radius);

    /// <summary>
    ///     Gets the current season name (e.g., "Spring", "Summer", "Fall", "Winter").
    /// </summary>
    public string GetCurrentSeason();

    /// <summary>
    ///     Returns true if it is currently raining.
    /// </summary>
    public bool IsRaining();

    /// <summary>
    ///     Marks the specified crop as watered. Called by the controller after simulating watering.
    /// </summary>
    public void MarkCropWatered(FarmCrop crop);

    /// <summary>
    ///     Harvests the specified crop and returns the item name produced.
    ///     Called by the controller after simulating harvesting.
    /// </summary>
    public string? HarvestCrop(FarmCrop crop);

    /// <summary>
    ///     Moves the agent toward the specified position. Returns true when arrived.
    /// </summary>
    public bool MoveToward(int x, int y);
}

/// <summary>
///     NPC farm work behavior controller. Handles watering, harvesting, and idle behavior
///     within a designated work area. Game-agnostic: uses <see cref="IFarmWorldProvider" />
///     to interact with the game world without referencing SMAPI types.
/// </summary>
public sealed class FarmController : IAgentController
{
    private readonly Dictionary<string, int> _virtualInventory = new(StringComparer.OrdinalIgnoreCase);
    private readonly IFarmWorldProvider _worldProvider;
    private int _actionDurationTicks;
    private int _actionStartTick;

    private FarmAction _currentAction = FarmAction.Idle;
    private FarmCrop? _targetCrop;
    private int _totalCropsHarvested;
    private int _totalCropsWatered;
    private bool _workCompleted;
    private int _workStartTick;

    /// <summary>
    ///     Initializes a new instance of the <see cref="FarmController" /> class.
    /// </summary>
    /// <param name="worldProvider">Provider for game-world farm data.</param>
    public FarmController(IFarmWorldProvider worldProvider)
    {
        _worldProvider = worldProvider ?? throw new ArgumentNullException(nameof(worldProvider));
    }

    /// <summary>
    ///     X coordinate of the work area center.
    /// </summary>
    public int WorkCenterX { get; set; }

    /// <summary>
    ///     Y coordinate of the work area center.
    /// </summary>
    public int WorkCenterY { get; set; }

    /// <summary>
    ///     Radius of the work area in tiles. Default is 20.
    /// </summary>
    public int WorkRadius { get; set; } = 20;

    /// <summary>
    ///     Maximum work time in game hours. Default is 2 hours.
    /// </summary>
    public int MaxWorkHours { get; set; } = 2;

    /// <summary>
    ///     Ticks per game hour. Assumes 60 ticks per second, 3600 ticks per game hour.
    /// </summary>
    public int TicksPerHour { get; set; } = 3600;

    /// <summary>
    ///     Gets a read-only view of the virtual inventory (harvested items).
    /// </summary>
    public IReadOnlyDictionary<string, int> VirtualInventory
    {
        get => _virtualInventory;
    }

    /// <inheritdoc />
    public string ControllerName
    {
        get => "FarmController";
    }

    /// <inheritdoc />
    public bool IsActive { get; private set; }

    /// <inheritdoc />
    public event EventHandler<string>? OnActionStarted;

    /// <inheritdoc />
    public event EventHandler<string>? OnActionCompleted;

    /// <inheritdoc />
    public event EventHandler<string>? OnActionFailed;

    /// <inheritdoc />
    public bool CanHandle(AgentState state) => state == AgentState.FARM;

    /// <inheritdoc />
    public void Start()
    {
        if (IsActive)
        {
            return;
        }

        IsActive = true;
        _workStartTick = 0; // Will be set on first Update
        _totalCropsWatered = 0;
        _totalCropsHarvested = 0;
        _workCompleted = false;
        _virtualInventory.Clear();
        _currentAction = FarmAction.Scanning;
        _targetCrop = null;

        OnActionStarted?.Invoke(this, "Starting farm work session");
    }

    /// <inheritdoc />
    public void Stop()
    {
        if (!IsActive)
        {
            return;
        }

        // Generate work summary
        var summary = $"Farm work ended. Watered: {_totalCropsWatered}, Harvested: {_totalCropsHarvested}";
        if (_virtualInventory.Count > 0)
        {
            var items = string.Join(", ", _virtualInventory.Select(kvp => $"{kvp.Value}x {kvp.Key}"));
            summary += $". Items: {items}";
        }

        IsActive = false;
        _currentAction = FarmAction.Idle;
        _targetCrop = null;

        OnActionCompleted?.Invoke(this, summary);
    }

    /// <inheritdoc />
    public void Update(int ticks)
    {
        if (!IsActive || _workCompleted)
        {
            return;
        }

        // Initialize work start tick on first update
        if (_workStartTick == 0)
        {
            _workStartTick = ticks;
        }

        // Check work time limit
        var elapsedTicks = ticks - _workStartTick;
        var maxWorkTicks = MaxWorkHours * TicksPerHour;
        if (elapsedTicks >= maxWorkTicks)
        {
            _workCompleted = true;
            OnActionCompleted?.Invoke(this,
                $"Farm work time limit reached ({MaxWorkHours} hours). Watered: {_totalCropsWatered}, Harvested: {_totalCropsHarvested}");
            return;
        }

        switch (_currentAction)
        {
            case FarmAction.Scanning:
                ScanForWork();
                break;

            case FarmAction.MovingToWater:
            case FarmAction.MovingToHarvest:
                ProcessMovement();
                break;

            case FarmAction.Watering:
                ProcessWatering(ticks);
                break;

            case FarmAction.Harvesting:
                ProcessHarvesting(ticks);
                break;

            case FarmAction.Idle:
                // Try to find work again
                _currentAction = FarmAction.Scanning;
                break;
        }
    }

    private void ScanForWork()
    {
        var crops = _worldProvider.GetCropsInArea(WorkCenterX, WorkCenterY, WorkRadius).ToList();

        if (crops.Count == 0)
        {
            OnActionFailed?.Invoke(this, "No crops found in work area");
            _currentAction = FarmAction.Idle;
            return;
        }

        var isRaining = _worldProvider.IsRaining();

        // Priority 1: Water crops (skip if raining)
        if (!isRaining)
        {
            var cropToWater = FindNearestCropNeedingWater(crops);
            if (cropToWater != null)
            {
                _targetCrop = cropToWater;
                _currentAction = FarmAction.MovingToWater;
                OnActionStarted?.Invoke(this,
                    $"Moving to water {cropToWater.CropName} at ({cropToWater.X}, {cropToWater.Y})");
                return;
            }
        }

        // Priority 2: Harvest mature crops
        var cropToHarvest = FindNearestMatureCrop(crops);
        if (cropToHarvest != null)
        {
            _targetCrop = cropToHarvest;
            _currentAction = FarmAction.MovingToHarvest;
            OnActionStarted?.Invoke(this,
                $"Moving to harvest {cropToHarvest.CropName} at ({cropToHarvest.X}, {cropToHarvest.Y})");
            return;
        }

        // Priority 3: Nothing to do
        OnActionFailed?.Invoke(this, "No farm work available (no crops need water or harvesting)");
        _currentAction = FarmAction.Idle;
        _workCompleted = true;
    }

    private FarmCrop? FindNearestCropNeedingWater(List<FarmCrop> crops)
    {
        return crops
            .Where(c => c.NeedsWater && c.IsInSeason && !c.IsDecorative)
            .OrderBy(c => GetDistance(c.X, c.Y, WorkCenterX, WorkCenterY))
            .FirstOrDefault();
    }

    private FarmCrop? FindNearestMatureCrop(List<FarmCrop> crops)
    {
        return crops
            .Where(c => c.IsMature && c.IsInSeason && !c.IsDecorative)
            .OrderBy(c => GetDistance(c.X, c.Y, WorkCenterX, WorkCenterY))
            .FirstOrDefault();
    }

    private static double GetDistance(int x1, int y1, int x2, int y2)
    {
        var dx = x1 - x2;
        var dy = y1 - y2;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private void ProcessMovement()
    {
        if (_targetCrop == null)
        {
            _currentAction = FarmAction.Scanning;
            return;
        }

        var arrived = _worldProvider.MoveToward(_targetCrop.X, _targetCrop.Y);
        if (arrived)
        {
            if (_currentAction == FarmAction.MovingToWater)
            {
                _currentAction = FarmAction.Watering;
                _actionStartTick = 0; // Will be set in ProcessWatering
            }
            else if (_currentAction == FarmAction.MovingToHarvest)
            {
                _currentAction = FarmAction.Harvesting;
                _actionStartTick = 0; // Will be set in ProcessHarvesting
            }
        }
    }

    private void ProcessWatering(int currentTick)
    {
        if (_targetCrop == null)
        {
            _currentAction = FarmAction.Scanning;
            return;
        }

        // Initialize action start tick
        if (_actionStartTick == 0)
        {
            _actionStartTick = currentTick;
            _actionDurationTicks = 120; // 2 seconds at 60 ticks per second
            OnActionStarted?.Invoke(this, $"Watering {_targetCrop.CropName}");
        }

        var elapsed = currentTick - _actionStartTick;
        if (elapsed >= _actionDurationTicks)
        {
            // Watering complete
            _worldProvider.MarkCropWatered(_targetCrop);
            _totalCropsWatered++;
            OnActionCompleted?.Invoke(this, $"Watered {_targetCrop.CropName}");

            _targetCrop = null;
            _actionStartTick = 0;
            _currentAction = FarmAction.Scanning;
        }
    }

    private void ProcessHarvesting(int currentTick)
    {
        if (_targetCrop == null)
        {
            _currentAction = FarmAction.Scanning;
            return;
        }

        // Initialize action start tick
        if (_actionStartTick == 0)
        {
            _actionStartTick = currentTick;
            _actionDurationTicks = 60; // 1 second at 60 ticks per second
            OnActionStarted?.Invoke(this, $"Harvesting {_targetCrop.CropName}");
        }

        var elapsed = currentTick - _actionStartTick;
        if (elapsed >= _actionDurationTicks)
        {
            // Harvesting complete
            var itemName = _worldProvider.HarvestCrop(_targetCrop);
            if (!string.IsNullOrEmpty(itemName))
            {
                if (!_virtualInventory.ContainsKey(itemName))
                {
                    _virtualInventory[itemName] = 0;
                }

                _virtualInventory[itemName] += _targetCrop.HarvestQuantity;
            }

            _totalCropsHarvested++;
            OnActionCompleted?.Invoke(this,
                $"Harvested {_targetCrop.CropName} ({_targetCrop.HarvestQuantity}x {itemName ?? "unknown"})");

            _targetCrop = null;
            _actionStartTick = 0;
            _currentAction = FarmAction.Scanning;
        }
    }

    private enum FarmAction
    {
        Idle,
        Scanning,
        MovingToWater,
        MovingToHarvest,
        Watering,
        Harvesting
    }
}