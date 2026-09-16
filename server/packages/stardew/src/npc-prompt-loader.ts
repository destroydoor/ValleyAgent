import { readFileSync } from "fs";

export interface NpcPhase {
  friendship_range: string;
  prompt: string;
}

export interface NpcPhases {
  stranger: NpcPhase;
  acquaintance: NpcPhase;
  friend: NpcPhase;
  close: NpcPhase;
  partner: NpcPhase;
}

export interface NpcData {
  base_memory: string;
  phases: NpcPhases;
}

export type PhaseName = keyof NpcPhases;

/**
 * 通用最小人设模板（issue #25，异常处理审计 §4.6）。
 * 只按 schema 字段给中性默认值，个性化内容仅限 NPC 名字本身——
 * 不编造任何具体 NPC 的性格/喜好/关系（那是 npc_prompts.json 的职责）。
 * 好感阶段差异仅表述关系远近，措辞全模板化。
 */
function minimalNpcData(npcName: string): NpcData {
  return {
    base_memory: `我是${npcName}，星露谷鹈鹕镇的居民。（人设文件缺失，暂时以最朴素的方式生活。）`,
    phases: {
      stranger: {
        friendship_range: "0-250",
        prompt: `我是${npcName}，一个星露谷的居民。我们还不算认识，初次见面请多指教。`,
      },
      acquaintance: {
        friendship_range: "251-500",
        prompt: `我是${npcName}，一个星露谷的居民。我们打过几次照面，算是认识了。`,
      },
      friend: {
        friendship_range: "501-1000",
        prompt: `我是${npcName}，一个星露谷的居民。我们聊过不少次，我觉得我们已经是朋友了。`,
      },
      close: {
        friendship_range: "1001-2000",
        prompt: `我是${npcName}，一个星露谷的居民。你是我很亲近的朋友。`,
      },
      partner: {
        friendship_range: "2001-2500",
        prompt: `我是${npcName}，你的伴侣。（人设文件缺失，我只能以最简单的方式陪伴你。）`,
      },
    },
  };
}

export class NpcPromptLoader {
  private readonly data: Record<string, NpcData>;

  /** 人设文件缺失/损坏时为 true——所有 NPC 回退最小模板（进程不秒退，issue #25）。 */
  private readonly degraded: boolean;

  private static readonly PHASE_THRESHOLDS: ReadonlyArray<{ name: PhaseName; min: number }> = [
    { name: "stranger", min: 0 },
    { name: "acquaintance", min: 251 },
    { name: "friend", min: 501 },
    { name: "close", min: 1001 },
    { name: "partner", min: 2001 },
  ];

  constructor(dataPath: string) {
    const loaded = NpcPromptLoader.loadData(dataPath);
    this.degraded = loaded === null;
    this.data = loaded ?? {};
  }

  /**
   * 读人设文件：缺失/不可读/损坏 JSON/根结构不对时不再让异常炸掉 startServer
   * （此前构造函数 readFileSync+JSON.parse 零守卫 → cli.ts fatal: 后 process.exit(1)，
   * ConsoleWindow=true 模式下 cmd 窗口随进程秒退关闭，玩家侧只剩"server exited code 1"）。
   * 现在降级为最小内置人设（degraded 模式按名字给每人一份同模板），
   * console.error 带文件路径 + 解析错误位置/原因，服务器继续启动。
   * 返回 null 表示降级（与"文件合法但内容为空对象"区分）。
   */
  private static loadData(dataPath: string): Record<string, NpcData> | null {
    try {
      const raw = readFileSync(dataPath, "utf-8");
      const parsed: unknown = JSON.parse(raw);
      if (parsed === null || typeof parsed !== "object" || Array.isArray(parsed)) {
        throw new Error("top-level JSON value is not an object (persona registry expected)");
      }
      return parsed as Record<string, NpcData>;
    } catch (err) {
      const detail = err instanceof Error ? `${err.name}: ${err.message}` : String(err);
      console.error(
        `[npc-prompts] failed to load persona file at "${dataPath}" ` +
          `— falling back to minimal built-in personas. reason: ${detail}`,
      );
      return null;
    }
  }

  listNpcs(): string[] {
    return Object.keys(this.data);
  }

  getNpcData(npcName: string): NpcData | null {
    const npc = this.data[npcName];
    if (npc) return npc;
    // 降级模式：TS 侧没有村民名字表（名字由 C# 快照驱动），按名字现给一份中性模板，
    // 让 phases/好感阶段走正常路径而不是层层空值兜底。
    return this.degraded ? minimalNpcData(npcName) : null;
  }

  getPhaseForFriendship(npcName: string, friendship: number): PhaseName {
    let result: PhaseName = "stranger";
    for (const { name, min } of NpcPromptLoader.PHASE_THRESHOLDS) {
      if (friendship >= min) result = name;
    }
    if (!this.data[npcName] && !this.degraded) return "stranger";
    return result;
  }

  getPhasePrompt(npcName: string, friendship: number): string {
    const npc = this.getNpcData(npcName);
    if (!npc) {
      return `你是 ${npcName}，一个星露谷的居民。`;
    }
    const phase = this.getPhaseForFriendship(npcName, friendship);
    const phaseData = npc.phases[phase];
    if (!phaseData) {
      return `你是 ${npcName}，一个星露谷的居民。`;
    }
    return phaseData.prompt;
  }

  /** 态度简报。M2a：主语"农场主"按对话发起玩家替换（多人时 NPC 对各玩家关系独立）。 */
  getAttitudeBrief(friendship: number, playerName?: string): string {
    const you = playerName && playerName.trim().length > 0 ? playerName.trim() : "农场主";
    const hearts = friendship / 250;
    if (hearts <= 0) return `你和${you}素不相识，保持距离。`;
    if (hearts <= 2) return `你和${you}只是点头之交，保持礼貌。`;
    if (hearts <= 3) return `你和${you}刚认识，保持礼貌距离。`;
    if (hearts <= 5) return `你和${you}是朋友，愿意聊天帮忙。`;
    if (hearts <= 7) return `你和${you}是亲密好友，无话不谈。`;
    if (hearts <= 8) return `你和${you}正在约会，有浪漫情愫。`;
    return `你和${you}是夫妻，深爱彼此。`;
  }
}
