using System.Collections.Generic;
using System.Text;

namespace ValleyAgent.Utils;

/// <summary>
///     E2-3 长文本规则：按句切分器（纯函数，无游戏依赖）。
///     把 NPC 说话文本切成"句子"，供显示规则判断 ≤1 句走气泡、&gt;1 句走聊天栏分句弹出。
///     切分规则：
///     - 句末分隔符：中文 。！？（U+3002 / U+FF01 / U+FF1F）+ 英文 .?! + 可选 ；…（U+FF1B / U+2026）。
///     - 连续句末标点合并为一次切分（"！！"、"！？"、"……" 只算一句末）。
///     - 英文句点 '.' 仅在后面紧跟 空白 / 结尾 / CJK 字符 时视为句末，
///     避免 "3.14"、"Mr.Smith" 误切。
///     - 句末标点后的闭合引号/括号归入本句（"「今天真棒！」"、"他说：'好的。'" 各为完整一句）。
///     - 标点随句保留，句子首尾空白裁剪，空句丢弃。
///     CJK 检测复用 LanguageValidation.IsCjkChar 的邻近写法（同款码位区间，自包含私有实现，
///     不盲目把该私有方法公开 — 见 Config/LanguageValidation.cs:84-119）。
///     设计依据：docs/ideas/2026-08-03-e23-long-text-rules.md。
/// </summary>
public static class SentenceSplitter
{
    /// <summary>
    ///     按句切分文本。空串 / 全空白返回空列表。
    /// </summary>
    public static IReadOnlyList<string> Split(string text)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            return result;
        }

        var sb = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            sb.Append(c);

            if (!IsSentenceTerminator(c))
            {
                continue;
            }

            // 合并连续句末标点（！！/！？/……）→ 一次切分
            while (i + 1 < text.Length && IsSentenceTerminator(text[i + 1]))
            {
                sb.Append(text[i + 1]);
                i++;
            }

            // 英文句点需后跟 空白/结尾/CJK 才切分；后接数字或 Latin 字母（3.14 / Mr.Smith）
            // 则是小数点/缩写，不切分。检查的是合并后整个标点串之后的首个字符。
            if (c == '.')
            {
                var next = i + 1 < text.Length ? text[i + 1] : '\0';
                if (next != '\0' && (char.IsDigit(next) || (char.IsLetter(next) && !IsCjkChar(next))))
                {
                    continue; // 不是句末（如 3.14 / Mr.X）→ 继续累积
                }
            }

            // 句末标点后的闭合引号/括号归入本句（「今天真棒！」、他说："好的。"）
            while (i + 1 < text.Length && IsClosingPunctuation(text[i + 1]))
            {
                sb.Append(text[i + 1]);
                i++;
            }

            Flush(sb, result);
        }

        Flush(sb, result);
        return result;
    }

    /// <summary>
    ///     句子数量（≥1 即"多句长文"的判定基础；空文本返回 0）。
    /// </summary>
    public static int CountSentences(string text) => Split(text).Count;

    /// <summary>
    ///     句末分隔符：中文句号/叹号/问号 + 英文句号/叹号/问号 + 分号/省略号（可选分隔，默认开启）。
    /// </summary>
    private static bool IsSentenceTerminator(char c) =>
        c is '\u3002' or '\uff01' or '\uff1f' or '.' or '!' or '?' or '\uff1b' or '\u2026';

    /// <summary>
    ///     句末标点之后的闭合引号/括号（」』）】》 + 引号 + ASCII 右括号）。
    ///     这些字符应随句子走，不拆成下一句的开头。
    /// </summary>
    private static bool IsClosingPunctuation(char c)
    {
        return c is '\u300d' or '\u300f' or '\uff09' or '\u3011' or '\u300b'
            or '\u0027' or '\u0022' or '\u2019' or '\u201d'
            or ')' or ']' or '}' or '>';
    }

    /// <summary>
    ///     把积累的句子裁剪空白后加入结果（空句丢弃）。
    /// </summary>
    private static void Flush(StringBuilder sb, List<string> result)
    {
        var sentence = sb.ToString().Trim();
        if (sentence.Length > 0)
        {
            result.Add(sentence);
        }

        sb.Clear();
    }

    /// <summary>
    ///     CJK 字符检测（与 LanguageValidation.IsCjkChar 同款码位区间，按需裁剪）。
    ///     仅供英文句点 '.' 的句末判定使用：'.' 后跟 CJK 视为"跨语言句末"（"Good morning.今天好吗"）。
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

        // CJK Symbols and Punctuation
        if (code is >= 0x3000 and <= 0x303F)
        {
            return true;
        }

        // Fullwidth forms（FF01~FF5E，全角 ASCII 变体，按 CJK 语境处理）
        if (code is >= 0xFF01 and <= 0xFF5E)
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
}