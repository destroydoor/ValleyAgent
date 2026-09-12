// MemoryBackend — 接口契约，让 AgentMemory 实现，方便 P2/P3 替换为 Qdrant/SQLite
// spec 3.2 节定义

export interface MemoryEntry {
  text: string;
  timestamp: number;       // Unix seconds
  importance: number;      // 0.0-10.0
  entryType: string;       // "conversation" | "decision" | "emotion" | "event" | "gift" | "generic"
  location: string;
  tags: string[];
  count: number;           // merge count: how many times this exact text was added (default 1)
}

export interface SignificantMemory {
  text: string;            // 第一人称
  timestamp: number;
  category: string;        // "relationship" | "life_event" | "trauma" | "achievement"
  emotionalWeight: string; // "joy" | "sorrow" | "anger" | "fear" | "love" | "pride" | "surprise"
  relatedNpcs: string[];
  location: string;
}

export interface ConversationEntry {
  role: "player" | "npc";
  text: string;
}

export interface MemoryBackend {
  // State (readable for prompt building)
  conversationHistory: ConversationEntry[];
  shortTermMemories: MemoryEntry[];
  significantMemories: SignificantMemory[];
  friendship: number;

  // Mutators
  addConversation(role: "player" | "npc", text: string): void;
  addMemory(
    text: string,
    importance?: number,
    entryType?: string,
    location?: string,
    tags?: string[]
  ): void;
  addSignificantMemory(
    text: string,
    category?: string,
    emotionalWeight?: string,
    relatedNpcs?: string[],
    location?: string
  ): boolean;
  addFriendship?(delta: number): void;

  // Readers (formatted for prompt injection)
  getConversationContext(count?: number): string;
  getRecentMemories(count?: number): string;
  getSignificantMemoriesText(): string;

  // Persistence
  load(): Promise<void>;
  save(): Promise<void>;
}
