using System.Windows.Media.Imaging;

namespace FocusCapture.Services;

/// <summary>AI 问答复用图片资源：**用户头像**（侧边栏底部那个圆形）。
/// 【硬要求】与本应用的其它图片资源（应用图标 custom_icon.png，另一套 AppIconService）独立存储、独立读写 ——
/// 任何一边的操作都不得影响另一边。
///
/// 为什么要有这个类（2026-09-23，见 docs/2026-09-23-AI问答界面重构设计稿.md §0.3）：
/// 全项目此前只有「应用图标」一套图片机制，头像若现写一份落盘/加载，早晚会在
/// 「换后缀残留两个文件」「文件句柄没放导致换图失败」这类细节上踩坑。
///
/// 2026-09-26 变更：**欢迎语图标已按用户要求删除**（起手页不再显示图标、设置里入口一并删除，
/// 相关的 4 个 API 从本类移除）。用户数据根里若还留着旧文件 chat_welcome_icon.*，**刻意不删** ——
/// 删除类动作只给用户（项目红线 7），且该文件不再被任何代码读取/显示，留着无害。
/// 因此本类现在只管一套资源，"两套资源靠基名区分"那条纪律随之失效，不要再往里加第二套。
///
/// 三条实现口径（改前先读，都是坑）：
/// - **落盘位置由 <see cref="FocusCapturePaths.Combine"/> 动态求值**，不缓存绝对路径：
///   用户可在设置里切换数据根，缓存下来的旧路径会指向已搬走的位置。
/// - **Import 是"复制"不是"记住原路径"**：用户把原图删了/移了，界面就裂图。
/// - **永不抛**：选错格式、图片损坏、目录不可用全部只回落，由调用方按 null 处理。
public static class ChatAssetsService
{
    /// <summary>源文件大小上限（10 MB）。超限直接拒收：头像显示尺寸不到 100px，
    /// 收一张 50MB 的原图只会白占数据根，还得全套解码一遍。</summary>
    public const long MaxSourceBytes = 10L * 1024 * 1024;

    /// <summary>
    /// 允许的图片后缀（有序！）。顺序即 <see cref="FindStoredFile"/> 的择优顺序 ——
    /// 万一目录里同时躺着两种后缀（旧版本遗留 / 手工放的），取第一个命中的，结果稳定可预期，
    /// 而不是依赖 HashSet 那种不保证的枚举顺序。
    /// </summary>
    private static readonly string[] AllowedExts = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" };

    /// <summary>用户头像落盘基名。</summary>
    private const string AvatarBase = "chat_user_avatar";

    /// <summary>
    /// 「删同类旧文件 → 复制新文件」不是原子操作：两个导入同时在跑的话，
    /// 后一个的删除会把前一个刚复制好的文件删掉。加锁把这两步合起来，代价可忽略。
    /// </summary>
    private static readonly object _ioLock = new();

    // ══════════════════ 用户头像（侧边栏底部那个圆形）══════════════════

    /// <summary>导入用户头像：复制到数据根并返回**目标绝对路径**；失败返回 null。</summary>
    public static string? ImportUserAvatar(string sourcePath) => Import(sourcePath, AvatarBase);

    /// <summary>恢复默认：删掉已落盘的头像（不设时界面用昵称首字画色块，不需要文件）。</summary>
    public static void ResetUserAvatar() => Reset(AvatarBase);

    /// <summary>载入用户头像；未设置 / 文件损坏 → null，永不抛。</summary>
    public static BitmapImage? LoadUserAvatar() => Load(AvatarBase);

    /// <summary>是否已设置用户头像。</summary>
    public static bool HasUserAvatar => HasStored(AvatarBase);

    // ══════════════════ 实现 ══════════════════

