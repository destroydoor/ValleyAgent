#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using StardewModdingAPI;
using XnaKeys = Microsoft.Xna.Framework.Input.Keys;

namespace ValleyAgent.TestMod.Input;

/// <summary>
///     A single programmable input action scheduled at a tick offset relative to the
///     containing script's start. Args are stored as <see cref="object" /> so a single
///     schema covers all action types (click / rightclick / key / text / move / wait).
/// </summary>
public sealed class InputAction
{
    /// <summary>Tick offset relative to script Start(). Dispatched when CurrentTick == TickOffset.</summary>
    public int TickOffset { get; set; }

    /// <summary>Action type: "click", "rightclick", "key", "text", "move", "wait".</summary>
    public string Type { get; set; } = "";

    /// <summary>
    ///     First typed argument. Semantics depend on <see cref="Type" />:
    ///     click/rightclick/move: int x/dx/tileX. key: XNA Keys (enum). text: string. wait: int duration.
    /// </summary>
    public object? Arg1 { get; set; }

    /// <summary>Second typed argument (click/rightclick y, move dy). Null for other types.</summary>
    public object? Arg2 { get; set; }

    /// <summary>Third typed argument (move durationTicks). Null for other types.</summary>
    public object? Arg3 { get; set; }

    /// <summary>Optional human-readable label for debugging.</summary>
    public string? Label { get; set; }
}

/// <summary>
///     A programmable sequence of input actions replayed against an <see cref="InputSimulator" />.
///     Tick offsets are relative to the <see cref="Start" /> call. Each <see cref="Tick" /> advances
///     the internal counter by one and dispatches any actions whose TickOffset equals the current
///     tick. The script is considered finished when the last scheduled tick has elapsed and any
///     active "wait" duration has expired.
/// </summary>
public sealed class InputScript
{
    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly Dictionary<int, List<InputAction>> _byTick = new();
    private readonly IMonitor? _monitor;

    private int _activeWaits;
    private int _maxOffset;

    public InputScript(IMonitor? monitor = null)
    {
        _monitor = monitor;
    }

    /// <summary>All scheduled actions in insertion order.</summary>
    public List<InputAction> Actions { get; } = new();

    /// <summary>Current tick counter, incremented once per <see cref="Tick" /> call.</summary>
    public int CurrentTick { get; private set; }

    /// <summary>
    ///     True when the last scheduled action's tick has elapsed and no wait is active.
    ///     An empty script is finished immediately after <see cref="Start" />.
    /// </summary>
    public bool IsFinished
    {
        get => Actions.Count == 0 || (CurrentTick > _maxOffset && _activeWaits == 0);
    }

    /// <summary>Reset CurrentTick to 0 and rebuild the per-tick dispatch index.</summary>
    public void Start()
    {
        CurrentTick = 0;
        _activeWaits = 0;
        RebuildIndex();
    }

    /// <summary>
    ///     Advance CurrentTick by one and dispatch any actions whose TickOffset equals the
    ///     pre-increment value. Active wait counters are decremented after dispatch.
    /// </summary>
    public void Tick(InputSimulator simulator)
    {
        ArgumentNullException.ThrowIfNull(simulator);

        if (_byTick.TryGetValue(CurrentTick, out var list))
        {
            foreach (var action in list)
            {
                Dispatch(action, simulator);
            }
        }

        CurrentTick++;
        if (_activeWaits > 0)
        {
            _activeWaits--;
        }
    }

    // ── Builder methods ────────────────────────────────────────────────

    public void AddClick(int tickOffset, int x, int y, string? label = null) => Add(new InputAction
        { TickOffset = tickOffset, Type = "click", Arg1 = x, Arg2 = y, Label = label });

    public void AddRightClick(int tickOffset, int tileX, int tileY, string? label = null) => Add(new InputAction
        { TickOffset = tickOffset, Type = "rightclick", Arg1 = tileX, Arg2 = tileY, Label = label });

    public void AddKeyPress(int tickOffset, XnaKeys key, string? label = null) => Add(new InputAction
        { TickOffset = tickOffset, Type = "key", Arg1 = key, Label = label });

    public void AddTextInput(int tickOffset, string text, string? label = null) => Add(new InputAction
        { TickOffset = tickOffset, Type = "text", Arg1 = text, Label = label });

    public void AddMove(int tickOffset, int dx, int dy, int durationTicks, string? label = null)
    {
        Add(new InputAction
        {
            TickOffset = tickOffset,
            Type = "move",
            Arg1 = dx,
            Arg2 = dy,
            Arg3 = durationTicks,
            Label = label
        });
    }

    public void AddWait(int tickOffset, int durationTicks, string? label = null) => Add(new InputAction
        { TickOffset = tickOffset, Type = "wait", Arg1 = durationTicks, Label = label });

