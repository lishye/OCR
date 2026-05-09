# C# OCR 控制台程序

已实现一个基于 C# 的控制台程序，功能包括：

- 递归扫描指定目录及其子目录中的图片文件
- 识别图片中的条码
- 识别图片中的文字
- 自动提取结构化字段，如 PN、QTY、DateCode、LotNo、Supplier、Description
- 为每张图片单独生成一个同名 JSON 文件

项目目录：

- OcrConsole

核心实现说明：

- 条码识别使用 ZXing
- 文字识别使用 Windows 自带 OCR 引擎
- 识别前会进行灰度化、阈值二值化、2 倍放大等预处理
- 条码和 OCR 会在多种预处理图像上重复尝试，并选择更优结果
- 支持的图片格式：png、jpg、jpeg、bmp、gif、tif、tiff、webp

运行方式：

```powershell
cd OcrConsole
dotnet run -- "D:\images" "D:\output" "zh-Hans"
```

参数说明：

1. 图片根目录
2. JSON 输出根目录
3. OCR 语言代码，可选，默认值为 zh-Hans

输出规则：

- 程序会递归扫描图片根目录
- 输出目录会保持与输入目录一致的相对层级
- 每张图片对应一个 JSON 文件，文件名与原图一致，仅扩展名改为 .json

示例：

- 输入图片：D:\images\batch1\sample.jpg
- 输出 JSON：D:\output\batch1\sample.json

单图 JSON 示例：

```json
{
  "fileName": "sample.jpg",
  "filePath": "D:\\images\\batch1\\sample.jpg",
  "relativePath": "batch1\\sample.jpg",
  "outputJsonPath": "D:\\output\\batch1\\sample.json",
  "barcodes": [
    {
      "format": "CODE_39",
      "value": "1PEEHZE1E331V",
      "sourceVariant": "original"
    }
  ],
  "text": "PN: EEHZE1E331V QTY: 500 Date Code: 2547 LotNo: Y5N21D0M30N1",
  "fields": {
    "partNumber": "EEHZE1E331V",
    "quantity": "500",
    "dateCode": "2547",
    "lotNo": "Y5N21D0M30N1",
    "supplier": null,
    "description": null
  },
  "appliedPreprocessing": [
    "grayscale",
    "threshold-160",
    "upscale-2x"
  ],
  "error": null
}
```

注意事项：

- OCR 依赖 Windows 系统可用的语言包；如果指定语言不可用，程序会尝试回退到用户系统语言
- 结构化字段提取基于 OCR 文本和条码内容的规则匹配，适合标签类图片，但不是通用文档解析器
- 单张图片识别失败时，不会中断整个批处理，错误会记录在对应图片的 error 字段中

## 密钥配置（.env 风格）

项目支持在仓库根目录使用 `.env` / `.env.local`（程序启动时自动加载），用于本地密钥配置，推荐方式：

```dotenv
OCR_ALI_ACCESS_KEY_ID=your_aliyun_ak
OCR_ALI_ACCESS_KEY_SECRET=your_aliyun_sk
OCR_BAILIAN_API_KEY=your_bailian_api_key
```

说明：

- 优先级：系统环境变量 > `.env.local` > `.env` > `App.config`
- `.env` 和 `.env.local` 已被 `.gitignore` 忽略，不会提交到仓库
- 可复制仓库根目录 `.env.example` 作为模板

## 本地离线 AI（OpenVINO GenAI + FastAPI）

`local-service` 使用 OpenVINO GenAI 驱动本地 LLM（Qwen2.5 INT4），提供 AI 字段推理接口。

### 服务接口约定

- `POST /ai/generate`，`application/json`
- 请求：`{ "prompt": "...", "max_new_tokens": 256, "temperature": 0.1 }`
- 返回 JSON：

```json
{
  "response": "{ \"candidates\": { ... } }"
}
```

`fields` 默认为空，结构化字段仍由 C# 规则引擎提取。

### 本地服务启动

```powershell
cd local-service
python -m venv .venv
.\.venv\Scripts\Activate.ps1
pip install -r requirements.txt
uvicorn app:app --host 127.0.0.1 --port 8000
```


### C# 配置项（`OcrConsole/App.config`）

- `OcrProvider`: `Aliyun` / `Windows` / `LocalPaddle`
- `LocalOcrEndpoint`: 本地 OCR 服务地址（默认 `http://127.0.0.1:8000`）
- `LocalOcrTimeoutSeconds`: 本地 OCR 超时秒数

### 一键切换在线/离线测试命令

在线（阿里云 OCR）：

```powershell
cd OcrConsole
dotnet run -- --run --ocr-provider Aliyun ..\Sample ..\Sample\json-output-aliyun
```

离线（本地 EasyOCR 服务）：

```powershell
cd OcrConsole
dotnet run -- --run --ocr-provider LocalPaddle --local-ocr-endpoint http://127.0.0.1:8000 ..\Sample ..\Sample\json-output-localpaddle
```
相关模型训练启动命令:
```powershell
D:\source\Research\LLaMA-Factory> llamafactory_env\Scripts\activate
llamafactory-cli version
llamafactory-cli webui
```

http://localhost:7860/