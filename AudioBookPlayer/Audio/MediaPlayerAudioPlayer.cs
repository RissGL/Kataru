using System;
using System.IO;
using System.Windows.Media;

namespace AudioBookPlayer.Audio
{
    /// <summary>
    /// 基于 WPF <see cref="MediaPlayer"/> 的播放器实现（Media Foundation，Windows 自带，无第三方依赖）。
    ///
    /// 支持格式：MP3 / WAV / WMA / M4A / AAC / MP4 等系统已安装解码器的容器；
    /// .m4b 会先尝试直接打开，失败后走 <see cref="AudioContainerBridge"/> 的扩展名兼容桥。
    ///
    /// 注意：MediaPlayer 必须在带消息泵的线程（WPF UI 线程）上创建和使用。
    /// </summary>
    public sealed class MediaPlayerAudioPlayer : IAudioPlayer
    {
        private readonly MediaPlayer _player = new MediaPlayer();

        private string? _requestedPath;
        private string? _aliasPath;
        private bool _aliasIsCopy;
        private bool _aliasAttempted;
        private bool _opened;
        private bool _disposed;
        private double _volume = 1.0;
        private TimeSpan? _pausedAt;
        private System.Windows.Threading.DispatcherTimer? _stallTimer;
        private TimeSpan _lastObservedPosition;
        private int _stallCount;

        public MediaPlayerAudioPlayer()
        {
            _player.MediaOpened += OnMediaOpened;
            _player.MediaEnded += OnMediaEnded;
            _player.MediaFailed += OnMediaFailed;
            _player.Volume = _volume;
        }

        public event EventHandler? MediaOpened;

        public event EventHandler? MediaEnded;

        public event EventHandler<AudioPlayerErrorEventArgs>? Failed;

        /// <summary>兼容桥是否被使用过（用于界面提示）。</summary>
        public string? LastCompatibilityNote { get; private set; }

        public bool IsOpen => _opened;

        public bool IsPlaying { get; private set; }

        public string? SourcePath { get; private set; }

        public TimeSpan Duration { get; private set; }

        public TimeSpan Position
        {
            get
            {
                if (!_opened)
                {
                    return TimeSpan.Zero;
                }

                var position = _player.Position;
                return position < TimeSpan.Zero ? TimeSpan.Zero : position;
            }
            set
            {
                if (!_opened)
                {
                    return;
                }

                if (value < TimeSpan.Zero)
                {
                    value = TimeSpan.Zero;
                }

                if (Duration > TimeSpan.Zero && value > Duration)
                {
                    value = Duration;
                }

                // 暂停状态下 MediaPlayer 也会立刻响应 Seek。
                _player.Position = value;

                // 用户显式跳转过，之前记的"暂停点"就不能再用了 ——
                // 否则"暂停 → 拖进度条 → 播放"会被一把拉回暂停前的位置，看起来就是自己乱跳。
                _pausedAt = null;
            }
        }

        public double Volume
        {
            get => _volume;
            set
            {
                _volume = Math.Clamp(value, 0.0, 1.0);
                _player.Volume = _volume;
            }
        }

        public void Open(string path)
        {
            ThrowIfDisposed();

            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("音频路径为空。", nameof(path));
            }

            var full = Path.GetFullPath(path);
            if (!File.Exists(full))
            {
                throw new FileNotFoundException("找不到音频文件。", full);
            }

            Close();

            _requestedPath = full;
            _aliasAttempted = false;
            SourcePath = full;
            LastCompatibilityNote = null;

            OpenCore(full);
        }
        public void Play()
        {
            ThrowIfDisposed();

            if (!_opened)
            {
                return;
            }

            var resumeFrom = _pausedAt;
            _pausedAt = null;

            _player.Play();

            // 倍速**延迟**再设：必须等管线真的跑起来（位置开始推进）之后才能改速率。
            IsPlaying = true;

            // WPF MediaPlayer 有个已知毛病：Pause() 之后 Play()，位置在走但**没有声音**。
            // 只有重新 seek 一次才能可靠唤醒，所以恢复播放时统一显式回到暂停点
            // （代价是极短的一次重新缓冲，换来"一定有声音"）。
            if (resumeFrom.HasValue)
            {
                try
                {
                    _player.Position = resumeFrom.Value;
                }
                catch (Exception)
                {
                    // 忽略
                }
            }

            StartStallWatch();
        }

        /// <summary>
        /// 播放中持续看护：位置连续两次（约 3 秒）没有推进，就认为卡住了，回到最后一个好位置重播。
        /// 这能兜住"暂停自愈"当时没发生、但播着播着又静音的情况。
        /// </summary>
        private void StartStallWatch()
        {
            _stallTimer ??= CreateStallTimer();
            _lastObservedPosition = SafePosition();
            _stallCount = 0;

            if (!_stallTimer.IsEnabled)
            {
                _stallTimer.Start();
            }
        }

