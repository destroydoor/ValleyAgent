import type { MemoryBackend, MemoryEntry, SignificantMemory, ConversationEntry } from "@valley/core";
import { readFile, mkdir, rm } from "fs/promises";
import { dirname, join } from "path";
import { writeFileAtomic } from "./atomic-fs";

/**
 * 2026-08-17 M2a 多玩家上下文：
 * 记忆拆为两层——
 * - 世界桶（world）：{npc}_memory.json。任务/事件类 shortTermMemories + 非 relationship significantMemories。
 * - 玩家桶（player）：{npc}_players/{playerId}_rel.json。per-player conversationHistory +
 *   friendship（TS 权威）+ 玩家相关记忆 + relationship 类 significantMemories。
 * 旧单文件格式（conversationHistory/friendship 在 world 文件）load 时惰性迁移到 _legacy 玩家桶，
 * 首个真实 playerId 对话时认领。
 */

interface MemoryFile {
  npcName: string;
  conversationHistory: ConversationEntry[];
  shortTermMemories: MemoryEntry[];
  significantMemories: SignificantMemory[];
  friendship: number;
  lastSavedAt: string;
  /** M2a 迁移守卫（2026-08-23）：世界文件按 M2a 语义解释过即置位并持久化——
   * 之后即使世界桶再出现对话（beat 路径本就写世界桶），load 也不再二次打包 _legacy。 */
  legacyMigrated?: boolean;
}

const MAX_CONVERSATION = 50;
const MAX_SHORT_TERM = 30;
const IMPORTANCE_BUMP = 0.5;
const IMPORTANCE_CAP = 10;
/** 迁移认领占位玩家（旧单文件数据的归属，首个真实玩家对话时认领）。 */
const LEGACY_PLAYER_ID = "_legacy";

/** 单个玩家的关系记忆桶（{npc}_players/{playerId}_rel.json）。 */
export class PlayerMemory {
  conversationHistory: ConversationEntry[] = [];
  shortTermMemories: MemoryEntry[] = [];
  significantMemories: SignificantMemory[] = [];
  private _friendship: number = 0;
  private loaded = false;

  constructor(
    public readonly npcName: string,
    public readonly playerId: string,
    public playerName: string,
    private readonly filePath: string,
  ) {}

  get friendship(): number {
    return this._friendship;
  }

  addConversation(role: "player" | "npc", text: string): void {
    this.conversationHistory.push({ role, text });
    if (this.conversationHistory.length > MAX_CONVERSATION) {
      this.conversationHistory = this.conversationHistory.slice(-MAX_CONVERSATION);
    }
  }

  addMemory(
    text: string,
    importance: number = 1.0,
    entryType: string = "generic",
    location: string = "",
    tags: string[] = [],
  ): void {
    const now = Date.now() / 1000;
    const clampedImportance = Math.max(0, Math.min(IMPORTANCE_CAP, importance));
    const existing = this.shortTermMemories.find((m) => m.text === text);
    if (existing) {
      existing.count = (existing.count ?? 1) + 1;
      existing.timestamp = now;
      existing.importance = Math.min(IMPORTANCE_CAP, existing.importance + IMPORTANCE_BUMP);
      return;
    }
    this.shortTermMemories.push({
      text,
      timestamp: now,
      importance: clampedImportance,
      entryType,
      location,
      tags,
      count: 1,
    });
    if (this.shortTermMemories.length > MAX_SHORT_TERM) {
      this.shortTermMemories.sort((a, b) => {
        const scoreA = a.importance + (1 - (now - a.timestamp) / 7200) * 2;
        const scoreB = b.importance + (1 - (now - b.timestamp) / 7200) * 2;
        return scoreB - scoreA;
      });
      this.shortTermMemories = this.shortTermMemories.slice(0, MAX_SHORT_TERM);
    }
  }

