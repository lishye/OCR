import json
import importlib
import re


SCHEMA_MAP = {
    "PartNumber": ["PartNumber", "PN", "P/N", "料号", "物料号"],
    "Description": ["Description", "DESC", "描述", "品名"],
    "Quantity": ["Quantity", "QTY", "Qty", "数量"],
    "DateCode": ["DateCode", "Date Code", "DC", "生产日期"],
    "LotNo": ["LotNo", "Lot", "LOT", "批号"],
    "Supplier": ["Supplier", "供货商", "供应商"],
    "Manufacturer": ["Manufacturer", "MFG", "制造商"],
    "Origin": ["Origin", "产地"],
    "Brand": ["Brand", "品牌"],
    "MPN": ["MPN"],
    "PO": ["PO", "PO#", "采购单"],
    "Customer": ["Customer", "客户"],
}

SCHEMA = list(SCHEMA_MAP.keys())

PREFERRED_RULE_FIELDS = {
    "PartNumber",
    "Quantity",
    "DateCode",
    "LotNo",
    "Brand",
    "Origin",
    "PO",
}


def normalize_text(text: str) -> str:
    text = text.replace("\n", " ").replace("\r", " ")
    text = re.sub(r"\s+", " ", text)
    return text.strip()


def first_uie_value(blocks: list[dict]) -> str | None:
    if not blocks:
        return None
    first = blocks[0]
    value = first.get("text")
    if isinstance(value, str):
        return value.strip() or None
    return None


def from_uie_result(raw_result: list[dict]) -> dict:
    if not raw_result:
        return {k: None for k in SCHEMA}

    one = raw_result[0]
    result = {}
    for field in SCHEMA:
        result[field] = first_uie_value(one.get(field, []))
    return result


def build_alias_field_pairs() -> list[tuple[str, str]]:
    pairs: list[tuple[str, str]] = []
    for field, aliases in SCHEMA_MAP.items():
        for alias in aliases:
            pairs.append((alias, field))
    pairs.sort(key=lambda x: len(x[0]), reverse=True)
    return pairs


ALIAS_FIELD_PAIRS = build_alias_field_pairs()


def build_alias_regex() -> re.Pattern:
    escaped = [re.escape(alias) for alias, _ in ALIAS_FIELD_PAIRS]
    pattern = r"(?<![A-Za-z0-9])(" + "|".join(escaped) + r")\s*[:：#]?"
    return re.compile(pattern, re.IGNORECASE)


ALIAS_PATTERN = build_alias_regex()

BOUNDARY_TOKENS = [
    "PartNumber", "PN", "P/N", "Description", "DESC", "Quantity", "QTY", "Qty",
    "DateCode", "Date Code", "DC", "LotNo", "Lot", "LOT", "Supplier", "Manufacturer",
    "MFG", "Origin", "Brand", "MPN", "PO", "PO#", "Customer",
    "料号", "物料号", "描述", "品名", "数量", "生产日期", "批号", "供货商", "供应商",
    "制造商", "产地", "品牌", "采购单", "客户", "型号", "序列号", "产品编号", "代码标识",
    "标准", "额定电压", "尺寸", "容量", "类别", "额定容量数值",
]


def trim_at_next_boundary(value: str) -> str:
    cutoff = len(value)
    lowered = value.lower()

    for token in BOUNDARY_TOKENS:
        pattern = token.lower() + ":"
        pos = lowered.find(pattern)
        if pos != -1 and pos < cutoff:
            cutoff = pos

    return value[:cutoff].strip(" :;,.，。")


def alias_to_field(alias_text: str) -> str | None:
    lowered = alias_text.lower()
    for alias, field in ALIAS_FIELD_PAIRS:
        if lowered == alias.lower():
            return field
    return None


def clean_field_value(field: str, value: str | None) -> str | None:
    if not value:
        return None

    cleaned = re.sub(r"\s+", " ", value).strip(" :;,.，。")
    cleaned = trim_at_next_boundary(cleaned)
    if not cleaned:
        return None

    if field in {"PartNumber", "MPN"}:
        m = re.search(r"[A-Za-z0-9][A-Za-z0-9_\-./]{3,}", cleaned)
        return m.group(0) if m else None

    if field == "Quantity":
        m = re.search(r"\d{1,9}(?:\.\d+)?", cleaned)
        return m.group(0) if m else None

    if field == "DateCode":
        m = re.search(r"\d{4,8}|[A-Za-z]{1,3}\d{2,4}|\d{2}[A-Za-z]{1,2}\d{1,2}", cleaned)
        return m.group(0) if m else None

    if field == "Origin":
        m = re.search(r"(?:MADE\s*IN\s*[A-Za-z ]+|[A-Za-z ]{3,}|[\u4e00-\u9fa5]{2,})", cleaned, re.IGNORECASE)
        if not m:
            return None
        return re.sub(r"\s+", " ", m.group(0)).strip()

    if field in {"Brand", "Supplier", "Manufacturer", "Customer", "Description"}:
        # Remove trailing tokens that look like a following key name.
        cleaned = re.sub(r"\b(?:LotNo|DateCode|QTY|Quantity|MPN|PN|P/N|Supplier|Brand|PO|Customer)\b.*$", "", cleaned, flags=re.IGNORECASE)
        cleaned = trim_at_next_boundary(cleaned)
        cleaned = cleaned.strip(" :;,.，。")
        return cleaned or None

    if field == "PO":
        m = re.search(r"[A-Za-z0-9\-]{3,}", cleaned)
        return m.group(0) if m else None

    if field == "LotNo":
        m = re.search(r"[A-Za-z0-9\-]{3,}", cleaned)
        return m.group(0) if m else None

    return cleaned


