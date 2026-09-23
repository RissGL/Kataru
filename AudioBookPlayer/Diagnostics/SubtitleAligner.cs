using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using AudioBookPlayer.Core;

namespace AudioBookPlayer.Diagnostics
{
    /// <summary>
    /// 把中文翻译"贴"到日文有声书字幕的时间轴上。
    ///
    /// 背景：有声书的 SRT 是逐句朗读切出来的（一段话被切成好几个字幕块），
    /// 时间轴只对得上日文；中文小说是另一个译本、没有时间轴。
    /// 两边内容是同一本书，所以按**顺序**做序列对齐：把日文和中方各自切成句子，
    /// 用「长度比例 + 句末标点 + 是否对话」做代价，跑一次带状/柱搜索对齐，
    /// 再把中文句子按字数摊回它覆盖的那几个字幕块上。
    ///
    /// 这是离线数据加工工具，只在命令行调用：
    ///   KATARU.exe --align --ja &lt;日文.srt&gt; --zh &lt;中文.txt&gt; --out &lt;输出.zh.srt&gt; [--limit N] [--report 文件]
    /// </summary>
    public static class SubtitleAligner
    {
        /// <summary>一句话最长多少字（超过就强制断开，避免一个字幕块吃下整段）。</summary>
        private const int MaxSentenceLength = 200;

        public static int Run(string[] args)
        {
            var jaPath = UiSnapshot.GetOption(args, "--ja");
            var zhPath = UiSnapshot.GetOption(args, "--zh");
            var outPath = UiSnapshot.GetOption(args, "--out");
            var reportPath = UiSnapshot.GetOption(args, "--report");
            var limitText = UiSnapshot.GetOption(args, "--limit");
            var zhStartValue = 0;
            var limit = int.TryParse(limitText, out var parsed) ? parsed : int.MaxValue;

            if (string.IsNullOrEmpty(jaPath) || string.IsNullOrEmpty(zhPath) || string.IsNullOrEmpty(outPath))
            {
                Console.WriteLine("用法: --align --ja <日文.srt> --zh <中文.txt> --out <输出.zh.srt> [--limit N] [--report 文件]");
                return 2;
            }

            var cues = SrtParser.ParseFile(jaPath).Lines;
            Console.WriteLine($"日文字幕块: {cues.Count}");

            var jaSentences = BuildJaSentences(cues);
            Console.WriteLine($"日文句子: {jaSentences.Count}");

            var zhSentences = new List<string>();
            foreach (var onePath in zhPath.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!File.Exists(onePath))
                {
                    Console.WriteLine($"找不到中文文件: {onePath}");
                    continue;
                }

                var part = BuildZhSentences(File.ReadAllText(onePath, Encoding.UTF8));
                Console.WriteLine($"  {Path.GetFileName(onePath)}: {part.Count} 句");
                zhSentences.AddRange(part);
            }

            Console.WriteLine($"中文句子: {zhSentences.Count}");

            // --zh-start N：从中文第 N 句开始（有声书后一卷要接着上一卷的位置）
            var zhStartText = UiSnapshot.GetOption(args, "--zh-start");
            if (int.TryParse(zhStartText, out var zhStart) && zhStart > 0)
            {
                zhStartValue = zhStart;
                zhStart = Math.Min(zhStart, zhSentences.Count);
                zhSentences = zhSentences.Skip(zhStart).ToList();
                Console.WriteLine($"中文起点: 跳过前 {zhStart} 句，剩 {zhSentences.Count} 句");
            }

            if (limit != int.MaxValue)
            {
                jaSentences = jaSentences.Take(limit).ToList();
                zhSentences = zhSentences.Take(Math.Min(zhSentences.Count, (int)(limit * 1.4))).ToList();
                Console.WriteLine($"（限制模式：日文 {jaSentences.Count} / 中文 {zhSentences.Count}）");
            }

            var jaChars = jaSentences.Sum(s => s.Text.Length);
            var zhChars = zhSentences.Sum(s => s.Length);
            var expectedRatio = jaChars > 0 ? (double)zhChars / jaChars : 1.0;
            Console.WriteLine($"总字数 日 {jaChars} / 中 {zhChars} → 期望长度比 {expectedRatio:0.000}");

