using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// Bootstrap (BundleRecompressCli.exe): единственное место, где проверяется и
// обновляется AssetsTools.NET (NuGet). Затем запускает рабочий процесс
// BundleRecompressCli.Worker.exe с переданными аргументами.
//
// Флаги Bootstrap (вырезаются из аргументов, Worker их не получает):
//   --update     принудительно проверить/обновить библиотеку
//   --no-update  не проверять обновления (библиотека всё равно устанавливается, если её нет)
//
// Без аргументов (двойной клик) показывается справка Worker и ожидается клавиша.

internal static class Program
{
    private const string DllName = "AssetsTools.NET.dll";
    private const string WorkerName = "BundleRecompressCli.Worker.exe";
    private const string StateName = ".assetstools_update_state.json";
    private const string MutexName = @"Local\BundleRecompressCli.AssetsToolsUpdate";
    private const int CheckIntervalDays = 1;
    private const int ExitError = 10;
    private const string IndexUrl = "https://api.nuget.org/v3-flatcontainer/assetstools.net/index.json";
    private const string PackageUrl = "https://api.nuget.org/v3-flatcontainer/assetstools.net/{0}/assetstools.net.{0}.nupkg";

    private static int Main(string[] args)
    {
        bool interactive = args.Length == 0;
        try
        {
            bool forceUpdate = HasFlag(args, "--update");
            bool noUpdate = HasFlag(args, "--no-update");

            if (forceUpdate && noUpdate)
                throw new ArgumentException("Параметры --update и --no-update нельзя использовать одновременно.");

            string baseDir = AppContext.BaseDirectory;
            EnsureLibraryAsync(baseDir, forceUpdate, noUpdate).GetAwaiter().GetResult();

            string[] workerArgs = args.Where(a => !IsBootstrapFlag(a)).ToArray();

            // Только флаги обновления — дальше ничего не делаем.
            if (forceUpdate && workerArgs.Length == 0)
                return 0;

            string workerPath = Path.Combine(baseDir, WorkerName);
            if (!File.Exists(workerPath))
                throw new FileNotFoundException("Не найден основной файл утилиты.", workerPath);

            // Двойной клик: показываем справку через Worker; паузу делает Bootstrap.
            if (interactive)
                workerArgs = new[] { "--help" };

            int exitCode = RunWorker(workerPath, workerArgs);

            if (interactive)
                WaitForAnyKey();

            return exitCode;
        }
        catch (Exception ex)
        {
            WriteColored(ConsoleColor.Red, $"ОШИБКА: {ex.Message}");
            if (interactive)
                WaitForAnyKey();
            return ExitError;
        }
    }

    // ───────────────────────── Запуск Worker ─────────────────────────

