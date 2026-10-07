using System.Globalization;
using System.Text;
using NLog;
using NLog.Config;
using NLog.Targets;

namespace VehicleVision.PleasanterTools.IndexCreator;

public static class RuntimeLog
{
    private static Logger? logger;
    public static string FileName(DateTime started) => "VehicleVision.PleasanterTools.IndexCreator_" + started.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".log";
    public static void Initialize()
    {
        var folder = Path.Combine(Environment.CurrentDirectory, "logs");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, FileName(DateTime.Now));
        using (File.Open(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { }
        var config = new LoggingConfiguration();
        var target = new FileTarget("execution") { FileName = path, Layout = "${message}", Encoding = new UTF8Encoding(false), KeepFileOpen = false };
        config.AddRule(LogLevel.Info, LogLevel.Fatal, target);
        LogManager.ThrowExceptions = true;
        LogManager.Configuration = config;
        logger = LogManager.GetLogger("IndexCreator");
        logger.Info("IndexCreator execution started.");
    }
    public static void WriteLine(string message)
    {
        logger?.Info(message);
        Console.WriteLine(message);
    }
    public static void Write(string message)
    {
        logger?.Info(message);
        Console.Write(message);
    }
    public static void Error(string message)
    {
        // Report a log failure without recursively attempting another file write.
        try { logger?.Error(message); } catch (Exception) { }
        Console.Error.WriteLine(message);
    }
    public static void Shutdown()
    {
        LogManager.Shutdown();
        logger = null;
    }
}
