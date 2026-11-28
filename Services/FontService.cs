using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;

namespace CoreSpectre.Services;

/// <summary>
/// Обеспечивает отказоустойчивую загрузку и мгновенное динамическое переключение
/// шрифтов интерфейса CoreSpectre без перезапуска приложения.
/// </summary>
public static class FontService
{
    public static string FontsDirectory { get; private set; } = string.Empty;

    private static readonly string[] RequiredFontFiles =
    [
        "Inter-Regular.ttf",
        "GolosText.ttf",
        "Manrope.ttf",
        "Onest.ttf",
        "JetBrainsMono.ttf"
    ];

    /// <summary>
    /// Инициализация сервиса шрифтов: поиск или извлечение TTF файлов на диск.
    /// </summary>
    public static void Initialize()
    {
        FontsDirectory = LocateOrExtractFonts();

        // Устанавливаем системные токены по умолчанию в Application.Resources
        Application.Current.Resources["FontMain"] = GetFontFamily("Inter");
        Application.Current.Resources["FontMono"] = GetFontFamily("JetBrains Mono");
    }

    /// <summary>
    /// Создает экземпляр FontFamily, указывающий на проверенный файл на диске.
    /// </summary>
    public static FontFamily GetFontFamily(string fontName)
    {
        if (fontName == "Segoe UI")
        {
            return new FontFamily("Segoe UI, -apple-system, sans-serif");
        }

        string targetFamily = fontName switch
        {
            "Golos Text" => "Golos Text",
            "Manrope" => "Manrope",
            "Onest" => "Onest",
            "JetBrains Mono" => "JetBrains Mono",
            _ => "Inter"
        };

        if (!string.IsNullOrEmpty(FontsDirectory) && Directory.Exists(FontsDirectory))
        {
            // Формат прямого пути WPF для файлов шрифтов: {FontsDirectory}\#{FontName}
            return new FontFamily($"{FontsDirectory}\\#{targetFamily}, Segoe UI, sans-serif");
        }

        // Защитный fallback, если папка не найдена
        return new FontFamily("Segoe UI, -apple-system, sans-serif");
    }

    private static string LocateOrExtractFonts()
    {
        // 1. Проверяем папку Assets/Fonts рядом с исполняемым файлом (и legacy Fonts/)
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string assetFontsDir = Path.Combine(baseDir, "Assets", "Fonts");
        if (DirectoryHasAllFonts(assetFontsDir))
        {
            return assetFontsDir;
        }

        string localFontsDir = Path.Combine(baseDir, "Fonts");
        if (DirectoryHasAllFonts(localFontsDir))
        {
            return localFontsDir;
        }

        // 2. Резервный путь в AppData пользователя (для переносимого single-file exe)
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!Path.IsPathFullyQualified(localAppData)) return baseDir;
        string appDataFontsDir = Path.Combine(localAppData, "CoreSpectre", "Fonts");

        // 3. Извлекаем из внедренных ресурсов сборки (EmbeddedResources)
        try
        {
            Directory.CreateDirectory(appDataFontsDir);
            var asm = Assembly.GetExecutingAssembly();
            foreach (var resName in asm.GetManifestResourceNames())
            {
                if (resName.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase))
                {
                    string fileName = resName;
                    int lastDot = resName.LastIndexOf('.', resName.Length - 5);
                    if (lastDot >= 0)
                    {
                        fileName = resName[(lastDot + 1)..];
                    }

                    string destFile = Path.Combine(appDataFontsDir, fileName);
                    using var stream = asm.GetManifestResourceStream(resName);
                    if (stream == null) continue;
                    var cached = new FileInfo(destFile);
                    if (cached.Exists && cached.Length == stream.Length) continue;

                    string tempFile = destFile + $".tmp.{Guid.NewGuid():N}";
                    try
                    {
                        using (var fs = File.Create(tempFile)) stream.CopyTo(fs);
                        File.Move(tempFile, destFile, overwrite: true);
                    }
                    finally
                    {
                        try { File.Delete(tempFile); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    }
                }
            }

            if (DirectoryHasAllFonts(appDataFontsDir))
            {
                return appDataFontsDir;
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"Font discovery fallback triggered: {ex.Message}");
        }

        // Если есть хоть один файл в localFontsDir, возвращаем его
        if (Directory.Exists(localFontsDir)) return localFontsDir;

        return baseDir;
    }

    private static bool DirectoryHasAllFonts(string dir)
    {
        if (!Directory.Exists(dir)) return false;
        foreach (var fontFile in RequiredFontFiles)
        {
            if (!File.Exists(Path.Combine(dir, fontFile))) return false;
        }
        return true;
    }
}
