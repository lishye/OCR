# Local OCR Service (EasyOCR + FastAPI)

## 1) Install

```powershell
cd local-ocr-service
python -m venv .venv
.\.venv\Scripts\Activate.ps1
pip install -r requirements.txt
```

若安装后启动时报 `easyocr` 或 `torch` 缺失，可补装 CPU 版 PyTorch：

```powershell
.\.venv\Scripts\pip.exe install torch torchvision --index-url https://download.pytorch.org/whl/cpu
```

## 2) Run

```powershell
uvicorn app:app --host 127.0.0.1 --port 8000
```

Health check:

```powershell
curl http://127.0.0.1:8000/health
```

## API Contract

### POST /ocr (multipart/form-data)

Request:
- field `file`: image binary (jpg/png/bmp/tif/webp)

Response JSON:

```json
{
  "text": "full ocr text ...",
  "fields": {},
  "lines": ["line1", "line2"],
  "raw": []
}
```

Notes:
- `fields` is intentionally empty by default. Structured extraction stays in C# rule-engine.
- C# `LocalPaddle` provider uses `text` and `fields`.
- OCR engine is EasyOCR (`ch_sim` + `en`) with auto-rotation pre-check.

# Local OCR Service (Paddle + FastAPI) 启动服务

```powershell
wsl -e bash -lc "cd '/mnt/d/source/Research/C#/OCR/local-ocr-service'; bash ./start-paddle-wsl.sh"
```

Health check:
```powershell
curl http://127.0.0.1:8001/health
```

## test
```powershell
wsl -e bash -lc "curl --noproxy '*' -s -X POST http://127.0.0.1:8001/ocr -F 'file=@/mnt/d/source/Research/C#/OCR/Sample/single-input/IMG_20260423_100016_1.jpg'"
```