        private System.Windows.Threading.DispatcherTimer CreateStallTimer()
        {
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1500),
            };

            timer.Tick += (_, _) =>
            {
                if (!_opened || !IsPlaying)
                {
                    _stallCount = 0;
                    return;
                }

                var now = SafePosition();

                // 只要往前动了一点点就算正常（阈值放到 1ms：宁可漏报，也不能误判）
                if (now > _lastObservedPosition + TimeSpan.FromMilliseconds(1))
                {
                    _lastObservedPosition = now;
                    _stallCount = 0;
                    return;
                }

                // 位置反而变小，多半是用户刚拖过进度条 / 换了章：只更新基准，不干预
                if (now < _lastObservedPosition - TimeSpan.FromMilliseconds(500))
                {
                    _lastObservedPosition = now;
                    _stallCount = 0;
                    return;
                }

                // 连续 6 秒完全没动才认为卡住（以前是 3 秒，太激进）
                if (++_stallCount < 4)
                {
                    return;
                }

                _stallCount = 0;


                try
                {
                    // 绝不动位置：只重新 Play 一次把时钟踢活。
                    // 之前这里会改 Position —— 位置读数一旦是陈旧的，就会让播放莫名跳一段，
                    // 甚至诱发 MediaEnded 假触发（然后直接跳到下一章）。
                    _player.Play();
                    _lastObservedPosition = SafePosition();
                }
                catch (Exception)
                {
                    // 忽略
                }
            };

            return timer;
        }

        private TimeSpan SafePosition()
        {
            try
            {
                var position = _player.Position;
                return position < TimeSpan.Zero ? TimeSpan.Zero : position;
            }
            catch (Exception)
            {
                return TimeSpan.Zero;
            }
        }

        private void StopStallWatch()
        {
            if (_stallTimer is { IsEnabled: true })
            {
                _stallTimer.Stop();
            }

            _stallCount = 0;
        }

        public void Pause()
        {
            if (!_opened)
            {
                return;
            }

            try
            {
                _pausedAt = _player.Position;
            }
            catch (Exception)
            {
                _pausedAt = null;
            }

            _player.Pause();
            IsPlaying = false;
            StopStallWatch();
        }


        public void Stop()
        {
            if (!_opened)
            {
                return;
            }

            _player.Stop();
            IsPlaying = false;
        }

        public void Close()
        {
            _opened = false;
            IsPlaying = false;
            StopStallWatch();
            Duration = TimeSpan.Zero;
            SourcePath = null;

            try
            {
                _player.Close();
            }
            catch (Exception)
            {
                // MediaPlayer.Close 在未打开时可能抛异常，忽略。
            }

            ReleaseAlias();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Close();
            _player.MediaOpened -= OnMediaOpened;
            _player.MediaEnded -= OnMediaEnded;
            _player.MediaFailed -= OnMediaFailed;
        }

        private void OpenCore(string path)
        {
            try
            {
                _player.Open(BuildFileUri(path));
            }
            catch (Exception ex)
            {
                Failed?.Invoke(this, new AudioPlayerErrorEventArgs($"打开音频失败：{ex.Message}", ex));
            }
        }

        private void OnMediaOpened(object? sender, EventArgs e)
        {
            _opened = true;


            // MediaOpened 时 MF 管线刚建好、还没开始播放，此时改播放速率
            // 会让音频渲染器静默失效（位置照走但没声音）—— 倍速功能刚加上时就是这么坏掉的。
            // 倍速只在用户真的改它、或者开始播放前才写一次（见 ApplySpeedIfNeeded）。

            var natural = _player.NaturalDuration;
            Duration = natural.HasTimeSpan ? natural.TimeSpan : TimeSpan.Zero;
            MediaOpened?.Invoke(this, EventArgs.Empty);
        }

        private void OnMediaEnded(object? sender, EventArgs e)
        {
            IsPlaying = false;
            MediaEnded?.Invoke(this, EventArgs.Empty);
        }

        private void OnMediaFailed(object? sender, ExceptionEventArgs e)
        {
            var message = e.ErrorException?.Message ?? "未知错误";
            if (e.ErrorException is System.Runtime.InteropServices.COMException com)
            {
                message = $"{message} (HRESULT 0x{com.HResult:X8})";
            }

            // 第一次失败时，若文件是 MP4 家族的"非标准扩展名"，用 .m4a 别名重试一次。
            if (!_aliasAttempted && AudioContainerBridge.CanAlias(_requestedPath))
            {
                _aliasAttempted = true;
                var (alias, method, isCopy) = AudioContainerBridge.TryCreateAlias(_requestedPath!);
                if (alias != null)
                {
                    _aliasPath = alias;
                    _aliasIsCopy = isCopy;
                    LastCompatibilityNote = $"已通过兼容方式打开（{method}）。";
                    OpenCore(alias);
                    return;
                }

                message = $"{message}（{method}）";
            }

            _opened = false;
            Failed?.Invoke(this, new AudioPlayerErrorEventArgs(
                $"无法播放该音频：{message}", e.ErrorException));
        }

        private void ReleaseAlias()
        {
            AudioContainerBridge.TryDeleteAlias(_aliasPath, _aliasIsCopy);
            _aliasPath = null;
            _aliasIsCopy = false;
        }

        /// <summary>
        /// 构造 file:// URI。直接用 new Uri(path) 会把 '#' 当成锚点、把 '%' 当成转义，
        /// 因此改用 UriBuilder（它会正确转义），带日文 / 中文的文件名也能正常工作。
        /// </summary>
        private static Uri BuildFileUri(string fullPath)
        {
            try
            {
                return new UriBuilder { Scheme = Uri.UriSchemeFile, Host = string.Empty, Path = fullPath }.Uri;
            }
            catch (UriFormatException)
            {
                return new Uri(fullPath);
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(MediaPlayerAudioPlayer));
            }
        }
    }
}
