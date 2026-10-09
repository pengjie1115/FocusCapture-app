using FocusCapture.Services.AI;

namespace FocusCapture.Services.AI;

/// <summary>
/// 动态提示词组装：根据输入形态选择词典式释义或整段翻译，禁止单模板走天下。
/// </summary>
public static class PromptBuilder
{
    /// <summary>
    /// 当前时间的一句话描述（2026-09-17）。**口径唯一**：Agent 每轮附加上下文与时间解析兜底提示词共用。
    ///
    /// 为什么必须每轮求值而不是写进系统提示词：系统提示词会被持久化进会话文件
    /// （ChatSessionService.Save 里的 SystemPrompt），跨天之后那里面就是**过期的日期** —— 比不告诉模型更糟。
    /// </summary>
    public static string DescribeNow(DateTime now)
    {
        var weekday = "日一二三四五六"[(int)now.DayOfWeek];
        return $"{now:yyyy-MM-dd HH:mm} 星期{weekday}";
    }

    /// <summary>短词判定：≤30 字符且不含空格 → 词典式释义；否则整段翻译</summary>
    public static bool IsShortWord(string text)
    {
        var trimmed = (text ?? "").Trim();
        return trimmed.Length <= 30 && !trimmed.Any(char.IsWhiteSpace);
    }

    public static string BuildTranslatePrompt(string text)
    {
        var trimmed = (text ?? "").Trim();
        if (IsShortWord(trimmed))
        {
            return $"请解释这个单词/短语：{trimmed}\n" +
                   "请以词典式输出：词性、释义、常见搭配、例句。";
        }
        return $"请把以下内容翻译成中文，并简要解释其含义：\n\n{trimmed}";
    }

    /// <summary>
    /// v3.5 待办编辑时间识别 LLM 兜底提示词：要求只输出严格 JSON，无时间 has_time=false。
    /// 2026-09-17 补：**必须给出当前时间**才能解析相对时间表达 —— 没有基准点时
    /// "明天中午 12 点""下周三"只能靠猜（本地 TimeParser 兜不住的长尾才会走到这里，猜错就是错提醒）。
    /// </summary>
    public static ChatMessage[] BuildTimeParseMessages(string text, DateTime now)
    {
        return new[]
        {
            new ChatMessage(ChatRoles.System,
                $"你是一个时间解析器。当前时间是 {DescribeNow(now)}。" +
                "从用户文本中找出唯一的提醒时间表达，输出严格 JSON：{\"has_time\":true/false,\"time\":\"yyyy-MM-dd HH:mm\"}（无时间则 has_time=false）。只输出 JSON，不要解释。"),
            new ChatMessage(ChatRoles.User, text)
        };
    }
}
