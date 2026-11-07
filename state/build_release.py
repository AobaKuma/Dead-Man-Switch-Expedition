"""以 Release 設定建置並部署 DMSE 到 1.7/Assemblies。

`build_project.py` 走的是 MSBuild 的預設設定（Debug），它讓 MSBuild 印出
csc 命令列後自己編譯，輸出落在 obj/Debug/DMSE.dll；MSBuild 的
CopyFilesToOutputDirectory 從來沒跑到（Csc 任務本身就會失敗），所以
1.7/Assemblies/DMSE.dll 不會更新。這支腳本做同一件事，但改用 -c Release，
編完再把 obj/Release/DMSE.dll 複製到 1.7/Assemblies/（也就是遊戲實際
載入、且納入版控的那一份）。

用法：
    python state/build_release.py [專案目錄] [framework]
例：
    python state/build_release.py .source/DMSE net10.0
"""
import io, os, re, shutil, subprocess, sys

CSC = r"C:\Program Files\dotnet\sdk\10.0.402\Roslyn\bincore\csc.dll"


def build(proj, framework):
    args = ["dotnet", "build", "--nologo", "-v:diag", "-c", "Release"]
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
    proj = os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else ".source/DMSE")
    framework = sys.argv[2] if len(sys.argv) > 2 else None

    line = find_csc(build(proj, framework))
    if not line or "/reference:" not in line:
        print("沒有可用的 csc 命令列（可能是增量跳過），略過編譯。")
        return 1

    args = re.findall(r'(?:[^\s"]+|"[^"]*")+', line)[1:]
    rsp = os.path.join(proj, "csc.rsp")
    with io.open(rsp, "w", encoding="utf-8") as f:
        for a in args:
            f.write(a + "\n")
    out_arg = next((a for a in args if a.startswith("/out:")), "")
    print("csc args:", len(args), out_arg)

    out = os.path.join(proj, "csc.out.txt")
    with open(out, "w", encoding="utf-8") as f:
        r = subprocess.run(["dotnet", "exec", CSC, "@" + rsp], cwd=proj,
                           stdout=f, stderr=subprocess.STDOUT,
                           stdin=subprocess.DEVNULL)
    text = open(out, "r", encoding="utf-8", errors="replace").read()
    errs = [l for l in text.splitlines() if ": error " in l]
    print("exit:", r.returncode, "errors:", len(errs))
    for l in errs[:30]:
        print("  ", l)
    if r.returncode != 0 or errs:
        return 1

    built = os.path.join(proj, out_arg[len("/out:"):])
    name = os.path.basename(built)
    try:
        with open(os.path.join(proj, "DMSE.csproj"), "r", encoding="utf-8") as f:
            m = re.search(r"<OutputPath>([^<]+)</OutputPath>", f.read())
        if m:
            raw = m.group(1).replace("$(MSBuildThisFileDirectory)", proj + os.sep)
            dest_dir = os.path.normpath(os.path.join(proj, raw))
        else:
            dest_dir = None
    except OSError:
        dest_dir = None
    if not dest_dir or not os.path.isdir(dest_dir):
        print("找不到 OutputPath，編譯結果留在:", built)
        return 0
    dest = os.path.join(dest_dir, name)
    if os.path.normcase(dest) != os.path.normcase(built):
        shutil.copy2(built, dest)
    print("部署:", dest)
    return 0


if __name__ == "__main__":
    sys.exit(main())
