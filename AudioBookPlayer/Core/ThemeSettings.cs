using System;
using System.Collections.Generic;
using System.Globalization;

namespace AudioBookPlayer.Core
{
    /// <summary>
    /// 配色方案：只存 4 个基础色（背景 / 文字 / 强调 / 金），
    /// 其余（面板、输入框、边框、次级文字、轨道、选中态…）全部按明暗关系推导出来。
    /// </summary>
    public sealed class ThemeSettings
    {
        public string Background { get; set; } = PresetMonogatari.Background;

        public string Foreground { get; set; } = PresetMonogatari.Foreground;

        public string Accent { get; set; } = PresetMonogatari.Accent;

        public string Gold { get; set; } = PresetMonogatari.Gold;

        public string PresetName { get; set; } = PresetMonogatari.Name;

        public const string PresetMonogatariName = "物语 · 暗红";
        public const string PresetMidnightName = "夜色 · 蓝";
        public const string PresetForestName = "深林 · 绿";
        public const string PresetVioletName = "紫罗兰";
        public const string PresetPaperName = "米白 · 浅色";

        public static class PresetMonogatari
        {
            public const string Name = PresetMonogatariName;
            public const string Background = "#121011";
            public const string Foreground = "#F1EBE1";
            public const string Accent = "#9E2B25";
            public const string Gold = "#C9A227";
        }

        /// <summary>内置预设。</summary>
        public static IReadOnlyList<ThemeSettings> Presets { get; } = new[]
        {
            new ThemeSettings
            {
                PresetName = PresetMonogatariName,
                Background = PresetMonogatari.Background,
                Foreground = PresetMonogatari.Foreground,
                Accent = PresetMonogatari.Accent,
                Gold = PresetMonogatari.Gold,
            },
            new ThemeSettings
            {
                PresetName = PresetMidnightName,
                Background = "#0E1116",
                Foreground = "#E8EEF6",
                Accent = "#3B82F6",
                Gold = "#6FB6D9",
            },
            new ThemeSettings
            {
                PresetName = PresetForestName,
                Background = "#0F1411",
                Foreground = "#E7EFE8",
                Accent = "#2F7D5B",
                Gold = "#C9A227",
            },
            new ThemeSettings
            {
                PresetName = PresetVioletName,
                Background = "#120F16",
                Foreground = "#EDE8F5",
                Accent = "#7C4DFF",
                Gold = "#D8B4FE",
            },
            new ThemeSettings
            {
                PresetName = PresetPaperName,
                Background = "#F4F1EC",
                Foreground = "#221E1B",
                Accent = "#9E2B25",
                Gold = "#A8791A",
            },
        };

        public ThemeSettings Clone() => new ThemeSettings
        {
            Background = Background,
            Foreground = Foreground,
            Accent = Accent,
            Gold = Gold,
            PresetName = PresetName,
        };

