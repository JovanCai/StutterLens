using System.Text.Json;
using System.Text.RegularExpressions;

namespace StutterLens.Core;

/// <summary>
/// 用户设置。优先保存在程序所在文件夹（便携使用，不往 C 盘写东西），
/// 那里不可写时退回到 %LOCALAPPDATA%\StutterLens。
/// </summary>
public sealed class Settings
{
    public const string App = "app", AppData = "appdata";

    /// <summary>报告位置：app（程序所在文件夹）、appdata，或自定义文件夹路径。空表示还没选过。</summary>
    public string ReportLocation { get; set; } = "";
    /// <summary>最多保留多少局的报告，超出时删除最旧的；0 表示不限。</summary>
    public int KeepReports { get; set; } = 100;

    public static string ExeDir => AppContext.BaseDirectory;
    public static string AppDataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StutterLens");
    private static string PortableFile => Path.Combine(ExeDir, "StutterLens.settings.json");
    private static string AppDataFile => Path.Combine(AppDataDir, "settings.json");

    public static string AppReportsDir => Path.Combine(ExeDir, "Reports");
    public static string AppDataReportsDir => Path.Combine(AppDataDir, "Reports");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load()
    {
        foreach (var f in new[] { PortableFile, AppDataFile })
        {
            try
            {
                if (File.Exists(f)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(f)) ?? new();
            }
            catch (Exception ex) { Log.Debug($"读取设置失败（{f}）：{ex.Message}"); }
        }
        return new();
    }

    /// <summary>保存设置，返回实际写入的文件路径。</summary>
    public string Save()
    {
        var text = JsonSerializer.Serialize(this, Json);
        try
        {
            File.WriteAllText(PortableFile, text);
            return PortableFile;
        }
        catch
        {
            Directory.CreateDirectory(AppDataDir);
            File.WriteAllText(AppDataFile, text);
            return AppDataFile;
        }
    }

    public string ReportDir => Resolve(ReportLocation);

    public static string Resolve(string location) => location.ToLowerInvariant() switch
    {
        AppData => AppDataReportsDir,
        App or "" => AppReportsDir,
        _ => Path.GetFullPath(Environment.ExpandEnvironmentVariables(location.Trim().Trim('"'))),
    };

    /// <summary>确认文件夹能创建且可写。</summary>
    public static bool TryUse(string dir, out string error)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, $".write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    // 只清理本程序生成的报告：20261112_213919_游戏名.html / .json
    private static readonly Regex ReportFile = new(@"^\d{8}_\d{6}_.+\.(html|json)$", RegexOptions.IgnoreCase);

    /// <summary>按局保留最新的 keep 份报告，返回删除的局数。</summary>
    public static int Prune(string dir, int keep)
    {
        if (keep <= 0 || !Directory.Exists(dir)) return 0;
        var sessions = Directory.EnumerateFiles(dir)
            .Where(f => ReportFile.IsMatch(Path.GetFileName(f)))
            .GroupBy(f => Path.GetFileNameWithoutExtension(f), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Key, StringComparer.Ordinal)
            .Skip(keep).ToList();
        foreach (var f in sessions.SelectMany(g => g))
        {
            try { File.Delete(f); } catch (Exception ex) { Log.Debug($"删除旧报告失败：{ex.Message}"); }
        }
        return sessions.Count;
    }
}
