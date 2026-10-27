import { fileIo } from '@kit.CoreFileKit';
import { BusinessError } from '@kit.BasicServicesKit';
import { hilog } from '@kit.PerformanceAnalysisKit';

/** 音频文件内部的一个章节标记 */
export class AudioChapterMark {
  startMs: number = 0;
  title: string = '';

  constructor(startMs: number, title: string) {
    this.startMs = startMs;
    this.title = title;
  }
}

/**
 * 从 MP4/M4B 里读内嵌章节表（Nero 的 chpl box）。
 *
 * 对应桌面版的 MediaTagReader.ReadChpl：
 *   chpl 结构 = version(1)+flags(3)+reserved(4)+count(1)
 *              每项 = 开始时间(8 字节大端, 100ns 单位) + 标题长度(1) + 标题(UTF-8)
 *
 * 位置：moov/udta/chpl（也有放在 moov/udta/meta/ilst 下的），
 * 所以这里按 box 树往下走，找到 chpl 就解析。
 */
export class Mp4Chapters {
  private static readonly TAG: string = 'KATARU';
  /** moov 最多读这么大（正常几 MB，防止异常文件吃内存） */
  private static readonly MAX_MOOV: number = 32 * 1024 * 1024;

  /** 读章节表；读不到就返回空数组 */
  static read(path: string): AudioChapterMark[] {
    let file: fileIo.File | null = null;

    try {
      const stat = fileIo.statSync(path);
      const fileSize = stat.size;
      if (fileSize < 16) {
        return [];
      }

      file = fileIo.openSync(path, fileIo.OpenMode.READ_ONLY);
      const fd = file.fd;

      // 顶层 box：找 moov
      let offset = 0;
      let moovOffset = -1;
      let moovSize = 0;
      while (offset + 8 <= fileSize) {
        const header = Mp4Chapters.readBytes(fd, offset, 16);
        if (header.byteLength < 8) {
          break;
        }

        const view = new DataView(header);
        let size = view.getUint32(0);
        const type = Mp4Chapters.fourCc(header, 4);
        let headerSize = 8;

        if (size === 1) {
          if (header.byteLength < 16) {
            break;
          }
          // 64 位长度
          const high = view.getUint32(8);
          const low = view.getUint32(12);
          size = high * 4294967296 + low;
          headerSize = 16;
        } else if (size === 0) {
          size = fileSize - offset;
        }

        if (size < headerSize) {
          break;
        }

        if (type === 'moov') {
          moovOffset = offset + headerSize;
          moovSize = size - headerSize;
          break;
        }

        offset += size;
      }

      if (moovOffset < 0 || moovSize <= 0) {
        hilog.info(0x0000, Mp4Chapters.TAG, 'no moov box in %{public}s', path);
        return [];
      }

      const readSize = Math.min(moovSize, Mp4Chapters.MAX_MOOV);
      const moov = Mp4Chapters.readBytes(fd, moovOffset, readSize);
      hilog.info(0x0000, Mp4Chapters.TAG, 'moov: %{public}d bytes (declared %{public}d)',
        moov.byteLength, moovSize);

      return Mp4Chapters.walk(moov, 0, moov.byteLength, 0);
    } catch (error) {
      const err = error as BusinessError;
      hilog.warn(0x0000, Mp4Chapters.TAG, 'read chapters failed: code=%{public}d msg=%{public}s',
        err.code, err.message);
      return [];
    } finally {
      if (file !== null) {
        try {
          fileIo.closeSync(file);
        } catch (error) {
          // 忽略
        }
      }
    }
  }

