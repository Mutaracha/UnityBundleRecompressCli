using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace BundleRecompressCli;

#region Constants & Configuration

/// Конфигурация приложения
internal static class AppConfig
{
	public const long MemoryThresholdBytes = 500L * 1024L * 1024L; // 500 MB

	// Безопасный лимит распакованных данных для режима памяти.
	// MemoryStream не может превышать int.MaxValue (~2 GB) и бросает
	// IOException "Stream was too long."; дополнительно нужно место на
	// заголовок bundle и удвоение ёмкости при росте буфера.
	public const long MemoryModeMaxDecompressedBytes = 1536L * 1024L * 1024L; // 1.5 GB

	public const string TempFileSuffix = ".tmp.decomp";
	public const int ProgressWriteThresholdPercent = 2;
	
	public const int ExitSuccess = 0;
	public const int ExitInvalidArgs = 1;
	public const int ExitFileNotFound = 2;
	public const int ExitInvalidCompression = 3;
	public const int ExitSameInputOutput = 4;
	public const int ExitException = 10;
	
	public const string RequiredRuntime = ".NET Framework 4.8";
	public const string RuntimeDownloadUrl = "https://dotnet.microsoft.com/download/dotnet-framework/net48";
	public const string WingetCommand = "winget install Microsoft.DotNet.Framework.DeveloperPack_4";
}

#endregion

#region Enums & DTOs

/// Режим работы утилиты
internal enum OperationMode
{
	Help,
	Info,
	Recompress
}

/// Результат парсинга аргументов командной строки
internal sealed class CommandLineOptions
{
	public OperationMode Mode { get; init; } = OperationMode.Help;
	public string? InputPath { get; init; }
	public string? OutputPath { get; init; }
	public AssetBundleCompressionType CompressionType { get; init; }
	public string? ProgressFilePath { get; init; }
	public bool ForceMemory { get; init; }
	public bool ForceTemp { get; init; }
	public bool DebugMode { get; init; }
	public string? ErrorMessage { get; init; }
	public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
}

/// Информация о bundle-файле
internal sealed record BundleInfo(
	string FilePath,
	long FileSize,
	string Md5Hash,
	AssetBundleCompressionType CompressionType
);

/// Результат операции перепаковки
internal sealed record RecompressResult(
	bool Success,
	bool Skipped,
	long OutputSize,
	string? ErrorMessage = null
);

#endregion

#region Argument Parser

/// Парсер аргументов командной строки
internal static class ArgumentParser
{
	private static readonly HashSet<string> HelpArgs = new(StringComparer.OrdinalIgnoreCase)
	{
		"-h", "--help", "/?"
	};

	public static CommandLineOptions Parse(string[] args)
	{
		if (args.Length == 0)
		{
			return new CommandLineOptions
			{
				Mode = OperationMode.Help
			};
		}

		if (IsHelpArg(args[0]))
		{
			return new CommandLineOptions { Mode = OperationMode.Help };
		}

		// Drag & drop одного файла → режим информации
		if (args.Length == 1 && !IsFlag(args[0]))
		{
			return new CommandLineOptions
			{
				Mode = OperationMode.Info,
				InputPath = args[0]
			};
		}

		return ParseFullArgs(args);
	}

