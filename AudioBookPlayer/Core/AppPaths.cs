using System;
using System.IO;

namespace AudioBookPlayer.Core
{
    /// <summary>
    /// 程序的数据存放位置。
    ///
    /// 默认 %LOCALAPPDATA%\AudioBookPlayer；设置环境变量 AUDIOBOOKPLAYER_DATA_DIR 可以改到别处
    /// （便携模式，或者把数据放到同步盘里）。
    /// </summary>
    public static class AppPaths
    {
        public const string DataDirectoryVariable = "AUDIOBOOKPLAYER_DATA_DIR";

        private static string? _cached;

        /// <summary>数据根目录。</summary>
        public static string DataDirectory
        {
            get
            {
                if (_cached != null)
                {
                    return _cached;
                }

                var custom = Environment.GetEnvironmentVariable(DataDirectoryVariable);
                _cached = !string.IsNullOrWhiteSpace(custom)
                    ? custom
                    : Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "AudioBookPlayer");

                return _cached;
            }
        }

        public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

        public static string PlaybackFile => Path.Combine(DataDirectory, "playback.json");

        public static string CoversDirectory => Path.Combine(DataDirectory, "covers");
    }
}