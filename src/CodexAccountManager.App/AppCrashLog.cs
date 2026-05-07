using System.Text;

namespace CodexAccountManager.App;

internal static class AppCrashLog
{
    public static void Write(Exception exception)
    {
        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CodexAccountManager",
                "logs");
            Directory.CreateDirectory(root);

            var path = Path.Combine(root, "app-crash.log");
            var builder = new StringBuilder();
            builder.AppendLine(DateTimeOffset.Now.ToString("O"));
            builder.AppendLine(exception.ToString());
            builder.AppendLine();

            File.AppendAllText(path, builder.ToString());
        }
        catch
        {
            // Last-resort logging must never become a second crash source.
        }
    }
}
