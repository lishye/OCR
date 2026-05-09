from __future__ import annotations

import threading
from typing import Any

from fastapi import FastAPI, HTTPException
from pydantic import BaseModel

app = FastAPI(title="OpenVINO LLM Qwen2.5 INT4 Service", version="2.0.0")

# ── OpenVINO GenAI LLM ────────────────────────────────────────────────────────
_llm_lock = threading.Lock()
_llm_pipe: Any | None = None
_LLM_MODEL_DIR = r"D:\source\Research\C#\OpenVINO\qwen2.5_7b_ov_int4"
_LLM_DEVICE = "GPU"  # 可改为 "NPU" 或 "CPU"


def get_llm_pipe() -> Any:
    global _llm_pipe
    if _llm_pipe is not None:
        return _llm_pipe
    with _llm_lock:
        if _llm_pipe is None:
            import openvino_genai as ov_genai  # type: ignore
            _llm_pipe = ov_genai.LLMPipeline(_LLM_MODEL_DIR, _LLM_DEVICE)
    return _llm_pipe


class AiGenerateRequest(BaseModel):
    prompt: str
    max_new_tokens: int = 256
    temperature: float = 0.1


class AiGenerateResponse(BaseModel):
    response: str


@app.get("/health")
def health() -> dict[str, str]:
    return {"status": "ok"}


@app.post("/ai/generate", response_model=AiGenerateResponse)
def ai_generate(req: AiGenerateRequest) -> AiGenerateResponse:
    """使用本地 OpenVINO LLM 生成文本（Qwen2.5 INT4）。"""
    import openvino_genai as ov_genai  # type: ignore

    try:
        pipe = get_llm_pipe()
    except Exception as exc:
        raise HTTPException(status_code=503, detail=f"LLM not available: {exc}") from exc

    formatted_prompt = (
        f"<|im_start|>user\n{req.prompt}<|im_end|>\n<|im_start|>assistant\n"
    )

    config = ov_genai.GenerationConfig()
    config.max_new_tokens = req.max_new_tokens
    config.temperature = req.temperature
    config.do_sample = False

    try:
        result = pipe.generate(formatted_prompt, config)
    except Exception as exc:
        raise HTTPException(status_code=500, detail=f"generation failed: {exc}") from exc

    return AiGenerateResponse(response=str(result))
