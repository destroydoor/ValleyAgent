# ValleyTalk Agent Upgrade - Draft Requirements

## Project: ValleyTalk Agent System
**Goal**: Transform ValleyTalk from dialogue generator into true AI Agent NPC system

---

## Confirmed Requirements

### 1. Agent NPC Capabilities
- [ ] **Autonomous Friendship Management**: NPCs can increase/decrease friendship points based on interactions
  - Must be rare/valuable - system should discourage frequent changes
  - Integrate with existing friendship system (hearts, points)
  
- [ ] **Autonomous Gift Giving**: NPCs can decide to give gifts to player
  - CRITICAL: Must prevent AI hallucination (giving non-existent items)
  - Needs validation against actual game item database
  - Should respect NPC personality and relationship level

- [ ] **Dynamic Agent Allocation**: Not all NPCs need full Agent capabilities
  - Only "close" NPCs (high interaction frequency) get Agent mode
  - Others use default behavior or simple dialogue
  - Need smart allocation algorithm (to be designed)

### 2. API System Upgrade
- [ ] **Multi-Provider Support**: Reference OpenCode implementation
  - OpenAI-compatible APIs (DeepSeek, OpenRouter, etc.)
  - Local APIs (LMStudio, Ollama)
  - Kimi API (coding plan support)
  
- [ ] **Provider Configuration**: Per-provider settings
  - API keys, base URLs, model names
  - Timeout settings, retry logic
  - Fallback chain between providers

### 3. RAG Enhancement
- [ ] **Stardew Valley Wiki Integration**
  - Scrape/download wiki content for knowledge base
  - Vector store for semantic search
  - Prevent hallucination by grounding AI in real game data
  - Categories: items, NPCs, locations, events, mechanics

---

## Open Questions / Design Decisions Needed

### Q1: Dynamic Agent Allocation Strategy
- How to measure "interaction closeness"?
  - Friendship heart count?
  - Recent interaction frequency?
  - Player-initiated vs NPC-initiated?
  - Time spent in same location?
- Should allocation change dynamically or be set at game start?
- Maximum number of concurrent Agent NPCs?
- How to handle players who want ALL NPCs as Agents (cost concern)?

### Q2: Gift Giving Safety
- How to prevent AI from hallucinating non-existent items?
  - Option A: Strict validation against item database before executing
  - Option B: Provide AI with limited "gift pool" per NPC based on tastes
  - Option C: RAG retrieval of valid gifts for each NPC
- Should gifts respect NPC's financial situation?
- How to handle "loved" vs "liked" vs "neutral" gifts?
- Gift frequency limits to prevent spam?

### Q3: Friendship System Integration
- Should friendship changes be visible immediately or queued?
- How to balance AI autonomy with game balance?
  - Too much friendship = game becomes too easy
  - Too little = players feel punished
- Should there be "friendship decay" for neglected NPCs?
- How to handle heart event triggers that depend on friendship level?

### Q4: RAG Implementation
- Which wiki sections are most critical?
- Update frequency (wiki changes over time)?
- Vector store choice (local SQLite, Chroma, etc.)?
- Search strategy (semantic vs keyword vs hybrid)?
- How to structure chunks for NPC-specific retrieval?

### Q5: API Cost Management
- Cost per NPC per interaction?
- Budget tracking and alerts?
- Fallback to cheaper models for less important NPCs?
- Local model for bulk operations, cloud for important ones?

---

## Technical Context

### Existing ValleyTalk Architecture
- C# SMAPI mod with Harmony patching
- Content Patcher data pack for prompts/bios
- DeepSeek via OpenRouter API
- 864+ prompt templates with gender variants
- 33 base game NPCs + 20 SVE NPCs

### Technologies to Research
- SMAPI API for NPC/ Friendship/ Gift manipulation
- Content Patcher advanced features
- Multi-provider LLM client patterns
- RAG/Vector store integration
- Game data extraction (items, schedules, etc.)

