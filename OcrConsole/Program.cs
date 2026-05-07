namespace OcrConsole;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
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
