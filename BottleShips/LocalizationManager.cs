using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using BottleShips;
using HarmonyLib;
using YamlDotNet.Serialization;

namespace LocalizationManager;

internal static class Localizer
{
    private const string DefaultLanguage = "English";
    private const string TranslationExtension = ".yml";

    private static readonly IDeserializer Deserializer =
        new DeserializerBuilder().IgnoreFields().Build();

    private static BottleShipsPlugin? _plugin;
    private static IReadOnlyDictionary<string, string> _externalTranslations =
        new Dictionary<string, string>();

    internal static void Load(BottleShipsPlugin plugin)
    {
        _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
        _externalTranslations = FindExternalTranslations();
    }

    internal static void LoadSelectedLanguage()
    {
        Localization? localization = Localization.instance;
        if (localization is null)
        {
            BottleShipsPlugin.BottleShipsLogger.LogWarning(
                "Could not load BottleShips translations because Localization.instance is not ready.");
            return;
        }

        LoadLocalization(localization, localization.GetSelectedLanguage());
    }

    internal static void LoadLocalization(Localization localization, string language)
    {
        if (_plugin is null)
        {
            return;
        }

        Dictionary<string, string> texts = new(StringComparer.Ordinal);
        if (!TryMergeEmbeddedTranslation(texts, DefaultLanguage, required: true))
        {
            return;
        }

        TryMergeExternalTranslation(texts, _externalTranslations, DefaultLanguage);

        if (!string.Equals(language, DefaultLanguage, StringComparison.OrdinalIgnoreCase))
        {
            TryMergeEmbeddedTranslation(texts, language, required: false);
            TryMergeExternalTranslation(texts, _externalTranslations, language);
        }

        foreach (KeyValuePair<string, string> translation in texts)
        {
            localization.AddWord(translation.Key, translation.Value);
        }
    }

    private static Dictionary<string, string> FindExternalTranslations()
    {
        Dictionary<string, string> translations = new(StringComparer.OrdinalIgnoreCase);
        string pluginName = _plugin!.Info.Metadata.Name;
        string? bepInExRoot = Path.GetDirectoryName(Paths.PluginPath);
        if (string.IsNullOrEmpty(bepInExRoot))
        {
            BottleShipsPlugin.BottleShipsLogger.LogWarning(
                "Could not locate the BepInEx directory. External BottleShips translations will be skipped.");
            return translations;
        }

        string[] candidates;
        try
        {
            candidates = Directory
                .GetFiles(bepInExRoot, $"{pluginName}*{TranslationExtension}", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(path => path, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception exception)
        {
            BottleShipsPlugin.BottleShipsLogger.LogWarning(
                $"Could not scan for external BottleShips translations: {exception.Message}");
            return translations;
        }

        foreach (string candidate in candidates)
        {
            if (!TryGetLanguageFromFileName(candidate, pluginName, out string language))
            {
                BottleShipsPlugin.BottleShipsLogger.LogWarning(
                    $"Skipping external translation with an invalid file name: {candidate}. " +
                    $"Expected {pluginName}.<Language>{TranslationExtension}.");
                continue;
            }

            if (translations.TryGetValue(language, out string? existing))
            {
                BottleShipsPlugin.BottleShipsLogger.LogWarning(
                    $"Multiple external BottleShips translations were found for {language}. " +
                    $"Using {existing} and skipping {candidate}.");
                continue;
            }

            translations[language] = candidate;
        }

        return translations;
    }

    private static bool TryGetLanguageFromFileName(string path, string pluginName, out string language)
    {
        string fileName = Path.GetFileName(path);
        string prefix = pluginName + ".";

        language = string.Empty;
        if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !fileName.EndsWith(TranslationExtension, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        language = fileName.Substring(
            prefix.Length,
            fileName.Length - prefix.Length - TranslationExtension.Length);

        return !string.IsNullOrWhiteSpace(language) &&
               language.IndexOf('.') < 0 &&
               string.Equals(language, language.Trim(), StringComparison.Ordinal);
    }

    private static bool TryMergeEmbeddedTranslation(
        IDictionary<string, string> destination,
        string language,
        bool required)
    {
        byte[]? data = ReadEmbeddedTranslation(language);
        if (data is null)
        {
            if (required)
            {
                BottleShipsPlugin.BottleShipsLogger.LogError(
                    $"BottleShips has no embedded {language} translation. " +
                    $"Expected translations/{language}{TranslationExtension}.");
            }

            return false;
        }

        return TryMergeYaml(
            destination,
            Encoding.UTF8.GetString(data),
            $"embedded {language} translation",
            required);
    }

    private static void TryMergeExternalTranslation(
        IDictionary<string, string> destination,
        IReadOnlyDictionary<string, string> externalFiles,
        string language)
    {
        if (!externalFiles.TryGetValue(language, out string? path))
        {
            return;
        }

        string yaml;
        try
        {
            yaml = File.ReadAllText(path);
        }
        catch (Exception exception)
        {
            BottleShipsPlugin.BottleShipsLogger.LogWarning(
                $"Could not read external BottleShips translation {path}; " +
                $"the previous translation layer will be used. {exception.Message}");
            return;
        }

        TryMergeYaml(destination, yaml, $"external translation {path}", required: false);
    }

    private static bool TryMergeYaml(
        IDictionary<string, string> destination,
        string yaml,
        string source,
        bool required)
    {
        try
        {
            Dictionary<string, string>? translations =
                Deserializer.Deserialize<Dictionary<string, string>?>(yaml);

            if (translations is null || translations.Count == 0)
            {
                LogProblem(required,
                    $"BottleShips {source} is empty; " +
                    $"{(required ? "translations cannot be loaded" : "the previous translation layer will be used")}.");
                return false;
            }

            foreach (KeyValuePair<string, string> translation in translations)
            {
                destination[translation.Key] = translation.Value;
            }

            return true;
        }
        catch (Exception exception)
        {
            LogProblem(required,
                $"Could not parse BottleShips {source}; " +
                $"{(required ? "translations cannot be loaded" : "the previous translation layer will be used")}. " +
                exception.Message);
            return false;
        }
    }

    private static byte[]? ReadEmbeddedTranslation(string language)
    {
        Assembly assembly = typeof(Localizer).Assembly;
        string resourceSuffix = $"translations.{language}{TranslationExtension}";
        string? resourceName = assembly
            .GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(resourceSuffix, StringComparison.Ordinal));

        if (resourceName is null)
        {
            return null;
        }

        using Stream? resource = assembly.GetManifestResourceStream(resourceName);
        if (resource is null)
        {
            return null;
        }

        using MemoryStream data = new();
        resource.CopyTo(data);
        return data.Length == 0 ? null : data.ToArray();
    }

    private static void LogProblem(bool error, string message)
    {
        if (error)
        {
            BottleShipsPlugin.BottleShipsLogger.LogError(message);
        }
        else
        {
            BottleShipsPlugin.BottleShipsLogger.LogWarning(message);
        }
    }
}

[HarmonyPatch(typeof(Localization), nameof(Localization.SetupLanguage))]
internal static class LocalizationSetupLanguagePatch
{
    [HarmonyPostfix]
    private static void Postfix(Localization __instance, string language)
    {
        Localizer.LoadLocalization(__instance, language);
    }
}

[HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.SetupGui))]
internal static class FejdStartupSetupGuiPatch
{
    [HarmonyPostfix]
    private static void Postfix()
    {
        Localizer.LoadSelectedLanguage();
    }
}
