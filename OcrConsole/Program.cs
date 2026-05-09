namespace OcrConsole;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        DotEnvLoader.Load();

        var cli = CliArgs.Parse(args);
        var options = AppOptions.FromConfigAndArgs(cli);

        if (cli.RunHeadless)
        {
            return RunHeadlessAsync(options).GetAwaiter().GetResult();
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm(options));
        return 0;
    }

    private static async Task<int> RunHeadlessAsync(AppOptions options)
    {
        var store = new LocalDbStore(options.LocalDbConnectionString);
        store.EnsureDatabaseAndSchema();
        store.EnsureBuiltInTemplates();

        var processor = new OcrProcessor(store);
        try
        {
            await processor.ProcessAsync(options, new Progress<string>(msg => Console.WriteLine(msg)));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }
}

internal static class DotEnvLoader
{
    public static void Load()
    {
        var baseDir = ResolveBaseDirectory();
        var loadedByDotEnv = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        LoadFile(Path.Combine(baseDir, ".env"), loadedByDotEnv, overrideDotEnvValues: false);
        LoadFile(Path.Combine(baseDir, ".env.local"), loadedByDotEnv, overrideDotEnvValues: true);
    }

    private static string ResolveBaseDirectory()
    {
        var current = new DirectoryInfo(Environment.CurrentDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "OCR.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return Environment.CurrentDirectory;
    }

    private static void LoadFile(string filePath, HashSet<string> loadedByDotEnv, bool overrideDotEnvValues)
    {
        if (!File.Exists(filePath)) return;

        foreach (var rawLine in File.ReadAllLines(filePath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var index = line.IndexOf('=');
            if (index <= 0) continue;

            var key = line[..index].Trim();
            if (key.Length == 0) continue;

            var value = line[(index + 1)..].Trim();
            value = Unquote(value);

            SetProcessEnv(key, value, loadedByDotEnv, overrideDotEnvValues);
        }
    }

    private static void SetProcessEnv(string key, string value, HashSet<string> loadedByDotEnv, bool overrideDotEnvValues)
    {
        var existing = Environment.GetEnvironmentVariable(key);
        var alreadySetByDotEnv = loadedByDotEnv.Contains(key);

        if (string.IsNullOrWhiteSpace(existing))
        {
            Environment.SetEnvironmentVariable(key, value);
            loadedByDotEnv.Add(key);
            return;
        }

        if (alreadySetByDotEnv && overrideDotEnvValues)
        {
            Environment.SetEnvironmentVariable(key, value);
        }
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2)
        {
            var first = value[0];
            var last = value[^1];
            if ((first == '"' && last == '"') || (first == '\'' && last == '\''))
            {
                return value[1..^1];
            }
        }

        return value;
    }
}
