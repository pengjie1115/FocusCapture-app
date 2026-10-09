using System.Text.Json;

namespace FocusCapture.Services;

/// <summary>AI 会话分组（一层平铺，不嵌套；会话通过 SessionFile.GroupId 引用，空 = 未分组）</summary>
public class ChatGroup
{
    public string Id { get; set; } = "";     // 分组唯一 ID（GUID；收藏保留分区为固定值 ChatGroupStore.FavoriteId）
    public string Name { get; set; } = "";   // 分组名（跨端同名合并按名称裁决：胜出 = CreatedAt 最早，平局按 DeviceId → Id 字典序）
    public DateTime CreatedAt { get; set; }  // 创建时间（同名合并：胜出者 = 创建时间最早）
    public string DeviceId { get; set; } = ""; // 创建端设备 ID（同名合并平局 tie-break：DeviceId 字典序小者胜；
                                               // 旧清单缺该字段由 ChatSyncEngine.SyncGroupsAsync 兜底填充）

    // ── 2026-09-23 新增：分组指令 + 跨端合并判方向 ──

    /// <summary>分组指令（Agent.md 等价物）：进入该分组时追加进系统提示的补充说明，空 = 无。
    /// 优先级低于系统红线（设计稿 §5.2），由 ChatSessionService.AppendSystemRules 注入。</summary>
    public string Instruction { get; set; } = "";

    /// <summary>名称最后修改时间。**跨端改名不丢的关键** —— 没有它就分不清哪端名字更新，
    /// 旧实现只能"本地无条件 wins"，于是 A 端改名会被 B 端同步时回滚。
    /// 旧清单缺该字段 → 反序列化得 DateTime.MinValue → 合并时回退到"本地 wins"的旧行为（不恶化现状）。</summary>
    public DateTime NameUpdatedAt { get; set; }

    /// <summary>指令最后修改时间（同上，跨端 LWW 判方向用）</summary>
    public DateTime InstructionUpdatedAt { get; set; }

    // ── 2026-09-24 新增：分组置顶 ──

    /// <summary>分组是否置顶 —— 置顶的分组在侧边栏分组列表里排到最前（收藏恒在最前，不参与排序）。
    ///
    /// 为什么要有这个字段：用户从 WorkBuddy 的「置顶任务」借鉴，点「置顶此分组」时期望的是
    /// **分组本身浮到前面**。旧实现（把组内会话全部置顶）在侧边栏上看不出任何变化 ——
    /// 空分组点下去更是彻底"没反应"（用户 2026-09-24 实测反馈）。</summary>
    public bool Pinned { get; set; }

    /// <summary>置顶状态最后修改时间（跨端 LWW 判方向）。
    /// ⚠️ **取消置顶也必须刷新它** —— 否则"取消"在合并时会被云端那条 Pinned=true 顶回来，
    /// 用户会看到自己明明取消了、换台机器又置顶了（本字段是 bool，没有它就无法区分真假）。
    /// 旧清单缺该字段 → 反序列化得 DateTime.MinValue → 合并时回退到"本地 wins"（不恶化现状）。</summary>
    public DateTime PinnedUpdatedAt { get; set; }

    // ── 2026-09-24 新增：删除墓碑 ──

    /// <summary>删除时间。**default（MinValue）= 未删除**；非默认值 = 这条记录是墓碑。
    ///
    /// 为什么必须有它：分组合并是"本地 ∪ 云端"的并集，并集表达不了"删除"——
    /// 本机删了分组，云端那份还在，下一轮同步就把它并回来（用户实测"删除分组后一会又复活"，
    /// 会话那条 2026-09-23 已有删除清单防线，分组是漏网的）。
    ///
    /// 语义（2026-09-24 用户拍板）：**删除即终态 + 墓碑永久保留** —— 任一端删了，
    /// 两端都永远消失，墓碑记录留在清单里压制云端残留（一条几十字节，不做清理）。</summary>
    public DateTime DeletedAt { get; set; }

