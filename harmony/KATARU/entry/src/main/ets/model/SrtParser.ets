import { Timecode } from './Timecode';

/**
 * 一条字幕（对应 .srt 里的一个条目）。
 */
export class SubtitleLine {
  index: number = 0;
  startMs: number = 0;
  endMs: number = 0;
  text: string = '';

  constructor(index: number, startMs: number, endMs: number, text: string) {
    this.index = index;
    this.startMs = startMs;
    this.endMs = endMs;
    this.text = text;
  }

  /** 给定播放位置是否落在本条区间内（左闭右开）。 */
  isVisibleAt(positionMs: number): boolean {
    return positionMs >= this.startMs && positionMs < this.endMs;
  }

  timeText(): string {
    return Timecode.formatShort(this.startMs);
  }
}

/**
 * SRT 解析器（从桌面版 Core/SrtParser.cs 移植）。
 *
 * 处理这些真实文件里常见的情况：
 *  - BOM、CRLF / LF 混用
 *  - 序号行缺失或错乱
 *  - 一条字幕跨多行
 *  - 时间码用逗号或小数点
 *  - 空块（只有序号没有时间码）
 */
export class SrtParser {
  /**
   * 解析 SRT 文本。
   * @param content 文件内容
   * @param warnings 收集解析过程中的告警（可选）
   */
  static parse(content: string, warnings?: string[]): SubtitleLine[] {
    const lines: SubtitleLine[] = [];
    if (content === null || content === undefined || content.length === 0) {
      return lines;
    }

    // 去 BOM
    let text = content;
    if (text.charCodeAt(0) === 0xfeff) {
      text = text.substring(1);
    }

    // 统一换行
    text = text.replace(/\r\n/g, '\n').replace(/\r/g, '\n');

    const blocks = text.split(/\n{2,}/);
    let autoIndex = 1;

    for (const block of blocks) {
      const rawLines = block.split('\n');
      // 去掉首尾空行
      while (rawLines.length > 0 && rawLines[0].trim().length === 0) {
        rawLines.shift();
      }
      while (rawLines.length > 0 && rawLines[rawLines.length - 1].trim().length === 0) {
        rawLines.pop();
      }
      if (rawLines.length === 0) {
        continue;
      }

      let cursor = 0;
      let index = autoIndex;

      // 第一行如果是纯数字，就是序号
      const first = rawLines[0].trim();
      if (/^\d+$/.test(first)) {
        index = Number.parseInt(first, 10);
        cursor = 1;
      }

      // 期望这一行是时间码
      if (cursor >= rawLines.length) {
        autoIndex++;
        continue;
      }

      const timeLine = rawLines[cursor].trim();
      const times = SrtParser.parseTimeLine(timeLine);
      if (times === null) {
        if (warnings !== undefined) {
          warnings.push(`第 ${index} 段没有时间码，已跳过`);
        }
        autoIndex++;
        continue;
      }

      cursor++;

      // 剩下的都是文本
      const textParts: string[] = [];
      for (let i = cursor; i < rawLines.length; i++) {
        textParts.push(rawLines[i]);
      }

      const body = textParts.join('\n').trim();
      if (body.length === 0) {
        autoIndex++;
        continue;
      }

      lines.push(new SubtitleLine(index, times[0], times[1], body));
      autoIndex = Math.max(autoIndex, index) + 1;
    }

    // 按开始时间排序（有的文件顺序是乱的）
    lines.sort((a: SubtitleLine, b: SubtitleLine): number => a.startMs - b.startMs);
    return lines;
  }

  /**
   * 解析 "00:00:01,000 --> 00:00:04,500"。
   * 返回 [开始, 结束]，失败返回 null。
   */
  private static parseTimeLine(line: string): number[] | null {
    const parts = line.split('-->');
    if (parts.length < 2) {
      return null;
    }

    const start = Timecode.parse(parts[0]);
    // 后半截可能带了位置信息（SRT 规范的 X1:Y1 X2:Y2），只取第一段
    const endText = parts[1].trim().split(/\s+/)[0];
    const end = Timecode.parse(endText);

    if (start < 0 || end < 0 || end < start) {
      return null;
    }

    return [start, end];
  }
}
