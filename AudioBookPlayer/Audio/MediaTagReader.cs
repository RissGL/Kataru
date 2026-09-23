using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AudioBookPlayer.Audio
{
    /// <summary>音频文件内部的一个章节标记（m4b 的 chpl）。</summary>
    public sealed class AudioChapter
    {
        public AudioChapter(TimeSpan start, string title)
        {
            Start = start;
            Title = title;
        }

        public TimeSpan Start { get; }

        public string Title { get; }

        public override string ToString() => $"{Core.Timecode.Format(Start)} {Title}";
    }

    /// <summary>从音频文件里读出来的标签信息。</summary>
    public sealed class AudioTagInfo
    {
        public string? Title { get; set; }

        public string? Album { get; set; }

        /// <summary>©ART / TPE1 —— 有声书里通常就是作者。</summary>
        public string? Artist { get; set; }

        public string? AlbumArtist { get; set; }

        /// <summary>©wrt / TCOM —— 作曲 / 著者。</summary>
        public string? Composer { get; set; }

        /// <summary>朗读者（iTunes 的 NARRATOR 自由标签）。</summary>
        public string? Narrator { get; set; }

        public string? Genre { get; set; }

        public string? Year { get; set; }

        public string? Comment { get; set; }

        /// <summary>内嵌封面图片数据。</summary>
        public byte[]? CoverData { get; set; }

        public string? CoverMimeType { get; set; }

        /// <summary>容器类型（M4B / MP3 / 未知）。</summary>
        public string? Container { get; set; }

        /// <summary>文件内章节标记。</summary>
        public IReadOnlyList<AudioChapter> Chapters { get; set; } = Array.Empty<AudioChapter>();

        /// <summary>总时长（能从 mvhd 之类读到的话）。</summary>
        public TimeSpan? Duration { get; set; }

        public bool HasCover => CoverData != null && CoverData.Length > 0;

        public bool HasChapters => Chapters.Count > 0;

        /// <summary>界面显示用的作者（优先著者，其次艺术家）。</summary>
        public string? DisplayAuthor =>
            !string.IsNullOrWhiteSpace(Composer) ? Composer :
            !string.IsNullOrWhiteSpace(Artist) ? Artist :
            AlbumArtist;

        /// <summary>书名（优先专辑名，其次标题）。</summary>
        public string? DisplayTitle =>
            !string.IsNullOrWhiteSpace(Album) ? Album :
            !string.IsNullOrWhiteSpace(Title) ? Title : null;

        /// <summary>拼一行"作者 · 朗读者"给界面用。</summary>
        public string DisplayCredits
        {
            get
            {
                var parts = new List<string>(2);
                if (!string.IsNullOrWhiteSpace(DisplayAuthor))
                {
                    parts.Add(DisplayAuthor!);
                }

                if (!string.IsNullOrWhiteSpace(Narrator))
                {
                    parts.Add("朗读：" + Narrator);
                }

                return string.Join(" · ", parts);
            }
        }
    }

    /// <summary>
    /// 读 MP4/M4B 与 MP3 的元数据：标题 / 作者 / 朗读者 / 内嵌封面 / 文件内章节。
    ///
    /// 只做 seek 不做全文件读取，几百 MB 的 m4b 也能秒开；任何异常都吞掉返回 null，
    /// 解析失败不影响播放。
    /// </summary>
    public static class MediaTagReader
    {
        private static readonly Dictionary<string, (DateTime Stamp, AudioTagInfo? Info)> Cache =
            new Dictionary<string, (DateTime, AudioTagInfo?)>(StringComparer.OrdinalIgnoreCase);

        /// <summary>按扩展名分发；带缓存（文件修改时间变了才重读）。</summary>
        public static AudioTagInfo? TryReadCached(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            DateTime stamp;
            try
            {
                stamp = File.GetLastWriteTimeUtc(path);
            }
            catch (Exception)
            {
                return null;
            }

            if (Cache.TryGetValue(path, out var cached) && cached.Stamp == stamp)
            {
                return cached.Info;
            }

            var info = TryRead(path);
            Cache[path] = (stamp, info);
            return info;
        }

        public static void ClearCache() => Cache.Clear();

        public static AudioTagInfo? TryRead(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

                var info = LooksLikeMp4(stream) ? ReadMp4(stream) : LooksLikeId3(stream) ? ReadId3(stream) : null;

                if (info != null)
                {
                    info.Container = LooksLikeMp4(stream) ? "MP4/M4B" : "MP3";
                }

                return info;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>把内嵌封面写成文件（给界面绑定路径用），返回文件路径。</summary>
        public static string? SaveCoverToFile(byte[] data, string? mimeType, string cacheDirectory, string key)
        {
            try
            {
                var extension = mimeType switch
                {
                    "image/png" => ".png",
                    "image/gif" => ".gif",
                    "image/bmp" => ".bmp",
                    _ => ".jpg",
                };

                Directory.CreateDirectory(cacheDirectory);
                var target = Path.Combine(cacheDirectory, Sanitize(key) + extension);

                if (!File.Exists(target) || new FileInfo(target).Length != data.Length)
                {
                    File.WriteAllBytes(target, data);
                }

                return target;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string Sanitize(string value)
        {
            var builder = new StringBuilder(value.Length);
            foreach (var ch in value)
            {
                builder.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), ch) >= 0 ? '_' : ch);
            }

            var result = builder.ToString();
            return result.Length > 80 ? result.Substring(0, 80) : result;
        }

        // ==================== MP4 / M4B ====================

        private static bool LooksLikeMp4(Stream stream)
        {
            if (stream.Length < 12)
            {
                return false;
            }

            stream.Position = 4;
            return ReadFourCc(stream) == "ftyp";
        }

        private static bool LooksLikeId3(Stream stream)
        {
            if (stream.Length < 10)
            {
                return false;
            }

            stream.Position = 0;
            return ReadMagic(stream, "ID3");
        }

        private static AudioTagInfo? ReadMp4(Stream stream)
        {
            var info = new AudioTagInfo();
            stream.Position = 0;
            ParseBoxes(stream, stream.Length, info, 0);
            return info;
        }

        /// <summary>解析 [start, end) 区间里的所有 box，遇到容器就递归。</summary>
        private static void ParseBoxes(Stream stream, long end, AudioTagInfo info, int depth)
        {
            if (depth > 8)
            {
                return;
            }

            while (stream.Position + 8 <= end)
            {
                var boxStart = stream.Position;
                var size = ReadUInt32BE(stream);
                var type = ReadFourCc(stream);

                long payloadSize;
                if (size == 1)
                {
                    if (stream.Position + 8 > end)
                    {
                        return;
                    }

                    payloadSize = (long)ReadUInt64BE(stream) - 16;
                }
                else if (size == 0)
                {
                    payloadSize = end - stream.Position;
                }
                else
                {
                    payloadSize = size - 8;
                }

                if (payloadSize < 0 || stream.Position + payloadSize > end)
                {
                    return; // 盒子坏了，别硬读
                }

                var payloadEnd = stream.Position + payloadSize;

                switch (type)
                {
                    case "moov":
                    case "udta":
                    case "trak":
                    case "mdia":
                    case "minf":
                    case "stbl":
                        ParseBoxes(stream, payloadEnd, info, depth + 1);
                        break;

                    case "meta":
                        stream.Position += 4; // version + flags
                        ParseBoxes(stream, payloadEnd, info, depth + 1);
                        break;

                    case "mvhd":
                        ReadMvhd(stream, payloadEnd, info);
                        break;

                    case "ilst":
                        // ilst 的子盒子全是"键盒子"（©nam / covr / aART / ---- …），
                        // 不能按普通容器递归，否则不姓 © 的键（covr、trkn）会被漏掉
                        ParseIlst(stream, payloadEnd, info);
                        break;

                    case "chpl":
                        ReadChpl(stream, payloadEnd, info);
                        break;

                    case "data":
                        // 只有出现在 ilst 的键盒子里才有意义，这里交给 ReadIlstKey 处理
                        break;

                    default:
                        break;
                }

                stream.Position = payloadEnd;
                if (stream.Position <= boxStart)
                {
                    return;
                }
            }
        }

        /// <summary>遍历 ilst 里的键盒子。</summary>
        private static void ParseIlst(Stream stream, long end, AudioTagInfo info)
        {
            while (stream.Position + 8 <= end)
            {
                var size = ReadUInt32BE(stream);
                var key = ReadFourCc(stream);
                var payloadEnd = stream.Position + Math.Max(0, size - 8);

                if (size < 8 || payloadEnd > end)
                {
                    return;
                }

                if (key == "----")
                {
                    ReadFreeformKey(stream, payloadEnd, info);
                }
                else
                {
                    ReadIlstKey(stream, payloadEnd, key, info);
                }

                stream.Position = payloadEnd;
            }
        }

        private static void ReadMvhd(Stream stream, long end, AudioTagInfo info)
        {
            var version = stream.ReadByte();
            stream.Position += 3; // flags

            long timescale;
            long duration;

            if (version == 1)
            {
                stream.Position += 16; // creation + modification
                timescale = ReadUInt32BE(stream);
                duration = (long)ReadUInt64BE(stream);
            }
            else
            {
                stream.Position += 8;
                timescale = ReadUInt32BE(stream);
                duration = ReadUInt32BE(stream);
            }

            if (timescale > 0 && duration > 0)
            {
                info.Duration = TimeSpan.FromSeconds(duration / (double)timescale);
            }

            stream.Position = end;
        }

        /// <summary>ilst 里的一个键盒子（©nam / ©ART / covr …），里面是 data 盒子。</summary>
        private static void ReadIlstKey(Stream stream, long end, string key, AudioTagInfo info)
        {
            while (stream.Position + 8 <= end)
            {
                var size = ReadUInt32BE(stream);
                var type = ReadFourCc(stream);
                var payloadEnd = stream.Position + Math.Max(0, size - 8);

                if (payloadEnd > end)
                {
                    return;
                }

                if (type == "data" && payloadEnd - stream.Position >= 8)
                {
                    var dataType = ReadUInt32BE(stream) & 0x00FFFFFF;
                    stream.Position += 4; // locale
                    var length = (int)(payloadEnd - stream.Position);
                    var payload = ReadBytes(stream, length);

                    ApplyIlstValue(key, dataType, payload, info);
                }

                stream.Position = payloadEnd;
            }
        }

        private static void ApplyIlstValue(string key, uint dataType, byte[] payload, AudioTagInfo info)
        {
            switch (key)
            {
                case "\u00A9nam":
                    info.Title = DecodeText(payload, dataType);
                    break;

                case "\u00A9alb":
                    info.Album = DecodeText(payload, dataType);
                    break;

                case "\u00A9ART":
                    info.Artist = DecodeText(payload, dataType);
                    break;

                case "aART":
                    info.AlbumArtist = DecodeText(payload, dataType);
                    break;

                case "\u00A9wrt":
                    info.Composer = DecodeText(payload, dataType);
                    break;

                case "\u00A9gen":
                    info.Genre = DecodeText(payload, dataType);
                    break;

                case "\u00A9day":
                    info.Year = DecodeText(payload, dataType);
                    break;

                case "\u00A9cmt":
                    info.Comment = DecodeText(payload, dataType);
                    break;

                case "covr":
                    if (payload.Length > 0 && info.CoverData == null)
                    {
                        info.CoverData = payload;
                        info.CoverMimeType = dataType == 14 ? "image/png" : "image/jpeg";
                    }

                    break;
            }
        }

        /// <summary>iTunes 的自由标签（----），朗读者一般放在 com.apple.iTunes:NARRATOR。</summary>
        private static void ReadFreeformKey(Stream stream, long end, AudioTagInfo info)
        {
            string? name = null;
            byte[]? value = null;

            while (stream.Position + 8 <= end)
            {
                var size = ReadUInt32BE(stream);
                var type = ReadFourCc(stream);
                var payloadEnd = stream.Position + Math.Max(0, size - 8);

                if (payloadEnd > end)
                {
                    return;
                }

                if (type == "name")
                {
                    stream.Position += 4;
                    name = Encoding.UTF8.GetString(ReadBytes(stream, (int)(payloadEnd - stream.Position))).Trim();
                }
                else if (type == "data")
                {
                    var dataType = ReadUInt32BE(stream) & 0x00FFFFFF;
                    stream.Position += 4;
                    value = ReadBytes(stream, (int)(payloadEnd - stream.Position));

                    if (name != null &&
                        name.Contains("NARRATOR", StringComparison.OrdinalIgnoreCase) &&
                        value.Length > 0)
                    {
                        info.Narrator = DecodeText(value, dataType);
                    }
                }

                stream.Position = payloadEnd;
            }
        }

        /// <summary>Nero 的章节表：version(1)+flags(3)+reserved(4)+count(1)，每项 = 开始时间(8, 100ns) + 标题长度(1) + 标题。</summary>
        private static void ReadChpl(Stream stream, long end, AudioTagInfo info)
        {
            stream.Position += 4; // version + flags
            stream.Position += 4; // reserved
            if (stream.Position >= end)
            {
                return;
            }

            var count = stream.ReadByte();
            var chapters = new List<AudioChapter>(Math.Max(0, count));

            for (var i = 0; i < count && stream.Position < end; i++)
            {
                if (stream.Position + 9 > end)
                {
                    break;
                }

                var start = (long)ReadUInt64BE(stream);
                var titleLength = stream.ReadByte();
                if (titleLength < 0 || stream.Position + titleLength > end)
                {
                    break;
                }

                var title = Encoding.UTF8.GetString(ReadBytes(stream, titleLength)).Trim();

                // 规范里 chpl 的时间戳就是 100ns 单位，正好等于 TimeSpan 的 tick
                chapters.Add(new AudioChapter(TimeSpan.FromTicks(start), title));
            }

            if (chapters.Count > 0)
            {
                info.Chapters = chapters;
            }

            stream.Position = end;
        }

        // ==================== MP3 (ID3v2) ====================

        private static AudioTagInfo? ReadId3(Stream stream)
        {
            stream.Position = 0;
            if (!ReadMagic(stream, "ID3"))
            {
                return null;
            }

            var major = stream.ReadByte();
            stream.Position += 1; // revision
            var flags = stream.ReadByte();
            var size = ReadSyncSafe(stream);

            if (major < 2 || size <= 0)
            {
                return null;
            }

            var end = Math.Min(stream.Length, stream.Position + size);
            var info = new AudioTagInfo();

            if ((flags & 0x40) != 0 && stream.Position + 4 <= end)
            {
                var extendedSize = major >= 4 ? ReadSyncSafe(stream) : (int)ReadUInt32BE(stream);
                stream.Position += Math.Max(0, extendedSize - 4);
            }

            while (stream.Position + (major >= 3 ? 10 : 6) <= end)
            {
                var id = ReadFourCc(stream);
                if (id.Length == 0 || id[0] == '\0')
                {
                    break; // 到填充区了
                }

                int frameSize;
                if (major >= 3)
                {
                    frameSize = major >= 4
                        ? ReadSyncSafe(stream)
                        : (int)ReadUInt32BE(stream);
                    stream.Position += 2; // flags
                }
                else
                {
                    frameSize = (ReadByte(stream) << 16) | (ReadByte(stream) << 8) | ReadByte(stream);
                }

                if (frameSize <= 0 || stream.Position + frameSize > end)
                {
                    break;
                }

                var payload = ReadBytes(stream, frameSize);

                switch (id)
                {
                    case "TIT2":
                        info.Title = DecodeId3Text(payload);
                        break;
                    case "TALB":
                        info.Album = DecodeId3Text(payload);
                        break;
                    case "TPE1":
                        info.Artist = DecodeId3Text(payload);
                        break;
                    case "TPE2":
                        info.AlbumArtist = DecodeId3Text(payload);
                        break;
                    case "TCOM":
                        info.Composer = DecodeId3Text(payload);
                        break;
                    case "TCON":
                        info.Genre = DecodeId3Text(payload);
                        break;
                    case "TYER":
                    case "TDRC":
                        info.Year = DecodeId3Text(payload);
                        break;
                    case "COMM":
                        info.Comment = DecodeId3Comment(payload);
                        break;
                    case "APIC":
                        ReadApic(payload, info);
                        break;
                }
            }

            return info;
        }

        private static void ReadApic(byte[] payload, AudioTagInfo info)
        {
            if (payload.Length < 4 || info.CoverData != null)
            {
                return;
            }

            var encoding = payload[0];
            var index = 1;

            var mimeEnd = Array.IndexOf(payload, (byte)0, index);
            if (mimeEnd < 0)
            {
                return;
            }

            var mime = Encoding.ASCII.GetString(payload, index, mimeEnd - index);
            index = mimeEnd + 1;

            if (index >= payload.Length)
            {
                return;
            }

            index++; // 图片类型

            // 描述文本（按编码决定是单字节还是双字节结尾）
            if (encoding == 1 || encoding == 2)
            {
                while (index + 1 < payload.Length && !(payload[index] == 0 && payload[index + 1] == 0))
                {
                    index += 2;
                }

                index += 2;
            }
            else
            {
                var descriptionEnd = Array.IndexOf(payload, (byte)0, index);
                index = descriptionEnd < 0 ? payload.Length : descriptionEnd + 1;
            }

            if (index >= payload.Length)
            {
                return;
            }

            var data = new byte[payload.Length - index];
            Array.Copy(payload, index, data, 0, data.Length);

            info.CoverData = data;
            info.CoverMimeType = mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                ? mime
                : GuessImageMime(data);
        }

        private static string GuessImageMime(byte[] data)
        {
            if (data.Length > 8 && data[0] == 0x89 && data[1] == 0x50)
            {
                return "image/png";
            }

            if (data.Length > 3 && data[0] == 0x47 && data[1] == 0x49)
            {
                return "image/gif";
            }

            return "image/jpeg";
        }

        private static string? DecodeId3Text(byte[] payload)
        {
            var value = DecodeId3String(payload, 0, payload.Length);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static string? DecodeId3Comment(byte[] payload)
        {
            if (payload.Length < 4)
            {
                return null;
            }

            var encoding = payload[0];
            var index = 4; // encoding + language

            if (encoding == 1 || encoding == 2)
            {
                while (index + 1 < payload.Length && !(payload[index] == 0 && payload[index + 1] == 0))
                {
                    index += 2;
                }

                index += 2;
            }
            else
            {
                var end = Array.IndexOf(payload, (byte)0, index);
                index = end < 0 ? payload.Length : end + 1;
            }

            var value = DecodeId3String(payload, 0, payload.Length, index);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static string DecodeId3String(byte[] payload, int start, int end, int textStart = 1)
        {
            if (payload.Length <= textStart)
            {
                return string.Empty;
            }

            var encoding = payload[0];
            var length = end - textStart;
            if (length <= 0)
            {
                return string.Empty;
            }

            return encoding switch
            {
                0 => Encoding.Latin1.GetString(payload, textStart, length).TrimEnd('\0'),
                1 => Encoding.Unicode.GetString(payload, textStart, length).TrimEnd('\0'),
                2 => Encoding.BigEndianUnicode.GetString(payload, textStart, length).TrimEnd('\0'),
                _ => Encoding.UTF8.GetString(payload, textStart, length).TrimEnd('\0'),
            };
        }

        // ==================== 基础读取 ====================

        private static string DecodeText(byte[] payload, uint dataType)
        {
            if (payload.Length == 0)
            {
                return string.Empty;
            }

            if (dataType == 2)
            {
                return Encoding.BigEndianUnicode.GetString(payload).TrimEnd('\0');
            }

            return Encoding.UTF8.GetString(payload).TrimEnd('\0');
        }

        /// <summary>
        /// 读 4 字节原子类型。必须用 Latin-1：iTunes 的键名里 © 是单字节 0xA9，
        /// 用 ASCII 解出来会变成 '?'，那样 ©nam / ©ART / covr 就全认不出来了。
        /// </summary>
        private static string ReadFourCc(Stream stream)
        {
            var buffer = new byte[4];
            var read = stream.Read(buffer, 0, 4);
            return read < 4 ? string.Empty : Encoding.Latin1.GetString(buffer);
        }

        /// <summary>读固定长度的 ASCII 魔数（"ID3" 这类不是 4 字节的）。</summary>
        private static bool ReadMagic(Stream stream, string magic)
        {
            var buffer = new byte[magic.Length];
            if (stream.Read(buffer, 0, buffer.Length) < buffer.Length)
            {
                return false;
            }

            for (var i = 0; i < buffer.Length; i++)
            {
                if (buffer[i] != (byte)magic[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static uint ReadUInt32BE(Stream stream)
        {
            var buffer = new byte[4];
            if (stream.Read(buffer, 0, 4) < 4)
            {
                return 0;
            }

            return ((uint)buffer[0] << 24) | ((uint)buffer[1] << 16) | ((uint)buffer[2] << 8) | buffer[3];
        }

        private static ulong ReadUInt64BE(Stream stream)
        {
            var high = ReadUInt32BE(stream);
            var low = ReadUInt32BE(stream);
            return ((ulong)high << 32) | low;
        }

        private static int ReadSyncSafe(Stream stream)
        {
            var buffer = new byte[4];
            if (stream.Read(buffer, 0, 4) < 4)
            {
                return 0;
            }

            return ((buffer[0] & 0x7F) << 21) | ((buffer[1] & 0x7F) << 14) | ((buffer[2] & 0x7F) << 7) | (buffer[3] & 0x7F);
        }

        private static int ReadByte(Stream stream) => stream.ReadByte() is var value && value >= 0 ? value : 0;

        private static byte[] ReadBytes(Stream stream, int count)
        {
            if (count <= 0)
            {
                return Array.Empty<byte>();
            }

            var buffer = new byte[count];
            var offset = 0;
            while (offset < count)
            {
                var read = stream.Read(buffer, offset, count - offset);
                if (read <= 0)
                {
                    break;
                }

                offset += read;
            }

            return offset == count ? buffer : buffer[..offset];
        }
    }
}