	private static CommandLineOptions ParseFullArgs(string[] args)
	{
		bool infoMode = false;
		bool forceMemory = false;
		bool forceTemp = false;
		bool debugMode = false;
		string? progressFilePath = null;
		string? compressionModeText = null;
		var positional = new List<string>();

		for (int i = 0; i < args.Length; i++)
		{
			string arg = args[i];

			switch (arg.ToLowerInvariant())
			{
				case "-i":
					infoMode = true;
					break;

				case "-m":
					forceMemory = true;
					break;

				case "-f":
					forceTemp = true;
					break;

				case "--debug":
					debugMode = true;
					break;

				case "-p":
					if (i + 1 >= args.Length)
					{
						return Error("Не указано значение для -p");
					}
					progressFilePath = args[++i];
					break;

				case "-c":
					if (i + 1 >= args.Length)
					{
						return Error("Не указано значение для -c");
					}
					compressionModeText = args[++i];
					break;

				case "-h":
				case "--help":
				case "/?":
					return new CommandLineOptions { Mode = OperationMode.Help };

				default:
					if (IsFlag(arg))
					{
						return Error($"Неизвестный параметр: {arg}");
					}
					positional.Add(arg);
					break;
			}
		}

		// Валидация
		if (forceMemory && forceTemp)
		{
			return Error("Параметры -m и -f нельзя использовать одновременно");
		}

		if (infoMode)
		{
			if (positional.Count < 1)
			{
				return Error("Не указан путь к файлу для режима информации");
			}

			return new CommandLineOptions
			{
				Mode = OperationMode.Info,
				InputPath = positional[0],
				DebugMode = debugMode
			};
		}

		// Режим перепаковки
		if (positional.Count < 2)
		{
			return Error("Не указаны входной и выходной пути");
		}

		if (string.IsNullOrWhiteSpace(compressionModeText))
		{
			return Error("Не указан метод сжатия. Используйте -c lzma, -c lz4 или -c lz4fast");
		}

		if (!CompressionHelper.TryParse(compressionModeText, out var compType))
		{
			return Error($"Неизвестный метод сжатия: {compressionModeText}");
		}

		return new CommandLineOptions
		{
			Mode = OperationMode.Recompress,
			InputPath = positional[0],
			OutputPath = positional[1],
			CompressionType = compType,
			ProgressFilePath = progressFilePath,
			ForceMemory = forceMemory,
			ForceTemp = forceTemp,
			DebugMode = debugMode
		};
	}

	private static CommandLineOptions Error(string message) =>
		new() { ErrorMessage = message };

	private static bool IsHelpArg(string arg) =>
		HelpArgs.Contains(arg);

	private static bool IsFlag(string arg) =>
		arg.StartsWith("-", StringComparison.Ordinal) || arg.StartsWith("/", StringComparison.Ordinal);
}

#endregion

#region Helpers

/// Хелпер для работы с типами сжатия
internal static class CompressionHelper
{
	public static bool TryParse(string modeText, out AssetBundleCompressionType compType)
	{
		compType = modeText.ToLowerInvariant() switch
		{
			"lzma" => AssetBundleCompressionType.LZMA,
			"lz4" => AssetBundleCompressionType.LZ4,
			"lz4fast" => AssetBundleCompressionType.LZ4Fast,
			_ => AssetBundleCompressionType.None
		};

		return compType != AssetBundleCompressionType.None ||
			   modeText.Equals("none", StringComparison.OrdinalIgnoreCase);
	}

	public static string GetDisplayName(AssetBundleCompressionType compType) =>
		compType switch
		{
			AssetBundleCompressionType.LZMA => "LZMA",
			AssetBundleCompressionType.LZ4 => "LZ4",
			AssetBundleCompressionType.None => "None",
			_ => compType.ToString()
		};

	public static string GetSkipMarker(AssetBundleCompressionType compType) =>
		compType switch
		{
			AssetBundleCompressionType.LZMA => "SKIPPED_ALREADY_LZMA",
			AssetBundleCompressionType.LZ4 => "SKIPPED_ALREADY_LZ4",
			_ => "SKIPPED_ALREADY_COMPRESSED"
		};

	public static bool IsAlreadyTargetCompression(
		AssetBundleCompressionType original,
		AssetBundleCompressionType target)
	{
		// LZ4 и LZ4Fast при чтении определяются как LZ4
		if (target == AssetBundleCompressionType.LZ4Fast)
			return original == AssetBundleCompressionType.LZ4;

		return original == target;
	}
}

/// Хелпер для работы с файлами
internal static class FileHelper
{
	public static string CalculateMd5(string path)
	{
		using var fs = File.OpenRead(path);
		using var md5 = MD5.Create();
		byte[] hash = md5.ComputeHash(fs);
		return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
	}

	public static void EnsureDirectoryExists(string? directoryPath)
	{
		if (!string.IsNullOrEmpty(directoryPath) && !Directory.Exists(directoryPath))
		{
			Directory.CreateDirectory(directoryPath);
		}
	}

	public static void SafeDelete(string? filePath)
	{
		try
		{
			if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
			{
				File.Delete(filePath);
			}
		}
		catch
		{
			// Игнорируем ошибки удаления
		}
	}
}

/// Управление progress-файлом
internal sealed class ProgressTracker : IDisposable
{
	private readonly string? _filePath;
	private bool _disposed;

	public ProgressTracker(string? progressFilePath)
	{
		_filePath = NormalizeProgressPath(progressFilePath);
	}