            var pairs = Align(jaSentences, zhSentences, expectedRatio);
            Console.WriteLine($"对齐结果: {pairs.Count} 组（日文 {pairs.Count(p => p.JaIndex >= 0)} / 中文 {pairs.Count(p => p.ZhIndex >= 0)}）");

            // 把中文写回字幕块
            var texts = DistributeBack(cues, jaSentences, zhSentences, pairs);

            WriteSrt(outPath, cues, texts);
            Console.WriteLine($"已输出: {outPath}");

            var lastZh = pairs.Where(p => p.ZhIndex >= 0).Select(p => p.ZhIndex).DefaultIfEmpty(-1).Max();
            Console.WriteLine($"ZH-END={(zhStartValue + lastZh + 1)}");

            if (!string.IsNullOrEmpty(reportPath))
            {
                WriteReport(reportPath, jaSentences, zhSentences, pairs);
                Console.WriteLine($"对齐抽样: {reportPath}");
            }

            return 0;
        }

        // ---------------- 切句 ----------------

        public sealed class JaSentence
        {
            public int FirstCue;
            public int LastCue;
            public string Text = string.Empty;
            public bool IsDialogue;
            public char EndPunctuation;
        }

        /// <summary>把日文字幕块合并成句子（一个句子可能横跨好几个块）。</summary>
        public static List<JaSentence> BuildJaSentencesForChunks(IReadOnlyList<SubtitleLine> cues) => BuildJaSentences(cues);

        private static List<JaSentence> BuildJaSentences(IReadOnlyList<SubtitleLine> cues)
        {
            var result = new List<JaSentence>();
            var buffer = new StringBuilder();
            var first = -1;
            var last = -1;

            for (var i = 0; i < cues.Count; i++)
            {
                var text = cues[i].Text.Replace("\r", " ").Replace("\n", " ").Trim();
                if (text.Length == 0)
                {
                    continue;
                }

                if (first < 0)
                {
                    first = i;
                }

                last = i;
                buffer.Append(text);

                var current = System.Text.RegularExpressions.Regex.Replace(buffer.ToString(), @"^\d{1,4}[　\s]+", string.Empty);
                if (IsSentenceEnd(current) || current.Length >= MaxSentenceLength)
                {
                    result.Add(new JaSentence
                    {
                        FirstCue = first,
                        LastCue = last,
                        Text = current,
                        IsDialogue = current.Contains('「'),
                        EndPunctuation = LastPunctuation(current),
                    });

                    buffer.Clear();
                    first = -1;
                    last = -1;
                }
            }

            if (buffer.Length > 0 && first >= 0)
            {
                var tail = buffer.ToString();
                result.Add(new JaSentence
                {
                    FirstCue = first,
                    LastCue = last,
                    Text = tail,
                    IsDialogue = tail.Contains('「'),
                    EndPunctuation = LastPunctuation(tail),
                });
            }

            return result;
        }

        /// <summary>把中文小说切成句子（按行读，行内再按句末标点切）。</summary>
        public static List<string> BuildZhSentencesForChunks(string content) => BuildZhSentences(content);

        private static List<string> BuildZhSentences(string content)
        {
            var result = new List<string>();
            var buffer = new StringBuilder();

            // 正文之前的目录 / 简介 / 版权页在日文有声书里是没有的，
            // 留着会让整条对齐错位好几句。正文第一段带段落编号（００１），拿它当起点门槛。
            var bodyStarted = false;

            foreach (var rawLine in content.Split('\n'))
            {
                var line = rawLine.Trim().TrimEnd('\r');
                if (line.Length == 0)
                {
                    Flush();
                    continue;
                }

                if (!bodyStarted)
                {
                    if (System.Text.RegularExpressions.Regex.IsMatch(line, @"^[0-9０-９]{1,4}$"))
                    {
                        bodyStarted = true;
                    }
                    else
                    {
                        continue; // 前置内容整段丢掉
                    }
                }

                // 明显的元信息 / 目录行跳过
                if (IsMetaLine(line))
                {
                    Flush();
                    continue;
                }

                foreach (var ch in line)
                {
                    buffer.Append(ch);
                    if (IsEndChar(ch) || buffer.Length >= MaxSentenceLength)
                    {
                        Flush();
                    }
                }

                Flush();
            }

            Flush();
            return result;

            void Flush()
            {
                if (buffer.Length == 0)
                {
                    return;
                }

                var sentence = buffer.ToString().Trim();
                buffer.Clear();

                if (sentence.Length == 0 || IsMetaLine(sentence))
                {
                    return;
                }

                result.Add(sentence);
            }
        }

