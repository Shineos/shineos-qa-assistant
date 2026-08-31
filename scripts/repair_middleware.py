# -*- coding: utf-8 -*-
"""ShineosQA 修復スクリプト（管理者権限で実行すること）

- Open WebUI の middleware をバックアップから復元し、最新のパッチを再適用する
- 設定は変更しない（ナレッジ・チャット履歴は保持される）
"""
import contextlib
import io
import os
import runpy
import shutil
import subprocess
import sys
import time

# インストール先はこのスクリプトの位置（{app}\scripts）から自動検出する（v1.0.74）。
# 従来は C:\Program Files\ShineosQA 固定で、カスタムインストール先では修復不能だった。
# 検出に失敗した場合のみ既定インストール先へフォールバックする。
APP_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if not os.path.isdir(os.path.join(APP_DIR, "venv")):
    APP_DIR = r"C:\Program Files\ShineosQA"

MW = os.path.join(APP_DIR, "venv", "Lib", "site-packages", "open_webui", "utils", "middleware.py")
BAK = MW + ".shineos.bak"
PATCH = os.path.join(APP_DIR, "scripts", "patch_openwebui_rag.py")
PATCH_MODELS = os.path.join(APP_DIR, "scripts", "patch_openwebui_models.py")
PATCH_HIST = os.path.join(APP_DIR, "scripts", "patch_openwebui_hist.py")
PY = os.path.join(APP_DIR, "venv", "Scripts", "python.exe")
LOG = os.path.join(APP_DIR, "logs", "repair.log")


def log(m):
    line = str(m)
    print(line)
    try:
        open(LOG, "a", encoding="utf-8").write(line + "\n")
    except Exception:
        pass


def main():
    log("=== ShineosQA 修復開始 ===")
    if not (__import__("os").path.exists(PY)):
        log("ERROR: venv python が見つかりません: " + PY)
        return 1

    # 1) middleware をバックアップから復元して最新パッチを再適用
    try:
        shutil.copy2(BAK, MW)
        log("middleware をバックアップから復元しました")
    except Exception as e:
        log(f"WARN: バックアップ復元スキップ: {e}")

    for script in (PATCH_MODELS, PATCH, PATCH_HIST):
        if not os.path.isfile(script):
            log(f"WARN: パッチスクリプトが見つからないためスキップ: {script}")
            continue
        # パッチスクリプトは同一プロセスで実行する（sys.prefix が venv のまま判定できる）
        buf = io.StringIO()
        try:
            with contextlib.redirect_stdout(buf):
                runpy.run_path(script, run_name="__main__")
            code = 0
        except SystemExit as e:
            code = e.code if isinstance(e.code, int) else 1
        except Exception as e:
            code = 1
            log(f"  EXCEPTION: {e}")
        log(f"{os.path.basename(script)}: exit={code} {buf.getvalue().strip()[:150]}")

    # 2) サービス再起動
    subprocess.run(["sc.exe", "stop", "ShineosQA"], capture_output=True)
    time.sleep(5)
    subprocess.run(["sc.exe", "start", "ShineosQA"], capture_output=True)
    log("ShineosQA サービスを再起動しました。30秒ほどで画面を開けるようになります。")
    log("=== 修復完了 ===")
    return 0


if __name__ == "__main__":
    sys.exit(main())