	public void WriteState(string state)
	{
		if (string.IsNullOrWhiteSpace(_filePath))
			return;

		try
		{
			File.WriteAllText(_filePath, state);
		}
		catch
		{
			// Игнорируем ошибки записи прогресса
		}
	}

	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;
		// Можно добавить очистку или финальную запись
	}

	private static string? NormalizeProgressPath(string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
			return null;

		string fullPath = Path.GetFullPath(path);
		FileHelper.EnsureDirectoryExists(Path.GetDirectoryName(fullPath));
		return fullPath;
	}
}

#endregion

#region Console Output

/// Форматированный вывод в консоль
internal static class ConsoleOutput
{
	private const int LabelWidth = 18;
	
	public static bool DebugMode { get; set; } = false;

	public static void WriteInfo(string label, string value)
	{
		Console.WriteLine($"{label + ":",-LabelWidth} {value}");
	}

	public static void WriteHelp(string label, string value)
	{
		Console.WriteLine("  " + FormatHelpLine(label, value));
	}

	private static string FormatHelpLine(string label, string value)
	{
		return string.Format("{0,-18} {1}", label, value);
	}

	public static void WriteLine(string message = "") =>
		Console.WriteLine(message);

	public static void WriteError(string message)
	{
		var oldColor = Console.ForegroundColor;
		Console.ForegroundColor = ConsoleColor.Red;
		Console.WriteLine($"ОШИБКА: {message}");
		Console.ForegroundColor = oldColor;
	}

	public static void WriteWarning(string message)
	{
		var oldColor = Console.ForegroundColor;
		Console.ForegroundColor = ConsoleColor.Yellow;
		Console.WriteLine($"ПРЕДУПРЕЖДЕНИЕ: {message}");
		Console.ForegroundColor = oldColor;
	}

	public static void WriteDebug(string message)
	{
		if (!DebugMode) return;
		
		var oldColor = Console.ForegroundColor;
		Console.ForegroundColor = ConsoleColor.DarkGray;
		Console.WriteLine($"[DEBUG] {message}");
		Console.ForegroundColor = oldColor;
	}

	public static void WaitForAnyKey()
	{
		Console.WriteLine();
		Console.WriteLine("Нажмите любую клавишу для выхода...");
		Console.ReadKey(true);
	}
	
	public static void PrintRuntimeInfo()
	{
		WriteLine();
		WriteLine("Системные требования:");
		WriteLine($"  Требуется {AppConfig.RequiredRuntime} Runtime");
		WriteLine();
		WriteLine("Установка runtime:");
		WriteLine($"  Windows: {AppConfig.WingetCommand}");
		WriteLine($"  Или:	 {AppConfig.RuntimeDownloadUrl}");
	}

