#!/usr/bin/env python3
"""社内知恵袋: /資料 コマンドのコード制御パイプライン（v1.0.66）

設計方針（docs/plan-document-creation.md の内容を実装。計画書自体は製品に含めない）:
- モデルの役割は「目次の JSON 出力（構造化出力で強制）」と「セクションの文章化」のみ。
  判断・検索・ファイル化はすべてこのモジュール（コード）が実行する。
- 資料作成は Q&A 経路（拒否システムプロンプト付き）の前に横取りするため、
  「頼んだのに対象外と拒否される」矛盾を回避する。
- 幻覚防止のルールは Q&A と同様の精神を資料向けに置き換えて維持:
  「材料の無いセクションは書かない（社内資料に該当記載なしと明記）」
- 生成 PDF は ShineosMcpoFiles (9003) の配信ディレクトリ (data/mcpo_output) に
  直接書き出し、既存の /files 配信でオフライン閲覧できる。

使い方: middleware から handle_document_request(request, form_data, extra_params, user, topic)
を呼び、戻り値 form_data をそのまま返す（process_chat_payload を早期リターンさせる）。
"""
import asyncio
import datetime
import html
import json
import os
import re
from pathlib import Path

import httpx

MAX_SECTIONS = 5
OUTLINE_TIMEOUT = 60.0
SECTION_TIMEOUT = 120.0
REFUSAL_MARKER = "対象外"

# 日本語フォント（tools/filegen_server.py と同じ候補）
_FONT_CANDIDATES = [
    (r"C:\Windows\Fonts\meiryo.ttc", 0, "Meiryo"),
    (r"C:\Windows\Fonts\msgothic.ttc", 0, "MSGothic"),
    (r"C:\Windows\Fonts\yugothic.ttc", 0, "YuGothic"),
]


def _data_dir() -> Path:
    try:
        from open_webui.env import DATA_DIR
        return Path(DATA_DIR)
    except Exception:
        return Path(os.environ.get("DATA_DIR", r"C:\Program Files\ShineosQA\data"))


def _output_dir() -> Path:
    d = Path(os.environ.get("FILE_EXPORT_DIR") or (_data_dir() / "mcpo_output"))
    d.mkdir(parents=True, exist_ok=True)
    return d


def _file_base_url() -> str:
    return os.environ.get("FILE_EXPORT_BASE_URL") or "http://localhost:9003/files"


def _ollama_base(request) -> str:
    try:
        urls = getattr(request.app.state.config, "OLLAMA_BASE_URLS", None)
        if urls:
            return urls[0].rstrip("/")
    except Exception:
        pass
    return os.environ.get("OLLAMA_BASE_URL", "http://127.0.0.1:11434").rstrip("/")


def _base_model_id(request, model: dict) -> str:
    mid = ""
    try:
        mid = (model.get("info") or {}).get("base_model_id") or ""
    except Exception:
        pass
    return mid or model.get("id") or "qwen2.5:3b"


async def _emit(emitter, description: str, done: bool = False):
    if not emitter:
        return
    try:
        await emitter({"type": "status", "data": {"action": "document", "description": description, "done": done}})
    except Exception:
        pass


