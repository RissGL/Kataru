using System;

namespace AudioBookPlayer.Core
{
    /// <summary>
    /// 一条字幕（对应 .srt 文件中的一个条目）。
    /// </summary>
    public sealed class SubtitleLine
    {
        /// <summary>字幕序号（SRT 文件中的第一行数字，缺失时由解析器自动编号）。</summary>
        public int Index { get; set; }

        /// <summary>开始时间（相对音频起点）。</summary>
        public TimeSpan StartTime { get; set; }

        /// <summary>结束时间（相对音频起点）。</summary>
        public TimeSpan EndTime { get; set; }

        /// <summary>
        /// 字幕文本。多行字幕保留换行符（\n），由显示层决定如何排版。
        /// </summary>
        public string Text { get; set; } = string.Empty;

        /// <summary>该条字幕的持续时间。</summary>
        public TimeSpan Duration => EndTime - StartTime;

        /// <summary>判断给定播放位置是否落在本条字幕的时间区间内（左闭右开）。</summary>
        public bool IsVisibleAt(TimeSpan position)
        {
            return position >= StartTime && position < EndTime;
        }

        public override string ToString()
        {
            var text = Text.Replace("\r", string.Empty).Replace("\n", " / ");
            return $"#{Index} {Timecode.FormatWithMilliseconds(StartTime)} --> {Timecode.FormatWithMilliseconds(EndTime)}  {text}";
        }
    }
}
