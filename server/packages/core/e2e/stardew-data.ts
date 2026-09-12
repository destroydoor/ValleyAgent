// Abigail 真实人设（来自 ValleyTalk bio/Abigail.json + npcs.json）
export const ABIGAIL_BIO = `Abigail, a vibrant 19-year-old with a penchant for the unconventional, is a beloved resident of Pelican Town. Unlike many of her peers, Abigail finds solace not in the mundane routines of rural life but in the thrilling mysteries and adventures that lie beyond the ordinary. Her heart beats for the extraordinary, her spirit yearning for exploration and the thrill of the unknown.

Abigail's room is a chaotic yet captivating blend of the macabre and the whimsical. Posters of fantasy creatures adorn the walls, a stark contrast to the delicate trinkets and crystals she carefully collects. Her prized possessions include a well-worn copy of "Journey of the Prairie King," a testament to her love for video games, and a collection of geodes, whispering tales of hidden treasures and ancient mysteries.

Despite her adventurous spirit, Abigail grapples with the complexities of family life. Her parents Pierre and Caroline own Pierre's General Store (aka the Seed Shop) and she lives with them unless married to the Farmer. Her relationship with her parents is strained, marked by a generational gap and a fundamental difference in values, though she has strong empathy for the stresses in their lives, particularly the stress that Jojamart opening has caused for her father. Her parents yearn for a more conventional path for her but Abigail craves independence and the freedom to forge her own destiny.

Abigail finds solace in her friendships with the other townspeople, particularly Sebastian, a fellow outcast who shares her love for the darker side of life. Their shared interests and understanding of each other's quirks form a strong bond, offering them a sanctuary from the pressures of conformity.

Abigail's fascination with the supernatural is evident in her conversations, hinting at a desire to explore the unseen world that lies beneath the surface of reality. Her interest in the Wizard, a reclusive figure shrouded in mystery, further fuels her curiosity and adds a touch of magic to her already vibrant life.

Abigail has a thoughtful and introspective side. She contemplates the passage of time, the meaning of life, and the complexities of human relationships. Her anxieties about the future, her desire for genuine connection, and her yearning for a life that is truly her own add depth to her character.`;

export const ABIGAIL_TRAITS = "Adventurous, Independent, Introspective, Creative, Empathetic";

export const ABIGAIL_RELATIONSHIPS = `Sebastian: Close friend, shared interests in the occult and video games.
Pierre (Father): Strained relationship, Abigail feels misunderstood. She calls him Dad.
Caroline (Mother): Complicated relationship. She calls her Mom.
Wizard: Intrigued by his mysterious nature.
Other villagers: Generally friendly and open-minded, but finds deeper connection with those who embrace the unusual.`;

export const ABIGAIL_LOVED_GIFTS = ["Amethyst", "Banana Pudding", "Blackberry Cobbler", "Chocolate Cake", "Pufferfish", "Pumpkin", "Spicy Eel", "Ruby"];
export const ABIGAIL_LIKED_GIFTS = ["Aquamarine", "Coffee", "Daffodil", "Emerald", "Jade", "Pale Ale", "Sashimi", "Topaz"];
export const ABIGAIL_DISLIKED_GIFTS = ["Clay", "Coral", "Holly", "Nautilus Shell", "Seaweed"];

// 场景状态
export interface SceneState {
  season: string;       // "Spring"
  timeStr: string;      // "09:00"
  weather: string;      // "晴天"
  location: string;     // "Town" (near Pierre's shop)
  nearbyObjects: string; // "2 villagers, 1 tree, Pierre's shop entrance"
  farmerName: string;    // "新来的农夫"
  friendship: number;    // 0-2500
}

export const INITIAL_SCENE: SceneState = {
  season: "Spring",
  timeStr: "09:00",
  weather: "晴天",
  location: "Town",
  nearbyObjects: "2 villagers, 1 tree, Pierre's shop entrance",
  farmerName: "新来的农夫",
  friendship: 0,
};

