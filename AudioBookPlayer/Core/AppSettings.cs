using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioBookPlayer.Core
{
    /// <summary>
    /// 极简的设置持久化：媒体库目录 + 上次用的字幕外观 / 位置 + 几个开关。
    /// 存在 %LOCALAPPDATA%\AudioBookPlayer\settings.json，读写失败一律静默降级为默认值。
    /// </summary>
    public sealed class AppSettings
    {
        private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        /// <summary>上次打开的媒体库目录。</summary>
        public string? LibraryFolder { get; set; }

        /// <summary>这份设置是从哪个文件读出来的（不序列化）。</summary>
        [JsonIgnore]
        public string? FilePath { get; set; }

        /// <summary>播完当前条目自动播下一条。</summary>
        public bool AutoContinue { get; set; } = true;

        /// <summary>媒体库只显示带字幕的条目。</summary>
        public bool ShowOnlyWithSubtitle { get; set; }

        /// <summary>媒体库只显示收藏的书。</summary>
        public bool ShowOnlyFavorites { get; set; }

        public double Volume { get; set; } = 1.0;

        /// <summary>快进 / 快退步长（秒）。</summary>
        public double SkipSeconds { get; set; } = 10;

        /// <summary>记住每个章节听到哪儿，下次自动接着播。</summary>
        public bool RememberPlaybackPosition { get; set; } = true;

        /// <summary>启动时自动恢复上次听的那一章。</summary>
        public bool ResumeLastOnStartup { get; set; } = true;

        /// <summary>启动时是否显示桌面悬浮字幕。</summary>
        public bool OverlayVisible { get; set; } = true;

        /// <summary>是否启用全局热键（不在前台也能控制播放）。</summary>
        public bool EnableGlobalHotkeys { get; set; } = true;

        /// <summary>配色方案（4 个基础色 + 预设名）。</summary>
        public string ThemeBackground { get; set; } = ThemeSettings.PresetMonogatari.Background;

        public string ThemeForeground { get; set; } = ThemeSettings.PresetMonogatari.Foreground;

        public string ThemeAccent { get; set; } = ThemeSettings.PresetMonogatari.Accent;

        public string ThemeGold { get; set; } = ThemeSettings.PresetMonogatari.Gold;

        public string ThemePresetName { get; set; } = ThemeSettings.PresetMonogatariName;

        /// <summary>是否显示字幕左上角的移动手柄。</summary>
        public bool OverlayShowHandle { get; set; } = true;

        /// <summary>关主窗口时收进托盘而不是退出。</summary>
        public bool MinimizeToTray { get; set; } = true;

        /// <summary>工具条平时自动隐藏，鼠标移上去才显示。</summary>
        public bool OverlayAutoHideToolbar { get; set; } = true;

        /// <summary>字幕是否处于"解锁可拖动"状态（首次运行默认解锁，方便先摆位置）。</summary>
        public bool OverlayMovable { get; set; } = true;

        /// <summary>悬浮字幕被拖动到的位置（Anchor = Custom 时生效）。</summary>
        public double? OverlayCustomX { get; set; }

        public double? OverlayCustomY { get; set; }

        // ---- 字幕外观 ----
        public string FontFamilyName { get; set; } = ViewModels.SubtitleOverlayViewModel.DefaultFontFamilyName;

        public double FontSize { get; set; } = 32;

        /// <summary>界面缩放：书库 / 播放 / 阅读，各自独立（1.0 = 100%）。</summary>
        public double LibraryZoom { get; set; } = 1.0;

        public double PlayerZoom { get; set; } = 1.0;

        public double ReadingZoom { get; set; } = 1.0;
        /// <summary>首选主字幕语言代码（""=原文，找不到就用第一条）。</summary>
        public string PreferredPrimaryLanguage { get; set; } = "";

        /// <summary>阅读模式首选语言（""=跟随悬浮字幕）。</summary>
        public string PreferredReadingLanguage { get; set; } = "";

        /// <summary>阅读模式对照语言（"__none__"=不显示对照）。</summary>
        public string PreferredReadingSecondaryLanguage { get; set; } = "__none__";

        /// <summary>首选副字幕语言代码（"__none__"=不显示副字幕）。</summary>
        public string PreferredSecondaryLanguage { get; set; } = "__none__";

        /// <summary>字重名（Light / Normal / Medium / SemiBold / Bold / Black）。</summary>
        public string FontWeightName { get; set; } = "Normal";

        public string ForegroundHex { get; set; } = "#FFFFFF";

        public string OutlineHex { get; set; } = "#000000";

        public double OutlineThickness { get; set; } = 3.5;

        // ---- 字幕位置 ----
        public string Anchor { get; set; } = nameof(ViewModels.OverlayAnchor.Bottom);

        public double OffsetX { get; set; }

        public double OffsetY { get; set; }

        public double OverlayWidth { get; set; } = 1400;

        public double OverlayHeight { get; set; } = 220;

        /// <summary>默认设置文件位置。</summary>
        public static string DefaultPath => AppPaths.SettingsFile;

        /// <summary>上一次保存失败的原因（保存成功则为 null）。</summary>
        [JsonIgnore]
        public string? LastError { get; private set; }

        /// <summary>读取设置时的错误（正常为 null）。设置坏了会退回默认值，这里记录原因。</summary>
        [JsonIgnore]
        public string? LoadError { get; set; }

        /// <summary>读取设置；文件不存在或损坏时返回默认值。</summary>
        public static AppSettings Load(string? path = null)
        {
            var target = path ?? DefaultPath;

            try
            {
                if (!File.Exists(target))
                {
                    return new AppSettings { FilePath = target };
                }

                var json = File.ReadAllText(target, System.Text.Encoding.UTF8);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
                if (settings == null)
                {
                    return new AppSettings { FilePath = target };
                }

                settings.FilePath = target;
                return settings;
            }
            catch (Exception ex)
            {
                // 设置坏了不该影响启动，但必须留下痕迹：
                // 否则表现就是"媒体库、配色全都莫名其妙变回默认"，还查不出原因。
                return new AppSettings { FilePath = target, LoadError = $"{ex.GetType().Name}: {ex.Message}" };
            }
        }

        /// <summary>保存设置；失败时静默忽略。</summary>
        public bool Save(string? path = null)
        {
            var target = path ?? FilePath ?? DefaultPath;

            try
            {
                var directory = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(target, JsonSerializer.Serialize(this, SerializerOptions), new System.Text.UTF8Encoding(false));
                LastError = null;
                return true;
            }
            catch (Exception ex)
            {
                // 不要静默失败：界面会把原因显示出来，否则用户只会觉得"设置没保存"
                LastError = $"{target}：{ex.Message}";
                return false;
            }
        }
    }
}
