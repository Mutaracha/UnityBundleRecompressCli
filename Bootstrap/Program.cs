using System.Diagnostics;
using System.Globalization;
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
// Только стандартная библиотека .NET Framework 4.8 (System.Web.Extensions входит в состав фреймворка).
//
// Флаги Bootstrap (вырезаются из аргументов, Worker их не получает):
//   --update     принудительно проверить и при необходимости обновить библиотеку
//   --no-update  не ходить в сеть (библиотека всё равно устанавливается, если её нет)
//
// Проверка обновлений (сеть) выполняется:
//   - при --update (принудительно);
//   - при перепаковке (есть -c), не чаще раза в сутки, если не задан --no-update.
// Справка, информация о файле (-i / перетаскивание) и двойной клик сеть не используют.
//
// Состояние (.assetstools_update_state.txt, key=value):
//   LastCheckUtc          время последней успешной проверки
//   InstalledVersion      установленная версия пакета NuGet
//   InstalledPackageSha512 SHA-512 пакета, сверенный с NuGet при установке (base64)
//   InstalledDllSha256    SHA-256 установленной DLL (обнаруживает ручную замену файла)

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
    // Метаданные версии (включая packageHash SHA-512) отдаёт NuGet registration API.
    private const string RegistrationUrl = "https://api.nuget.org/v3/registration5-semver1/assetstools.net/{0}.json";

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
    /// - DLL есть и allowCheck (или force) — проверяем версию по NuGet, не чаще раза в сутки.
    ///   Ошибка сети/проверки не фатальна: остаётся установленная версия, проверка повторится в следующий раз.
    /// Метод синхронный намеренно: мьютекс должен освобождаться тем же потоком, который его захватил.
    /// </summary>
    private static void EnsureLibrary(string baseDir, bool force, bool allowCheck)
    {
        string dllPath = Path.Combine(baseDir, DllName);
        string statePath = Path.Combine(baseDir, StateName);
        bool present = File.Exists(dllPath);

        // Без сети: библиотека на месте — ничего не делаем и не пишем в консоль.
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
            bool localIntact = localHash != null &&
                               string.Equals(localHash, state.InstalledDllSha256, StringComparison.OrdinalIgnoreCase);

            bool checkDue = state.LastCheckUtc == default ||
                            DateTime.UtcNow - state.LastCheckUtc >= TimeSpan.FromDays(CheckIntervalDays);

            // Суточное правило: библиотека на месте, сохранена как установленная, проверка не due.
            if (!force && localIntact && !checkDue)
                return;

            Console.WriteLine("Проверка обновлений AssetsTools.NET...");
            try
            {
                string latest = GetLatestVersion();

                if (localIntact && string.Equals(latest, state.InstalledVersion, StringComparison.Ordinal))
                {
                    Console.WriteLine($"AssetsTools.NET {latest} — актуальная версия.");
                }
                else
                {
                    Console.WriteLine(InstallVersion(latest, dllPath, localHash, state));
                }
                Console.WriteLine($"  SHA-256 DLL: {Hash(dllPath)}");

                state.LastCheckUtc = DateTime.UtcNow;
                SaveState(statePath, state);
            }
            catch (Exception ex) when (localHash != null)
            {
                // Библиотека уже есть: работаем на установленной версии, проверка повторится позже.
                WriteColored(ConsoleColor.Yellow,
                    $"ПРЕДУПРЕЖДЕНИЕ: не удалось проверить/обновить AssetsTools.NET: {ex.Message}");
                WriteColored(ConsoleColor.Yellow, "Используется установленная версия.");
            }
        }
        finally
        {
            if (entered)
                mutex.ReleaseMutex();
        }
    }

    /// <summary>
    /// Скачивает пакет версии, сверяет SHA-512 с packageHash из NuGet, устанавливает DLL и обновляет состояние.
    /// При любой ошибке старая DLL остаётся на месте.
    /// </summary>
    /// <summary>Возвращает текст результата для вывода в консоль.</summary>
    private static string InstallVersion(string version, string dllPath, string? localHash, UpdateState state)
    {
        string backupPath = dllPath + ".bak";
        string tempPath = dllPath + ".tmp";

        var meta = GetPackageMeta(version);

        byte[] packageBytes = GetBytes(string.Format(PackageUrl, version.ToLowerInvariant()));
        string actualSha512;
        using (var sha = SHA512.Create())
            actualSha512 = Convert.ToBase64String(sha.ComputeHash(packageBytes));

        if (!string.Equals(meta.PackageHash, actualSha512, StringComparison.Ordinal))
            throw new InvalidDataException($"SHA-512 пакета AssetsTools.NET {version} не совпадает с NuGet. Установка отменена.");

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
            string previousVersion = state.InstalledVersion ?? "неизвестна";
            string message;

            if (localHash != null && string.Equals(localHash, newHash, StringComparison.OrdinalIgnoreCase))
            {
                // Файл на диске уже совпадает с пакетом NuGet — заменять нечего.
                File.Delete(tempPath);
                message = $"AssetsTools.NET {version} — актуальная версия (файл совпадает с NuGet).";
            }
            else
            {
                if (File.Exists(backupPath)) File.Delete(backupPath);
                if (File.Exists(dllPath)) File.Move(dllPath, backupPath);
                File.Move(tempPath, dllPath);
                if (File.Exists(backupPath)) File.Delete(backupPath);

                if (localHash == null)
                    message = $"AssetsTools.NET загружена: версия {version}.";
                else if (state.InstalledVersion == null)
                    message = $"AssetsTools.NET заменена на версию {version} (версия прежней DLL неизвестна, файла состояния не было).";
                else if (string.Equals(previousVersion, version, StringComparison.Ordinal))
                    message = $"AssetsTools.NET восстановлена: версия {version} (файл был изменён вне утилиты).";
                else
                    message = $"AssetsTools.NET обновлена: {previousVersion} -> {version}.";
            }

            state.InstalledVersion = version;
            state.InstalledPackageSha512 = actualSha512;
            state.InstalledDllSha256 = newHash;
            return message + "\n  пакет SHA-512 проверен с NuGet.";
        }
        catch
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            if (!File.Exists(dllPath) && File.Exists(backupPath)) File.Move(backupPath, dllPath);
            throw;
        }
    }

    private static string GetLatestVersion()
    {
        var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(GetString(IndexUrl));
        var versions = ((System.Collections.IEnumerable)root["versions"])
            .Cast<object>()
            .Select(v => v?.ToString())
            .Where(v => !string.IsNullOrEmpty(v) && !v!.Contains('-'))
            .ToList();
        return versions.LastOrDefault()
               ?? throw new InvalidDataException("NuGet не вернул стабильные версии AssetsTools.NET.");
    }

    private static (string PackageHash, long PackageSize) GetPackageMeta(string version)
    {
        var registration = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(
            GetString(string.Format(RegistrationUrl, version.ToLowerInvariant())));
        string catalogUrl = registration["catalogEntry"].ToString();

        var catalog = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(GetString(catalogUrl));
        if (!string.Equals(catalog["packageHashAlgorithm"]?.ToString(), "SHA512", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("NuGet вернул неизвестный алгоритм хеша пакета.");

        string hash = catalog["packageHash"].ToString();
        long size = Convert.ToInt64(catalog["packageSize"]);
        return (hash, size);
    }

    private static string GetString(string url) => Encoding.UTF8.GetString(GetBytes(url));

    private static byte[] GetBytes(string url)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BundleRecompressCli/1.0");
        return client.GetByteArrayAsync(url).GetAwaiter().GetResult();
    }

    // Текстовый формат состояния (key=value), без внешних библиотек.
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
                        if (DateTime.TryParse(value, CultureInfo.InvariantCulture,
                                DateTimeStyles.RoundtripKind, out var dt))
                            state.LastCheckUtc = dt;
                        break;
                    case "InstalledVersion":
                        state.InstalledVersion = value;
                        break;
                    case "InstalledPackageSha512":
                        state.InstalledPackageSha512 = value;
                        break;
                    case "InstalledDllSha256":
                        state.InstalledDllSha256 = value;
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
                "LastCheckUtc=" + state.LastCheckUtc.ToString("o", CultureInfo.InvariantCulture),
                "InstalledVersion=" + (state.InstalledVersion ?? ""),
                "InstalledPackageSha512=" + (state.InstalledPackageSha512 ?? ""),
                "InstalledDllSha256=" + (state.InstalledDllSha256 ?? "")
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

    private static bool IsFlag(string arg) =>
        arg.StartsWith("-", StringComparison.Ordinal) || arg.StartsWith("/", StringComparison.Ordinal);

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
        public string? InstalledVersion { get; set; }
        public string? InstalledPackageSha512 { get; set; }
        public string? InstalledDllSha256 { get; set; }
    }
}
