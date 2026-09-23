using System;
using System.Collections.Generic;
using System.IO;

namespace AudioBookPlayer.Core
{
    /// <summary>
    /// 一条字幕轨（一种语言）。
    /// </summary>
    public sealed class SubtitleTrack
    {
        public SubtitleTrack(string filePath, string languageCode, string displayName)
        {
            FilePath = filePath;
            LanguageCode = languageCode;
            DisplayName = displayName;
        }

        /// <summary>字幕文件路径。</summary>
        public string FilePath { get; }

        /// <summary>语言代码（ja / zh / en / ""=没标语言）。</summary>
        public string LanguageCode { get; }

        /// <summary>界面显示名（日本語 / 中文 / English / 原文）。</summary>
        public string DisplayName { get; }

        /// <summary>列表里显示的"语言 · 文件名"。</summary>
        public string Description => $"{DisplayName} · {Path.GetFileName(FilePath)}";

        public override string ToString() => Description;
    }

    /// <summary>
    /// 从字幕文件名里认语言。
    ///
    /// 支持常见写法：<c>第01话.zh.srt</c>、<c>第01话.chs.srt</c>、<c>第01话.zh-CN.srt</c>、
    /// <c>第01话.ja.srt</c>、<c>第01话.中文.srt</c>、<c>第01话.eng.srt</c>；
    /// 没有任何语言标记的（<c>第01话.srt</c>）当成"原文"。
    /// </summary>
    public static class SubtitleLanguage
    {
        public const string UnknownCode = "";
        public const string UnknownName = "原文";

        private static readonly Dictionary<string, (string Code, string Name)> Aliases =
            new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
            {
                // 日语
                ["ja"] = ("ja", "日本語"),
                ["jp"] = ("ja", "日本語"),
                ["jpn"] = ("ja", "日本語"),
                ["japanese"] = ("ja", "日本語"),
                ["日"] = ("ja", "日本語"),
                ["日语"] = ("ja", "日本語"),
                ["日語"] = ("ja", "日本語"),
                ["日本語"] = ("ja", "日本語"),

                // 中文
                ["zh"] = ("zh", "中文"),
                ["cn"] = ("zh", "中文"),
                ["chs"] = ("zh", "简体中文"),
                ["cht"] = ("zh", "繁體中文"),
                ["chi"] = ("zh", "中文"),
                ["zho"] = ("zh", "中文"),
                ["chinese"] = ("zh", "中文"),
                ["sc"] = ("zh", "简体中文"),
                ["tc"] = ("zh", "繁體中文"),
                ["zhcn"] = ("zh", "简体中文"),
                ["zhtw"] = ("zh", "繁體中文"),
                ["zhhans"] = ("zh", "简体中文"),
                ["zhhant"] = ("zh", "繁體中文"),
                ["中"] = ("zh", "中文"),
                ["中文"] = ("zh", "中文"),
                ["简"] = ("zh", "简体中文"),
                ["繁"] = ("zh", "繁體中文"),
                ["简体"] = ("zh", "简体中文"),
                ["繁体"] = ("zh", "繁體中文"),
                ["简体中文"] = ("zh", "简体中文"),
                ["繁體中文"] = ("zh", "繁體中文"),

                // 英语
                ["en"] = ("en", "English"),
                ["eng"] = ("en", "English"),
                ["english"] = ("en", "English"),
                ["英"] = ("en", "English"),
                ["英语"] = ("en", "English"),
                ["英文"] = ("en", "English"),

                // 韩语
                ["ko"] = ("ko", "한국어"),
                ["kr"] = ("ko", "한국어"),
                ["kor"] = ("ko", "한국어"),
                ["korean"] = ("ko", "한국어"),
                ["韩"] = ("ko", "한국어"),
                ["韩语"] = ("ko", "한국어"),
                ["한국어"] = ("ko", "한국어"),
            };

        /// <summary>界面上可以选的常用语言（"原文"永远在最前）。</summary>
        public static IReadOnlyList<(string Code, string Name)> Common { get; } = new[]
        {
            (UnknownCode, UnknownName),
            ("ja", "日本語"),
            ("zh", "中文"),
            ("en", "English"),
            ("ko", "한국어"),
        };

        public static string GetDisplayName(string languageCode)
        {
            foreach (var (code, name) in Common)
            {
                if (string.Equals(code, languageCode, StringComparison.OrdinalIgnoreCase))
                {
                    return name;
                }
            }

            return string.IsNullOrEmpty(languageCode) ? UnknownName : languageCode.ToUpperInvariant();
        }

        /// <summary>
        /// 判断某个字幕文件是不是这条音频的（同名，或者同名 + 语言后缀）。
        /// </summary>
        public static bool IsRelatedSubtitle(string audioStem, string subtitleStem)
        {
            if (string.IsNullOrEmpty(audioStem) || string.IsNullOrEmpty(subtitleStem))
            {
                return false;
            }

            if (string.Equals(audioStem, subtitleStem, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (subtitleStem.Length <= audioStem.Length ||
                !subtitleStem.StartsWith(audioStem, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // 后面必须紧跟分隔符，避免 01话 匹配到 01话extra
            var separator = subtitleStem[audioStem.Length];
            return separator is '.' or '-' or '_' or ' ' or '(' or '[' or '（' or '【';
        }

        /// <summary>从字幕文件名里解析语言（拿不到就返回"原文"）。</summary>
        public static (string Code, string Name) Detect(string audioStem, string subtitlePath)
        {
            var subtitleStem = Path.GetFileNameWithoutExtension(subtitlePath);

            if (string.Equals(audioStem, subtitleStem, StringComparison.OrdinalIgnoreCase))
            {
                return (UnknownCode, UnknownName);
            }

            var tag = subtitleStem.StartsWith(audioStem, StringComparison.OrdinalIgnoreCase)
                ? subtitleStem.Substring(audioStem.Length)
                : subtitleStem;

            tag = Normalize(tag);

            if (tag.Length == 0)
            {
                return (UnknownCode, UnknownName);
            }

            if (Aliases.TryGetValue(tag, out var known))
            {
                return known;
            }

            // zh-CN / zh_TW / zh.Hans 这类：去掉分隔符再试一次
            var compact = tag.Replace("-", string.Empty).Replace("_", string.Empty).Replace(".", string.Empty);
            if (compact.Length > 0 && Aliases.TryGetValue(compact, out var compactKnown))
            {
                return compactKnown;
            }

            // 认不出来的标记就原样显示，至少用户能区分
            return (tag.ToLowerInvariant(), tag.ToUpperInvariant());
        }

        private static string Normalize(string tag)
        {
            var trimmed = tag.Trim();
            while (trimmed.Length > 0 && (trimmed[0] == '.' || trimmed[0] == '-' || trimmed[0] == '_' || trimmed[0] == ' '))
            {
                trimmed = trimmed.Substring(1);
            }

            while (trimmed.Length > 0 && (trimmed[^1] == ')' || trimmed[^1] == ']' || trimmed[^1] == '）' || trimmed[^1] == '】'))
            {
                trimmed = trimmed.Substring(0, trimmed.Length - 1);
            }

            return trimmed.Trim();
        }
    }
}
