import type { TSchema } from "@sinclair/typebox";

export type ToolVisibility = "llm_visible" | "tactical" | "local";

export interface ToolResult {
  content: string;
  details?: Record<string, unknown>;
  isError?: boolean;
  terminate?: boolean;
}

export interface Tool {
  name: string;
  description: string;
  visibility: ToolVisibility;
  parameters: TSchema;
  execute: (args: Record<string, unknown>) => Promise<ToolResult>;
}

export interface ValidationResult {
  valid: boolean;
  errors?: string[];
}
