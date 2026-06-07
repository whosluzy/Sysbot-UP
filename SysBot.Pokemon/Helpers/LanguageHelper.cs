using PKHeX.Core;
using PKHeX.Core.AutoMod;
using System;

namespace SysBot.Pokemon.Helpers;

public static class LanguageHelper
{
    /// <summary>
    /// Canonical language-token parser used by every front-end and command. Accepts PKHeX
    /// enum names (Japanese, ChineseS), full English names (Japanese, Chinese Traditional),
    /// 3- and 2-letter codes (jpn/ja, fra/fr), and native scripts (日本語, 한국어). This is the
    /// single source of truth — do not add separate language maps elsewhere.
    /// </summary>
    public static bool TryParseLanguage(string token, out byte languageId)
    {
        languageId = 0;
        if (string.IsNullOrWhiteSpace(token))
            return false;

        token = token.Trim();

        // PKHeX enum names: Japanese, English, French, Italian, German, Spanish,
        // Korean, ChineseS, ChineseT, SpanishL, etc.
        if (Enum.TryParse<LanguageID>(token, true, out var langId) && Enum.IsDefined(langId))
        {
            languageId = (byte)langId;
            return languageId != 0;
        }

        languageId = token.ToLowerInvariant() switch
        {
            "japanese" or "jpn" or "jp" or "ja" or "日本語" => (byte)LanguageID.Japanese,
            "english" or "eng" or "en" => (byte)LanguageID.English,
            "french" or "fre" or "fra" or "fr" or "français" or "francais" => (byte)LanguageID.French,
            "italian" or "ita" or "it" or "italiano" => (byte)LanguageID.Italian,
            "german" or "ger" or "deu" or "de" or "deutsch" => (byte)LanguageID.German,
            "spanish" or "spa" or "esp" or "es" or "español" or "espanol" => (byte)LanguageID.Spanish,
            "spanish-latam" or "spanishl" or "latam" or "es-419" or "es-mx" => (byte)LanguageID.SpanishL,
            "korean" or "kor" or "ko" or "한국어" => (byte)LanguageID.Korean,
            "chinese" or "chinese simplified" or "chinese-simplified" or "simplified chinese"
                or "chs" or "zh" or "zh-cn" or "zh-hans" or "中文" or "中文简体" or "简体中文" => (byte)LanguageID.ChineseS,
            "chinese traditional" or "chinese-traditional" or "traditional chinese"
                or "cht" or "chineset" or "zh-tw" or "zh-hant" or "中文繁體" or "繁體中文" => (byte)LanguageID.ChineseT,
            _ => 0
        };

        return languageId != 0;
    }

    public static byte GetFinalLanguage(string content, ShowdownSet? set, byte configLanguage, Func<string, byte> detectLanguageFunc)
    {
        // Check if user explicitly specified a language in the showdown set
        var lines = content.Split('\n', StringSplitOptions.TrimEntries);
        foreach (var line in lines)
        {
            if (line.StartsWith("Language:", StringComparison.OrdinalIgnoreCase))
            {
                var languageValue = line["Language:".Length..].Trim();
                if (TryParseLanguage(languageValue, out var explicitLang))
                    return explicitLang;
            }
        }

        // No explicit language found, use detection
        byte detectedLanguage = detectLanguageFunc(content);

        // If no language was detected (0), use the config language setting
        if (detectedLanguage == 0)
        {
            return configLanguage;
        }

        return detectedLanguage;
    }

    // NOTE: This method is no longer used. We generate Pokemon with the normal trainer
    // and set the language AFTER generation to avoid encounter matching issues.
    // Kept for reference only.
    /*
    public static ITrainerInfo GetTrainerInfoWithLanguage<T>(LanguageID language) where T : PKM, new()
    {
        // This approach caused "No valid matching encounter" errors
        // because ALM couldn't find encounters with language-specific trainers
        return AutoLegalityWrapper.GetTrainerInfo<T>();
    }
    */
}
