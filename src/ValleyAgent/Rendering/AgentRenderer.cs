using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI.Events;
using StardewValley;
using ValleyAgent.Config;
using ValleyAgent.Handlers;
using ValleyAgent.Health;
using ValleyAgent.Services;
using ValleyAgent.StateMachine;

namespace ValleyAgent.Rendering;

/// <summary>
///     Agent 渲染器。负责血条绘制、武器/工具动画。
///     从 ModEntry 提取。
/// </summary>
public class AgentRenderer
{
    private readonly AgentService? _agentService;
    private readonly FightHandler? _fightHandler;
    private readonly MineHandler? _mineHandler;

    public AgentRenderer(
        AgentService? agentService,
        FightHandler? fightHandler,
        MineHandler? mineHandler)
    {
        _agentService = agentService;
        _fightHandler = fightHandler;
        _mineHandler = mineHandler;
    }

    public void OnRenderedWorld(object? sender, RenderedWorldEventArgs e)
    {
        if (_agentService == null)
        {
            return;
        }

        foreach (var agent in _agentService.GetAllAgents())
        {
            if (agent.Health.IsDead)
            {
                continue;
            }

            var npc = Game1.getCharacterFromName(agent.NpcName);
            if (npc?.currentLocation == null)
            {
                continue;
            }

            if (npc.currentLocation != Game1.currentLocation)
            {
                continue;
            }

            switch (agent.StateMachine.CurrentStateFlag)
            {
                case AgentState.FIGHT:
                    _fightHandler?.DrawWeapon(e.SpriteBatch, npc);
                    break;
                case AgentState.MINE:
                    _mineHandler?.DrawTool(e.SpriteBatch, npc);
                    break;
                // 其他状态无需特殊渲染
                case AgentState.IDLE:
                case AgentState.FOLLOW:
                case AgentState.FARM:
                case AgentState.FORAGE:
                case AgentState.TALK:
                default:
                    break;
            }

            var shouldShowHealth = agent.StateMachine.CurrentStateFlag == AgentState.FIGHT
                                   || FightHandler.FindNearestMonster(npc) != null;

            if (shouldShowHealth)
            {
                DrawHealthBar(e.SpriteBatch, npc, agent.Health);
            }
        }
    }

    private static void DrawHealthBar(SpriteBatch b, NPC npc, AgentHealth health)
    {
        if (health == null)
        {
            return;
        }

        var pos = npc.getLocalPosition(Game1.viewport);
        var x = pos.X + GameConstants.HealthBarOffsetX;
        var y = pos.Y + GameConstants.HealthBarOffsetY;
        var barWidth = GameConstants.HealthBarWidth;
        var barHeight = GameConstants.HealthBarHeight;
        var pct = health.HealthPercent;

        var bgColor = GameConstants.HealthBarBgColor;
        var fgColor = pct > GameConstants.HealthBarHighThreshold ? GameConstants.HealthBarHighColor
            : pct > GameConstants.HealthBarMidThreshold ? GameConstants.HealthBarMidColor
            : GameConstants.HealthBarLowColor;

        b.Draw(Game1.staminaRect, new Rectangle((int)x, (int)y, barWidth, barHeight), bgColor);
        var fillWidth = (int)(barWidth * pct);
        if (fillWidth > 0)
        {
            b.Draw(Game1.staminaRect, new Rectangle((int)x, (int)y, fillWidth, barHeight), fgColor);
        }
    }
}