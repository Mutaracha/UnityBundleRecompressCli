using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class Program
{
    private const string DllName = "AssetsTools.NET.dll";
    private const string WorkerName = "BundleRecompressCli.Worker.exe";
    private const string StateName = ".assetstools_update_state.json";
    private const int CheckIntervalDays = 1;
    private const string IndexUrl = "https://api.nuget.org/v3-flatcontainer/assetstools.net/index.json";
    private const string PackageUrl = "https://api.nuget.org/v3-flatcontainer/assetstools.net/{0}/assetstools.net.{0}.nupkg";

    private static int Main(string[] args)
    {
        string baseDir = AppContext.BaseDirectory;
        try
        {
            bool force = args.Any(a => a.Equals("--update", StringComparison.OrdinalIgnoreCase));
            EnsureLibrary(baseDir, force).GetAwaiter().GetResult();

            if (force)
                return 0;

            string workerPath = Path.Combine(baseDir, WorkerName);
            if (!File.Exists(workerPath))
                throw new FileNotFoundException("Не найден основной файл утилиты.", workerPath);

            var startInfo = new ProcessStartInfo
            {
                FileName = workerPath,
                UseShellExecute = false,
                CreateNoWindow = false,
                Arguments = string.Join(" ", args.Select(QuoteWindowsArgument))
            };

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Не удалось запустить основной процесс.");
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"ОШИБКА: {ex.Message}");
            Console.ResetColor();
            return 10;
        }
    }

    private static async Task EnsureLibrary(string baseDir, bool force)
    {
        string dllPath = Path.Combine(baseDir, DllName);
        string statePath = Path.Combine(baseDir, StateName);
        string backupPath = dllPath + ".bak";

        using var mutex = new Mutex(false, @"Local\BundleRecompressCli.AssetsToolsUpdate");
        if (!mutex.WaitOne(TimeSpan.FromMinutes(5)))
            throw new TimeoutException("Не удалось получить блокировку обновления AssetsTools.NET.");

        try
        {
            var state = LoadState(statePath);
            string? localHash = File.Exists(dllPath) ? Hash(dllPath) : null;
            bool shouldCheck = force || localHash == null || state.LastCheckUtc == default ||
                               DateTime.UtcNow - state.LastCheckUtc >= TimeSpan.FromDays(CheckIntervalDays);

            if (!shouldCheck && localHash != null &&
                string.Equals(localHash, state.InstalledDllHash, StringComparison.OrdinalIgnoreCase))
                return;

            Console.WriteLine("Проверка обновлений AssetsTools.NET...");
            string version = await GetLatestVersion();
            state.LastCheckUtc = DateTime.UtcNow;
            state.LastKnownVersion = version;

            byte[] packageBytes;
            using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd("BundleRecompressCli/1.0");
                packageBytes = await client.GetByteArrayAsync(string.Format(PackageUrl, version.ToLowerInvariant()));
            }

            string tempPath = dllPath + ".tmp";
            try
            {
                using var packageStream = new MemoryStream(packageBytes);
                using var archive = new ZipArchive(packageStream, ZipArchiveMode.Read);
                var entry = archive.GetEntry($"lib/netstandard2.0/{DllName}") ??
                            archive.Entries.FirstOrDefault(e =>
                                e.Name.Equals(DllName, StringComparison.OrdinalIgnoreCase) &&
                                e.FullName.StartsWith("lib/", StringComparison.OrdinalIgnoreCase));
                if (entry == null)
                    throw new InvalidDataException($"В пакете AssetsTools.NET {version} не найден {DllName}.");

                using (var source = entry.Open())
                using (var target = File.Create(tempPath))
                    await source.CopyToAsync(target);

                string newHash = Hash(tempPath);
                state.InstalledDllHash = newHash;
                if (!string.Equals(localHash, newHash, StringComparison.OrdinalIgnoreCase))
                {
                    if (File.Exists(backupPath)) File.Delete(backupPath);
                    if (File.Exists(dllPath)) File.Move(dllPath, backupPath);
                    File.Move(tempPath, dllPath);
                    if (File.Exists(backupPath)) File.Delete(backupPath);
                    Console.WriteLine($"AssetsTools.NET обновлена до версии {version}.");
                }
                else
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
                if (!File.Exists(dllPath) && File.Exists(backupPath)) File.Move(backupPath, dllPath);
                throw;
            }

            SaveState(statePath, state);
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private static async Task<string> GetLatestVersion()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BundleRecompressCli/1.0");
        using var document = JsonDocument.Parse(await client.GetStringAsync(IndexUrl));
        var versions = document.RootElement.GetProperty("versions").EnumerateArray()
            .Select(x => x.GetString()).Where(x => !string.IsNullOrEmpty(x)).Cast<string>()
            .Where(x => !x.Contains('-')).ToList();
        return versions.LastOrDefault() ?? throw new InvalidDataException("NuGet не вернул стабильные версии AssetsTools.NET.");
    }

    private static UpdateState LoadState(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(path)) ?? new UpdateState();
        }
        catch { }
        return new UpdateState();
    }

    private static void SaveState(string path, UpdateState state) =>
        File.WriteAllText(path, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }

    private static string QuoteWindowsArgument(string value)
    {
        if (value.Length == 0) return "\"\"";
        if (!value.Any(char.IsWhiteSpace) && value.IndexOf('"') < 0) return value;
        var result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') { result.Append('\\', slashes * 2 + 1).Append('"'); }
            else result.Append('\\', slashes).Append(c);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    private sealed class UpdateState
    {
        public DateTime LastCheckUtc { get; set; }
        public string? LastKnownVersion { get; set; }
        public string? InstalledDllHash { get; set; }
    }
}
