using StardewModdingAPI;
using ValleyAgent.Config;
using ValleyAgent.Patches;
using ValleyAgent.Services;
using SmaLogLevel = StardewModdingAPI.LogLevel;

namespace ValleyAgent.Debug;

/// <summary>
///     Handles the ValleyAgent_reload_config console command.
///     Reloads configuration from disk.
/// </summary>
public class ReloadConfigHandler : CommandHandlerBase
{
    private readonly IModHelper? _helper;

    public ReloadConfigHandler(
        IMonitor? monitor,
        AgentService? agentService,
        ModConfig? config,
        IModHelper? helper)
        : base(monitor, agentService, config)
    {
        _helper = helper;
    }

    public CommandResult Handle()
    {
        return SafeExecute(() =>
        {
            var helperCheck = EnsureServiceAvailable(_helper, "IModHelper");
            if (!helperCheck.Success)
            {
                return helperCheck;
            }

            var cfg = _helper!.ReadConfig<ModConfig>();
            NPCDialoguePatch.ClearCache();
            _monitor?.Log("ValleyAgent configuration reloaded.", SmaLogLevel.Info);
            return CommandResult.Ok("Configuration reloaded successfully.");
        }, "ReloadConfig");
    }
}