    private static int RunWorker(string workerPath, string[] workerArgs)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = workerPath,
            UseShellExecute = false,
            CreateNoWindow = false,
            Arguments = string.Join(" ", workerArgs.Select(QuoteWindowsArgument))
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Не удалось запустить основной процесс.");
        process.WaitForExit();
        return process.ExitCode;
    }

    // ───────────────────────── Библиотека ─────────────────────────

    /// <summary>
    /// Гарантирует наличие AssetsTools.NET.dll.
    /// - Если DLL отсутствует — устанавливает её (ошибка фатальна).
    /// - Если DLL есть — проверяет обновление (не чаще раза в сутки, либо при --update).
    ///   Ошибки сети/NuGet здесь НЕ фатальны: используется установленная версия.
    /// </summary>
    private static async Task EnsureLibraryAsync(string baseDir, bool force, bool noUpdate)
    {
        string dllPath = Path.Combine(baseDir, DllName);
        string statePath = Path.Combine(baseDir, StateName);
        bool present = File.Exists(dllPath);

        // Библиотека на месте, проверка не запрошена — ничего не делаем.
        if (present && noUpdate && !force)
            return;

        using var mutex = new Mutex(false, MutexName);
        bool entered = false;
        try
        {
            entered = mutex.WaitOne(TimeSpan.FromMinutes(5));
            if (!entered)
                throw new TimeoutException("Не удалось получить блокировку обновления AssetsTools.NET.");

            // Состояние читаем уже под блокировкой: другой процесс мог обновить библиотеку.
            var state = LoadState(statePath);
            string? localHash = present ? Hash(dllPath) : null;

            bool checkDue = state.LastCheckUtc == default ||
                            DateTime.UtcNow - state.LastCheckUtc >= TimeSpan.FromDays(CheckIntervalDays);
            bool hashMatches = localHash != null &&
                               string.Equals(localHash, state.InstalledDllHash, StringComparison.OrdinalIgnoreCase);

            if (!force && localHash != null && hashMatches && !checkDue)
                return;

            Console.WriteLine("Проверка обновлений AssetsTools.NET...");
            state.LastCheckUtc = DateTime.UtcNow;

            try
            {
                string version = await GetLatestVersionAsync();
                state.LastKnownVersion = version;
                await DownloadAndInstallAsync(version, dllPath, localHash, state);
            }
            catch (Exception ex) when (localHash != null)
            {
                // Библиотека уже есть: продолжаем работу на установленной версии.
                WriteColored(ConsoleColor.Yellow,
                    $"ПРЕДУПРЕЖДЕНИЕ: не удалось проверить/обновить AssetsTools.NET: {ex.Message}");
                WriteColored(ConsoleColor.Yellow, "Используется установленная версия.");
            }

            SaveState(statePath, state);
        }
        finally
        {
            if (entered)
                mutex.ReleaseMutex();
        }
    }

    private static async Task<string> GetLatestVersionAsync()
    {
        using var client = CreateHttpClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync(IndexUrl));
        var versions = document.RootElement.GetProperty("versions").EnumerateArray()
            .Select(x => x.GetString())
            .Where(x => !string.IsNullOrEmpty(x))
            .Cast<string>()
            .Where(x => !x.Contains('-'))
            .ToList();
        return versions.LastOrDefault()
               ?? throw new InvalidDataException("NuGet не вернул стабильные версии AssetsTools.NET.");
    }

    private static async Task DownloadAndInstallAsync(string version, string dllPath, string? localHash, UpdateState state)
    {
        string backupPath = dllPath + ".bak";
        string tempPath = dllPath + ".tmp";

        byte[] packageBytes;
        using (var client = CreateHttpClient())
        {
            packageBytes = await client.GetByteArrayAsync(string.Format(PackageUrl, version.ToLowerInvariant()));
        }

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

            if (string.Equals(localHash, newHash, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(tempPath);
                state.InstalledDllHash = newHash;
                return;
            }

            if (File.Exists(backupPath)) File.Delete(backupPath);
            if (File.Exists(dllPath)) File.Move(dllPath, backupPath);
            File.Move(tempPath, dllPath);
            if (File.Exists(backupPath)) File.Delete(backupPath);

            state.InstalledDllHash = newHash;
            Console.WriteLine($"AssetsTools.NET обновлена до версии {version}.");
        }
        catch
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            if (!File.Exists(dllPath) && File.Exists(backupPath)) File.Move(backupPath, dllPath);
            throw;
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BundleRecompressCli/1.0");
        return client;
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

    private static void SaveState(string path, UpdateState state)
    {
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }

    // ───────────────────────── Аргументы и вывод ─────────────────────────

    private static bool HasFlag(string[] args, string flag) =>
        args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

    private static bool IsBootstrapFlag(string arg) =>
        arg.Equals("--update", StringComparison.OrdinalIgnoreCase) ||
        arg.Equals("--no-update", StringComparison.OrdinalIgnoreCase);

    private static void WriteColored(ConsoleColor color, string text)
    {
        var old = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ForegroundColor = old;
    }

    private static void WaitForAnyKey()
    {
        Console.WriteLine();
        Console.WriteLine("Нажмите любую клавишу для выхода...");
        Console.ReadKey(true);
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
