using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.Navigation;
using ValleyAgent.Services;

namespace ValleyAgent.Commands;

public abstract class AgentCommandBase : IAgentCommand
{
    protected readonly IMonitor Monitor;

    protected AgentCommandBase(IMonitor monitor)
    {
        Monitor = monitor;
    }

    public abstract string CommandName { get; }

    public virtual IReadOnlyList<string> Aliases
    {
        get => Array.Empty<string>();
    }

    public abstract void Execute(NPC npc, AgentInstance agent, Dictionary<string, object> parameters,
        Func<string, string, bool, Dictionary<string, object>, Task> sendResult);

    public static object? GetParamAny(Dictionary<string, object> parameters, params object[] keysOrDefault)
    {
        object? defaultVal = null;
        var keyCount = keysOrDefault.Length;
        if (keyCount > 0 && keysOrDefault[^1] is not string)
        {
            defaultVal = keysOrDefault[^1];
            keyCount--;
        }

        for (var i = 0; i < keyCount; i++)
        {
            if (keysOrDefault[i] is string key && parameters.TryGetValue(key, out var val))
            {
                return val;
            }
        }

        return defaultVal;
    }

    protected static bool TryGetCoordinate(Dictionary<string, object> parameters, out float x, out float y)
    {
        var xObj = GetParamAny(parameters, "x");
        var yObj = GetParamAny(parameters, "y");
        if (xObj != null && yObj != null)
        {
            x = Convert.ToSingle(xObj);
            y = Convert.ToSingle(yObj);
            return true;
        }

        if (parameters.TryGetValue("coordinates", out var coordObj) && coordObj is Dictionary<string, object> coords)
        {
            if (coords.TryGetValue("x", out var cx) && coords.TryGetValue("y", out var cy))
            {
                x = Convert.ToSingle(cx);
                y = Convert.ToSingle(cy);
                return true;
            }
        }

        foreach (var key in new[] { "target", "tile", "position" })
        {
            if (parameters.TryGetValue(key, out var nestedObj) && nestedObj is Dictionary<string, object> nested)
            {
                if (nested.TryGetValue("x", out var nx) && nested.TryGetValue("y", out var ny))
                {
                    x = Convert.ToSingle(nx);
                    y = Convert.ToSingle(ny);
                    return true;
                }
            }
        }

        x = 0;
        y = 0;
        return false;
    }

#pragma warning disable IDE0007
    protected static bool TryGetCoordinate(Dictionary<string, object> parameters, out int x, out int y)
#pragma warning restore IDE0007
    {
#pragma warning disable IDE0007
        if (TryGetCoordinate(parameters, out var fx, out float fy))
#pragma warning restore IDE0007
        {
            x = (int)fx;
            y = (int)fy;
            return true;
        }

        x = 0;
        y = 0;
        return false;
    }

    protected static Point GetApproachTile(NPC npc, Point targetTile)
    {
        var location = npc.currentLocation;
        if (location == null)
        {
            return targetTile;
        }

        return PathfindingUtility.GetApproachTile(
            targetTile,
            (x, y) => TileWalkability.IsTileWalkable(location, x, y));
    }
}