  addSignificantMemory(
    text: string,
    category: string = "life_event",
    emotionalWeight: string = "joy",
    relatedNpcs: string[] = [],
    location: string = "",
  ): boolean {
    if (this.significantMemories.some((m) => m.text === text)) {
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
    if (this.significantMemories.length === 0) return "";
    return this.significantMemories.map((m) => `我记得... ${m.text}`).join("\n");
  }

  getRecentMemories(count: number = 5): string {
    const recent = this.shortTermMemories.slice(-count);
    if (recent.length === 0) return "";
    return recent
      .map((m) => {
        const c = m.count ?? 1;
        const suffix = c > 1 ? `（×${c}）` : "";
        return `- ${m.text}${suffix}`;
      })
      .join("\n");
  }

  /** 对话历史文本（player 发言用 playerName 标注，替换硬编码"农场主"）。 */
  getConversationContext(count: number = 10): string {
    if (this.conversationHistory.length === 0) return "";
    const recent = this.conversationHistory.slice(-count);
    return recent
      .map((e) => {
        const label = e.role === "player" ? this.playerName : this.npcName;
        return `${label}: ${e.text}`;
      })
      .join("\n");
  }

  addFriendship(delta: number): void {
    this._friendship = Math.max(0, Math.min(2500, this._friendship + delta));
  }

  async load(): Promise<void> {
    if (this.loaded) return;
    this.loaded = true;
    try {
      const raw = await readFile(this.filePath, "utf-8");
      const data = JSON.parse(raw) as MemoryFile & { playerId?: string; playerName?: string };
      this.conversationHistory = data.conversationHistory ?? [];
      this.shortTermMemories = (data.shortTermMemories ?? []).map((m) => ({
        ...m,
        count: m.count ?? 1,
      }));
      this.significantMemories = data.significantMemories ?? [];
      this._friendship = data.friendship ?? 0;
      if (typeof data.playerName === "string" && data.playerName.length > 0) {
        this.playerName = data.playerName;
      }
    } catch (err) {
      if ((err as NodeJS.ErrnoException).code !== "ENOENT") {
        console.warn(`[PlayerMemory] Failed to load ${this.filePath}: ${(err as Error).message}`);
      }
    }
  }

  async save(): Promise<void> {
    const data: MemoryFile & { playerId: string; playerName: string } = {
      npcName: this.npcName,
      playerId: this.playerId,
      playerName: this.playerName,
      conversationHistory: this.conversationHistory,
      shortTermMemories: this.shortTermMemories,
      significantMemories: this.significantMemories,
      friendship: this._friendship,
      lastSavedAt: new Date().toISOString(),
    };
    await mkdir(dirname(this.filePath), { recursive: true });
    // 原子写（temp+rename）：玩家桶半截截断 = 该玩家全部关系记忆丢失。
    await writeFileAtomic(this.filePath, JSON.stringify(data, null, 2));
  }
}

export class AgentMemory implements MemoryBackend {
  // ── 世界桶（任务/事件记忆；conversation/friendship 字段保留兼容旧格式，迁移后为空）──
  conversationHistory: ConversationEntry[] = [];
  shortTermMemories: MemoryEntry[] = [];
  significantMemories: SignificantMemory[] = [];
  private _friendship: number = 0;
  private readonly filePath: string;
  /** M2a 迁移守卫：世界文件已按 M2a 语义解释过（load 成功即置位，save 持久化），
   * 之后世界桶再积累对话也不得二次打包 _legacy。 */
  private legacyMigrated = false;
  /** 玩家桶（playerId → PlayerMemory，惰性加载）。 */
  private readonly players = new Map<string, PlayerMemory>();

  constructor(public readonly npcName: string, filePath: string) {
    this.filePath = filePath;
  }

  /** 旧字段兼容（单机/迁移场景；多玩家下请用 getFriendship(playerId)）。 */
  get friendship(): number {
    return this._friendship;
  }

  /** 玩家桶文件路径：{dir}/{npc}_players/{playerId}_rel.json。 */
  private playerFilePath(playerId: string): string {
    return join(dirname(this.filePath), `${this.npcName}_players`, `${playerId}_rel.json`);
  }

