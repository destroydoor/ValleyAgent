# ValleyTalk AI Agent Upgrade - Decisions

## Architecture Decisions
- Greenfield rewrite (no source code available)
- StateMachine + Controller pattern (from NPC Adventures)
- ILLMProvider interface for multi-provider support
- IActionValidator for safety layer
- AgentAllocationManager for dynamic NPC assignment
- AIDecisionEngine for LLM async decision making
- CircuitBreaker for failure handling

## API Security
- LM Studio: no API key needed (local)
- Remote APIs: environment variables or config (never hardcoded)
- Exposed API key in original config.json must NOT be committed

## Cost Control
- Default: 1 Agent NPC
- Max: 5 Agent NPCs
- Token budget per session (configurable, default 0 = unlimited)
- Local-first: LM Studio preferred for zero cost