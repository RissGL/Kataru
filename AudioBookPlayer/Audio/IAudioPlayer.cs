using System;

namespace AudioBookPlayer.Audio
{
    /// <summary>
    /// 音频播放器抽象。业务层只依赖这个接口，方便后续替换实现
    /// （例如换成 NAudio / WASAPI / FFmpeg，而不改动字幕与界面代码）。
    /// </summary>
    public interface IAudioPlayer : IDisposable
    {
        /// <summary>音频已成功打开，可以播放 / Seek。</summary>
        event EventHandler? MediaOpened;

        /// <summary>播放到文件结尾。</summary>
        event EventHandler? MediaEnded;

        /// <summary>打开或播放失败。</summary>
        event EventHandler<AudioPlayerErrorEventArgs>? Failed;

        /// <summary>是否已经成功打开音频。</summary>
        bool IsOpen { get; }

        /// <summary>当前是否正在播放。</summary>
        bool IsPlaying { get; }

        /// <summary>当前打开的音频路径（未打开时为 null）。</summary>
        string? SourcePath { get; }

        /// <summary>当前播放位置。读取即为字幕同步的时间基准；赋值等价于 Seek。</summary>
        TimeSpan Position { get; set; }

        /// <summary>总时长（未打开时为 TimeSpan.Zero）。</summary>
        TimeSpan Duration { get; }

        /// <summary>音量 0.0 ~ 1.0。</summary>
        double Volume { get; set; }


        /// <summary>打开音频文件（重复调用会先关闭上一个文件）。</summary>
        void Open(string path);

        void Play();

        void Pause();

        /// <summary>停止并回到开头。</summary>
        void Stop();

        /// <summary>关闭当前文件。</summary>
        void Close();
    }

    /// <summary>播放器错误信息。</summary>
    public sealed class AudioPlayerErrorEventArgs : EventArgs
    {
        public AudioPlayerErrorEventArgs(string message, Exception? exception = null)
        {
            Message = message;
            Exception = exception;
        }

        public string Message { get; }

        public Exception? Exception { get; }
    }
}
