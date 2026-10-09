namespace FocusCapture.Services.Sync;

/// <summary>
/// 分组清单的跨端合并裁决（2026-09-23 从 ChatSyncEngine 抽出来）。
///
/// 抽出来的理由：这三条规则是「跨端改名不丢」的唯一保障，却埋在同步引擎的私有方法里，
/// 除了跑真实双设备没法验证。抽成 public 纯函数后可以直接写检查点 ——
/// 与项目既有做法一致（能抽成纯函数的一律抽出来单独测）。
///
/// 它们只处理对象字段，不做 IO、不碰加密，所以能脱离同步引擎独立验证。
/// </summary>
public static class ChatGroupMerge
{
    /// <summary>
    /// 同一分组在两端都有时的字段级合并：名称 / 指令各按自己的时间戳取新。
    ///
    /// **时间戳相等（含两端都是旧数据的 default）时保持「本地 wins」** ——
    /// 这是刻意的：旧版清单没有这两个时间戳字段，升级后不能因为一条新规则把用户本地现有的名字改掉。
    /// </summary>
    public static ChatGroup MergeSameId(ChatGroup cloud, ChatGroup local)
    {
        var result = new ChatGroup
        {
            Id = local.Id,
            // CreatedAt 取更早的（"创建时间"语义）。注意它**不参与 IsSameGroup 比较** ——
            // 一旦参与，两端 CreatedAt 不同就会每轮都判为"有变化"→ 触发落盘 → 空转同步。
            CreatedAt = cloud.CreatedAt <= local.CreatedAt ? cloud.CreatedAt : local.CreatedAt,
            DeviceId = string.IsNullOrEmpty(local.DeviceId) ? cloud.DeviceId : local.DeviceId,
        };

        var localNameWins = local.NameUpdatedAt >= cloud.NameUpdatedAt;
        result.Name = localNameWins ? local.Name : cloud.Name;
        result.NameUpdatedAt = localNameWins ? local.NameUpdatedAt : cloud.NameUpdatedAt;

        var localInstructionWins = local.InstructionUpdatedAt >= cloud.InstructionUpdatedAt;
        result.Instruction = localInstructionWins ? local.Instruction : cloud.Instruction;
        result.InstructionUpdatedAt = localInstructionWins ? local.InstructionUpdatedAt : cloud.InstructionUpdatedAt;

        // 置顶状态同样按时间戳取新。**bool 字段尤其依赖时间戳** ——
        // 只看"哪边是真"是不行的：用户取消置顶（false）也是有效意图，没有时间戳就分不清
        // 它和"本地是没更新的旧数据"，取消动作会被云端 true 顶回来。
        var localPinWins = local.PinnedUpdatedAt >= cloud.PinnedUpdatedAt;
        result.Pinned = localPinWins ? local.Pinned : cloud.Pinned;
        result.PinnedUpdatedAt = localPinWins ? local.PinnedUpdatedAt : cloud.PinnedUpdatedAt;

        MergeTombstone(result, cloud, local);
        return result;
    }

    /// <summary>墓碑裁决（2026-09-24）：**删除即终态** —— 任一端带墓碑，合并结果就是墓碑。
    ///
    /// 为什么删除胜过一切修改（包括时间戳更新的修改）：并集合并表达不了"删除"，这是唯一能
    /// 保证"删了就永远不复活"的规则（用户 2026-09-24 拍板：墓碑永久保留）。
    /// 代价是另一端对已删分组的修改会丢 —— 但那个分组在用户眼里已经不存在了，可以接受。
    /// DeletedAt 取两端较早的非空值：时间更早意味着压制力覆盖更早的云端修改。</summary>
    public static void MergeTombstone(ChatGroup result, ChatGroup cloud, ChatGroup local)
    {
        if (cloud.IsTombstone && local.IsTombstone)
            result.DeletedAt = cloud.DeletedAt < local.DeletedAt ? cloud.DeletedAt : local.DeletedAt;
        else if (cloud.IsTombstone)
            result.DeletedAt = cloud.DeletedAt;
        else if (local.IsTombstone)
            result.DeletedAt = local.DeletedAt;
        // 两端都活着 → 保持 default（未删除）
    }

    /// <summary>同名分组合并时，把败者更新的名称 / 指令 / 置顶 / 墓碑并进胜者（按时间戳取新）。
    /// 不做这一步的话，"早建的分组"会把"晚改的指令"一起吞掉。</summary>
    public static void MergeLoserIntoWinner(ChatGroup winner, ChatGroup loser)
    {
        if (loser.NameUpdatedAt > winner.NameUpdatedAt)
        {
            winner.Name = loser.Name;
            winner.NameUpdatedAt = loser.NameUpdatedAt;
        }
        if (loser.InstructionUpdatedAt > winner.InstructionUpdatedAt)
        {
            winner.Instruction = loser.Instruction;
            winner.InstructionUpdatedAt = loser.InstructionUpdatedAt;
        }
        if (loser.PinnedUpdatedAt > winner.PinnedUpdatedAt)
        {
            winner.Pinned = loser.Pinned;
            winner.PinnedUpdatedAt = loser.PinnedUpdatedAt;
        }
        // 墓碑是终态，直接传播（同名合并的败者若在他端已删，胜者也必须死，否则下轮复活）
        if (loser.IsTombstone && !winner.IsTombstone)
        {
            winner.DeletedAt = loser.DeletedAt;
        }
        else if (winner.IsTombstone && loser.IsTombstone && loser.DeletedAt < winner.DeletedAt)
        {
            winner.DeletedAt = loser.DeletedAt;
        }
    }

    /// <summary>
    /// 两条分组记录的「可变内容」是否等价 —— 决定合并结果要不要落盘。
    ///
    /// 为什么它重要：`ChatGroupStore.Save` 会触发 `GroupsChanged` → 启动上传防抖窗口。
    /// 如果这个判断偏保守（把没变的判成变了），每轮同步都会触发下一轮同步，形成
    /// 「同步 → 30s → 同步」的空转，白烧坚果云 600 请求/30min 的额度。
    ///
    /// 刻意**不比 CreatedAt / Id** —— 它们不可变，比了只会制造"假变化"。
    /// </summary>
    public static bool IsSameGroup(ChatGroup a, ChatGroup b) =>
        string.Equals(a.Name, b.Name, StringComparison.Ordinal)
        && string.Equals(a.Instruction, b.Instruction, StringComparison.Ordinal)
        && string.Equals(a.DeviceId, b.DeviceId, StringComparison.Ordinal)
        && a.NameUpdatedAt == b.NameUpdatedAt
        && a.InstructionUpdatedAt == b.InstructionUpdatedAt
        && a.Pinned == b.Pinned
        && a.PinnedUpdatedAt == b.PinnedUpdatedAt
        && a.DeletedAt == b.DeletedAt;
}
