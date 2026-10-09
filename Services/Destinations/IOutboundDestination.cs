using System.Threading;
using FocusCapture.Services.Agent;

namespace FocusCapture.Services.Destinations;

/// <summary>一条外发能力（数据，不是类）。适配器用它声明"我能做什么"。</summary>
public sealed record OutboundCapability(
    string Name,
    string Description,
    string ParametersJson,
    bool IsReadOnly);

/// <summary>执行结果（给模型读的文本）；NoteId 仅保存类能力回填，供按钮路径去重映射使用</summary>
public sealed record OutboundResult(bool Success, string Message, string? NoteId = null);

/// <summary>
/// 外发目的地接口：能力目录声明 + 通用执行入口。
/// 新增目的地（飞书/金山/…）= 新增一个实现类 + 注册一行，框架与既有工具零改动。
/// </summary>
public interface IOutboundDestination
{
    /// <summary>目的地显示名（如"得到大脑"）</summary>
    string Name { get; }

    /// <summary>能力目录：每条会转成一个独立工具挂进注册表</summary>
    IReadOnlyList<OutboundCapability> Capabilities { get; }

    /// <summary>按能力名执行。参数校验/HTTP 细节/错误翻译全部封装在适配器内部。</summary>
    Task<OutboundResult> ExecuteAsync(string capabilityName, string argumentsJson, CancellationToken ct);
}

/// <summary>把目的地的单条能力包装成 AgentTool（通用执行入口，无每能力一个类）</summary>
public class OutboundTool : AgentTool
{
    private readonly IOutboundDestination _destination;
    private readonly OutboundCapability _capability;

    public OutboundTool(IOutboundDestination destination, OutboundCapability capability)
    {
        _destination = destination;
        _capability = capability;
    }

    public override string Name => _capability.Name;
    public override string Description => _capability.Description;
    public override string ParametersJson => _capability.ParametersJson;
    public override bool IsReadOnly => _capability.IsReadOnly;

    public override string DescribeAction(string argumentsJson) =>
        $"通过{_destination.Name}执行：{_capability.Description.Split('。')[0]}（参数：{argumentsJson}）";

    public override async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        var result = await _destination.ExecuteAsync(_capability.Name, argumentsJson, ct);
        return result.Success ? result.Message : $"错误：{result.Message}";
    }
}
