using System.Threading;

namespace FocusCapture.Services.Agent;

/// <summary>
/// Agent 工具基类：框架层唯一认识的工具抽象。
/// 本地工具直接子类化；外发目的地的能力由 OutboundTool 包装成此抽象（框架不知目的地存在）。
/// </summary>
public abstract class AgentTool
{
    /// <summary>工具名（发给模型的 function name，全注册表内唯一）</summary>
    public abstract string Name { get; }

    /// <summary>给模型看的用途描述（含使用前提与限制，写清楚能显著降低误调用率）</summary>
    public abstract string Description { get; }

    /// <summary>JSON Schema 参数定义字符串</summary>
    public abstract string ParametersJson { get; }

    /// <summary>只读工具直接执行；非只读（写/外发）执行前必须过确认闸</summary>
    public abstract bool IsReadOnly { get; }

    /// <summary>执行工具。返回给模型的文本结果；出错抛异常由主循环捕获转写。</summary>
    public abstract Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct);

    /// <summary>确认弹窗里的动作描述（默认为工具名，写类工具应重写成人话）</summary>
    public virtual string DescribeAction(string argumentsJson) => Name;
}
