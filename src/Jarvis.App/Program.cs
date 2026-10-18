using Jarvis.App;
using Jarvis.Core;
using System.Text;

ApplicationConfiguration.Initialize();

using var cancellationTokenSource = new CancellationTokenSource();

AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
{
    if (eventArgs.ExceptionObject is Exception exception)
    {
        StartupCrashLog.Write(exception);
    }
};

Application.ThreadException += (_, eventArgs) =>
{
    if (eventArgs.Exception is not null)
    {
        StartupCrashLog.Write(eventArgs.Exception);
    }
};

try
{
    var context = await AppBootstrapper.CreateAsync(cancellationTokenSource.Token);
    Application.Run(new JarvisMainForm(context));
}
catch (Exception exception)
{
    StartupCrashLog.Write(exception);
    MessageBox.Show(
        exception.Message,
        "Jarvis Startup Failed",
        MessageBoxButtons.OK,
        MessageBoxIcon.Error);
}

static class StartupCrashLog
{
    public static void Write(Exception exception)
    {
        try
        {
            var logPath = JarvisDataPaths.Resolve("data/startup-error.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            var builder = new StringBuilder();
            builder.Append('[')
                .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"))
                .AppendLine("]");
            builder.AppendLine(exception.ToString());
            builder.AppendLine();
            File.AppendAllText(logPath, builder.ToString());
        }
        catch
        {
            // Best effort only.
        }
    }
}
