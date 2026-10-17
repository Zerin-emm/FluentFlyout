// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using FluentFlyout.Classes.Utils;
using FluentFlyoutWPF;
using System.Globalization;
using System.Windows;

namespace FluentFlyout.Classes;

public static class LocalizationManager
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    public static double maxLength = 0;

    // current language code (first two letters) for easy access
    public static string LanguageCode { get; set; } = string.Empty;

    // dictionary of supported languages where key is the local language name and value is the language/culture code
    // this fork ships English, Simplified Chinese and Traditional Chinese only. Any other language - including
    // one saved by an older build or the language Windows itself reports - falls back to English (en-US), see
    // ApplyLocalization below. Add a Resources/Localization/Dictionary-<code>.xaml and a line here to re-enable one.
    private static readonly Dictionary<string, string> _supportedLanguages = new()
    {
        { "System", "system" },
        { "English", "en-US" },
        { "中文（简体）", "zh-CN" },          // Chinese (Simplified)
        { "中文（繁體）", "zh-TW" },          // Chinese (Traditional)
    };

    // dictionary of font families for specific languages, priorities are switched around
    private static readonly Dictionary<string, string> _languageFontFamilies = new()
    {
        { "default", "Segoe UI Variable, Microsoft YaHei UI, Yu Gothic UI, Malgun Gothic" }, // default support for multiple languages
        //{ "zh-CN", "Segoe UI Variable, Microsoft YaHei UI, Yu Gothic UI, Malgun Gothic" }, // same as default
        { "zh-TW", "Segoe UI Variable, Microsoft JhengHei UI, Yu Gothic UI, Malgun Gothic" },
    };

    // readonly property to access supported languages
    public static Dictionary<string, string> SupportedLanguages => _supportedLanguages;

    public static void ApplyLocalization()
    {
        string culture;
        if (SettingsManager.Current.AppLanguage == "system")
        {
            culture = CultureInfo.CurrentUICulture.Name;
        }
        else
        {
            culture = SettingsManager.Current.AppLanguage;
        }

        // extract only the language code (first two letters) from the culture
        string languageCode = culture[..Math.Min(2, culture.Length)];
        LanguageCode = languageCode;

        // get current localization
        var dictionaries = App.Current.Resources.MergedDictionaries;

        // remove all localization dictionaries except the default one (en-US)
        foreach (var dictionary in dictionaries.ToList())
        {
            if (dictionary.Source != null
                && dictionary.Source.OriginalString.StartsWith("Resources/Localization/")
                && !dictionary.Source.OriginalString.EndsWith("Dictionary-en-US.xaml"))
            {
                dictionaries.Remove(dictionary);
            }
        }

        Logger.Info("Applying localization for language: " + culture);

        // change flow direction of all windows
        ApplyFlowDirection();

        ApplyFontFamily(culture);

        // Measure the Lock Key Flyout text before returning for English, otherwise maxLength stays 0
        // and the LockWindow collapses to a few pixels wide (see LockWindow.xaml.cs).
        CalculateLockKeyMaxLength();

        // if English, the default (en-US) is already loaded, so no need to add another dictionary
        if (languageCode == "en") return;

        // find the localization file path based on the first two letters of the language code
        string? localizationDictPath = $"Resources/Localization/Dictionary-{culture}.xaml";

        var uri = new Uri(localizationDictPath, UriKind.Relative);

        try
        {
            var resourceDict = new ResourceDictionary() { Source = uri };
            dictionaries.Add(resourceDict);
        }
        catch (Exception ex)
        {
            // localization file not found or malformed, try simplified language code instead
            // (log the reason: a duplicate x:Key or a wrongly encoded file would otherwise make the
            // whole application fall back to English without any trace)
            Logger.Error(ex, "Failed to load localization dictionary: " + localizationDictPath);

            try
            {
                localizationDictPath = $"Resources/Localization/Dictionary-{languageCode}.xaml";
                uri = new Uri(localizationDictPath, UriKind.Relative);

                var resourceDict = new ResourceDictionary() { Source = uri };
                dictionaries.Add(resourceDict);
            }
            catch (Exception fallbackEx)
            {
                // do nothing and keep the default (en-US)
                Logger.Error(fallbackEx, "Failed to load localization dictionary: " + localizationDictPath + ", keeping English");
            }
        }
    }

    // Calculate the Lock Key Flyout text's max length
    private static void CalculateLockKeyMaxLength()
    {
        List<double> Lengths = new List<double>();

        var insertPressed = Application.Current.TryFindResource("LockWindow_InsertPressed")?.ToString() ?? string.Empty;
        Lengths.Add(StringWidth.GetStringWidth(insertPressed));

        var On = Application.Current.TryFindResource("LockWindow_LockOn")?.ToString() ?? string.Empty;
        var Off = Application.Current.TryFindResource("LockWindow_LockOff")?.ToString() ?? string.Empty;
        var OnOffMax = On.Length >= Off.Length ? On + " " : Off + " ";

        var capsLock = Application.Current.TryFindResource("LockWindow_CapsLock")?.ToString() ?? string.Empty;
        var numLock = Application.Current.TryFindResource("LockWindow_NumLock")?.ToString() ?? string.Empty;
        var scrollLock = Application.Current.TryFindResource("LockWindow_ScrollLock")?.ToString() ?? string.Empty;

        Lengths.Add(StringWidth.GetStringWidth(OnOffMax + capsLock));
        Lengths.Add(StringWidth.GetStringWidth(OnOffMax + numLock));
        Lengths.Add(StringWidth.GetStringWidth(OnOffMax + scrollLock));

        maxLength = Lengths.Max() + 8; // additional margin to avoid text clipping

        // set minimum just in case if resources weren't loaded
        if (maxLength < 20)
            maxLength = 115; // 160 (default width) - 45 (estimated padding)
    }

    // every language this fork ships is left-to-right, so the flow direction is fixed
    private static void ApplyFlowDirection()
    {
        SettingsManager.Current.FlowDirection = FlowDirection.LeftToRight;

        Logger.Info("Applied flow direction: " + SettingsManager.Current.FlowDirection);
    }

    private static void ApplyFontFamily(string culture)
    {
        string fontFamily;
        if (_languageFontFamilies.TryGetValue(culture, out string? value))
        {
            fontFamily = value;
        }
        else if (_languageFontFamilies.TryGetValue(LanguageCode, out string? value1))
        {
            fontFamily = value1;
        }
        else
        {
            fontFamily = _languageFontFamilies["default"];
        }
        SettingsManager.Current.FontFamily = fontFamily;

        Logger.Debug("Applied font family: " + fontFamily);
    }
}