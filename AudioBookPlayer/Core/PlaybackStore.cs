using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Linq;
using System.Text.Json.Serialization;

namespace AudioBookPlayer.Core
{
    /// <summary>一个章节的播放记录。</summary>
    public sealed class PlaybackRecord
    {
        public string AudioPath { get; set; } = string.Empty;

        public double PositionSeconds { get; set; }

        public double DurationSeconds { get; set; }

        public DateTime LastPlayedUtc { get; set; }

        /// <summary>是否已经听完（离结尾 15 秒内算听完）。</summary>
        public bool Completed { get; set; }

        [JsonIgnore]
        public double Progress => DurationSeconds > 0
            ? Math.Clamp(PositionSeconds / DurationSeconds, 0, 1)
            : 0;

        [JsonIgnore]
        public TimeSpan Position => TimeSpan.FromSeconds(PositionSeconds);
    }

    /// <summary>
    /// 播放数据持久化：每个章节听到哪儿、最近播放、收藏。
    /// 存在 %LOCALAPPDATA%\AudioBookPlayer\playback.json，读写失败一律静默降级（不影响播放）。
    /// </summary>
    public sealed class PlaybackStore
    {
        /// <summary>离结尾这么近就当作"已听完"。</summary>
        public const double CompletedThresholdSeconds = 15;

        /// <summary>小于这个位置不值得记忆（当作没听过）。</summary>
        public const double MinimumResumeSeconds = 5;

        private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private readonly Dictionary<string, PlaybackRecord> _records =
            new Dictionary<string, PlaybackRecord>(StringComparer.OrdinalIgnoreCase);

        private readonly List<string> _favorites = new List<string>();

        private bool _dirty;
        private DateTime _lastSaveUtc = DateTime.MinValue;

        /// <summary>默认存储位置。</summary>
        public static string DefaultPath => AppPaths.PlaybackFile;

        /// <summary>这份数据来自哪里（不序列化）。</summary>
        [JsonIgnore]
        public string? FilePath { get; set; }

        [JsonPropertyName("records")]
        public Dictionary<string, PlaybackRecord> Records
        {
            get => _records;
            set
            {
                _records.Clear();
                if (value == null)
                {
                    return;
                }

                foreach (var pair in value)
                {
                    _records[Normalize(pair.Key)] = pair.Value;
                }
            }
        }

        /// <summary>收藏的书（文件夹路径）。</summary>
        [JsonPropertyName("favorites")]
        public List<string> Favorites
        {
            get => _favorites;
            set
            {
                _favorites.Clear();
                if (value == null)
                {
                    return;
                }

                foreach (var item in value)
                {
                    if (!string.IsNullOrWhiteSpace(item))
                    {
                        _favorites.Add(Normalize(item));
                    }
                }
            }
        }

        /// <summary>最近一次播放的章节路径（用来做"继续收听"）。</summary>
        public string? LastPlayedPath { get; set; }

        /// <summary>用户手动指定的封面：书文件夹 → 图片路径。</summary>
        public Dictionary<string, string> CoverOverrides { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>取这本书的自定义封面；没有就返回 null。</summary>
        public string? GetCustomCover(string? folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return null;
            }

            foreach (var pair in CoverOverrides)
            {
                if (string.Equals(Normalize(pair.Key), Normalize(folderPath), StringComparison.OrdinalIgnoreCase))
                {
                    return File.Exists(pair.Value) ? pair.Value : null;
                }
            }

            return null;
        }

        /// <summary>设置 / 清除自定义封面（传 null 表示清除）。</summary>
        public void SetCustomCover(string folderPath, string? coverPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return;
            }

            var key = Normalize(folderPath);
            var existing = CoverOverrides.Keys.FirstOrDefault(k => string.Equals(Normalize(k), key, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                CoverOverrides.Remove(existing);
            }

            if (!string.IsNullOrWhiteSpace(coverPath))
            {
                CoverOverrides[folderPath] = coverPath;
            }

            _dirty = true;
        }

