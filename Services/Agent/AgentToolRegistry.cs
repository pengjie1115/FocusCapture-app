using FocusCapture.Services.AI;

namespace FocusCapture.Services.Agent;

/// <summary>
/// 工具注册表：启动/装配时把本地工具与各外发目的地的能力目录转成的工具统一挂进来。
/// 主循环按名查找。框架层（本目录）不引用任何目的地命名空间——装配发生在使用方（AIDialogWindow）。
/// </summary>
public class AgentToolRegistry
{
    private readonly Dictionary<string, AgentTool> _tools = new(StringComparer.Ordinal);

    /// <summary>注册工具；重名抛异常（装配期错误应当场暴露，不允许静默覆盖）</summary>
    public void Register(AgentTool tool)
    {
        if (string.IsNullOrWhiteSpace(tool.Name))
            throw new ArgumentException("工具名不能为空");
        if (!_tools.TryAdd(tool.Name, tool))
            throw new InvalidOperationException($"工具名重复注册: {tool.Name}");
    }

    public bool TryGetTool(string name, out AgentTool tool) =>
        _tools.TryGetValue(name ?? "", out tool!);

    public IReadOnlyList<ToolDefinition> GetDefinitions() =>
        _tools.Values.Select(t => new ToolDefinition(t.Name, t.Description, t.ParametersJson)).ToList();

    public string DescribeAvailable() =>
        _tools.Count == 0 ? "（无）" : string.Join(", ", _tools.Keys);
}
