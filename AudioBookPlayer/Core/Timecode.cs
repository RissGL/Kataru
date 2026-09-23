using System;

namespace AudioBookPlayer.Core
{
    /// <summary>
    /// 时间轴格式化工具。不使用 TimeSpan 的 "hh" 格式，避免超过 24 小时的有声书被截断。
    /// </summary>
    public static class Timecode
    {
        /// <summary>格式化为 HH:MM:SS（小时可以超过 24）。</summary>
        public static string Format(TimeSpan value)
        {
            if (value < TimeSpan.Zero)
            {
                value = TimeSpan.Zero;
            }

            var hours = (int)value.TotalHours;
            return $"{hours:00}:{value.Minutes:00}:{value.Seconds:00}";
        }

        /// <summary>格式化为 HH:MM:SS,mmm。</summary>
        public static string FormatWithMilliseconds(TimeSpan value)
        {
            if (value < TimeSpan.Zero)
            {
                value = TimeSpan.Zero;
            }

            var hours = (int)value.TotalHours;
            return $"{hours:00}:{value.Minutes:00}:{value.Seconds:00},{value.Milliseconds:000}";
        }
    }
}