        public static PlaybackStore Load(string? path = null)
        {
            var target = path ?? DefaultPath;

            try
            {
                if (!File.Exists(target))
                {
                    return new PlaybackStore { FilePath = target };
                }

                var json = File.ReadAllText(target, Encoding.UTF8);
                var store = JsonSerializer.Deserialize<PlaybackStore>(json, SerializerOptions);
                if (store == null)
                {
                    return new PlaybackStore { FilePath = target };
                }

                store.FilePath = target;
                store._dirty = false;
                return store;
            }
            catch (Exception)
            {
                // 数据坏了不该影响启动
                return new PlaybackStore { FilePath = target };
            }
        }

        public bool Save(string? path = null, bool force = false)
        {
            var target = path ?? FilePath ?? DefaultPath;

            if (!force && !_dirty)
            {
                return true;
            }

            try
            {
                var directory = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(target, JsonSerializer.Serialize(this, SerializerOptions), new UTF8Encoding(false));
                _dirty = false;
                _lastSaveUtc = DateTime.UtcNow;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>节流保存：播放中最多每 N 秒落盘一次。</summary>
        public void SaveThrottled(TimeSpan minimumInterval)
        {
            if (!_dirty)
            {
                return;
            }

            if (DateTime.UtcNow - _lastSaveUtc < minimumInterval)
            {
                return;
            }

            Save();
        }

        public PlaybackRecord? Get(string? audioPath)
        {
            if (string.IsNullOrWhiteSpace(audioPath))
            {
                return null;
            }

            return _records.TryGetValue(Normalize(audioPath), out var record) ? record : null;
        }

        /// <summary>记录 / 更新一个章节的播放位置。</summary>
        public void Update(string audioPath, TimeSpan position, TimeSpan duration)
        {
            if (string.IsNullOrWhiteSpace(audioPath))
            {
                return;
            }

            var key = Normalize(audioPath);
            if (!_records.TryGetValue(key, out var record))
            {
                record = new PlaybackRecord { AudioPath = audioPath };
                _records[key] = record;
            }

            record.AudioPath = audioPath;
            record.PositionSeconds = Math.Max(0, position.TotalSeconds);
            if (duration > TimeSpan.Zero)
            {
                record.DurationSeconds = duration.TotalSeconds;
            }

            record.LastPlayedUtc = DateTime.UtcNow;
            record.Completed = record.DurationSeconds > 0 &&
                               record.PositionSeconds >= record.DurationSeconds - CompletedThresholdSeconds;

            LastPlayedPath = audioPath;
            _dirty = true;
        }

        /// <summary>这个章节是否值得"继续收听"。</summary>
        public bool TryGetResumePosition(string? audioPath, out TimeSpan position)
        {
            position = TimeSpan.Zero;

            var record = Get(audioPath);
            if (record == null || record.Completed)
            {
                return false;
            }

            if (record.PositionSeconds < MinimumResumeSeconds)
            {
                return false;
            }

            if (record.DurationSeconds > 0 &&
                record.PositionSeconds >= record.DurationSeconds - CompletedThresholdSeconds)
            {
                return false;
            }

            position = TimeSpan.FromSeconds(record.PositionSeconds);
            return true;
        }

        public void ClearAll()
        {
            _records.Clear();
            _favorites.Clear();
            CoverOverrides.Clear();
            LastPlayedPath = null;
            _dirty = true;
        }

        /// <summary>清掉已经不存在的文件对应的记录。</summary>
        public int RemoveMissingFiles()
        {
            var stale = new List<string>();
            foreach (var pair in _records)
            {
                if (!File.Exists(pair.Value.AudioPath))
                {
                    stale.Add(pair.Key);
                }
            }

            foreach (var key in stale)
            {
                _records.Remove(key);
            }

            if (stale.Count > 0)
            {
                _dirty = true;
            }

            return stale.Count;
        }

        public bool IsFavorite(string? folderPath)
        {
            return !string.IsNullOrWhiteSpace(folderPath) && _favorites.Contains(Normalize(folderPath));
        }

        public bool ToggleFavorite(string? folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return false;
            }

            var key = Normalize(folderPath);
            if (_favorites.Remove(key))
            {
                _dirty = true;
                return false;
            }

            _favorites.Add(key);
            _dirty = true;
            return true;
        }

        public int Count => _records.Count;

        private static string Normalize(string path)
        {
            try
            {
                return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant();
            }
            catch (Exception)
            {
                return path.Trim().ToLowerInvariant();
            }
        }
    }
}
