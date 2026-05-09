from __future__ import annotations

import concurrent.futures
import logging
import time
from pathlib import Path
from typing import Any

import cv2
import numpy as np

try:
    from pyzbar.pyzbar import decode as zbar_decode, ZBarSymbol as _ZBarSymbol
    # ZBar 仅启用一维码，二维码由 WeChatQRCode / OpenCV QR 负责，避免重复识别。
    _ZBAR_1D_SYMBOLS = [
        _ZBarSymbol.CODE39,
        _ZBarSymbol.CODE93,
        _ZBarSymbol.CODE128,
        _ZBarSymbol.I25,
        _ZBarSymbol.EAN8,
        _ZBarSymbol.EAN13,
        _ZBarSymbol.UPCA,
        _ZBarSymbol.UPCE,
        _ZBarSymbol.CODABAR,
        _ZBarSymbol.PDF417,
    ]
except Exception:
    zbar_decode = None
    _ZBAR_1D_SYMBOLS = None


logger = logging.getLogger(__name__)


def _has_file(path: Path) -> bool:
    return path.exists() and path.is_file()


class BarcodeScanner:
    """多级识别: WeChatQRCode -> OpenCV QR -> ZBar。"""

    def __init__(self, model_dir: str | Path | None = None) -> None:
        if model_dir is None:
            model_dir = Path(__file__).resolve().parent
        self.model_dir = Path(model_dir)

        self.wechat_detector = self._build_wechat_detector()
        self.qr_detector = cv2.QRCodeDetector()

    def _build_wechat_detector(self) -> Any | None:
        if not hasattr(cv2, "wechat_qrcode_WeChatQRCode"):
            return None

        model_root = self.model_dir / "models"
        detect_proto = model_root / "detect.prototxt"
        detect_model = model_root / "detect.caffemodel"
        sr_proto = model_root / "sr.prototxt"
        sr_model = model_root / "sr.caffemodel"

        required_files = [detect_proto, detect_model, sr_proto, sr_model]
        if not all(_has_file(p) for p in required_files):
            # Backward compatibility: also support legacy flat layout.
            detect_proto = self.model_dir / "detect.prototxt"
            detect_model = self.model_dir / "detect.caffemodel"
            sr_proto = self.model_dir / "sr.prototxt"
            sr_model = self.model_dir / "sr.caffemodel"
            required_files = [detect_proto, detect_model, sr_proto, sr_model]
            if not all(_has_file(p) for p in required_files):
                return None

        try:
            return cv2.wechat_qrcode_WeChatQRCode(
                str(detect_proto),
                str(detect_model),
                str(sr_proto),
                str(sr_model),
            )
        except Exception:
            return None

    def _decode_with_wechat(self, img: np.ndarray) -> list[dict[str, Any]]:
        if self.wechat_detector is None:
            return []

        try:
            results, points = self.wechat_detector.detectAndDecode(img)
        except Exception:
            return []

        decoded: list[dict[str, Any]] = []
        if not results:
            return decoded

        for i, text in enumerate(results):
            if not text:
                continue
            pts = None
            if points is not None and i < len(points):
                pts = np.asarray(points[i]).tolist()
            decoded.append(
                {
                    "value": str(text).strip(),
                    "symbology": "QR_CODE",
                    "engine": "wechat_qrcode",
                    "points": pts,
                }
            )
        return decoded

    def _decode_with_opencv_qr(self, img: np.ndarray) -> list[dict[str, Any]]:
        decoded: list[dict[str, Any]] = []

        try:
            ok, texts, points, _ = self.qr_detector.detectAndDecodeMulti(img)
            if ok and texts:
                for i, text in enumerate(texts):
                    text = str(text).strip()
                    if not text:
                        continue
                    pts = None
                    if points is not None and i < len(points):
                        pts = np.asarray(points[i]).tolist()
                    decoded.append(
                        {
                            "value": text,
                            "symbology": "QR_CODE",
                            "engine": "opencv_qr",
                            "points": pts,
                        }
                    )
        except Exception:
            pass

        if decoded:
            return decoded

        try:
            text, points, _ = self.qr_detector.detectAndDecode(img)
            text = str(text).strip()
            if text:
                decoded.append(
                    {
                        "value": text,
                        "symbology": "QR_CODE",
                        "engine": "opencv_qr",
                        "points": np.asarray(points).tolist() if points is not None else None,
                    }
                )
        except Exception:
            pass

        return decoded

    def _decode_with_zbar(self, img: np.ndarray) -> list[dict[str, Any]]:
        if zbar_decode is None:
            return []

        src = img
        h, w = src.shape[:2]
        max_side = max(h, w)
        if max_side > 1800:
            scale = 1800.0 / float(max_side)
            src = cv2.resize(src, None, fx=scale, fy=scale, interpolation=cv2.INTER_AREA)

        # 多种预处理形态提升 1D 条码识别率。
        gray = cv2.cvtColor(src, cv2.COLOR_BGR2GRAY) if src.ndim == 3 else src.copy()
        variants = [
            gray,
            cv2.GaussianBlur(gray, (3, 3), 0),
            cv2.threshold(gray, 0, 255, cv2.THRESH_BINARY + cv2.THRESH_OTSU)[1],
            cv2.adaptiveThreshold(
                gray, 255, cv2.ADAPTIVE_THRESH_GAUSSIAN_C, cv2.THRESH_BINARY, 31, 2
            ),
            cv2.equalizeHist(gray),
            src,
        ]

        results: dict[tuple[str, str], dict[str, Any]] = {}
        for v in variants:
            try:
                codes = zbar_decode(v, symbols=_ZBAR_1D_SYMBOLS)
            except Exception:
                continue

            for code in codes:
                try:
                    text = code.data.decode("utf-8", errors="ignore").strip()
                except Exception:
                    text = str(code.data).strip()
                text = text.replace("\x00", "").strip()
                if not text:
                    continue

                code_type = str(getattr(code, "type", "UNKNOWN"))
                rect = getattr(code, "rect", None)
                polygon = getattr(code, "polygon", None)

                key = (text, code_type)
                if key in results:
                    continue

                results[key] = {
                    "value": text,
                    "symbology": code_type,
                    "engine": "zbar",
                    "rect": [rect.left, rect.top, rect.width, rect.height]
                    if rect is not None
                    else None,
                    "points": [[p.x, p.y] for p in polygon] if polygon else None,
                }

        return list(results.values())

    @staticmethod
    def _normalize_symbology(symbology: str) -> str:
        s = symbology.strip().upper().replace("-", "").replace("_", "")
        if s in {"QRCODE", "QR"}:
            return "QR_CODE"
        if s in {"CODE39", "CODE3OF9"}:
            return "CODE39"
        if s in {"CODE128", "CODE 128"}:
            return "CODE128"
        return symbology.strip().upper() if symbology else "UNKNOWN"

    @staticmethod
    def _merge_unique(items: list[dict[str, Any]]) -> list[dict[str, Any]]:
        merged: list[dict[str, Any]] = []
        seen: set[tuple[str, str]] = set()

        for item in items:
            value = str(item.get("value") or "").strip()
            symbology = BarcodeScanner._normalize_symbology(
                str(item.get("symbology") or "UNKNOWN")
            )
            if not value:
                continue
            key = (value, symbology)
            if key in seen:
                continue
            seen.add(key)
            item["symbology"] = symbology
            merged.append(item)

        return merged

    def scan_labels(
        self, img: np.ndarray, timeout_s: float | None = None
    ) -> list[dict[str, Any]]:
        if img is None or not isinstance(img, np.ndarray):
            return []

        def _run(fn, *args):
            if timeout_s is None:
                return fn(*args)
            with concurrent.futures.ThreadPoolExecutor(max_workers=1) as ex:
                fut = ex.submit(fn, *args)
                try:
                    return fut.result(timeout=timeout_s)
                except concurrent.futures.TimeoutError:
                    logger.warning("%s 超时 (%.1fs)", fn.__name__, timeout_s)
                    return []

        all_results: list[dict[str, Any]] = []

        t0 = time.perf_counter()
        wechat_r = _run(self._decode_with_wechat, img)
        logger.debug("wechat_qrcode: %d 个结果 %.0fms", len(wechat_r), (time.perf_counter() - t0) * 1000)
        all_results.extend(wechat_r)

        # WeChatQRCode 已找到二维码则跳过 OpenCV QR，避免重复且节省耗时。
        wechat_found_qr = any(r.get("symbology") == "QR_CODE" for r in wechat_r)
        if not wechat_found_qr:
            t1 = time.perf_counter()
            opencv_r = _run(self._decode_with_opencv_qr, img)
            logger.debug("opencv_qr: %d 个结果 %.0fms", len(opencv_r), (time.perf_counter() - t1) * 1000)
            all_results.extend(opencv_r)
        else:
            logger.debug("opencv_qr: 跳过（wechat_qrcode 已识别二维码）")

        # ZBar 专注一维码（_ZBAR_1D_SYMBOLS 已排除 QRCODE）。
        t2 = time.perf_counter()
        zbar_r = _run(self._decode_with_zbar, img)
        logger.debug("zbar: %d 个结果 %.0fms", len(zbar_r), (time.perf_counter() - t2) * 1000)
        all_results.extend(zbar_r)

        return self._merge_unique(all_results)

    def scan_label(self, img: np.ndarray, timeout_s: float | None = None) -> str | None:
        items = self.scan_labels(img, timeout_s=timeout_s)
        if not items:
            return None
        return str(items[0].get("value") or "") or None


