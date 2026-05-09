using System.Configuration;
using System.Data;
using System.Text;

namespace OcrConsole;

internal sealed class MainForm : Form
{
    private readonly AppOptions _baseOptions;
    private readonly LocalDbStore _store;

    private readonly TextBox _inputBox = new();
    private readonly TextBox _outputBox = new();
    private readonly ComboBox _providerBox = new();
    private readonly ComboBox _barcodeProviderBox = new();
    private readonly ComboBox _aiProviderBox = new();
    private readonly ComboBox _logVerbosityBox = new();
    private readonly ComboBox _templateBox = new();
    private readonly DataGridView _fieldGrid = new();
    private readonly TextBox _logBox = new();
    private readonly Button _runButton = new();
    private readonly Button _pauseButton = new();
    private readonly Button _saveButton = new();
    private readonly ProcessingControl _processingControl = new();
    private CancellationTokenSource? _runCts;
    private bool _isRunning;

    private readonly ComboBox _queryProviderBox = new();
    private readonly ComboBox _queryAiProviderBox = new();
    private readonly TextBox _queryKeywordBox = new();
    private readonly DateTimePicker _queryFromPicker = new();
    private readonly DateTimePicker _queryToPicker = new();
    private readonly DataGridView _queryGrid = new();
    private readonly TextBox _queryFieldsBox = new();
    private readonly TextBox _queryDetailBox = new();

    public MainForm(AppOptions options)
    {
        _baseOptions = options;
        _store = new LocalDbStore(options.LocalDbConnectionString);
        _store.EnsureDatabaseAndSchema();
        _store.EnsureBuiltInTemplates();

        Text = "OCR 执行与管理";
        Width = 1300;
        Height = 860;
        StartPosition = FormStartPosition.CenterScreen;

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(new TabPage("执行") { Padding = new Padding(10) });
        tabs.TabPages.Add(new TabPage("配置") { Padding = new Padding(10) });
        tabs.TabPages.Add(new TabPage("查询") { Padding = new Padding(10) });

        tabs.TabPages[0].Controls.Add(BuildExecuteTab(options));
        tabs.TabPages[1].Controls.Add(BuildConfigTab(options));
        tabs.TabPages[2].Controls.Add(BuildQueryTab());

        Controls.Add(tabs);
        LoadTemplateNames(options.Provider.ToString());
        QueryHistory();
    }

    // ── 执行页 ───────────────────────────────────────────────────────────────

