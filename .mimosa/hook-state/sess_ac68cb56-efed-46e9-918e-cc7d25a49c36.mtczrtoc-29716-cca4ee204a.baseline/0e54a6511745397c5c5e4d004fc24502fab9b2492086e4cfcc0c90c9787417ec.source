# -*- coding: utf-8 -*-
"""ShineosQA 修復スクリプト（管理者権限で実行すること）

- Open WebUI の middleware をバックアップから復元し、最新のパッチを再適用する
- 設定は変更しない（ナレッジ・チャット履歴は保持される）
"""
import shutil
import subprocess
import sys
import time

MW = r"C:\Program Files\ShineosQA\venv\Lib\site-packages\open_webui\utils\middleware.py"
BAK = MW + ".shineos.bak"
PATCH = r"C:\Program Files\ShineosQA\scripts\patch_openwebui_rag.py"
PATCH_MODELS = r"C:\Program Files\ShineosQA\scripts\patch_openwebui_models.py"
PY = r"C:\Program Files\ShineosQA\venv\Scripts\python.exe"
LOG = r"C:\Program Files\ShineosQA\logs\repair.log"


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

    for script in (PATCH_MODELS, PATCH):
        r = subprocess.run([PY, script], capture_output=True, text=True, encoding="utf-8", errors="replace")
        log(f"{script.rsplit(chr(92),1)[-1]}: exit={r.returncode} {r.stdout.strip()[:150]}")

    # 2) サービス再起動
    subprocess.run(["sc.exe", "stop", "ShineosQA"], capture_output=True)
    time.sleep(5)
    subprocess.run(["sc.exe", "start", "ShineosQA"], capture_output=True)
    log("ShineosQA サービスを再起動しました。30秒ほどで画面を開けるようになります。")
    log("=== 修復完了 ===")
    return 0


if __name__ == "__main__":
    sys.exit(main())
