export const STARDEW_VERSION = "0.1.0";

// Phase 1 E1-1：全量留痕存储（TranscriptStore）+ 记录类型。
export { TranscriptStore } from "./transcript-store";
export type { TranscriptSummary } from "./transcript-store";
export type {
  AgentRunRecord,
  AgentRunTrigger,
  AgentRunStatus,
  AgentRunValidation,
  AgentRunFallback,
  AgentRunTokens,
  AgentTurnRecord,
  DirectorRunRecord,
  DirectorRunStatus,
} from "./transcript-types";