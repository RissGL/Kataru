/**
 * 时间码工具（从桌面版 Core/Timecode.cs 移植）。
 *
 * SRT 的时间格式：00:00:01,000 --> 00:00:04,500
 * 内部统一用毫秒数表示，避免 ArkTS 里到处转 TimeSpan 之类的对象。
 */
export class Timecode {
  /** 把 "00:00:01,000" 解析成毫秒；解析失败返回 -1。 */
  static parse(text: string): number {
    const trimmed = text.trim();
    // 兼容 00:00:01,000 / 00:00:01.000 / 0:00:01,000
    const match = trimmed.match(/^(\d+):(\d{1,2}):(\d{1,2})[,.](\d{1,3})$/);
    if (match === null) {
      return -1;
    }

    const hours = Number.parseInt(match[1], 10);
    const minutes = Number.parseInt(match[2], 10);
    const seconds = Number.parseInt(match[3], 10);
    let millis = Number.parseInt(match[4], 10);
    // "5" 表示 500ms，"50" 表示 50ms —— SRT 里毫秒通常补零到三位
    if (match[4].length === 1) {
      millis *= 100;
    } else if (match[4].length === 2) {
      millis *= 10;
    }

    return ((hours * 60 + minutes) * 60 + seconds) * 1000 + millis;
  }

  /** 毫秒 → "00:00:01,000" */
  static format(milliseconds: number): string {
    const total = Math.max(0, Math.floor(milliseconds));
    const hours = Math.floor(total / 3600000);
    const minutes = Math.floor((total % 3600000) / 60000);
    const seconds = Math.floor((total % 60000) / 1000);
    const millis = total % 1000;

    return `${Timecode.pad(hours, 2)}:${Timecode.pad(minutes, 2)}:${Timecode.pad(seconds, 2)},${Timecode.pad(millis, 3)}`;
  }

  /** 毫秒 → "01:23"（界面上显示进度用，省掉毫秒） */
  static formatShort(milliseconds: number): string {
    const total = Math.max(0, Math.floor(milliseconds / 1000));
    const hours = Math.floor(total / 3600);
    const minutes = Math.floor((total % 3600) / 60);
    const seconds = total % 60;

    if (hours > 0) {
      return `${hours}:${Timecode.pad(minutes, 2)}:${Timecode.pad(seconds, 2)}`;
    }

    return `${minutes}:${Timecode.pad(seconds, 2)}`;
  }

  private static pad(value: number, width: number): string {
    let text = `${value}`;
    while (text.length < width) {
      text = '0' + text;
    }
    return text;
  }
}
