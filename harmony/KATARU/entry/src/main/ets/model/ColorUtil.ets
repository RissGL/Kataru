/**
 * 颜色工具：hex ↔ HSL。
 *
 * 桌面版的调色盘是完整的 HSV 取色器（ColorPickerPanel）。
 * 手机上没有现成的取色控件，这里用 H/S/L 三个滑杆做等价的自定义调色，
 * 这些函数负责在两种表示之间转换。
 */

export class Hsl {
  h: number = 0; // 0-360
  s: number = 0; // 0-100
  l: number = 0; // 0-100

  constructor(h: number, s: number, l: number) {
    this.h = h;
    this.s = s;
    this.l = l;
  }
}

export class ColorUtil {
  /** #RRGGBB / #AARRGGBB → HSL（忽略 alpha） */
  static hexToHsl(hex: string): Hsl {
    let value = hex.trim();
    if (value.startsWith('#')) {
      value = value.substring(1);
    }
    if (value.length === 8) {
      // 丢掉 alpha
      value = value.substring(2);
    }
    if (value.length !== 6) {
      return new Hsl(0, 0, 50);
    }

    const r = parseInt(value.substring(0, 2), 16) / 255;
    const g = parseInt(value.substring(2, 4), 16) / 255;
    const b = parseInt(value.substring(4, 6), 16) / 255;

    const max = Math.max(r, Math.max(g, b));
    const min = Math.min(r, Math.min(g, b));
    const l = (max + min) / 2;
    let h = 0;
    let s = 0;

    if (max !== min) {
      const d = max - min;
      s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
      if (max === r) {
        h = (g - b) / d + (g < b ? 6 : 0);
      } else if (max === g) {
        h = (b - r) / d + 2;
      } else {
        h = (r - g) / d + 4;
      }
      h = h / 6;
    }

    return new Hsl(Math.round(h * 360), Math.round(s * 100), Math.round(l * 100));
  }

  /** HSL → #RRGGBB */
  static hslToHex(h: number, s: number, l: number): string {
    const hh = ((h % 360) + 360) % 360 / 360;
    const ss = Math.min(100, Math.max(0, s)) / 100;
    const ll = Math.min(100, Math.max(0, l)) / 100;

    let r = ll;
    let g = ll;
    let b = ll;

    if (ss !== 0) {
      const q = ll < 0.5 ? ll * (1 + ss) : ll + ss - ll * ss;
      const p = 2 * ll - q;
      r = ColorUtil.hueToRgb(p, q, hh + 1 / 3);
      g = ColorUtil.hueToRgb(p, q, hh);
      b = ColorUtil.hueToRgb(p, q, hh - 1 / 3);
    }

    return '#' + ColorUtil.toHex(r) + ColorUtil.toHex(g) + ColorUtil.toHex(b);
  }

  private static hueToRgb(p: number, q: number, t: number): number {
    let tt = t;
    if (tt < 0) {
      tt += 1;
    }
    if (tt > 1) {
      tt -= 1;
    }
    if (tt < 1 / 6) {
      return p + (q - p) * 6 * tt;
    }
    if (tt < 1 / 2) {
      return q;
    }
    if (tt < 2 / 3) {
      return p + (q - p) * (2 / 3 - tt) * 6;
    }
    return p;
  }

  private static toHex(channel: number): string {
    const value = Math.round(Math.min(1, Math.max(0, channel)) * 255);
    const text = value.toString(16);
    return text.length === 1 ? '0' + text : text;
  }
}