  /**
   * 获取/创建玩家桶（惰性加载 + 迁移认领）。
   * 首个真实 playerId 访问时若存在 _legacy 桶（旧单文件数据）且该玩家无自有文件 → 认领。
   */
  async getPlayerMemory(playerId: string, playerName?: string): Promise<PlayerMemory> {
    const existing = this.players.get(playerId);
    if (existing) {
      if (playerName && playerName.length > 0 && existing.playerName !== playerName) {
        existing.playerName = playerName;
      }
      return existing;
    }

    const mem = new PlayerMemory(this.npcName, playerId, playerName ?? playerId, this.playerFilePath(playerId));
    await mem.load();
    this.players.set(playerId, mem);

    // 迁移认领：_legacy 桶存在且当前玩家无数据 → 认领（旧单文件格式的 conversation/friendship）。
    if (playerId !== LEGACY_PLAYER_ID) {
      const legacy = this.players.get(LEGACY_PLAYER_ID);
      if (legacy && mem.conversationHistory.length === 0 && mem.shortTermMemories.length === 0) {
        mem.conversationHistory = legacy.conversationHistory;
        mem.shortTermMemories = legacy.shortTermMemories;
        mem.significantMemories = legacy.significantMemories;
        mem.addFriendship(legacy.friendship);
        this.players.delete(LEGACY_PLAYER_ID);
        // 磁盘文件一并删（2026-08-23）：只删内存 map 时 _legacy_rel.json 残留，
        // 下个玩家认领前若发生 save/load 周期，残留文件会被再次当迁移数据认领（复制而非转移）。
        // rm force 容忍文件不存在；删除失败只告警不回滚认领（内存态已转移，save 会落新桶）。
        try {
          await rm(this.playerFilePath(LEGACY_PLAYER_ID), { force: true });
        } catch (err) {
          console.warn(`[AgentMemory] Failed to remove claimed _legacy file for ${this.npcName}: ${(err as Error).message}`);
        }
      }
    }
    return mem;
  }

  /** 同步版玩家桶（迁移认领前——只用于已加载的桶；不存在返回 undefined）。 */
  getLoadedPlayerMemory(playerId: string): PlayerMemory | undefined {
    return this.players.get(playerId);
  }

  /** 全部已加载玩家桶（save 用）。 */
  loadedPlayers(): Iterable<PlayerMemory> {
    return this.players.values();
  }

  // ── 对话（M2a：playerId 提供时写玩家桶——需已预加载；否则写世界桶/旧调用兼容）──
  addConversation(role: "player" | "npc", text: string, playerId?: string, playerName?: string): void {
    if (playerId) {
      const mem = this.players.get(playerId);
      if (mem) {
        if (playerName && playerName.length > 0) mem.playerName = playerName;
        mem.addConversation(role, text);
      }
      return;
    }
    this.conversationHistory.push({ role, text });
    if (this.conversationHistory.length > MAX_CONVERSATION) {
      this.conversationHistory = this.conversationHistory.slice(-MAX_CONVERSATION);
    }
  }

  addMemory(
    text: string,
    importance: number = 1.0,
    entryType: string = "generic",
    location: string = "",
    tags: string[] = [],
    playerId?: string,
    playerName?: string,
  ): void {
    if (playerId) {
      const mem = this.players.get(playerId);
      if (mem) {
        if (playerName && playerName.length > 0) mem.playerName = playerName;
        mem.addMemory(text, importance, entryType, location, tags);
      }
      return;
    }
    const now = Date.now() / 1000;
    const clampedImportance = Math.max(0, Math.min(IMPORTANCE_CAP, importance));
    const existing = this.shortTermMemories.find((m) => m.text === text);
    if (existing) {
      existing.count = (existing.count ?? 1) + 1;
      existing.timestamp = now;
      existing.importance = Math.min(IMPORTANCE_CAP, existing.importance + IMPORTANCE_BUMP);
      return;
    }
    this.shortTermMemories.push({
      text,
      timestamp: now,
      importance: clampedImportance,
      entryType,
      location,
      tags,
      count: 1,
    });
    if (this.shortTermMemories.length > MAX_SHORT_TERM) {
      this.shortTermMemories.sort((a, b) => {
        const scoreA = a.importance + (1 - (now - a.timestamp) / 7200) * 2;
        const scoreB = b.importance + (1 - (now - b.timestamp) / 7200) * 2;
        return scoreB - scoreA;
      });
      this.shortTermMemories = this.shortTermMemories.slice(0, MAX_SHORT_TERM);
    }
  }

