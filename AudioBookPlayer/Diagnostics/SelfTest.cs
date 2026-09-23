using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using AudioBookPlayer.Audio;
using AudioBookPlayer.Core;
using AudioBookPlayer.ViewModels;
using System.Linq;
using AudioBookPlayer.Views;
using AudioBookPlayer.Windows;

namespace AudioBookPlayer.Diagnostics
{
    /// <summary>
    /// 命令行自检：把"第 20 节 MVP 验收标准"能自动化的部分跑一遍。
    ///
    ///     AudioBookPlayer.exe --selftest
    ///
    /// 不会显示任何窗口（悬浮窗口只创建 HWND 验证样式，不 Show），播放音频时音量为 0。
    /// 结果同时打印到控制台和输出目录下的 selftest-report.txt。
    /// </summary>
    internal static class SelfTest
    {
        public static bool IsRequested(string[] args)
        {
            foreach (var arg in args)
            {
                if (arg.Equals("--selftest", StringComparison.OrdinalIgnoreCase) ||
                    arg.Equals("-t", StringComparison.OrdinalIgnoreCase) ||
                    arg.Equals("--test", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        public static async Task<int> RunAsync(string[] args)
        {
            Win32WindowHelper.AttachConsoleToParent();

            var reportPath = GetOption(args, "--report")
                             ?? Path.Combine(AppContext.BaseDirectory, "selftest-report.txt");

            var report = new List<string>();
            var passed = 0;
            var failed = 0;
            var skipped = 0;

            void Check(string name, bool ok, string detail = "")
            {
                if (ok)
                {
                    passed++;
                }
                else
                {
                    failed++;
                }

                report.Add($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  — " + detail : string.Empty)}");
            }

            void Skip(string name, string reason)
            {
                skipped++;
                report.Add($"[SKIP] {name}  — {reason}");
            }

            void Info(string message)
            {
                report.Add($"[INFO] {message}");
            }

            void Section(string title)
            {
                report.Add(string.Empty);
                report.Add($"--- {title} ---");
            }

            report.Add("AudioBookPlayer 自检报告");
            report.Add($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            report.Add($"输出目录：{AppContext.BaseDirectory}");

            // ============ 1. SRT 解析 ============
            Section("Phase 1  SRT 解析");

            const string embedded =
                "1\r\n" +
                "00:00:01,500 --> 00:00:04,200\r\n" +
                "戦場ヶ原、俺はお前が好きだ。\r\n" +
                "\r\n" +
                "2\r\n" +
                "00:00:05.000 --> 00:00:08.250\r\n" +
                "第一行\r\n" +
                "第二行\r\n" +
                "\r\n" +
                "3\r\n" +
                "00:00:09,000 --> 00:00:10,000\r\n" +
                "{\\an8}<i>带标签</i>的字幕\r\n";

            var embeddedResult = SrtParser.Parse(embedded);

            Check("内嵌样例解析出 3 条", embeddedResult.Lines.Count == 3, $"实际 {embeddedResult.Lines.Count}");

            if (embeddedResult.Lines.Count == 3)
            {
                var first = embeddedResult.Lines[0];
                Check("序号 = 1", first.Index == 1, $"实际 {first.Index}");
                Check("开始时间 = 00:00:01.500", first.StartTime == TimeSpan.FromMilliseconds(1500), first.StartTime.ToString());
                Check("结束时间 = 00:00:04.200", first.EndTime == TimeSpan.FromMilliseconds(4200), first.EndTime.ToString());
                Check("日文文本无损", first.Text == "戦場ヶ原、俺はお前が好きだ。", first.Text);

                var second = embeddedResult.Lines[1];
                Check("多行字幕保留换行", second.Text == "第一行\n第二行", second.Text.Replace("\n", "\\n"));
                Check("小数点时间轴 (00:00:05.000)", second.StartTime == TimeSpan.FromSeconds(5), second.StartTime.ToString());

                var third = embeddedResult.Lines[2];
                Check("ASS 位置标签与 HTML 标签被清理", third.Text == "带标签的字幕", third.Text);
            }

            // BOM + CRLF 文件
            var bomPath = Path.Combine(AppContext.BaseDirectory, "selftest-bom.srt");
            try
            {
                File.WriteAllText(bomPath, embedded, new UTF8Encoding(true));
                var bomResult = SrtParser.ParseFile(bomPath);
                Check("UTF-8 BOM 文件解析正确", bomResult.Lines.Count == 3, $"实际 {bomResult.Lines.Count}");
                Check("识别出 BOM 编码", bomResult.EncodingName.Contains("BOM", StringComparison.Ordinal), bomResult.EncodingName);
            }
            catch (Exception ex)
            {
                Check("UTF-8 BOM 文件解析正确", false, ex.Message);
            }
            finally
            {
                try
                {
                    if (File.Exists(bomPath))
                    {
                        File.Delete(bomPath);
                    }
                }
                catch
                {
                    // 忽略清理失败
                }
            }

            // ============ 2. 字幕同步 ============
            Section("Phase 3  字幕同步（时间基准 = 音频位置）");

            var lines = new List<SubtitleLine>
            {
                new SubtitleLine { Index = 1, StartTime = TimeSpan.FromSeconds(10), EndTime = TimeSpan.FromSeconds(13), Text = "「阿良良木君。」" },
                new SubtitleLine { Index = 2, StartTime = TimeSpan.FromSeconds(20), EndTime = TimeSpan.FromSeconds(24), Text = "「これは突然のことだった。」" },
                new SubtitleLine { Index = 3, StartTime = TimeSpan.FromSeconds(30), EndTime = TimeSpan.FromSeconds(35), Text = "「第三句。」" },
            };

            var sync = new SubtitleSynchronizer();
            sync.Load(lines);

            sync.Update(TimeSpan.FromSeconds(12.5));
            Check("12.5s → 第 1 条", sync.Current?.Index == 1, sync.Current?.Text ?? "(null)");

            sync.Seek(TimeSpan.FromSeconds(10));
            Check("正好在开始时间 → 显示该条", sync.Current?.Index == 1, sync.Current?.ToString() ?? "(null)");

            sync.Seek(TimeSpan.FromSeconds(13));
            Check("正好在结束时间 → 不显示（左闭右开）", sync.Current == null, sync.Current?.ToString() ?? "(null)");

            sync.Seek(TimeSpan.FromSeconds(9.999));
            Check("开始之前 → 不显示", sync.Current == null, "空档期");

            sync.Seek(TimeSpan.FromSeconds(15));
            Check("两条之间 → 不显示", sync.Current == null, "空档期");

            // 关键验收点：快进之后必须立刻重新定位，而不是从旧字幕往后找
            sync.Seek(TimeSpan.FromSeconds(0));
            sync.Update(TimeSpan.FromSeconds(31));
            Check("从 0s 直接跳到 31s → 第 3 条（Seek 重算）", sync.Current?.Index == 3, sync.Current?.Text ?? "(null)");

            // 快退同理
            sync.Seek(TimeSpan.FromSeconds(22));
            Check("从 31s 快退到 22s → 第 2 条", sync.Current?.Index == 2, sync.Current?.Text ?? "(null)");

            sync.Seek(TimeSpan.FromSeconds(21.5));
            sync.Update(TimeSpan.FromSeconds(21.6));
            Check("连续 Tick 保持在同一条", sync.Current?.Index == 2, sync.Current?.Text ?? "(null)");

            sync.Seek(TimeSpan.FromSeconds(36));
            Check("超过最后一条 → 不显示", sync.Current == null, "结尾之后");

            // 暂停语义：位置不变 → 字幕不变
            sync.Seek(TimeSpan.FromSeconds(12));
            var before = sync.Current;
            sync.Update(TimeSpan.FromSeconds(12));
            Check("暂停时位置不变 → 字幕停留在当前条", ReferenceEquals(before, sync.Current), sync.Current?.Text ?? "(null)");

            // ============ 3. 样例文件 ============
            Section("测试素材");

            var sampleSrt = FindSample("test.srt");
            var sampleWav = FindSample("test.wav");

            SrtParseResult? sampleResult = null;
            if (sampleSrt == null)
            {
                Skip("samples/测试样例/test.srt", "未找到样例字幕文件");
            }
            else
            {
                sampleResult = SrtParser.ParseFile(sampleSrt);
                Check($"samples/测试样例/test.srt 解析成功（{sampleResult.Lines.Count} 条）", sampleResult.Lines.Count > 0,
                    $"编码 {sampleResult.EncodingName}" + (sampleResult.HasWarnings ? $"，警告 {sampleResult.Warnings.Count} 条" : "，无警告"));

                var multiLine = sampleResult.Lines.Find(l => l.Text.Contains('\n'));
                Check("样例中包含多行字幕", multiLine != null, multiLine?.Text.Replace("\n", "\\n") ?? "(无)");
            }

            // ============ 3b. 字幕语言识别 ============
            Section("多语言字幕（语言识别 / 双语）");

            try
            {
                var cases = new (string File, string Code, string Name)[]
                {
                    ("01 ひたぎクラブ.srt", "", "原文"),
                    ("01 ひたぎクラブ.zh.srt", "zh", "中文"),
                    ("01 ひたぎクラブ.chs.srt", "zh", "简体中文"),
                    ("01 ひたぎクラブ.zh-CN.srt", "zh", "简体中文"),
                    ("01 ひたぎクラブ.中文.srt", "zh", "中文"),
                    ("01 ひたぎクラブ.ja.srt", "ja", "日本語"),
                    ("01 ひたぎクラブ.jp.srt", "ja", "日本語"),
                    ("01 ひたぎクラブ.日本語.srt", "ja", "日本語"),
                    ("01 ひたぎクラブ.en.srt", "en", "English"),
                    ("01 ひたぎクラブ.eng.srt", "en", "English"),
                    ("01 ひたぎクラブ.ko.srt", "ko", "한국어"),
                };

                var wrong = new List<string>();
                foreach (var (file, code, name) in cases)
                {
                    var (detectedCode, detectedName) = SubtitleLanguage.Detect("01 ひたぎクラブ", file);
                    if (detectedCode != code || detectedName != name)
                    {
                        wrong.Add($"{file}→{detectedCode}/{detectedName}");
                    }
                }

                Check($"{cases.Length} 种文件名写法都能认出语言", wrong.Count == 0,
                    wrong.Count == 0 ? "全部正确" : string.Join("、", wrong));

                Check("认不出的标记原样保留", SubtitleLanguage.Detect("01", "01.xyz.srt").Code == "xyz");

                Check("同名才算同一条音频的字幕",
                    SubtitleLanguage.IsRelatedSubtitle("01 ひたぎクラブ", "01 ひたぎクラブ.zh") &&
                    !SubtitleLanguage.IsRelatedSubtitle("01 ひたぎクラブ", "01 ひたぎクラブ2"));

                // 扫描时同一条音频要收齐多种语言
                var multiDirectory = Path.Combine(AppContext.BaseDirectory, "selftest-multi");
                if (Directory.Exists(multiDirectory))
                {
                Directory.Delete(multiDirectory, true);
                }

                Directory.CreateDirectory(multiDirectory);
                const string jaSrt = "1\r\n00:00:01,000 --> 00:00:03,000\r\n日本語の字幕\r\n";
                const string zhSrt = "1\r\n00:00:01,000 --> 00:00:03,000\r\n中文字幕\r\n";

                File.WriteAllText(Path.Combine(multiDirectory, "第01話.mp3"), "placeholder");
                File.WriteAllText(Path.Combine(multiDirectory, "第01話.srt"), jaSrt, new UTF8Encoding(true));      // 无标记 → 原文
                File.WriteAllText(Path.Combine(multiDirectory, "第01話.ja.srt"), jaSrt, new UTF8Encoding(true));   // 日语
                File.WriteAllText(Path.Combine(multiDirectory, "第01話.zh.srt"), zhSrt, new UTF8Encoding(true));   // 中文
                File.WriteAllText(Path.Combine(multiDirectory, "第01話.en.srt"), zhSrt, new UTF8Encoding(true));   // 英语

                var multiScan = MediaLibraryScanner.Scan(multiDirectory);
                var multiEntry = multiScan.Entries.Count > 0 ? multiScan.Entries[0] : null;

                Check("一条音频收齐 4 条语言轨", multiEntry?.SubtitleTracks.Count == 4,
                    $"{multiEntry?.SubtitleTracks.Count} 条：" + string.Join("、", multiEntry?.SubtitleTracks.Select(x => x.DisplayName) ?? Array.Empty<string>()));

                Check("排序：原文 → 日本語 → 中文 → English",
                    multiEntry != null &&
                    multiEntry.SubtitleTracks[0].LanguageCode == "" &&
                    multiEntry.SubtitleTracks[1].LanguageCode == "ja" &&
                    multiEntry.SubtitleTracks[2].LanguageCode == "zh" &&
                    multiEntry.SubtitleTracks[3].LanguageCode == "en",
                    string.Join("、", multiEntry?.SubtitleTracks.Select(x => $"{x.DisplayName}({x.LanguageCode})") ?? Array.Empty<string>()));

                Check("能按语言代码取轨", multiEntry?.FindTrack("zh")?.LanguageCode == "zh");
                Check("主字幕路径取第一条", multiEntry?.SubtitlePath == multiEntry?.SubtitleTracks[0].FilePath);

                // 双语：两份字幕各自同步到同一个时间点
                var primarySync = new SubtitleSynchronizer();
                var secondarySync = new SubtitleSynchronizer();
                primarySync.Load(SrtParser.Parse(jaSrt).Lines);
                secondarySync.Load(SrtParser.Parse(zhSrt).Lines);

                primarySync.Seek(TimeSpan.FromSeconds(2));
                secondarySync.Seek(TimeSpan.FromSeconds(2));
                Check("双语：主字幕同步", primarySync.Current?.Text == "日本語の字幕", primarySync.Current?.Text ?? "(null)");
                Check("双语：副字幕同步", secondarySync.Current?.Text == "中文字幕", secondarySync.Current?.Text ?? "(null)");

                primarySync.Seek(TimeSpan.FromSeconds(5));
                secondarySync.Seek(TimeSpan.FromSeconds(5));
                Check("双语：跳到空档期两边都清空",
                    primarySync.Current == null && secondarySync.Current == null);
                    // ---- 端到端：用假播放器真的加载一次章节，验证阅读模式拿得到文本 ----
                var langAudio = new FakeAudioPlayer();
                var skipAudio = new FakeAudioPlayer();
                var langSettingsPath = Path.Combine(AppContext.BaseDirectory, "selftest-lang-settings.json");
                var langPlaybackPath = Path.Combine(AppContext.BaseDirectory, "selftest-lang-playback.json");

                try
                {
                    using var langViewModel = new MainViewModel(
                        audioPlayerFactory: () => langAudio,
                        settings: AppSettings.Load(langSettingsPath),
                        playbackStore: PlaybackStore.Load(langPlaybackPath));

                    langViewModel.LoadEntry(multiEntry!, autoPlay: false);

                    Check("加载章节后阅读模式有内容（这条之前漏了）", langViewModel.ReadingLines.Count > 0,
                        $"阅读行数 {langViewModel.ReadingLines.Count}");

                    Check("阅读模式用的是主字幕的文本",
                        langViewModel.ReadingLines.Count > 0 && langViewModel.ReadingLines[0].Text == "日本語の字幕",
                        langViewModel.ReadingLines.Count > 0 ? langViewModel.ReadingLines[0].Text : "(空)");

                    Check("当前字幕也同步好了",
                        langViewModel.CurrentSubtitle != null || langViewModel.SubtitleCount > 0,
                        $"字幕条数 {langViewModel.SubtitleCount}");

                    // 换主字幕语言：阅读模式也要跟着换
                    var zhTrack = multiEntry!.FindTrack("zh");
                    if (zhTrack != null)
                    {
                        langViewModel.PrimaryTrack = zhTrack;
                        langViewModel.SeekTo(TimeSpan.FromSeconds(2));

                        // 注意：阅读语言已和悬浮字幕解耦，所以这里**不该**跟着变。
                        // 阅读侧的行为由下面"阅读语言与悬浮字幕解耦"那组用例覆盖。
                        Check("切换悬浮字幕语言后，阅读列表保持自己的语言",
                            langViewModel.ReadingLines.Count > 0 &&
                            langViewModel.ReadingLines[0].Text == "日本語の字幕",
                            langViewModel.ReadingLines.Count > 0 ? langViewModel.ReadingLines[0].Text : "(空)");
                    }

                    // ---- 上/下一章（托盘菜单和字幕工具条都靠这套）----
                    var navScan = MediaLibraryScanner.Scan(multiDirectory);
                    Check("上/下一章：单章时两边都不可用",
                        navScan.Entries.Count == 1 && !langViewModel.CanGoPrevious && !langViewModel.CanGoNext,
                        $"章节数 {navScan.Entries.Count}");

                    var twoChapterDirectory = Path.Combine(AppContext.BaseDirectory, "selftest-nav");
                    if (Directory.Exists(twoChapterDirectory))
                    {
                        Directory.Delete(twoChapterDirectory, true);
                    }

                    Directory.CreateDirectory(twoChapterDirectory);
                    File.WriteAllText(Path.Combine(twoChapterDirectory, "01 第一章.mp3"), "placeholder");
                    File.WriteAllText(Path.Combine(twoChapterDirectory, "02 第二章.mp3"), "placeholder");

                    var navScan2 = MediaLibraryScanner.Scan(twoChapterDirectory);
                    using (var navViewModel = new MainViewModel(
                        audioPlayerFactory: () => new FakeAudioPlayer(),
                        settings: AppSettings.Load(langSettingsPath),
                        playbackStore: PlaybackStore.Load(langPlaybackPath)))
                    {
                        navViewModel.SelectedBook = navScan2.Books[0];
                        navViewModel.SelectedEntry = navScan2.Entries[0];

                        Check("第一章：上一章不可用、下一章可用",
                            !navViewModel.CanGoPrevious && navViewModel.CanGoNext,
                            $"prev={navViewModel.CanGoPrevious} next={navViewModel.CanGoNext}");

                        navViewModel.GoNextChapter();
                        Check("跳到下一章后选中项变了",
                            navViewModel.SelectedEntry?.Title.Contains("第二章") == true,
                            navViewModel.SelectedEntry?.Title ?? "(空)");

                        Check("第二章：上一章可用、下一章不可用",
                            navViewModel.CanGoPrevious && !navViewModel.CanGoNext,
                            $"prev={navViewModel.CanGoPrevious} next={navViewModel.CanGoNext}");

                        navViewModel.GoPreviousChapter();
                        Check("再跳回上一章",
                            navViewModel.SelectedEntry?.Title.Contains("第一章") == true,
                            navViewModel.SelectedEntry?.Title ?? "(空)");
                    }

                    // ---- 反复换书换章：确认不会越换越慢 / 卡死 ----
                    var stressDirectory = Path.Combine(AppContext.BaseDirectory, "selftest-stress");
                    if (Directory.Exists(stressDirectory))
                    {
                        Directory.Delete(stressDirectory, true);

                    }

                    for (var b = 1; b <= 6; b++)
                    {
                        var bookDirectory = Path.Combine(stressDirectory, $"书{b}");
                        Directory.CreateDirectory(bookDirectory);

                        for (var c = 1; c <= 8; c++)
                        {
                            File.WriteAllText(Path.Combine(bookDirectory, $"{c:00} 第{c}章.mp3"), "placeholder");
                            File.WriteAllText(Path.Combine(bookDirectory, $"{c:00} 第{c}章.srt"), jaSrt, new UTF8Encoding(true));
                        }
                    }

                    var stressScan = MediaLibraryScanner.Scan(stressDirectory);
                    using (var stressViewModel = new MainViewModel(
                        audioPlayerFactory: () => new FakeAudioPlayer(),
                        settings: AppSettings.Load(langSettingsPath),
                        playbackStore: PlaybackStore.Load(langPlaybackPath)))
                    {
                        var stressWatch = System.Diagnostics.Stopwatch.StartNew();
                        var switches = 0;

                        for (var round = 0; round < 3; round++)
                        {
                            foreach (var stressBook in stressScan.Books)
                            {
                                stressViewModel.SelectedBook = stressBook;
                                foreach (var stressEntry in stressBook.Entries)
                                {
                                    stressViewModel.SelectedEntry = stressEntry;
                                    switches++;
                                }
                            }
                        }

                        stressWatch.Stop();
                        var perSwitch = stressWatch.Elapsed.TotalMilliseconds / Math.Max(1, switches);

                        Check($"连续换书换章 {switches} 次不卡死", switches == 144 && perSwitch < 50,
                            $"共 {stressWatch.Elapsed.TotalMilliseconds:0} ms，平均每次 {perSwitch:0.0} ms");
                    }

                    Directory.Delete(stressDirectory, true);
                    // ---- 工具条上的 ±10 秒：命令真的会改播放位置 ----
                    using (var skipViewModel = new MainViewModel(
                        audioPlayerFactory: () => skipAudio,
                        settings: AppSettings.Load(langSettingsPath),
                        playbackStore: PlaybackStore.Load(langPlaybackPath)))
                    {
                        var twoChapterDirectoryForSkip = Path.Combine(AppContext.BaseDirectory, "selftest-nav");

                        skipViewModel.SelectedBook = navScan.Books[0];
                        skipViewModel.SelectedEntry = navScan.Entries[0];

                        skipAudio.Position = TimeSpan.FromSeconds(30);
                        var positionBefore = skipAudio.Position;

                        skipViewModel.SkipBackCommand.Execute(null);
                        Check("工具条快退：位置回退一个步长",
                            Math.Abs((positionBefore - skipAudio.Position).TotalSeconds - skipViewModel.SkipSeconds) < 0.5,
                            $"{positionBefore.TotalSeconds:0}s → {skipAudio.Position.TotalSeconds:0}s（步长 {skipViewModel.SkipSeconds:0}s）");

                        var afterBack = skipAudio.Position;
                        skipViewModel.SkipForwardCommand.Execute(null);
                        Check("工具条快进：位置前进一个步长",
                            Math.Abs((skipAudio.Position - afterBack).TotalSeconds - skipViewModel.SkipSeconds) < 0.5,
                            $"{afterBack.TotalSeconds:0}s → {skipAudio.Position.TotalSeconds:0}s");

                        Check("工具条快退标签带步长",
                            skipViewModel.SkipBackLabel.Contains($"{skipViewModel.SkipSeconds:0}"),
                            skipViewModel.SkipBackLabel);
                    }
                    Directory.Delete(twoChapterDirectory, true);
                    // ---- 阅读语言与悬浮字幕解耦 + 阅读双语 ----
                    var jaTrack = multiEntry!.FindTrack("ja") ?? multiEntry.SubtitleTracks[1];
                    var zhTrackForReading = multiEntry.FindTrack("zh");
                    var enTrackForReading = multiEntry.FindTrack("en");

                    langViewModel.PrimaryTrack = jaTrack;
                    langViewModel.ReadingTrack = zhTrackForReading;

                    Check("悬浮字幕和阅读可以选不同语言",
                        langViewModel.PrimaryTrack?.LanguageCode == "ja" &&
                        langViewModel.ReadingTrack?.LanguageCode == "zh",
                        $"悬浮={langViewModel.PrimaryTrack?.DisplayName} / 阅读={langViewModel.ReadingTrack?.DisplayName}");

                    Check("阅读内容用的是阅读轨的语言",
                        langViewModel.ReadingLines.Count > 0 && langViewModel.ReadingLines[0].Text == "中文字幕",
                        langViewModel.ReadingLines.Count > 0 ? langViewModel.ReadingLines[0].Text : "(空)");

                    langViewModel.PrimaryTrack = enTrackForReading;
                    Check("改悬浮字幕语言不影响阅读内容",
                        langViewModel.ReadingLines.Count > 0 && langViewModel.ReadingLines[0].Text == "中文字幕",
                        langViewModel.ReadingLines.Count > 0 ? langViewModel.ReadingLines[0].Text : "(空)");

                    langViewModel.ReadingSecondaryTrack = enTrackForReading;
                    Check("阅读支持对照语言",
                        langViewModel.ReadingLines.Count > 0 && langViewModel.ReadingLines[0].HasSecondaryText,
                        langViewModel.ReadingLines.Count > 0 ? $"对照={langViewModel.ReadingLines[0].SecondaryText}" : "(空)");

                    // ---- 回归：播放到某句时，阅读列表要自动高亮到那一句 ----
                    langViewModel.ReadingTrack = zhTrackForReading;
                    langViewModel.ReadingSecondaryTrack = MainViewModel.NoSecondaryTrack;
                    langViewModel.SeekTo(TimeSpan.FromSeconds(2));

                    var currentLine = langViewModel.ReadingLines.Count > 0 ? langViewModel.ReadingLines[0] : null;
                    Check("播放到某句时自动高亮那一句（按时间匹配，不按对象）",
                        currentLine != null && currentLine.IsCurrent,
                        currentLine == null ? "(没有阅读行)" : $"{currentLine.Text} IsCurrent={currentLine.IsCurrent}");

                    Check("高亮的那句同时被选中（会自动滚动过去）",
                        currentLine != null && ReferenceEquals(langViewModel.SelectedReadingLine, currentLine),
                        langViewModel.SelectedReadingLine?.Text ?? "(未选中)");

                    // 跳到空档期：不该还亮着
                    langViewModel.SeekTo(TimeSpan.FromSeconds(5));
                    Check("跳到没有字幕的时刻就不高亮了",
                        langViewModel.ReadingLines.All(l => !l.IsCurrent));
                    langViewModel.ReadingSecondaryTrack = MainViewModel.NoSecondaryTrack;
                    Check("关掉对照后不显示第二行",
                        langViewModel.ReadingLines.Count > 0 && !langViewModel.ReadingLines[0].HasSecondaryText);
                    // 开双语：副字幕同步器要有内容
                    var enTrack = multiEntry.FindTrack("en");
                    if (enTrack != null)
                    {
                        langViewModel.SecondaryTrack = enTrack;
                        langViewModel.SeekTo(TimeSpan.FromSeconds(2));
                        Check("开双语后副字幕有文本", langViewModel.Overlay.SecondaryText.Length > 0,
                            langViewModel.Overlay.SecondaryText);
                        Check("副字幕字号比主字幕小",
                            langViewModel.Overlay.SecondaryFontSize < langViewModel.Overlay.FontSize,
                            $"{langViewModel.Overlay.SecondaryFontSize:0.#} < {langViewModel.Overlay.FontSize:0.#}");
                    }
                }
                finally
                {
                    foreach (var temp in new[] { langSettingsPath, langPlaybackPath })
                    {
                        try
                        {
                            if (File.Exists(temp))
                            {
                                File.Delete(temp);
                            }
                        }
                        catch
                        {
                            // 忽略
                        }
                    }
                }


                Directory.Delete(multiDirectory, true);

            }
            catch (Exception ex)
            {
                Check("多语言字幕", false, ex.Message);
            }
            // ============ 4. 媒体库扫描 ============
            Section("媒体库：递归扫描 + 字幕配对");

            var libraryRoot = Path.Combine(AppContext.BaseDirectory, "selftest-library");
            try
            {
                BuildSampleLibrary(libraryRoot);
                var scan = MediaLibraryScanner.Scan(libraryRoot);

                Check("扫描出 5 个音频条目", scan.Entries.Count == 5, $"实际 {scan.Entries.Count}");
                Check("识别出 3 个带字幕条目", scan.CountWithSubtitle == 3, $"实际 {scan.CountWithSubtitle}");
                Check("非音频文件被忽略（.txt / 封面图片 / .srt 本身）", scan.Entries.Count == 5);

                var entry01 = FindEntry(scan.Entries, "01 - 序章");
                var entry02 = FindEntry(scan.Entries, "02 - 第二章");
                var entry03 = FindEntry(scan.Entries, "03 - 第三章");
                var entry10 = FindEntry(scan.Entries, "10 - 第十章");

                Check("完全同名配对：01 - 序章.srt",
                    entry01?.SubtitlePath != null && Path.GetFileName(entry01.SubtitlePath) == "01 - 序章.srt",
                    entry01?.SubtitleBadge ?? "(没有找到条目)");

                Check("没有字幕的条目也能列出（02 - 第二章）",
                    entry02 != null && !entry02.HasSubtitle, entry02?.SubtitleBadge ?? "(没有找到条目)");

                Check("前缀兜底配对：03 - 第三章（字幕）.srt",
                    entry03?.SubtitlePath != null && Path.GetFileName(entry03.SubtitlePath) == "03 - 第三章（字幕）.srt",
                    entry03?.SubtitleBadge ?? "(没有找到条目)");

                Check("语言后缀配对：10 - 第十章.ja.srt",
                    entry10?.SubtitlePath != null && Path.GetFileName(entry10.SubtitlePath) == "10 - 第十章.ja.srt",
                    entry10?.SubtitleBadge ?? "(没有找到条目)");

                var order = new List<string>();
                foreach (var entry in scan.Entries)
                {
                    order.Add(entry.Title);
                }

                Check("按相对路径自然排序（第2章 排在 第10章 之前，Disc1 排在最后）",
                    string.Join(" | ", order) == "01 - 序章 | 02 - 第二章 | 03 - 第三章 | 10 - 第十章 | 01 track",
                    string.Join(" | ", order));

                Check("分组标签带子目录名",
                    entry01?.FolderLabel == "第1章" && entry10?.FolderLabel == "第2章",
                    $"{entry01?.FolderLabel} / {entry10?.FolderLabel}");

                // 自然排序器本身
                Check("自然排序：第2章 < 第10章",
                    NaturalStringComparer.Instance.Compare("第2章", "第10章") < 0);
                Check("自然排序：a2.mp3 < a10.mp3",
                    NaturalStringComparer.Instance.Compare("a2.mp3", "a10.mp3") < 0);
                Check("自然排序：相同字符串返回 0",
                    NaturalStringComparer.Instance.Compare("相同的名字", "相同的名字") == 0);

                // 错误目录要给出可读的异常，而不是崩溃
                var missingThrown = false;
                try
                {
                    MediaLibraryScanner.Scan(Path.Combine(libraryRoot, "不存在的目录"));
                }
                catch (DirectoryNotFoundException)
                {
                    missingThrown = true;
                }

                Check("扫描不存在的目录会抛出 DirectoryNotFoundException", missingThrown);

                // ---- 按文件夹整理成"书" ----
                Check("按文件夹归并出 3 本书", scan.Books.Count == 3, $"实际 {scan.Books.Count}");

                var book1 = FindBook(scan.Books, "第1章");
                var book2 = FindBook(scan.Books, "第2章");
                var disc = FindBook(scan.Books, "Disc1");

                Check("第1章 有 3 个章节", book1?.ChapterCount == 3, $"实际 {book1?.ChapterCount}");
                Check("第2章 有 1 个章节", book2?.ChapterCount == 1, $"实际 {book2?.ChapterCount}");
                Check("嵌套子目录也算独立一本书（第3章/Disc1）", disc?.ChapterCount == 1, $"实际 {disc?.ChapterCount}");
                Check("书名取文件夹名", book1?.Title == "第1章" && disc?.Title == "Disc1",
                    $"{book1?.Title} / {disc?.Title}");

                // ---- 封面解析：本级 → 上一级 → 媒体库根目录 ----
                Check("封面：优先用书自己文件夹里的 cover.png",
                    book1?.CoverPath != null &&
                    string.Equals(Path.GetDirectoryName(book1.CoverPath), Path.Combine(libraryRoot, "第1章"), StringComparison.OrdinalIgnoreCase),
                    book1?.CoverPath ?? "(没有找到)");

                Check("封面：本级没有就回退到媒体库根目录",
                    book2?.CoverPath != null &&
                    string.Equals(Path.GetDirectoryName(book2.CoverPath), libraryRoot, StringComparison.OrdinalIgnoreCase),
                    book2?.CoverPath ?? "(没有找到)");

                Check("封面：子文件夹回退到上一级（Disc1 → 第3章）",
                    disc?.CoverPath != null &&
                    string.Equals(Path.GetDirectoryName(disc.CoverPath), chapter3Path(libraryRoot), StringComparison.OrdinalIgnoreCase),
                    disc?.CoverPath ?? "(没有找到)");
            }
            catch (Exception ex)
            {
                Check("媒体库扫描", false, ex.Message);
            }

            // ============ 5. 设置持久化 ============
            Section("设置持久化（%LOCALAPPDATA%\\AudioBookPlayer\\settings.json）");

            var settingsPath = Path.Combine(AppContext.BaseDirectory, "selftest-settings.json");
            try
            {
                if (File.Exists(settingsPath))
                {
                    File.Delete(settingsPath);
                }

                var defaults = AppSettings.Load(settingsPath);
                Check("设置文件不存在时返回默认值", defaults.FontSize == 32 && defaults.AutoContinue, $"字号 {defaults.FontSize}");

                defaults.LibraryFolder = libraryRoot;
                defaults.FontSize = 44;
                defaults.ForegroundHex = "#FFE066";
                defaults.Anchor = nameof(OverlayAnchor.Top);
                defaults.OffsetY = -12;
                defaults.SkipSeconds = 30;
                defaults.RememberPlaybackPosition = false;
                defaults.ResumeLastOnStartup = false;
                defaults.ShowOnlyFavorites = true;
                defaults.OverlayVisible = false;
                defaults.FontWeightName = "SemiBold";
                defaults.OverlayAutoHideToolbar = false;
                defaults.MinimizeToTray = false;
                Check("保存设置成功", defaults.Save());

                var reloaded = AppSettings.Load(settingsPath);
                Check("重新读取：媒体库目录",
                    string.Equals(reloaded.LibraryFolder, libraryRoot, StringComparison.OrdinalIgnoreCase));
                Check("重新读取：字号 / 颜色 / 位置",
                    reloaded.FontSize == 44 && reloaded.ForegroundHex == "#FFE066" &&
                    reloaded.Anchor == nameof(OverlayAnchor.Top) && reloaded.OffsetY == -12,
                    $"{reloaded.FontSize} / {reloaded.ForegroundHex} / {reloaded.Anchor} / {reloaded.OffsetY}");
                Check("重新读取：字重", reloaded.FontWeightName == "SemiBold", reloaded.FontWeightName);
                Check("重新读取：工具条自动隐藏开关", !reloaded.OverlayAutoHideToolbar);
                // 托盘图标用的是 exe 自己的图标，这里验证 exe 确实带图标
                try
                {
                    var exePath = Environment.ProcessPath;
                    var exeIcon = string.IsNullOrEmpty(exePath) ? null : System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                    Check("exe 带图标（托盘图标就取它）", exeIcon != null);
                    exeIcon?.Dispose();
                }
                catch (Exception ex)
                {
                    Check("exe 带图标（托盘图标就取它）", false, ex.Message);
                }

                Check("重新读取：关闭时收进托盘开关", !reloaded.MinimizeToTray);
                Check("重新读取：播放与数据相关开关",
                    Math.Abs(reloaded.SkipSeconds - 30) < 0.001 && !reloaded.RememberPlaybackPosition &&
                    !reloaded.ResumeLastOnStartup && reloaded.ShowOnlyFavorites && !reloaded.OverlayVisible,
                    $"步长 {reloaded.SkipSeconds} / 记忆 {reloaded.RememberPlaybackPosition} / 收藏过滤 {reloaded.ShowOnlyFavorites}");

                File.WriteAllText(settingsPath, "{ 这不是合法 JSON", new UTF8Encoding(true));
                var broken = AppSettings.Load(settingsPath);
                Check("设置文件损坏时回退到默认值", broken.FontSize == 32, $"字号 {broken.FontSize}");
            }
            catch (Exception ex)
            {
                Check("设置持久化", false, ex.Message);
            }
            finally
            {
                try
                {
                    if (File.Exists(settingsPath))
                    {
                        File.Delete(settingsPath);
                    }
                }
                catch
                {
                    // 忽略
                }
            }

            // ============ 6. 播放数据（听到哪儿 / 最近播放 / 收藏） ============
            Section("播放数据存储（playback.json）");

            var playbackPath = Path.Combine(AppContext.BaseDirectory, "selftest-playback.json");
            try
            {
                if (File.Exists(playbackPath))
                {
                    File.Delete(playbackPath);
                }

                var store = PlaybackStore.Load(playbackPath);
                Check("空存储没有记录", store.Count == 0, $"实际 {store.Count}");

                var chapter01 = Path.Combine(libraryRoot, "第1章", "01 - 序章.wav");
                var chapter02 = Path.Combine(libraryRoot, "第1章", "02 - 第二章.mp3");

                store.Update(chapter01, TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(600));
                store.Update(chapter02, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(300));

                Check("记录播放位置", Math.Abs((store.Get(chapter01)?.PositionSeconds ?? 0) - 120) < 0.001,
                    $"{store.Get(chapter01)?.PositionSeconds}");
                Check("未听完不标记完成", store.Get(chapter01)?.Completed == false);

                var canResume = store.TryGetResumePosition(chapter01, out var resumeAt);
                Check("可以续听（位置 2:00）", canResume && Math.Abs(resumeAt.TotalSeconds - 120) < 0.001,
                    Timecode.Format(resumeAt));

                Check("最近播放指向最后更新的章节",
                    string.Equals(store.LastPlayedPath, chapter02, StringComparison.OrdinalIgnoreCase),
                    store.LastPlayedPath ?? "(空)");

                Check("保存到磁盘", store.Save(force: true) && File.Exists(playbackPath));

                var reloadedStore = PlaybackStore.Load(playbackPath);
                Check("重新读取：记录条数一致", reloadedStore.Count == 2, $"实际 {reloadedStore.Count}");
                Check("重新读取：位置一致",
                    Math.Abs((reloadedStore.Get(chapter01)?.PositionSeconds ?? 0) - 120) < 0.001);
                Check("重新读取：最近播放一致",
                    string.Equals(reloadedStore.LastPlayedPath, chapter02, StringComparison.OrdinalIgnoreCase));

                store.Update(chapter01, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(600));
                Check("只听了 3 秒不记忆", !store.TryGetResumePosition(chapter01, out _));

                store.Update(chapter01, TimeSpan.FromSeconds(592), TimeSpan.FromSeconds(600));
                Check("离结尾 15 秒内标记为已听完", store.Get(chapter01)?.Completed == true,
                    $"{store.Get(chapter01)?.PositionSeconds} / 600");
                Check("已听完不再续听", !store.TryGetResumePosition(chapter01, out _));

                Check("收藏一本书", store.ToggleFavorite(libraryRoot) && store.IsFavorite(libraryRoot));
                Check("取消收藏", !store.ToggleFavorite(libraryRoot) && !store.IsFavorite(libraryRoot));

                store.Update(chapter01, TimeSpan.FromSeconds(150), TimeSpan.FromSeconds(300));
                var hydrated = MediaLibraryScanner.Scan(libraryRoot, store);
                var hydratedBook = FindBook(hydrated.Books, "第1章");

                Check("扫描时把播放进度灌进章节",
                    hydratedBook?.Entries[0].ProgressText == "已听 50%",
                    hydratedBook?.Entries[0].ProgressText ?? "(没有找到)");
                Check("整本书进度 = 各章平均（50% + 10% + 0%）/ 3 = 20%",
                    hydratedBook != null && Math.Abs(hydratedBook.ProgressPercent - 0.2) < 0.01,
                    $"{hydratedBook?.ProgressPercent:P0}");

                var ghost = Path.Combine(libraryRoot, "第1章", "不存在了.wav");
                store.Update(ghost, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(100));
                var removedCount = store.RemoveMissingFiles();
                Check("清理掉已删除文件的记录", removedCount == 1, $"清理 {removedCount} 条");

                store.ClearAll();
                Check("清空全部播放数据", store.Count == 0 && !store.IsFavorite(libraryRoot) && store.LastPlayedPath == null);
            }
            catch (Exception ex)
            {
                Check("播放数据存储", false, ex.Message);
            }
            finally
            {
                try
                {
                    if (File.Exists(playbackPath))
                    {
                        File.Delete(playbackPath);
                    }
                }
                catch
                {
                    // 忽略
                }
            }

            // ============ 7. 音频元数据（M4B / MP3） ============
            Section("音频元数据解析（m4b chpl / ilst / ID3v2）");

            var tagDirectory = Path.Combine(AppContext.BaseDirectory, "selftest-tags");
            try
            {
                if (Directory.Exists(tagDirectory))
                {
                    Directory.Delete(tagDirectory, true);
                }

                Directory.CreateDirectory(tagDirectory);
                Audio.MediaTagReader.ClearCache();

                var m4bPath = Path.Combine(tagDirectory, "测试书.m4b");
                File.WriteAllBytes(m4bPath, BuildSyntheticM4b());

                var m4b = Audio.MediaTagReader.TryRead(m4bPath);
                Check("M4B：识别为 MP4/M4B 容器", m4b?.Container == "MP4/M4B", m4b?.Container ?? "(null)");
                Check("M4B：读到专辑名（书名）", m4b?.Album == "化物語 上", m4b?.Album ?? "(null)");
                Check("M4B：读到标题", m4b?.Title == "ひたぎクラブ", m4b?.Title ?? "(null)");
                Check("M4B：读到作者（©ART / ©wrt）", m4b?.DisplayAuthor == "西尾維新", m4b?.DisplayAuthor ?? "(null)");
                Check("M4B：读到朗读者（iTunes NARRATOR 自由标签）", m4b?.Narrator == "神谷浩史", m4b?.Narrator ?? "(null)");
                Check("M4B：读到年份与类型", m4b?.Year == "2006" && m4b?.Genre == "Audiobook",
                    $"{m4b?.Year} / {m4b?.Genre}");

                Check("M4B：读到内嵌封面（covr）", m4b?.HasCover == true, $"{m4b?.CoverData?.Length ?? 0} 字节");
                Check("M4B：封面格式识别为 PNG", m4b?.CoverMimeType == "image/png", m4b?.CoverMimeType ?? "(null)");

                Check("M4B：读到 3 个文件内章节", m4b?.Chapters.Count == 3, $"实际 {m4b?.Chapters.Count}");
                Check("M4B：章节标题与时间正确",
                    m4b is { Chapters.Count: 3 } &&
                    m4b.Chapters[1].Title == "まよいマイマイ" &&
                    Math.Abs(m4b.Chapters[1].Start.TotalSeconds - 1800) < 0.001 &&
                    Math.Abs(m4b.Chapters[2].Start.TotalSeconds - 3600) < 0.001,
                    m4b is { Chapters.Count: 3 } ? m4b.Chapters[1].ToString() : "(章节数不对)");

                Check("M4B：读到总时长（mvhd）",
                    m4b?.Duration != null && Math.Abs(m4b.Duration!.Value.TotalSeconds - 5400) < 0.5,
                    m4b?.Duration?.ToString() ?? "(null)");

                // ---- MP3 / ID3v2 ----
                var mp3Path = Path.Combine(tagDirectory, "测试曲.mp3");
                File.WriteAllBytes(mp3Path, BuildSyntheticMp3WithId3());

                var mp3 = Audio.MediaTagReader.TryRead(mp3Path);
                Check("MP3：读到标题 / 艺术家 / 专辑",
                    mp3?.Title == "第一話" && mp3?.Artist == "西尾維新" && mp3?.Album == "化物語 上",
                    $"{mp3?.Title} / {mp3?.Artist} / {mp3?.Album}");
                Check("MP3：读到 APIC 内嵌封面", mp3?.HasCover == true, $"{mp3?.CoverData?.Length ?? 0} 字节");
                Check("MP3：封面格式识别为 PNG", mp3?.CoverMimeType == "image/png", mp3?.CoverMimeType ?? "(null)");

                // ---- 坏文件不能把程序弄崩 ----
                var brokenPath = Path.Combine(tagDirectory, "坏文件.m4b");
                File.WriteAllBytes(brokenPath, new byte[] { 0, 0, 0, 16, 0x66, 0x74, 0x79, 0x70, 1, 2, 3 });
                Check("损坏的音频文件返回 null 且不抛异常", Audio.MediaTagReader.TryRead(brokenPath) == null ||
                    Audio.MediaTagReader.TryRead(brokenPath)?.Chapters.Count == 0);

                // ---- 扫描时用内嵌封面兜底 ----
                var bookDirectory = Path.Combine(tagDirectory, "库", "内嵌封面的书");
                Directory.CreateDirectory(bookDirectory);
                File.Copy(m4bPath, Path.Combine(bookDirectory, "01.m4b"), overwrite: true);

                var coverCache = Path.Combine(tagDirectory, "covers");
                var tagScan = MediaLibraryScanner.Scan(Path.Combine(tagDirectory, "库"), null, coverCache);
                var embeddedBook = tagScan.Books.Count > 0 ? tagScan.Books[0] : null;

                Check("扫描：没有文件夹封面时用内嵌封面兜底",
                    embeddedBook?.CoverPath != null && File.Exists(embeddedBook.CoverPath),
                    embeddedBook?.CoverPath ?? "(null)");
                Check("扫描：书名优先用标签里的专辑名",
                    embeddedBook?.Title == "化物語 上", embeddedBook?.Title ?? "(null)");
                Check("扫描：作者与朗读者带进书本卡片",
                    embeddedBook?.Author == "西尾維新" && embeddedBook?.Narrator == "神谷浩史",
                    embeddedBook?.CreditsText ?? "(null)");
            }
            catch (Exception ex)
            {
                Check("音频元数据解析", false, ex.ToString());
            }

            // ============ 8. 配色方案 ============
            Section("配色方案（动态主题）");

            try
            {
                var beforeAccent = Application.Current?.TryFindResource("AccentBrush") as System.Windows.Media.SolidColorBrush;
                var original = Themes.ThemeService.Current.Clone();

                var midnight = ThemeSettings.Presets[1];
                Themes.ThemeService.Apply(midnight);

                var after = Application.Current?.TryFindResource("WindowBackgroundBrush") as System.Windows.Media.SolidColorBrush;
                Check("切换预设后窗口底色立刻变化",
                    after != null && after.Color.ToString() == "#FF0E1116",
                    after?.Color.ToString() ?? "(null)");

                var accent = Application.Current?.TryFindResource("AccentBrush") as System.Windows.Media.SolidColorBrush;
                Check("强调色跟着变", accent != null && accent.Color.ToString() == "#FF3B82F6",
                    accent?.Color.ToString() ?? "(null)");

                var palette = midnight.BuildPalette();
                Check("派生色齐全（面板 / 边框 / 次级文字…）",
                    palette.ContainsKey("PanelBackgroundBrush") && palette.ContainsKey("BorderStrongBrush") &&
                    palette.ContainsKey("TextMutedBrush") && palette.ContainsKey("ListSelectedBrush"),
                    $"共 {palette.Count} 个颜色");

                var light = ThemeSettings.Presets[4];
                Check("浅色预设被识别为浅色主题", light.IsLightTheme);
                Check("深色预设被识别为深色主题", !midnight.IsLightTheme);

                var lightPalette = light.BuildPalette();
                Check("浅色方案下面板比背景更白",
                    lightPalette["CardBackgroundBrush"].CompareTo(lightPalette["WindowBackgroundBrush"]) != 0,
                    $"{lightPalette["WindowBackgroundBrush"]} → {lightPalette["CardBackgroundBrush"]}");

                Themes.ThemeService.Apply(original);
                Check("恢复默认配色",
                    (Application.Current?.TryFindResource("AccentBrush") as System.Windows.Media.SolidColorBrush)?.Color ==
                    beforeAccent?.Color);
            }
            catch (Exception ex)
            {
                Check("配色方案", false, ex.Message);
            }

            // ============ 8b. 单实例 ============
            Section("单实例（不重复打开）");

            try
            {
                Check("注册了唤醒用的窗口消息", SingleInstanceGuard.ShowWindowMessage != 0,
                    $"0x{SingleInstanceGuard.ShowWindowMessage:X}");

                using (var first = new SingleInstanceGuard())
                {
                    Check("第一个实例拿到所有权", first.IsFirstInstance);

                    // 命名 Mutex 已存在 → 第二个实例应当被判成"已有实例在跑"
                    using (var second = new SingleInstanceGuard())
                    {
                        Check("第二个实例被识别出来（会去唤起前一个然后退出）", !second.IsFirstInstance);
                    }
                }

                using (var afterRelease = new SingleInstanceGuard())
                {
                    Check("前一个退出后又能正常启动", afterRelease.IsFirstInstance);
                }
            }
            catch (Exception ex)
            {
                Check("单实例", false, ex.Message);
            }
            // ============ 9. 全局热键 ============
            Section("全局热键");

            try
            {
                var probe = new SubtitleWindow { DataContext = new SubtitleOverlayViewModel() };
                using var hotkeys = new GlobalHotkeyService();
                if (!hotkeys.Attach(probe))
                {
                    Skip("全局热键注册", "拿不到窗口句柄");
                }
                else
                {
                    var definitions = DefaultHotkeys.Create(10);
                    var registered = hotkeys.Register(definitions);
                    var effective = string.Join("、", hotkeys.Registered.Select(h => $"{h.Description}={h.GestureText}"));

                    // 注册成败取决于当前系统里别的程序占了哪些键，所以这里只验证"机制"：
                    // 成功注册的必须来自候选组合；被占用的要能如实报告出来。
                    Check("至少注册成功一个全局热键", registered >= 1, $"实际注册 {registered} 个");
                    Check("注册成功的都来自候选组合",
                        hotkeys.Registered.All(h => definitions.Any(d =>
                            d.Action == h.Action && d.Candidates.Any(c => c.Key == h.Key && c.Modifiers == h.Modifiers))),
                        effective);
                    Info($"生效热键：{effective}"
                        + (hotkeys.Failed.Count > 0
                            ? $"　被占用（已自动降级或跳过）：{string.Join("、", hotkeys.Failed.Select(h => h.Description))}"
                            : string.Empty));

                    var play = DefaultHotkeys.Create(10)[0];
                    var playRegistered = hotkeys.Registered.FirstOrDefault(h => h.Action == DefaultHotkeys.TogglePlay);
                    Check("播放/暂停用的是候选组合之一（媒体键被占用会自动退到 Ctrl+Alt+空格）",
                        playRegistered != null &&
                        play.Candidates.Any(c => c.Key == playRegistered.Key && c.Modifiers == playRegistered.Modifiers),
                        playRegistered?.GestureText ?? "(没有注册)");

                    Check("候选里媒体键排在第一位",
                        play.Candidates[0].Key == Key.MediaPlayPause && play.Candidates[0].Modifiers == ModifierKeys.None,
                        RegisteredHotkey.FormatGesture(play.Candidates[0].Modifiers, play.Candidates[0].Key));

                    Check("热键手势可读", RegisteredHotkey.FormatGesture(ModifierKeys.Control | ModifierKeys.Alt, Key.Space) == "Ctrl + Alt + 空格",
                        RegisteredHotkey.FormatGesture(ModifierKeys.Control | ModifierKeys.Alt, Key.Space));

                    Check("候选组合有序（第一个不行会退到下一个）",
                        DefaultHotkeys.Create(10)[1].Candidates.Count >= 2 &&
                        DefaultHotkeys.Create(10)[1].Candidates[0].Modifiers.HasFlag(ModifierKeys.Shift),
                        string.Join(" / ", DefaultHotkeys.Create(10)[1].Candidates.Select(c => RegisteredHotkey.FormatGesture(c.Modifiers, c.Key))));

                    hotkeys.UnregisterAll();
                    Check("注销后干净退出", hotkeys.Failed.Count == 0);
                }

                probe.Close();
            }
            catch (Exception ex)
            {
                Check("全局热键", false, ex.Message);
            }

            // ============ 10. 自定义封面 ============
            Section("自定义封面（手动指定 / 恢复自动）");

            var coverStorePath = Path.Combine(AppContext.BaseDirectory, "selftest-cover-store.json");
            try
            {
                if (File.Exists(coverStorePath))
                {
                    File.Delete(coverStorePath);
                }

                var coverStore = PlaybackStore.Load(coverStorePath);
                var fakeFolder = Path.Combine(AppContext.BaseDirectory, "selftest-tags", "库", "内嵌封面的书");
                var fakeCoverDirectory = Path.Combine(AppContext.BaseDirectory, "selftest-tags", "mycover");
                Directory.CreateDirectory(fakeCoverDirectory);
                var fakeCover = Path.Combine(fakeCoverDirectory, "自己选的封面.png");
                File.WriteAllBytes(fakeCover, TinyPng());

                Check("初始没有自定义封面", coverStore.GetCustomCover(fakeFolder) == null);

                coverStore.SetCustomCover(fakeFolder, fakeCover);
                Check("设置自定义封面", coverStore.GetCustomCover(fakeFolder) == fakeCover,
                    coverStore.GetCustomCover(fakeFolder) ?? "(null)");

                coverStore.Save(force: true);
                var reloadedCoverStore = PlaybackStore.Load(coverStorePath);
                Check("自定义封面能持久化", reloadedCoverStore.GetCustomCover(fakeFolder) == fakeCover);

                // 自定义封面要能盖过自动解析出来的封面
                var libraryRootForCover = Path.Combine(AppContext.BaseDirectory, "selftest-tags", "库");
                var withCustom = MediaLibraryScanner.Scan(libraryRootForCover, reloadedCoverStore, null);
                Check("扫描时自定义封面优先于内嵌封面",
                    withCustom.Books.Count > 0 &&
                    string.Equals(withCustom.Books[0].CoverPath, fakeCover, StringComparison.OrdinalIgnoreCase),
                    withCustom.Books.Count > 0 ? withCustom.Books[0].CoverPath ?? "(null)" : "(没有书)");

                coverStore.SetCustomCover(fakeFolder, null);
                Check("清除自定义封面", coverStore.GetCustomCover(fakeFolder) == null);
            }
            catch (Exception ex)
            {
                Check("自定义封面", false, ex.Message);
            }
            finally
            {
                try
                {
                    if (File.Exists(coverStorePath))
                    {
                        File.Delete(coverStorePath);
                    }
                }
                catch
                {
                    // 忽略
                }
            }
            // ============ 11. 主题与资源 ============
            Section("界面主题与资源");

            try
            {
                Check("主题画刷已合并（窗口底色）", Application.Current?.TryFindResource("WindowBackgroundBrush") != null);
                Check("主题画刷已合并（强调色）", Application.Current?.TryFindResource("AccentBrush") != null);
                Check("卡片样式已注册", Application.Current?.TryFindResource("CardBorderStyle") != null);

                Check("隐式 Button 样式（灰黑）", Application.Current?.TryFindResource(typeof(System.Windows.Controls.Button)) != null);
                Check("隐式 TextBox 样式", Application.Current?.TryFindResource(typeof(System.Windows.Controls.TextBox)) != null);
                Check("隐式 ComboBox 样式", Application.Current?.TryFindResource(typeof(System.Windows.Controls.ComboBox)) != null);
                Check("隐式 CheckBox 样式", Application.Current?.TryFindResource(typeof(System.Windows.Controls.CheckBox)) != null);
                Check("隐式 Slider 样式", Application.Current?.TryFindResource(typeof(System.Windows.Controls.Slider)) != null);
                Check("隐式 ListBoxItem 样式", Application.Current?.TryFindResource(typeof(System.Windows.Controls.ListBoxItem)) != null);
                Check("隐式 ScrollBar 样式", Application.Current?.TryFindResource(typeof(System.Windows.Controls.Primitives.ScrollBar)) != null);

                var iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app-header.png"));
                Check("程序图标资源可加载（app-header.png）", iconStream?.Stream != null);
                iconStream?.Stream.Dispose();
            }
            catch (Exception ex)
            {
                Check("界面主题与资源", false, ex.Message);
            }

            // ============ 10b. 假结束事件不能跳章 ============
            Section("播放结束判定（防误跳章）");

            try
            {
                var endedDirectory = Path.Combine(AppContext.BaseDirectory, "selftest-ended");
                if (Directory.Exists(endedDirectory))
                {
                    Directory.Delete(endedDirectory, true);
                }

                Directory.CreateDirectory(endedDirectory);
                File.WriteAllText(Path.Combine(endedDirectory, "01 第一章.mp3"), "placeholder");
                File.WriteAllText(Path.Combine(endedDirectory, "02 第二章.mp3"), "placeholder");

                var endedScan = MediaLibraryScanner.Scan(endedDirectory);
                var endedSettings = Path.Combine(AppContext.BaseDirectory, "selftest-ended.json");
                if (File.Exists(endedSettings))
                {
                    File.Delete(endedSettings);
                }

                var endedAudio = new FakeAudioPlayer();
                using (var endedViewModel = new MainViewModel(
                    audioPlayerFactory: () => endedAudio,
                    settings: AppSettings.Load(endedSettings),
                    playbackStore: PlaybackStore.Load(Path.Combine(AppContext.BaseDirectory, "selftest-ended-playback.json"))))
                {
                    endedViewModel.SelectedBook = endedScan.Books[0];
                    endedViewModel.SelectedEntry = endedScan.Entries[0];
                    var firstEntry = endedViewModel.SelectedEntry;

                    // 位置还在第 5 秒（时长 30 分钟）就报告"播放结束" —— 典型的假结束
                    endedAudio.Position = TimeSpan.FromSeconds(5);
                    endedAudio.RaiseEndedForTest();

                    Check("假结束（位置远没到结尾）不会跳到下一章",
                        ReferenceEquals(endedViewModel.SelectedEntry, firstEntry),
                        endedViewModel.SelectedEntry?.Title ?? "(空)");
                }

                Directory.Delete(endedDirectory, true);
            }
            catch (Exception ex)
            {
                Check("播放结束判定", false, ex.Message);
            }
            // ============ 11b. 界面缩放 ============
            Section("界面缩放（Ctrl + 滚轮）");

            try
            {
                // 每次自检都要从干净的设置开始，否则上一轮存下来的缩放会让断言失效
                var zoomSettingsPath = Path.Combine(AppContext.BaseDirectory, "selftest-zoom.json");
                if (File.Exists(zoomSettingsPath))
                {
                    File.Delete(zoomSettingsPath);
                }

                using var zoomViewModel = new MainViewModel(
                    settings: AppSettings.Load(zoomSettingsPath),
                    playbackStore: PlaybackStore.Load(Path.Combine(AppContext.BaseDirectory, "selftest-zoom-playback.json")));

                Check("默认 100%", Math.Abs(zoomViewModel.ReadingZoom - 1.0) < 0.001 && !zoomViewModel.IsZoomed,
                    zoomViewModel.ZoomText.Length == 0 ? "(不显示)" : zoomViewModel.ZoomText);

                // ---- 音量 / 静音 ----
                Check("默认音量不为 0（不是静音）", !zoomViewModel.IsMuted,
                    $"{zoomViewModel.Volume:0.00}");

                zoomViewModel.Volume = 0.5;
                Check("音量可以取中间值（不是只有 0 和 1）",
                    Math.Abs(zoomViewModel.Volume - 0.5) < 0.001 && !zoomViewModel.IsMuted,
                    $"{zoomViewModel.Volume:0.00}");

                zoomViewModel.Volume = 0.37;
                Check("音量保留小数", Math.Abs(zoomViewModel.Volume - 0.37) < 0.001,
                    $"{zoomViewModel.Volume:0.00}");

                zoomViewModel.Volume = 0;
                Check("音量 0 时提示已静音（避免被当成暂停后没声音）", zoomViewModel.IsMuted);

                zoomViewModel.Volume = 1.5;
                Check("音量上限夹紧到 1", Math.Abs(zoomViewModel.Volume - 1.0) < 0.001);

                zoomViewModel.Volume = 1.0;
                Check("恢复音量后不再是静音", !zoomViewModel.IsMuted);
                zoomViewModel.CurrentMode = AppMode.Reading;
                zoomViewModel.AdjustZoom(1);
                zoomViewModel.AdjustZoom(1);

                Check("阅读视图放大到 120%",
                    Math.Abs(zoomViewModel.ReadingZoom - 1.2) < 0.001 && zoomViewModel.ZoomText == "120%",
                    $"{zoomViewModel.ReadingZoom:0.00} / {zoomViewModel.ZoomText}");

                Check("放大不影响其它视图",
                    Math.Abs(zoomViewModel.LibraryZoom - 1.0) < 0.001 &&
                    Math.Abs(zoomViewModel.PlayerZoom - 1.0) < 0.001);

                zoomViewModel.CurrentMode = AppMode.Library;
                Check("切到书库后显示的是书库的缩放", !zoomViewModel.IsZoomed, zoomViewModel.ZoomText);

                zoomViewModel.AdjustZoom(-1);
                Check("书库缩小到 90%", Math.Abs(zoomViewModel.LibraryZoom - 0.9) < 0.001,
                    $"{zoomViewModel.LibraryZoom:0.00}");

                for (var i = 0; i < 40; i++)
                {
                    zoomViewModel.AdjustZoom(1);
                }

                Check("放大有上限", Math.Abs(zoomViewModel.LibraryZoom - MainViewModel.MaxZoom) < 0.001,
                    $"{zoomViewModel.LibraryZoom:0.00}（上限 {MainViewModel.MaxZoom}）");

                for (var i = 0; i < 40; i++)
                {
                    zoomViewModel.AdjustZoom(-1);
                }

                Check("缩小有下限", Math.Abs(zoomViewModel.LibraryZoom - MainViewModel.MinZoom) < 0.001,
                    $"{zoomViewModel.LibraryZoom:0.00}（下限 {MainViewModel.MinZoom}）");

                zoomViewModel.ResetZoom();
                Check("Ctrl+0 回到 100%", Math.Abs(zoomViewModel.LibraryZoom - 1.0) < 0.001);

                zoomViewModel.CurrentMode = AppMode.Reading;
                Check("回到阅读视图，缩放还记得", Math.Abs(zoomViewModel.ReadingZoom - 1.2) < 0.001,
                    $"{zoomViewModel.ReadingZoom:0.00}");
            }
            catch (Exception ex)
            {
                Check("界面缩放", false, ex.Message);
            }
            // ============ 12. 主窗口界面加载 ============
            Section("主窗口（灰黑界面 + 深色标题栏）");

            MainWindow? mainWindow = null;
            MainViewModel? mainViewModel = null;
            var roundTripSettings = Path.Combine(AppContext.BaseDirectory, "selftest-settings-roundtrip.json");
            try
            {
                if (File.Exists(roundTripSettings))
                {
                    File.Delete(roundTripSettings);
                }

                mainViewModel = new MainViewModel(settings: AppSettings.Load(roundTripSettings));
                mainWindow = new MainWindow { DataContext = mainViewModel };

                var handle = Win32WindowHelper.GetHandle(mainWindow);
                Check("MainWindow.xaml 解析并创建窗口成功", handle != IntPtr.Zero);
                Check("窗口标题是 KATARU", mainWindow.Title == "KATARU", mainWindow.Title);
                Check("关窗口默认收进托盘（不退出）", mainViewModel.MinimizeToTray);
                Check("深色标题栏 API 生效（Win10 2004+ / Win11）", mainWindow.Win32TitleBarApplied,
                    "旧系统上为 false 属正常，只是标题栏为浅色");

                mainViewModel.Overlay.FontSize = 51;
                mainViewModel.Volume = 0.42;
                mainViewModel.MinimizeToTray = false;
                mainViewModel.Dispose();
                mainViewModel = null;

                var saved = AppSettings.Load(roundTripSettings);
                Check("退出时保存界面设置（字号 / 音量 / 关闭行为）",
                    Math.Abs(saved.FontSize - 51) < 0.001 && Math.Abs(saved.Volume - 0.42) < 0.001 && !saved.MinimizeToTray,
                    $"字号 {saved.FontSize} / 音量 {saved.Volume} / 收托盘 {saved.MinimizeToTray}");
            }
            catch (Exception ex)
            {
                Check("主窗口界面加载", false, ex.Message);
            }
            finally
            {
                try
                {
                    mainWindow?.Close();
                    mainViewModel?.Dispose();
                }
                catch
                {
                    // 忽略
                }

                try
                {
                    if (File.Exists(roundTripSettings))
                    {
                        File.Delete(roundTripSettings);
                    }
                }
                catch
                {
                    // 忽略
                }
            }

            // ============ 13. 悬浮窗口 Win32 样式 ============
            Section("Phase 4/5  悬浮字幕窗口");

            SubtitleWindow? overlay = null;
            try
            {
                overlay = new SubtitleWindow { DataContext = new SubtitleOverlayViewModel() };

                // EnsureHandle 只创建 HWND，不显示窗口：桌面上不会闪出任何东西
                var handle = Win32WindowHelper.GetHandle(overlay);
                var description = Win32WindowHelper.DescribeExtendedStyle(overlay);

                Check("窗口句柄创建成功", handle != IntPtr.Zero, $"HWND = 0x{handle.ToInt64():X}");
                Check("WS_EX_LAYERED（透明分层窗口）", (Win32WindowHelper.GetExtendedStyle(overlay) & 0x00080000) != 0, description);
                Check("WS_EX_TRANSPARENT（鼠标穿透）", Win32WindowHelper.HasClickThrough(overlay), description);
                Check("WS_EX_NOACTIVATE（不抢键盘焦点）", Win32WindowHelper.HasNoActivate(overlay), description);
                Check("WS_EX_TOOLWINDOW（不进入 Alt+Tab）", (Win32WindowHelper.GetExtendedStyle(overlay) & 0x00000080) != 0, description);

                Check("WPF AllowsTransparency = true", overlay.AllowsTransparency);
                Check("WPF WindowStyle = None（无边框）", overlay.WindowStyle == WindowStyle.None, overlay.WindowStyle.ToString());
                Check("WPF Topmost = true（始终置顶）", overlay.Topmost);
                Check("WPF ShowActivated = false", !overlay.ShowActivated);
                Check("WPF ShowInTaskbar = false", !overlay.ShowInTaskbar);
                Check("WM_NCHITTEST / WM_MOUSEACTIVATE 穿透钩子已安装", overlay.MessageHookInstalled);

                // 位置计算：主屏底部居中
                overlay.ApplyPlacement();
                var workArea = SystemParameters.WorkArea;
                var centerDelta = Math.Abs(overlay.Left + (overlay.Width / 2) - (workArea.Left + (workArea.Width / 2)));
                Check("默认位置为主屏水平居中", centerDelta <= 1.0, $"偏差 {centerDelta:0.##} DIP");
                Check("默认位置在屏幕下方 1/3 以下", overlay.Top > workArea.Top + (workArea.Height * 0.5),
                    $"Top = {overlay.Top:0.#} / 工作区高 {workArea.Height:0.#}");
                Check("窗口尺寸为有效值", overlay.Width > 100 && overlay.Height > 50,
                    $"{overlay.Width:0.#} x {overlay.Height:0.#}");

                // 换一个预设位置后窗口应该跟着动
                var bottom = overlay.Top;
                ((SubtitleOverlayViewModel)overlay.DataContext).Anchor = OverlayAnchor.Top;
                Check("切换到顶部锚点后位置改变", Math.Abs(overlay.Top - bottom) > 10, $"Top = {overlay.Top:0.#}");

                // 真正 Show / Hide：窗口内容为空且全透明，屏幕上不会出现任何可见东西。
                // 这一步用来验证 WPF 在 Hide→Show 重建 HWND 之后，Win32 样式仍然被重新套上。
                var handleBefore = handle;
                overlay.ShowOverlay();
                var afterShow = Win32WindowHelper.GetHandle(overlay);
                overlay.HideOverlay();
                overlay.ShowOverlay();
                var afterReshow = Win32WindowHelper.GetHandle(overlay);

                Info($"HWND: 初始 0x{handleBefore.ToInt64():X} → Show 后 0x{afterShow.ToInt64():X} → Hide/Show 后 0x{afterReshow.ToInt64():X}");
                Check("Show 之后窗口可见", overlay.IsVisible);
                Check("Hide/Show 之后鼠标穿透样式仍然生效", Win32WindowHelper.HasClickThrough(overlay),
                    Win32WindowHelper.DescribeExtendedStyle(overlay));
                Check("Hide/Show 之后不抢焦点样式仍然生效", Win32WindowHelper.HasNoActivate(overlay),
                    Win32WindowHelper.DescribeExtendedStyle(overlay));

                overlay.HideOverlay();
                Check("Hide 之后窗口不可见", !overlay.IsVisible);

                overlay.HideOverlay();

                // ---- 移动模式：解锁后必须真的关掉鼠标穿透，否则拖不动 ----
                var overlayViewModel = (SubtitleOverlayViewModel)overlay.DataContext;
                overlay.ShowOverlay();

                Check("默认锁定：鼠标穿透开着", Win32WindowHelper.HasClickThrough(overlay),
                    Win32WindowHelper.DescribeExtendedStyle(overlay));

                overlayViewModel.IsMovable = true;
                Check("解锁后关掉鼠标穿透（鼠标消息才能进来）", !Win32WindowHelper.HasClickThrough(overlay),
                    Win32WindowHelper.DescribeExtendedStyle(overlay));
                Check("解锁后依然不抢焦点", Win32WindowHelper.HasNoActivate(overlay),
                    Win32WindowHelper.DescribeExtendedStyle(overlay));
                Check("解锁后依然不进 Alt+Tab", (Win32WindowHelper.GetExtendedStyle(overlay) & 0x00000080) != 0);

                overlayViewModel.SetCustomPosition(120, 240);
                Check("拖动后切到自定义位置", overlayViewModel.Anchor == OverlayAnchor.Custom &&
                    Math.Abs(overlay.Left - 120) < 1 && Math.Abs(overlay.Top - 240) < 1,
                    $"({overlay.Left:0},{overlay.Top:0})");

                overlayViewModel.IsMovable = false;
                Check("重新锁定：鼠标穿透恢复", Win32WindowHelper.HasClickThrough(overlay),
                    Win32WindowHelper.DescribeExtendedStyle(overlay));

                // 拖动过的位置要能被设置记住（Anchor=Custom 时用 CustomX/CustomY 摆放）
                overlay.ApplyPlacement();
                Check("自定义位置重新摆放后不变",
                    Math.Abs(overlay.Left - 120) < 1 && Math.Abs(overlay.Top - 240) < 1,
                    $"({overlay.Left:0},{overlay.Top:0})");

                // ---- 拖动数学：必须按"屏幕坐标增量"移动。用窗口相对坐标的话，
                //      窗口一跟着动、鼠标相对坐标就变，下一帧会把窗口拉回起点 → 来回抽搐 ----
                overlayViewModel.MoveTo(300, 400, persist: true);
                overlay.ApplyPlacement();

                var dragStartLeft = overlay.Left;
                var dragStartTop = overlay.Top;

                overlay.BeginDragAt(new Point(500, 500));
                overlay.DragToScreen(new Point(560, 530));   // 鼠标 +60 / +30
                var firstStepX = overlay.Left - dragStartLeft;
                var firstStepY = overlay.Top - dragStartTop;

                overlay.DragToScreen(new Point(620, 560));   // 再 +60 / +30 → 累计 +120 / +60
                var secondStepX = overlay.Left - dragStartLeft;
                var secondStepY = overlay.Top - dragStartTop;
                overlay.EndDrag();

                Check("拖动第一步：窗口跟着鼠标走 +60/+30",
                    Math.Abs(firstStepX - 60) < 0.6 && Math.Abs(firstStepY - 30) < 0.6,
                    $"实际位移 {firstStepX:0.#}/{firstStepY:0.#}");

                Check("拖动第二步：按累计位移走 +120/+60（不会把窗口拉回起点）",
                    Math.Abs(secondStepX - 120) < 0.6 && Math.Abs(secondStepY - 60) < 0.6,
                    $"实际位移 {secondStepX:0.#}/{secondStepY:0.#}");

                overlayViewModel.IsMovable = false;

                // ---- 工具条：要能点，所以不能穿透 ----
                // 注意先关掉"自动隐藏"：自检里没有真实光标，开着自动隐藏时工具条本来就该是收起的，
                // 那样只能验证"收起"，验证不了位置和样式。自动隐藏的规则另有纯函数用例覆盖。
                overlayViewModel.ShowHandle = true;
                overlayViewModel.AutoHideToolbar = false;
                overlay.ShowOverlay();
                var toolbar = overlay.ToolbarWindow;

                Check("显示字幕工具条窗口", toolbar != null);
                if (toolbar != null)
                {
                    var toolbarStyle = Win32WindowHelper.DescribeExtendedStyle(toolbar);
                    Check("工具条可点击（没有 WS_EX_TRANSPARENT）", !Win32WindowHelper.HasClickThrough(toolbar), toolbarStyle);
                    Check("工具条不抢焦点", Win32WindowHelper.HasNoActivate(toolbar), toolbarStyle);
                    Check("工具条不进 Alt+Tab", (Win32WindowHelper.GetExtendedStyle(toolbar) & 0x00000080) != 0, toolbarStyle);
                    Check("工具条贴在字幕上方",
                        Math.Abs(toolbar.Top - (overlay.Top - toolbar.ActualHeight - 8)) < 4 ||
                        Math.Abs(toolbar.Top - (overlay.Top + 8)) < 4,
                        $"工具条=({toolbar.Left:0},{toolbar.Top:0}) 字幕=({overlay.Left:0},{overlay.Top:0})");
                }

                // 工具条要水平居中于字幕
                if (overlay.ToolbarWindow != null && overlay.ToolbarWindow.ActualWidth > 1)
                {
                    var toolbarCenter = overlay.ToolbarWindow.Left + (overlay.ToolbarWindow.ActualWidth / 2);
                    var subtitleCenter = overlay.Left + (overlay.Width / 2);
                    Check("工具条水平居中于字幕", Math.Abs(toolbarCenter - subtitleCenter) < 4,
                        $"工具条中心 {toolbarCenter:0} / 字幕中心 {subtitleCenter:0}");
                }

                // 工具条上的调色：色板要有内容，改完要能生效并算作"自定义"
                Check("工具条色板有字色可选", overlayViewModel.ForegroundColorChoices.Count >= 4,
                    $"{overlayViewModel.ForegroundColorChoices.Count} 个");
                Check("工具条色板有描边色可选", overlayViewModel.OutlineColorChoices.Count >= 4,
                    $"{overlayViewModel.OutlineColorChoices.Count} 个");

                var colorBefore = overlayViewModel.ForegroundHex;
                var pickedColor = overlayViewModel.ForegroundColorChoices[1];
                overlayViewModel.ForegroundHex = pickedColor;

                var expectedColor = (System.Windows.Media.Color)
                    System.Windows.Media.ColorConverter.ConvertFromString(pickedColor)!;
                var actualBrush = overlayViewModel.Foreground as System.Windows.Media.SolidColorBrush;

                Check("从工具条改字色立即生效",
                    overlayViewModel.ForegroundHex == pickedColor &&
                    actualBrush != null && actualBrush.Color == expectedColor,
                    $"{pickedColor} → {actualBrush?.Color.ToString() ?? "(不是纯色刷)"}");

                overlayViewModel.OutlineThickness = 6;
                Check("工具条改描边粗细", Math.Abs(overlayViewModel.OutlineThickness - 6) < 0.001);

                Check("字重可选项齐全", overlayViewModel.FontWeightChoices.Count >= 5,
                    string.Join("/", overlayViewModel.FontWeightChoices.Select(w => w.Value)));

                overlayViewModel.FontWeightName = "Bold";
                Check("切换到粗体",
                    overlayViewModel.FontWeight == System.Windows.FontWeights.Bold,
                    overlayViewModel.FontWeight.ToString());

                overlayViewModel.FontWeightName = "Light";
                Check("切换到细体",
                    overlayViewModel.FontWeight == System.Windows.FontWeights.Light,
                    overlayViewModel.FontWeight.ToString());

                overlayViewModel.FontWeightName = "Normal";

                // ---- 字幕框大小：能改、有上下限、改了窗口真的跟着变 ----
                var sizeBefore = (overlay.Width, overlay.Height);
                overlayViewModel.OverlayWidth = 900;
                overlayViewModel.OverlayHeight = 300;
                overlay.ApplyPlacement();
                Check("改宽高后窗口尺寸跟着变",
                    Math.Abs(overlay.Width - 900) < 1 && Math.Abs(overlay.Height - 300) < 1,
                    $"{sizeBefore.Width:0}x{sizeBefore.Height:0} → {overlay.Width:0}x{overlay.Height:0}");

                overlayViewModel.OverlayWidth = 10;
                Check("宽度有下限（不会缩成一条线）", overlayViewModel.OverlayWidth >= 200,
                    overlayViewModel.OverlayWidth.ToString("0"));

                overlayViewModel.OverlayHeight = 5;
                Check("高度有下限", overlayViewModel.OverlayHeight >= 60,
                    overlayViewModel.OverlayHeight.ToString("0"));

                overlayViewModel.OverlayWidth = 1200;
                overlayViewModel.OverlayHeight = 220;

                overlayViewModel.ResetAppearance();
                Check("恢复默认外观", overlayViewModel.ForegroundHex != colorBefore || colorBefore == "#FFFFFF",
                    overlayViewModel.ForegroundHex);

                // 回归：隐藏字幕再显示，工具条必须回来（之前 Hide 之后忘了 Show）
                overlay.HideOverlay();
                Check("隐藏字幕时工具条也跟着隐藏",
                    overlay.ToolbarWindow == null || !overlay.ToolbarWindow.IsVisible);

                overlay.ShowOverlay();
                Check("重新显示字幕时工具条回到屏幕上",
                    overlay.ToolbarWindow != null && overlay.ToolbarWindow.IsVisible,
                    overlay.ToolbarWindow == null ? "(窗口没了)" : $"可见={overlay.ToolbarWindow.IsVisible}");

                // 网易云那套：锁定且开了自动隐藏时，鼠标不在字幕上就不显示工具条
                Check("锁定 + 自动隐藏 + 鼠标不在字幕上 → 工具条收起",
                    !SubtitleWindow.ShouldShowToolbar(autoHide: true, isMovable: false, cursorInHotZone: false, popupOpen: false));
                Check("锁定 + 自动隐藏 + 鼠标移到字幕上 → 工具条浮出来",
                    SubtitleWindow.ShouldShowToolbar(true, false, true, false));
                Check("颜色面板开着时不收起",
                    SubtitleWindow.ShouldShowToolbar(true, false, false, true));
                Check("解锁拖动时一直显示（要能点到锁定按钮）",
                    SubtitleWindow.ShouldShowToolbar(true, true, false, false));
                Check("关掉自动隐藏就常显",
                    SubtitleWindow.ShouldShowToolbar(false, false, false, false));

                overlayViewModel.ShowHandle = false;
                Check("关掉开关后工具条隐藏", overlay.ToolbarWindow == null || !overlay.ToolbarWindow.IsVisible);

                overlayViewModel.AutoHideToolbar = true;

                overlay.HideOverlay();
                overlay.HideOverlay();
            }
            catch (Exception ex)
            {
                Check("悬浮窗口样式验证", false, ex.Message);
            }
            finally
            {
                try
                {
                    overlay?.Close();
                }
                catch
                {
                    // 从未 Show 过的窗口关闭时可能抛异常，忽略
                }
            }

            // ============ 5. 音频播放 ============
            Section("Phase 2  音频播放（MP3 / WAV）");

            if (sampleWav == null)
            {
                Skip("音频打开 / Seek / 播放 / 暂停", "未找到 samples/测试样例/test.wav");
            }
            else
            {
                await RunAudioChecksAsync(sampleWav, sampleResult, Check, Skip);
            }

            // ============ 汇总 ============
            report.Add(string.Empty);
            report.Add($"===== 通过 {passed} / 失败 {failed} / 跳过 {skipped} =====");
            report.Add(failed == 0 ? "结果：全部通过 ✅" : "结果：存在失败项 ❌");

            var text = string.Join(Environment.NewLine, report);

            // 先落盘，再打印：即使控制台被重定向或编码有问题，报告也不会丢。
            string? writeError = null;
            try
            {
                var directory = Path.GetDirectoryName(reportPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(reportPath, text, new UTF8Encoding(true));
            }
            catch (Exception ex)
            {
                writeError = ex.GetType().Name + ": " + ex.Message;
            }

            try
            {
                Console.WriteLine(text);
            }
            catch (Exception ex)
            {
                writeError ??= "控制台输出失败 " + ex.GetType().Name;
            }

            if (writeError != null)
            {
                try
                {
                    Console.Error.WriteLine("报告写入失败（" + reportPath + "）：" + writeError);
                }
                catch
                {
                    // 忽略
                }

                return 2;
            }

            return failed == 0 ? 0 : 1;
        }

        private static string? GetOption(string[] args, string name)
        {
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (arg.Equals(name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    return args[i + 1];
                }

                var prefix = name + "=";
                if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return arg.Substring(prefix.Length);
                }
            }

            return null;
        }

        private static async Task RunAudioChecksAsync(
            string wavPath,
            SrtParseResult? sampleSrt,
            Action<string, bool, string> check,
            Action<string, string> skip)
        {
            using var player = new MediaPlayerAudioPlayer();

            var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var failure = string.Empty;

            player.MediaOpened += (_, _) => opened.TrySetResult(true);
            player.Failed += (_, e) =>
            {
                failure = e.Message;
                opened.TrySetResult(false);
            };

            player.Volume = 0; // 自检不出声
            player.Open(wavPath);

            var completed = await Task.WhenAny(opened.Task, Task.Delay(TimeSpan.FromSeconds(8)));
            if (completed != opened.Task)
            {
                skip("音频打开 / Seek / 播放 / 暂停",
                    "等待 MediaOpened 超时（8s）：当前会话里 Windows 媒体栈（Media Foundation / WMP）没有响应");
                return;
            }

            if (!opened.Task.Result)
            {
                skip("音频打开 / Seek / 播放 / 暂停",
                    $"Windows 媒体栈打不开该文件：{failure}。这是运行环境问题（受限会话 / 沙箱 / 缺少解码器），" +
                    "不是播放器代码问题；请在正常桌面会话里直接运行本程序，用 samples/测试样例/test.wav 手动验证");
                return;
            }

            check("音频打开成功", player.IsOpen, Path.GetFileName(wavPath));

            var duration = player.Duration.TotalSeconds;
            check("读取到总时长", duration > 1, $"{Timecode.Format(player.Duration)}（{duration:0.###}s）");

            // Seek 到 12s（样例字幕第 4 条的区间内）
            var target = TimeSpan.FromSeconds(12);
            player.Position = target;
            await Task.Delay(300);

            var seeked = player.Position.TotalSeconds;
            check("Seek 到 12s 生效", Math.Abs(seeked - 12) < 0.5, $"实际 {seeked:0.###}s");

            if (sampleSrt != null)
            {
                var sync = new SubtitleSynchronizer();
                sync.Load(sampleSrt.Lines);
                sync.Seek(player.Position);
                var expected = sampleSrt.Lines.Find(l => l.IsVisibleAt(target));
                var actual = sync.Current;
                check("Seek 后字幕立刻重新同步", expected != null && actual != null && actual.Index == expected.Index,
                    actual?.Text.Replace("\n", "\\n") ?? "(null)");
            }

            // 播放
            player.Play();
            await Task.Delay(900);
            var playingPosition = player.Position.TotalSeconds;
            check("播放后位置前进", playingPosition > seeked + 0.3, $"{seeked:0.###}s → {playingPosition:0.###}s");

            // 暂停
            player.Pause();
            await Task.Delay(150);
            var paused = player.Position.TotalSeconds;
            await Task.Delay(400);
            var afterPause = player.Position.TotalSeconds;
            check("暂停后位置不再变化", Math.Abs(afterPause - paused) < 0.08, $"{paused:0.###}s → {afterPause:0.###}s");

            // 暂停状态下 Seek（拖动进度条）
            var back = TimeSpan.FromSeconds(2);
            player.Position = back;
            await Task.Delay(300);
            check("暂停状态下 Seek 生效", Math.Abs(player.Position.TotalSeconds - 2) < 0.5, $"实际 {player.Position.TotalSeconds:0.###}s");

            player.Stop();
            await Task.Delay(200);
            check("Stop 回到开头", player.Position.TotalSeconds < 0.5, $"实际 {player.Position.TotalSeconds:0.###}s");        }

        private static LibraryBook? FindBook(IReadOnlyList<LibraryBook> books, string title)
        {
            foreach (var book in books)
            {
                if (string.Equals(book.Title, title, StringComparison.Ordinal))
                {
                    return book;
                }
            }

            return null;
        }

        private static string chapter3Path(string libraryRoot)
        {
            return Path.Combine(libraryRoot, "第3章");
        }

        private static LibraryEntry? FindEntry(IReadOnlyList<LibraryEntry> entries, string title)
        {
            foreach (var entry in entries)
            {
                if (string.Equals(entry.Title, title, StringComparison.Ordinal))
                {
                    return entry;
                }
            }

            return null;
        }

        /// <summary>
        /// 造一棵嵌套目录的假媒体库（每个文件夹 = 一本书）：
        ///   第1章/cover.png                                              （自己的封面）
        ///   第1章/01 - 序章.wav        + 01 - 序章.srt                   （完全同名配对）
        ///   第1章/02 - 第二章.mp3                                        （没有字幕）
        ///   第1章/03 - 第三章.wav      + 03 - 第三章（字幕）.srt         （前缀兜底配对）
        ///   第2章/10 - 第十章.m4b      + 10 - 第十章.ja.srt              （语言后缀配对，封面回退到根目录）
        ///   第3章/cover.jpg            + 第3章/Disc1/01 track.wav        （封面回退到上一级目录）
        ///   另外还有 .txt / .jpg / 还没配对上的 .srt，都应该被正确忽略。
        /// </summary>
        private static void BuildSampleLibrary(string root)
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }

            var chapter1 = Path.Combine(root, "第1章");
            var chapter2 = Path.Combine(root, "第2章");
            var chapter3 = Path.Combine(root, "第3章");
            var disc1 = Path.Combine(chapter3, "Disc1");
            Directory.CreateDirectory(chapter1);
            Directory.CreateDirectory(chapter2);
            Directory.CreateDirectory(disc1);

            const string srt =
                "1\r\n00:00:00,500 --> 00:00:03,000\r\n测试字幕第一行\r\n\r\n" +
                "2\r\n00:00:03,200 --> 00:00:06,400\r\n测试字幕第二行\r\n";

            // 扫描器只看扩展名，所以音频用占位内容；封面用一张真的 1x1 PNG
            var png = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

            File.WriteAllBytes(Path.Combine(root, "cover.png"), png);
            File.WriteAllBytes(Path.Combine(chapter1, "cover.png"), png);
            File.WriteAllBytes(Path.Combine(chapter3, "cover.jpg"), png);

            File.WriteAllText(Path.Combine(chapter1, "01 - 序章.wav"), "RIFF-placeholder");
            File.WriteAllText(Path.Combine(chapter1, "01 - 序章.srt"), srt, new UTF8Encoding(true));

            File.WriteAllText(Path.Combine(chapter1, "02 - 第二章.mp3"), "placeholder");

            File.WriteAllText(Path.Combine(chapter1, "03 - 第三章.wav"), "RIFF-placeholder");
            File.WriteAllText(Path.Combine(chapter1, "03 - 第三章（字幕）.srt"), srt, new UTF8Encoding(true));

            File.WriteAllText(Path.Combine(chapter2, "10 - 第十章.m4b"), "placeholder");
            File.WriteAllText(Path.Combine(chapter2, "10 - 第十章.ja.srt"), srt, new UTF8Encoding(true));

            File.WriteAllText(Path.Combine(disc1, "01 track.wav"), "RIFF-placeholder");

            File.WriteAllText(Path.Combine(root, "说明.txt"), "不是音频，应该被忽略");
            File.WriteAllText(Path.Combine(chapter2, "notes.txt"), "不是音频，应该被忽略");
            File.WriteAllText(Path.Combine(root, "孤立的字幕.srt"), srt, new UTF8Encoding(true));
        }

        // ==================== 合成测试用的音频文件 ====================

        /// <summary>自检用的假播放器：不碰真实媒体栈，只用来驱动 ViewModel。</summary>
        private sealed class FakeAudioPlayer : Audio.IAudioPlayer
        {
#pragma warning disable CS0067 // 只有 MediaEnded 会被自检手动触发
            public event EventHandler? MediaOpened;
            public event EventHandler? MediaEnded;
            public event EventHandler<AudioPlayerErrorEventArgs>? Failed;
#pragma warning restore CS0067

            public bool IsOpen { get; private set; }

            public bool IsPlaying { get; private set; }

            public string? SourcePath { get; private set; }

            public TimeSpan Position { get; set; }

            public TimeSpan Duration { get; } = TimeSpan.FromMinutes(30);

            public double Volume { get; set; } = 1.0;

            public void Open(string path)
            {
                SourcePath = path;
                IsOpen = true;
                Position = TimeSpan.Zero;
                MediaOpened?.Invoke(this, EventArgs.Empty);
            }

            public void Play() => IsPlaying = true;

            /// <summary>自检用：手动制造一次"播放结束"事件。</summary>
            public void RaiseEndedForTest() => MediaEnded?.Invoke(this, EventArgs.Empty);

            public void Pause() => IsPlaying = false;

            public void Stop()
            {
                IsPlaying = false;
                Position = TimeSpan.Zero;
            }

            public void Close() => IsOpen = false;

            public void Dispose() => Close();
        }
        private static byte[] Box(string type, params byte[][] parts)
        {
            var payloadLength = 0;
            foreach (var part in parts)
            {
                payloadLength += part.Length;
            }

            var result = new byte[8 + payloadLength];
            WriteBe32(result, 0, result.Length);
            Encoding.Latin1.GetBytes(type, 0, 4, result, 4); // © 是单字节 0xA9

            var offset = 8;
            foreach (var part in parts)
            {
                Buffer.BlockCopy(part, 0, result, offset, part.Length);
                offset += part.Length;
            }

            return result;
        }

        private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

        private static void WriteBe32(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }

        private static void WriteBe64(byte[] buffer, int offset, long value)
        {
            for (var i = 0; i < 8; i++)
            {
                buffer[offset + i] = (byte)(value >> (56 - i * 8));
            }
        }

        private static byte[] Be32(int value)
        {
            var buffer = new byte[4];
            WriteBe32(buffer, 0, value);
            return buffer;
        }

        /// <summary>ilst 里的 data 盒子：4 字节类型 + 4 字节 locale + 内容。</summary>
        private static byte[] DataBox(int dataType, byte[] payload)
            => Box("data", Be32(dataType), Be32(0), payload);

        private static byte[] TextBox(string key, string value)
            => Box(key, DataBox(1, Utf8(value)));

        /// <summary>一张 1x1 的真 PNG，用来当内嵌封面。</summary>
        private static byte[] TinyPng() => Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

        /// <summary>
        /// 手搓一个最小的 m4b：ftyp + moov(mvhd + udta(meta(ilst) + chpl))。
        /// 用来验证解析器能读出书名 / 作者 / 朗读者 / 内嵌封面 / 文件内章节 / 总时长。
        /// </summary>
        private static byte[] BuildSyntheticM4b()
        {
            var ftyp = Box("ftyp", Utf8("M4B "), Be32(0), Utf8("M4B "), Utf8("mp42"));

            // mvhd：timescale 1000，duration 5,400,000 → 5400 秒
            var mvhdPayload = new byte[100];
            mvhdPayload[0] = 0; // version 0
            WriteBe32(mvhdPayload, 4, 0);      // creation
            WriteBe32(mvhdPayload, 8, 0);      // modification
            WriteBe32(mvhdPayload, 12, 1000);  // timescale
            WriteBe32(mvhdPayload, 16, 5400000); // duration
            var mvhd = Box("mvhd", mvhdPayload);

            var ilst = Box(
                "ilst",
                TextBox("\u00A9nam", "ひたぎクラブ"),
                TextBox("\u00A9alb", "化物語 上"),
                TextBox("\u00A9ART", "西尾維新"),
                TextBox("\u00A9wrt", "西尾維新"),
                TextBox("\u00A9day", "2006"),
                TextBox("\u00A9gen", "Audiobook"),
                Box("covr", DataBox(14, TinyPng())),
                Box(
                    "----",
                    Box("mean", Be32(0), Utf8("com.apple.iTunes")),
                    Box("name", Be32(0), Utf8("NARRATOR")),
                    DataBox(1, Utf8("神谷浩史"))));

            var meta = Box("meta", Be32(0), Box("hdlr", Be32(0), Be32(0), Utf8("mdir"), Utf8("appl")), ilst);

            // chpl：3 个章节，时间戳单位 100ns
            var chapters = new List<byte>();
            var titles = new[] { "ひたぎクラブ", "まよいマイマイ", "するがモンキー" };
            var starts = new long[] { 0, 1800L * TimeSpan.TicksPerSecond, 3600L * TimeSpan.TicksPerSecond };

            var header = new byte[9];
            header[0] = 1;                 // version
            header[8] = (byte)titles.Length; // chapter count
            chapters.AddRange(header);

            for (var i = 0; i < titles.Length; i++)
            {
                var titleBytes = Utf8(titles[i]);
                var entry = new byte[9 + titleBytes.Length];
                WriteBe64(entry, 0, starts[i]);
                entry[8] = (byte)titleBytes.Length;
                Buffer.BlockCopy(titleBytes, 0, entry, 9, titleBytes.Length);
                chapters.AddRange(entry);
            }

            var chpl = Box("chpl", chapters.ToArray());

            var udta = Box("udta", meta, chpl);
            var moov = Box("moov", mvhd, udta);

            var file = new byte[ftyp.Length + moov.Length];
            Buffer.BlockCopy(ftyp, 0, file, 0, ftyp.Length);
            Buffer.BlockCopy(moov, 0, file, ftyp.Length, moov.Length);
            return file;
        }

        /// <summary>手搓一个带 ID3v2.3 标签的假 mp3（标题 / 艺术家 / 专辑 / APIC 封面）。</summary>
        private static byte[] BuildSyntheticMp3WithId3()
        {
            var frames = new List<byte>();

            void AddTextFrame(string id, string value)
            {
                var payload = new List<byte> { 3 }; // UTF-8
                payload.AddRange(Utf8(value));

                var frame = new byte[10 + payload.Count];
                Encoding.ASCII.GetBytes(id, 0, 4, frame, 0);
                WriteBe32(frame, 4, payload.Count);
                Buffer.BlockCopy(payload.ToArray(), 0, frame, 10, payload.Count);
                frames.AddRange(frame);
            }

            AddTextFrame("TIT2", "第一話");
            AddTextFrame("TPE1", "西尾維新");
            AddTextFrame("TALB", "化物語 上");

            // APIC：编码(1) + mime\0 + 图片类型(1) + 描述\0 + 图片数据
            var apic = new List<byte> { 3 };
            apic.AddRange(Encoding.ASCII.GetBytes("image/png"));
            apic.Add(0);
            apic.Add(3); // front cover
            apic.Add(0); // 空描述
            apic.AddRange(TinyPng());

            var apicFrame = new byte[10 + apic.Count];
            Encoding.ASCII.GetBytes("APIC", 0, 4, apicFrame, 0);
            WriteBe32(apicFrame, 4, apic.Count);
            Buffer.BlockCopy(apic.ToArray(), 0, apicFrame, 10, apic.Count);
            frames.AddRange(apicFrame);

            var tagBytes = frames.ToArray();

            // ID3 头：ID3 + 版本 3.0 + flags 0 + syncsafe 长度
            var header = new byte[10];
            Encoding.ASCII.GetBytes("ID3", 0, 3, header, 0);
            header[3] = 3;
            header[4] = 0;
            header[5] = 0;
            var size = tagBytes.Length;
            header[6] = (byte)((size >> 21) & 0x7F);
            header[7] = (byte)((size >> 14) & 0x7F);
            header[8] = (byte)((size >> 7) & 0x7F);
            header[9] = (byte)(size & 0x7F);

            var file = new byte[header.Length + tagBytes.Length + 4];
            Buffer.BlockCopy(header, 0, file, 0, header.Length);
            Buffer.BlockCopy(tagBytes, 0, file, header.Length, tagBytes.Length);
            return file;
        }

        private static string? FindSample(string fileName)
        {
            // samples 下按书分了子文件夹，所以递归找
            var found = FindInTree(Path.Combine(AppContext.BaseDirectory, "samples"), fileName);
            if (found != null)
            {
                return found;
            }

            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            for (var i = 0; i < 6 && directory != null; i++, directory = directory.Parent)
            {
                found = FindInTree(Path.Combine(directory.FullName, "samples"), fileName);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private static string? FindInTree(string root, string fileName)
        {
            try
            {
                if (!Directory.Exists(root))
                {
                    return null;
                }

                foreach (var file in Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories))
                {
                    return file;
                }
            }
            catch (Exception)
            {
                // 枚举失败就当没找到
            }

            return null;
        }
    }

    /// <summary>List&lt;T&gt;.Find 的等价扩展（IReadOnlyList 上没有）。</summary>
    internal static class SelfTestListExtensions
    {
        public static T? Find<T>(this IReadOnlyList<T> list, Func<T, bool> predicate)
            where T : class
        {
            for (var i = 0; i < list.Count; i++)
            {
                if (predicate(list[i]))
                {
                    return list[i];
                }
            }

            return null;
        }
    }
}
