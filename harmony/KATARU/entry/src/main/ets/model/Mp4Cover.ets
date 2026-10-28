import { fileIo } from '@kit.CoreFileKit';
import { hilog } from '@kit.PerformanceAnalysisKit';

/**
 * 从 m4b / m4a 里抠出内嵌封面。
 *
 * 有声书的封面一般藏在 MP4 元数据里：
 *   moov → udta → meta → ilst → covr → data
 * `data` 的载荷就是图片原始字节（JPEG / PNG）。
 *
 * 只读 moov（通常几 MB），不会把 700 MB 的音频整个读进内存。
 */
export class Mp4Cover {
  private static readonly TAG: string = 'KATARU';
  private static readonly MAX_MOOV: number = 32 * 1024 * 1024;

  /** 抠出封面字节；没有就返回 null。第二个返回值是扩展名推断用的类型。 */
  static extract(path: string): CoverBytes | null {
    let file: fileIo.File | null = null;
    try {
      file = fileIo.openSync(path, fileIo.OpenMode.READ_ONLY);
      const stat = fileIo.statSync(file.fd);
      const fileSize = stat.size;

      // 顶层遍历，找到 moov
      let offset = 0;
      while (offset + 8 <= fileSize) {
        const header = Mp4Cover.readBytes(file.fd, offset, 8);
        if (header.byteLength < 8) {
          break;
        }
        const view = new DataView(header);
        let boxSize = view.getUint32(0);
        const type = Mp4Cover.fourCc(header, 4);
        let headerSize = 8;

        if (boxSize === 1) {
          // 64 位长度
          const ext = Mp4Cover.readBytes(file.fd, offset + 8, 8);
          if (ext.byteLength < 8) {
            break;
          }
          const extView = new DataView(ext);
          boxSize = extView.getUint32(0) * 4294967296 + extView.getUint32(4);
          headerSize = 16;
        } else if (boxSize === 0) {
          boxSize = fileSize - offset;
        }

        if (boxSize < headerSize) {
          break;
        }

        if (type === 'moov') {
          const bodyLength = Math.min(boxSize - headerSize, Mp4Cover.MAX_MOOV);
          const moov = Mp4Cover.readBytes(file.fd, offset + headerSize, bodyLength);
          return Mp4Cover.findCover(moov, 0, moov.byteLength);
        }

        offset += boxSize;
      }

      return null;
    } catch (error) {
      hilog.warn(0x0000, Mp4Cover.TAG, 'cover extract failed: %{public}s', JSON.stringify(error));
      return null;
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

  /** 在 moov 里递归找 covr */
  private static findCover(buffer: ArrayBuffer, start: number, end: number): CoverBytes | null {
    let offset = start;
    while (offset + 8 <= end) {
      const view = new DataView(buffer, offset, Math.min(8, end - offset));
      const boxSize = view.getUint32(0);
      const type = Mp4Cover.fourCc(buffer, offset + 4);
      if (boxSize < 8 || offset + boxSize > end) {
        return null;
      }

      const bodyStart = offset + 8;
      const bodyEnd = offset + boxSize;

      if (type === 'covr') {
        return Mp4Cover.readCovr(buffer, bodyStart, bodyEnd);
      }

      // meta 是 full box，多 4 字节 version/flags
      if (type === 'meta') {
        const nested = Mp4Cover.findCover(buffer, bodyStart + 4, bodyEnd);
        if (nested !== null) {
          return nested;
        }
      } else if (type === 'udta' || type === 'ilst') {
        const nested = Mp4Cover.findCover(buffer, bodyStart, bodyEnd);
        if (nested !== null) {
          return nested;
        }
      }

      offset = bodyEnd;
    }
    return null;
  }

  /** covr 里是一个或多个 data box（type 13=JPEG, 14=PNG） */
  private static readCovr(buffer: ArrayBuffer, start: number, end: number): CoverBytes | null {
    let offset = start;
    while (offset + 8 <= end) {
      const view = new DataView(buffer, offset, Math.min(16, end - offset));
      const boxSize = view.getUint32(0);
      const type = Mp4Cover.fourCc(buffer, offset + 4);
      if (boxSize < 8 || offset + boxSize > end) {
        return null;
      }

      if (type === 'data' && boxSize > 16) {
        // data: 4 字节 type + 4 字节 locale，然后是图片数据
        const dataType = view.getUint32(8);
        const payloadStart = offset + 16;
        const payloadLength = boxSize - 16;
        const bytes = new Uint8Array(buffer, payloadStart, payloadLength);

        let extension = 'jpg';
        if (dataType === 14 || Mp4Cover.looksLikePng(bytes)) {
          extension = 'png';
        }
        return new CoverBytes(bytes, extension);
      }

      offset += boxSize;
    }
    return null;
  }

  private static looksLikePng(bytes: Uint8Array): boolean {
    return bytes.length > 8 && bytes[0] === 0x89 && bytes[1] === 0x50 && bytes[2] === 0x4E && bytes[3] === 0x47;
  }

  private static fourCc(buffer: ArrayBuffer, offset: number): string {
    if (offset + 4 > buffer.byteLength) {
      return '';
    }
    const bytes = new Uint8Array(buffer, offset, 4);
    return String.fromCharCode(bytes[0], bytes[1], bytes[2], bytes[3]);
  }

  private static readBytes(fd: number, offset: number, length: number): ArrayBuffer {
    if (length <= 0) {
      return new ArrayBuffer(0);
    }
    const buffer = new ArrayBuffer(length);
    try {
      fileIo.readSync(fd, buffer, { offset: offset, length: length });
    } catch (error) {
      return new ArrayBuffer(0);
    }
    return buffer;
  }
}

/** 抠出来的封面字节 + 建议扩展名 */
export class CoverBytes {
  bytes: Uint8Array;
  extension: string;

  constructor(bytes: Uint8Array, extension: string) {
    this.bytes = bytes;
    this.extension = extension;
  }
}
