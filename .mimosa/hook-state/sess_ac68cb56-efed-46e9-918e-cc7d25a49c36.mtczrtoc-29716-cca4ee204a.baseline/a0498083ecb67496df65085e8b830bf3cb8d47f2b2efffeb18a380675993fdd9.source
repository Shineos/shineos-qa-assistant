# ファイル生成ツールサーバー（PDF / PPTX 生成）
# Open WebUI の「ツールサーバー」（OpenAPI 形式）として登録して使用する
# 起動: python filegen_server.py
import os
import io
import uuid
from pathlib import Path

from fastapi import FastAPI, HTTPException
from fastapi.responses import FileResponse
from pydantic import BaseModel

from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import getSampleStyleSheet
from reportlab.platypus import SimpleDocTemplate, Paragraph, Spacer
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from pptx import Presentation
from pptx.util import Inches, Pt

# 日本語フォントを登録（Windows 標準フォント・最初に登録成功したものを使用）
_REGISTERED_FONT = None
_FONT_CANDIDATES = [
    (r"C:\Windows\Fonts\msgothic.ttc", 0, "MSGothic"),
    (r"C:\Windows\Fonts\meiryo.ttc", 0, "Meiryo"),
    (r"C:\Windows\Fonts\yugothic.ttc", 0, "YuGothic"),
]
for _fpath, _sub, _name in _FONT_CANDIDATES:
    if os.path.exists(_fpath):
        try:
            pdfmetrics.registerFont(TTFont(_name, _fpath, subfontIndex=_sub))
            _REGISTERED_FONT = _name
            break
        except Exception:
            continue

OUT_DIR = Path(os.environ.get("FILEGEN_OUT", str(Path.home() / "shineos-qa-out")))
OUT_DIR.mkdir(parents=True, exist_ok=True)

app = FastAPI(title="File Generation Tools", version="1.0.0")


class PDFRequest(BaseModel):
    title: str = "Document"
    content: str = ""


class PPTXRequest(BaseModel):
    title: str = "Presentation"
    slides: list[dict] = []


@app.post("/create_pdf", summary="Generate a PDF document from text content")
def create_pdf(req: PDFRequest):
    """Create a PDF file from the given title and text content (markdown-like plain text)."""
    if not req.content.strip():
        raise HTTPException(status_code=400, detail="content is required")
    fname = f"pdf_{uuid.uuid4().hex[:8]}.pdf"
    fpath = OUT_DIR / fname
    doc = SimpleDocTemplate(str(fpath), pagesize=A4)
    styles = getSampleStyleSheet()
    # 日本語フォントを本文スタイルに適用
    for _s in styles.byName.values():
        try:
            if _REGISTERED_FONT:
                _s.fontName = _REGISTERED_FONT
        except Exception:
            pass
    story = [Paragraph(req.title, styles["Title"]), Spacer(1, 12)]
    for line in req.content.splitlines():
        line = line.strip()
        if not line:
            continue
        story.append(Paragraph(line, styles["BodyText"]))
    doc.build(story)
    return {"file": fname, "url": f"/files/{fname}", "message": f"PDF created: {fname}"}


@app.post("/create_pptx", summary="Generate a PowerPoint presentation from slide content")
def create_pptx(req: PPTXRequest):
    """Create a PPTX file. slides: [{"title": "...", "content": "..."}]"""
    if not req.slides:
        raise HTTPException(status_code=400, detail="slides are required")
    fname = f"pptx_{uuid.uuid4().hex[:8]}.pptx"
    fpath = OUT_DIR / fname
    prs = Presentation()
    for s in req.slides:
        slide = prs.slides.add_slide(prs.slide_layouts[1])
        slide.shapes.title.text = s.get("title", "")
        body = slide.placeholders[1].text_frame
        body.text = s.get("content", "")
        for para in body.paragraphs[1:]:
            para.text = ""
    prs.save(str(fpath))
    return {"file": fname, "url": f"/files/{fname}", "message": f"PPTX created: {fname}"}


@app.get("/files/{fname}")
def get_file(fname: str):
    fpath = OUT_DIR / fname
    if not fpath.exists():
        raise HTTPException(status_code=404, detail="file not found")
    return FileResponse(str(fpath), filename=fname)


@app.get("/health")
def health():
    return {"status": "ok"}


if __name__ == "__main__":
    import uvicorn

    uvicorn.run(app, host="127.0.0.1", port=9100)