	public static void PrintHelp()
	{
		Console.WriteLine("BUNDLE RECOMPRESS CLI");
		Console.WriteLine("=====================");
		Console.WriteLine();
		Console.WriteLine("Описание:");
		Console.WriteLine("  Утилита для просмотра информации о Unity bundle и их перепаковки");
		Console.WriteLine("  в LZMA / LZ4 на основе библиотеки AssetsTools.NET");
		Console.WriteLine();
		Console.WriteLine("Режимы работы:");
		Console.WriteLine("  1) Информация о файле");
		Console.WriteLine("	 BundleRecompressCli <file>");
		Console.WriteLine("	 BundleRecompressCli -i <file>");
		Console.WriteLine();
		Console.WriteLine("  2) Перепаковка");
		Console.WriteLine("	 BundleRecompressCli <input> <output> -c <mode> [опции]");
		Console.WriteLine();
		Console.WriteLine("Параметры:");
		Console.WriteLine("  " + FormatHelpLine("-i", "Показать информацию о bundle-файле"));
		Console.WriteLine("  " + FormatHelpLine("-c <mode>", "Метод сжатия: lzma, lz4, lz4fast"));
		Console.WriteLine("  " + FormatHelpLine("-p <file>", "Путь к progress-файлу для внешнего отслеживания"));
		Console.WriteLine("  " + FormatHelpLine("-m", "Принудительно распаковывать в память (автоматически отключается при объёме данных более 1.5 GB)"));
		Console.WriteLine("  " + FormatHelpLine("-f", "Принудительно распаковывать во временный файл"));
		Console.WriteLine("  " + FormatHelpLine("--debug", "Включить отладочный вывод процесса обновления"));
		Console.WriteLine("  " + FormatHelpLine("-h, --help, /?", "Показать эту справку"));
		Console.WriteLine();
		Console.WriteLine("Методы сжатия:");
		Console.WriteLine("  " + FormatHelpLine("lzma", "Меньше размер, медленнее"));
		Console.WriteLine("  " + FormatHelpLine("lz4", "Быстрее, обычно больше размер"));
		Console.WriteLine("  " + FormatHelpLine("lz4fast", "Быстрая упаковка LZ4"));
		Console.WriteLine();
		Console.WriteLine("Поведение по умолчанию:");
		Console.WriteLine("  - Если bundle уже сжат указанным методом, обработка пропускается");
		Console.WriteLine("  - Если bundle сжат другим методом, он обработается с предварительной распаковкой");
		Console.WriteLine("  - Для файлов меньше 500 МБ по умолчанию для распаковки используется память");
		Console.WriteLine("  - Для файлов 500 МБ и больше используется временный файл");
		Console.WriteLine("  - Обновление AssetsTools.NET выполняет BundleRecompressCli.exe (Bootstrap)");
		Console.WriteLine();
		Console.WriteLine("Примеры:");
		Console.WriteLine(@"  BundleRecompressCli ""D:\in\test.bundle""");
		Console.WriteLine(@"  BundleRecompressCli -i ""D:\in\test.bundle""");
		Console.WriteLine(@"  BundleRecompressCli ""D:\in\test.bundle"" ""D:\out\test.bundle"" -c lzma");
		Console.WriteLine(@"  BundleRecompressCli ""D:\in\test.bundle"" ""D:\out\test.bundle"" -c lz4");
		Console.WriteLine(@"  BundleRecompressCli ""D:\in\big.bundle"" ""D:\out\big.bundle"" -c lzma -p ""D:\temp\big.progress""");
		Console.WriteLine(@"  BundleRecompressCli ""D:\in\test.bundle"" ""D:\out\test.bundle"" -c lzma -m");
		Console.WriteLine(@"  BundleRecompressCli ""D:\in\test.bundle"" ""D:\out\test.bundle"" -c lzma -f");
		PrintRuntimeInfo();
	}
}

#endregion

#region Error Logging

/// Логирование ошибок в файл
internal static class ErrorLogger
{
	public static string? WriteErrorLog(Exception ex)
	{
		try
		{
			string exeDir = AppContext.BaseDirectory;
			string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
			string logPath = Path.Combine(exeDir, $"error_{stamp}.log");

			using var sw = new StreamWriter(logPath, false);
			sw.WriteLine($"Время: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
			sw.WriteLine($"Папка exe: {exeDir}");
			sw.WriteLine($"Текущая папка: {Environment.CurrentDirectory}");
			sw.WriteLine();
			sw.WriteLine("Аргументы командной строки:");

			foreach (string arg in Environment.GetCommandLineArgs())
			{
				sw.WriteLine($"  {arg}");
			}

			sw.WriteLine();
			sw.WriteLine("Исключение:");
			sw.WriteLine(ex.ToString());

			return logPath;
		}
		catch
		{
			return null;
		}
	}
}

#endregion

#region Progress Reporter

/// Отчёт о прогрессе сжатия
internal sealed class ConsoleCompressProgress : IAssetBundleCompressProgress
{
	private readonly ProgressTracker? _tracker;
	private int _lastPercent = -1;
	private int _lastWrittenPercent = -1;

	public ConsoleCompressProgress(ProgressTracker? tracker)
	{
		_tracker = tracker;
	}

	public void SetProgress(float progress)
	{
		int percent = (int)(progress * 100f);

		if (percent == _lastPercent)
			return;

		_lastPercent = percent;

		if (_tracker != null)
		{
			bool shouldWrite = percent == 100 ||
							   _lastWrittenPercent < 0 ||
							   Math.Abs(percent - _lastWrittenPercent) >= AppConfig.ProgressWriteThresholdPercent;

			if (shouldWrite)
			{
				_tracker.WriteState($"packing:{percent}");
				_lastWrittenPercent = percent;
			}
		}
		else if (!Console.IsOutputRedirected)
		{
			Console.Write($"\rПрогресс: {percent}%   ");
		}
	}
}

#endregion

#region Bundle Service

/// Сервис для работы с Unity bundle файлами
internal sealed class BundleService : IDisposable
{
	private readonly AssetsManager _assetsManager = new();
	private bool _disposed;
	
