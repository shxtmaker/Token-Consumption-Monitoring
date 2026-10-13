using System.IO;
using System.Windows;
using TokenConsumptionMonitoring.Converters;
using TokenConsumptionMonitoring.Services;
using TokenConsumptionMonitoring.UI;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var app = new Application();
        app.Resources["ConnBrush"] = new ConnectionToBrushConverter();
        app.Resources["LevelBrush"] = new LevelToBrushConverter();
        app.Resources["Progress"] = new ProgressConverter();
        app.Resources["StrVis"] = new StringToVisibilityConverter();
        app.Resources["BoolVis"] = new BoolToVisibilityConverter();
        if (args.Length >= 3 && args[0] == "--opendesign")
            OpenDesignSmoke.Run(app, args[1], args[2], args.Contains("--verify"), new MemoryCredentials());
        else EngineSmoke.Run(app, new MemoryCredentials());
    }

    private sealed class MemoryCredentials : IPageCredentialStore
    {
        private readonly Dictionary<string, string> _secrets = new();
        public bool TryRead(string target, out string? secret) => _secrets.TryGetValue(target, out secret);
        public void Write(string target, string secret) => _secrets[target] = secret;
        public void Delete(string target) => _secrets.Remove(target);
    }
}