    /// <summary>是否为删除墓碑（DeletedAt 有值）。Load() 会把墓碑过滤掉，UI 永远看不到它们。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsTombstone => DeletedAt != default;
}

/// <summary>
/// 会话分组清单读写（%APPDATA%\FocusCapture\chat_groups.json，独立文件，参与坚果云同步）。
///
/// **2026-09-23 重写**，修掉三处真缺陷（原实现的问题与证据见设计稿 §11）：
/// ① 读-改-写无锁 ↔ ChatSyncEngine 在后台线程做同样的事 → 竞态丢分组。
///    现在：所有读写都在同一个进程锁下，并新增 Mutate 原子接口 —— 调用方不再自己组织 Load→改→Save。
/// ② 无变更通知钩子 → 新建空分组后不启动上传防抖窗口，换机器看不到（要等下次任意同步周期）。
///    现在：落盘成功后触发 GroupsChanged，由 MainWindow（引擎创建点）订阅并转发给当前 ChatSyncEngine。
/// ③ 收藏分区（新功能：「收藏 = 分组的另一种形式」）无处安放且需跨端一致。
///    现在：FavoriteId 固定 + CreatedAt 取 DateTime.MinValue（同名合并永远它胜出）+ Load 自动补建。
///
/// 序列化方案沿用：独立 JsonSerializerOptions，与 SessionFile 同风格反射序列化。
/// **业务操作（新建/重命名/删除/改指令）一律走 ChatGroupService，别往这个类里加业务方法。**
/// </summary>
public static class ChatGroupStore
{
    /// <summary>收藏保留分区的固定 Id。会话的 GroupId 指向它 = 已收藏。
    /// 不可重命名 / 删除 / 加指令；一个会话只能属于一个分组或收藏（归属互斥）。</summary>
    public const string FavoriteId = "__favorite__";

    public const string FavoriteName = "收藏";

