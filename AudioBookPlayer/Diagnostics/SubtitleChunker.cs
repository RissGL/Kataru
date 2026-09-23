using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AudioBookPlayer.Core;

namespace AudioBookPlayer.Diagnostics
{
    /// <summary>
    /// 喂给网页版 AI 的分块 / 回收工具。
    ///
    /// 为什么这么做：把中文译本按顺序贴到日文时间轴上，本质是**双语对齐**，
    /// 只看长度和标点是猜不准的（误差会累积，越到后面越歪）。
    /// 网页版 AI 懂两种语言，能一句一句对；但它一次吃不下整本书，
    /// 所以这里把活儿切块、拼好提示词，用户贴过去、把回答存回来，再整合成 SRT。
    ///
    /// 用法：
    ///   KATARU.exe --make-chunks --ja &lt;日文.srt&gt; --zh &lt;中文.txt[;中文2.txt]&gt; --out-dir &lt;目录&gt; [--chunk-size 400]
    ///   KATARU.exe --merge-replies --ja &lt;日文.srt&gt; --replies &lt;回答目录&gt; --out &lt;输出.zh.srt&gt;
    /// </summary>
    public static class SubtitleChunker
    {
        // ---------------- 切块：生成提示词 ----------------

        public static int MakeChunks(string[] args)
        {
            var jaPath = UiSnapshot.GetOption(args, "--ja");
            var zhPath = UiSnapshot.GetOption(args, "--zh");
            var outDir = UiSnapshot.GetOption(args, "--out-dir");
            var mode = UiSnapshot.GetOption(args, "--mode") ?? "align";
            var translateMode = string.Equals(mode, "translate", StringComparison.OrdinalIgnoreCase);
            var chunkSizeText = UiSnapshot.GetOption(args, "--chunk-size");
            var chunkSize = int.TryParse(chunkSizeText, out var parsed) ? Math.Clamp(parsed, 50, 2000) : 400;

            if (string.IsNullOrEmpty(jaPath) || string.IsNullOrEmpty(zhPath) || string.IsNullOrEmpty(outDir))
            {
                Console.WriteLine("用法: --make-chunks --ja <日文.srt> --zh <中文.txt[;...]> --out-dir <目录> [--chunk-size 400]");
                return 2;
            }

            var cues = SrtParser.ParseFile(jaPath).Lines;
            var zhSentences = LoadZh(zhPath);

            // 先用粗糙的自动对齐估个大概位置，只是为了知道"这一块该去中文的哪一段找"
            var jaSentences = SubtitleAligner.BuildJaSentencesForChunks(cues);
            var anchors = SubtitleAligner.EstimateZhAnchors(jaSentences, zhSentences);

            Directory.CreateDirectory(outDir);

            var volumeName = Path.GetFileNameWithoutExtension(jaPath);
            var total = (int)Math.Ceiling((double)cues.Count / chunkSize);
            var index = 0;

            for (var start = 0; start < cues.Count; start += chunkSize)
            {
                index++;
                var end = Math.Min(cues.Count, start + chunkSize);

                // 这一块覆盖的日文句子 → 对应的中文区间（左右各留安全余量）
                var firstSentence = jaSentences.FirstOrDefault(s => s.LastCue >= start);
                var lastSentence = jaSentences.LastOrDefault(s => s.FirstCue < end);

                var zhFrom = 0;
                var zhTo = zhSentences.Count - 1;
                if (firstSentence != null && lastSentence != null &&
                    anchors.TryGetValue(firstSentence.FirstCue, out var anchorStart) &&
                    anchors.TryGetValue(lastSentence.FirstCue, out var anchorEnd))
                {
                    zhFrom = Math.Max(0, anchorStart - 40);
                    zhTo = Math.Min(zhSentences.Count - 1, anchorEnd + 40);
                }

                var builder = new StringBuilder();
                if (translateMode)
                {
                    builder.AppendLine("你是日语文学翻译。任务：把下面每一条日文字幕翻译成简体中文。");
                    builder.AppendLine();
                    builder.AppendLine("规则（很重要）：");
                    builder.AppendLine("1. 这是西尾维新《化物语》有声书的字幕，语气是轻小说式的第一人称独白与对话，译文要自然。");
                    builder.AppendLine("2. **一条日文对一条中文**：每行日文是朗读时切出来的一小块，可能只是半句话。");
                    builder.AppendLine("   请只翻译这一块，不要擅自补全后面的内容，也不要重复上一行。");
                    builder.AppendLine("3. 保持术语一致：阿良良木历 / 战场原黑仪 / 羽川翼 / 八九寺真宵 / 神原骏河 / 忍野咩咩。");
                    builder.AppendLine("4. 输出格式严格为：编号|中文（例如 3|是一副体弱多病少女的做派——理所当然地）");
                    builder.AppendLine("5. 只输出这些行，不要任何解释、标题、空行、markdown 代码块。");
                }
                else
                {
                    builder.AppendLine("你是中日双语字幕对齐助手。任务：为下面每一条日文字幕，给出它对应的中文。");
                    builder.AppendLine();
                    builder.AppendLine("规则（很重要）：");
                    builder.AppendLine("1. 必须使用我给的中文译本用词，不要自己另译；译本里没有的内容才允许你补。");
                    builder.AppendLine("2. **一条日文对一条中文**：中文译本里的一句话如果对应好几条日文字幕，");
                    builder.AppendLine("   请按日文的分块把它拆成对应几段，在语义通顺处断开，绝对不要把一个词切断。");
                    builder.AppendLine("3. 每条日文字幕都要有输出，**不要重复上一行的内容**，长度尽量和日文那条相当。");
                    builder.AppendLine("4. 输出格式严格为：编号|中文（例如 12|战场原黑仪，在班上被定位成体弱多病的女孩子——）");
                    builder.AppendLine("5. 只输出这些行，不要任何解释、标题、空行、markdown 代码块。");
                }
                builder.AppendLine();
                builder.AppendLine($"【日文字幕】第 {start + 1} ~ {end} 条");
                for (var i = start; i < end; i++)
                {
                    builder.AppendLine($"{i + 1}|{cues[i].Text.Replace("\r", " ").Replace("\n", " ").Trim()}");
                }

                if (!translateMode)
                {
                    builder.AppendLine();
                    builder.AppendLine("【中文译本片段（只作对照，按顺序）】");
                    for (var j = zhFrom; j <= zhTo; j++)
                    {
                        builder.AppendLine($"S{j + 1}|{zhSentences[j]}");
                    }
                }

                builder.AppendLine();
                builder.AppendLine($"【请输出第 {start + 1} ~ {end} 条的中文，每行格式：编号|中文】");

                var file = Path.Combine(outDir, $"part-{index:000}.txt");
                File.WriteAllText(file, builder.ToString(), new UTF8Encoding(false));
            }

            // 说明文件
            var readme = new StringBuilder();
            readme.AppendLine($"# {volumeName} —— " + (translateMode ? "分块翻译任务" : "分块对齐任务") + $"（共 {total} 块，每块 {chunkSize} 条字幕）");
            readme.AppendLine();
            readme.AppendLine("## 你要做的");
            readme.AppendLine("1. 打开 part-001.txt，**全选复制**，粘贴到网页版 AI（Claude / ChatGPT / Gemini 都行），发送。");
            readme.AppendLine("2. 把 AI 的回答**完整复制**，存成 replies\\part-001.txt（用记事本，编码选 UTF-8）。");
            readme.AppendLine("3. 对 part-002.txt …… 重复，直到全部做完。");
            readme.AppendLine("4. 回来告诉我，我用 --merge-replies 把回答整合成 .zh.srt。");
            readme.AppendLine();
            readme.AppendLine("## 小技巧");
            readme.AppendLine("* 可以几块一起发给 AI（比如 part-001 + part-002），只要回答里编号对得上就行。");
            readme.AppendLine("* 回答里带了多余的解释也没关系，我整合时会自动忽略不认识的格式。");
            readme.AppendLine("* 做到一半也可以先整合，我会告诉你还缺哪些编号。");
            readme.AppendLine();
            readme.AppendLine("## 编号说明");
            readme.AppendLine($"* 编号就是日文字幕的序号（1 ~ {cues.Count}），跨块连续，不要改。");
            readme.AppendLine("* 每块文件里都带了对应的中文译本片段，AI 用它当对照；片段可能比实际需要的多一点，属正常。");
            File.WriteAllText(Path.Combine(outDir, "README.txt"), readme.ToString(), new UTF8Encoding(false));

            Console.WriteLine($"已生成 {total} 个分块 → {outDir}");
            Console.WriteLine($"字幕总数 {cues.Count} / 每块 {chunkSize} 条");
            Console.WriteLine($"中文译本句数 {zhSentences.Count}");
            return 0;
        }

