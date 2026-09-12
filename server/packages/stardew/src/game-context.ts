// GameContextManager — in-memory cache + prompt summarizer for GameContext (Task 7).
// Spec: docs/superpowers/specs/2026-07-21-narrative-director-design.md §5

import type { GameContext, NpcStateSnapshot } from "./types";

const DAY_OF_WEEK_MAP: Record<string, string> = {
  Sunday: "周日",
  Monday: "周一",
  Tuesday: "周二",
  Wednesday: "周三",
  Thursday: "周四",
  Friday: "周五",
  Saturday: "周六",
};

const WEATHER_MAP: Record<string, string> = {
  sunny: "晴天",
  rainy: "雨天",
  snowy: "雪天",
  stormy: "暴风雨",
};

const SEASON_MAP: Record<string, string> = {
  spring: "春",
  summer: "夏",
  fall: "秋",
  winter: "冬",
};

/**
 * In-memory cache of the latest GameContext pushed by the C# Mod via
 * `game_context_sync` messages. Provides:
 *   - cached reads (DirectorAgent queries this on every dayPlan run)
 *   - NPC availability checks (BeatScheduler + Director validation)
 *   - compact prompt-injection summaries for the Director and NPC agents
 *
 * This manager holds no SQLite state of its own — the C# Mod is the
 * source of truth for game state, and pushes full snapshots daily plus
 * incremental `player_state_update` messages every 60 ticks.
 */
export class GameContextManager {
  private ctx: GameContext | null = null;

  update(ctx: GameContext): void {
    this.ctx = ctx;
  }

  getCurrent(): GameContext | null {
    return this.ctx;
  }

  isNpcAvailable(npcName: string): boolean {
    if (!this.ctx) return false;
    const npc = this.findNpc(npcName);
    return npc !== null && npc.isAvailable;
  }

  /**
   * Compact director prompt injection (spec §5.3 example):
   * ```
   * [游戏世界]
   * 时间: Year 2 夏 14 (周二), 晴天
   * 进度: 社区中心已完成 2/6, 已解锁沙漠, 已解锁姜岛, 未解锁下水道, 温室已修复
   * 季节资源: 可种[玉米/向日葵/辣椒], 可钓[虹鳟/红鲷], 节日[夏威夷宴会]
   * 玩家: 在 Farm (32,18), 生命 95/100, 钱 8500g, 背包有[玉米x12, 锄头, 钓竿]
   * ```
   */
  summarizeForDirector(): string {
    if (!this.ctx) return "[游戏世界]\n无游戏上下文\n";
    const ctx = this.ctx;
    const lines: string[] = ["[游戏世界]"];

    // Time
    const seasonZh = SEASON_MAP[ctx.time.season] ?? ctx.time.season;
    const dowZh = DAY_OF_WEEK_MAP[ctx.time.dayOfWeek] ?? ctx.time.dayOfWeek;
    const weatherZh = WEATHER_MAP[ctx.time.weather] ?? ctx.time.weather;
    const festivalSuffix =
      ctx.time.isFestivalDay && ctx.time.festivalName
        ? `, 节日: ${ctx.time.festivalName}`
        : "";
    lines.push(
      `时间: Year ${ctx.time.year} ${seasonZh} ${ctx.time.day} (${dowZh}), ${weatherZh}${festivalSuffix}`,
    );

    // Progress
    const totalBundles = 6;
    const doneBundles = ctx.progress.communityCenterBundlesDone.length;
    const progressParts: string[] = [];
    if (ctx.progress.communityCenterComplete) {
      progressParts.push("社区中心已完成");
    } else {
      progressParts.push(`社区中心 ${doneBundles}/${totalBundles}`);
    }
    progressParts.push(ctx.progress.desertUnlocked ? "已解锁沙漠" : "未解锁沙漠");
    if (ctx.progress.islandsUnlocked.length > 0) {
      progressParts.push(`已解锁${ctx.progress.islandsUnlocked.join("/")}`);
    } else {
      progressParts.push("未解锁岛屿");
    }
    progressParts.push(ctx.progress.railroadUnlocked ? "已解锁铁路" : "未解锁铁路");
    progressParts.push(ctx.progress.sewersUnlocked ? "已解锁下水道" : "未解锁下水道");
    progressParts.push(ctx.progress.greenhouseRestored ? "温室已修复" : "温室未修复");
    if (ctx.progress.jojaMartRoute) progressParts.push("Joja路线");
    lines.push(`进度: ${progressParts.join(", ")}`);

    // Seasonal resources
    const crops = ctx.seasonalResources.plantableCrops.join("/") || "无";
    const fish = ctx.seasonalResources.catchableFish.join("/") || "无";
    const forage = ctx.seasonalResources.forageItems.join("/") || "无";
    const festivals = ctx.seasonalResources.activeFestivals.join("/") || "无";
    lines.push(
      `季节资源: 可种[${crops}], 可钓[${fish}], 可采[${forage}], 节日[${festivals}]`,
    );

    // Player state
    const inv = ctx.playerState.inventory
      .map((i) => `${i.name}x${i.quantity}`)
      .join(", ");
    lines.push(
      `玩家: 在 ${ctx.playerState.location} (${ctx.playerState.tile.x},${ctx.playerState.tile.y}), ` +
        `生命 ${ctx.playerState.health}/${ctx.playerState.maxHealth}, ` +
        `钱 ${ctx.playerState.money}g, 背包有[${inv}]`,
    );

    return lines.join("\n") + "\n";
  }

  /**
   * NPC-specific prompt fragment. Includes the shared game-world summary
   * (trimmed) plus the NPC's current state (location, friendship, etc.).
   */
  summarizeForNpc(npcName: string): string {
    if (!this.ctx) {
      return `[NPC ${npcName}]\n无游戏上下文\n`;
    }
    const ctx = this.ctx;
    const npc = this.findNpc(npcName);

    const seasonZh = SEASON_MAP[ctx.time.season] ?? ctx.time.season;
    const timeLine = `时间: Year ${ctx.time.year} ${seasonZh} ${ctx.time.day}`;

    if (!npc) {
      return `[NPC ${npcName}]\n${timeLine}\n状态: 未找到\n`;
    }

    const lines: string[] = [`[NPC ${npcName}]`];
    lines.push(timeLine);
    lines.push(`位置: ${npc.location} (${npc.tile.x},${npc.tile.y})`);
    lines.push(`状态: ${npc.currentState}, 可用: ${npc.isAvailable ? "是" : "否"}`);
    lines.push(`友谊点: ${npc.friendshipPoints}`);
    lines.push(
      `玩家: 在 ${ctx.playerState.location} (${ctx.playerState.tile.x},${ctx.playerState.tile.y})`,
    );
    return lines.join("\n") + "\n";
  }

  private findNpc(npcName: string): NpcStateSnapshot | null {
    if (!this.ctx) return null;
    for (const n of this.ctx.npcStates) {
      if (n.name === npcName) return n;
    }
    return null;
  }
}
