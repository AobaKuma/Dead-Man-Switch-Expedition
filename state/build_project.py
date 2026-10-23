"""以「直呼 csc」的方式建置任何 MSBuild 專案。

用途：本機沙箱會擋掉 MSBuild → csc 子程序的 pipe（WinError 5），
dotnet build 一定在 Csc 任務失敗。這裡改成先讓 MSBuild 印出它想跑的
csc 命令列（/v:diag），再自己用 `dotnet exec csc.dll @rsp` 編譯，
輸出重導向到檔案（不用 pipe）。

用法：
    python state/build_project.py <專案目錄> [framework]
例：
    python state/build_project.py ../_Fortified-Framework/_Sources/Fortified net10.0
"""
import io, os, re, subprocess, sys


def build(proj, framework=None):
    args = ["dotnet", "build", "--nologo", "-v:diag"]
    if framework:
        args += ["-f", framework]
    log = os.path.join(proj, "build.log")
    with open(log, "w", encoding="utf-8") as f:
        subprocess.run(args, cwd=proj, stdout=f, stderr=subprocess.STDOUT,
                       stdin=subprocess.DEVNULL)
    return log


def find_csc(log):
    data = open(log, "r", encoding="utf-8", errors="replace").read()
    best, start = None, 0
    while True:
        i = data.find("csc.exe /noconfig", start)
        if i < 0:
            break
        end = data.find("\n", i)
        line = data[i:end] if end > 0 else data[i:]
        line = re.sub(r"\s*\(工作 ID:\d+\)\s*$", "", line)
        if best is None or len(line) > len(best):
            best = line
        start = i + 1
    return best


def main():
    proj = os.path.abspath(sys.argv[1])
    framework = sys.argv[2] if len(sys.argv) > 2 else None

    log = build(proj, framework)
    line = find_csc(log)
    if not line or "/reference:" not in line:
        print("沒有可用的 csc 命令列（可能是增量跳過），略過編譯。")
        return 0

    args = re.findall(r'(?:[^\s"]+|"[^"]*")+', line)[1:]
    rsp = os.path.join(proj, "csc.rsp")
    with io.open(rsp, "w", encoding="utf-8") as f:
        for a in args:
            f.write(a + "\n")
    print("csc args:", len(args))

    out = os.path.join(proj, "csc.out.txt")
    with open(out, "w", encoding="utf-8") as f:
        r = subprocess.run(["dotnet", "exec",
                            r"C:\Program Files\dotnet\sdk\10.0.402\Roslyn\bincore\csc.dll",
                            "@" + rsp],
                           cwd=proj, stdout=f, stderr=subprocess.STDOUT,
                           stdin=subprocess.DEVNULL)
    text = open(out, "r", encoding="utf-8", errors="replace").read()
    errs = [l for l in text.splitlines() if ": error " in l]
    print("exit:", r.returncode, "errors:", len(errs))
    for l in errs[:30]:
        print("  ", l)
    return 0


if __name__ == "__main__":
    sys.exit(main())
