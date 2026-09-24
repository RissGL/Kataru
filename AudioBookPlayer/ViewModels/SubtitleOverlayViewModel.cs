using System;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace AudioBookPlayer.ViewModels
{
    /// <summary>字幕在屏幕上的三种预设位置。</summary>
    public enum OverlayAnchor
    {
        /// <summary>底部居中（默认）。</summary>
        Bottom,

        /// <summary>屏幕正中。</summary>
        Center,

        /// <summary>顶部居中。</summary>
        Top,

        /// <summary>用户自己拖到的位置。</summary>
        Custom,
    }

    /// <summary>
    /// 悬浮字幕窗口的显示状态（文本 + 外观 + 位置）。
    /// SubtitleWindow 直接把这个对象当作 DataContext。
    /// </summary>
    public sealed class SubtitleOverlayViewModel : ObservableObject
    {
        /// <summary>默认字体回退链：优先日文字体，其次中文字体，最后西文兜底。</summary>
        public const string DefaultFontFamilyName =
            "Yu Gothic UI, Meiryo, Microsoft YaHei UI, Microsoft JhengHei UI, Segoe UI";

        private string _text = string.Empty;
        private string _secondaryText = string.Empty;
        private double _secondaryFontScale = 0.78;
        private string _fontFamilyName = DefaultFontFamilyName;
        private FontFamily _fontFamily = new FontFamily(DefaultFontFamilyName);
        private double _fontSize = 32;
        private System.Windows.FontWeight _fontWeight = System.Windows.FontWeights.Normal;
        private string _fontWeightName = nameof(System.Windows.FontWeights.Normal);
        private string _foregroundHex = "#FFFFFF";
        private string _outlineHex = "#000000";
        private double _outlineThickness = 3.5;
        private Brush _foreground = Brushes.White;
        private Brush _outlineBrush = Brushes.Black;
        private OverlayAnchor _anchor = OverlayAnchor.Bottom;
        private double _offsetX;
        private double _offsetY;
        private double _overlayWidth = 1400;
        private double _overlayHeight = 220;
        private bool _isVisible = true;
        private bool _showHandle = true;
        private bool _autoHideToolbar = true;
        private bool _isMovable;
        private double _customX;
        private double _customY;

        /// <summary>副字幕（双语模式的第二行，比如中文翻译）。</summary>
        public string SecondaryText
        {
            get => _secondaryText;
            set
            {
                if (SetProperty(ref _secondaryText, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(HasSecondaryText));
                }
            }
        }

        public bool HasSecondaryText => _secondaryText.Length > 0;

        /// <summary>副字幕相对主字幕的字号比例。</summary>
        public double SecondaryFontScale
        {
            get => _secondaryFontScale;
            set
            {
                if (SetProperty(ref _secondaryFontScale, Math.Clamp(value, 0.4, 1.5)))
                {
                    OnPropertyChanged(nameof(SecondaryFontSize));
                }
            }
        }
        /// <summary>当前显示的字幕文本（空字符串 = 不显示任何内容）。</summary>
        public string Text
        {
            get => _text;
            set => SetProperty(ref _text, value ?? string.Empty);
        }

        /// <summary>字体名，支持 WPF 的逗号回退链。</summary>
        public string FontFamilyName
        {
            get => _fontFamilyName;
            set
            {
                if (!SetProperty(ref _fontFamilyName, value ?? string.Empty))
                {
                    return;
                }

                try
                {
                    FontFamily = new FontFamily(string.IsNullOrWhiteSpace(value) ? DefaultFontFamilyName : value);
                }
                catch (ArgumentException)
                {
                    FontFamily = new FontFamily(DefaultFontFamilyName);
                }
            }
        }

        public FontFamily FontFamily
        {
            get => _fontFamily;
            private set => SetProperty(ref _fontFamily, value);
        }

        /// <summary>字重（WPF 的 FontWeight）。</summary>
        public System.Windows.FontWeight FontWeight
        {
            get => _fontWeight;
            private set => SetProperty(ref _fontWeight, value);
        }

        /// <summary>字重名（Light / Normal / Medium / SemiBold / Bold / Black），用于持久化与界面。</summary>
        public string FontWeightName
        {
            get => _fontWeightName;
            set
            {
                var name = string.IsNullOrWhiteSpace(value) ? nameof(System.Windows.FontWeights.Normal) : value.Trim();
                if (!SetProperty(ref _fontWeightName, name))
                {
                    return;
                }

                FontWeight = ParseFontWeight(name);
            }
        }

        /// <summary>可选字重（键 / 中文说明）。</summary>
        public static IReadOnlyList<KeyValuePair<string, string>> FontWeightOptions { get; } = new[]
        {
            new KeyValuePair<string, string>("Light", "细体"),
            new KeyValuePair<string, string>("Normal", "常规"),
            new KeyValuePair<string, string>("Medium", "中等"),
            new KeyValuePair<string, string>("SemiBold", "半粗"),
            new KeyValuePair<string, string>("Bold", "粗体"),
            new KeyValuePair<string, string>("Black", "特粗"),
        };

        /// <summary>给 XAML 绑定的实例访问器（静态属性绑不到）。</summary>
        public IReadOnlyList<KeyValuePair<string, string>> FontWeightChoices => FontWeightOptions;

        private static System.Windows.FontWeight ParseFontWeight(string name) => name switch
        {
            "Light" => System.Windows.FontWeights.Light,
            "Medium" => System.Windows.FontWeights.Medium,
            "SemiBold" => System.Windows.FontWeights.SemiBold,
            "Bold" => System.Windows.FontWeights.Bold,
            "Black" => System.Windows.FontWeights.Black,
            _ => System.Windows.FontWeights.Normal,
        };

        /// <summary>字号（DIP）。</summary>
        public double FontSize
        {
            get => _fontSize;
            set
            {
                if (SetProperty(ref _fontSize, Math.Clamp(value, 8, 200)))
                {
                    OnPropertyChanged(nameof(SecondaryFontSize));
                }
            }
        }

        /// <summary>副字幕的字号（按比例跟着主字号走）。</summary>
        public double SecondaryFontSize => Math.Clamp(_fontSize * _secondaryFontScale, 8, 200);


        /// <summary>字色（#RRGGBB / #AARRGGBB）。</summary>
        public string ForegroundHex
        {
            get => _foregroundHex;
            set
            {
                if (!SetProperty(ref _foregroundHex, value ?? string.Empty))
                {
                    return;
                }

                if (TryParseBrush(value, out var brush))
                {
                    Foreground = brush;
                    ColorParseError = null;
                }
                else
                {
                    ColorParseError = $"无法识别的颜色：{value}（请使用 #RRGGBB）";
                }

                OnPropertyChanged(nameof(ColorParseError));
            }
        }

        /// <summary>描边色。</summary>
        public string OutlineHex
        {
            get => _outlineHex;
            set
            {
                if (!SetProperty(ref _outlineHex, value ?? string.Empty))
                {
                    return;
                }

                if (TryParseBrush(value, out var brush))
                {
                    OutlineBrush = brush;
                    ColorParseError = null;
                }
                else
                {
                    ColorParseError = $"无法识别的颜色：{value}（请使用 #RRGGBB）";
                }

                OnPropertyChanged(nameof(ColorParseError));
            }
        }

        /// <summary>描边粗细（0 = 不描边）。</summary>
        public double OutlineThickness
        {
            get => _outlineThickness;
            set => SetProperty(ref _outlineThickness, Math.Clamp(value, 0, 20));
        }

        public Brush Foreground
        {
            get => _foreground;
            private set => SetProperty(ref _foreground, value);
        }

        public Brush OutlineBrush
        {
            get => _outlineBrush;
            private set => SetProperty(ref _outlineBrush, value);
        }

        /// <summary>颜色解析失败的提示，供界面显示。</summary>
        public string? ColorParseError { get; private set; }

        /// <summary>预设位置。</summary>
        public OverlayAnchor Anchor
        {
            get => _anchor;
            set => SetProperty(ref _anchor, value);
        }

        /// <summary>水平微调（DIP，正数向右）。</summary>
        public double OffsetX
        {
            get => _offsetX;
            set => SetProperty(ref _offsetX, value);
        }

        /// <summary>垂直微调（DIP，正数向下）。</summary>
        public double OffsetY
        {
            get => _offsetY;
            set => SetProperty(ref _offsetY, value);
        }

        /// <summary>字幕窗口宽度（DIP，会按屏幕宽度自动收敛）。</summary>
        public double OverlayWidth
        {
            get => _overlayWidth;
            set => SetProperty(ref _overlayWidth, Math.Clamp(value, 200, 20000));
        }

        /// <summary>字幕窗口高度（DIP）。</summary>
        public double OverlayHeight
        {
            get => _overlayHeight;
            set => SetProperty(ref _overlayHeight, Math.Clamp(value, 60, 2000));
        }

        /// <summary>是否显示字幕左上角的"移动手柄"（点一下就能解锁拖动，不用回主窗口）。</summary>
        public bool ShowHandle
        {
            get => _showHandle;
            set => SetProperty(ref _showHandle, value);
        }

        /// <summary>工具条平时自动隐藏，鼠标移到字幕上才浮出来（和网易云一样）。</summary>
        public bool AutoHideToolbar
        {
            get => _autoHideToolbar;
            set => SetProperty(ref _autoHideToolbar, value);
        }

        /// <summary>解锁鼠标穿透，可以直接拖动字幕窗口。</summary>
        public bool IsMovable
        {
            get => _isMovable;
            set => SetProperty(ref _isMovable, value);
        }

        /// <summary>自定义位置的 X（DIP，Anchor = Custom 时生效）。</summary>
        public double CustomX
        {
            get => _customX;
            set => SetProperty(ref _customX, value);
        }

        /// <summary>自定义位置的 Y（DIP）。</summary>
        public double CustomY
        {
            get => _customY;
            set => SetProperty(ref _customY, value);
        }

        /// <summary>把当前窗口位置记成自定义位置。</summary>
        public void SetCustomPosition(double x, double y)
        {
            MoveTo(x, y, persist: true);
        }

        /// <summary>
        /// 拖动过程中调用：只更新坐标并通知一次界面，不落盘（拖动结束再存）。
        /// 注意只对 CustomX 发通知，窗口那边据此重排一次即可，避免每帧两次 SetWindowPos。
        /// </summary>
        public void MoveTo(double x, double y, bool persist)
        {
            _customX = x;
            _customY = y;

            if (_anchor != OverlayAnchor.Custom)
            {
                _anchor = OverlayAnchor.Custom;
                OnPropertyChanged(nameof(Anchor));
            }

            OnPropertyChanged(nameof(CustomX));

            if (persist)
            {
                OnPropertyChanged(nameof(CustomY));
            }
        }

        /// <summary>是否显示悬浮字幕窗口。</summary>
        public bool IsVisible
        {
            get => _isVisible;
            set => SetProperty(ref _isVisible, value);
        }

        /// <summary>精选的常用字体（不想让下拉里塞进几百个系统字体，想加别的走"添加字体"）。</summary>
        public static IReadOnlyList<string> AvailableFonts { get; } = new[]
        {
            DefaultFontFamilyName,
            "Yu Gothic UI",
            "Meiryo",
            "MS Gothic",
            "Yu Mincho",
            "Microsoft YaHei UI",
            "Microsoft YaHei",
            "SimHei",
            "Source Han Sans SC",
            "Noto Sans CJK JP",
            "Segoe UI",
        };

        /// <summary>下拉里实际显示的字体（精选 + 扩展文件夹里发现的）。</summary>
        public System.Collections.ObjectModel.ObservableCollection<string> FontChoices { get; } =
            new System.Collections.ObjectModel.ObservableCollection<string>(AvailableFonts);

        /// <summary>字体名 → 字体家族（扩展文件夹里加载进来的，解析字体时要优先用它）。</summary>
        private static readonly Dictionary<string, FontFamily> ExtraFonts =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>扩展字体文件夹（用户指定；留空则用数据目录下的 fonts）。</summary>
        public string ExtraFontsFolder
        {
            get => _extraFontsFolder;
            set => SetProperty(ref _extraFontsFolder, value ?? string.Empty);
        }

        private string _extraFontsFolder = string.Empty;

        /// <summary>实际生效的扩展字体文件夹。</summary>
        public string EffectiveFontsFolder => string.IsNullOrWhiteSpace(ExtraFontsFolder)
            ? System.IO.Path.Combine(Core.AppPaths.DataDirectory, "fonts")
            : ExtraFontsFolder.Trim();

        /// <summary>
        /// 扫描扩展字体文件夹，把里面的字体加进下拉列表。
        /// 放进去就能用，不用装到系统里 —— 换电脑时把整个文件夹拷过去即可。
        /// </summary>
        public int ScanExtraFonts()
        {
            var folder = EffectiveFontsFolder;
            var added = 0;

            try
            {
                System.IO.Directory.CreateDirectory(folder);

                // 目录里没有字体文件就什么都不做（目录不存在也已经建好了）
                var hasFontFile = System.IO.Directory
                    .EnumerateFiles(folder)
                    .Any(f => f.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) ||
                              f.EndsWith(".otf", StringComparison.OrdinalIgnoreCase) ||
                              f.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase));

                if (!hasFontFile)
                {
                    return 0;
                }

                // WPF 能直接从目录里列出字体家族，不用自己解析 ttf
                // 注意：路径必须以分隔符结尾并转成 URI —— 否则 WPF 会把它当成"某个字体文件"，
                // 一个字体都读不出来（这个坑踩过）。
                var location = new Uri(folder.EndsWith(System.IO.Path.DirectorySeparatorChar)
                    ? folder
                    : folder + System.IO.Path.DirectorySeparatorChar);

                foreach (var family in Fonts.GetFontFamilies(location))
                {
                    var name = family.Source;
                    // 从目录加载的字体会带 "./#" 前缀（如 "./#Consolas"），显示时要去掉
                    if (name.StartsWith("./#", StringComparison.Ordinal))
                    {
                        name = name.Substring(3);
                    }

                    var hashIndex = name.LastIndexOf('#');
                    if (hashIndex >= 0)
                    {
                        name = name.Substring(hashIndex + 1);
                    }
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    ExtraFonts[name] = family;

                    if (!FontChoices.Any(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase)))
                    {
                        FontChoices.Add(name);
                        added++;
                    }
                }
            }
            catch (Exception)
            {
                // 文件夹不可读、字体损坏都不该影响其它功能
            }

            return added;
        }

        /// <summary>配置里存的字体名 → FontFamily（扩展字体优先）。</summary>
        /// <summary>解析字体名（扩展字体文件夹里的优先）。界面字体也用它。</summary>
        public static FontFamily ResolveFont(string name) => ResolveFontFamily(name);

        private static FontFamily ResolveFontFamily(string name) =>
            ExtraFonts.TryGetValue(name, out var extra) ? extra : new FontFamily(name);

        /// <summary>可供选择的描边色。</summary>
        public static IReadOnlyList<string> AvailableOutlineColors { get; } = new[]
        {
            "#000000", "#1A1A1A", "#3C3C3C", "#FFFFFF", "#5B2C6F", "#0B3D91",
        };

        /// <summary>可供选择的字色。</summary>
        public static IReadOnlyList<string> AvailableForegroundColors { get; } = new[]
        {
            "#FFFFFF", "#FFF3B0", "#FFE066", "#B3E5FC", "#C8E6C9", "#FFCDD2", "#000000",
        };

        // XAML 绑定不能走静态属性，这里提供实例访问器。

        public IReadOnlyList<string> ForegroundColorChoices => AvailableForegroundColors;

        public IReadOnlyList<string> OutlineColorChoices => AvailableOutlineColors;

        /// <summary>把位置重置为"主屏底部居中"。</summary>
        public void ResetPlacement()
        {
            Anchor = OverlayAnchor.Bottom;
            CustomX = 0;
            CustomY = 0;
            OffsetX = 0;
            OffsetY = 0;
            OverlayWidth = 1400;
            OverlayHeight = 220;
        }

        /// <summary>把外观重置为默认值。</summary>
        public void ResetAppearance()
        {
            FontFamilyName = DefaultFontFamilyName;
            FontSize = 32;
            FontWeightName = nameof(System.Windows.FontWeights.Normal);
            SecondaryText = string.Empty;
            ForegroundHex = "#FFFFFF";
            OutlineHex = "#000000";
            OutlineThickness = 3.5;
        }

        private static bool TryParseBrush(string? value, out Brush brush)
        {
            brush = Brushes.White;

            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            try
            {
                var color = (Color)ColorConverter.ConvertFromString(value.Trim())!;
                var solid = new SolidColorBrush(color);
                solid.Freeze();
                brush = solid;
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
            catch (InvalidCastException)
            {
                return false;
            }
            catch (NotSupportedException)
            {
                return false;
            }
        }

        /// <summary>把颜色格式化为 #RRGGBB，便于界面显示。</summary>
        public static string FormatColor(Color color)
        {
            return string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", color.R, color.G, color.B);
        }
    }
}
