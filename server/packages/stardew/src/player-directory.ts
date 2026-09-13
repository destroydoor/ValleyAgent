/**
 * PlayerDirectory — 服务器侧"见过的玩家"名录（M3 多玩家化，2026-09-13）。
 *
 * 背景：导演（Director）原本是单玩家视角——`morningPlan()` 只面对一份玩家画像。
 * 联机下 TS 侧没有任何"当前有哪些玩家"的权威名单：
 * - `day_started` 不带玩家列表（协议不改）；
 * - `GameContext.playerState` 是主机自己的状态（世界级快照）；
 * - 唯一稳定携带玩家身份的是 `dialogue` 请求的 `playerId`（C# 每玩家各自发起）。
 *
 * 因此这里做一个**观察式**名录：凡发起过对话的玩家即登记，导演换日编排时按此名单
 * 逐个玩家取画像、各自组 prompt。
 *
 * 设计取舍（明确记为已知限制）：
 * - 纯内存、不持久化：服务器重启后名录为空 → 当天 morningPlan 退化为单玩家行为，
 *   直到有玩家开口说话。画像本身是持久化的，重启不会丢数据，只丢"谁在场上"的
 *   瞬时认知——换日编排发生在玩家上线并说过话之后，实际影响有限。
 * - 上限 8 人（星露谷联机上限 4 + 分屏），超出按最久未观察淘汰，防无界增长。
 * - 只登记真实 playerId；`_legacy` 占位键不进名录（它是旧单玩家数据的归属标记，
 *   不是活人）。
 */

import { LEGACY_PLAYER_ID } from "./player-profile-store";

/** 名录容量（星露谷联机上限 4 人 + 分屏副屏，留一倍余量）。 */
const MAX_TRACKED_PLAYERS = 8;

export class PlayerDirectory {
  /** playerId → 最近一次观察到的玩家显示名（Map 保持插入顺序 = 观察先后）。 */
  private readonly names = new Map<string, string>();

  constructor(private readonly maxTracked: number = MAX_TRACKED_PLAYERS) {}

  /**
   * 记录一次玩家出现（由 handleDialogue 在拿到请求时调用）。
   * 空 playerId（旧 C# 客户端/无玩家上下文）与 `_legacy` 占位键一律不登记。
   */
  observe(playerId?: string, playerName?: string): void {
    if (!playerId || playerId.trim() === "") return;
    if (playerId === LEGACY_PLAYER_ID) return;

    // 重新观察：先删再插 → 移到 Map 末尾（= 最近使用），淘汰时不会误伤活跃玩家。
    if (this.names.has(playerId)) this.names.delete(playerId);
    this.names.set(playerId, playerName ?? this.names.get(playerId) ?? "");

    while (this.names.size > this.maxTracked) {
      const oldest = this.names.keys().next();
      if (oldest.done) break;
      this.names.delete(oldest.value);
    }
  }

  /** 已知玩家 ID（按观察先后，最近观察的在最后）。 */
  listPlayerIds(): string[] {
    return [...this.names.keys()];
  }

  /** 玩家显示名（未登记返回 undefined）。 */
  playerNameOf(playerId: string): string | undefined {
    return this.names.get(playerId);
  }

  /** 清空名录（测试 / 回标题）。 */
  clear(): void {
    this.names.clear();
  }
}
