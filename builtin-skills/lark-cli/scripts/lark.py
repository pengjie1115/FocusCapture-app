#!/usr/bin/env python3
r"""lark-cli 透传壳（FocusCapture 内置桥接 Skill）

作用：把收到的参数**原样**交给官方 lark-cli，并把它的退出码 / stdout / stderr 原样交回。
本文件刻意不含任何飞书业务知识 —— 业务用法由 lark-cli 自带的官方技能提供
（`lark-cli skills list` / `lark-cli skills read <名字>`）。

用法（由应用的 run_skill_script 调用；args 是数组，不经过 shell）：
    lark.py skills list
    lark.py skills read lark-base
    lark.py base record list --app-token <xxx> --as user
    lark.py <任意 lark-cli 参数...>

契约：
    退出码 = lark-cli 的退出码（原样传回；找不到 lark-cli 用 127，超时用 124）
    stdout = lark-cli 的 stdout
    stderr = lark-cli 的 stderr
    stdin  = 宿主喂了内容就转给 lark-cli（长 JSON 参数用得上）

为什么找 lark-cli **只认 PATH**：宿主在跑脚本之前，会把依赖的全部候选目录
（应用自带的 runtime\lark-cli → 用户数据目录下的 runtime\lark-cli）前置到 PATH。
刻意不硬编码任何"别的应用安装目录"—— 那会让本应用的行为取决于别人装了什么，
换台机器就失效，而且失败时看不出原因。
"""
import os
import shutil
import stat
import subprocess
import sys

LARK_EXE_NAME = "lark-cli"

# 必须**小于**宿主给脚本的超时（执行器是 150 秒）：好让这里先超时、说出"是哪一条命令超了"，
# 而不是被宿主一刀切掉（那时候连"跑了什么"都看不出来）。
TIMEOUT_SECONDS = 120

EXIT_NOT_FOUND = 127
EXIT_TIMEOUT = 124


def find_lark():
    """只在 PATH 里找（宿主已把候选目录前置）。找不到返回 None，**不抛**。"""
    try:
        exe = shutil.which(LARK_EXE_NAME)
        if exe:
            return exe
        # PATHEXT 被改过时的兜底：仍然只在 PATH 范围内找，不外扩
        for ext in (".exe", ".cmd", ".bat"):
            exe = shutil.which(LARK_EXE_NAME + ext)
            if exe:
                return exe
    except Exception:
        pass
    return None


def decode(raw):
    """智能解码：优先 utf-8，失败回退 gbk（Windows 上走 .cmd 时输出可能是系统代码页）"""
    if not raw:
        return ""
    if isinstance(raw, str):
        return raw
    for enc in ("utf-8", "gbk"):
        try:
            return raw.decode(enc)
        except UnicodeDecodeError:
            continue
    return raw.decode("utf-8", errors="replace")


def emit(stream, text):
    """按 UTF-8 写出去。宿主就是按 UTF-8 解码的（所以不能用系统默认编码）。"""
    if not text:
        return
    data = text.encode("utf-8", errors="replace")
    try:
        stream.buffer.write(data)
        stream.buffer.flush()
    except Exception:
        try:
            stream.write(text)
            stream.flush()
        except Exception:
            pass


def stdin_is_pipe():
    """宿主到底有没有把标准输入接成管道。

    不能用 isatty()：句柄无效时（应用是 GUI、没接控制台）它同样是 False，
    那种情况下 read() 要么报错、要么**永久阻塞**。fstat 能区分「管道」与「无效/控制台」，
    而且不阻塞 —— 这是唯一可靠又不冒险的判据。
    """
    try:
        return stat.S_ISFIFO(os.fstat(0).st_mode)
    except OSError:
        return False


def main():
    args = sys.argv[1:]

    exe = find_lark()
    if exe is None:
        emit(sys.stderr,
             "找不到 {0}：应用自带的那一份不在、按需目录里也没有、系统 PATH 里也没有。\n"
             "请在应用里确认外部依赖是否已准备好（设置 → AI 功能 → Skill 扩展）。\n".format(LARK_EXE_NAME))
        return EXIT_NOT_FOUND

    # 宿主喂了内容才读 stdin（它会把 stdin 写成管道并立即关闭）；否则一律 DEVNULL，
    # 避免 lark-cli 在无控制台的环境里等一个永远不会来的输入。
    piped = stdin_is_pipe()
    data = None
    if piped:
        try:
            data = sys.stdin.buffer.read()
        except Exception:
            data = None
        if data is None:
            data = b""

    try:
        # 刻意不打印 argv —— 参数里可能有用户的密钥，日志/回显都不该出现它
        r = subprocess.run(
            [exe] + args,
            input=data if piped else None,
            stdin=None if piped else subprocess.DEVNULL,
            capture_output=True,
            timeout=TIMEOUT_SECONDS,
        )
    except subprocess.TimeoutExpired as ex:
        # 超时也要把已经拿到的输出交出去（"吐完输出就卡住"的命令全靠它）
        emit(sys.stdout, decode(ex.stdout))
        emit(sys.stderr, decode(ex.stderr))
        emit(sys.stderr,
             "\n[提示] 这条命令超过 {0} 秒仍未结束，已被终止。若它本身就需要很久，"
             "请改成分批调用。\n".format(TIMEOUT_SECONDS))
        return EXIT_TIMEOUT
    except Exception as ex:
        emit(sys.stderr, "无法启动 {0}：{1}\n".format(LARK_EXE_NAME, ex))
        return EXIT_NOT_FOUND

    emit(sys.stdout, decode(r.stdout))
    emit(sys.stderr, decode(r.stderr))
    return r.returncode


if __name__ == "__main__":
    sys.exit(main())
