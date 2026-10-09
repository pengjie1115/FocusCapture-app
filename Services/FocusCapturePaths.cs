namespace FocusCapture;

/// <summary>
/// 数据根目录唯一入口（2026-09-11 引入隔离能力；2026-09-16 升级为「用户可自定义」）。
///
/// 背景：改造前 `%AppData%\FocusCapture\` 被硬编码散落在十余个文件中，
/// 导致测试无法在不触碰用户真实数据的前提下运行（回收站记录 deleted.json 等正在此目录下）。
///
/// 三条通道与优先级（高 → 低）：
/// 1. <see cref="RootOverride"/> —— 测试 / 快照隔离专用，进程内设置，最高优先级
/// 2. <see cref="CustomRoot"/>   —— 用户在设置面板指定的数据根（重启后由 <see cref="LoadCustomRoot"/> 读入）
/// 3. <see cref="DefaultRoot"/>  —— `%AppData%\FocusCapture`，维持现状
///
/// 为什么自定义根要落一个「指针文件」而不是写进 settings.json：
/// settings.json 自己就住在数据根里 —— 想读它得先知道根在哪，形成自指。
/// 所以指针必须放在**固定的默认根**下（<see cref="PointerFile"/>），先读指针定位根，再读 settings.json。
/// 默认根由此升格为「指针目录」：自定义后它只剩指针文件与历史遗留数据，不再承载新写入。
///
/// 实现注意：Root 必须是**动态求值的属性**，不能是 static readonly 字段 ——
/// 类型初始化时缓存会导致设置 RootOverride 后不生效（存量代码已有此先例，如 AppSettings.ConfigPath）。
///
/// 命名空间刻意置于根 `FocusCapture`，使所有 `FocusCapture.*` 子命名空间可直接引用，无需逐文件加 using。
/// </summary>
public static class FocusCapturePaths
{
    /// <summary>应用数据目录名（默认根目录的末级）。</summary>
    public const string AppFolderName = "FocusCapture";

    /// <summary>指针文件名：内容为自定义数据根的绝对路径（只在默认根下出现）。</summary>
    public const string PointerFileName = "data_root.txt";

    private static string? _rootOverride;
    private static string? _customRoot;

    /// <summary>默认根（= 改造前的固定位置）。同时充当「指针目录」。</summary>
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        AppFolderName);

    /// <summary>指针文件完整路径（永远在默认根下，不随自定义根移动）。</summary>
    public static string PointerFile => Path.Combine(DefaultRoot, PointerFileName);

    /// <summary>根目录覆盖值；null 或空白 = 不覆盖。仅测试与隔离场景设置。</summary>
    public static string? RootOverride
    {
        get => _rootOverride;
        set => _rootOverride = Normalize(value);
    }

    /// <summary>
    /// 用户自定义的数据根；null 或空白 = 使用默认根。
    /// 不要直接改它来实现「切换数据根」—— 切换必须走 <see cref="SaveCustomRoot"/>（写指针）+
    /// <see cref="RootMigrationService"/>（搬数据），否则会出现「设置指向新根、老数据留在旧根」的分裂状态。
    /// </summary>
    public static string? CustomRoot
    {
        get => _customRoot;
        set => _customRoot = Normalize(value);
    }

    /// <summary>数据根目录（动态求值，不缓存）。</summary>
    public static string Root => _rootOverride ?? _customRoot ?? DefaultRoot;

    /// <summary>当前是否由用户自定义根生效（测试覆盖不算）。</summary>
    public static bool IsCustomRootInEffect => _rootOverride == null && _customRoot != null;

    /// <summary>在数据根目录下拼接子路径。</summary>
    public static string Combine(params string[] parts)
    {
        var all = new string[parts.Length + 1];
        all[0] = Root;
        Array.Copy(parts, 0, all, 1, parts.Length);
        return Path.Combine(all);
    }

    /// <summary>创建并返回根目录。</summary>
    public static string EnsureRoot()
    {
        var root = Root;
        Directory.CreateDirectory(root);
        return root;
    }

    // ── 指针文件读写（应用启动 / 切换数据根时使用） ──

    /// <summary>
    /// 应用启动时调用一次：把指针文件里的自定义根读进 <see cref="CustomRoot"/>。
    /// <see cref="RootOverride"/> 非空（测试 / 快照隔离）时直接跳过，保证隔离优先。
    /// </summary>
    /// <returns>需要提示用户的警告文案；一切正常返回 null。</returns>
    public static string? LoadCustomRoot()
    {
        if (_rootOverride != null) return null;   // 隔离模式：完全不碰真实配置

        _customRoot = ReadPointerFile();
        if (_customRoot == null) return null;

        // 自定义根不可用（外置盘未插入 / 盘符变了 / 权限被收回）时**绝不回退默认根**。
        // 回退的后果比报错严重得多：用户会看到一套空数据，以为笔记丢了，然后在默认根里重新记
        // —— 数据就此分裂成两套，比「启动时明确报错」难收拾一个量级。
        // 这里的处理是：保持指向自定义根（写入会失败但不会污染默认根），并把提示交给启动流程弹窗。
        if (!Directory.Exists(_customRoot))
        {
            return $"数据目录当前不可用：\n{_customRoot}\n\n" +
                   "该目录（可能是移动硬盘或网络位置）未就绪。程序不会改用默认目录，" +
                   "以免出现两套数据；请恢复该位置后重新启动。";
        }
        return null;
    }

    /// <summary>把自定义根写进指针文件（path 为空/等于默认根 = 删除指针，回到默认根）。</summary>
    public static void SaveCustomRoot(string? path)
    {
        var normalized = Normalize(path);
        if (normalized != null &&
            string.Equals(TrimEnd(normalized), TrimEnd(DefaultRoot), StringComparison.OrdinalIgnoreCase))
        {
            normalized = null;   // 指回默认根 = 等于没自定义
        }

        var dir = DefaultRoot;
        Directory.CreateDirectory(dir);

        if (normalized == null)
        {
            try { if (File.Exists(PointerFile)) File.Delete(PointerFile); } catch { /* 删不掉就留着，下次读仍指向旧根 */ }
        }
        else
        {
            // 明确 UTF-8 无 BOM 单行：这个文件极小，格式越简单越不容易被编辑器/迁移工具改坏
            File.WriteAllText(PointerFile, normalized, new System.Text.UTF8Encoding(false));
        }
        _customRoot = normalized;
    }

    private static string? ReadPointerFile()
    {
        try
        {
            var file = PointerFile;
            if (!File.Exists(file)) return null;

            var raw = File.ReadAllText(file).Trim();
            var normalized = Normalize(raw);
            if (normalized == null) return null;

            if (string.Equals(TrimEnd(normalized), TrimEnd(DefaultRoot), StringComparison.OrdinalIgnoreCase))
                return null;   // 指向默认根 = 视同未自定义

            return normalized;
        }
        catch
        {
            return null;   // 指针读不出来就按默认根走，不阻塞启动
        }
    }

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            // 只接受绝对路径；相对路径解析结果依赖工作目录，是「看似生效实则到处跑」的经典坑
            var full = Path.GetFullPath(value.Trim());
            return TrimEnd(full);
        }
        catch
        {
            return null;
        }
    }

    private static string TrimEnd(string path)
    {
        var t = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // 盘根（如 "D:" → "D:\"）不能把分隔符也吃掉，否则 "D:" 会变成当前工作目录
        return t.EndsWith(':') ? t + Path.DirectorySeparatorChar : t;
    }
}
