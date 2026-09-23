using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text;

namespace AudioBookPlayer.Core
{
    /// <summary>
    /// 媒体库里的一个条目 = 一个音频文件（可能带同名 SRT 字幕）。
    /// 同时携带这个章节的播放进度（由 <see cref="PlaybackStore"/> 灌入）。
    /// </summary>
    public sealed class LibraryEntry : INotifyPropertyChanged
    {
        private double _playbackSeconds;
        private double _playbackDuration;
        private bool _isCompleted;

        public LibraryEntry(string audioPath, string? subtitlePath, string rootFolder)
        {
            AudioPath = audioPath;
            SubtitlePath = subtitlePath;
            RootFolder = rootFolder;

            var directory = Path.GetDirectoryName(audioPath) ?? rootFolder;
            RelativeFolder = GetRelativeFolder(rootFolder, directory);
            Title = Path.GetFileNameWithoutExtension(audioPath);

            // 相对路径用于排序 / 搜索，保证同一文件夹里的章节按顺序排
            RelativePath = string.IsNullOrEmpty(RelativeFolder)
                ? Path.GetFileName(audioPath)
                : Path.Combine(RelativeFolder, Path.GetFileName(audioPath));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>音频文件绝对路径。</summary>
        public string AudioPath { get; }

        /// <summary>所属的书（进度刷新要用）。</summary>
        public LibraryBook? Book { get; private set; }

        /// <summary>扫描时把所属的书挂上。</summary>
        public void AttachBook(LibraryBook book) => Book = book;
        /// <summary>这条音频配到的所有字幕轨（按语言区分，可以有中日英多条）。</summary>
        public IReadOnlyList<SubtitleTrack> SubtitleTracks { get; private set; } = Array.Empty<SubtitleTrack>();
        /// <summary>配对到的字幕文件绝对路径；没有则为 null。</summary>
        public string? SubtitlePath { get; }

        /// <summary>媒体库根目录。</summary>
        public string RootFolder { get; }

        /// <summary>相对根目录的子文件夹（根目录本身为空字符串）。</summary>
        public string RelativeFolder { get; }

        /// <summary>用于列表分组显示的文件夹标签。</summary>
        public string FolderLabel => string.IsNullOrEmpty(RelativeFolder) ? "（根目录）" : RelativeFolder;

        /// <summary>不含扩展名的文件名，作为条目标题。</summary>
        public string Title { get; }

        /// <summary>相对根目录的路径，用于排序与搜索。</summary>
        public string RelativePath { get; }

        public bool HasSubtitle => !string.IsNullOrEmpty(SubtitlePath);

        /// <summary>列表右侧显示的角标文字。</summary>
        public string SubtitleBadge => HasSubtitle ? Path.GetFileName(SubtitlePath!) : "无字幕";

        /// <summary>鼠标悬停提示。</summary>
        public string ToolTipText => AudioPath + (HasSubtitle ? Environment.NewLine + SubtitlePath : Environment.NewLine + "（没有找到同名 .srt）");

        // ---------------- 播放进度 ----------------

        /// <summary>上次听到的位置（秒）。</summary>
        public double PlaybackSeconds => _playbackSeconds;

        /// <summary>音频总时长（秒）；没播过就是 0。</summary>
        public double PlaybackDuration => _playbackDuration;

        /// <summary>是否已经听完。</summary>
        public bool IsCompleted => _isCompleted;

        public bool HasProgress => _playbackSeconds > 0;

        public double ProgressPercent => _playbackDuration > 0
            ? Math.Clamp(_playbackSeconds / _playbackDuration, 0, 1)
            : 0;

        public string ProgressText
        {
            get
            {
                if (_isCompleted)
                {
                    return "已听完";
                }

                if (_playbackDuration <= 0 || _playbackSeconds <= 0)
                {
                    return "未开始";
                }

                return $"已听 {ProgressPercent * 100:0}%";
            }
        }

        /// <summary>把播放数据灌进这个条目（扫描时调用，或播放中更新）。</summary>
        public void ApplyPlaybackState(double positionSeconds, double durationSeconds, bool completed)
        {
            _playbackSeconds = Math.Max(0, positionSeconds);
            _playbackDuration = Math.Max(0, durationSeconds);
            _isCompleted = completed;

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlaybackSeconds)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlaybackDuration)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCompleted)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasProgress)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProgressPercent)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProgressText)));
        }

        /// <summary>把扫到的字幕轨挂上去（扫描时调用一次）。</summary>
        public void ApplySubtitleTracks(IReadOnlyList<SubtitleTrack> tracks)
        {
            SubtitleTracks = tracks ?? Array.Empty<SubtitleTrack>();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SubtitleTracks)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SubtitleBadge)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasSubtitle)));
        }

        /// <summary>按语言代码找一条轨（找不到返回 null）。</summary>
        public SubtitleTrack? FindTrack(string languageCode)
        {
            foreach (var track in SubtitleTracks)
            {
                if (string.Equals(track.LanguageCode, languageCode, StringComparison.OrdinalIgnoreCase))
                {
                    return track;
                }
            }

            return null;
        }
        /// <summary>搜索用的小写文本。</summary>
        internal string SearchText => (RelativePath + " " + (SubtitlePath ?? string.Empty)).ToLowerInvariant();

        public override string ToString() => RelativePath;

        private static string GetRelativeFolder(string root, string directory)
        {
            try
            {
                var relative = Path.GetRelativePath(root, directory);
                return relative == "." ? string.Empty : relative;
            }
            catch (ArgumentException)
            {
                return string.Empty;
            }
        }
    }

    /// <summary>
    /// 一本书 = 媒体库里的一个文件夹（下面可以有若干章节音频）。
    /// </summary>
    public sealed class LibraryBook : INotifyPropertyChanged
    {
        public LibraryBook(string folderPath, string rootFolder, IReadOnlyList<LibraryEntry> entries, string? coverPath, Audio.AudioTagInfo? tags = null)
        {
            FolderPath = folderPath;
            RootFolder = rootFolder;
            Entries = entries;
            CoverPath = coverPath;
            Tags = tags;

            var relative = GetRelativeFolder(rootFolder, folderPath);
            RelativeFolder = relative;
            FolderTitle = relative.Length == 0
                ? new DirectoryInfo(rootFolder).Name
                : Path.GetFileName(relative);

            // 标签里的专辑名 / 标题比文件夹名更权威（有声书的文件夹经常是 01、Disc1 之类）
            var tagged = tags?.DisplayTitle;
            Title = IsUsableTitle(tagged) ? tagged! : FolderTitle;

            Author = string.IsNullOrWhiteSpace(tags?.DisplayAuthor) ? null : tags!.DisplayAuthor;
            Narrator = string.IsNullOrWhiteSpace(tags?.Narrator) ? null : tags!.Narrator;
        }

        private static bool IsUsableTitle(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var trimmed = value.Trim();
            return !trimmed.Equals("unknown", StringComparison.OrdinalIgnoreCase) &&
                   !trimmed.Equals("unknown album", StringComparison.OrdinalIgnoreCase) &&
                   !trimmed.Equals("untitled", StringComparison.OrdinalIgnoreCase) &&
                   trimmed.Length > 1;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>书所在的文件夹。</summary>
        public string FolderPath { get; }

        public string RootFolder { get; }

        /// <summary>相对媒体库根目录的位置（根目录本身为空字符串）。</summary>
        public string RelativeFolder { get; }

        /// <summary>书名（优先用音频标签里的专辑名，否则取文件夹名）。</summary>
        public string Title { get; }

        /// <summary>文件夹名（书名的兜底来源，界面 hover 时显示）。</summary>
        public string FolderTitle { get; }

        /// <summary>这本书的章节（按自然顺序排好）。</summary>
        public IReadOnlyList<LibraryEntry> Entries { get; }

        /// <summary>封面图片路径；找不到就是 null，界面会显示占位封面。</summary>
        public string? CoverPath { get; private set; }

        /// <summary>第一个章节的音频标签（书名 / 作者 / 朗读者 / 内嵌封面都来自它）。</summary>
        public Audio.AudioTagInfo? Tags { get; }

        /// <summary>换一张封面（用户手动指定 / 扫描后补充）。</summary>
        public void ApplyCover(string? coverPath)
        {
            if (string.Equals(CoverPath, coverPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            CoverPath = coverPath;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CoverPath)));
        }

        public int ChapterCount => Entries.Count;

        public int SubtitleCount
        {
            get
            {
                var count = 0;
                foreach (var entry in Entries)
                {
                    if (entry.HasSubtitle)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        public string ChapterCountText => $"{ChapterCount} 章 · {SubtitleCount} 章有字幕";

        // ---------------- 整本书的播放进度 ----------------

        /// <summary>所有章节进度的平均值（0~1）。</summary>
        public double ProgressPercent
        {
            get
            {
                if (Entries.Count == 0)
                {
                    return 0;
                }

                var total = 0.0;
                foreach (var entry in Entries)
                {
                    total += entry.IsCompleted ? 1 : entry.ProgressPercent;
                }

                return Math.Clamp(total / Entries.Count, 0, 1);
            }
        }

        public bool HasProgress
        {
            get
            {
                foreach (var entry in Entries)
                {
                    if (entry.HasProgress)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        public string ProgressText
        {
            get
            {
                if (!HasProgress)
                {
                    return "未开始";
                }

                return ProgressPercent >= 0.999 ? "已听完" : $"已听 {ProgressPercent * 100:0}%";
            }
        }

        /// <summary>章节进度变化后刷新卡片上的进度显示。</summary>
        public void RefreshProgress()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProgressPercent)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasProgress)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProgressText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChapterCountText)));
        }

        /// <summary>这本书是否被收藏。</summary>
        public bool IsFavorite { get; private set; }

        /// <summary>从音频标签里读到的作者（没有就是 null）。</summary>
        public string? Author { get; private set; }

        /// <summary>朗读者。</summary>
        public string? Narrator { get; private set; }

        /// <summary>书封面是不是用户手动指定的。</summary>
        public bool HasCustomCover { get; private set; }

        /// <summary>界面显示用的一行"作者 · 朗读：（有就显示）。</summary>
        public string CreditsText
        {
            get
            {
                var parts = new System.Collections.Generic.List<string>(2);
                if (!string.IsNullOrWhiteSpace(Author))
                {
                    parts.Add(Author!);
                }

                if (!string.IsNullOrWhiteSpace(Narrator))
                {
                    parts.Add("朗读：" + Narrator);
                }

                return string.Join(" · ", parts);
            }
        }

        public bool HasCredits => CreditsText.Length > 0;

        /// <summary>收藏星标（★ / ☆）。</summary>
        public string FavoriteGlyph => IsFavorite ? "★" : "☆";

        /// <summary>把播放记录灌进这本书的所有章节，同时刷新收藏状态。</summary>
        public void ApplyPlaybackStore(PlaybackStore store)
        {
            IsFavorite = store.IsFavorite(FolderPath);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFavorite)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FavoriteGlyph)));

            var custom = store.GetCustomCover(FolderPath);
            if (custom != null)
            {
                HasCustomCover = true;
                ApplyCover(custom);
            }

            foreach (var entry in Entries)
            {
                var record = store.Get(entry.AudioPath);
                entry.ApplyPlaybackState(
                    record?.PositionSeconds ?? 0,
                    record?.DurationSeconds ?? 0,
                    record?.Completed ?? false);
            }
        }

        public string ToolTipText => FolderPath;

        public override string ToString() => Title;

        private static string GetRelativeFolder(string root, string directory)
        {
            try
            {
                var relative = Path.GetRelativePath(root, directory);
                return relative == "." ? string.Empty : relative;
            }
            catch (ArgumentException)
            {
                return string.Empty;
            }
        }
    }

    /// <summary>一次扫描的结果。</summary>
    public sealed class LibraryScanResult
    {
        public LibraryScanResult(
            string root,
            IReadOnlyList<LibraryEntry> entries,
            IReadOnlyList<LibraryBook> books,
            IReadOnlyList<string> skippedFolders,
            TimeSpan elapsed)
        {
            Root = root;
            Entries = entries;
            Books = books;
            SkippedFolders = skippedFolders;
            Elapsed = elapsed;
        }

        public string Root { get; }

        public IReadOnlyList<LibraryEntry> Entries { get; }

        /// <summary>按文件夹归并出来的书。</summary>
        public IReadOnlyList<LibraryBook> Books { get; }

        /// <summary>没有权限 / 读取失败的文件夹（不影响其它目录）。</summary>
        public IReadOnlyList<string> SkippedFolders { get; }

        public TimeSpan Elapsed { get; }

        public int CountWithSubtitle
        {
            get
            {
                var count = 0;
                foreach (var entry in Entries)
                {
                    if (entry.HasSubtitle)
                    {
                        count++;
                    }
                }

                return count;
            }
        }
    }

    /// <summary>
    /// 媒体库扫描器：递归遍历指定文件夹，找出所有音频文件，并自动配对同目录下的同名 .srt。
    ///
    /// 配对顺序（都忽略大小写）：
    ///   1. 完全同名：     化物語 上.m4b   ↔  化物語 上.srt
    ///   2. 带语言后缀：   化物語 上.m4b   ↔  化物語 上.ja.srt / 化物語 上.zh-CN.srt
    ///   3. 前缀匹配：     化物語 上.m4b   ↔  化物語 上.chs.srt
    /// </summary>
    public static class MediaLibraryScanner
    {
        /// <summary>被当作音频的扩展名。</summary>
        public static readonly string[] AudioExtensions =
        {
            ".mp3", ".wav", ".m4a", ".m4b", ".wma", ".aac", ".mp4", ".flac", ".aiff", ".aif", ".ogg", ".opus",
        };

        public static LibraryScanResult Scan(
            string rootFolder,
            PlaybackStore? playbackStore = null,
            string? coverCacheDirectory = null)
        {
            var started = DateTime.UtcNow;
            var entries = new List<LibraryEntry>();
            var skipped = new List<string>();

            if (string.IsNullOrWhiteSpace(rootFolder))
            {
                throw new ArgumentException("媒体库目录为空。", nameof(rootFolder));
            }

            var root = Path.GetFullPath(rootFolder);
            if (!Directory.Exists(root))
            {
                throw new DirectoryNotFoundException("找不到目录：" + root);
            }

            var audioFiles = new List<string>();
            CollectFiles(root, audioFiles, skipped);

            // 先把这个目录树里的 srt 都建索引，便于前缀匹配
            var srtIndex = BuildSubtitleIndex(root, skipped);

            foreach (var audioPath in audioFiles)
            {
                var tracks = FindSubtitleTracks(audioPath, srtIndex);
                var primary = tracks.Count > 0 ? tracks[0].FilePath : null;

                var entry = new LibraryEntry(audioPath, primary, root);
                entry.ApplySubtitleTracks(tracks);
                entries.Add(entry);
            }

            entries.Sort(static (a, b) => NaturalStringComparer.Instance.Compare(a.RelativePath, b.RelativePath));

            var books = BuildBooks(root, entries, coverCacheDirectory);

            if (playbackStore != null)
            {
                foreach (var book in books)
                {
                    book.ApplyPlaybackStore(playbackStore);
                }
            }

            return new LibraryScanResult(root, entries, books, skipped, DateTime.UtcNow - started);
        }

        /// <summary>把条目按所在文件夹归并成"书"，并按自然顺序排序。</summary>
        private static List<LibraryBook> BuildBooks(string root, List<LibraryEntry> entries, string? coverCacheDirectory)
        {
            var byFolder = new Dictionary<string, List<LibraryEntry>>(StringComparer.OrdinalIgnoreCase);
            var folderOrder = new List<string>();

            foreach (var entry in entries)
            {
                var folder = Path.GetDirectoryName(entry.AudioPath) ?? root;
                if (!byFolder.TryGetValue(folder, out var list))
                {
                    list = new List<LibraryEntry>();
                    byFolder[folder] = list;
                    folderOrder.Add(folder);
                }

                list.Add(entry);
            }

            var books = new List<LibraryBook>(folderOrder.Count);
            foreach (var folder in folderOrder)
            {
                var list = byFolder[folder];
                list.Sort(static (a, b) => NaturalStringComparer.Instance.Compare(a.RelativePath, b.RelativePath));

                // 只读第一个章节的标签，避免整个库扫一遍要开几百个文件
                var tags = Audio.MediaTagReader.TryReadCached(list[0].AudioPath);

                var cover = FindCover(folder, root)
                            ?? ExtractEmbeddedCover(tags, coverCacheDirectory, folder, root);

                books.Add(new LibraryBook(folder, root, list, cover, tags));

                // 反向引用：回写进度时要按"真正装载的那一条"找到它所属的书去刷新显示
                foreach (var entry in list)
                {
                    entry.AttachBook(books[^1]);
                }
            }

            books.Sort(static (a, b) => NaturalStringComparer.Instance.Compare(a.RelativeFolder, b.RelativeFolder));
            return books;
        }

        /// <summary>封面文件名（不含扩展名）的候选，按优先级排列。</summary>
        private static readonly string[] CoverNameHints =
        {
            "cover", "folder", "front", "album", "poster", "book", "封面", "封面图",
        };

        private static readonly string[] CoverExtensions =
        {
            ".jpg", ".jpeg", ".png", ".bmp", ".gif",
        };

        /// <summary>
        /// 找封面：先看书自己的文件夹，再看上一级（例如 化物語 上/Disc1 → 化物語 上/cover.jpg），
        /// 最后看媒体库根目录。找不到就返回 null，界面显示占位封面。
        /// </summary>
        private static string? FindCover(string bookFolder, string root)
        {
            foreach (var folder in new[] { bookFolder, Path.GetDirectoryName(bookFolder), root })
            {
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                {
                    continue;
                }

                var cover = FindCoverInFolder(folder);
                if (cover != null)
                {
                    return cover;
                }
            }

            return null;
        }

        private static string? FindCoverInFolder(string folder)
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(folder);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                return null;
            }

            string? fallback = null;
            foreach (var file in files)
            {
                var extension = Path.GetExtension(file);
                if (!IsCoverExtension(extension))
                {
                    continue;
                }

                var name = Path.GetFileNameWithoutExtension(file);

                foreach (var hint in CoverNameHints)
                {
                    if (name.Equals(hint, StringComparison.OrdinalIgnoreCase))
                    {
                        return file; // 完全命中，最高优先级
                    }
                }

                if (fallback == null &&
                    name.Contains("cover", StringComparison.OrdinalIgnoreCase))
                {
                    fallback = file;
                }
            }

            return fallback;
        }

        /// <summary>封面优先级第三级：音频文件里内嵌的封面（covr / APIC），解出来写进缓存目录。</summary>
        private static string? ExtractEmbeddedCover(
            Audio.AudioTagInfo? tags,
            string? coverCacheDirectory,
            string bookFolder,
            string root)
        {
            if (tags?.CoverData == null || string.IsNullOrEmpty(coverCacheDirectory))
            {
                return null;
            }

            var relative = GetRelativeFolder(root, bookFolder);
            var key = relative.Length == 0 ? new DirectoryInfo(root).Name : relative.Replace('\\', '_').Replace('/', '_');

            return Audio.MediaTagReader.SaveCoverToFile(tags.CoverData, tags.CoverMimeType, coverCacheDirectory, key);
        }

        private static string GetRelativeFolder(string root, string directory)
        {
            try
            {
                var relative = Path.GetRelativePath(root, directory);
                return relative == "." ? string.Empty : relative;
            }
            catch (ArgumentException)
            {
                return string.Empty;
            }
        }

        private static bool IsCoverExtension(string extension)
        {
            foreach (var known in CoverExtensions)
            {
                if (string.Equals(extension, known, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static void CollectFiles(string directory, List<string> audioFiles, List<string> skipped)
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(directory);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                skipped.Add(directory);
                return;
            }

            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                if (name.StartsWith('.'))
                {
                    continue; // 隐藏 / 元数据文件
                }

                if (IsAudioFile(file))
                {
                    audioFiles.Add(file);
                }
            }

            string[] subDirectories;
            try
            {
                subDirectories = Directory.GetDirectories(directory);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                skipped.Add(directory);
                return;
            }

            foreach (var sub in subDirectories)
            {
                var name = Path.GetFileName(sub);
                if (name.StartsWith('.'))
                {
                    continue;
                }

                CollectFiles(sub, audioFiles, skipped);
            }
        }

        private sealed class SubtitleIndex
        {
            /// <summary>键 = "小写文件夹|文件名（不含扩展名）"。</summary>
            public Dictionary<string, List<string>> ByKey { get; } = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            /// <summary>键 = "小写文件夹"，值 = 该目录下所有字幕的（文件名, 路径）。</summary>
            public Dictionary<string, List<KeyValuePair<string, string>>> ByFolder { get; } = new Dictionary<string, List<KeyValuePair<string, string>>>(StringComparer.OrdinalIgnoreCase);
        }

        private static SubtitleIndex BuildSubtitleIndex(string root, List<string> skipped)
        {
            var index = new SubtitleIndex();
            var srtFiles = new List<string>();
            CollectByExtension(root, ".srt", srtFiles, skipped);

            foreach (var srt in srtFiles)
            {
                var directory = Path.GetDirectoryName(srt) ?? string.Empty;
                var folderKey = NormalizeFolder(directory);
                var stem = Path.GetFileNameWithoutExtension(srt);

                // 完整名与"去掉语言后缀的名"都登记，两种情况都能精确命中
                AddKey(index, folderKey, stem, srt);
                var dot = stem.LastIndexOf('.');
                if (dot > 0)
                {
                    AddKey(index, folderKey, stem.Substring(0, dot), srt);
                }

                if (!index.ByFolder.TryGetValue(folderKey, out var list))
                {
                    list = new List<KeyValuePair<string, string>>();
                    index.ByFolder[folderKey] = list;
                }

                list.Add(new KeyValuePair<string, string>(stem, srt));
            }

            foreach (var list in index.ByKey.Values)
            {
                list.Sort(static (a, b) => NaturalStringComparer.Instance.Compare(a, b));
            }

            foreach (var list in index.ByFolder.Values)
            {
                list.Sort(static (a, b) => NaturalStringComparer.Instance.Compare(a.Key, b.Key));
            }

            return index;
        }

        private static void AddKey(SubtitleIndex index, string folderKey, string stem, string path)
        {
            var key = folderKey + "|" + stem;
            if (!index.ByKey.TryGetValue(key, out var list))
            {
                list = new List<string>();
                index.ByKey[key] = list;
            }

            if (!list.Exists(existing => string.Equals(existing, path, StringComparison.OrdinalIgnoreCase)))
            {
                list.Add(path);
            }
        }

        /// <summary>
        /// 找出这条音频的所有字幕轨（同名 + 各种语言后缀），按"原文优先，其次常用语言"排好。
        /// </summary>
        private static IReadOnlyList<SubtitleTrack> FindSubtitleTracks(string audioPath, SubtitleIndex index)
        {
            var directory = Path.GetDirectoryName(audioPath);
            if (string.IsNullOrEmpty(directory))
            {
                return Array.Empty<SubtitleTrack>();
            }

            var audioStem = Path.GetFileNameWithoutExtension(audioPath);
            var folderKey = NormalizeFolder(directory);
            var found = new List<SubtitleTrack>();

            if (index.ByFolder.TryGetValue(folderKey, out var folderList))
            {
                foreach (var pair in folderList)
                {
                    if (!SubtitleLanguage.IsRelatedSubtitle(audioStem, pair.Key))
                    {
                        continue;
                    }

                    var (code, name) = SubtitleLanguage.Detect(audioStem, pair.Value);
                    found.Add(new SubtitleTrack(pair.Value, code, name));
                }
            }

            if (found.Count == 0)
            {
                return Array.Empty<SubtitleTrack>();
            }

            // 排序：无语言标记的（原文）最前，然后日语 / 中文 / 英语 / 其它
            found.Sort((a, b) => Rank(a).CompareTo(Rank(b)));
            return found;

            static int Rank(SubtitleTrack track) => track.LanguageCode.ToLowerInvariant() switch
            {
                "" => 0,
                "ja" => 1,
                "zh" => 2,
                "en" => 3,
                _ => 4,
            };
        }

        private static string? FindSubtitle(string audioPath, SubtitleIndex index)
        {
            var directory = Path.GetDirectoryName(audioPath);
            if (string.IsNullOrEmpty(directory))
            {
                return null;
            }

            var folderKey = NormalizeFolder(directory);
            var stem = Path.GetFileNameWithoutExtension(audioPath);

            if (index.ByKey.TryGetValue(folderKey + "|" + stem, out var candidates) && candidates.Count > 0)
            {
                // 完全同名优先，其次才是带语言后缀的
                foreach (var candidate in candidates)
                {
                    if (string.Equals(Path.GetFileNameWithoutExtension(candidate), stem, StringComparison.OrdinalIgnoreCase))
                    {
                        return candidate;
                    }
                }

                return candidates[0];
            }

            // 兜底：同目录下"以音频名开头"的字幕（化物語 上（字幕）.srt / 化物語 上.chs.srt）
            if (stem.Length > 0 && index.ByFolder.TryGetValue(folderKey, out var folderList))
            {
                foreach (var pair in folderList)
                {
                    if (pair.Key.StartsWith(stem, StringComparison.OrdinalIgnoreCase))
                    {
                        return pair.Value;
                    }
                }
            }

            return null;
        }

        private static void CollectByExtension(string directory, string extension, List<string> results, List<string> skipped)
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(directory, "*" + extension);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                skipped.Add(directory);
                return;
            }

            foreach (var file in files)
            {
                if (!Path.GetFileName(file).StartsWith('.'))
                {
                    results.Add(file);
                }
            }

            string[] subDirectories;
            try
            {
                subDirectories = Directory.GetDirectories(directory);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                skipped.Add(directory);
                return;
            }

            foreach (var sub in subDirectories)
            {
                if (!Path.GetFileName(sub).StartsWith('.'))
                {
                    CollectByExtension(sub, extension, results, skipped);
                }
            }
        }

        private static string NormalizeFolder(string directory)
        {
            var trimmed = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return trimmed.ToLowerInvariant();
        }

        private static bool IsAudioFile(string path)
        {
            var extension = Path.GetExtension(path);
            if (extension.Length == 0)
            {
                return false;
            }

            foreach (var known in AudioExtensions)
            {
                if (string.Equals(extension, known, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// 自然排序：把数字段按数值比较，避免出现 "第10章" 排在 "第2章" 前面。
    /// </summary>
    public sealed class NaturalStringComparer : IComparer<string>
    {
        public static NaturalStringComparer Instance { get; } = new NaturalStringComparer();

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x == null)
            {
                return -1;
            }

            if (y == null)
            {
                return 1;
            }

            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
                {
                    var startI = i;
                    var startJ = j;
                    while (i < x.Length && char.IsDigit(x[i]))
                    {
                        i++;
                    }

                    while (j < y.Length && char.IsDigit(y[j]))
                    {
                        j++;
                    }

                    if (!TryCompareNumbers(x, startI, i, y, startJ, j, out var result))
                    {
                        // 数字太长（大数）时退化为字符串比较
                        result = string.CompareOrdinal(x.Substring(startI, i - startI), y.Substring(startJ, j - startJ));
                    }

                    if (result != 0)
                    {
                        return result;
                    }
                }
                else
                {
                    var cx = char.ToUpperInvariant(x[i]);
                    var cy = char.ToUpperInvariant(y[j]);
                    if (cx != cy)
                    {
                        return cx.CompareTo(cy);
                    }

                    i++;
                    j++;
                }
            }

            return (x.Length - i).CompareTo(y.Length - j);
        }

        private static bool TryCompareNumbers(string x, int startX, int endX, string y, int startY, int endY, out int result)
        {
            result = 0;

            // 跳过前导 0
            while (startX < endX && x[startX] == '0')
            {
                startX++;
            }

            while (startY < endY && y[startY] == '0')
            {
                startY++;
            }

            var lengthX = endX - startX;
            var lengthY = endY - startY;

            if (lengthX != lengthY)
            {
                result = lengthX.CompareTo(lengthY);
                return true;
            }

            for (var k = 0; k < lengthX; k++)
            {
                if (x[startX + k] != y[startY + k])
                {
                    result = x[startX + k].CompareTo(y[startY + k]);
                    return true;
                }
            }

            return true;
        }
    }
}