---

## Notes
- User wants to start with API provider research and standards document
- Then upgrade API system (multi-provider)
- Then implement Agent features
- Need to balance ambition with feasibility
- Cost is a major concern - dynamic allocation is critical

## Research Findings

### SMAPI API Research (Completed)
- **Event System**: GameLoop.DayStarted, TimeChanged, UpdateTicked, Warped, ButtonsChanged
- **Harmony Patching**: Can intercept NPC methods (tryToReceiveActiveObject, receiveGift, getSchedule)
- **NPC Data Format**: Data/Characters (displayName, birthSeason, home), Data/NPCGiftTastes (love/like/neutral/dislike/hate)
- **Friendship API**: Farmer.changeFriendship(int amount, NPC npc)
- **Dialogue System**: Key formats (dayOfWeek, heart-gated, location-based), commands ($q, $r, $e, etc.)
- **Schedule System**: Time + Location + TileX + TileY + Animation format

### Game Data Sources (Completed)
- **stardew-valley-data npm package**: 1,900+ typed items with query builder API
  - crops(), villagers(), fish(), artisanGoods(), forageable(), craftable()
  - Example: villagers().findByName("Abigail").get().gifts.loved
- **Complete friendship mechanics**: 250 points per heart, gift reactions (+80 to -40)
- **Existing RAG implementations**:
  - Stardew-RAG-Farmhand: Qdrant + Jina embeddings (hybrid search)
  - Stardew-Sage: Redis + Spring AI
  - StardewGPT: Real-time GPT-3.5 NPC dialogue
- **Universal gift categories** documented for all 34 villagers

### Multi-Provider LLM (In Progress)
- Researching OpenCode provider abstraction
- Found references to: LLMKit (.NET), ModelRouter (.NET 10), Microsoft.Extensions.AI, Semantic Kernel, ai-sdk.net (33 providers), OllamaSharp

### NPC Autonomous Action (In Progress)
- Researching NPC movement override feasibility
- Researching schedule system manipulation
- Researching existing follower mods

## New Requirements (Added)

### 4. NPC Autonomous Actions
- [ ] **Follow Player**: NPC can follow player around the map
- [ ] **Farm Work**: Help with farming (planting, watering, harvesting)
- [ ] **Combat Assistance**: Help fight monsters in mines
- [ ] **Decision Making**: AI-driven state machine for NPC behavior
- **Feasibility concern**: May require deep game integration, some features may be impossible

## Design Decisions Needed

### D1: Agent Allocation Strategy
**Recommended**: Hybrid approach
- Base allocation by friendship hearts (0-2: none, 3-4: simple, 5-6: standard, 7-10: advanced)
- Dynamic boost for recently interacted NPCs
- Hard limit: max 5 concurrent advanced agents
- Configurable by player

### D2: Gift Safety Mechanism
**Recommended**: 4-layer protection
1. **Restricted Pool**: Pre-filter gifts by NPC taste + relationship level
2. **RAG Validation**: Retrieve valid gifts from knowledge base
3. **Runtime Check**: Verify item ID exists in ObjectInformation before execution
4. **Frequency Limit**: Max 1 gift per week per NPC

### D3: Friendship Change Rules
**Recommended**: AI autonomous but constrained
- System prompt emphasizes "friendship is precious"
- Soft cap: +30 max per interaction, -20 max
- Daily global limit: total +50 across all NPCs
- Visual indicator when friendship changes (so player notices)

### D4: NPC Autonomous Action Scope
**Needs user decision**: A) Light (follow + basic help) / B) Medium (full schedule + farming) / C) Heavy (combat + complex AI)

## Next Steps
1. ✅ Research SMAPI API and game integration patterns
2. ✅ Research multi-provider LLM implementation (partial)
3. ✅ Research game data sources for RAG
4. ⏳ Research NPC autonomous action feasibility
5. ⏳ Get user decisions on design questions
6. ⏳ Generate comprehensive work plan
