namespace InputSwitcher;

static class Program
{
    const string MutexName = "Local\\InputSwitcher.Tray.SingleInstance";

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--self-test"))
            return Native.SelfTest();

        using var mutex = new Mutex(true, MutexName, out var created);
        if (!created)
            return 0;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
        return 0;
    }
}