scanner = BarcodeScanner()


def scan_label(img: np.ndarray) -> str | None:
    """兼容旧接口: 返回第一个识别结果文本。"""
    return scanner.scan_label(img)


def scan_labels(img: np.ndarray) -> list[dict[str, Any]]:
    return scanner.scan_labels(img)


def benchmark_images(image_paths: list[str]) -> dict[str, Any]:
    total = 0
    success = 0
    total_ms = 0.0
    details: list[dict[str, Any]] = []

    for p in image_paths:
        total += 1
        img = cv2.imread(p)
        if img is None:
            details.append({"image": p, "ok": False, "error": "read_failed"})
            continue

        t0 = time.perf_counter()
        items = scanner.scan_labels(img)
        elapsed_ms = (time.perf_counter() - t0) * 1000.0
        total_ms += elapsed_ms

        ok = bool(items)
        if ok:
            success += 1

        details.append(
            {
                "image": p,
                "ok": ok,
                "elapsed_ms": round(elapsed_ms, 2),
                "count": len(items),
                "results": items,
            }
        )

    return {
        "total": total,
        "success": success,
        "success_rate": round((success / total) if total else 0.0, 4),
        "avg_ms": round((total_ms / total) if total else 0.0, 2),
        "details": details,
        "wechat_enabled": scanner.wechat_detector is not None,
        "zbar_enabled": zbar_decode is not None,
    }
