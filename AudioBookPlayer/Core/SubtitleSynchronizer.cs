using System;
using System.Collections.Generic;

namespace AudioBookPlayer.Core
{
    /// <summary>
    /// 字幕同步器：把"音频当前播放时间"映射到"当前应该显示的那条字幕"。
    ///
    /// 唯一时间基准是音频播放位置（<see cref="Audio.IAudioPlayer.Position"/>），
    /// 不使用 DateTime.Now / Stopwatch。
    ///
    /// 定位策略：
    ///  1. 命中缓存（当前行 / 下一行）时直接返回，正常顺序播放几乎没有开销；
    ///  2. 其余情况（尤其是拖动进度条之后的 Seek）一律用二分查找重新定位，
    ///     绝不会"从旧位置往后扫"，因此快进 / 快退后能立刻同步。
    /// </summary>
    public sealed class SubtitleSynchronizer
    {
        private IReadOnlyList<SubtitleLine> _lines = Array.Empty<SubtitleLine>();
        private int _hint = -1;
        private int _currentIndex = -1;

        /// <summary>当前应该显示的字幕；没有（空档期）时为 null。</summary>
        public SubtitleLine? Current => _currentIndex >= 0 && _currentIndex < _lines.Count ? _lines[_currentIndex] : null;

        /// <summary>当前字幕在列表中的下标，-1 表示没有。</summary>
        public int CurrentIndex => _currentIndex;

        /// <summary>已加载的字幕条目数。</summary>
        public int Count => _lines.Count;

        /// <summary>最近一次用于同步的播放位置。</summary>
        public TimeSpan LastPosition { get; private set; }

        /// <summary>加载字幕列表（会防御性地按开始时间排序）。</summary>
        public void Load(IReadOnlyList<SubtitleLine>? lines)
        {
            if (lines == null || lines.Count == 0)
            {
                _lines = Array.Empty<SubtitleLine>();
            }
            else
            {
                var copy = new List<SubtitleLine>(lines);
                copy.Sort(static (a, b) => a.StartTime.CompareTo(b.StartTime));
                _lines = copy;
            }

            Invalidate();
        }

        public void Clear()
        {
            Load(null);
        }

        /// <summary>丢弃缓存，强制下一次 Update 重新二分定位（Seek / 换文件后调用）。</summary>
        public void Invalidate()
        {
            _hint = -1;
            _currentIndex = -1;
            LastPosition = TimeSpan.Zero;
        }

        /// <summary>
        /// 用新的播放位置更新当前字幕。返回 true 表示"显示内容发生了变化"，界面需要刷新。
        /// </summary>
        public bool Update(TimeSpan position)
        {
            LastPosition = position;

            var index = Locate(position);
            if (index == _currentIndex)
            {
                return false;
            }

            _currentIndex = index;
            _hint = index;
            return true;
        }

        /// <summary>Seek 之后调用：重新计算当前字幕并返回结果。</summary>
        public SubtitleLine? Seek(TimeSpan position)
        {
            Invalidate();
            Update(position);
            return Current;
        }

        /// <summary>查找给定播放位置对应的字幕下标，找不到返回 -1。</summary>
        public int Locate(TimeSpan position)
        {
            var count = _lines.Count;
            if (count == 0)
            {
                return -1;
            }

            // 快路径 1：当前行仍然覆盖该时间点（绝大多数 Tick 都走这里）
            var current = _currentIndex;
            if (current >= 0 && current < count && _lines[current].IsVisibleAt(position))
            {
                return current;
            }

            // 快路径 2：正好推进到下一条（正常顺序播放）
            var next = current + 1;
            if (current >= 0 && next < count && _lines[next].IsVisibleAt(position))
            {
                return next;
            }

            var hint = _hint;
            if (hint >= 0 && hint < count && _lines[hint].IsVisibleAt(position))
            {
                return hint;
            }

            // 其余情况（含 Seek 跳转）：二分查找重新定位
            return LocateByBinarySearch(position);
        }

        /// <summary>找出所有覆盖指定时间的字幕（用于诊断 / 未来的双语字幕）。</summary>
        public IReadOnlyList<SubtitleLine> FindAllAt(TimeSpan position)
        {
            var found = new List<SubtitleLine>();
            for (var i = 0; i < _lines.Count; i++)
            {
                if (_lines[i].IsVisibleAt(position))
                {
                    found.Add(_lines[i]);
                }
            }

            return found;
        }

        private int LocateByBinarySearch(TimeSpan position)
        {
            var count = _lines.Count;

            // 找到最后一个 StartTime <= position 的位置
            var low = 0;
            var high = count - 1;
            var candidate = -1;

            while (low <= high)
            {
                var mid = low + ((high - low) >> 1);
                if (_lines[mid].StartTime <= position)
                {
                    candidate = mid;
                    low = mid + 1;
                }
                else
                {
                    high = mid - 1;
                }
            }

            if (candidate < 0)
            {
                return -1; // 还在第一条字幕之前
            }

            // 正常情况下 candidate 就是答案；再往前探几步是为了兼容时间轴互相重叠的字幕，
            // 取"开始时间最晚且仍然覆盖该时刻"的那一条。
            const int maxOverlapProbe = 16;
            for (var i = candidate; i >= 0 && candidate - i < maxOverlapProbe; i--)
            {
                if (_lines[i].EndTime > position)
                {
                    return i;
                }
            }

            return -1; // 空档期：不显示任何字幕
        }
    }
}