	public BundleInfo GetBundleInfo(string filePath)
	{
		string fullPath = Path.GetFullPath(filePath);
		var fileInfo = new FileInfo(fullPath);
		
		var bundleInst = LoadBundle(fullPath, unpack: false);
		
		try
		{
			var compression = bundleInst.file.GetCompressionType();
			string md5 = FileHelper.CalculateMd5(fullPath);

			return new BundleInfo(fullPath, fileInfo.Length, md5, compression);
		}
		finally
		{
			bundleInst.file.Close();
		}
	}

	public RecompressResult Recompress(
		string inputPath,
		string outputPath,
		AssetBundleCompressionType targetCompression,
		ProgressTracker? progressTracker,
		bool forceMemory,
		bool forceTemp)
	{
		string inputFull = Path.GetFullPath(inputPath);
		string outputFull = Path.GetFullPath(outputPath);

		// Валидация
		if (string.Equals(inputFull, outputFull, StringComparison.OrdinalIgnoreCase))
		{
			return new RecompressResult(false, false, 0, 
				"Входной и выходной пути должны отличаться");
		}

		if (!File.Exists(inputFull))
		{
			return new RecompressResult(false, false, 0, 
				$"Входной файл не найден: {inputFull}");
		}

		FileHelper.EnsureDirectoryExists(Path.GetDirectoryName(outputFull));

		var inputInfo = new FileInfo(inputFull);
		var bundleInst = LoadBundle(inputFull, unpack: false);

		try
		{
			return ProcessBundle(bundleInst, inputInfo, outputFull, 
				targetCompression, progressTracker, forceMemory, forceTemp);
		}
		finally
		{
			bundleInst.file.Close();
		}
	}

	private RecompressResult ProcessBundle(
		BundleFileInstance bundleInst,
		FileInfo inputInfo,
		string outputFull,
		AssetBundleCompressionType targetCompression,
		ProgressTracker? progressTracker,
		bool forceMemory,
		bool forceTemp)
	{
		var originalCompression = bundleInst.file.GetCompressionType();
		bool needsUnpack = originalCompression != AssetBundleCompressionType.None;
		bool useMemoryMode = DetermineMemoryMode(inputInfo.Length, forceMemory, forceTemp);

		// Распакованный размер может превышать лимит MemoryStream (~2 GB)
		// даже при небольшом сжатом файле (например, LZMA с сжатием 4x+).
		// В этом случае принудительно переходим на временный файл.
		if (needsUnpack && useMemoryMode)
		{
			long decompressedSize = GetTotalDecompressedSize(bundleInst.file);
			if (decompressedSize > AppConfig.MemoryModeMaxDecompressedBytes)
			{
				useMemoryMode = false;
				ConsoleOutput.WriteWarning(string.Format(
					"Распакованный объём данных ({0} MB) превышает безопасный лимит режима памяти ({1} MB). Используется временный файл.",
					decompressedSize / (1024 * 1024),
					AppConfig.MemoryModeMaxDecompressedBytes / (1024 * 1024)));
			}
		}

		// Вывод информации
		PrintProcessingInfo(inputInfo, outputFull, targetCompression, 
			originalCompression, needsUnpack, useMemoryMode);

		// Проверка: уже сжат нужным методом?
		if (CompressionHelper.IsAlreadyTargetCompression(originalCompression, targetCompression))
		{
			string marker = CompressionHelper.GetSkipMarker(targetCompression);
			ConsoleOutput.WriteLine(marker);
			ConsoleOutput.WriteLine($"Файл уже сжат в {CompressionHelper.GetDisplayName(targetCompression)}. Пропуск.");
			progressTracker?.WriteState("done");
			return new RecompressResult(true, true, inputInfo.Length);
		}

		string? tempFile = null;
		Stream? tempStream = null;

		try
		{
			// Распаковка если нужно
			if (needsUnpack)
			{
				(bundleInst, tempStream, tempFile) = UnpackBundle(
					bundleInst, outputFull, useMemoryMode, progressTracker);
			}

			// Упаковка
			progressTracker?.WriteState("packing:0");
			ConsoleOutput.WriteLine("Упаковка...");

			using var fs = File.Open(outputFull, FileMode.Create, FileAccess.Write);
			using var writer = new AssetsFileWriter(fs);
			
			var progress = new ConsoleCompressProgress(progressTracker);
			bundleInst.file.Pack(writer, targetCompression, true, progress);

			progressTracker?.WriteState("done");

			var outputInfo = new FileInfo(outputFull);
			ConsoleOutput.WriteLine();
			ConsoleOutput.WriteLine("Готово.");
			ConsoleOutput.WriteInfo("Размер выхода", $"{outputInfo.Length} bytes");

			return new RecompressResult(true, false, outputInfo.Length);
		}
		finally
		{
			tempStream?.Dispose();
			FileHelper.SafeDelete(tempFile);
		}
	}