    private Control BuildExecuteTab(AppOptions options)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 138));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 4 };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
        top.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        top.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        top.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        top.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));

        AddRow(top, 0, "输入目录", _inputBox, options.InputDirectory, "输出目录", _outputBox, options.OutputDirectory);

        _providerBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _providerBox.Items.AddRange(new object[] { "Aliyun", "Windows", "Paddle" });
        _providerBox.SelectedItem = options.Provider.ToString();

        _barcodeProviderBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _barcodeProviderBox.Items.AddRange(new object[] { "ZXing", "WechatQrCode" });
        _barcodeProviderBox.SelectedItem = options.BarcodeProvider.ToString();

        _aiProviderBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _aiProviderBox.Items.AddRange(new object[] { "ollama", "openvino", "bailian", "mock" });
        var configuredAiProvider = (ConfigurationManager.AppSettings["AiProvider"] ?? "ollama").Trim();
        var matchedAiProvider = _aiProviderBox.Items.Cast<object>()
            .Select(x => x?.ToString() ?? string.Empty)
            .FirstOrDefault(x => string.Equals(x, configuredAiProvider, StringComparison.OrdinalIgnoreCase));
        _aiProviderBox.SelectedItem = matchedAiProvider ?? "ollama";

        _logVerbosityBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _logVerbosityBox.Items.AddRange(new object[] { "简洁", "详细" });
        _logVerbosityBox.SelectedItem = options.LogVerbosity == LogVerbosity.Concise ? "简洁" : "详细";

        AddRow(top, 1, "OCR 提供者", _providerBox, string.Empty, "条码提供者", _barcodeProviderBox, string.Empty);
        AddRow(top, 2, "AI 提供者", _aiProviderBox, string.Empty, "日志级别", _logVerbosityBox, string.Empty);

        var actionPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };

        _runButton.Text = "开始识别";
        _runButton.Width = 120;
        _runButton.Click += async (_, _) => await RunAsync();
        actionPanel.Controls.Add(_runButton);

        _pauseButton.Text = "暂停";
        _pauseButton.Width = 120;
        _pauseButton.Enabled = false;
        _pauseButton.Click += (_, _) => TogglePause();
        actionPanel.Controls.Add(_pauseButton);

        _saveButton.Text = "保存配置";
        _saveButton.Width = 120;
        _saveButton.Click += (_, _) => SaveConfigOnly();
        actionPanel.Controls.Add(_saveButton);

        var host = new Panel { Dock = DockStyle.Fill };
        host.Controls.Add(actionPanel);
        actionPanel.Dock = DockStyle.Fill;
        top.Controls.Add(host, 0, 3);
        top.SetColumnSpan(host, 4);

        panel.Controls.Add(top, 0, 0);
        panel.Controls.Add(new Label { Text = "运行日志", Dock = DockStyle.Fill }, 0, 1);

        _logBox.Dock = DockStyle.Fill;
        _logBox.Multiline = true;
        _logBox.ScrollBars = ScrollBars.Both;
        _logBox.ReadOnly = true;
        _logBox.WordWrap = true;
        _logBox.MaxLength = 0;
        panel.Controls.Add(_logBox, 0, 2);

        return panel;
    }

    // ── 配置页 ───────────────────────────────────────────────────────────────

    private Control BuildConfigTab(AppOptions options)
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // 模板行: 标签 | 下拉框 | 保存按钮
        var row0 = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
        row0.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        row0.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row0.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));

        row0.Controls.Add(new Label { Text = "模板", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);

        _templateBox.Dock = DockStyle.Fill;
        _templateBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _templateBox.SelectedIndexChanged += (_, _) =>
        {
            LoadSelectedTemplateToGrid();
            UpdateFieldGridColumns();
        };
        row0.Controls.Add(_templateBox, 1, 0);

        var saveTemplateButton = new Button { Text = "保存当前字段为模板", Dock = DockStyle.Fill };
        saveTemplateButton.Click += (_, _) => SaveCurrentTemplate();
        row0.Controls.Add(saveTemplateButton, 2, 0);

        root.Controls.Add(row0, 0, 0);
        root.Controls.Add(BuildFieldPanel(options), 0, 1);
        return root;
    }

    private Control BuildFieldPanel(AppOptions options)
    {
        var container = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        container.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        container.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var tip = new Label
        {
            Dock = DockStyle.Fill,
            Text = "字段规则支持: Aliyun 键映射、文本正则、条码正则、模板拼接、日期格式(yyyy-MM-dd)。DateCode 4/6/8 位会自动标准化。",
            AutoSize = false
        };

        _fieldGrid.Dock = DockStyle.Fill;
        _fieldGrid.AllowUserToAddRows = false;
        _fieldGrid.AllowUserToDeleteRows = false;
        _fieldGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;

        _fieldGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Field", HeaderText = "输出字段", ReadOnly = true });
        _fieldGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "AliKeys", HeaderText = "Ali键(逗号分隔)" });
        _fieldGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "TextRegex", HeaderText = "文本正则(组1)" });
        _fieldGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "BarcodeRegex", HeaderText = "条码正则(组1)" });
        _fieldGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Template", HeaderText = "模板(支持{Field})" });
        _fieldGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "DateFormat", HeaderText = "日期格式" });

        FillGrid(options.FieldRules);

        container.Controls.Add(tip, 0, 0);
        container.Controls.Add(_fieldGrid, 0, 1);
        return container;
    }

    // ── 查询页 ───────────────────────────────────────────────────────────────

    private Control BuildQueryTab()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var filter = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
        filter.Controls.Add(new Label { Text = "OCR Provider", Width = 90, TextAlign = ContentAlignment.MiddleLeft });
        _queryProviderBox.Width = 110;
        _queryProviderBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _queryProviderBox.Items.AddRange(new object[] { "All", "Aliyun", "Windows", "Paddle" });
        _queryProviderBox.SelectedIndex = 0;
        filter.Controls.Add(_queryProviderBox);

        filter.Controls.Add(new Label { Text = "AI Provider", Width = 80, TextAlign = ContentAlignment.MiddleLeft });
        _queryAiProviderBox.Width = 110;
        _queryAiProviderBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _queryAiProviderBox.Items.AddRange(new object[] { "All", "ollama", "openvino", "bailian", "mock", "None" });
        _queryAiProviderBox.SelectedIndex = 0;
        filter.Controls.Add(_queryAiProviderBox);

        filter.Controls.Add(new Label { Text = "关键字", Width = 55, TextAlign = ContentAlignment.MiddleLeft });
        _queryKeywordBox.Width = 220;
        filter.Controls.Add(_queryKeywordBox);

        filter.Controls.Add(new Label { Text = "From", Width = 40, TextAlign = ContentAlignment.MiddleLeft });
        _queryFromPicker.Width = 150;
        _queryFromPicker.Value = DateTime.Today.AddDays(-7);
        filter.Controls.Add(_queryFromPicker);

        filter.Controls.Add(new Label { Text = "To", Width = 26, TextAlign = ContentAlignment.MiddleLeft });
        _queryToPicker.Width = 150;
        _queryToPicker.Value = DateTime.Today.AddDays(1).AddTicks(-1);
        filter.Controls.Add(_queryToPicker);

        var search = new Button { Text = "查询", Width = 90 };
        search.Click += (_, _) => QueryHistory();
        filter.Controls.Add(search);

        _queryGrid.Dock = DockStyle.Fill;
        _queryGrid.ReadOnly = true;
        _queryGrid.AllowUserToAddRows = false;
        _queryGrid.AllowUserToDeleteRows = false;
        _queryGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _queryGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _queryGrid.MultiSelect = false;
        _queryGrid.DataBindingComplete += (_, _) =>
        {
            ApplyQueryGridDisplayColumns(_queryGrid, fixIdColumnWidth: true);
        };
        _queryGrid.SelectionChanged += (_, _) => LoadHistoryDetail();

        _queryFieldsBox.Dock = DockStyle.Fill;
        _queryFieldsBox.Multiline = true;
        _queryFieldsBox.ScrollBars = ScrollBars.Both;
        _queryFieldsBox.ReadOnly = true;
        _queryFieldsBox.WordWrap = false;

        _queryDetailBox.Dock = DockStyle.Fill;
        _queryDetailBox.Multiline = true;
        _queryDetailBox.ScrollBars = ScrollBars.Both;
        _queryDetailBox.ReadOnly = true;

        root.Controls.Add(filter, 0, 0);

        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        content.Controls.Add(_queryGrid, 0, 0);

        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.Controls.Add(new Label { Text = "OcrResultFields", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        right.Controls.Add(_queryFieldsBox, 0, 1);
        right.Controls.Add(new Label { Text = "详情", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 2);
        right.Controls.Add(_queryDetailBox, 0, 3);

        content.Controls.Add(right, 1, 0);

        root.Controls.Add(content, 0, 1);

        return root;
    }

    // ── 辅助方法 ─────────────────────────────────────────────────────────────

    private static void AddRow(
        TableLayoutPanel panel,
        int row,
        string? leftLabel,
        Control? leftControl,
        string? leftText,
        string? rightLabel,
        Control? rightControl,
        string? rightText)
    {
        if (!string.IsNullOrWhiteSpace(leftLabel) && leftControl is not null)
        {
            panel.Controls.Add(new Label { Text = leftLabel, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
            leftControl.Dock = DockStyle.Fill;
            if (leftControl is TextBox leftBox) leftBox.Text = leftText ?? string.Empty;
            panel.Controls.Add(leftControl, 1, row);
        }

        if (!string.IsNullOrWhiteSpace(rightLabel) && rightControl is not null)
        {
            panel.Controls.Add(new Label { Text = rightLabel, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 2, row);
            rightControl.Dock = DockStyle.Fill;
            if (rightControl is TextBox rightBox) rightBox.Text = rightText ?? string.Empty;
            panel.Controls.Add(rightControl, 3, row);
        }
    }

    private void FillGrid(IReadOnlyList<FieldRule> rules)
    {
        _fieldGrid.Rows.Clear();

        var map = rules.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var name in GetDefaultFieldOrder())
        {
            if (map.TryGetValue(name, out var rule))
            {
                _fieldGrid.Rows.Add(rule.Name, string.Join(',', rule.AliKeys), rule.TextRegex, rule.BarcodeRegex, rule.Template, rule.DateFormat);
            }
            else
            {
                _fieldGrid.Rows.Add(name, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
            }
        }
    }

    private IReadOnlyList<FieldRule> ReadRulesFromGrid()
    {
        var rules = new List<FieldRule>();
        foreach (DataGridViewRow row in _fieldGrid.Rows)
        {
            rules.Add(new FieldRule(
                Name: row.Cells["Field"].Value?.ToString()?.Trim() ?? string.Empty,
                AliKeys: SplitCsv(row.Cells["AliKeys"].Value?.ToString()),
                TextRegex: row.Cells["TextRegex"].Value?.ToString() ?? string.Empty,
                BarcodeRegex: row.Cells["BarcodeRegex"].Value?.ToString() ?? string.Empty,
                Template: row.Cells["Template"].Value?.ToString() ?? string.Empty,
                DateFormat: row.Cells["DateFormat"].Value?.ToString() ?? string.Empty));
        }

        return rules;
    }

    private AppOptions BuildOptionsFromUi()
    {
        var provider = Enum.TryParse<OcrProvider>(_providerBox.SelectedItem?.ToString(), true, out var p) ? p : OcrProvider.Aliyun;
        var barcodeProvider = Enum.TryParse<BarcodeProvider>(_barcodeProviderBox.SelectedItem?.ToString(), true, out var b) ? b : BarcodeProvider.ZXing;
        var logVerbosity = string.Equals(_logVerbosityBox.SelectedItem?.ToString(), "简洁", StringComparison.OrdinalIgnoreCase)
            ? LogVerbosity.Concise
            : LogVerbosity.Detailed;
        var selectedTemplate = _templateBox.SelectedItem?.ToString() ?? _baseOptions.TemplateName;

        return _baseOptions with
        {
            InputDirectory = _inputBox.Text.Trim(),
            OutputDirectory = _outputBox.Text.Trim(),
            Provider = provider,
            BarcodeProvider = barcodeProvider,
            LogVerbosity = logVerbosity,
            LanguageTag = _baseOptions.LanguageTag,
            AliEndpoint = _baseOptions.AliEndpoint,
            TemplateName = selectedTemplate,
            FieldRules = ReadRulesFromGrid()
        };
    }

    // ── 操作处理 ─────────────────────────────────────────────────────────────

    private async Task RunAsync()
    {
        if (_isRunning) return;

        var selectedAiProvider = _aiProviderBox.SelectedItem?.ToString() ?? "ollama";
        if (string.Equals(selectedAiProvider, "bailian", StringComparison.OrdinalIgnoreCase))
        {
            var bailianApiKey = SettingHelper.GetSettingOrEnv("BailianApiKey", "OCR_BAILIAN_API_KEY") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(bailianApiKey))
            {
                MessageBox.Show(
                    this,
                    "当前已选择 AI Provider=bailian，但 BailianApiKey 为空。请先在 App.config 中配置 BailianApiKey。",
                    "配置缺失",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }
        }

        _isRunning = true;
        _runButton.Enabled = false;
        _pauseButton.Enabled = true;
        _pauseButton.Text = "暂停";
        _processingControl.Resume();
        _runCts = new CancellationTokenSource();
        _logBox.Clear();
        try
        {
            var options = BuildOptionsFromUi();
            var processor = new OcrProcessor(
                _store,
                AiResolverFactory.CreateFromConfig(selectedAiProvider),
                selectedAiProvider);
            await processor.ProcessAsync(options, new Progress<string>(AppendLog), _processingControl, _runCts.Token);
            MessageBox.Show(this, "识别完成。", "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (OperationCanceledException)
        {
            AppendLog("识别已停止。");
        }
        catch (Exception ex)
        {
            AppendLog(ex.Message);
            MessageBox.Show(this, ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _runCts?.Dispose();
            _runCts = null;
            _isRunning = false;
            _runButton.Enabled = true;
            _pauseButton.Enabled = false;
            _pauseButton.Text = "暂停";
            _processingControl.Resume();
        }
    }

    private void TogglePause()
    {
        if (!_isRunning) return;

        if (_processingControl.IsPaused)
        {
            _processingControl.Resume();
            _pauseButton.Text = "暂停";
            AppendLog("继续识别。");
            return;
        }

        _processingControl.Pause();
        _pauseButton.Text = "继续";
        AppendLog("已暂停，等待继续。");
    }

    private void SaveConfigOnly()
    {
        var cfg = BuildOptionsFromUi();
        var exeConfig = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None);
        var app = exeConfig.AppSettings.Settings;

        SetAppSetting(app, "InputDirectory", cfg.InputDirectory);
        SetAppSetting(app, "OutputDirectory", cfg.OutputDirectory);
        SetAppSetting(app, "OcrProvider", cfg.Provider.ToString());
        SetAppSetting(app, "BarcodeProvider", cfg.BarcodeProvider.ToString());
        SetAppSetting(app, "LogVerbosity", cfg.LogVerbosity.ToString());
        SetAppSetting(app, "OcrTemplateName", cfg.TemplateName);
        SetAppSetting(app, "AiProvider", _aiProviderBox.SelectedItem?.ToString() ?? "ollama");

        exeConfig.Save(ConfigurationSaveMode.Modified);
        ConfigurationManager.RefreshSection("appSettings");

        MessageBox.Show(this, "已保存基础配置到 App.config。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void SaveCurrentTemplate()
    {
        var name = _templateBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show(this, "请输入模板名或先选择模板。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var rules = ReadRulesFromGrid();
        _store.UpsertTemplate(name, rules);
        LoadTemplateNames(name);
        MessageBox.Show(this, $"模板已保存: {name}", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void LoadTemplateNames(string? selectName = null)
    {
        var names = new List<string> { "Aliyun", "Windows", "Paddle" };
        _templateBox.Items.Clear();
        foreach (var name in names) _templateBox.Items.Add(name);

        var target = !string.IsNullOrWhiteSpace(selectName) && names.Contains(selectName)
            ? selectName
            : names.FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(target))
            _templateBox.SelectedItem = target;
    }

    private void LoadSelectedTemplateToGrid()
    {
        var name = _templateBox.SelectedItem?.ToString();
        if (string.IsNullOrWhiteSpace(name)) return;

        var rules = _store.GetTemplateRules(name);
        if (rules.Count == 0) return;
        FillGrid(rules);
    }

    /// <summary>
    /// 根据当前选中的模板切换字段列可见性：
    /// Aliyun 模式显示 Ali键列；非 Aliyun 模式显示正则列。
    /// </summary>
    private void UpdateFieldGridColumns()
    {
        var template = _templateBox.SelectedItem?.ToString();
        var isAliyun = string.Equals(template, "Aliyun", StringComparison.OrdinalIgnoreCase);
        var useRegexRules = !isAliyun;

        if (_fieldGrid.Columns.Contains("AliKeys"))
            _fieldGrid.Columns["AliKeys"]!.Visible = isAliyun;
        if (_fieldGrid.Columns.Contains("TextRegex"))
            _fieldGrid.Columns["TextRegex"]!.Visible = useRegexRules;
        if (_fieldGrid.Columns.Contains("BarcodeRegex"))
            _fieldGrid.Columns["BarcodeRegex"]!.Visible = useRegexRules;
    }

    private void QueryHistory()
    {
        var provider = _queryProviderBox.SelectedItem?.ToString();
        var aiProvider = _queryAiProviderBox.SelectedItem?.ToString();
        var keyword = _queryKeywordBox.Text.Trim();
        var from = _queryFromPicker.Value;
        var to = _queryToPicker.Value;

        var table = _store.QueryHistory(provider, aiProvider, keyword, from, to);
        _queryGrid.DataSource = table;
        LoadHistoryDetail();
    }

    private void LoadHistoryDetail()
    {
        if (_queryGrid.CurrentRow?.DataBoundItem is not DataRowView row)
        {
            _queryFieldsBox.Clear();
            _queryDetailBox.Clear();
            return;
        }

        var fields = row.Row["Fields"]?.ToString();
        var error = row.Row["ErrorMessage"]?.ToString();

        var idText = row.Row["Id"]?.ToString();
        long.TryParse(idText, out var resultId);
        var detail = resultId > 0
            ? _store.QueryHistoryDetail(resultId)
            : (AliRawJson: (string?)null, BarcodeJson: (string?)null, RawJson: (string?)null);

        var aliRaw = JsonDisplayHelper.FormatForDisplay(detail.AliRawJson);
        var barcode = JsonDisplayHelper.FormatForDisplay(detail.BarcodeJson);

        _queryFieldsBox.Text = FormatFieldsForPanel(fields);
        _queryDetailBox.Text = BuildDetailText(row, barcode, aliRaw, error);
    }

    private static void ApplyQueryGridDisplayColumns(DataGridView grid, bool fixIdColumnWidth)
    {
        if (grid.Columns.Contains("Fields")) grid.Columns["Fields"]!.Visible = false;

        if (grid.Columns.Contains("CreatedAt"))
        {
            var createdAt = grid.Columns["CreatedAt"]!;
            createdAt.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
            createdAt.Width = 155;
            createdAt.DefaultCellStyle.Format = "yyyy-MM-dd HH:mm:ss";
        }

        if (fixIdColumnWidth && grid.Columns.Contains("Id"))
        {
            var id = grid.Columns["Id"]!;
            id.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
            id.Width = 85;
        }
    }

    private static string FormatFieldsForPanel(string? fieldsText)
    {
        if (string.IsNullOrWhiteSpace(fieldsText))
        {
            return "PartNumber: -\r\nMPN: -\r\nQuantity: -\r\nLotNo: -";
        }

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = fieldsText
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim());

        foreach (var line in lines)
        {
            var idx = line.IndexOf(':');
            if (idx <= 0) continue;

            var key = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim();
            if (!string.IsNullOrWhiteSpace(key))
            {
                map[key] = string.IsNullOrWhiteSpace(value) ? "-" : value;
            }
        }

        static string V(Dictionary<string, string> m, string k) => m.TryGetValue(k, out var v) ? v : "-";

        var sb = new StringBuilder();
        sb.Append($"PartNumber: {V(map, "PartNumber")} ");
        sb.Append("\r\n");
        sb.Append($"Quantity: {V(map, "Quantity")}");
        sb.Append("\r\n");
        sb.Append($"DateCode: {V(map, "DateCode")}");
        sb.Append("\r\n");
        sb.Append($"LotNo: {V(map, "LotNo")}");
        return sb.ToString();
    }

    private static string BuildDetailText(DataRowView row, string barcode, string aliRaw, string? error)
    {
        return
            $"文件: {row.Row["FileName"]}{Environment.NewLine}" +
            $"时间: {row.Row["CreatedAt"]}{Environment.NewLine}" +
            $"Provider: {row.Row["OcrProvider"]}{Environment.NewLine}" +
            $"AI Provider: {row.Row["AiProvider"]}{Environment.NewLine}" +
            $"Error: {error}{Environment.NewLine}{Environment.NewLine}" +
            $"Barcodes:{Environment.NewLine}{barcode}{Environment.NewLine}{Environment.NewLine}" +
            $"OCR Raw:{Environment.NewLine}{aliRaw}";
    }

    private static void SetAppSetting(KeyValueConfigurationCollection app, string key, string value)
    {
        if (app[key] is null)
            app.Add(key, value);
        else
            app[key]!.Value = value;
    }

    private void AppendLog(string message)
    {
        if (InvokeRequired)
        {
            Invoke(new Action(() => AppendLog(message)));
            return;
        }

        if (_logBox.IsDisposed) return;
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
        _logBox.AppendText(line);
    }

    private static string[] SplitCsv(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();

    private static IReadOnlyList<string> GetDefaultFieldOrder()
    {
        return ["PartNumber", "Description", "Quantity", "DateCode", "LotNo", "Supplier", "Brand", "MPN", "PO", "HuId"];
    }
}
