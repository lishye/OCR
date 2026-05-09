# Local Service (OpenVINO GenAI + FastAPI)

## 1) Install

```powershell
cd local-service
python -m venv .venv
.\.venv\Scripts\Activate.ps1
pip install -r requirements.txt
```

## 2) Run

```powershell
python -m uvicorn app:app --host 127.0.0.1 --port 8000
```

Health check:

```powershell
curl http://127.0.0.1:8000/health
```

## API Contract

### POST /ai/generate (application/json)

Request JSON:

```json
{
  "prompt": "Extract JSON from this OCR text: ...",
  "max_new_tokens": 256,
  "temperature": 0.1
}
```

Response JSON:

```json
{
  "response": "{ \"candidates\": { ... } }"
}
```

Notes:
- LLM model is loaded lazily on first request from `qwen2.5_7b_ov_int4/` directory.
- Set `_LLM_DEVICE = "GPU"` / `"NPU"` / `"CPU"` in `app.py` as needed.

# Local Service (Paddle + FastAPI) 启动服务

```powershell
wsl -e bash -lc "cd '/mnt/d/source/Research/C#/OCR/local-service'; bash ./start-paddle-wsl.sh"
```

Health check:
```powershell
curl http://127.0.0.1:8001/health
```

## test
```powershell
wsl -e bash -lc "curl --noproxy '*' -s -X POST http://127.0.0.1:8001/ocr -F 'file=@/mnt/d/source/Research/C#/OCR/Sample/single-input/IMG_20260423_100016_1.jpg'"
```

## Barcode/QR (OpenCV + WeChatQRCode + ZBar)

Install optional dependencies:

```powershell
cd local-service
.\.venv\Scripts\Activate.ps1
pip install -r requirements-barcode.txt
```

Place WeChatQRCode model files in `local-service/`:

- `detect.prototxt`
- `detect.caffemodel`
- `sr.prototxt`
- `sr.caffemodel`

If the 4 model files are missing, code will automatically fallback to OpenCV `QRCodeDetector` and ZBar.

Run benchmark with sample images:

```powershell
cd local-service
.\.venv\Scripts\python.exe .\benchmark_readbarcode.py ..\Sample --output ..\Sample\barcode-benchmark.json
```