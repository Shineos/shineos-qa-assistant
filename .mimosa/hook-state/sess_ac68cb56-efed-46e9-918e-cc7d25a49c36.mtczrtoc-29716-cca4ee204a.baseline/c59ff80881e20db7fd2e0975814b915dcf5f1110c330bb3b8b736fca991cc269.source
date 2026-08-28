#!/usr/bin/env python3
"""社内知恵袋: 登録済みナレッジコレクションをプリセットモデルへ紐付ける（冪等）

背景（v1.0.47）:
- Open WebUI の RAG はモデルの meta.knowledge に紐付いたコレクションのみ検索する。
  これが無いと社内文書が参照されず、ハルシネーションの原因になる
  （0.11.0 実機検証: setup_knowledge 単体では紐付けが行われない）。
- function_calling は 'legacy'（middleware の knowledge_files → metadata.files
  経由の常時注入。patch_openwebui_rag.py とセットで動作）に統一する。

使い方（setup_knowledge.ps1 から venv python で呼ばれる）:
  python attach_knowledge_to_models.py --base-url http://localhost:8080 \
      --email admin@localhost --password admin [--models 社内知恵袋,経費精算ガイド]

終了コード: 0 = 成功（紐付け済み含む）/ 1 = 失敗
"""
import argparse
import json
import sys
import urllib.parse
import urllib.request


def http_json(base_url, path, method="GET", token=None, body=None, timeout=60):
    url = base_url.rstrip("/") + path
    data = None
    headers = {"Content-Type": "application/json"}
    if token:
        headers["Authorization"] = "Bearer " + token
    if body is not None:
        data = json.dumps(body, ensure_ascii=False).encode("utf-8")
    req = urllib.request.Request(url, data=data, headers=headers, method=method)
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        raw = resp.read().decode("utf-8")
    return json.loads(raw) if raw else None


def signin(base_url, email, password):
    r = http_json(base_url, "/api/v1/auths/signin", "POST", body={"email": email, "password": password})
    if not r or not r.get("token"):
        raise RuntimeError("signin failed")
    return r["token"]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--base-url", default="http://localhost:8080")
    ap.add_argument("--email", default="admin@localhost")
    ap.add_argument("--password", default="admin")
    ap.add_argument("--models", default="社内知恵袋,経費精算ガイド,ITヘルプデスク")
    args = ap.parse_args()

    token = signin(args.base_url, args.email, args.password)

    # 1) 既存コレクション一覧（{items:[...]} 旧配列の両対応）
    colls = http_json(args.base_url, "/api/v1/knowledge/", token=token)
    items = colls.get("items") if isinstance(colls, dict) else colls
    coll_ids = [c["id"] for c in (items or []) if c.get("id")]
    print(f"collections: {len(coll_ids)}")
    if not coll_ids:
        print("no collections - nothing to attach (ok)")
        return 0

    # 2) 各プリセットへ knowledge を紐付け（既存維持・重複排除）
    failed = 0
    for name in [m.strip() for m in args.models.split(",") if m.strip()]:
        try:
            q = urllib.parse.quote(name)
            model = http_json(args.base_url, f"/api/v1/models/model?id={q}", token=token)
            if not model or model.get("id") != name:
                print(f"WARN: model not found: {name}")
                continue
            meta = model.get("meta") or {}
            existing = [k for k in (meta.get("knowledge") or []) if isinstance(k, dict)]
            ids = {k.get("id") for k in existing}
            merged = existing + [{"id": cid} for cid in coll_ids if cid not in ids]
            meta["knowledge"] = merged
            params = model.get("params") or {}
            params["function_calling"] = "legacy"
            body = {
                "id": model["id"],
                "base_model_id": model.get("base_model_id"),
                "name": model["name"],
                "meta": meta,
                "params": params,
                "access_grants": [],
                "is_active": True,
            }
            updated = http_json(args.base_url, "/api/v1/models/model/update", "POST", token, body)
            ok = isinstance(updated, dict) and updated.get("id") == name
            kn = (updated.get("meta", {}).get("knowledge") or []) if isinstance(updated, dict) else []
            print(f"{'OK' if ok else 'ERR'}: {name} (knowledge={len(kn)}, fc={params['function_calling']})")
            if not ok:
                failed += 1
        except Exception as e:
            print(f"ERR: {name}: {e}")
            failed += 1
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
