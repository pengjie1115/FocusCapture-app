namespace FocusCapture.Services;

/// <summary>
/// 分组业务操作的**唯一入口**（2026-09-23 新建）。
///
/// 为什么必须收敛到一个类：原先「新建分组」有两个入口各自实现，撞名行为还不一致 ——
/// ChatGroupsWindow 弹框拒绝、HistoryDrawer 静默复用已有分组（用户以为新建了，实际进了老分组，全程无提示）。
/// 现在查重策略一处定义、落盘与变更通知一处负责、删除顺序一处保证。
///
/// 与 ChatGroupStore 的分工：Store 只管「清单读写 + 锁 + 通知」，
/// 本类管「业务语义」（查重、删组时把会话放回未分组、悬空引用自愈）。
/// </summary>
public static class ChatGroupService
{
    /// <summary>分组指令长度上限。超长截断（防止用户把整篇文档贴进去撑爆每次请求的上下文）。</summary>
    public const int MaxInstructionChars = 2000;

    /// <summary>新建分组的结果</summary>
    public enum CreateResult
    {
        /// <summary>新建成功</summary>
        Created,
        /// <summary>名字为空</summary>
        EmptyName,
        /// <summary>已存在同名分组 —— **拒绝**，不静默复用</summary>
        NameExists,
    }

