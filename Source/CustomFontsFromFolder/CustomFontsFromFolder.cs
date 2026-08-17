using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using RimWorld.Planet;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore;
using UnityEngine.TextCore.LowLevel;
using Verse;

namespace CustomFontsFromFolder
{
    public class FontSettings : ModSettings
    {
        public const string DefaultFontName = "<default>";

        public string uiFontName = DefaultFontName;
        public string worldFontName = DefaultFontName;
        public float scaleFactor = 1f;
        public int verticalOffset = 0;
        public int settingsVersion = 0;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref uiFontName, "uiFontName", DefaultFontName);
            Scribe_Values.Look(ref worldFontName, "worldFontName", DefaultFontName);
            Scribe_Values.Look(ref scaleFactor, "scaleFactor", 1f);
            Scribe_Values.Look(ref verticalOffset, "verticalOffset", 0);
            Scribe_Values.Look(ref settingsVersion, "settingsVersion", 0);
            base.ExposeData();
        }
    }

    public class FontEntry
    {
        public string Key;
        public string FilePath;
        public string DisplayName;
        public string[] OsFontNames;
        public Font Font;
        public AssetBundle Bundle;
        public TMP_FontAsset TmpFontAsset;
    }

    public static class FontManager
    {
        public const string DefaultFontName = FontSettings.DefaultFontName;

        private static ModContentPack _content;
        private static FontSettings _settings;

        private static bool _uiDefaultsCaptured;
        private static bool _worldDefaultsCaptured;
        private static bool _fontsScanned;
        private static bool _guiReady;

        private static readonly Font[] DefaultUIFonts = new Font[3];
        private static readonly int[] DefaultUIFontSizes = new int[3];
        private static readonly Vector2[] DefaultFontContentOffsets = new Vector2[3];
        private static readonly Vector2[] DefaultTextFieldContentOffsets = new Vector2[3];
        private static readonly Vector2[] DefaultTextAreaContentOffsets = new Vector2[3];
        private static readonly Vector2[] DefaultTextAreaReadOnlyContentOffsets = new Vector2[3];
        private static TMP_FontAsset _defaultWorldFontAsset;

        private static FieldInfo _forceLegacyTextField;
        private static FieldInfo _textScaleField;

        private static bool _osFontNamesLoaded;
        private static readonly List<string> OSInstalledFontNames = new List<string>();



        public static readonly Dictionary<string, FontEntry> Fonts =
            new Dictionary<string, FontEntry>(StringComparer.OrdinalIgnoreCase);

        public static FontSettings Settings => _settings;

        public static string FontsDirectory =>
            _content == null ? null : Path.Combine(_content.RootDir, "fonts");

        public static IEnumerable<FontEntry> SortedFonts =>
            Fonts.Values.OrderBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase);

        public static bool HasCustomUIFont =>
            _settings != null &&
            _settings.uiFontName != DefaultFontName &&
            Fonts.ContainsKey(_settings.uiFontName);

        public static bool HasCustomWorldFont =>
            _settings != null &&
            _settings.worldFontName != DefaultFontName &&
            Fonts.ContainsKey(_settings.worldFontName);

        public static void Initialize(ModContentPack content, FontSettings settings)
        {
            _content = content;
            _settings = settings;
        }

        public static void OnGUIReady()
        {
            if (_guiReady)
            {
                return;
            }

            _guiReady = true;
            CaptureUIDefaults();
            CaptureWorldDefaults();
            ScanFonts();
            ApplyUIFont();
            ApplyWorldFont();
        }

        public static void ReloadFonts()
        {
            ScanFonts(force: true);
            ApplyUIFont();
            ApplyWorldFont();
        }

        private static void ResetMissingFontSelections()
        {
            if (_settings == null)
            {
                return;
            }

            if (_settings.uiFontName != DefaultFontName && !Fonts.ContainsKey(_settings.uiFontName))
            {
                Log.Warning($"[CustomFontsFromFolder] Previously selected UI font '{_settings.uiFontName}' is no longer present; resetting to default.");
                _settings.uiFontName = DefaultFontName;
            }

            if (_settings.worldFontName != DefaultFontName && !Fonts.ContainsKey(_settings.worldFontName))
            {
                Log.Warning($"[CustomFontsFromFolder] Previously selected world font '{_settings.worldFontName}' is no longer present; resetting to default.");
                _settings.worldFontName = DefaultFontName;
            }
        }

        public static void CaptureUIDefaults()
        {
            if (_uiDefaultsCaptured)
            {
                return;
            }

            _uiDefaultsCaptured = true;
            for (int i = 0; i < Text.fontStyles.Length; i++)
            {
                DefaultUIFonts[i] = Text.fontStyles[i].font;
                DefaultUIFontSizes[i] = DefaultUIFonts[i] != null ? DefaultUIFonts[i].fontSize : Text.fontStyles[i].fontSize;
                if (DefaultUIFontSizes[i] <= 0)
                {
                    DefaultUIFontSizes[i] = Text.fontStyles[i].fontSize;
                }
                DefaultFontContentOffsets[i] = Text.fontStyles[i].contentOffset;
                DefaultTextFieldContentOffsets[i] = Text.textFieldStyles[i].contentOffset;
                DefaultTextAreaContentOffsets[i] = Text.textAreaStyles[i].contentOffset;
                DefaultTextAreaReadOnlyContentOffsets[i] = Text.textAreaReadOnlyStyles[i].contentOffset;
            }
        }

        public static void CaptureWorldDefaults()
        {
            if (_worldDefaultsCaptured)
            {
                return;
            }

            try
            {
                GameObject prefab = WorldFeatureTextMesh_TextMeshPro.WorldTextPrefab;
                TextMeshPro textMesh = prefab?.GetComponent<TextMeshPro>();
                _defaultWorldFontAsset = textMesh?.font;
                _worldDefaultsCaptured = true;
                if (_defaultWorldFontAsset == null)
                {
                    Log.Warning("[CustomFontsFromFolder] Could not read the default world map font.");
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[CustomFontsFromFolder] Could not initialize world map font support: {ex.Message}");
            }
        }

        public static void ScanFonts(bool force = false)
        {
            if (_fontsScanned && !force)
            {
                return;
            }

            _fontsScanned = true;
            UnloadAllBundles();
            Fonts.Clear();

            string directory = FontsDirectory;
            if (directory == null)
            {
                return;
            }

            if (!Directory.Exists(directory))
            {
                Log.Warning($"[CustomFontsFromFolder] Fonts folder does not exist: {directory}");
                return;
            }

            try
            {
                if (FontConfigInterop.AddFontDirectory(directory))
                {
                    _osFontNamesLoaded = false;
                    OSInstalledFontNames.Clear();
                    Log.Message($"[CustomFontsFromFolder] Registered '{directory}' with fontconfig.");
                }
                else
                {
                    Log.Warning("[CustomFontsFromFolder] Could not register fonts folder with fontconfig; dynamic OS font lookup may not see these files.");
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[CustomFontsFromFolder] fontconfig interop failed: {ex.Message}");
            }

            string[] files;
            try
            {
                files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                    .Where(file => IsSupportedFontFile(file))
                    .ToArray();
            }
            catch (Exception ex)
            {
                Log.Warning($"[CustomFontsFromFolder] Failed to enumerate fonts folder '{directory}': {ex.Message}");
                return;
            }

            foreach (string file in files)
            {
                string familyName = null;
                string styleName = null;

                try
                {
                    FontEngine.InitializeFontEngine();
                    if (FontEngine.LoadFontFace(file) != FontEngineError.Success)
                    {
                        Log.Warning($"[CustomFontsFromFolder] Unity FontEngine could not load font file '{file}'; skipping.");
                        continue;
                    }

                    FaceInfo faceInfo = FontEngine.GetFaceInfo();
                    familyName = faceInfo.familyName;
                    styleName = faceInfo.styleName;
                }
                catch (Exception ex)
                {
                    Log.Warning($"[CustomFontsFromFolder] Failed to read font metadata from '{file}': {ex.Message}");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(familyName))
                {
                    Log.Warning($"[CustomFontsFromFolder] Font file '{file}' does not expose a family name; skipping.");
                    continue;
                }

                string key = GetRelativeFontPath(directory, file);
                if (Fonts.ContainsKey(key))
                {
                    Log.Warning($"[CustomFontsFromFolder] Duplicate font path '{key}'; skipping '{file}'.");
                    continue;
                }

                if (TryLoadBundledFont(file, out Font bundledFont, out AssetBundle bundle))
                {
                    bool fileHasCJK = FontFileHasCJK();
                    bool bundledFontHasCJK = FontHasCharacter(bundledFont, '中');
                    if (fileHasCJK && !bundledFontHasCJK)
                    {
                        Log.Warning($"[CustomFontsFromFolder] Bundled font '{familyName}' from '{file}' does not expose CJK glyphs; skipping.");
                        if (bundle != null)
                        {
                            bundle.Unload(true);
                        }
                        continue;
                    }

                    string bundledDisplayName = MakeDisplayName(file, familyName, styleName);
                    Fonts[key] = new FontEntry
                    {
                        Key = key,
                        FilePath = file,
                        DisplayName = bundledDisplayName,
                        OsFontNames = BuildOSFontNameCandidates(familyName, styleName),
                        Font = bundledFont,
                        Bundle = bundle
                    };
                    Log.Message($"[CustomFontsFromFolder] Registered bundled font '{bundledDisplayName}' from {file}.fontbundle.");
                    continue;
                }

                string[] osFontNames = BuildOSFontNameCandidates(familyName, styleName);
                Font dynamicFont = null;
                try
                {
                    dynamicFont = Font.CreateDynamicFontFromOSFont(osFontNames, 16);
                }
                catch (Exception ex)
                {
                    Log.Warning($"[CustomFontsFromFolder] Failed to create dynamic OS font for '{familyName}': {ex.Message}");
                }

                if (dynamicFont == null || !dynamicFont.dynamic)
                {
                    Log.Warning($"[CustomFontsFromFolder] Font '{familyName}' from file '{file}' could not be created as a dynamic OS font. RimWorld's UI can only render dynamic OS fonts on Linux, so this file will not be listed.");
                    continue;
                }

                if (FontFileHasCJK() && !FontHasCharacter(dynamicFont, '中'))
                {
                    Log.Warning($"[CustomFontsFromFolder] Font '{familyName}' from file '{file}' contains CJK glyphs, but Unity's dynamic OS font does not expose them. The font will not be listed.");
                    continue;
                }

                string displayName = MakeDisplayName(file, familyName, styleName);
                Fonts[key] = new FontEntry
                {
                    Key = key,
                    FilePath = file,
                    DisplayName = displayName,
                    OsFontNames = osFontNames
                };

                Log.Message($"[CustomFontsFromFolder] Registered font '{displayName}' from {file}.");
            }

            Log.Message($"[CustomFontsFromFolder] Loaded {Fonts.Count} font file(s) from {directory}.");
            ResetMissingFontSelections();
        }

        public static void ApplyUIFont()
        {
            CaptureUIDefaults();

            float scale = Mathf.Clamp(_settings?.scaleFactor ?? 1f, 0.5f, 2f);
            float verticalOffset = _settings?.verticalOffset ?? 0;
            bool useDefaultFont = _settings == null || _settings.uiFontName == DefaultFontName;

            for (int i = 0; i < Text.fontStyles.Length; i++)
            {
                int fontSize = Mathf.RoundToInt(DefaultUIFontSizes[i] * scale);
                Font font = GetUIFont(i, fontSize);
                Vector2 baseFontOffset = useDefaultFont ? DefaultFontContentOffsets[i] : Vector2.zero;
                Vector2 baseTextFieldOffset = useDefaultFont ? DefaultTextFieldContentOffsets[i] : Vector2.zero;
                Vector2 baseTextAreaOffset = useDefaultFont ? DefaultTextAreaContentOffsets[i] : Vector2.zero;
                Vector2 baseTextAreaReadOnlyOffset = useDefaultFont ? DefaultTextAreaReadOnlyContentOffsets[i] : Vector2.zero;

                Text.fontStyles[i].font = font;
                Text.fontStyles[i].fontSize = fontSize;
                Text.fontStyles[i].contentOffset = baseFontOffset + new Vector2(0f, verticalOffset);

                Text.textFieldStyles[i].font = font;
                Text.textFieldStyles[i].fontSize = fontSize;
                Text.textFieldStyles[i].contentOffset = baseTextFieldOffset + new Vector2(0f, verticalOffset);

                Text.textAreaStyles[i].font = font;
                Text.textAreaStyles[i].fontSize = fontSize;
                Text.textAreaStyles[i].contentOffset = baseTextAreaOffset + new Vector2(0f, verticalOffset);

                Text.textAreaReadOnlyStyles[i].font = font;
                Text.textAreaReadOnlyStyles[i].fontSize = fontSize;
                Text.textAreaReadOnlyStyles[i].contentOffset = baseTextAreaReadOnlyOffset + new Vector2(0f, verticalOffset);
            }
        }

        public static void ApplyWorldFont()
        {
            CaptureWorldDefaults();

            bool custom = HasCustomWorldFont;
            SetForceLegacyText(!custom);
            SetWorldTextScale(Mathf.Clamp(_settings?.scaleFactor ?? 1f, 0.5f, 2f));
            ApplyWorldFontToPrefab();
            InvalidateWorldTexts();
        }

        public static void ApplyWorldFontToPrefab()
        {
            CaptureWorldDefaults();

            GameObject prefab = null;
            try
            {
                prefab = WorldFeatureTextMesh_TextMeshPro.WorldTextPrefab;
            }
            catch (Exception ex)
            {
                Log.Warning($"[CustomFontsFromFolder] World text prefab is not available: {ex.Message}");
                return;
            }

            TextMeshPro textMesh = prefab?.GetComponent<TextMeshPro>();
            if (textMesh == null)
            {
                return;
            }

            TMP_FontAsset fontAsset = GetWorldFontAsset();
            if (fontAsset == null)
            {
                fontAsset = _defaultWorldFontAsset;
            }

            if (fontAsset == null)
            {
                return;
            }

            textMesh.font = fontAsset;
            textMesh.UpdateFontAsset();
        }

        private static Font GetUIFont(int fontIndex, int fontSize)
        {
            if (_settings != null &&
                _settings.uiFontName != DefaultFontName &&
                Fonts.TryGetValue(_settings.uiFontName, out FontEntry entry))
            {
                if (entry.Font != null)
                {
                    return entry.Font;
                }

                Font font = CreateDynamicOSFont(entry, fontSize);
                if (font != null)
                {
                    return font;
                }

                Log.Warning($"[CustomFontsFromFolder] Failed to create dynamic OS font for '{entry.DisplayName}'; falling back to default UI font.");
            }

            return DefaultUIFonts[fontIndex] ?? Text.fontStyles[fontIndex].font;
        }

        private static TMP_FontAsset GetWorldFontAsset()
        {
            if (_settings == null || _settings.worldFontName == DefaultFontName)
            {
                return _defaultWorldFontAsset;
            }

            if (!Fonts.TryGetValue(_settings.worldFontName, out FontEntry entry))
            {
                return _defaultWorldFontAsset;
            }

            if (entry.TmpFontAsset != null)
            {
                return entry.TmpFontAsset;
            }

            Font sourceFont = entry.Font;
            if (sourceFont == null)
            {
                sourceFont = CreateDynamicOSFont(entry, 90);
            }

            if (sourceFont == null)
            {
                Log.Warning($"[CustomFontsFromFolder] Failed to create source font for world map font '{entry.DisplayName}'.");
                return _defaultWorldFontAsset;
            }

            try
            {
                entry.TmpFontAsset = TMP_FontAsset.CreateFontAsset(
                    sourceFont,
                    90,
                    9,
                    GlyphRenderMode.SDFAA,
                    1024,
                    1024,
                    AtlasPopulationMode.Dynamic,
                    enableMultiAtlasSupport: true);

                if (entry.TmpFontAsset != null)
                {
                    Log.Message($"[CustomFontsFromFolder] Created world map font asset for '{entry.DisplayName}'.");
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[CustomFontsFromFolder] Failed to create world map font asset for '{entry.DisplayName}': {ex.Message}");
                entry.TmpFontAsset = null;
            }

            return entry.TmpFontAsset ?? _defaultWorldFontAsset;
        }

        private static void SetForceLegacyText(bool value)
        {
            if (_forceLegacyTextField == null)
            {
                _forceLegacyTextField = typeof(WorldFeatures).GetField(
                    "ForceLegacyText",
                    BindingFlags.NonPublic | BindingFlags.Static);
            }

            try
            {
                _forceLegacyTextField?.SetValue(null, value);
            }
            catch (Exception ex)
            {
                Log.Warning($"[CustomFontsFromFolder] Failed to set WorldFeatures.ForceLegacyText: {ex.Message}");
            }
        }

        private static void SetWorldTextScale(float scale)
        {
            if (_textScaleField == null)
            {
                _textScaleField = typeof(WorldFeatureTextMesh_TextMeshPro).GetField(
                    "TextScale",
                    BindingFlags.NonPublic | BindingFlags.Static);
            }

            try
            {
                _textScaleField?.SetValue(null, scale);
            }
            catch (Exception ex)
            {
                Log.Warning($"[CustomFontsFromFolder] Failed to set world text scale: {ex.Message}");
            }
        }

        private static void InvalidateWorldTexts()
        {
            try
            {
                if (Current.Game != null && Current.Game.World != null && Current.Game.World.features != null)
                {
                    Current.Game.World.features.textsCreated = false;
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[CustomFontsFromFolder] Failed to invalidate world feature texts: {ex.Message}");
            }
        }

        private static bool IsSupportedFontFile(string file)
        {
            string extension = Path.GetExtension(file);
            return extension.Equals(".ttf", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".otf", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".ttc", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetRelativeFontPath(string fontsDirectory, string file)
        {
            if (file.StartsWith(fontsDirectory, StringComparison.OrdinalIgnoreCase))
            {
                string relative = file.Substring(fontsDirectory.Length);
                relative = relative.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return relative;
            }

            return Path.GetFileName(file);
        }

        private static void UnloadAllBundles()
        {
            foreach (FontEntry entry in Fonts.Values)
            {
                if (entry.Bundle != null)
                {
                    entry.Bundle.Unload(true);
                    entry.Bundle = null;
                }
                entry.Font = null;
            }
        }

        private static bool TryLoadBundledFont(string fontFilePath, out Font font, out AssetBundle bundle)
        {
            font = null;
            bundle = null;

            string bundlePath = fontFilePath + ".fontbundle";
            if (!File.Exists(bundlePath))
            {
                return false;
            }

            try
            {
                bundle = AssetBundle.LoadFromFile(bundlePath);
                if (bundle == null)
                {
                    Log.Warning($"[CustomFontsFromFolder] Failed to load font bundle '{bundlePath}'.");
                    return false;
                }

                Font[] fonts = bundle.LoadAllAssets<Font>();
                for (int i = 0; i < fonts.Length; i++)
                {
                    if (fonts[i] != null && fonts[i].dynamic)
                    {
                        font = fonts[i];
                        return true;
                    }
                }

                Log.Warning($"[CustomFontsFromFolder] Font bundle '{bundlePath}' contains no dynamic Font asset.");
                bundle.Unload(true);
                bundle = null;
                return false;
            }
            catch (Exception ex)
            {
                Log.Warning($"[CustomFontsFromFolder] Failed to load font bundle '{bundlePath}': {ex.Message}");
                if (bundle != null)
                {
                    bundle.Unload(true);
                }
                bundle = null;
                return false;
            }
        }

        private static bool FontFileHasCJK()
        {
            try
            {
                return FontEngine.TryGetGlyphIndex(0x4E2D, out _);
            }
            catch
            {
                return false;
            }
        }

        private static bool FontHasCharacter(Font font, char character)
        {
            if (font == null)
            {
                return false;
            }

            try
            {
                return font.HasCharacter(character);
            }
            catch
            {
                return false;
            }
        }

        private static Font CreateDynamicOSFont(FontEntry entry, int fontSize)
        {
            if (entry == null || entry.OsFontNames == null || entry.OsFontNames.Length == 0)
            {
                return null;
            }

            try
            {
                Font font = Font.CreateDynamicFontFromOSFont(entry.OsFontNames, fontSize);
                if (font != null && font.dynamic)
                {
                    return font;
                }

                Log.Warning($"[CustomFontsFromFolder] Unity refused to create a dynamic OS font for '{entry.DisplayName}'.");
            }
            catch (Exception ex)
            {
                Log.Warning($"[CustomFontsFromFolder] Failed to create dynamic OS font for '{entry.DisplayName}': {ex.Message}");
            }

            return null;
        }

        private static string MakeDisplayName(string file, string familyName, string styleName)
        {
            string fileName = Path.GetFileName(file);

            if (string.IsNullOrWhiteSpace(familyName))
            {
                return fileName;
            }

            if (string.IsNullOrWhiteSpace(styleName) ||
                styleName.Equals("Regular", StringComparison.OrdinalIgnoreCase))
            {
                return $"{familyName} ({fileName})";
            }

            return $"{familyName} {styleName} ({fileName})";
        }

        private static string[] BuildOSFontNameCandidates(string familyName, string styleName)
        {
            EnsureOSInstalledFontNames();

            string trimmedFamily = familyName.Trim();
            string trimmedStyle = string.IsNullOrWhiteSpace(styleName) ? null : styleName.Trim();
            string familyKey = NormalizeFontName(trimmedFamily);
            string fullKey = null;

            if (!string.IsNullOrWhiteSpace(trimmedStyle) &&
                !trimmedStyle.Equals("Regular", StringComparison.OrdinalIgnoreCase))
            {
                fullKey = NormalizeFontName($"{trimmedFamily} {trimmedStyle}");
            }

            var matched = new List<string>();
            var fallback = new List<string>();

            // Prefer exact names that Unity itself reports as installed OS fonts.
            for (int i = 0; i < OSInstalledFontNames.Count; i++)
            {
                string installedName = OSInstalledFontNames[i];
                if (string.IsNullOrWhiteSpace(installedName))
                {
                    continue;
                }

                string installedKey = NormalizeFontName(installedName);

                if (fullKey != null && installedKey == fullKey)
                {
                    matched.Add(installedName);
                }
                else if (installedKey == familyKey)
                {
                    matched.Add(installedName);
                }
                else if (installedKey.StartsWith(familyKey, StringComparison.Ordinal))
                {
                    fallback.Add(installedName);
                }
            }

            var candidates = new List<string>();
            candidates.AddRange(matched);
            candidates.AddRange(fallback);

            if (!string.IsNullOrWhiteSpace(trimmedStyle) &&
                !trimmedStyle.Equals("Regular", StringComparison.OrdinalIgnoreCase))
            {
                string generatedFullName = $"{trimmedFamily} {trimmedStyle}";
                if (!candidates.Contains(generatedFullName, StringComparer.OrdinalIgnoreCase))
                {
                    candidates.Add(generatedFullName);
                }
            }

            if (!candidates.Contains(trimmedFamily, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(trimmedFamily);
            }

            if (candidates.Count > 1)
            {
                Log.Message($"[CustomFontsFromFolder] OS font candidates for '{trimmedFamily}': {string.Join(" | ", candidates.ToArray())}");
            }

            return candidates.ToArray();
        }

        private static void EnsureOSInstalledFontNames()
        {
            if (_osFontNamesLoaded)
            {
                return;
            }

            _osFontNamesLoaded = true;
            try
            {
                string[] installedNames = Font.GetOSInstalledFontNames();
                if (installedNames == null)
                {
                    return;
                }

                for (int i = 0; i < installedNames.Length; i++)
                {
                    if (!string.IsNullOrWhiteSpace(installedNames[i]))
                    {
                        OSInstalledFontNames.Add(installedNames[i].Trim());
                    }
                }

                Log.Message($"[CustomFontsFromFolder] Unity reports {OSInstalledFontNames.Count} installed OS font name(s): {string.Join(" | ", OSInstalledFontNames.ToArray())}");
            }
            catch (Exception ex)
            {
                Log.Warning($"[CustomFontsFromFolder] Failed to read Unity's OS font name list: {ex.Message}");
            }
        }

        private static string NormalizeFontName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return string.Empty;
            }

            var sb = new System.Text.StringBuilder(name.Length);
            for (int i = 0; i < name.Length; i++)
            {
                if (!char.IsWhiteSpace(name[i]))
                {
                    sb.Append(char.ToLowerInvariant(name[i]));
                }
            }

            return sb.ToString();
        }
    }

    internal static class FontConfigInterop
    {
        private const string FontConfigLibrary = "fontconfig";

        [DllImport(FontConfigLibrary)]
        private static extern int FcInit();

        [DllImport(FontConfigLibrary)]
        private static extern IntPtr FcConfigGetCurrent();

        [DllImport(FontConfigLibrary)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool FcConfigAppFontAddDir(IntPtr config, [MarshalAs(UnmanagedType.LPStr)] string dir);

        public static bool AddFontDirectory(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return false;
            }

            FcInit();
            IntPtr config = FcConfigGetCurrent();
            if (config == IntPtr.Zero)
            {
                return false;
            }

            return FcConfigAppFontAddDir(config, directory);
        }
    }

    public class CustomFontsFromFolderMod : Mod
    {
        public static CustomFontsFromFolderMod Instance { get; private set; }

        private Vector2 _settingsScrollPosition = Vector2.zero;

        public CustomFontsFromFolderMod(ModContentPack content) : base(content)
        {
            Instance = this;
            FontSettings settings = GetSettings<FontSettings>();
            FontManager.Initialize(content, settings);

            // The first released build wrote font-size 0 for custom fonts, which made all UI text
            // invisible. Reset old settings once so affected users can recover on startup.
            if (settings.settingsVersion < 3)
            {
                bool hadCustomFont =
                    settings.uiFontName != FontManager.DefaultFontName ||
                    settings.worldFontName != FontManager.DefaultFontName;

                settings.settingsVersion = 3;
                if (hadCustomFont)
                {
                    Log.Warning("[CustomFontsFromFolder] Old broken font settings detected; resetting to default.");
                    settings.uiFontName = FontManager.DefaultFontName;
                    settings.worldFontName = FontManager.DefaultFontName;
                }
            }

            Harmony harmony = new Harmony("ntp.customfontsfromfolder");
            harmony.PatchAll();
        }

        public override string SettingsCategory()
        {
            return "Custom Fonts From Folder";
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            FontSettings settings = FontManager.Settings;
            if (settings == null)
            {
                return;
            }

            FontManager.ScanFonts();

            int fontRowCount = 2 + FontManager.Fonts.Count * 2;
            float viewHeight = (fontRowCount + 10) * Text.LineHeight + 120f;
            Rect viewRect = new Rect(0f, 0f, inRect.width - 24f, viewHeight);

            Widgets.BeginScrollView(inRect, ref _settingsScrollPosition, viewRect);

            Listing_Standard listing = new Listing_Standard();
            listing.Begin(viewRect);

            listing.Label($"字体文件夹 / Fonts folder:\n{FontManager.FontsDirectory ?? "unknown"}");
            listing.Gap(4f);

            if (listing.ButtonText("重新扫描字体 / Reload fonts"))
            {
                FontManager.ReloadFonts();
            }

            listing.GapLine();

            listing.Label("界面字体 / Interface font:");
            if (listing.RadioButton("默认 / Default", settings.uiFontName == FontManager.DefaultFontName))
            {
                settings.uiFontName = FontManager.DefaultFontName;
                FontManager.ApplyUIFont();
            }

            foreach (FontEntry entry in FontManager.SortedFonts)
            {
                if (listing.RadioButton(entry.DisplayName, settings.uiFontName == entry.Key))
                {
                    settings.uiFontName = entry.Key;
                    FontManager.ApplyUIFont();
                }
            }

            listing.GapLine();

            listing.Label("世界地图字体 / World map font:");
            if (listing.RadioButton("默认 / Default", settings.worldFontName == FontManager.DefaultFontName))
            {
                settings.worldFontName = FontManager.DefaultFontName;
                FontManager.ApplyWorldFont();
            }

            foreach (FontEntry entry in FontManager.SortedFonts)
            {
                if (listing.RadioButton(entry.DisplayName, settings.worldFontName == entry.Key))
                {
                    settings.worldFontName = entry.Key;
                    FontManager.ApplyWorldFont();
                }
            }

            if (FontManager.Fonts.Count == 0)
            {
                listing.Gap(6f);
                listing.Label("未找到字体文件。请将 .ttf、.otf 或 .ttc 文件放入 fonts 文件夹，然后点击 Reload fonts。\nNo font files found. Put .ttf, .otf or .ttc files into the fonts folder and press Reload fonts.");
            }

            listing.GapLine();

            float newScale = listing.SliderLabeled(
                $"字体缩放 / Font scale: {settings.scaleFactor:F2}",
                settings.scaleFactor,
                0.5f,
                2f,
                0.45f);

            float roundedScale = Mathf.Round(newScale * 10f) / 10f;
            if (Mathf.Abs(roundedScale - settings.scaleFactor) > 0.0001f)
            {
                settings.scaleFactor = roundedScale;
                FontManager.ApplyUIFont();
                FontManager.ApplyWorldFont();
            }

            float newOffset = listing.SliderLabeled(
                $"垂直偏移 / Vertical offset: {settings.verticalOffset}",
                settings.verticalOffset,
                -20f,
                20f,
                0.45f);

            int roundedOffset = Mathf.RoundToInt(newOffset);
            if (roundedOffset != settings.verticalOffset)
            {
                settings.verticalOffset = roundedOffset;
                FontManager.ApplyUIFont();
            }

            listing.End();
            Widgets.EndScrollView();
        }
    }

    [HarmonyPatch(typeof(Text), nameof(Text.StartOfOnGUI))]
    public static class Text_StartOfOnGUI_Patch
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            FontManager.OnGUIReady();
        }
    }

    [HarmonyPatch(typeof(GenScene), nameof(GenScene.GoToMainMenu))]
    public static class GenScene_GoToMainMenu_Patch
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            FontManager.ApplyUIFont();
            FontManager.ApplyWorldFont();
        }
    }

    [HarmonyPatch(typeof(WorldFeatureTextMesh_TextMeshPro), nameof(WorldFeatureTextMesh_TextMeshPro.Init))]
    public static class WorldFeatureTextMesh_TextMeshPro_Init_Patch
    {
        [HarmonyPrefix]
        public static void Prefix()
        {
            FontManager.ApplyWorldFontToPrefab();
        }
    }

    [HarmonyPatch(typeof(WorldFeatures), "HasCharacter")]
    public static class WorldFeatures_HasCharacter_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(ref bool __result)
        {
            if (FontManager.HasCustomWorldFont)
            {
                __result = true;
                return false;
            }

            return true;
        }
    }
}
