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

export class NpcPromptLoader {
  private readonly data: Record<string, NpcData>;
  private static readonly PHASE_THRESHOLDS: ReadonlyArray<{ name: PhaseName; min: number }> = [
    { name: "stranger", min: 0 },
    { name: "acquaintance", min: 251 },
    { name: "friend", min: 501 },
    { name: "close", min: 1001 },
    { name: "partner", min: 2001 },
  ];

  constructor(dataPath: string) {
    const raw = readFileSync(dataPath, "utf-8");
    this.data = JSON.parse(raw) as Record<string, NpcData>;
  }

  listNpcs(): string[] {
    return Object.keys(this.data);
  }

  getNpcData(npcName: string): NpcData | null {
    return this.data[npcName] ?? null;
  }

  getPhaseForFriendship(npcName: string, friendship: number): PhaseName {
    let result: PhaseName = "stranger";
    for (const { name, min } of NpcPromptLoader.PHASE_THRESHOLDS) {
      if (friendship >= min) result = name;
    }
    if (!this.data[npcName]) return "stranger";
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
