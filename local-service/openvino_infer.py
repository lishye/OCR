import openvino_genai as ov_genai

# 1. 初始化推理引擎，指定设备为 'GPU' (Arc Pro)
# 如果想尝试 NPU，可以改为 'NPU'，但在标签识别这种短文本场景，GPU 通常更成熟
device = "GPU"
pipe = ov_genai.LLMPipeline(r"D:\source\Research\C#\OpenVINO\qwen2.5_7b_ov_int4", device)

# 2. 设定推理参数
config = ov_genai.GenerationConfig()
config.max_new_tokens = 128
config.temperature = 0.1  # 电子物料识别需要高度确定性，建议低采样
config.do_sample = False

# 3. 模拟 OCR 输入并推理
ocr_text = "P/N: 12345-ABC QTY: 1000PCS DC: 2412 VENDOR: Intel"
prompt = f"Extract JSON from this OCR text: {ocr_text}"

# 使用简洁的模板，加速首字生成
formatted_prompt = f"<|im_start|>user\n{prompt}<|im_end|>\n<|im_start|>assistant\n"

print("--- 推理结果 ---")
result = pipe.generate(formatted_prompt, config)
print(result)