    // ── Persistence ────────────────────────────────────────────────────

    /// <summary>Serialize <see cref="Actions" /> to JSON at <paramref name="path" /> (camelCase, enum-as-string).</summary>
    public void SaveToFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        try
        {
            var json = JsonSerializer.Serialize(Actions, s_jsonOpts);
            File.WriteAllText(path, json);
            _monitor?.Log($"[InputScript] Saved {Actions.Count} actions to {path}", LogLevel.Info);
        }
        catch (Exception ex)
        {
            _monitor?.Log($"[InputScript] SaveToFile failed: {ex.Message}", LogLevel.Error);
            throw;
        }
    }

    /// <summary>
    ///     Load an <see cref="InputScript" /> from a JSON file. Throws <see cref="FileNotFoundException" />
    ///     if the file is missing; logs and re-throws on JSON parse errors.
    /// </summary>
    public static InputScript LoadFromFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Input script file not found: {path}", path);
        }

        var json = File.ReadAllText(path);
        List<InputAction>? actions;
        try
        {
            actions = JsonSerializer.Deserialize<List<InputAction>>(json, s_jsonOpts);
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"[InputScript] Failed to parse '{path}': {ex.Message}");
            throw;
        }

        if (actions == null)
        {
            throw new JsonException($"Failed to parse input script: {path} (null result)");
        }

        var script = new InputScript();
        foreach (var action in actions)
        {
            script.Add(action);
        }

        return script;
    }

    // ── Internals ──────────────────────────────────────────────────────

    private void Add(InputAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Actions.Add(action);
        if (action.TickOffset > _maxOffset)
        {
            _maxOffset = action.TickOffset;
        }
    }

    private void RebuildIndex()
    {
        _byTick.Clear();
        _maxOffset = 0;
        foreach (var a in Actions)
        {
            if (!_byTick.TryGetValue(a.TickOffset, out var list))
            {
                list = new List<InputAction>();
                _byTick[a.TickOffset] = list;
            }

            list.Add(a);
            if (a.TickOffset > _maxOffset)
            {
                _maxOffset = a.TickOffset;
            }
        }
    }

    private void Dispatch(InputAction action, InputSimulator simulator)
    {
        try
        {
            switch (action.Type)
            {
                case "click":
                    simulator.SimulateMouseClick(AsInt(action.Arg1), AsInt(action.Arg2));
                    break;
                case "rightclick":
                    simulator.SimulateRightClick(AsInt(action.Arg1), AsInt(action.Arg2));
                    break;
                case "key":
                    simulator.SimulateKeyPress(AsKeys(action.Arg1));
                    break;
                case "text":
                    simulator.SimulateTextInput(AsString(action.Arg1));
                    break;
                case "move":
                    simulator.SimulatePlayerMove(AsInt(action.Arg1), AsInt(action.Arg2), AsInt(action.Arg3));
                    break;
                case "wait":
                    _activeWaits = Math.Max(_activeWaits, AsInt(action.Arg1));
                    break;
                default:
                    _monitor?.Log(
                        $"[InputScript] Unknown action type '{action.Type}' at tick {action.TickOffset}",
                        LogLevel.Warn);
                    break;
            }
        }
        catch (Exception ex)
        {
            _monitor?.Log(
                $"[InputScript] Dispatch failed for action type '{action.Type}' at tick {action.TickOffset}: {ex.Message}",
                LogLevel.Warn);
        }
    }

    /// <summary>
    ///     Coerce a deserialized argument to int. Handles boxed int, long, and JsonElement
    ///     (the latter appears after System.Text.Json round-trips through <see cref="object" />).
    /// </summary>
    private static int AsInt(object? arg)
    {
        return arg switch
        {
            null => 0,
            int i => i,
            long l => (int)l,
            JsonElement je => je.GetInt32(),
            _ => Convert.ToInt32(arg)
        };
    }

    private static string AsString(object? arg)
    {
        return arg switch
        {
            null => "",
            string s => s,
            JsonElement je => je.GetString() ?? "",
            _ => arg.ToString() ?? ""
        };
    }

    private static XnaKeys AsKeys(object? arg)
    {
        return arg switch
        {
            null => XnaKeys.None,
            XnaKeys k => k,
            int i => (XnaKeys)i,
            long l => (XnaKeys)l,
            JsonElement je when je.ValueKind == JsonValueKind.Number => (XnaKeys)je.GetInt32(),
            JsonElement je when je.ValueKind == JsonValueKind.String
                => Enum.Parse<XnaKeys>(je.GetString() ?? "None", true),
            string s => Enum.Parse<XnaKeys>(s, true),
            _ => XnaKeys.None
        };
    }
}