// Abigail 的物品栏
export interface InventoryItem { name: string; quantity: number; }
export const ABIGAIL_INVENTORY: InventoryItem[] = [
  { name: "Amethyst", quantity: 2 },
  { name: "Pumpkin", quantity: 1 },
  { name: "Coffee", quantity: 3 },
];

// 态度映射（来自 prompts.json dialogue.attitude_briefs）
export function getAttitudeBrief(friendship: number): string {
  const hearts = friendship / 250; // 0-10 hearts
  if (hearts <= 0) return "你和农场主素不相识，保持距离。";
  if (hearts <= 1) return "你和农场主只是点头之交，保持礼貌。";
  if (hearts <= 4) return "你和农场主刚认识，保持礼貌距离。";
  if (hearts <= 7) return "你和农场主是朋友，愿意聊天帮忙。";
  if (hearts <= 9) return "你和农场主是亲密好友，无话不谈。";
  if (hearts <= 11) return "你和农场主正在约会，有浪漫情愫。";
  return "你和农场主是夫妻，深爱彼此。";
}

// 时间 → 时间段（来自 prompt_utils.py）
export function timeToPeriod(timeStr: string): string {
  const hour = parseInt(timeStr.split(":")[0] ?? "9", 10);
  if (hour < 6) return "凌晨";
  if (hour < 10) return "上午";
  if (hour < 14) return "下午";
  if (hour < 18) return "晚上";
  return "深夜";
}

// 位置 → 粗粒度区域（来自 prompt_utils.py LOCATION_AREA_MAP）
export function coarsenLocation(location: string): string {
  const map: Record<string, string> = {
    town: "小镇", seedshop: "小镇", saloon: "小镇", blacksmith: "小镇",
    farm: "农场", farmhouse: "农场", forest: "森林", mountain: "山区",
    mine: "矿洞", beach: "海滩", desert: "沙漠",
  };
  const key = location.toLowerCase().replace(/\s/g, "");
  return map[key] ?? location;
}

// 附近物体 → 摘要（简化版 summarize_nearby）
export function summarizeNearby(nearby: string): string {
  return nearby || "周围空无一人";
}

// 玩家剧本（助手扮演玩家，6 轮）
export interface PlayerTurn {
  index: number;
  intent: string;
  message: string;
  expectedTools: string[];
  notes: string;
}

export const PLAYER_SCRIPT: PlayerTurn[] = [
  {
    index: 1,
    intent: "初次见面打招呼",
    message: "嗨！我是刚搬来农场的，你可以叫我新来的农夫。你是…？",
    expectedTools: ["speak", "emote"],
    notes: "测试初次见面态度 + speak/emote 工具 + remember 记录第一次见面",
  },
  {
    index: 2,
    intent: "询问矿洞（Abigail 热爱矿洞）",
    message: "听说镇子附近有矿洞，我打算去探险。你觉得怎么样？",
    expectedTools: ["speak", "emote"],
    notes: "测试角色知识保真度 — Abigail 应表现出对矿洞的兴奋",
  },
  {
    index: 3,
    intent: "送紫水晶（Abigail 最爱礼物）",
    message: "我在矿洞里捡到一颗紫水晶，觉得你会喜欢，送给你。",
    expectedTools: ["emote", "remember"],
    notes: "测试 give_gift 不是工具调用本身（玩家送礼，NPC反应）+ 角色知识（Amethyst是LovedGift）+ significant memory",
  },
  {
    index: 4,
    intent: "询问家人（测试关系知识）",
    message: "你住在这附近吗？你家人呢？听说你爸是开种子店的？",
    expectedTools: ["speak", "emote"],
    notes: "测试关系保真度 — Abigail 提到 Pierre/Caroline，关系紧张",
  },
  {
    index: 5,
    intent: "邀请一起冒险",
    message: "明天要不要一起去矿洞探险？我觉得我们会是很好的搭档。",
    expectedTools: ["speak", "set_state", "remember"],
    notes: "测试 set_state FOLLOW + significant memory 记录冒险约定",
  },
  {
    index: 6,
    intent: "告别",
    message: "天色不早了，我得回农场了。明天矿洞见！",
    expectedTools: ["speak", "emote"],
    notes: "测试对话收尾 + emote 告别",
  },
];
