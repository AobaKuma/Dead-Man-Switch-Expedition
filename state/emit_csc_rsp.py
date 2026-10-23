import io, os, re, shlex, subprocess, sys

PROJ = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".source", "DMSE"))
LOG = os.path.join(PROJ, "build.log")
RSP = os.path.join(PROJ, "obj", "csc.rsp")
CSC = r"C:\Program Files\dotnet\sdk\10.0.402\Roslyn\bincore\csc.dll"


def find_cmdline(path):
    data = open(path, "r", encoding="utf-8", errors="replace").read()
    best = None
    start = 0
    while True:
        idx = data.find("csc.exe /noconfig", start)
        if idx < 0:
            break
        end = data.find("\n", idx)
        line = data[idx:end] if end > 0 else data[idx:]
        line = re.sub(r"\s*\(工作 ID:\d+\)\s*$", "", line)
        if best is None or len(line) > len(best):
            best = line
        start = idx + 1
    return best


def _build():
    with open(LOG, "w", encoding="utf-8") as log:
        subprocess.run(["dotnet", "build", "--nologo", "-v:diag"], cwd=PROJ,
                       stdout=log, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL)


def main():
    # 先讓 MSBuild 產生命令列（它自己會在 Csc 任務失敗，無妨——我們只要那行命令列）。
    # 必須用預設 target：-t:CoreCompile 不會跑 ResolveReferences，產出的命令列沒有參考組件。
    _build()
    line = find_cmdline(LOG)
    if not line or "/reference:" not in line:
        # 沒有觸發編譯（增量跳過）：刪掉輸出逼它重編一次
        for p in ("obj/Debug/DMSE.dll", "obj/Debug/refint/DMSE.dll"):
            fp = os.path.join(PROJ, p)
            if os.path.exists(fp):
                os.remove(fp)
        _build()
        line = find_cmdline(LOG)
    if line:
        # 保留原本的引號：rsp 與命令列的引號規則一致，直接用「空白分隔、引號內不切」切
        args = re.findall(r'(?:[^\s"]+|"[^"]*")+', line)
        args = args[1:]  # 去掉 csc.exe
        with io.open(RSP, "w", encoding="utf-8") as f:
            for a in args:
                f.write(a + "\n")
        print("args:", len(args), "(from msbuild)")
    elif os.path.exists(RSP):
        print("MSBuild 這次沒觸發編譯，沿用既有 rsp:", RSP)
    else:
        print("找不到 csc 命令列，也沒有既有 rsp；請先改動任一 .cs 再跑一次")
        return 2
    outpath = os.path.join(PROJ, "obj", "csc.out.txt")
    with open(outpath, "w", encoding="utf-8") as out:
        r = subprocess.run(["dotnet", "exec", CSC, "@" + RSP], cwd=PROJ,
                           stdout=out, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL)
    out = open(outpath, "r", encoding="utf-8", errors="replace").read()
    errs = [l for l in out.splitlines() if ": error " in l]
    print("exit:", r.returncode, "errors:", len(errs))
    for l in errs[:40]:
        print("  ", l)
    return 0


if __name__ == "__main__":
    sys.exit(main())