	private (BundleFileInstance, Stream?, string?) UnpackBundle(
		BundleFileInstance bundleInst,
		string outputFull,
		bool useMemoryMode,
		ProgressTracker? progressTracker)
	{
		var oldBundle = bundleInst.file;
		Stream tempStream;
		string? tempFile = null;

		if (useMemoryMode)
		{
			ConsoleOutput.WriteLine("Распаковка bundle в память...");
			tempStream = new MemoryStream();
		}
		else
		{
			progressTracker?.WriteState("decompress-temp");
			ConsoleOutput.WriteLine("Распаковка bundle во временный файл...");
			
			tempFile = outputFull + AppConfig.TempFileSuffix;
			FileHelper.SafeDelete(tempFile);
			tempStream = File.Open(tempFile, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
		}

		oldBundle.Unpack(new AssetsFileWriter(tempStream));
		tempStream.Position = 0;

		var newBundle = new AssetBundleFile();
		newBundle.Read(new AssetsFileReader(tempStream));
		oldBundle.Close();

		bundleInst.file = newBundle;
		return (bundleInst, tempStream, tempFile);
	}

	private static bool DetermineMemoryMode(long fileSize, bool forceMemory, bool forceTemp)
	{
		if (forceMemory) return true;
		if (forceTemp) return false;
		return fileSize < AppConfig.MemoryThresholdBytes;
	}

	private static long GetTotalDecompressedSize(AssetBundleFile bundle)
	{
		// Суммарный размер данных после распаковки всех блоков.
		// Именно этот объём будет записан в целевой поток при Unpack.
		long total = 0;
		var blockInfos = bundle.BlockAndDirInfo?.BlockInfos;
		if (blockInfos == null) return total;

		foreach (var blockInfo in blockInfos)
		{
			total += blockInfo.DecompressedSize;
		}

		return total;
	}

	private static void PrintProcessingInfo(
		FileInfo inputInfo,
		string outputFull,
		AssetBundleCompressionType targetCompression,
		AssetBundleCompressionType originalCompression,
		bool needsUnpack,
		bool useMemoryMode)
	{
		ConsoleOutput.WriteInfo("Открытие", inputInfo.FullName);
		ConsoleOutput.WriteInfo("Выходной файл", outputFull);
		ConsoleOutput.WriteInfo("Сжатие", CompressionHelper.GetDisplayName(targetCompression));
		ConsoleOutput.WriteInfo("Размер входа", $"{inputInfo.Length} bytes");
		ConsoleOutput.WriteInfo("Исходное сжатие", CompressionHelper.GetDisplayName(originalCompression));
		ConsoleOutput.WriteInfo("Нужна распаковка", needsUnpack ? "Да" : "Нет");
		ConsoleOutput.WriteInfo("Режим памяти", useMemoryMode ? "Да" : "Нет");
	}

	private BundleFileInstance LoadBundle(string path, bool unpack) =>
		_assetsManager.LoadBundleFile(path, unpack);

	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;
		// AssetsManager не реализует IDisposable, но можем добавить очистку при необходимости
	}
}

#endregion

#region Commands

/// Базовый интерфейс команды
internal interface ICommand
{
	int Execute();
}

/// Команда показа справки
internal sealed class HelpCommand : ICommand
{
	private readonly bool _hasError;
	private readonly string? _errorMessage;

	public HelpCommand(string? errorMessage = null)
	{
		_hasError = !string.IsNullOrEmpty(errorMessage);
		_errorMessage = errorMessage;
	}