async def _make_outline(request, model: dict, topic: str) -> dict:
    """目次を Ollama の構造化出力（format=schema）で生成する。

    チャット用モデル（拒否システムプロンプト付き）を使わず素のベースモデルに
    直接問い合わせるため、Q&A ルールと干渉しない。
    """
    schema = {
        "type": "object",
        "properties": {
            "title": {"type": "string"},
            "sections": {
                "type": "array",
                "items": {
                    "type": "object",
                    "properties": {
                        "heading": {"type": "string"},
                        "query": {"type": "string"},
                    },
                    "required": ["heading", "query"],
                },
            },
        },
        "required": ["title", "sections"],
    }
    prompt = (
        f"社内文書を検索して「{topic}」に関する資料を作ります。\n"
        "資料の表題と、各セクションの見出し・検索語を JSON で出力してください。\n"
        "セクションは2〜5個。検索語は社内文書を探すための短いキーワードにしてください。\n"
        '例: {"title": "出張手当の整理", "sections": [{"heading": "概要", "query": "出張手当"}]}'
    )
    body = {
        "model": _base_model_id(request, model),
        "messages": [{"role": "user", "content": prompt}],
        "stream": False,
        "format": schema,
        "options": {"temperature": 0.2, "num_predict": 400},
    }
    async with httpx.AsyncClient(timeout=OUTLINE_TIMEOUT) as client:
        r = await client.post(_ollama_base(request) + "/api/chat", json=body)
        r.raise_for_status()
        content = (r.json().get("message") or {}).get("content", "")
    try:
        data = json.loads(content)
        title = str(data.get("title") or topic)[:60]
        sections = [
            {"heading": str(s.get("heading") or "")[:60], "query": str(s.get("query") or s.get("heading") or "")[:60]}
            for s in (data.get("sections") or [])
            if isinstance(s, dict) and s.get("heading")
        ]
        sections = sections[:MAX_SECTIONS]
        if sections:
            return {"title": title, "sections": sections}
    except Exception:
        pass
    # フォールバック: 固定テンプレ
    return {
        "title": topic[:60],
        "sections": [
            {"heading": "概要", "query": topic[:40]},
            {"heading": "詳細", "query": topic[:40] + " 手順"},
            {"heading": "まとめ", "query": topic[:40]},
        ],
    }


_SECTION_PROMPT = (
    "社内文書の参考片段を使って、資料のセクション「{heading}」の本文を書いてください。\n"
    "ルール:\n"
    "- 与えられた社内文書の片段だけを使うこと（片段に無い事実・数値を書かない）\n"
    "- 箇条書き中心・簡潔にまとめること\n"
    "- 参考片段の中に記載が無い場合は「この質問は社内文書の対象外です。一般的な情報はチャット入力欄のWeb検索をONにすると回答できます。」とだけ答えること\n"
    "\n参考片段:\n{query}"
)


async def _write_section(request, form_data: dict, heading: str, query: str) -> str:
    """セクション本文を 1 回のチャット完結で書かせる。

    ナレッジ RAG 注入（legacy）はチャット経路に組み込み済みのため、
    ループバックの chat completions を 1 回呼ぶだけで「検索 + 文章化」が完結する。
    材料が無い場合は Q&A の拒否文が返るので、資料向けの表記に置き換える。
    """
    auth = request.headers.get("authorization") or ""
    headers = {"Authorization": auth, "Content-Type": "application/json"} if auth else {"Content-Type": "application/json"}
    payload = {
        "model": form_data.get("model"),
        "messages": [{"role": "user", "content": _SECTION_PROMPT.format(heading=heading, query=query)}],
        "stream": False,
    }
    base = str(request.base_url).rstrip("/")
    async with httpx.AsyncClient(timeout=SECTION_TIMEOUT) as client:
        r = await client.post(base + "/api/chat/completions", json=payload, headers=headers)
        r.raise_for_status()
        data = r.json()
    content = ((data.get("choices") or [{}])[0].get("message") or {}).get("content", "")
    content = str(content).strip()
    if not content or REFUSAL_MARKER in content:
        return "（社内資料に該当記載がありません）"
    # 引用表記は資料ではノイズになるため簡易に落とす
    content = re.sub(r"\[\d+\]", "", content)
    return content


