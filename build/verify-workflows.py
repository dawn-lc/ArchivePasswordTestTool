"""校验 .github 下的工作流与 composite action（推送前本地自检）。

GitHub Actions 的 YAML 错误、PowerShell 语法错误、`with:` 传了 action 未声明的输入，
都要等推上去才暴露，一次反馈要好几分钟。这个脚本在本地就能抓出来：

  1. YAML 能否解析；
  2. composite action 的 steps 结构是否合法（每个 step 有且只有 run / uses）；
  3. 工作流传给 action 的 `with:` 键是否都在 action 的 `inputs:` 里声明，必填项是否漏传；
  4. 每个 `run:` 块里的 PowerShell 能否通过 AST parser（`${{ }}` 先换成占位符）。

用法（仓库根目录）：

    python build/verify-workflows.py

退出码 0 = 全部通过。需要 Python 3 + PyYAML，以及 pwsh 或 Windows PowerShell 之一。
"""

import os
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

import yaml

ROOT = Path(__file__).resolve().parent.parent
GITHUB = ROOT / ".github"

failures: list[str] = []


def fail(message: str) -> None:
    failures.append(message)
    print(f"  [失败] {message}")


def find_shell() -> str:
    """优先 pwsh（与工作流里的 `shell: pwsh` 一致），本机没装则退回 Windows PowerShell。

    PS 5.1 的语法是 7 的子集，通过 5.1 检查的脚本不会在 7 上因语法失败。
    """
    for name in ("pwsh", "powershell"):
        if shutil.which(name):
            return name
    raise RuntimeError("找不到 pwsh 或 powershell，无法校验脚本语法。")


SHELL = find_shell()


def substitute_expressions(script: str) -> str:
    """把 ${{ ... }} 换成合法占位符，让 parser 只看语法。"""
    return re.sub(r"\$\{\{[^}]*\}\}", "PLACEHOLDER", script)


def check_shell_syntax(script: str, where: str) -> None:
    """交给真实的 PowerShell parser 检查（只解析，不执行）。"""
    handle = tempfile.NamedTemporaryFile("w", suffix=".ps1", delete=False, encoding="utf-8")
    try:
        handle.write(script)
        handle.close()
        subject = handle.name.replace("'", "''")

        # PS 5.1 默认按控制台 ANSI 编码输出，不显式置 UTF-8 会在管道里变成 GBK 字节
        probe = (
            "[Console]::OutputEncoding = [Text.Encoding]::UTF8\n"
            f"$text = Get-Content -Raw -Encoding UTF8 '{subject}'\n"
            "$tokens = $null\n"
            "$errors = $null\n"
            "[void][System.Management.Automation.Language.Parser]::ParseInput($text, [ref]$tokens, [ref]$errors)\n"
            "if ($errors.Count -gt 0) { $errors | ForEach-Object { Write-Output $_.Message }; exit 1 }\n"
        )

        result = subprocess.run(
            [SHELL, "-NoProfile", "-NonInteractive", "-Command", probe],
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
        )
        if result.returncode != 0:
            detail = (result.stdout or result.stderr or "（无输出）").strip()
            fail(f"{where} 的 PowerShell 语法错误：{detail}")
    finally:
        os.unlink(handle.name)


def load(path: Path) -> dict:
    try:
        data = yaml.safe_load(path.read_text(encoding="utf-8"))
    except yaml.YAMLError as exc:
        fail(f"{path.relative_to(ROOT)} YAML 解析失败：{exc}")
        return {}
    if not isinstance(data, dict):
        fail(f"{path.relative_to(ROOT)} 顶层不是映射")
        return {}
    return data


def check_action(path: Path) -> tuple[set[str], dict]:
    print(f"\n== {path.relative_to(ROOT)} ==")
    data = load(path)
    if not data:
        return set(), {}

    runs = data.get("runs") or {}
    if runs.get("using") != "composite":
        fail("runs.using 不是 composite")

    declared = set((data.get("inputs") or {}).keys())
    print(f"  声明的 inputs：{sorted(declared) if declared else '（无）'}")

    for index, step in enumerate(runs.get("steps") or []):
        name = step.get("name") or f"step#{index}"
        if ("run" in step) == ("uses" in step):
            fail(f"{name}：必须且只能有 run 或 uses 之一")
            continue

        if "run" in step:
            if step.get("shell") != "pwsh":
                fail(f"{name}：shell 应为 pwsh")
            check_shell_syntax(substitute_expressions(step["run"]), name)
            print(f"  [OK] {name}（PowerShell 语法通过）")
        else:
            print(f"  [OK] {name}（uses: {step['uses']}）")

    return declared, data.get("inputs") or {}


def check_workflow(path: Path, action_inputs: set[str], required: dict) -> None:
    print(f"\n== {path.relative_to(ROOT)} ==")
    data = load(path)
    if not data:
        return

    env = data.get("env") or {}
    print(f"  工作流级 env：{env}")

    jobs = data.get("jobs") or {}
    for job_name, job in jobs.items():
        print(f"  job {job_name}: timeout={job.get('timeout-minutes')} runs-on={job.get('runs-on')}")

        for step in job.get("steps") or []:
            name = step.get("name") or "（未命名）"
            uses = step.get("uses") or ""

            if uses.startswith("./.github/actions/"):
                passed = set((step.get("with") or {}).keys())
                unknown = passed - action_inputs
                if unknown:
                    fail(f"{path.name}/{name}：传了 action 未声明的输入 {sorted(unknown)}")
                missing = set(required) - passed
                if missing:
                    fail(f"{path.name}/{name}：缺少必填输入 {sorted(missing)}")
                print(f"  [OK] {path.name}/{name}：传入 {sorted(passed)}")

            if "run" in step:
                check_shell_syntax(substitute_expressions(step["run"]), f"{path.name}/{name}")
                print(f"  [OK] {path.name}/{name}（PowerShell 语法通过）")


def main() -> int:
    action_inputs, input_specs = check_action(GITHUB / "actions" / "build-verify" / "action.yml")
    required = {name for name, spec in input_specs.items() if spec.get("required") is True}
    print(f"\n必填 inputs：{sorted(required)}")

    for name in ("ci.yml", "release.yml"):
        check_workflow(GITHUB / "workflows" / name, action_inputs, required)

    print("\n" + "=" * 60)
    if failures:
        print(f"发现 {len(failures)} 个问题：")
        for item in failures:
            print(f"  - {item}")
        return 1

    print("全部检查通过。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