  removeMemory(matchText: string, playerId?: string): number {
    // 空串守卫对两分支统一前置：includes("") 恒真，放行会清空整个桶
    // （玩家桶分支此前缺此守卫）。
    if (!matchText || matchText.trim() === "") return 0;
    if (playerId) {
      const mem = this.players.get(playerId);
      if (!mem) return 0;
      const before = mem.shortTermMemories.length;
      mem.shortTermMemories = mem.shortTermMemories.filter((m) => !m.text.includes(matchText));
      return before - mem.shortTermMemories.length;
    }
    const needle = matchText.trim();
    const before = this.shortTermMemories.length;
    this.shortTermMemories = this.shortTermMemories.filter((m) => !m.text.includes(needle));
    return before - this.shortTermMemories.length;
  }

  addSignificantMemory(
    text: string,
    category: string = "life_event",
    emotionalWeight: string = "joy",
    relatedNpcs: string[] = [],
    location: string = "",
    playerId?: string,
    playerName?: string,
  ): boolean {
    if (playerId) {
      // 玩家桶需已在对话前预加载（protocol-adapter 调用 getPlayerMemory）——未加载返回 false。
      const loaded = this.players.get(playerId);
      if (!loaded) return false;
      if (playerName && playerName.length > 0) loaded.playerName = playerName;
      return loaded.addSignificantMemory(text, category, emotionalWeight, relatedNpcs, location);
    }
    if (this.significantMemories.some((m) => m.text === text)) {
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

  // ── 读取（M2a：playerId 提供时合并世界桶 + 玩家桶）──
  getSignificantMemoriesText(playerId?: string): string {
    if (!playerId) {
      if (this.significantMemories.length === 0) return "（暂无特别记忆）";
      return this.significantMemories.map((m) => `我记得... ${m.text}`).join("\n");
    }
    const parts: string[] = [];
    if (this.significantMemories.length > 0) {
      parts.push(this.significantMemories.map((m) => `我记得... ${m.text}`).join("\n"));
    }
    const player = this.players.get(playerId);
    if (player) {
      const text = player.getSignificantMemoriesText();
      if (text) parts.push(text);
    }
    return parts.length > 0 ? parts.join("\n") : "（暂无特别记忆）";
  }

  getRecentMemories(count: number = 5, playerId?: string): string {
    if (!playerId) {
      const recent = this.shortTermMemories.slice(-count);
      if (recent.length === 0) return "（无特别记忆）";
      return recent
        .map((m) => {
          const c = m.count ?? 1;
          const suffix = c > 1 ? `（×${c}）` : "";
          return `- ${m.text}${suffix}`;
        })
        .join("\n");
    }
    // 世界近事 + 玩家近事合并（各取 count 的一半，至少 1）
    const half = Math.max(1, Math.floor(count / 2));
    const world = this.shortTermMemories.slice(-half);
    const player = this.players.get(playerId);
    const playerRecent = player ? player.shortTermMemories.slice(-half) : [];
    const lines: string[] = [];
    for (const m of world) {
      const c = m.count ?? 1;
      lines.push(`- ${m.text}${c > 1 ? `（×${c}）` : ""}`);
    }
    for (const m of playerRecent) {
      const c = m.count ?? 1;
      lines.push(`- ${m.text}${c > 1 ? `（×${c}）` : ""}`);
    }
    return lines.length > 0 ? lines.join("\n") : "（无特别记忆）";
  }

  getConversationContext(count: number = 10, playerId?: string): string {
    if (playerId) {
      const player = this.players.get(playerId);
      if (!player || player.conversationHistory.length === 0) return "";
      return player.getConversationContext(count);
    }
    if (this.conversationHistory.length === 0) return "";
    const recent = this.conversationHistory.slice(-count);
    return recent
      .map((e) => {
        const label = e.role === "player" ? "农场主" : this.npcName;
        return `${label}: ${e.text}`;
      })
      .join("\n");
  }

  // ── 好感度（M2a：TS 权威在玩家桶；world 桶字段兼容旧格式）──
  getFriendship(playerId?: string): number {
    if (!playerId) return this._friendship;
    const player = this.players.get(playerId);
    return player ? player.friendship : this._friendship;
  }

  addFriendship(delta: number, playerId?: string): void {
    if (playerId) {
      const mem = this.players.get(playerId);
      if (mem) mem.addFriendship(delta);
      return;
    }
    this._friendship = Math.max(0, Math.min(2500, this._friendship + delta));
  }

  /** 快照自愈：直接设玩家好感（差 >50 时以快照为准校准）。 */
  setFriendship(value: number, playerId?: string): void {
    if (playerId) {
      const mem = this.players.get(playerId);
      if (mem) mem.addFriendship(Math.max(0, Math.min(2500, value)) - mem.friendship);
      return;
    }
    this._friendship = Math.max(0, Math.min(2500, value));
  }

  async load(): Promise<void> {
    try {
      const raw = await readFile(this.filePath, "utf-8");
      const data = JSON.parse(raw) as MemoryFile;
      this.conversationHistory = data.conversationHistory ?? [];
      this.shortTermMemories = (data.shortTermMemories ?? []).map((m) => ({
        ...m,
        count: m.count ?? 1,
      }));
      this.significantMemories = data.significantMemories ?? [];
      this._friendship = data.friendship ?? 0;

      // M2a 迁移：旧单文件格式携带对话/好感（多玩家拆分前）→ 惰性搬入 _legacy 玩家桶，
      // 首个真实玩家对话时认领（getPlayerMemory）。世界桶只保留非 relationship significant
      // 与任务类 shortTerm（玩家相关的按 entryType/tags 无法精确区分——对话与好感必属玩家）。
      // 守卫（2026-08-23）：文件带 legacyMigrated 标记则直接跳过——修复项 1 落地后对话
      // 走玩家桶，但 beat 等路径本就合法写世界桶，没有标记会把它们误打包成"旧数据"。
      if (data.legacyMigrated === true) {
        // 继承持久化守卫：否则本次 save 会把标记丢掉，下一轮 load 又把世界桶对话打包走。
        this.legacyMigrated = true;
      } else {
        const hasLegacyConversation = this.conversationHistory.length > 0;
        const hasLegacyFriendship = this._friendship > 0;
        if (hasLegacyConversation || hasLegacyFriendship) {
          const legacy = new PlayerMemory(
            this.npcName,
            LEGACY_PLAYER_ID,
            "农场主",
            this.playerFilePath(LEGACY_PLAYER_ID),
          );
          legacy.conversationHistory = this.conversationHistory;
          legacy.addFriendship(this._friendship);
          // relationship 类 significant 归属玩家；其余留世界桶
          legacy.significantMemories = this.significantMemories.filter((m) => m.category === "relationship");
          this.significantMemories = this.significantMemories.filter((m) => m.category !== "relationship");
          this.conversationHistory = [];
          this._friendship = 0;
          this.players.set(LEGACY_PLAYER_ID, legacy);
        }
        // 只要成功解释过一次世界文件就置位：本文件自此按 M2a 格式权威，
        // 后续任何回写（含 beat 的世界桶台词）都不再触发迁移。
        this.legacyMigrated = true;
      }
    } catch (err) {
      if ((err as NodeJS.ErrnoException).code !== "ENOENT") {
        console.warn(`[AgentMemory] Failed to load ${this.filePath}: ${(err as Error).message}`);
      }
    }
  }

  async save(): Promise<void> {
    const data: MemoryFile & { legacyMigrated?: boolean } = {
      npcName: this.npcName,
      conversationHistory: this.conversationHistory,
      shortTermMemories: this.shortTermMemories,
      significantMemories: this.significantMemories,
      friendship: this._friendship,
      lastSavedAt: new Date().toISOString(),
      ...(this.legacyMigrated ? { legacyMigrated: true } : {}),
    };
    await mkdir(dirname(this.filePath), { recursive: true });
    // 原子写（temp+rename）：世界桶半截截断会让全部任务记忆降级丢失。
    await writeFileAtomic(this.filePath, JSON.stringify(data, null, 2));
    for (const player of this.players.values()) {
      await player.save();
    }
  }
}
