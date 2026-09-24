using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace AudioBookPlayer.ViewModels
{
    /// <summary>
    /// 配色方案里的一个颜色槽（背景 / 强调 / 金色 / 文字）。
    ///
    /// 每个槽自带一块**常用色调色盘**：点小色块直接换色，点大色块则打开系统调色板挑任意颜色，
    /// 旁边还能直接改十六进制值。比原来"一排预设 + 一个输入框"直观得多。
    /// </summary>
    public sealed class ThemeColorSlot : ObservableObject
    {
        private readonly Action<string> _apply;
        private string _hex;

        public ThemeColorSlot(string key, string title, string description, string hex, Action<string> apply, bool isLightColor = false)
        {
            Key = key;
            Title = title;
            Description = description;
            _hex = hex;
            _apply = apply;
            IsLightColor = isLightColor; // 用来决定大色块上显示黑字还是白字
        }

        /// <summary>内部标识（background / accent / gold / foreground）。</summary>
        public string Key { get; }

        public string Title { get; }

        public string Description { get; }

        /// <summary>大色块上的文字该用深色还是浅色。</summary>
        public bool IsLightColor { get; }

        /// <summary>当前颜色的十六进制值（双向绑定）。</summary>
        public string Hex
        {
            get => _hex;
            set
            {
                var normalized = Normalize(value);
                if (normalized == null || !SetProperty(ref _hex, normalized))
                {
                    return;
                }

                OnPropertyChanged(nameof(Brush));
                OnPropertyChanged(nameof(LabelBrush));
                OnPropertyChanged(nameof(Color));
                _apply(normalized);
            }
        }

        /// <summary>当前颜色（给调色盘用，和 <see cref="Hex"/> 双向同步）。</summary>
        public Color Color
        {
            get => ToColor(_hex);
            set
            {
                var hex = $"#{value.R:X2}{value.G:X2}{value.B:X2}";
                Hex = hex;
            }
        }

        /// <summary>大色块用的画刷。</summary>
        public Brush Brush => ToBrush(_hex);

        /// <summary>大色块上文字的颜色（浅色底用深字）。</summary>
        public Brush LabelBrush => IsLightColor ? Brushes.Black : Brushes.White;

        /// <summary>从外部（切换预设）同步颜色，不触发回写。</summary>
        public void SyncFrom(string? hex)
        {
            var normalized = Normalize(hex);
            if (normalized == null || normalized == _hex)
            {
                return;
            }

            _hex = normalized;
            OnPropertyChanged(nameof(Hex));
            OnPropertyChanged(nameof(Brush));
            OnPropertyChanged(nameof(LabelBrush));
            OnPropertyChanged(nameof(Color));
        }


        public override string ToString() => $"{Title} {_hex}";

        private static string? Normalize(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var text = value.Trim();
            if (!text.StartsWith('#'))
            {
                text = "#" + text;
            }

            try
            {
                var color = (Color)ColorConverter.ConvertFromString(text)!;
                return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            }
            catch (Exception)
            {
                return null; // 输入不合法就当没改
            }
        }

        private static Color ToColor(string hex)
        {
            try
            {
                return (Color)ColorConverter.ConvertFromString(hex)!;
            }
            catch (Exception)
            {
                return Colors.Transparent;
            }
        }

        private static Brush ToBrush(string hex)
        {
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(hex)!;
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                return brush;
            }
            catch (Exception)
            {
                return Brushes.Transparent;
            }
        }
    }
}