        /// <summary>推导出全部主题画刷：键名与 Themes\Dark.xaml 里的资源键一一对应。</summary>
        public Dictionary<string, string> BuildPalette()
        {
            var background = Parse(Background, "#121011");
            var foreground = Parse(Foreground, "#F1EBE1");
            var accent = Parse(Accent, "#9E2B25");
            var gold = Parse(Gold, "#C9A227");

            var isLight = Luminance(background) > 0.55;

            // 深色主题：面板比背景亮一点；浅色主题：面板比背景更白，边框反而更深
            var surface = RgbColor.White;
            var panel = isLight ? Mix(background, surface, 0.55) : Mix(background, surface, 0.055);
            var card = isLight ? Mix(background, surface, 0.82) : Mix(background, surface, 0.095);
            var inset = isLight ? Mix(background, RgbColor.Black, 0.05) : Mix(background, RgbColor.Black, 0.32);

            var input = isLight ? Mix(background, RgbColor.White, 0.85) : Mix(background, surface, 0.13);
            var inputHover = isLight ? Mix(background, RgbColor.White, 0.65) : Mix(background, surface, 0.18);
            var inputPressed = isLight ? Mix(background, RgbColor.Black, 0.08) : Mix(background, surface, 0.24);

            var borderSoft = isLight ? Mix(background, RgbColor.Black, 0.14) : Mix(background, surface, 0.16);
            var borderStrong = isLight ? Mix(background, RgbColor.Black, 0.26) : Mix(background, surface, 0.28);

            var textSecondary = isLight ? Mix(foreground, background, 0.38) : Mix(foreground, background, 0.42);
            var textMuted = isLight ? Mix(foreground, background, 0.58) : Mix(foreground, background, 0.62);

            var onAccent = Luminance(accent) > 0.6 ? RgbColor.Black : RgbColor.White;

            var palette = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["WindowBackgroundBrush"] = Hex(background),
                ["PanelBackgroundBrush"] = Hex(panel),
                ["CardBackgroundBrush"] = Hex(card),
                ["InsetBackgroundBrush"] = Hex(inset),

                ["InputBackgroundBrush"] = Hex(input),
                ["InputHoverBrush"] = Hex(inputHover),
                ["InputPressedBrush"] = Hex(inputPressed),

                ["BorderSoftBrush"] = Hex(borderSoft),
                ["BorderStrongBrush"] = Hex(borderStrong),

                ["TextPrimaryBrush"] = Hex(foreground),
                ["TextSecondaryBrush"] = Hex(textSecondary),
                ["TextMutedBrush"] = Hex(textMuted),
                ["TextOnAccentBrush"] = Hex(onAccent),

                ["AccentBrush"] = Hex(accent),
                ["AccentHoverBrush"] = Hex(isLight ? Mix(accent, RgbColor.Black, 0.12) : Mix(accent, RgbColor.White, 0.14)),
                ["AccentPressedBrush"] = Hex(isLight ? Mix(accent, RgbColor.Black, 0.24) : Mix(accent, RgbColor.Black, 0.18)),
                ["AccentSoftBrush"] = Hex(Mix(background, accent, isLight ? 0.16 : 0.26), 0x3D),

                ["GoldBrush"] = Hex(gold),
                ["GoldSoftBrush"] = Hex(gold, 0x33),

                ["TrackBrush"] = Hex(isLight ? Mix(background, RgbColor.Black, 0.12) : Mix(background, surface, 0.19)),
                ["ListHoverBrush"] = Hex(isLight ? Mix(background, RgbColor.Black, 0.05) : Mix(background, surface, 0.06)),
                ["ListSelectedBrush"] = Hex(Mix(background, accent, isLight ? 0.20 : 0.28)),

                ["ScrollThumbBrush"] = Hex(isLight ? Mix(background, RgbColor.Black, 0.22) : Mix(background, surface, 0.24)),
                ["ScrollThumbHoverBrush"] = Hex(isLight ? Mix(background, RgbColor.Black, 0.34) : Mix(background, surface, 0.34)),

                ["DangerBrush"] = isLight ? "#C0392B" : "#E06C60",
            };

            return palette;
        }

        /// <summary>当前配色是不是浅色方案（决定悬浮字幕默认字色之类）。</summary>
        public bool IsLightTheme => Luminance(Parse(Background, "#121011")) > 0.55;

        // ---------------- 颜色工具 ----------------

        /// <summary>一个简单的 RGB 值（Core 层不引用 WPF）。</summary>
        public readonly struct RgbColor
        {
            public RgbColor(byte r, byte g, byte b)
            {
                R = r;
                G = g;
                B = b;
            }

            public byte R { get; }

            public byte G { get; }

            public byte B { get; }

            public static RgbColor White { get; } = new RgbColor(255, 255, 255);

            public static RgbColor Black { get; } = new RgbColor(0, 0, 0);
        }

        public static RgbColor Parse(string? hex, string fallback)
        {
            var value = (hex ?? string.Empty).Trim();

            if (value.StartsWith('#'))
            {
                value = value.Substring(1);
            }

            if (value.Length == 8)
            {
                value = value.Substring(2); // 丢掉 alpha
            }

            if (value.Length != 6 ||
                !int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var packed))
            {
                return Parse(fallback, "#000000");
            }

            return new RgbColor((byte)((packed >> 16) & 0xFF), (byte)((packed >> 8) & 0xFF), (byte)(packed & 0xFF));
        }

        public static string Hex(RgbColor color, byte alpha = 255)
        {
            return alpha == 255
                ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
                : $"#{alpha:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
        }

        private static RgbColor Mix(RgbColor from, RgbColor to, double amount)
        {
            amount = Math.Clamp(amount, 0, 1);
            return new RgbColor(
                (byte)Math.Round(from.R + (to.R - from.R) * amount),
                (byte)Math.Round(from.G + (to.G - from.G) * amount),
                (byte)Math.Round(from.B + (to.B - from.B) * amount));
        }

        private static double Luminance(RgbColor color)
        {
            return (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255.0;
        }
    }
}
