import io, os, re, shlex, subprocess, sys

PROJ = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".source", "DMSE"))
LOG = os.path.join(PROJ, "build.log")
RSP = os.path.join(PROJ, "obj", "csc.rsp")
CSC = r"C:\Program Files\dotnet\sdk\10.0.402\Roslyn\bincore\csc.dll"


def find_cmdline(path):
    data = open(path, "r", encoding="utf-8", errors="replace").read()
    idx = data.find("csc.exe /noconfig")
    if idx < 0:
        return None
    end = data.find("\n", idx)
    line = data[idx:end] if end > 0 else data[idx:]
    return re.sub(r"\s*\(工作 ID:\d+\)\s*$", "", line)


def main():
    # 先讓 MSBuild 產生命令列（它自己會在 Csc 任務失敗，無妨——我們只要那行命令列）。
    with open(LOG, "w", encoding="utf-8") as log:
        subprocess.run(["dotnet", "build", "--nologo", "-v:diag", "-t:CoreCompile"], cwd=PROJ,
                       stdout=log, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL)
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