def segment_by_aliases(text: str) -> dict:
    result = {k: None for k in SCHEMA}
    matches = list(ALIAS_PATTERN.finditer(text))
    if not matches:
        return result

    for idx, m in enumerate(matches):
        alias = m.group(1)
        field = alias_to_field(alias)
        if field is None:
            continue

        value_start = m.end()
        value_end = matches[idx + 1].start() if idx + 1 < len(matches) else len(text)
        raw_value = text[value_start:value_end]
        candidate = clean_field_value(field, raw_value)
        if candidate and not result[field]:
            result[field] = candidate

    return result


def regex_fallback(text: str) -> dict:
    segmented = segment_by_aliases(text)

    # Extra targeted fallback for patterns not always preceded by explicit field names.
    if not segmented["DateCode"]:
        m = re.search(r"\(9D\)\s*([A-Za-z0-9\-]{2,20})", text, re.IGNORECASE)
        if m:
            segmented["DateCode"] = clean_field_value("DateCode", m.group(1))

    if not segmented["Quantity"]:
        m = re.search(r"\(Q\)\)?\s*(\d{1,9})", text, re.IGNORECASE)
        if m:
            segmented["Quantity"] = clean_field_value("Quantity", m.group(1))

    if not segmented["Origin"]:
        m = re.search(r"Made\s*in\s*([A-Za-z ]{2,40})", text, re.IGNORECASE)
        if m:
            segmented["Origin"] = clean_field_value("Origin", f"MADE IN {m.group(1)}")

    return segmented


def merge_result(primary: dict, secondary: dict) -> dict:
    merged = {}
    for field in SCHEMA:
        uie_value = clean_field_value(field, primary.get(field))
        rule_value = clean_field_value(field, secondary.get(field))

        if field in PREFERRED_RULE_FIELDS:
            merged[field] = rule_value or uie_value
        else:
            merged[field] = uie_value or rule_value
    return merged


def extract_label_fields(ie, text: str) -> dict:
    normalized = normalize_text(text)
    uie_raw = ie(normalized)
    uie_fields = from_uie_result(uie_raw)
    rule_fields = regex_fallback(normalized)
    merged = merge_result(uie_fields, rule_fields)
    return {
        "text": normalized,
        "uie": uie_fields,
        "rule": rule_fields,
        "fields": merged,
    }


def build_uie_extractor():
    # Compatibility shim: some aistudio-sdk versions do not expose hub.download,
    # but paddlenlp imports it at module import time.
    try:
        aistudio_hub = importlib.import_module("aistudio_sdk.hub")
        if not hasattr(aistudio_hub, "download"):
            def _download_not_supported(*args, **kwargs):
                raise RuntimeError("当前 aistudio-sdk 不支持 hub.download，请升级或更换版本。")

            setattr(aistudio_hub, "download", _download_not_supported)
    except ModuleNotFoundError:
        pass

    try:
        paddlenlp = importlib.import_module("paddlenlp")
    except ModuleNotFoundError as ex:
        raise RuntimeError(
            "未安装 paddlenlp。请先执行: python -m pip install -r requirements-paddle.txt"
        ) from ex

    return paddlenlp.Taskflow("information_extraction", schema=SCHEMA, model="uie-base")


if __name__ == "__main__":
    samples = [
        "1W272CM: (P)HCM1A1305V2-R22-R1: (Q))250: Description: Inductor EAT·N: Magnetics 30051712013575: 4516019951: HCM1A1305V2-R22-R1: 250: (9D)JJ18: ES: (1L)MadeinChina:",
        "Brand: Panasonic LotNo: Y5N21D0M40U1 Description: 貼片铝電解電容 DateCode: 2547 QTY: 500 MPN: EEHZE1E331V Supplier: 上海英恒电子有限公司 PN: G051002139100999",
        "产地: MADE IN JAPAN 品牌: Panasonic 额定容量数值: 5.00 序列号: Y5N21DOM30N1 型号: EEHZE1E331V 批次号: 022 数量: 500 pcs. 生产日期: 2025 11 23 标准: EIAJC-3 额定电压: 25V 尺寸: 10 × 10.5 产品编号: 108010 代码标识: (3N)2 Y5N21D0M30N1 制造商: Panasonic Industry Co., Ltd. 容量: 330 µF 类别: FIXED ALUMINUM ELECTROLYTIC CAPACITOR",
        "1W272CM: (P)HCM1A1305V2-R22-R1: (Q))250: Description: Inductor EAT·N: Magnetics 30051712013575: 4516019951: HCM1A1305V2-R22-R1: 250: (9D)JJ18: ES: (1L)MadeinChina:",
        "KEMET: KEMET RoHS-PRC: RoHS-PRC QUANTITY: 4000 CUSTP.O#: 3401358961 N: N SPL: 490D (04477261: ( 04477261 MEXICO: MEXICO KEMETP/N: C0805C334K5RACAUTO 31433: 31433 D/C: 2612 CUSTP/N: KEMC0805C334K5RACAUTO 80A88C13: 80A88C13 ID: 261270415 51: 51 aYAGEOcomcompan1: aYAGEOcom compan 1",
    ]

    ie = build_uie_extractor()
    for idx, sample in enumerate(samples, start=1):
        result = extract_label_fields(ie, sample)
        print(f"===== sample #{idx} =====")
        print(json.dumps(result, ensure_ascii=False, indent=2))
