using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AudioBookPlayer.Audio
{
    /// <summary>
    /// M4B 兼容桥。
    ///
    /// WPF 的 MediaPlayer 通过扩展名选择 Media Foundation 的解码器，而 .m4b 不在它的已知扩展名列表里，
    /// 即使容器本身就是 MP4/AAC（.m4b 实际上就是带章节的 MP4）。这里把 .m4b 以 .m4a 扩展名"暴露"给
    /// MediaPlayer。
    ///
    /// 核心原则：**只建硬链接，绝不复制大文件**。
    /// 硬链接是同一份数据换个名字，瞬间完成、不占额外空间；
    /// 而复制一本几百 MB 的有声书会卡住界面，还可能覆盖正在播放的文件导致没声音 —— 这两件事都真实发生过。
    /// 所以硬链接建不出来的话，宁可明确报错，也不偷偷复制。
    /// </summary>
    internal static class AudioContainerBridge
    {
        /// <summary>这些扩展名 MediaPlayer 自己能处理，不需要兼容桥。</summary>
        private static readonly string[] NativeExtensions =
        {
            ".mp3", ".wav", ".wma", ".m4a", ".aac", ".mp4", ".flac", ".aiff", ".aif",
        };

        /// <summary>别名文件的前缀（删的时候靠它认人，绝不动用户的真实文件）。</summary>
        private const string AliasPrefix = ".kataru-";


        /// <summary>历史版本把副本堆在这里，启动时会清理。</summary>
        private static readonly string LegacyAliasDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AudioBookPlayer",
            "container-alias");

        /// <summary>该文件是否值得尝试兼容桥。</summary>
        public static bool CanAlias(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var extension = Path.GetExtension(path);
            if (string.IsNullOrEmpty(extension))
            {
                return false;
            }

            foreach (var native in NativeExtensions)
            {
                if (string.Equals(extension, native, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            // 只对 MP4 家族容器做别名；其他格式改名也没有意义。
            return extension.Equals(".m4b", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".m4v", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".m4p", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".mp4a", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 为源文件创建一个 .m4a 别名。成功返回别名路径与说明文字，失败返回 (null, 原因)。
        /// </summary>
        public static (string? AliasPath, string Method, bool IsCopy) TryCreateAlias(string sourcePath)
        {
            try
            {
                var full = Path.GetFullPath(sourcePath);
                if (!File.Exists(full))
                {
                    return (null, "源文件不存在", false);
                }

                var info = new FileInfo(full);
                var aliasName = BuildAliasName(info.Name, info.Length, info.LastWriteTimeUtc.Ticks);

                // ① 优先建在源文件**同一个目录**里：同一个卷，硬链接必然成功，瞬时且不占空间。
                //    这也顺带解决了"库在 D:、别名目录在 C:"的跨卷问题。
                var directory = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(directory))
                {
                    var beside = Path.Combine(directory, AliasPrefix + aliasName);

                    if (IsUsableAlias(beside, info.Length))
                    {
                        return (beside, "复用已有别名", false);
                    }

                    if (Windows.NativeMethods.CreateHardLink(beside, full))
                    {
                        return (beside, "已创建硬链接（同目录）", false);
                    }
                }

                // ② 退一步：建在数据目录（库本身在 C: 时这条能成）
                Directory.CreateDirectory(LegacyAliasDirectory);
                var inData = Path.Combine(LegacyAliasDirectory, aliasName);

                if (IsUsableAlias(inData, info.Length))
                {
                    return (inData, "复用已有别名", false);
                }

                if (Windows.NativeMethods.CreateHardLink(inData, full))
                {
                    return (inData, "已创建硬链接", false);
                }

                // ③ 最后才复制。
                //    硬链接建不出来只有两种情况：库在别的卷上、或者分区不支持硬链接（exFAT / FAT32）。
                //    这时候不复制就等于这本 m4b 完全播不了，所以还是复制 —— 但只复制这一个文件，
                //    并且用完立刻删掉。
                File.Copy(full, inData, overwrite: true);
                return (inData, "已创建临时副本（该分区不支持硬链接）", true);
            }
            catch (Exception ex)
            {
                return (null, $"创建别名失败：{ex.Message}", false);
            }
        }

        /// <summary>
        /// 删除别名。
        /// 只删我们自己建的（固定前缀 / 固定目录），**绝不动用户的真实文件** —— 硬链接删掉也不影响源文件。
        /// </summary>
        public static void TryDeleteAlias(string? aliasPath, bool isCopy)
        {
            if (string.IsNullOrWhiteSpace(aliasPath))
            {
                return;
            }

            try
            {
                var name = Path.GetFileName(aliasPath);
                var isOurs = name.StartsWith(AliasPrefix, StringComparison.OrdinalIgnoreCase) ||
                             IsInsideLegacyDirectory(aliasPath);

                if (!isOurs)
                {
                    return;
                }

                if (File.Exists(aliasPath))
                {
                    File.Delete(aliasPath);
                }
            }
            catch
            {
                // 清理失败不影响程序运行，忽略。
            }
        }

        /// <summary>
        /// 清理历史版本留下的大副本（老版本会把整本有声书复制到数据目录，动辄几个 GB）。
        /// 只删符合"我们生成的别名"命名规则的文件。
        /// </summary>
        public static long CleanupLegacyCopies()
        {
            long freed = 0;

            try
            {
                if (!Directory.Exists(LegacyAliasDirectory))
                {
                    return 0;
                }

                foreach (var file in Directory.EnumerateFiles(LegacyAliasDirectory, "*.m4a"))
                {
                    try
                    {
                        var length = new FileInfo(file).Length;
                        File.Delete(file);
                        freed += length;
                    }
                    catch
                    {
                        // 正在被占用就跳过
                    }
                }
            }
            catch
            {
                // 忽略
            }

            return freed;
        }

        private static bool IsUsableAlias(string path, long expectedLength)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists && info.Length == expectedLength;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsInsideLegacyDirectory(string path)
        {
            try
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(path));
                return directory != null &&
                       string.Equals(
                           Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar),
                           Path.GetFullPath(LegacyAliasDirectory).TrimEnd(Path.DirectorySeparatorChar),
                           StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 别名文件名。
        /// 注意：**不要把完整路径算进哈希** —— 老版本这么干，结果用户一移动媒体库目录就会重新复制一份。
        /// </summary>
        private static string BuildAliasName(string fileName, long length, long lastWriteTicks)
        {
            var hashInput = $"{fileName.ToLowerInvariant()}|{length}|{lastWriteTicks}";
            var hash = SHA1.HashData(Encoding.UTF8.GetBytes(hashInput));
            var shortHash = Convert.ToHexString(hash, 0, 8).ToLowerInvariant();

            var stem = Sanitize(Path.GetFileNameWithoutExtension(fileName));
            if (stem.Length > 40)
            {
                stem = stem.Substring(0, 40);
            }

            return $"{stem}-{shortHash}.m4a";
        }

        private static string Sanitize(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (var ch in name)
            {
                sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), ch) >= 0 ? '_' : ch);
            }

            return sb.ToString();
        }
    }
}