        private static bool IsMetaLine(string line)
        {
            if (line.Length <= 1)
            {
                return true;
            }

            // 制作组信息 / 论坛标语
            string[] noise =
            {
                "SOSG", "轻之国度", "ePub", "EPUB", "首发于", "转载", "仅供", "禁止", "录入",
                "校对", "扫图", "修图", "排版", "制作", "下载后", "商业用途", "论坛", "http",
                "化物语（上）", "化物语(上)", "Vol.", "台版", "译者", "译者", "作者：", "插画",
            };

            foreach (var word in noise)
            {
                if (line.Contains(word, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            // 段落编号（００１ / 001）与分隔符
            var stripped = line.Trim().Replace("　", string.Empty).Replace(" ", string.Empty);
            if (stripped.Length > 0 && stripped.All(ch => char.IsDigit(ch) || ch == '*' || ch == '＊' || ch == '≡' || ch == '-' || ch == '—'))
            {
                return true;
            }

            // 单独的章节标题（目录和正文各有一份，留着会重复）
            if (System.Text.RegularExpressions.Regex.IsMatch(line, @"^第[一二三四五六七八九十百零\d]+[话話章節节][\s　]"))
            {
                return true;
            }

            // 目录里的"后记"之类
            if (line is "后记" or "後記" or "序" or "序章" or "终章" or "終章")
            {
                return true;
            }

            if (line.StartsWith("≡") || line.StartsWith("---") || line.StartsWith("录入") ||
                line.StartsWith("校对") || line.StartsWith("扫图") || line.StartsWith("修图") ||
                line.StartsWith("排版") || line.StartsWith("作者") || line.StartsWith("插画") ||
                line.StartsWith("译者") || line.StartsWith("制作信息") || line.StartsWith("[自录]"))
            {
                return true;
            }

            // 目录 / 章节标题单独成句
            return false;
        }

        // 只在真正的句末标点处断句：两边粒度必须一致，否则中文会被切得比日文碎得多
        private static bool IsEndChar(char ch) => ch is '。' or '！' or '？' or '!' or '?';

        private static bool IsSentenceEnd(string text)
        {
            var trimmed = text.TrimEnd('」', '』', '"', '）', ')', ' ');
            if (trimmed.Length == 0)
            {
                return false;
            }

            var last = trimmed[^1];
            if (last is '。' or '！' or '？' or '!' or '?')
            {
                return true;
            }

            // …… 结尾也算
            return trimmed.Length >= 2 && trimmed[^2] == '…' && last == '…';
        }

        private static char LastPunctuation(string text)
        {
            var trimmed = text.TrimEnd('」', '』', '"', '）', ')', ' ');
            for (var i = trimmed.Length - 1; i >= 0; i--)
            {
                if (trimmed[i] is '。' or '！' or '？' or '…' or '!' or '?' or '、' or '，' or ',')
                {
                    return trimmed[i];
                }
            }

            return '\0';
        }

        // ---------------- 对齐 ----------------

        public sealed class Pair
        {
            public int JaIndex = -1;
            public int ZhIndex = -1;
        }

        /// <summary>
        /// 柱搜索对齐：状态 (i, j) 表示"日文前 i 句 ↔ 中文前 j 句已配好"。
        /// 每步允许 1:1 / 1:2 / 2:1 / 1:0 / 0:1，代价由长度比例、句末标点、是否对话决定，
        /// 再加一个"别偏离对角线太远"的惩罚，防止越走越歪。
        /// </summary>
        private static List<Pair> Align(List<JaSentence> ja, List<string> zh, double expectedRatio)
        {
            const int beamWidth = 24;

            var start = new List<State> { new State(0, 0, 0, null, null) };
            var beam = start;

            while (beam.Count > 0)
            {
                if (beam.Any(s => s.I >= ja.Count))
                {
                    break;
                }

                var next = new List<State>();
                foreach (var state in beam)
                {
                    if (state.I >= ja.Count)
                    {
                        next.Add(state);
                        continue;
                    }

                    // 1:1
                    if (state.I < ja.Count && state.J < zh.Count)
                    {
                        next.Add(new State(state.I + 1, state.J + 1,
                            state.Cost + PairCost(ja[state.I], zh[state.J], expectedRatio), state, new Pair { JaIndex = state.I, ZhIndex = state.J }));
                    }

                    // 1:2（日文一句 ↔ 中文两句，翻译把长句拆开了）
                    if (state.I < ja.Count && state.J + 1 < zh.Count)
                    {
                        var merged = zh[state.J] + zh[state.J + 1];
                        next.Add(new State(state.I + 1, state.J + 2,
                            state.Cost + PairCost(ja[state.I], merged, expectedRatio) + 0.6, state,
                            new Pair { JaIndex = state.I, ZhIndex = state.J },
                            new Pair { JaIndex = -1, ZhIndex = state.J + 1 }));
                    }

                    // 2:1（中文一句 ↔ 日文两句）
                    if (state.I + 1 < ja.Count && state.J < zh.Count)
                    {
                        var mergedJa = ja[state.I].Text + ja[state.I + 1].Text;
                        var fake = new JaSentence { Text = mergedJa, IsDialogue = mergedJa.Contains('「'), EndPunctuation = ja[state.I + 1].EndPunctuation };
                        next.Add(new State(state.I + 2, state.J + 1,
                            state.Cost + PairCost(fake, zh[state.J], expectedRatio) + 0.6, state,
                            new Pair { JaIndex = state.I, ZhIndex = state.J },
                            new Pair { JaIndex = state.I + 1, ZhIndex = -1 }));
                    }

                    // 跳过日文一句（中文漏译 / 日文是拟声词）
                    if (state.I < ja.Count)
                    {
                        next.Add(new State(state.I + 1, state.J, state.Cost + 2.2, state, new Pair { JaIndex = state.I }));
                    }

                    // 跳过中文一句（中文有译注 / 日文合并了）
                    if (state.J < zh.Count)
                    {
                        next.Add(new State(state.I, state.J + 1, state.Cost + 2.2, state, new Pair { JaIndex = -1, ZhIndex = state.J }));
                    }
                }

                // 去重（同一 (i,j) 只留最优）+ 按"代价 + 偏离对角线"排序取前 beamWidth 个
                var best = new Dictionary<(int, int), State>();
                foreach (var state in next)
                {
                    var key = (state.I, state.J);
                    if (!best.TryGetValue(key, out var existing) || state.Cost < existing.Cost)
                    {
                        best[key] = state;
                    }
                }

                beam = best.Values
                    .OrderBy(s => s.Cost + DiagonalPenalty(s.I, s.J, ja.Count, zh.Count))
                    .Take(beamWidth)
                    .ToList();
            }

            var final = beam.Where(s => s.I >= ja.Count).OrderBy(s => s.Cost).FirstOrDefault()
                        ?? beam.OrderBy(s => s.Cost + DiagonalPenalty(s.I, s.J, ja.Count, zh.Count)).First();

            var pairs = new List<Pair>();
            for (var state = final; state?.Incoming != null; state = state.Previous)
            {
                pairs.AddRange(state.Incoming);
            }

            pairs.Reverse();
            return pairs;
        }

        private static double DiagonalPenalty(int i, int j, int n, int m)
        {
            if (n == 0 || m == 0)
            {
                return 0;
            }

            // 中文侧可能只用得上前一段（有声书和纸质书的分卷不一定对得上），
            // 所以这里只做很轻的"别乱跳"约束，主要靠句子本身的代价对齐。
            var expectedJ = (double)i / n * m;
            return Math.Abs(j - expectedJ) * 0.02;
        }

        /// <summary>
        /// 先用粗糙的自动对齐估一个位置：每个日文句子大概对应中文的第几句。
        /// 只为给分块文件圈出"中文译本片段"的范围，精度不重要（网页 AI 负责真正的对齐）。
        /// </summary>
        public static Dictionary<int, int> EstimateZhAnchors(List<JaSentence> ja, List<string> zh)
        {
            var result = new Dictionary<int, int>();
            if (ja.Count == 0 || zh.Count == 0)
            {
                return result;
            }

            var pairs = Align(ja, zh, (double)zh.Sum(s => s.Length) / Math.Max(1, ja.Sum(s => s.Text.Length)));

            var lastZh = 0;
            foreach (var pair in pairs)
            {
                if (pair.JaIndex >= 0)
                {
                    if (pair.ZhIndex >= 0)
                    {
                        lastZh = pair.ZhIndex;
                    }

                    result[ja[pair.JaIndex].FirstCue] = lastZh;
                }
            }

            return result;
        }
        private static double PairCost(JaSentence ja, string zh, double expectedRatio)
        {
            var jaLength = Math.Max(1, ja.Text.Length);
            var ratio = (double)zh.Length / jaLength;

            var cost = Math.Abs(ratio - expectedRatio) / Math.Max(0.2, expectedRatio) * 1.6;

            var jaDialogue = ja.IsDialogue;
            var zhDialogue = zh.Contains('「');
            if (jaDialogue != zhDialogue)
            {
                cost += 1.8;
            }

            var zhEnd = LastPunctuation(zh);
            if (ja.EndPunctuation != '\0' && zhEnd != '\0' && ja.EndPunctuation != zhEnd)
            {
                var bothStrong = IsStrong(ja.EndPunctuation) && IsStrong(zhEnd);
                cost += bothStrong ? 1.2 : 0.4;
            }

            return cost;

            static bool IsStrong(char ch) => ch is '。' or '！' or '？' or '!' or '?';
        }

        private sealed class State
        {
            public State(int i, int j, double cost, State? previous, params Pair?[]? incoming)
            {
                I = i;
                J = j;
                Cost = cost;
                Previous = previous;
                Incoming = incoming?.Where(p => p != null).Select(p => p!).ToList() ?? new List<Pair>();
            }

            public int I { get; }

            public int J { get; }

            public double Cost { get; }

            public State? Previous { get; }

            public List<Pair> Incoming { get; }
        }

        // ---------------- 把中文摊回字幕块 ----------------

        /// <summary>
        /// 每个句子的中文摊到它覆盖的字幕块上：按各块日文字数占比切分中文。
        /// </summary>
        private static string[] DistributeBack(
            IReadOnlyList<SubtitleLine> cues,
            List<JaSentence> jaSentences,
            List<string> zhSentences,
            List<Pair> pairs)
        {
            var result = new string[cues.Count];

            // 句子 → 中文
            var sentenceChinese = new Dictionary<int, string>();
            var pending = new List<int>();

            foreach (var pair in pairs)
            {
                if (pair.JaIndex >= 0)
                {
                    pending.Add(pair.JaIndex);
                }

                if (pair.ZhIndex >= 0)
                {
                    var text = zhSentences[pair.ZhIndex];
                    foreach (var jaIndex in pending)
                    {
                        sentenceChinese[jaIndex] = sentenceChinese.TryGetValue(jaIndex, out var old) ? old + text : text;
                    }

                    pending.Clear();
                }

                if (pair.JaIndex >= 0 && pair.ZhIndex >= 0)
                {
                    pending.Clear();
                }
            }

            for (var s = 0; s < jaSentences.Count; s++)
            {
                var sentence = jaSentences[s];
                if (!sentenceChinese.TryGetValue(s, out var chinese) || string.IsNullOrWhiteSpace(chinese))
                {
                    continue;
                }

                var blockCount = sentence.LastCue - sentence.FirstCue + 1;

                // 整句重复，而不是按字数劈开：
                // 日文一句话被朗读的这几秒里，中文翻译整句一直显示（对照阅读正好），
                // 也不会出现"女孩子"被切成"女"/"孩子"这种断词。
                for (var k = 0; k < blockCount; k++)
                {
                    result[sentence.FirstCue + k] = chinese;
                }

                continue;

                // 按各块日文字数占比分配中文，但**只在标点处切**：
                // 按字数硬切会把"时间"这种词劈成两个字幕块，读起来很难受。
                var weights = new int[blockCount];
                var total = 0;
                for (var k = 0; k < blockCount; k++)
                {
                    weights[k] = Math.Max(1, cues[sentence.FirstCue + k].Text.Length);
                    total += weights[k];
                }

                var cuts = new List<int>();
                var consumed = 0;
                for (var k = 0; k < blockCount - 1; k++)
                {
                    consumed += weights[k];
                    var target = (int)Math.Round((double)chinese.Length * consumed / total);
                    cuts.Add(SnapToPunctuation(chinese, target, cuts.Count > 0 ? cuts[^1] : 0));
                }

                cuts.Add(chinese.Length);
                var cursor = 0;
                for (var k = 0; k < blockCount; k++)
                {
                    var end = Math.Clamp(cuts[k], cursor, chinese.Length);
                    if (end > cursor)
                    {
                        result[sentence.FirstCue + k] = chinese.Substring(cursor, end - cursor).Trim();
                        cursor = end;
                    }
                }
            }

            return result;
        }

        /// <summary>把切分点吸附到最近的标点上（找不到就原地切）。</summary>
        private static int SnapToPunctuation(string text, int target, int lowerBound)
        {
            if (target <= lowerBound)
            {
                return Math.Min(lowerBound + 1, text.Length);
            }

            if (target >= text.Length)
            {
                return text.Length;
            }

            const string breaks = "，、；：。！？…」）) ";

            for (var radius = 0; radius <= 14; radius++)
            {
                var after = target + radius;
                if (after > lowerBound && after <= text.Length &&
                    after > 0 && breaks.IndexOf(text[after - 1]) >= 0)
                {
                    return after;
                }

                var before = target - radius;
                if (before > lowerBound && before <= text.Length &&
                    before > 0 && breaks.IndexOf(text[before - 1]) >= 0)
                {
                    return before;
                }
            }

            return ExtendThroughRun(text, target);
        }

        /// <summary>切点落在连续相同符号（—— / ……）中间时，整串带过去。</summary>
        private static int ExtendThroughRun(string text, int index)
        {
            if (index <= 0 || index > text.Length)
            {
                return index;
            }

            var ch = text[index - 1];
            if (ch is not ('—' or '…' or '－' or '-'))
            {
                return index;
            }

            while (index < text.Length && text[index] == ch)
            {
                index++;
            }

            return index;
        }
        private static void WriteSrt(string path, IReadOnlyList<SubtitleLine> cues, string[] texts)
        {
            var builder = new StringBuilder();
            for (var i = 0; i < cues.Count; i++)
            {
                var text = texts[i];
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue; // 没配到中文的块直接不输出
                }

                builder.Append(i + 1).Append('\n');
                builder.Append(Format(cues[i].StartTime)).Append(" --> ").Append(Format(cues[i].EndTime)).Append('\n');
                builder.Append(text).Append('\n').Append('\n');
            }

            File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
        }

        private static string Format(TimeSpan time) => string.Format(
            CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00},{3:000}",
            (int)time.TotalHours, time.Minutes, time.Seconds, time.Milliseconds);

        private static void WriteReport(string path, List<JaSentence> ja, List<string> zh, List<Pair> pairs)
        {
            var builder = new StringBuilder();
            var matched = 0;
            var skippedJa = 0;

            foreach (var pair in pairs)
            {
                if (pair.JaIndex >= 0 && pair.ZhIndex < 0)
                {
                    skippedJa++;
                }
            }

            var maxZh = pairs.Where(p => p.ZhIndex >= 0).Select(p => p.ZhIndex).DefaultIfEmpty(-1).Max();
            builder.AppendLine($"日文句子 {ja.Count} / 中文句子 {zh.Count} / 配对 {pairs.Count(p => p.JaIndex >= 0 && p.ZhIndex >= 0)} / 日文未配对 {skippedJa}");
            var maxZhIndex = pairs.Where(p => p.ZhIndex >= 0).Select(p => p.ZhIndex).DefaultIfEmpty(-1).Max();
            builder.AppendLine($"中文用到第 {maxZhIndex + 1} 句（共 {zh.Count} 句 = {100.0 * (maxZhIndex + 1) / Math.Max(1, zh.Count):0.0}%）");
            builder.AppendLine($"中文用到第 {maxZh + 1} 句（共 {zh.Count} 句，即 {100.0 * (maxZh + 1) / Math.Max(1, zh.Count):0.0}%）");
            builder.AppendLine();

            var shown = 0;
            var i = 0;
            while (i < pairs.Count && shown < 40)
            {
                var pair = pairs[i];
                if (pair.JaIndex >= 0 && pair.ZhIndex >= 0)
                {
                    var jaText = ja[pair.JaIndex].Text;
                    var zhText = zh[pair.ZhIndex];
                    builder.AppendLine($"[{pair.JaIndex}/{pair.ZhIndex}] 日: {Truncate(jaText, 70)}");
                    builder.AppendLine($"            中: {Truncate(zhText, 70)}");
                    shown++;
                    matched++;
                }

                i += Math.Max(1, pairs.Count / 400);
            }

            File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
        }

        private static string Truncate(string text, int max) =>
            text.Length <= max ? text : text.Substring(0, max) + "…";
    }
}
