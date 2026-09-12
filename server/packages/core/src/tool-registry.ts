import { Value } from "@sinclair/typebox/value";
import type { Tool, ToolResult, ToolVisibility, ValidationResult } from "./tool";

export class ToolRegistry {
  private tools: Map<string, Tool> = new Map();

  register(tool: Tool): void {
    if (this.tools.has(tool.name)) {
      throw new Error(`Tool already registered: ${tool.name}`);
    }
    this.tools.set(tool.name, tool);
  }

  unregister(name: string): void {
    this.tools.delete(name);
  }

  getByName(name: string): Tool | undefined {
    return this.tools.get(name);
  }

  getByVisibility(visibility: ToolVisibility): Tool[] {
    return [...this.tools.values()].filter((t) => t.visibility === visibility);
  }

  getLlmVisibleTools(): Tool[] {
    return this.getByVisibility("llm_visible");
  }

  getAll(): Tool[] {
    return [...this.tools.values()];
  }

  validateCall(name: string, args: Record<string, unknown>): ValidationResult {
    const tool = this.tools.get(name);
    if (!tool) {
      return { valid: false, errors: [`Tool not found: ${name}`] };
    }
    const errors = [...Value.Errors(tool.parameters, args)];
    if (errors.length > 0) {
      return {
        valid: false,
        errors: errors.map((e) => `${e.path}: ${e.message}`),
      };
    }
    return { valid: true };
  }

  async execute(name: string, args: Record<string, unknown>): Promise<ToolResult> {
    const validation = this.validateCall(name, args);
    if (!validation.valid) {
      return {
        content: `Invalid arguments: ${validation.errors!.join(", ")}`,
        isError: true,
      };
    }
    const tool = this.tools.get(name)!;
    try {
      return await tool.execute(args);
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      return {
        content: `Tool execution failed: ${message}`,
        isError: true,
      };
    }
  }
}
