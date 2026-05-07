namespace OcrConsole;

internal static class TemplateFactory
{
    public static IReadOnlyList<FieldRule> Aliyun() =>
    [
        new("PartNumber", ["PN", "料号", "编号", "物料号", "产品编号"], @"\b(?:PN|Part\s*Number|料号|编号)\s*[:：]?\s*([A-Z0-9\-_/]+)", @"\b1P([A-Z0-9\-_/]+)", "", ""),
        new("Description", ["Description", "描述", "物料描述", "类别"], @"\b(?:Description|描述|类别)\s*[:：]?\s*([^:：]+?)\s*(?=\b(?:Date\s*Code|QTY|MPN|Supplier|PN|Lot\s*No|Brand)\b|$)", "", "", ""),
        new("Quantity", ["QTY", "数量","QUANTITY"], @"\b(?:QTY|数量)\s*[:：;]?\s*([0-9]+(?:\s*pcs\.)?)", @"\b3N1[A-Z0-9\-_/]+\s+([0-9]+)\b", "", ""),
        new("DateCode", ["DateCode", "Date Code", "生产日期","D/C"], @"\b(?:Date\s*Code|DateCode|生产日期)\s*[:：]?\s*([0-9A-Z\-_/ ]{4,16})", "", "", "yyyy-MM-dd"),
        new("LotNo", ["LotNo", "Lot No", "Serial No.", "批次号", "序列号"], @"\b(?:Lot\s*No|LotNo|Serial\s*No\.?|批次号|序列号)\s*[:：]?\s*([A-Z0-9\-_/]+)", @"\b3N2\s*([A-Z0-9\-_/]+)\b", "", ""),
        new("Supplier", ["Supplier", "供应商"], @"\b(?:Supplier|供应商|制造商)\s*[:：]?\s*([^:：]+?)\s*(?=\b(?:PN|MPN|Brand|Lot\s*No|Date\s*Code|QTY)\b|$)", "", "", ""),
        new("Brand", ["Brand", "原厂", "品牌", "制造商"], @"\b(?:Brand|原厂|品牌)\s*[:：]?\s*([A-Z0-9\-_/\. ]+)", "", "", ""),
        new("MPN", ["MPN", "原厂料号", "型号","CUSTP/N"], @"\b(?:MPN|原厂料号|型号)\s*[:：]?\s*([A-Z0-9\-_/]+)", @"\b1P([A-Z0-9\-_/]+)", "", ""),
        new("PO", ["CUSTP.O.#", "PO"], @"\b(?:CUST\s*P\.?O\.?\s*#|PO)\s*[:：#]?\s*([A-Z0-9\-_/]+)", "", "", ""),
        new("HuId", ["ID", "HuId"], @"\b(?:ID|HuId)\s*[:：#]?\s*([A-Z0-9\-_/]+)", "", "", "")
    ];

    public static IReadOnlyList<FieldRule> Windows() =>
    [
        new("PartNumber", [], @"\b(?:PN|P\s*/\s*N|P\s*N|Part\s*Number|CUST\s*P\s*/\s*N)\s*[:：#/]?\s*([A-Z0-9\-_/]{8,})", @"\b1P([A-Z0-9\-_/]+)\b|\b(KEMC[0-9A-Z]+|EEHZE[0-9A-Z]+)\b", "", ""),
        new("Description", [], @"\b(FIXED\s+ALUMINUM\s+ELECTROLYTIC\s+CAPACITOR)\b", "", "", ""),
        new("Quantity", [], @"\b(?:Quantity|QIJANTITY|QUANTITY|QTY|数量)\s*[:：]?\s*((?:[0-9]\s*){3,6})(?:pcs\.)?\b", @"\b3N1[A-Z0-9\-_/]+\s+([0-9]+)\b", "", ""),
        new("DateCode", [], @"\b(?:D\s*/\s*C|Date\s*Code|DateCode|生产日期)\s*[:：]?\s*((?:[0-9]\s*){4,8})\b", "", "", "yyyy-MM-dd"),
        new("LotNo", [], @"\b(?:Lot\s*No|LotNo|Serial|序列号|批次号)\s*[:：]?\s*([A-Z0-9\-_/]{4,})", @"\b3N2\s*([A-Z0-9\-_/]+)\b", "", ""),
        new("Supplier", [], @"\b(?:Supplier|供应商|制造商)\s*[:：]?\s*([^:：]+)", "", "", ""),
        new("Brand", [], @"\b(KEMET|Panasonic)\b", "", "", ""),
        new("MPN", [], @"\b(?:MPN|型号|P\s*/\s*N)\s*[:：]?\s*([A-Z0-9\-_/]{8,})\b", @"\b1P([A-Z0-9\-_/]+)\b|\b(KEMC[0-9A-Z]+|EEHZE[0-9A-Z]+)\b", "", ""),
        new("PO", ["CUSTP.O.#", "PO"], @"\b(?:CUST\s*P\.?O\.?\s*#|PO)\s*[:：#]?\s*([A-Z0-9\-_/]+)", "", "", ""),
        new("HuId", ["ID", "HuId"], @"\b(?:ID|HuId)\s*[:：#]?\s*([A-Z0-9\-_/]+)", "", "", "")
    ];
}
