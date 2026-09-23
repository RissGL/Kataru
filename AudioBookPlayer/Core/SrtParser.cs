using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace AudioBookPlayer.Core
{
    /// <summary>
    /// SRT 解析结果。解析器对坏数据采取"尽力而为"策略：能救的救回来并记录 Warning，而不是直接抛异常。
    /// </summary>
    public sealed class SrtParseResult
    {
        public SrtParseResult(IReadOnlyList<SubtitleLine> lines, IReadOnlyList<string> warnings, string encodingName)
        {
            Lines = lines;
            Warnings = warnings;
            EncodingName = encodingName;
        }

        /// <summary>按开始时间升序排列的字幕列表。</summary>
        public IReadOnlyList<SubtitleLine> Lines { get; }

        /// <summary>解析过程中的警告（编码问题、坏时间轴、时间倒挂等）。</summary>
        public IReadOnlyList<string> Warnings { get; }

        /// <summary>实际使用的编码名称，用于界面提示。</summary>
        public string EncodingName { get; }

        public bool HasWarnings => Warnings.Count > 0;
    }

    /// <summary>
    /// .srt → List&lt;SubtitleLine&gt; 解析器。
    ///
    /// 支持：HH:MM:SS,mmm 与 HH:MM:SS.mmm、多行字幕、空行、UTF-8、UTF-8 BOM、UTF-16 BOM、
    /// 日文 / 中文 Unicode、缺失序号、时间轴后面的坐标后缀、{ASS 标签} 与 &lt;i&gt; 之类的残留标签。
    /// 不支持 ASS/SSA（不在 MVP 范围内）。
    /// </summary>
    public static class SrtParser
    {
        /// <summary>匹配 "00:00:01,500 --> 00:00:04,200"（允许小数点、单位数毫秒、行尾附加内容）。</summary>
        private static readonly Regex TimeLineRegex = new Regex(
            @"^\s*(?<h1>\d+)\s*:\s*(?<m1>\d{1,2})\s*:\s*(?<s1>\d{1,2})\s*[,.](?<f1>\d{1,3})\s*-->\s*" +
            @"(?<h2>\d+)\s*:\s*(?<m2>\d{1,2})\s*:\s*(?<s2>\d{1,2})\s*[,.](?<f2>\d{1,3})",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>ASS/SSA 覆盖标签，例如 {\an8}、{\pos(1,2)}。</summary>
        private static readonly Regex AssOverrideRegex = new Regex(@"\{[^{}]*\}", RegexOptions.Compiled);

        /// <summary>SRT 里常见的 HTML 风格标签，仅保留纯文本。</summary>
        private static readonly Regex HtmlTagRegex = new Regex(
            @"</?\s*(?:i|b|u|s|em|strong|font|ruby|rt|rp|span|c|v)(?:\s[^<>]*)?/?>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>从文件解析（自动识别 BOM 与 UTF-8）。</summary>
        public static SrtParseResult ParseFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("字幕路径为空。", nameof(path));
            }

            if (!File.Exists(path))
            {
                throw new FileNotFoundException("找不到字幕文件。", path);
            }

            var bytes = File.ReadAllBytes(path);
            var (text, encodingName, warning) = DecodeText(bytes);
            var result = Parse(text, encodingName);

            if (warning == null)
            {
                return result;
            }

            var warnings = new List<string>(result.Warnings) { warning };
            return new SrtParseResult(result.Lines, warnings, result.EncodingName);
        }

        /// <summary>从字符串解析。</summary>
        public static SrtParseResult Parse(string content, string encodingName = "UTF-8")
        {
            var lines = new List<SubtitleLine>();
            var warnings = new List<string>();

            if (string.IsNullOrEmpty(content))
            {
                warnings.Add("字幕内容为空。");
                return new SrtParseResult(lines, warnings, encodingName);
            }

            var raw = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var autoIndex = 1;
            var i = 0;

            while (i < raw.Length)
            {
                if (IsBlank(raw[i]))
                {
                    i++;
                    continue;
                }

                // 序号行是可选的：只有"数字 + 下一行是时间轴"才认为它是序号。
                var index = autoIndex;
                if (TryParseIndex(raw[i], out var parsedIndex) &&
                    i + 1 < raw.Length && TimeLineRegex.IsMatch(raw[i + 1]))
                {
                    index = parsedIndex;
                    i++;
                }

                if (i >= raw.Length || !TimeLineRegex.IsMatch(raw[i]))
                {
                    warnings.Add($"第 {i + 1} 行不是合法的时间轴，已跳过：{Ellipsis(raw[i])}");
                    i++;
                    continue;
                }

                var match = TimeLineRegex.Match(raw[i]);
                i++;
                if (!TryParseTime(match, "1", out var start) || !TryParseTime(match, "2", out var end))
                {
                    warnings.Add($"第 {i} 行的时间无法解析，已跳过：{Ellipsis(match.Value)}");
                    continue;
                }

                if (end <= start)
                {
                    warnings.Add($"第 {i} 行结束时间不大于开始时间，已修正 +0.5s：{Ellipsis(match.Value)}");
                    end = start + TimeSpan.FromMilliseconds(500);
                }

                // 收集正文，直到空行或"下一条的序号 + 时间轴"。
                var textStart = i;
                while (i < raw.Length && !IsBlank(raw[i]))
                {
                    if (TryParseIndex(raw[i], out _) &&
                        i + 1 < raw.Length && TimeLineRegex.IsMatch(raw[i + 1]))
                    {
                        break; // 文件块之间没有空行时的容错
                    }

                    i++;
                }

                var text = CleanText(raw, textStart, i);
                if (text.Length == 0)
                {
                    warnings.Add($"第 {textStart + 1} 行附近没有字幕文本，已跳过。");
                    continue;
                }

                lines.Add(new SubtitleLine
                {
                    Index = index,
                    StartTime = start,
                    EndTime = end,
                    Text = text,
                });
                autoIndex++;
            }

            if (lines.Count == 0)
            {
                warnings.Add("没有解析出任何字幕条目。");
            }

            // 绝大多数 SRT 本来就是有序的；只有乱序文件才排序，并给出提示。
            var outOfOrder = false;
            for (var n = 1; n < lines.Count; n++)
            {
                if (lines[n].StartTime < lines[n - 1].StartTime)
                {
                    outOfOrder = true;
                    break;
                }
            }

            if (outOfOrder)
            {
                lines.Sort((a, b) => a.StartTime.CompareTo(b.StartTime));
                warnings.Add("字幕时间轴顺序错乱，已按开始时间重新排序。");
            }

            return new SrtParseResult(lines, warnings, encodingName);
        }

        /// <summary>判断一行文本是否是合法的 SRT 时间轴。</summary>
        public static bool IsTimeLine(string line)
        {
            return line != null && TimeLineRegex.IsMatch(line);
        }

        private static (string Text, string EncodingName, string? Warning) DecodeText(byte[] bytes)
        {
            // UTF-8 BOM
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                return (new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3), "UTF-8 (BOM)", null);
            }

            // UTF-16 BOM
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), "UTF-16 LE", null);
            }

            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                return (Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), "UTF-16 BE", null);
            }

            // 严格 UTF-8 解码，失败说明是别的编码（例如 Shift-JIS / GBK）。
            // 本项目不引入额外编码包，因此退化为宽松 UTF-8 并给出提示。
            try
            {
                var strict = new UTF8Encoding(false, throwOnInvalidBytes: true);
                return (strict.GetString(bytes), "UTF-8", null);
            }
            catch (DecoderFallbackException)
            {
                var lenient = new UTF8Encoding(false).GetString(bytes);
                return (lenient, "UTF-8 (含无效字节)", "字幕文件不是有效的 UTF-8（可能是 Shift-JIS / GBK），部分字符可能显示异常。建议转存为 UTF-8。");
            }
        }

        private static string CleanText(string[] raw, int start, int end)
        {
            var kept = new List<string>(Math.Max(0, end - start));
            for (var n = start; n < end; n++)
            {
                var line = raw[n];

                line = AssOverrideRegex.Replace(line, string.Empty);
                line = HtmlTagRegex.Replace(line, string.Empty);

                // ASS 风格的硬回车 / 硬空格在 SRT 里偶尔出现
                line = line.Replace("\\N", "\n").Replace("\\n", "\n").Replace("\\h", " ");

                kept.Add(line.TrimEnd());
            }

            // 去掉首尾空行，中间的空行保留（多行字幕的排版由显示层决定）
            var first = 0;
            var last = kept.Count - 1;
            while (first <= last && kept[first].Trim().Length == 0)
            {
                first++;
            }

            while (last >= first && kept[last].Trim().Length == 0)
            {
                last--;
            }

            if (first > last)
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            for (var n = first; n <= last; n++)
            {
                if (n > first)
                {
                    sb.Append('\n');
                }

                sb.Append(kept[n].Trim());
            }

            return sb.ToString();
        }

        private static bool TryParseTime(Match match, string suffix, out TimeSpan value)
        {
            value = TimeSpan.Zero;

            var hours = match.Groups["h" + suffix].Value;
            var minutes = match.Groups["m" + suffix].Value;
            var seconds = match.Groups["s" + suffix].Value;
            var fraction = match.Groups["f" + suffix].Value;

            if (!int.TryParse(hours, NumberStyles.None, CultureInfo.InvariantCulture, out var h) ||
                !int.TryParse(minutes, NumberStyles.None, CultureInfo.InvariantCulture, out var m) ||
                !int.TryParse(seconds, NumberStyles.None, CultureInfo.InvariantCulture, out var s))
            {
                return false;
            }

            var ms = 0;
            if (fraction.Length > 0 &&
                int.TryParse(fraction, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedFraction))
            {
                // 1 位 = 十分之一秒，2 位 = 百分之一秒，3 位 = 毫秒
                ms = fraction.Length switch
                {
                    1 => parsedFraction * 100,
                    2 => parsedFraction * 10,
                    _ => parsedFraction,
                };
            }

            value = new TimeSpan(0, h, m, s) + TimeSpan.FromMilliseconds(ms);
            return true;
        }

        private static bool TryParseIndex(string line, out int index)
        {
            index = 0;
            var trimmed = line.Trim();
            return trimmed.Length > 0 &&
                   trimmed.Length <= 9 &&
                   int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out index);
        }

        private static bool IsBlank(string line)
        {
            return string.IsNullOrWhiteSpace(line);
        }

        private static string Ellipsis(string text)
        {
            var value = (text ?? string.Empty).Trim();
            return value.Length <= 60 ? value : value.Substring(0, 60) + "…";
        }
    }
}
