using System.Linq;

namespace ValleyAgent.Config;

/// <summary>
///     语言校验 + 极端重生成机制。
///     检测 LLM 输出的第一个有效 token 是否为目标语言，
///     如果不是则触发重生成（一次），自定义模式不受限。
/// </summary>
public static class LanguageValidation
{
    /// <summary>
    ///     检查文本的语言是否匹配目标模式。
    ///     Custom 模式始终返回 true（不受限）。
    /// </summary>
    public static bool IsMatch(string text, LanguageMode mode)
    {
        if (mode == LanguageMode.Custom)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        // 找到第一个有意义的字符（跳过空白、标点、引号、括号）
        var firstMeaningful = text.TrimStart()
            .FirstOrDefault(c => !IsSkippableChar(c));

        if (firstMeaningful == default)
        {
            return true;
        }

        var isCjk = IsCjkChar(firstMeaningful);

        return mode switch
        {
            LanguageMode.Chinese => isCjk,
            LanguageMode.English => !isCjk,
            LanguageMode.Custom => true,
            _ => true
        };
    }

    /// <summary>
    ///     是否跳过该字符（标点、引号、括号、空格等开头常见字符）。
    /// </summary>
    private static bool IsSkippableChar(char c)
    {
        if (char.IsWhiteSpace(c))
        {
            return true;
        }

        if (char.IsPunctuation(c))
        {
            return true;
        }

        if (char.IsSeparator(c))
        {
            return true;
        }

        // 常见英文/中文引号括号
        return c switch
        {
            '\"' or '\'' or '`' or '´' or '‘' or '’' or '“' or '”' => true,
            '(' or ')' or '[' or ']' or '{' or '}' or '<' or '>' => true,
            '《' or '》' or '「' or '」' or '『' or '』' => true,
            '【' or '】' or '（' or '）' => true,
            '…' or '—' or '～' => true,
            _ => false
        };
    }

    /// <summary>
    ///     CJK 字符检测（中、日、韩统一表意文字 + 扩展区）。
    /// </summary>
    private static bool IsCjkChar(char c)
    {
        int code = c;
        // CJK Unified Ideographs
        if (code is >= 0x4E00 and <= 0x9FFF)
        {
            return true;
        }

        // CJK Extension A
        if (code is >= 0x3400 and <= 0x4DBF)
        {
            return true;
        }

        // CJK Extension B
        if (code is >= 0x20000 and <= 0x2A6DF)
        {
            return true;
        }

        // CJK Compatibility Ideographs
        if (code is >= 0xF900 and <= 0xFAFF)
        {
            return true;
        }

        // Fullwidth forms (FF01~FF5E are fullwidth ASCII variants, but they count as CJK context)
        if (code is >= 0xFF01 and <= 0xFF5E)
        {
            return true;
        }

        // CJK Symbols and Punctuation
        if (code is >= 0x3000 and <= 0x303F)
        {
            return true;
        }

        // Hiragana / Katakana
        if (code is >= 0x3040 and <= 0x30FF)
        {
            return true;
        }

        // Hangul Syllables
        return code is >= 0xAC00 and <= 0xD7AF;
    }

    /// <summary>
    ///     获取语言校验失败后的强化语言指令。
    /// </summary>
    public static string GetRetryInstruction(LanguageMode mode)
    {
        return mode switch
        {
            LanguageMode.Chinese =>
                "CRITICAL: Your previous response was NOT in Chinese. " +
                "You MUST rewrite the ENTIRE response in Chinese (中文). " +
                "Every text field must use Chinese characters. " +
                "Do NOT use English for any text content.",
            LanguageMode.English =>
                "CRITICAL: Your previous response was NOT in English. " +
                "You MUST rewrite the ENTIRE response in English. " +
                "Every text field must use English. " +
                "Do NOT use Chinese for any text content.",
            LanguageMode.Custom => "",
            _ => ""
        };
    }

    /// <summary>
    ///     获取语言指令（用于初始 prompt 的强化语言要求）。
    ///     中文模式：要求使用中文，否则重生成。
    ///     英文模式：要求使用英文，否则重生成。
    ///     自定义模式：无要求。
    /// </summary>
    public static string GetLanguageInstruction(LanguageMode mode)
    {
        return mode switch
        {
            LanguageMode.Chinese =>
                "LANGUAGE REQUIREMENT (STRICT): You MUST respond in Chinese (中文). " +
                "The first character of your response MUST be a Chinese character. " +
                "If you respond in any other language, you will be forced to regenerate.",
            LanguageMode.English =>
                "LANGUAGE REQUIREMENT (STRICT): You MUST respond in English. " +
                "The first character of your response MUST be an English letter. " +
                "If you respond in Chinese or any other language, you will be forced to regenerate.",
            LanguageMode.Custom => "",
            _ => ""
        };
    }
}