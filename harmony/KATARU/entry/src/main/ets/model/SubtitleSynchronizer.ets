import { SubtitleLine } from './SrtParser';

/**
 * 字幕同步器（从桌面版 Core/SubtitleSynchronizer.cs 移植）。
 *
 * 设计要点和桌面版一致：
 *  - 正常播放时用"游标往前走"的方式，复杂度 O(1)，不会每帧二分；
 *  - 用户拖动进度条 / 跳转时调用 seek()，用二分重新定位，避免长章节里逐条扫。
 */
export class SubtitleSynchronizer {
  private lines: SubtitleLine[] = [];
  private cursor: number = -1;

  /** 当前应该显示的字幕（不在任何区间内时为 null）。 */
  current: SubtitleLine | null = null;

  /** 装载一组字幕（必须已按开始时间排序）。 */
  load(lines: SubtitleLine[]): void {
    this.lines = lines;
    this.cursor = -1;
    this.current = null;
  }

  get count(): number {
    return this.lines.length;
  }

  get all(): SubtitleLine[] {
    return this.lines;
  }

  clear(): void {
    this.lines = [];
    this.cursor = -1;
    this.current = null;
  }

  /** 用户跳转：二分定位。 */
  seek(positionMs: number): void {
    this.cursor = this.findIndexAt(positionMs);
    this.updateCurrent(positionMs);
  }

  /**
   * 播放中的每帧调用。
   * @returns 当前字幕是否发生了变化（界面据此决定要不要刷新文本）
   */
  update(positionMs: number): boolean {
    const before = this.current;

    if (this.cursor < 0) {
      // 第一次调用：二分定位
      this.cursor = this.findIndexAt(positionMs);
    } else {
      // 游标往前走：正常情况下最多前进一两条
      while (this.cursor + 1 < this.lines.length && this.lines[this.cursor + 1].startMs <= positionMs) {
        this.cursor++;
      }

      // 往回走了（用户拖动过但没调 seek）：退回去
      while (this.cursor >= 0 && this.lines[this.cursor].startMs > positionMs) {
        this.cursor--;
      }
    }

    this.updateCurrent(positionMs);
    return before !== this.current;
  }

  /** 按开始时间二分：找最后一条 startMs <= position 的。 */
  private findIndexAt(positionMs: number): number {
    let low = 0;
    let high = this.lines.length - 1;
    let candidate = -1;

    while (low <= high) {
      const mid = low + Math.floor((high - low) / 2);
      if (this.lines[mid].startMs <= positionMs) {
        candidate = mid;
        low = mid + 1;
      } else {
        high = mid - 1;
      }
    }

    return candidate;
  }

  private updateCurrent(positionMs: number): void {
    if (this.cursor < 0 || this.cursor >= this.lines.length) {
      this.current = null;
      return;
    }

    const line = this.lines[this.cursor];
    // 超过结束时间就是空档期，不显示（和桌面版行为一致）
    this.current = positionMs < line.endMs ? line : null;
  }

  /** 当前字幕在列表里的下标（-1 表示没有）。 */
  get currentIndex(): number {
    return this.current === null ? -1 : this.cursor;
  }
}
