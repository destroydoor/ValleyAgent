# ValleyTalk Upgrade - Design Draft

## Core Requirements (Confirmed)
1. **Multi-provider LLM API**: Support local APIs (LM Studio) AND Kimi API
2. **Dynamic Agent Allocation**: Cost-controlled intelligent NPC system
3. **Autonomous Friendship/Gift Systems**: Anti-hallucination safety mechanisms
4. **RAG Enhancement**: Wiki data integration for accuracy
5. **NPC Autonomous Actions**: Follow, farm, combat capabilities

## Technical Decisions

### Agent Allocation Strategy (Confirmed)
- **Default**: Only 1 NPC has advanced intelligence (cost control)
- **Manual Override**: Player can set additional NPCs to have advanced intelligence
- **Dynamic System**: Automatically adjusts which NPCs get advanced intelligence based on:
  - Player conversation frequency with each NPC
  - Gift frequency
  - Friendship level (hearts)
  - Recent interaction history

### Multi-Provider LLM Architecture
- **Unified Interface**: Abstract LLM provider (OpenAI/OpenRouter/Kimi/LM Studio)
- **Dynamic Switching**: 
  - High-frequency NPCs → Local LM Studio (cost-free)
  - Critical NPCs → Kimi/DeepSeek (high quality)
- **Fallback Chain**: Primary → Secondary → Local

### Gift System Safety (4-Layer)
1. **Whitelist**: Only real in-game items
2. **RAG Verification**: Query NPC gift tastes
3. **Runtime Check**: SMAPI `getGiftTaste()` != Hate
4. **Frequency Limit**: Max 2 gifts per week per NPC

### Friendship Change Controls
- **Soft Cap Increase**: Max +30 per interaction
- **Soft Cap Decrease**: Max -20 per interaction  
- **Critical Events**: Max +60 (festivals, birthdays)

## Research Findings

### SMAPI API Capabilities
- `NPC` class: `CurrentDialogue`, `Friendship`, `GiftTaste`
- `Harmony` patches: Intercept dialogue requests, gift events
- `Schedule` system: `NPC.dayUpdate()` for behavior modification

### Game Data Sources
- `stardew-valley-data` npm package: 1,900+ items, NPC data, festivals
- **Stardew-RAG-Farmhand**: Reference implementation (Qdrant + Jina, 200+ stars)

### NPC Autonomous Actions (Pending Decision)
- **Light (Recommended)**: Follow player, simple animations, temporary dialogue
- **Medium**: Help water crops, simple foraging, follow to mines
- **Heavy**: Full farm assistant (planting, harvesting, animal care, mine combat)

## Decisions Made
1. ✅ **NPC Autonomous Action Scope**: **Medium** - Help water crops, simple foraging, follow to mines
2. ✅ **API Key Security**: **Hybrid Mode** - Local LM Studio needs no key, remote APIs use environment variables
3. ✅ **RAG Implementation**: **Use Stardew-RAG-Farmhand directly** - 1,000+ wiki pages indexed, mature solution

## Open Questions
1. **Content Patcher Integration**: Keep existing CP packs or migrate to pure C#?
2. **Dynamic Allocation Algorithm**: Specific scoring formula for NPC intelligence priority?
3. **NPC Action Frequency**: How often should medium-tier NPCs perform actions (every X minutes)?

## Scope Boundaries
- **INCLUDE**: C# SMAPI mod, LLM abstraction layer, dynamic agent system, RAG integration
- **EXCLUDE**: New NPC sprites/art, new maps, multiplayer synchronization (Phase 2)
