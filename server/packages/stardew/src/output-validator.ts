import type { ToolAction } from "./types";

const MIN_CJK_RATIO = 0.3;

export interface ValidationResult {
  valid: boolean;
  reason?: string;
}

export class OutputValidator {
  /**
   * Count CJK characters in text (Hiragana/Katakana/Han/Hangul ranges)
   */
  private countCjk(text: string): number {
    let count = 0;
    for (const ch of text) {
      const code = ch.codePointAt(0) ?? 0;
      // Hiragana: 0x3040-0x309F
      // Katakana: 0x30A0-0x30FF
      // CJK Unified Ideographs: 0x4E00-0x9FFF
      // Hangul Syllables: 0xAC00-0xD7AF
      if (
        (code >= 0x3040 && code <= 0x309F) ||
        (code >= 0x30A0 && code <= 0x30FF) ||
        (code >= 0x4E00 && code <= 0x9FFF) ||
        (code >= 0xAC00 && code <= 0xD7AF)
      ) {
        count++;
      }
    }
    return count;
  }

  validate(text: string): ValidationResult {
    if (!text || text.trim().length === 0) {
      return { valid: false, reason: "empty text" };
    }
    const cjkCount = this.countCjk(text);
    const totalChars = text.length;
    if (totalChars === 0) {
      return { valid: false, reason: "empty text" };
    }
    const ratio = cjkCount / totalChars;
    if (ratio < MIN_CJK_RATIO) {
      return { valid: false, reason: `CJK ratio ${ratio.toFixed(2)} < ${MIN_CJK_RATIO}` };
    }
    return { valid: true };
  }

  buildRetryPrompt(badText: string): string {
    return `你刚才的回复 "${badText.slice(0, 100)}" 不符合要求。请用中文重新回复，确保回复主要为中文，且内容简短（1-2句话）。Please respond in Chinese.`;
  }

  /**
   * Validate the final speech + actions extracted from agentLoop events.
   * Accepts if EITHER the speech field OR any speak/show_dialogue tool's text is valid.
   */
  validateSpeechAndActions(speech: string, actions: ToolAction[]): ValidationResult {
    // Check speak/show_dialogue tool args first
    for (const action of actions) {
      if (action.tool === "speak" || action.tool === "show_dialogue") {
        const text = String(action.args.text ?? "");
        const result = this.validate(text);
        if (result.valid) return { valid: true };
      }
    }
    // Fall back to speech field
    return this.validate(speech);
  }
}