        // ---------------- 回收：把 AI 的回答整合成 SRT ----------------

        public static int MergeReplies(string[] args)
        {
            var jaPath = UiSnapshot.GetOption(args, "--ja");
            var replyDir = UiSnapshot.GetOption(args, "--replies");
            var outPath = UiSnapshot.GetOption(args, "--out");

            if (string.IsNullOrEmpty(jaPath) || string.IsNullOrEmpty(replyDir) || string.IsNullOrEmpty(outPath))
            {
                Console.WriteLine("用法: --merge-replies --ja <日文.srt> --replies <回答目录> --out <输出.zh.srt>");
                return 2;
            }

            var cues = SrtParser.ParseFile(jaPath).Lines;
            var map = new Dictionary<int, string>();

            // --prefix：只收本卷的回答文件。
            // 不加这个限制的话，多卷的编号都从 1 开始，后读的卷会把前面的整段覆盖掉。
            var prefix = UiSnapshot.GetOption(args, "--prefix");

            var replyFiles = Directory.GetFiles(replyDir, "*.txt", SearchOption.AllDirectories)
                .Where(f => string.IsNullOrEmpty(prefix) ||
                            Path.GetFileName(f).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f);

            foreach (var file in replyFiles)
            {
                var lines = File.ReadAllLines(file, Encoding.UTF8);
                var taken = 0;

                foreach (var raw in lines)
                {
                    // 容忍各种写法：12|中文 / 12｜中文 / 12: 中文 / 12 中文
                    var match = Regex.Match(raw, @"^\s*(\d{1,6})\s*[|｜:：\t]\s*(.+?)\s*$");
                    if (!match.Success)
                    {
                        match = Regex.Match(raw, @"^\s*(\d{1,6})\s*[.、,，]\s*(.+?)\s*$");
                    }

                    if (!match.Success)
                    {
                        continue;
                    }

                    var id = int.Parse(match.Groups[1].Value);
                    var text = match.Groups[2].Value.Trim();

                    if (id < 1 || id > cues.Count || text.Length == 0)
                    {
                        continue;
                    }

                    // 同一编号以最后一次为准（重跑某块时不用手动清理旧文件）
                    map[id] = text;
                    taken++;
                }

                if (taken > 0)
                {
                    Console.WriteLine($"  {Path.GetFileName(file)}: {taken} 行");
                }
            }

            Console.WriteLine($"共收到 {map.Count} / {cues.Count} 条（{100.0 * map.Count / cues.Count:0.0}%）");

            var builder = new StringBuilder();
            var missing = new List<int>();
            var number = 0;

            for (var i = 0; i < cues.Count; i++)
            {
                if (!map.TryGetValue(i + 1, out var text) || string.IsNullOrWhiteSpace(text))
                {
                    missing.Add(i + 1);
                    continue;
                }

                number++;
                builder.Append(number).Append('\n');
                builder.Append(Format(cues[i].StartTime)).Append(" --> ").Append(Format(cues[i].EndTime)).Append('\n');
                builder.Append(text).Append('\n').Append('\n');
            }

            File.WriteAllText(outPath, builder.ToString(), new UTF8Encoding(false));
            Console.WriteLine($"已输出 {number} 条 → {outPath}");

            if (missing.Count > 0)
            {
                var file = Path.ChangeExtension(outPath, ".missing.txt");
                File.WriteAllText(file, string.Join(",", missing), new UTF8Encoding(false));
                Console.WriteLine($"还缺 {missing.Count} 条，清单 → {file}");
                Console.WriteLine($"缺的编号：{string.Join(",", missing.Take(40))}{(missing.Count > 40 ? " ..." : string.Empty)}");
            }

            return 0;
        }

        private static List<string> LoadZh(string zhPath)
        {
            var all = new List<string>();
            foreach (var one in zhPath.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                if (File.Exists(one))
                {
                    all.AddRange(SubtitleAligner.BuildZhSentencesForChunks(File.ReadAllText(one, Encoding.UTF8)));
                }
            }

            return all;
        }

        private static string Format(TimeSpan time) => string.Format(
            System.Globalization.CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00},{3:000}",
            (int)time.TotalHours, time.Minutes, time.Seconds, time.Milliseconds);
    }
}
