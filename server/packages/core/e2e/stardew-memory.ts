// 简化版 AgentMemory — 移植自 valley_agent_server/server/agent_memory.py
// 保留：conversation_history, short_term_memories, significant_memories
// 省略：3层 tiered system, LLM compression, file persistence

export interface MemoryEntry {
  text: string;
  timestamp: number;
  importance: number; // 0.0-10.0
  entryType: string;  // "conversation" | "decision" | "emotion" | "event" | "gift"
  location: string;
  tags: string[];
}

export interface SignificantMemory {
  text: string;        // 第一人称，如"我和农场主第一次见面了"
  timestamp: number;
  category: string;    // "relationship" | "life_event" | "trauma" | "achievement"
  emotionalWeight: string; // "joy" | "sorrow" | "anger" | "fear" | "love" | "pride" | "surprise"
  relatedNpcs: string[];
  location: string;
}

export class AgentMemory {
  conversationHistory: Array<{ role: "player" | "npc"; text: string }> = [];
  shortTermMemories: MemoryEntry[] = [];
  significantMemories: SignificantMemory[] = [];
  private _friendship: number = 0;

  constructor(public npcName: string) {}

  get friendship(): number { return this._friendship; }

  addConversation(role: "player" | "npc", text: string): void {
    this.conversationHistory.push({ role, text });
    if (this.conversationHistory.length > 50) {
      this.conversationHistory = this.conversationHistory.slice(-50);
    }
  }

  addMemory(
    text: string,
    importance: number = 1.0,
    entryType: string = "generic",
    location: string = "",
    tags: string[] = []
  ): void {
    // 去重（60秒窗口）
    const now = Date.now() / 1000;
    const exists = this.shortTermMemories.some(
      m => m.text === text && (now - m.timestamp) < 60
    );
    if (exists) return;
    this.shortTermMemories.push({
      text,
      timestamp: now,
      importance: Math.max(0, Math.min(10, importance)),
      entryType,
      location,
      tags,
    });
    // 保持最多 30 条短期记忆
    if (this.shortTermMemories.length > 30) {
      // 按重要性+时间排序，保留高分记忆
      this.shortTermMemories.sort((a, b) => {
        const scoreA = a.importance + (1 - (now - a.timestamp) / 7200) * 2;
        const scoreB = b.importance + (1 - (now - b.timestamp) / 7200) * 2;
        return scoreB - scoreA;
      });
      this.shortTermMemories = this.shortTermMemories.slice(0, 30);
    }
  }

  addSignificantMemory(
    text: string,
    category: string = "life_event",
    emotionalWeight: string = "joy",
    relatedNpcs: string[] = [],
    location: string = ""
  ): boolean {
    // 去重：相同文本不重复添加
    if (this.significantMemories.some(m => m.text === text)) {
      return false;
    }
    this.significantMemories.push({
      text,
      timestamp: Date.now() / 1000,
      category,
      emotionalWeight,
      relatedNpcs,
      location,
    });
    return true;
  }

  getSignificantMemoriesText(): string {
    if (this.significantMemories.length === 0) return "（暂无特别记忆）";
    return this.significantMemories.map(m => `我记得... ${m.text}`).join("\n");
  }

  getRecentMemories(count: number = 5): string {
    const recent = this.shortTermMemories.slice(-count);
    if (recent.length === 0) return "（无特别记忆）";
    return recent.map(m => `- ${m.text}`).join("\n");
  }

  getConversationContext(count: number = 10): string {
    if (this.conversationHistory.length === 0) return "";
    const recent = this.conversationHistory.slice(-count);
    return recent.map(e => {
      const label = e.role === "player" ? "农场主" : this.npcName;
      return `${label}: ${e.text}`;
    }).join("\n");
  }

  addFriendship(delta: number): void {
    this._friendship = Math.max(0, Math.min(2500, this._friendship + delta));
  }
}