    /// <summary>
    /// 新建分组。成功返回新分组对象，失败返回 null 并给出原因。
    ///
    /// 撞名策略（2026-09-23 定）：**拒绝并提示**，不做静默复用 ——
    /// 静默复用会让用户以为建了新组、实际把会话塞进了老组，而且没有任何提示，是纯粹的陷阱。
    /// </summary>
    public static ChatGroup? Create(string name, out CreateResult result)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) { result = CreateResult.EmptyName; return null; }

        ChatGroup? created = null;
        var duplicated = false;

        ChatGroupStore.Mutate(groups =>
        {
            if (groups.Any(g => string.Equals(g.Name, name, StringComparison.Ordinal)))
            {
                duplicated = true;
                return false;
            }
            created = new ChatGroup
            {
                Id = Guid.NewGuid().ToString(),
                Name = name,
                CreatedAt = DateTime.Now,
                NameUpdatedAt = DateTime.Now,   // 跨端 LWW 判方向
            };
            groups.Add(created);
            return true;
        });

        result = duplicated ? CreateResult.NameExists : CreateResult.Created;
        return duplicated ? null : created;
    }

    /// <summary>
    /// 重命名分组。收藏保留分区不可改名。成功 / 无变化返回 true，失败返回 false 并给出原因。
    /// </summary>
    public static bool Rename(string id, string newName, out string error)
    {
        newName = (newName ?? "").Trim();
        if (newName.Length == 0) { error = "分组名不能为空"; return false; }
        if (ChatGroupStore.IsFavorite(id)) { error = "收藏分区不能重命名"; return false; }

        var ok = false;
        string? failure = null;

        ChatGroupStore.Mutate(groups =>
        {
            var target = groups.FirstOrDefault(g => g.Id == id);
            if (target == null)
            {
                failure = "分组不存在（可能已在另一台设备上删除）";
                return false;
            }
            if (string.Equals(target.Name, newName, StringComparison.Ordinal))
            {
                ok = true;          // 名字没变，不算失败也无需落盘
                return false;
            }
            if (groups.Any(g => g.Id != id && string.Equals(g.Name, newName, StringComparison.Ordinal)))
            {
                failure = "已存在同名分组";
                return false;
            }
            target.Name = newName;
            target.NameUpdatedAt = DateTime.Now;   // 跨端 LWW 判方向：没有它，改名会在另一台设备上被回滚
            ok = true;
            return true;
        });

        error = failure ?? "";
        return ok;
    }

    /// <summary>
    /// 删除分组：**先把组内会话放回未分组，再把分组记录标记为删除墓碑** —— 顺序不可颠倒。
    ///
    /// 为什么是墓碑而不是真删：分组合并是"本地 ∪ 云端"的并集，并集表达不了"删除"——
    /// 真删的话，云端那份还在，下一轮同步就把它并回来（用户实测"删除分组后一会又复活"；
    /// 会话那条 2026-09-23 已有删除清单防线，分组是漏网的）。
    /// 语义（用户拍板）：删除即终态 + 墓碑永久保留，任一端删了两端都永远消失。
    ///
    /// 旧实现是先删记录、再逐个改会话，中途失败会留下「分组没了但会话还挂着失效 GroupId」，
    /// 界面上表现为冒出「（未知分组）」幽灵分区 —— 先清会话的顺序保持不变。
    /// 收藏保留分区不可删除。
    /// </summary>
    public static bool DeleteGroup(string id, out string error)
    {
        if (ChatGroupStore.IsFavorite(id)) { error = "收藏分区不能删除"; return false; }

        // ① 先清会话引用：指向该分组的会话全部回到未分组。
        //    逐个 Load→改→Save：Save 会自增 Rev 并触发 SessionChanged，删除结果随同步管道传到他端。
        var moved = 0;
        try
        {
            var dir = FocusCapturePaths.Combine("chat_history");
            if (Directory.Exists(dir))
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
                {
                    try
                    {
                        var svc = ChatSessionService.Load(file);
                        if (svc == null || !string.Equals(svc.GroupId, id, StringComparison.Ordinal)) continue;
                        svc.GroupId = "";
                        svc.Save();
                        moved++;
                    }
                    catch (Exception ex)
                    {
                        // 单个会话文件失败不影响其余 —— 剩下的靠 RepairDanglingGroupIds 兜底
                        Debug.WriteLine($"[FocusCapture] 分组删除时重置会话分组失败: {file}: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FocusCapture] 分组删除遍历会话失败: {ex.Message}");
        }

        // ② 标记删除（墓碑留在清单里，Load() 会把它过滤出 UI；同步引擎拿它压制云端残留）
        var removed = false;
        ChatGroupStore.Mutate(groups =>
        {
            var target = groups.FirstOrDefault(g => g.Id == id);
            if (target == null) return false;
            target.DeletedAt = DateTime.Now;   // 墓碑时间：另一端以它为界判定"这个分组已死"
            removed = true;
            return true;
        });

        if (removed) AppLog.Info("ChatGroup", $"已删除分组 {id}（墓碑保留），{moved} 个会话回到未分组");
        error = removed ? "" : "分组不存在（可能已在另一台设备上删除）";
        return removed;
    }

    /// <summary>
    /// 写入分组指令（Agent.md 等价物）。收藏分区不支持指令。超长自动截断到 <see cref="MaxInstructionChars"/>。
    /// </summary>
    public static bool SetInstruction(string id, string instruction)
    {
        instruction = instruction ?? "";
        if (instruction.Length > MaxInstructionChars) instruction = instruction[..MaxInstructionChars];
        if (ChatGroupStore.IsFavorite(id)) return false;

        var ok = false;
        ChatGroupStore.Mutate(groups =>
        {
            var target = groups.FirstOrDefault(g => g.Id == id);
            if (target == null) return false;
            if (string.Equals(target.Instruction, instruction, StringComparison.Ordinal))
            {
                ok = true;
                return false;   // 内容没变，不落盘（避免每次点确定都刷新同步时间戳）
            }
            target.Instruction = instruction;
            target.InstructionUpdatedAt = DateTime.Now;   // 跨端 LWW 判方向
            ok = true;
            return true;
        });
        return ok;
    }

    /// <summary>
    /// 置顶 / 取消置顶一个分组（2026-09-24）。收藏保留分区不支持 —— 它本来就恒在列表最前。
    ///
    /// 语义：置顶的分组排到分组列表最前（收藏之后）。与「置顶会话」是两件事：
    /// 前者改的是分组清单里的 ChatGroup.Pinned，后者改的是会话文件里的 SessionFile.Pinned。
    /// </summary>
    public static bool SetPinned(string id, bool pinned)
    {
        if (ChatGroupStore.IsFavorite(id)) return false;   // 收藏恒在最前，不需要也不能置顶

        var ok = false;
        ChatGroupStore.Mutate(groups =>
        {
            var target = groups.FirstOrDefault(g => g.Id == id);
            if (target == null) return false;
            if (target.Pinned == pinned) { ok = true; return false; }   // 状态没变，不落盘、不刷新时间戳
            target.Pinned = pinned;
            // ⚠️ 取消置顶（pinned=false）走到这里也必须刷新时间戳 ——
            // 少了这一步，跨端合并分不清"本地取消置顶"和"本地还是旧数据"，会把它顶回 true。
            target.PinnedUpdatedAt = DateTime.Now;
            ok = true;
            return true;
        });
        return ok;
    }

    /// <summary>某分组是否置顶（分组不存在 / 收藏 → false）。侧边栏排序与菜单文案用。</summary>
    public static bool IsPinned(string id)
    {
        if (string.IsNullOrEmpty(id) || ChatGroupStore.IsFavorite(id)) return false;
        return ChatGroupStore.Load().FirstOrDefault(g => g.Id == id)?.Pinned ?? false;
    }

    /// <summary>取某分组的指令（未分组 / 收藏 / 分组不存在 → 空串）。
    /// 语义是**现读**：调用方每次要用时都重新调，这样用户改完指令下一次发消息就生效，不需要重开会话。</summary>
    public static string GetInstruction(string groupId)
    {
        if (string.IsNullOrEmpty(groupId) || ChatGroupStore.IsFavorite(groupId)) return "";
        return ChatGroupStore.Load().FirstOrDefault(g => g.Id == groupId)?.Instruction?.Trim() ?? "";
    }

    /// <summary>取分组名（不存在 → 空串；收藏 → 「收藏」）。列表分区头与标题展示用。</summary>
    public static string GetGroupName(string groupId)
    {
        if (string.IsNullOrEmpty(groupId)) return "";
        return ChatGroupStore.Load().FirstOrDefault(g => g.Id == groupId)?.Name ?? "";
    }

    /// <summary>
    /// 构造分组指令的**注入文本**（未分组 / 收藏 / 无指令 → 空串）。
    ///
    /// 三个硬要求，动这里之前先读：
    /// ① 必须显式标注「来源 + 优先级分层」—— 模型会把分组指令当成"用户的新命令"来执行，
    ///    不标注边界就等于开了一条绕过系统红线的后门。所以这段文本由检查点守着（慢层「会话分组」组）。
    /// ② 必须**现读**（不缓存）—— 用户改完指令，下一次发消息就生效，不需要重开会话。
    /// ③ 2026-10-01 起**分层**：本指令可覆盖【默认行为】，不可覆盖【安全红线】——
    ///    这段文案与 `AIDialogWindow.AppendAgentRulesOnce` 的分层规则必须**成对改**，改一半必红。
    ///
    /// 放在服务层而不是窗口里：窗口的私有方法没法被测到，而这条是安全相关的。
    /// </summary>
    public static string BuildInstructionContext(string groupId)
    {
        var instruction = GetInstruction(groupId);
        if (instruction.Length == 0) return "";

        var groupName = GetGroupName(groupId);
        if (groupName.Length == 0) groupName = "（未知分组）";

        return $"【分组指令 — 来自会话所属分组「{groupName}」】\n" +
               "这是用户为当前分组写的**实时指令**，只适用于本会话。优先级两条，务必分清：\n" +
               "① 高于系统提示里的【默认行为】—— 例如「写操作前先问一句」这类默认习惯，冲突时以本指令为准；\n" +
               "② 低于系统提示里的【安全红线】（不许谎报成功、不许编造内容、不许泄露密钥、不许让用户去敲命令行、删除类操作必须征询）—— " +
               "本指令**不能覆盖**安全红线，与系统规则冲突时一律以红线为准。\n" + instruction;
    }

    /// <summary>
    /// 自愈：把 GroupId 指向「清单里已不存在的分组」的会话清回未分组，返回修好的会话数。
    ///
    /// 什么情况会产生悬空引用：① 旧版本删分组的非原子操作留下的残留
    /// ② 另一台设备删了分组，本机同步到"分组清单少了一条"但会话文件还没轮到更新。
    /// 不修的话界面上会出现一个「（未知分组）」幽灵分区。
    /// </summary>
    public static int RepairDanglingGroupIds()
    {
        var valid = ChatGroupStore.Load()
            .Select(g => g.Id)
            .ToHashSet(StringComparer.Ordinal);

        var fixedCount = 0;
        try
        {
            var dir = FocusCapturePaths.Combine("chat_history");
            if (!Directory.Exists(dir)) return 0;

            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                try
                {
                    var svc = ChatSessionService.Load(file);
                    if (svc == null || svc.GroupId.Length == 0) continue;
                    if (valid.Contains(svc.GroupId)) continue;

                    svc.GroupId = "";
                    svc.Save();
                    fixedCount++;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[FocusCapture] 悬空分组引用自愈跳过文件: {file}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FocusCapture] 悬空分组引用自愈失败: {ex.Message}");
        }

        if (fixedCount > 0) AppLog.Info("ChatGroup", $"已把 {fixedCount} 个悬空分组引用的会话放回未分组");
        return fixedCount;
    }
}
