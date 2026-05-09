from __future__ import annotations

import argparse
import json
from pathlib import Path

from readbarcode import benchmark_images


IMG_EXTS = {".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff", ".webp"}


def collect_images(input_path: Path) -> list[str]:
    if input_path.is_file():
        if input_path.suffix.lower() in IMG_EXTS:
            return [str(input_path)]
        return []

    if not input_path.is_dir():
        return []

    files = [
        str(p)
        for p in sorted(input_path.rglob("*"))
        if p.is_file() and p.suffix.lower() in IMG_EXTS
    ]
    return files


def main() -> int:
    parser = argparse.ArgumentParser(description="Benchmark barcode/QR scan on a folder")
    parser.add_argument(
        "input",
        nargs="?",
        default="../Sample",
        help="image file or folder path (default: ../Sample)",
    )
    parser.add_argument(
        "--output",
        default="../Sample/barcode-benchmark.json",
        help="where to save benchmark json",
    )
    args = parser.parse_args()

    input_path = Path(args.input).resolve()
    image_paths = collect_images(input_path)

    if not image_paths:
        print(f"no images found in: {input_path}")
        return 1

    report = benchmark_images(image_paths)
    report["input"] = str(input_path)

    output_path = Path(args.output).resolve()
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(
        json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8"
    )

    print(f"images: {report['total']}")
    print(f"success: {report['success']}")
    print(f"success_rate: {report['success_rate']:.2%}")
    print(f"avg_ms: {report['avg_ms']}")
    print(f"wechat_enabled: {report['wechat_enabled']}")
    print(f"zbar_enabled: {report['zbar_enabled']}")
    print(f"report: {output_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
