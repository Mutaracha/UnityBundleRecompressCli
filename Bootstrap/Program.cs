using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

// Bootstrap (BundleRecompressCli.exe): единственное место, где проверяется и
// обновляется AssetsTools.NET (NuGet). Затем запускает рабочий процесс
// BundleRecompressCli.Worker.exe с переданными аргументами.
//
// Только стандартная библиотека .NET Framework 4.8 (System.Web.Extensions входит в состав ОС/фреймворка).
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
    private const string StateName = ".assetstools_update_state.txt";
    private const string MutexName = @"Local\BundleRecompressCli.AssetsToolsUpdate";
    private const int CheckIntervalDays = 1;
    private const int ExitError = 10;
    private const string IndexUrl = "https://api.nuget.org/v3-flatcontainer/assetstools.net/index.json";
    private const string PackageUrl = "https://api.nuget.org/v3-flatcontainer/assetstools.net/{0}/assetstools.net.{0}.nupkg";

    private static int Main(string[] args)
    {
        // Двойной клик (нет аргументов) и перетаскивание одного файла на exe: окно нужно задержать.
        bool dragDropInfo = args.Length == 1 && !IsFlag(args[0]);
        bool pauseAtEnd = args.Length == 0 || dragDropInfo;
        try
        {
            // NuGet принимает только TLS 1.2+; на .NET Framework 4.8 это не всегда включено по умолчанию.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            bool forceUpdate = HasFlag(args, "--update");
            bool noUpdate = HasFlag(args, "--no-update");

            if (forceUpdate && noUpdate)
                throw new ArgumentException("Параметры --update и --no-update нельзя использовать одновременно.");

            // Проверка обновлений выполняется только там, где она нужна:
            //  - явный --update;
            //  - перепаковка (есть -c), если не задан --no-update (не чаще раза в сутки).
            // Справка, информация о файле (-i / перетаскивание) и двойной клик сеть не трогают.
            bool isRecompress = HasFlag(args, "-c");
            bool allowCheck = forceUpdate || (isRecompress && !noUpdate);

            string baseDir = AppContext.BaseDirectory;
            EnsureLibrary(baseDir, forceUpdate, allowCheck);

            string[] workerArgs = args.Where(a => !IsBootstrapFlag(a)).ToArray();

            // Только флаги обновления — дальше ничего не делаем.
            if (forceUpdate && workerArgs.Length == 0)
                return 0;

            string workerPath = Path.Combine(baseDir, WorkerName);
            if (!File.Exists(workerPath))
                throw new FileNotFoundException("Не найден основной файл утилиты.", workerPath);

            // Двойной клик: справка через Worker.
            if (args.Length == 0)
                workerArgs = new[] { "--help" };

            int exitCode = RunWorker(workerPath, workerArgs);

            if (pauseAtEnd)
                WaitForAnyKey();

            return exitCode;
        }
        catch (Exception ex)
        {
            WriteColored(ConsoleColor.Red, $"ОШИБКА: {ex.Message}");
            if (pauseAtEnd)
                WaitForAnyKey();
            return ExitError;
        }
    }

    private static bool IsFlag(string arg) =>
        arg.StartsWith("-", StringComparison.Ordinal) || arg.StartsWith("/", StringComparison.Ordinal);

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
    /// - DLL отсутствует — устанавливаем (ошибка фатальна).
    /// - DLL есть — проверяем обновление, если allowCheck (раз в сутки либо force). Сбой сети/NuGet не фатален.
    /// Метод синхронный намеренно: мьютекс должен освобождаться тем же потоком, который его захватил.
    /// </summary>
    private static void EnsureLibrary(string baseDir, bool force, bool allowCheck)
    {
        string dllPath = Path.Combine(baseDir, DllName);
        string statePath = Path.Combine(baseDir, StateName);
        bool present = File.Exists(dllPath);

        // Библиотека на месте, проверка не нужна — ничего не делаем и не пишем в консоль.
        if (present && !force && !allowCheck)
            return;

        using var mutex = new Mutex(false, MutexName);
        bool entered = false;
        try
        {
            entered = mutex.WaitOne(TimeSpan.FromMinutes(5));
            if (!entered)
                throw new TimeoutException("Не удалось получить блокировку обновления AssetsTools.NET.");

            // Состояние читаем под блокировкой: другой процесс мог уже обновить библиотеку.
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
                string version = GetLatestVersion();
                state.LastKnownVersion = version;
                DownloadAndInstall(version, dllPath, localHash, state);
                Console.WriteLine($"AssetsTools.NET пакет {version}, SHA-256 DLL: {Hash(dllPath)}");
            }
            catch (Exception ex) when (localHash != null)
            {
                // Библиотека уже есть: работаем на установленной версии.
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

    private static string GetLatestVersion()
    {
        string json = GetString(IndexUrl);
        var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
        var versions = ((System.Collections.IEnumerable)root["versions"])
            .Cast<object>()
            .Select(v => v?.ToString())
            .Where(v => !string.IsNullOrEmpty(v) && !v!.Contains('-'))
            .ToList();
        return versions.LastOrDefault()
               ?? throw new InvalidDataException("NuGet не вернул стабильные версии AssetsTools.NET.");
    }

    private static void DownloadAndInstall(string version, string dllPath, string? localHash, UpdateState state)
    {
        string backupPath = dllPath + ".bak";
        string tempPath = dllPath + ".tmp";

        string packageUrl = string.Format(PackageUrl, version.ToLowerInvariant());
        byte[] packageBytes = GetBytes(packageUrl);

        // Проверка целостности: NuGet публикует SHA-512 пакета (base64) рядом с ним.
        // Не совпало или не получено — пакет не устанавливаем.
        string expectedSha512 = GetString(packageUrl + ".sha512").Trim();
        string actualSha512;
        using (var sha = SHA512.Create())
            actualSha512 = Convert.ToBase64String(sha.ComputeHash(packageBytes));
        if (!string.Equals(expectedSha512, actualSha512, StringComparison.Ordinal))
            throw new InvalidDataException($"Контрольная сумма пакета AssetsTools.NET {version} не совпала с NuGet.");

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
                source.CopyTo(target);

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
            Console.WriteLine($"AssetsTools.NET обновлена до версии {version} (пакет проверен по SHA-512).");
        }
        catch
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            if (!File.Exists(dllPath) && File.Exists(backupPath)) File.Move(backupPath, dllPath);
            throw;
        }
    }

    private static string GetString(string url) => Encoding.UTF8.GetString(GetBytes(url));

    private static byte[] GetBytes(string url)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BundleRecompressCli/1.0");
        return client.GetByteArrayAsync(url).GetAwaiter().GetResult();
    }

    // Простой текстовый формат состояния (key=value), без внешних библиотек.
    private static UpdateState LoadState(string path)
    {
        var state = new UpdateState();
        try
        {
            if (!File.Exists(path)) return state;
            foreach (string line in File.ReadAllLines(path))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();
                switch (key)
                {
                    case "LastCheckUtc":
                        if (DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                            state.LastCheckUtc = dt;
                        break;
                    case "LastKnownVersion":
                        state.LastKnownVersion = value;
                        break;
                    case "InstalledDllHash":
                        state.InstalledDllHash = value;
                        break;
                }
            }
        }
        catch { }
        return state;
    }

    private static void SaveState(string path, UpdateState state)
    {
        try
        {
            File.WriteAllLines(path, new[]
            {
                "LastCheckUtc=" + state.LastCheckUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                "LastKnownVersion=" + (state.LastKnownVersion ?? ""),
                "InstalledDllHash=" + (state.InstalledDllHash ?? "")
            });
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
