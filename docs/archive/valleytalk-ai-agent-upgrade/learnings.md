

## Gift System Architecture (Task 10)
- **4-Layer Safety**: (1) Restricted gift pool per NPC, (2) RAG context validation, (3) Runtime item validation (exists, obtainable, not legendary/quest), (4) Frequency limit (1 gift/week NPC→Player, 1 gift/day Player→NPC)
- **NPC→Player gifts**: AI-initiated via `CanNpcGiveGift()` with friendship threshold (1 heart), festival boost, random chance (5% base + 15% at 10 hearts)
- **Player→NPC gifts**: LLM evaluates appropriateness and generates reaction dialogue; falls back to RAG-only if LLM fails
- **Gift pool**: Built from RAG data (loved + liked + 3 neutral items per NPC); customizable via `SetRestrictedGiftPool()`
- **History tracking**: `GiftHistoryTracker` with thread-safe date parsing for weekly limits
- **Friendship integration**: Uses base points (Love=80, Like=45, Neutral=20, Dislike=-20, Hate=-40) with birthday (8x) and festival (1.5x) multipliers
- **Thread safety**: `SemaphoreSlim(1,1)` for LLM calls, `lock` for gift pools and history
- **Events**: `OnGiftGiven` (NPC→Player), `OnGiftReceived` (Player→NPC), `OnHistoryUpdated`
- **Game-agnostic**: No SMAPI types; uses RAGKnowledgeBase, FriendshipSystem, ILLMProvider interfaces
- **Build verification**: Created isolated test project with stub dependencies to verify compilation (full build requires Stardew Valley game folder)
- **Key files**: `src/ValleyTalk/Gifts/GiftSystem.cs` (569 lines)
