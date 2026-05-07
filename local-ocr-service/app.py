from __future__ import annotations

import io
import threading
from typing import Any

import cv2
import easyocr
import numpy as np
from fastapi import FastAPI, File, HTTPException, UploadFile
from PIL import Image

app = FastAPI(title="Local OCR Service", version="2.0.0")

_reader_lock = threading.Lock()
_ocr_reader: easyocr.Reader | None = None


@app.on_event("startup")
def _warmup_ocr_reader() -> None:
    try:
        get_ocr_reader()
    except Exception:
        # Keep service bootable; actual OCR endpoint will surface details if init fails.
        pass


def get_ocr_reader() -> easyocr.Reader:
    global _ocr_reader
    if _ocr_reader is not None:
        return _ocr_reader

    with _reader_lock:
        if _ocr_reader is None:
            # Lazy-init to keep /health responsive; model files may download on first OCR call.
            _ocr_reader = easyocr.Reader(["ch_sim", "en"], gpu=False)

    return _ocr_reader


def _to_jsonable(value: Any) -> Any:
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
    return value


_ROTATIONS = [
    (0,   None),                          # 原始方向
    (90,  cv2.ROTATE_90_CLOCKWISE),
    (180, cv2.ROTATE_180),
    (270, cv2.ROTATE_90_COUNTERCLOCKWISE),
]
_ORIENT_MAX_PX = 800   # 方向检测用缩略图最大边长
_ORIENT_MIN_CONF = 0.6 # 原始方向平均置信度超过此值则跳过检测，直接使用原图


def _auto_rotate(np_img: np.ndarray, reader: easyocr.Reader) -> tuple[np.ndarray, int]:
    """返回旋转后的图像及旋转角度（0/90/180/270）。
    先在缩略图上检测4个方向的平均置信度，选最高者；
    若原始方向置信度已足够高则跳过额外检测，避免不必要开销。
    """
    h, w = np_img.shape[:2]
    scale = min(1.0, _ORIENT_MAX_PX / max(h, w, 1))
    if scale < 1.0:
        thumb = cv2.resize(np_img, (int(w * scale), int(h * scale)), interpolation=cv2.INTER_AREA)
    else:
        thumb = np_img

    # 先检测原始方向
    result0 = reader.readtext(thumb)
    conf0 = (sum(r[2] for r in result0) / len(result0)) if result0 else 0.0
    if conf0 >= _ORIENT_MIN_CONF:
        return np_img, 0  # 原始方向置信度足够，无需旋转

    best_angle = 0
    best_conf = conf0
    for angle, rotate_code in _ROTATIONS[1:]:
        rotated_thumb = cv2.rotate(thumb, rotate_code)
        result = reader.readtext(rotated_thumb)
        conf = (sum(r[2] for r in result) / len(result)) if result else 0.0
        if conf > best_conf:
            best_conf = conf
            best_angle = angle

    if best_angle == 0:
        return np_img, 0
    rotate_code = next(code for deg, code in _ROTATIONS if deg == best_angle and code is not None)
    return cv2.rotate(np_img, rotate_code), best_angle


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
        ocr_reader = get_ocr_reader()
        np_img, rotation = _auto_rotate(np_img, ocr_reader)
        # result: list of (bbox, text, confidence)
        result = ocr_reader.readtext(np_img)
    except Exception as exc:
        raise HTTPException(status_code=500, detail=f"ocr failed: {exc}") from exc

    lines = [item[1] for item in result if item[1].strip()]
    text = " ".join(lines)

    # Keep contract identical to original C# expectation:
    # - text: full OCR text joined by spaces
    # - fields: empty dict (C# rule-engine extracts structured fields)
    # - lines: list of recognized text lines
    # - raw: raw OCR result for debugging
    return {
        "text": text,
        "fields": {},
        "lines": lines,
        "raw": _to_jsonable(result),
        "rotation": rotation,
    }