  /** 在一段内存里遍历 box，遇到 chpl 解析，遇到容器继续往下 */
  private static walk(buffer: ArrayBuffer, start: number, end: number, depth: number): AudioChapterMark[] {
    const result: AudioChapterMark[] = [];
    if (depth > 8) {
      return result;
    }

    let offset = start;
    while (offset + 8 <= end) {
      const view = new DataView(buffer, offset);
      let size = view.getUint32(0);
      const type = Mp4Chapters.fourCc(buffer, offset + 4);
      let headerSize = 8;

      if (size === 1) {
        if (offset + 16 > end) {
          break;
        }
        const big = new DataView(buffer, offset + 8);
        const high = big.getUint32(0);
        const low = big.getUint32(4);
        size = high * 4294967296 + low;
        headerSize = 16;
      } else if (size === 0) {
        size = end - offset;
      }

      if (size < headerSize || offset + size > end) {
        break;
      }

      const payloadStart = offset + headerSize;
      const payloadEnd = offset + size;

      if (type === 'chpl') {
        const chapters = Mp4Chapters.parseChpl(buffer, payloadStart, payloadEnd);
        for (const chapter of chapters) {
          result.push(chapter);
        }
        if (result.length > 0) {
          return result;
        }
      } else if (type === 'moov' || type === 'udta' || type === 'trak' || type === 'mdia') {
        const nested = Mp4Chapters.walk(buffer, payloadStart, payloadEnd, depth + 1);
        for (const chapter of nested) {
          result.push(chapter);
        }
        if (result.length > 0) {
          return result;
        }
      } else if (type === 'meta') {
        // meta 的 children 前面多 4 字节 version/flags
        const nested = Mp4Chapters.walk(buffer, payloadStart + 4, payloadEnd, depth + 1);
        for (const chapter of nested) {
          result.push(chapter);
        }
        if (result.length > 0) {
          return result;
        }
      }

      offset += size;
    }

    return result;
  }

  /** 解析 chpl */
  private static parseChpl(buffer: ArrayBuffer, start: number, end: number): AudioChapterMark[] {
    const chapters: AudioChapterMark[] = [];
    if (start + 9 > end) {
      return chapters;
    }

    const view = new DataView(buffer);
    let offset = start + 4; // version + flags
    offset += 4;           // reserved
    if (offset >= end) {
      return chapters;
    }

    const count = view.getUint8(offset);
    offset++;

    for (let i = 0; i < count && offset < end; i++) {
      if (offset + 9 > end) {
        break;
      }

      const high = view.getUint32(offset);
      const low = view.getUint32(offset + 4);
      offset += 8;

      const titleLength = view.getUint8(offset);
      offset++;

      if (titleLength < 0 || offset + titleLength > end) {
        break;
      }

      const titleBytes = new Uint8Array(buffer, offset, titleLength);
      offset += titleLength;

      // chpl 的时间戳是 100ns 单位 → 毫秒
      const ticks = high * 4294967296 + low;
      const startMs = Math.round(ticks / 10000);
      const title = Mp4Chapters.decodeUtf8(titleBytes).trim();

      chapters.push(new AudioChapterMark(startMs, title));
    }

    hilog.info(0x0000, Mp4Chapters.TAG, 'chpl parsed: %{public}d chapter(s)', chapters.length);
    return chapters;
  }

  private static decodeUtf8(bytes: Uint8Array): string {
    let out = '';
    for (let i = 0; i < bytes.length; i++) {
      out += String.fromCharCode(bytes[i]);
    }
    return out;
  }

  private static fourCc(buffer: ArrayBuffer, offset: number): string {
    const bytes = new Uint8Array(buffer, offset, 4);
    let text = '';
    for (let i = 0; i < 4; i++) {
      text += String.fromCharCode(bytes[i]);
    }
    return text;
  }

  private static readBytes(fd: number, offset: number, length: number): ArrayBuffer {
    const buffer = new ArrayBuffer(length);
    const read = fileIo.readSync(fd, buffer, { offset: offset, length: length });
    if (read <= 0) {
      return new ArrayBuffer(0);
    }
    if (read === length) {
      return buffer;
    }
    // 只读到一部分：截断到实际长度
    const view = new Uint8Array(buffer, 0, read);
    const trimmed = new ArrayBuffer(read);
    new Uint8Array(trimmed).set(view);
    return trimmed;
  }
}