def _render_pdf(title: str, sections: list[dict], out_path: Path):
    """Markdown 風の節構造を PDF にする（reportlab・日本語フォント対応）。"""
    from reportlab.lib.pagesizes import A4
    from reportlab.lib.styles import getSampleStyleSheet
    from reportlab.pdfbase import pdfmetrics
    from reportlab.pdfbase.ttfonts import TTFont
    from reportlab.platypus import Paragraph, SimpleDocTemplate, Spacer

    font_name = None
    for fpath, sub, name in _FONT_CANDIDATES:
        if os.path.exists(fpath):
            try:
                pdfmetrics.registerFont(TTFont(name, fpath, subfontIndex=sub))
                font_name = name
                break
            except Exception:
                continue

    doc = SimpleDocTemplate(str(out_path), pagesize=A4, title=title)
    styles = getSampleStyleSheet()
    for s in styles.byName.values():
        try:
            if font_name:
                s.fontName = font_name
        except Exception:
            pass

    story = [Paragraph(html.escape(title), styles["Title"]), Spacer(1, 12)]
    # 目次
    story.append(Paragraph("目次", styles["Heading2"]))
    for idx, sec in enumerate(sections, 1):
        story.append(Paragraph(html.escape(f"{idx}. {sec['heading']}"), styles["BodyText"]))
    story.append(Spacer(1, 14))
    for idx, sec in enumerate(sections, 1):
        story.append(Paragraph(html.escape(f"{idx}. {sec['heading']}"), styles["Heading2"]))
        story.append(Spacer(1, 6))
        for line in sec["body"].splitlines():
            line = line.strip()
            if not line:
                continue
            if line.startswith(("-", "・", "•")):
                story.append(Paragraph("• " + html.escape(line.lstrip("-・• ").strip()), styles["BodyText"]))
            else:
                story.append(Paragraph(html.escape(line), styles["BodyText"]))
        story.append(Spacer(1, 10))
    doc.build(story)


def _build_pdf(title: str, sections: list[dict]) -> str:
    stamp = datetime.datetime.now().strftime("%Y%m%d_%H%M%S")
    fname = f"doc_{stamp}.pdf"
    out = _output_dir() / fname
    _render_pdf(title, sections, out)
    return f"{_file_base_url()}/{fname}"


async def handle_document_request(request, form_data: dict, extra_params: dict, user, topic: str) -> dict:
    """/資料 コマンドを処理し、form_data.messages を完成通知へ差し替える。

    例外は呼び出し側（middleware パッチ）が捕捉して通常チャットへフォールバックする。
    """
    emitter = extra_params.get("__event_emitter__")
    model = extra_params.get("__model__") or {}

    if not topic:
        form_data["messages"] = [
            {"role": "system", "content": "復唱タスク: ユーザーの文を、最初の文字から最後の文字まで一切変更せずそのまま出力してください。前置き・要約・省略・言い換えは禁止です。"},
            {"role": "user", "content": "主題が空のため資料を作成できませんでした。/資料 の後に半角スペースと主題を付けて、もう一度送信してください。"},
        ]
        return form_data

    await _emit(emitter, "資料の目次を作成しています…")
    outline = await _make_outline(request, model, topic)
    sections = [{"heading": s["heading"], "query": s["query"], "body": ""} for s in outline["sections"]]

    for i, sec in enumerate(sections, 1):
        await _emit(emitter, f"第{i}節「{sec['heading']}」を執筆しています…")
        sec["body"] = await _write_section(request, form_data, sec["heading"], sec["query"])

    await _emit(emitter, "PDF を生成しています…")
    url = await asyncio.to_thread(_build_pdf, outline["title"], sections)
    empty = [s["heading"] for s in sections if "該当記載がありません" in s["body"]]

    note = ""
    if empty:
        note = "\n※ ナレッジに材料がないセクションは「該当記載なし」と記載しています。"

    # チャット文は短くする（長文の復唱は小モデルが省略・脚色するため。
    # 目次・本文の詳細は PDF 内に全て入っている）
    notice = (
        f"資料を作成しました（PDF / {len(sections)}セクション）。\n"
        f"PDF: {url}{note}"
    )
    form_data["messages"] = [
        {"role": "system", "content": "復唱タスク: ユーザーの文を、最初の文字から最後の文字まで一切変更せずそのまま出力してください。前置き・要約・省略・言い換えは禁止です。"},
        {"role": "user", "content": notice},
    ]
    await _emit(emitter, "資料を作成しました", done=True)
    return form_data
