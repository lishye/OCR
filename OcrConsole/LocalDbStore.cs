using System.Data;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace OcrConsole;

internal sealed class LocalDbStore
{
    private readonly string _connectionString;
    private static readonly TimeZoneInfo ChinaTimeZone = ResolveChinaTimeZone();
    private static readonly JsonSerializerOptions DbJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public LocalDbStore(string connectionString)
    {
        _connectionString = string.IsNullOrWhiteSpace(connectionString)
            ? "Data Source=(localdb)\\MSSQLLocalDB;Initial Catalog=OcrLocalDb;Integrated Security=True;TrustServerCertificate=True;"
            : connectionString;
    }

    public void EnsureDatabaseAndSchema()
    {
        var builder = new SqlConnectionStringBuilder(_connectionString);
        var dbName = builder.InitialCatalog;
        if (string.IsNullOrWhiteSpace(dbName))
            throw new InvalidOperationException("LocalDbConnectionString 必须包含 Initial Catalog。");

        var masterBuilder = new SqlConnectionStringBuilder(_connectionString) { InitialCatalog = "master" };
        using (var conn = new SqlConnection(masterBuilder.ConnectionString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"IF DB_ID('{dbName.Replace("'", "''")}') IS NULL CREATE DATABASE [{dbName}]";
            cmd.ExecuteNonQuery();
        }

        using var dbConn = new SqlConnection(_connectionString);
        dbConn.Open();

        ExecuteNonQuery(dbConn, @"
IF OBJECT_ID('dbo.RuleTemplates', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.RuleTemplates (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        TemplateName NVARCHAR(128) NOT NULL UNIQUE,
        FieldRulesJson NVARCHAR(MAX) NOT NULL,
        UpdatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
    )
END");

        ExecuteNonQuery(dbConn, @"
IF OBJECT_ID('dbo.OcrResults', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.OcrResults (
        Id BIGINT IDENTITY(1,1) PRIMARY KEY,
        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        FileName NVARCHAR(260) NULL,
        FilePath NVARCHAR(1000) NULL,
        OutputJsonPath NVARCHAR(1000) NULL,
        OcrProvider NVARCHAR(32) NULL,
        AiProvider NVARCHAR(32) NULL,
        TextContent NVARCHAR(MAX) NULL,
        FieldsJson NVARCHAR(MAX) NULL,
        AliRawJson NVARCHAR(MAX) NULL,
        BarcodeJson NVARCHAR(MAX) NULL,
        CorrectionNotesJson NVARCHAR(MAX) NULL,
        ErrorMessage NVARCHAR(MAX) NULL,
        RawJson NVARCHAR(MAX) NULL
    )
END");

        ExecuteNonQuery(dbConn, @"
IF COL_LENGTH('dbo.OcrResults', 'AiProvider') IS NULL
BEGIN
    ALTER TABLE dbo.OcrResults ADD AiProvider NVARCHAR(32) NULL
END");

        ExecuteNonQuery(dbConn, @"
IF OBJECT_ID('dbo.OcrResultFields', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.OcrResultFields (
        Id BIGINT IDENTITY(1,1) PRIMARY KEY,
        ResultId BIGINT NOT NULL,
        PartNumber NVARCHAR(MAX) NULL,
        Description NVARCHAR(MAX) NULL,
        Quantity NVARCHAR(MAX) NULL,
        DateCode NVARCHAR(MAX) NULL,
        LotNo NVARCHAR(MAX) NULL,
        Supplier NVARCHAR(MAX) NULL,
        Brand NVARCHAR(MAX) NULL,
        MPN NVARCHAR(MAX) NULL,
        PO NVARCHAR(MAX) NULL,
        HuId NVARCHAR(MAX) NULL,
        CONSTRAINT FK_OcrResultFields_OcrResults FOREIGN KEY (ResultId) REFERENCES dbo.OcrResults(Id) ON DELETE CASCADE
    );
    CREATE INDEX IX_OcrResultFields_ResultId ON dbo.OcrResultFields(ResultId);
END");

        ExecuteNonQuery(dbConn, @"
IF COL_LENGTH('dbo.OcrResultFields', 'PartNumber') IS NULL ALTER TABLE dbo.OcrResultFields ADD PartNumber NVARCHAR(MAX) NULL;
IF COL_LENGTH('dbo.OcrResultFields', 'Description') IS NULL ALTER TABLE dbo.OcrResultFields ADD Description NVARCHAR(MAX) NULL;
IF COL_LENGTH('dbo.OcrResultFields', 'Quantity') IS NULL ALTER TABLE dbo.OcrResultFields ADD Quantity NVARCHAR(MAX) NULL;
IF COL_LENGTH('dbo.OcrResultFields', 'DateCode') IS NULL ALTER TABLE dbo.OcrResultFields ADD DateCode NVARCHAR(MAX) NULL;
IF COL_LENGTH('dbo.OcrResultFields', 'LotNo') IS NULL ALTER TABLE dbo.OcrResultFields ADD LotNo NVARCHAR(MAX) NULL;
IF COL_LENGTH('dbo.OcrResultFields', 'Supplier') IS NULL ALTER TABLE dbo.OcrResultFields ADD Supplier NVARCHAR(MAX) NULL;
IF COL_LENGTH('dbo.OcrResultFields', 'Brand') IS NULL ALTER TABLE dbo.OcrResultFields ADD Brand NVARCHAR(MAX) NULL;
IF COL_LENGTH('dbo.OcrResultFields', 'MPN') IS NULL ALTER TABLE dbo.OcrResultFields ADD MPN NVARCHAR(MAX) NULL;
IF COL_LENGTH('dbo.OcrResultFields', 'PO') IS NULL ALTER TABLE dbo.OcrResultFields ADD PO NVARCHAR(MAX) NULL;
IF COL_LENGTH('dbo.OcrResultFields', 'HuId') IS NULL ALTER TABLE dbo.OcrResultFields ADD HuId NVARCHAR(MAX) NULL;
IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.OcrResultFields') AND name = 'IX_OcrResultFields_FieldName') DROP INDEX IX_OcrResultFields_FieldName ON dbo.OcrResultFields;
IF COL_LENGTH('dbo.OcrResultFields', 'FieldName') IS NOT NULL ALTER TABLE dbo.OcrResultFields DROP COLUMN FieldName;
IF COL_LENGTH('dbo.OcrResultFields', 'FieldValue') IS NOT NULL ALTER TABLE dbo.OcrResultFields DROP COLUMN FieldValue;
");
    }

    public void EnsureBuiltInTemplates()
    {
        InsertTemplateIfMissing("Aliyun", TemplateFactory.Aliyun());
        InsertTemplateIfMissing("Windows", TemplateFactory.Windows());
        InsertTemplateIfMissing("Paddle", TemplateFactory.Windows());
        InsertTemplateIfMissing("EasyOcr", TemplateFactory.Windows());
    }

    public void UpsertTemplate(string name, IReadOnlyList<FieldRule> rules)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var json = JsonSerializer.Serialize(rules, DbJsonOptions);

        using var conn = new SqlConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
MERGE dbo.RuleTemplates AS t
USING (SELECT @TemplateName AS TemplateName, @FieldRulesJson AS FieldRulesJson) AS s
ON t.TemplateName = s.TemplateName
WHEN MATCHED THEN UPDATE SET FieldRulesJson = s.FieldRulesJson, UpdatedAt = SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT (TemplateName, FieldRulesJson) VALUES (s.TemplateName, s.FieldRulesJson);";
        cmd.Parameters.AddWithValue("@TemplateName", name);
        cmd.Parameters.AddWithValue("@FieldRulesJson", json);
        cmd.ExecuteNonQuery();
    }

    private void InsertTemplateIfMissing(string name, IReadOnlyList<FieldRule> rules)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var json = JsonSerializer.Serialize(rules, DbJsonOptions);

        using var conn = new SqlConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
IF NOT EXISTS (SELECT 1 FROM dbo.RuleTemplates WHERE TemplateName = @TemplateName)
BEGIN
    INSERT INTO dbo.RuleTemplates (TemplateName, FieldRulesJson)
    VALUES (@TemplateName, @FieldRulesJson)
END";
        cmd.Parameters.AddWithValue("@TemplateName", name);
        cmd.Parameters.AddWithValue("@FieldRulesJson", json);
        cmd.ExecuteNonQuery();
    }

    public List<string> ListTemplateNames()
    {
        var list = new List<string>();
        using var conn = new SqlConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT TemplateName FROM dbo.RuleTemplates ORDER BY TemplateName";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(reader.GetString(0));
        return list;
    }

    public IReadOnlyList<FieldRule> GetTemplateRules(string name)
    {
        using var conn = new SqlConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT FieldRulesJson FROM dbo.RuleTemplates WHERE TemplateName=@TemplateName";
        cmd.Parameters.AddWithValue("@TemplateName", name);
        var json = cmd.ExecuteScalar()?.ToString();
        if (string.IsNullOrWhiteSpace(json)) return [];
        var rules = JsonSerializer.Deserialize<List<FieldRule>>(json);
        return rules ?? [];
    }

    public void SaveOcrResult(ImageRecognitionResult result, string rawJson)
    {
        using var conn = new SqlConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO dbo.OcrResults
    (FileName, FilePath, OutputJsonPath, OcrProvider, AiProvider, TextContent, FieldsJson, AliRawJson, BarcodeJson, CorrectionNotesJson, ErrorMessage, RawJson)
VALUES
    (@FileName, @FilePath, @OutputJsonPath, @OcrProvider, @AiProvider, @TextContent, @FieldsJson, @AliRawJson, @BarcodeJson, @CorrectionNotesJson, @ErrorMessage, @RawJson);
SELECT CAST(SCOPE_IDENTITY() AS BIGINT);";

        cmd.Parameters.AddWithValue("@FileName", (object?)result.FileName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@FilePath", (object?)result.FilePath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@OutputJsonPath", (object?)result.OutputJsonPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@OcrProvider", (object?)result.OcrProvider ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@AiProvider", (object?)result.AiProvider ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@TextContent", (object?)result.Text ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@FieldsJson", JsonSerializer.Serialize(result.Fields, DbJsonOptions));
        cmd.Parameters.AddWithValue("@AliRawJson", (object?)result.AliRawStructuredJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@BarcodeJson", JsonSerializer.Serialize(result.Barcodes, DbJsonOptions));
        cmd.Parameters.AddWithValue("@CorrectionNotesJson", JsonSerializer.Serialize(result.CorrectionNotes, DbJsonOptions));
        cmd.Parameters.AddWithValue("@ErrorMessage", (object?)result.Error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@RawJson", rawJson);

        var resultId = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);

        using var fieldCmd = conn.CreateCommand();
        fieldCmd.CommandText = @"
    INSERT INTO dbo.OcrResultFields (ResultId, PartNumber, Description, Quantity, DateCode, LotNo, Supplier, Brand, MPN, PO, HuId)
    VALUES (@ResultId, @PartNumber, @Description, @Quantity, @DateCode, @LotNo, @Supplier, @Brand, @MPN, @PO, @HuId)";

        fieldCmd.Parameters.Add("@ResultId", SqlDbType.BigInt);
        fieldCmd.Parameters.Add("@PartNumber", SqlDbType.NVarChar, -1);
        fieldCmd.Parameters.Add("@Description", SqlDbType.NVarChar, -1);
        fieldCmd.Parameters.Add("@Quantity", SqlDbType.NVarChar, -1);
        fieldCmd.Parameters.Add("@DateCode", SqlDbType.NVarChar, -1);
        fieldCmd.Parameters.Add("@LotNo", SqlDbType.NVarChar, -1);
        fieldCmd.Parameters.Add("@Supplier", SqlDbType.NVarChar, -1);
        fieldCmd.Parameters.Add("@Brand", SqlDbType.NVarChar, -1);
        fieldCmd.Parameters.Add("@MPN", SqlDbType.NVarChar, -1);
        fieldCmd.Parameters.Add("@PO", SqlDbType.NVarChar, -1);
        fieldCmd.Parameters.Add("@HuId", SqlDbType.NVarChar, -1);

        fieldCmd.Parameters["@ResultId"].Value = resultId;
        fieldCmd.Parameters["@PartNumber"].Value = DbOrValue(result.Fields.PartNumber);
        fieldCmd.Parameters["@Description"].Value = DbOrValue(result.Fields.Description);
        fieldCmd.Parameters["@Quantity"].Value = DbOrValue(result.Fields.Quantity);
        fieldCmd.Parameters["@DateCode"].Value = DbOrValue(result.Fields.DateCode);
        fieldCmd.Parameters["@LotNo"].Value = DbOrValue(result.Fields.LotNo);
        fieldCmd.Parameters["@Supplier"].Value = DbOrValue(result.Fields.Supplier);
        fieldCmd.Parameters["@Brand"].Value = DbOrValue(result.Fields.Brand);
        fieldCmd.Parameters["@MPN"].Value = DbOrValue(result.Fields.MPN);
        fieldCmd.Parameters["@PO"].Value = DbOrValue(result.Fields.PO);
        fieldCmd.Parameters["@HuId"].Value = DbOrValue(result.Fields.HuId);
        fieldCmd.ExecuteNonQuery();
    }

    public DataTable QueryHistory(string? provider, string? aiProvider, string keyword, DateTime from, DateTime to)
    {
        var fromUtc = ConvertChinaLocalToUtc(from);
        var toUtc = ConvertChinaLocalToUtc(to);

        using var conn = new SqlConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT TOP 500
    r.Id,
    DATEADD(HOUR, 8, r.CreatedAt) AS CreatedAt,
    r.FileName,
    r.OcrProvider,
    r.AiProvider,
    r.ErrorMessage,
    ISNULL(fa.Fields, '') AS Fields
FROM dbo.OcrResults r
OUTER APPLY (
    SELECT STRING_AGG(v.Line, CHAR(10)) AS Fields
    FROM dbo.OcrResultFields f
    CROSS APPLY (VALUES
        ('PartNumber: ' + ISNULL(NULLIF(f.PartNumber, ''), '')),
        ('Description: ' + ISNULL(NULLIF(f.Description, ''), '')),
        ('Quantity: ' + ISNULL(NULLIF(f.Quantity, ''), '')),
        ('DateCode: ' + ISNULL(NULLIF(f.DateCode, ''), '')),
        ('LotNo: ' + ISNULL(NULLIF(f.LotNo, ''), '')),
        ('Supplier: ' + ISNULL(NULLIF(f.Supplier, ''), '')),
        ('Brand: ' + ISNULL(NULLIF(f.Brand, ''), '')),
        ('MPN: ' + ISNULL(NULLIF(f.MPN, ''), '')),
        ('PO: ' + ISNULL(NULLIF(f.PO, ''), '')),
        ('HuId: ' + ISNULL(NULLIF(f.HuId, ''), ''))
    ) v(Line)
    WHERE f.ResultId = r.Id AND RIGHT(v.Line, 1) <> ':'
) fa
WHERE r.CreatedAt >= @FromUtc AND r.CreatedAt <= @ToUtc
    AND (
            @Provider = 'All'
            OR r.OcrProvider = @Provider
    )
        AND (
            @AiProvider = 'All'
            OR (@AiProvider = 'None' AND (r.AiProvider IS NULL OR r.AiProvider = ''))
            OR r.AiProvider = @AiProvider
        )
  AND (
      @Keyword = ''
      OR r.FileName LIKE '%' + @Keyword + '%'
      OR r.TextContent LIKE '%' + @Keyword + '%'
      OR EXISTS (
          SELECT 1
          FROM dbo.OcrResultFields f2
          WHERE f2.ResultId = r.Id
            AND (
                ISNULL(f2.PartNumber, '') LIKE '%' + @Keyword + '%'
                OR ISNULL(f2.Description, '') LIKE '%' + @Keyword + '%'
                OR ISNULL(f2.Quantity, '') LIKE '%' + @Keyword + '%'
                OR ISNULL(f2.DateCode, '') LIKE '%' + @Keyword + '%'
                OR ISNULL(f2.LotNo, '') LIKE '%' + @Keyword + '%'
                OR ISNULL(f2.Supplier, '') LIKE '%' + @Keyword + '%'
                OR ISNULL(f2.Brand, '') LIKE '%' + @Keyword + '%'
                OR ISNULL(f2.MPN, '') LIKE '%' + @Keyword + '%'
                OR ISNULL(f2.PO, '') LIKE '%' + @Keyword + '%'
                OR ISNULL(f2.HuId, '') LIKE '%' + @Keyword + '%'
            )
      )
  )
ORDER BY r.Id DESC";

        cmd.Parameters.AddWithValue("@FromUtc", fromUtc);
        cmd.Parameters.AddWithValue("@ToUtc", toUtc);
        cmd.Parameters.AddWithValue("@Provider", provider ?? "All");
        cmd.Parameters.AddWithValue("@AiProvider", aiProvider ?? "All");
        cmd.Parameters.AddWithValue("@Keyword", keyword ?? string.Empty);

        var table = new DataTable();
        using var adapter = new SqlDataAdapter(cmd);
        adapter.Fill(table);
        return table;
    }

    public (string? AliRawJson, string? BarcodeJson, string? RawJson) QueryHistoryDetail(long resultId)
    {
        using var conn = new SqlConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT TOP 1
    r.AliRawJson,
    r.BarcodeJson,
    r.RawJson
FROM dbo.OcrResults r
WHERE r.Id = @Id";
        cmd.Parameters.AddWithValue("@Id", resultId);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return (null, null, null);
        }

        var aliRawJson = reader.IsDBNull(0) ? null : reader.GetString(0);
        var barcodeJson = reader.IsDBNull(1) ? null : reader.GetString(1);
        var rawJson = reader.IsDBNull(2) ? null : reader.GetString(2);
        return (aliRawJson, barcodeJson, rawJson);
    }

    private static void ExecuteNonQuery(SqlConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static TimeZoneInfo ResolveChinaTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        }
        catch
        {
            return TimeZoneInfo.CreateCustomTimeZone("UTC+08", TimeSpan.FromHours(8), "UTC+08", "UTC+08");
        }
    }

    private static DateTime ConvertChinaLocalToUtc(DateTime localTime)
    {
        var unspecified = DateTime.SpecifyKind(localTime, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(unspecified, ChinaTimeZone);
    }

    private static object DbOrValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;
}
