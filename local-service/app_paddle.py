from __future__ import annotations

import io
import threading
from typing import Any

import numpy as np
from fastapi import FastAPI, File, HTTPException, UploadFile
from PIL import Image

app = FastAPI(title="Local OCR Service (PaddleOCR)", version="1.0.0")

_reader_lock = threading.Lock()
_ocr_engine: Any | None = None


def get_ocr_engine() -> Any:
    global _ocr_engine
    if _ocr_engine is not None:
        return _ocr_engine

    with _reader_lock:
        if _ocr_engine is None:
            from paddleocr import PaddleOCR  # type: ignore
            # PaddleOCR 3.x + paddlepaddle 3.x on CPU:
            #   - Default engine uses PIR executor + OneDNN, which hits a runtime
            #     incompatibility (ConvertPirAttribute2RuntimeAttribute error).
            #   - PP-OCRv5_server models require ~62 GB RAM on paddle_static.
            # Fix: use paddle_static engine + enable_mkldnn=False + PP-OCRv4 mobile
            #      models (det≈4 MB, rec≈10 MB) which run stably on CPU.
            _ocr_engine = PaddleOCR(
                lang="ch",
                ocr_version="PP-OCRv4",
                engine="paddle_static",
                enable_mkldnn=False,
            )

    return _ocr_engine


def _to_jsonable(value: Any) -> Any:
    if value is None or isinstance(value, (bool, int, float, str)):
        return value
    if isinstance(value, np.generic):
        return value.item()
    if isinstance(value, np.ndarray):
        return value.tolist()
    if isinstance(value, list):
        return [_to_jsonable(v) for v in value]
    if isinstance(value, tuple):
        return [_to_jsonable(v) for v in value]
    if isinstance(value, dict):
        return {str(k): _to_jsonable(v) for k, v in value.items()}
    # Unknown type (e.g. paddlex result objects): skip
    return None


def _extract_lines_from_pages(pages: Any) -> list[str]:
    lines: list[str] = []
    if not pages:
        return lines

    for page in pages:
        # PaddleOCR v3 / PaddleX result objects expose rec_texts as an attribute
        rec_texts = None
        if isinstance(page, dict):
            rec_texts = page.get("rec_texts")
        else:
            rec_texts = getattr(page, "rec_texts", None)

        if isinstance(rec_texts, (list, tuple)):
            for text in rec_texts:
                s = str(text).strip()
                if s:
                    lines.append(s)
            continue

        # PaddleOCR v2 fallback: list of [bbox, (text, conf)]
        if isinstance(page, list):
            for item in page:
                try:
                    text = item[1][0] if item and item[1] else ""
                except (IndexError, TypeError):
                    text = ""
                s = str(text).strip()
                if s:
                    lines.append(s)

    return lines


@app.get("/health")
def health() -> dict[str, str]:
    return {"status": "ok"}


@app.post("/ocr")
async def ocr(file: UploadFile = File(...)) -> dict[str, Any]:
    if not file.filename:
        raise HTTPException(status_code=400, detail="missing file")

    data = await file.read()
    if not data:
        raise HTTPException(status_code=400, detail="empty file")

    try:
        image = Image.open(io.BytesIO(data)).convert("RGB")
    except Exception as exc:
        raise HTTPException(status_code=400, detail=f"invalid image: {exc}") from exc

    np_img = np.array(image)

    try:
        engine = get_ocr_engine()
        # v3 常见为 predict()（返回生成器），v2 常见为 ocr()。
        if hasattr(engine, "predict"):
            pages = list(engine.predict(np_img))
        else:
            pages = engine.ocr(np_img, cls=True)
    except Exception as exc:
        raise HTTPException(status_code=500, detail=f"ocr failed: {exc}") from exc

    lines = _extract_lines_from_pages(pages)

    return {
        "text": " ".join(lines),
        "fields": {},
        "lines": lines,
        "rotation": 0,
    }