    private static string StorePath => FocusCapturePaths.Combine("chat_groups.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>进程内串行闸。对清单文件的每一次读写都在它保护下 —— 与 ChatSyncEngine 的后台线程互斥。</summary>
    private static readonly object Gate = new();

    /// <summary>分组清单落盘成功后触发。**设计意图**：由 ChatSyncEngine 订阅它启动上传防抖窗口 ——
    /// 没有这个钩子时，新建的空分组没有会话改动可借力，要等下次任意同步周期才会上传（换机器就是看不到）。
    ///
    /// **2026-09 批 0 钩子已接线**：订阅方 = MainWindow.CreateSyncEngine（`OnChatGroupsChanged` 读
    /// `_chatSyncEngine` 字段，引擎重建后自动指向新实例，静态事件 -=/+= 防重复）；ChatSyncEngine 已实现
    /// Dispose，RebuildSyncEngine 重建前与退出路径（ExitApp / OnClosed）都会调用。
    ///
    /// ⚠️ **构造函数里禁止订阅**：① 每 new 一个引擎就多一个订阅者（慢层测试会 new 很多个）
    /// ② 已停用引擎的定时器被事件唤醒去写真实目录 —— 本机 2026-09-23 上午刚因此出过
    /// 「慢层测试写穿真实 settings.json」的事故。订阅只许放唯一创建点，且必须与 Dispose 成对管理。</summary>
    public static event Action? GroupsChanged;

    /// <summary>读取分组清单（**恒含收藏保留分区**；文件不存在 / 损坏时返回只含收藏的清单，不抛）。
    /// **UI / 业务口径：不含删除墓碑** —— 墓碑只活在磁盘和同步引擎里（见 <see cref="LoadAll"/>）。</summary>
    public static List<ChatGroup> Load()
    {
        lock (Gate) return LoadUnlocked().Where(g => !g.IsTombstone).ToList();
    }

    /// <summary>读取含**删除墓碑**的完整清单（**同步引擎专用**）。
    /// 合并必须拿得到墓碑：并集合并表达不了"删除"，靠墓碑压制云端残留（否则删了又复活）。</summary>
    public static List<ChatGroup> LoadAll()
    {
        lock (Gate) return LoadUnlocked();
    }

    /// <summary>
    /// 原子读-改-写：把当前清单交给 mutate，返回 true 表示有改动需要落盘；返回落盘后的最终清单。
    /// **这就是替代「Load→改→Save」的正确姿势** —— 中间不会被后台同步线程插入。
    /// mutate 收到的列表**不含墓碑**（业务永远看不到墓碑）；落盘时墓碑原样并回，
    /// 业务代码不需要（也不允许）关心墓碑的存在。
    /// </summary>
    public static List<ChatGroup> Mutate(Func<List<ChatGroup>, bool> mutate)
    {
        lock (Gate)
        {
            var all = LoadUnlocked();
            var tombstones = all.Where(g => g.IsTombstone).ToList();
            var groups = all.Where(g => !g.IsTombstone).ToList();

            var changed = false;
            try { changed = mutate(groups); }
            catch (Exception ex) { Debug.WriteLine($"[FocusCapture] 分组清单变更回调失败: {ex.Message}"); }

            if (!changed) return groups;
            SaveUnlocked(tombstones.Concat(groups).ToList());
            return groups;
        }
    }

    /// <summary>整体覆盖写（**同步引擎专用**：传含墓碑的完整清单，见 <see cref="LoadAll"/>）。
    /// 加锁 + 落盘成功后触发变更通知。
    /// ⚠️ 业务代码不许调它 —— 业务路径走 <see cref="Mutate"/>（会自动保留墓碑），
    /// 直接 Save 一个不含墓碑的清单会把墓碑抹掉，下一轮同步删除就被云端复活。</summary>
    public static void Save(List<ChatGroup> groups)
    {
        lock (Gate) SaveUnlocked(groups);
    }

    /// <summary>收藏保留分区对象（恒存在，Load 时自动补建）</summary>
    public static ChatGroup NewFavorite() => new()
    {
        Id = FavoriteId,
        Name = FavoriteName,
        // 刻意取 MinValue：跨端同名分组合并规则是「胜出 = CreatedAt 最早」，收藏因此永远胜出，
        // 不会被另一台设备上恰好同名的普通分组顶掉。（且两端收藏 Id 相同，本就不会判出败者。）
        CreatedAt = DateTime.MinValue,
        DeviceId = "",
    };

    /// <summary>是否为收藏保留分区</summary>
    public static bool IsFavorite(string id) => string.Equals(id, FavoriteId, StringComparison.Ordinal);

    // ── 内部（调用方必须已持有 Gate） ──

    private static List<ChatGroup> LoadUnlocked()
    {
        var groups = new List<ChatGroup>();
        try
        {
            if (File.Exists(StorePath))
            {
                var parsed = JsonSerializer.Deserialize<List<ChatGroup>>(File.ReadAllText(StorePath, Encoding.UTF8), JsonOptions);
                if (parsed != null) groups = parsed;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Debug.WriteLine($"[FocusCapture] 会话分组清单读取失败: {ex.Message}");
            groups = new List<ChatGroup>();
        }

        // 收藏保留分区缺失就补上（内存态）。刻意**不在这里落盘** —— Load 是读操作，不该有写副作用；
        // 用户任何一次分组写操作都会把含收藏的整份清单写下去。
        if (!groups.Any(g => IsFavorite(g.Id))) groups.Add(NewFavorite());
        return groups;
    }

    private static void SaveUnlocked(List<ChatGroup> groups)
    {
        try
        {
            var dir = Path.GetDirectoryName(StorePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(groups, JsonOptions), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FocusCapture] 会话分组清单保存失败: {ex.Message}");
            return;   // 没落盘就不发变更通知 —— 通知的语义是「已落盘，可以传了」
        }
        GroupsChanged?.Invoke();
    }
}