	public int Execute()
	{
		if (_hasError)
		{
			ConsoleOutput.WriteError(_errorMessage!);
		}

		ConsoleOutput.PrintHelp();

		return _hasError ? AppConfig.ExitInvalidArgs : AppConfig.ExitSuccess;
	}
}

/// Команда показа информации о bundle
internal sealed class InfoCommand : ICommand
{
	private readonly string _filePath;

	public InfoCommand(string filePath)
	{
		_filePath = filePath;
	}

	public int Execute()
	{
		try
		{
			if (!File.Exists(_filePath))
			{
				ConsoleOutput.WriteError($"Файл не найден: {_filePath}");
				return AppConfig.ExitFileNotFound;
			}

			using var service = new BundleService();
			var info = service.GetBundleInfo(_filePath);

			ConsoleOutput.WriteLine("Информация о bundle");
			ConsoleOutput.WriteInfo("Файл", info.FilePath);
			ConsoleOutput.WriteInfo("Размер", $"{info.FileSize} bytes");
			ConsoleOutput.WriteInfo("MD5", info.Md5Hash);
			ConsoleOutput.WriteInfo("Метод сжатия", CompressionHelper.GetDisplayName(info.CompressionType));

			return AppConfig.ExitSuccess;
		}
		catch (Exception ex)
		{
			return HandleException(ex, waitForKey: true);
		}
	}

	private static int HandleException(Exception ex, bool waitForKey)
	{
		string? errorLogPath = ErrorLogger.WriteErrorLog(ex);

		ConsoleOutput.WriteLine();
		ConsoleOutput.WriteError(ex.ToString());

		if (!string.IsNullOrWhiteSpace(errorLogPath))
		{
			ConsoleOutput.WriteLine();
			ConsoleOutput.WriteLine($"Лог ошибки записан в: {errorLogPath}");
		}

		if (waitForKey)
		{
		}

		return AppConfig.ExitException;
	}
}

/// Команда перепаковки bundle
internal sealed class RecompressCommand : ICommand
{
	private readonly CommandLineOptions _options;

	public RecompressCommand(CommandLineOptions options)
	{
		_options = options;
	}

	public int Execute()
	{
		using var progressTracker = new ProgressTracker(_options.ProgressFilePath);
		progressTracker.WriteState("opening");

		try
		{
			using var service = new BundleService();
			
			var result = service.Recompress(
				_options.InputPath!,
				_options.OutputPath!,
				_options.CompressionType,
				progressTracker,
				_options.ForceMemory,
				_options.ForceTemp);

			if (!result.Success)
			{
				ConsoleOutput.WriteError(result.ErrorMessage ?? "Неизвестная ошибка");
				return AppConfig.ExitInvalidArgs;
			}

			return AppConfig.ExitSuccess;
		}
		catch (Exception ex)
		{
			return HandleException(ex);
		}
	}

	private static int HandleException(Exception ex)
	{
		string? errorLogPath = ErrorLogger.WriteErrorLog(ex);

		ConsoleOutput.WriteLine();
		ConsoleOutput.WriteError(ex.ToString());

		if (!string.IsNullOrWhiteSpace(errorLogPath))
		{
			ConsoleOutput.WriteLine();
			ConsoleOutput.WriteLine($"Лог ошибки записан в: {errorLogPath}");
		}

		return AppConfig.ExitException;
	}
}

#endregion

#region Command Factory

/// Фабрика команд
internal static class CommandFactory
{
	public static ICommand Create(CommandLineOptions options)
	{
		if (options.HasError)
		{
			return new HelpCommand(options.ErrorMessage);
		}

		return options.Mode switch
		{
			OperationMode.Help => new HelpCommand(),
			OperationMode.Info => new InfoCommand(options.InputPath!),
			OperationMode.Recompress => new RecompressCommand(options),
			_ => new HelpCommand("Неизвестный режим работы")
		};
	}
}

#endregion

#region Entry Point

internal static class Program
{
	private static int Main(string[] args)
	{
		var options = ArgumentParser.Parse(args);

		ConsoleOutput.DebugMode = options.DebugMode;

		var command = CommandFactory.Create(options);
		int result = command.Execute();

		// Прямой запуск без аргументов (двойной клик) интерактивный: справка и пауза.
		// AssetsTools.NET подготавливает Bootstrap до запуска этого процесса.
		if (args.Length == 0)
			ConsoleOutput.WaitForAnyKey();

		return result;
	}

}

#endregion