    /// <summary>
    /// 复制的通用实现。校验顺序为「格式 → 存在 → 大小」，先做便宜的判定，避免对大文件白做 IO。
    /// 目标名固定为 <c>{baseName}{小写后缀}</c>：换后缀时旧后缀文件必须先删（见 <see cref="DeleteStoredVariants"/>），
    /// 否则目录里会同时存在 .png 与 .jpg 两份，Load 读哪份全看择优顺序 —— 用户会看到"明明换了图却没变"。
    /// </summary>
    private static string? Import(string sourcePath, string baseName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sourcePath)) return null;
            if (!File.Exists(sourcePath)) return null;

            // 小写后缀再落盘：Windows 上 "IMG.PNG" 与 "img.png" 是同一文件，
            // 但字符串不等于 —— 不统一大小写，下次按小写去 Find/Delete 就找不着。
            var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
            if (!AllowedExts.Contains(ext, StringComparer.OrdinalIgnoreCase)) return null;

            if (new FileInfo(sourcePath).Length > MaxSourceBytes) return null;

            var sourceFull = Path.GetFullPath(sourcePath);
            var targetFull = Path.GetFullPath(FocusCapturePaths.Combine(baseName + ext));

            lock (_ioLock)
            {
                // 自定义根未就绪时这里会抛 → 被下面 catch 兜住返回 null，绝不把异常抛给 UI
                FocusCapturePaths.EnsureRoot();

                // 用户从数据根里挑了"已经落盘的这一张"：源与目标同一文件，
                // 必须直接返回 —— 否则下面的删旧会把源文件删掉，再复制时源已不在。
                if (string.Equals(sourceFull, targetFull, StringComparison.OrdinalIgnoreCase))
                    return targetFull;

                DeleteStoredVariants(baseName, keepFullPath: sourceFull);
                File.Copy(sourceFull, targetFull, overwrite: true);
            }
            return targetFull;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FocusCapture] 导入图片失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>删掉该基名下所有后缀的落盘文件（失败逐个吞掉，不影响其余）。</summary>
    private static void Reset(string baseName)
    {
        try
        {
            lock (_ioLock) DeleteStoredVariants(baseName, keepFullPath: null);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FocusCapture] 重置图片失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 载入通用实现。
    ///
    /// <c>CacheOption = OnLoad</c> + <c>Freeze()</c> 两个都不能省：
    /// - OnLoad：读完立即释放文件句柄。少了它，WPF 会懒加载并在整个 BitmapImage 生命周期里占着文件，
    ///   用户点「换图」时目标文件被自己占住，删除/覆盖就失败。
    /// - IgnoreImageCache：同一路径换了内容也必须重读，否则 WPF 的 URI 缓存会继续给旧图。
    /// - Freeze：冻结后才能被多个控件 / 后台线程安全共用，也免去重复解码。
    /// 这三条与 <see cref="AppIconService.Reload"/> 口径一致，别改出分叉。
    /// </summary>
    private static BitmapImage? Load(string baseName)
    {
        try
        {
            var path = FindStoredFile(baseName);
            if (path == null) return null;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex)
        {
            // 图片损坏 / 被独占 / 目录不可用 → 一律当"没有"，绝不向上抛
            Debug.WriteLine($"[FocusCapture] 读取图片失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 「是否已设置」= 数据根下存在该基名的落盘文件。**刻意不解析图片内容**：
    /// - 便宜：每次进设置页都要问一次，不该做解码；
    /// - 语义清晰：判据是"用户设过"，不是"这张图现在能不能显示"。
    /// 代价（有意接受）：文件损坏时 <c>Has*</c> 为 true 而 <c>Load*</c> 为 null。
    /// 这样反而给了用户出路 —— 界面仍显示「恢复默认」，能一键清掉那张坏图；
    /// 若把 Has 也做成解码判断，坏图会变成"既显示不出来、又没有重置入口"的死角。
    /// </summary>
    private static bool HasStored(string baseName) => FindStoredFile(baseName) != null;

    /// <summary>按 <see cref="AllowedExts"/> 顺序取第一个存在的落盘文件；都没有返回 null。</summary>
    private static string? FindStoredFile(string baseName)
    {
        try
        {
            foreach (var ext in AllowedExts)
            {
                var path = FocusCapturePaths.Combine(baseName + ext);
                if (File.Exists(path)) return path;
            }
        }
        catch (Exception ex)
        {
            // Root 求值本身可能抛（如自定义根非法）；当作"没有"，不阻塞调用方
            Debug.WriteLine($"[FocusCapture] 查找图片失败: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// 删除某基名下所有允许后缀的落盘文件。
    /// <paramref name="keepFullPath"/> 不为空时跳过该绝对路径 —— 用于"源文件恰在本目录"的自复制场景，
    /// 防止把源删了。只按白名单后缀删，绝不碰 <c>custom_icon.png</c>（那是应用图标，名字也不匹配）或其他文件。
    /// </summary>
    private static void DeleteStoredVariants(string baseName, string? keepFullPath)
    {
        foreach (var ext in AllowedExts)
        {
            try
            {
                var path = FocusCapturePaths.Combine(baseName + ext);
                if (keepFullPath != null &&
                    string.Equals(Path.GetFullPath(path), keepFullPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // 单个删不掉（被别的程序占着等）不影响其余；Load 时按择优顺序取第一个存在的即可
            }
        }
    }
}
