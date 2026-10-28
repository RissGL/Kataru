import { ColorUtil } from '../model/ColorUtil';

/**
 * HSV 调色盘（对应桌面版的 ColorPickerPanel）。
 *
 * 结构：
 *  - 上方：饱和度/明度方块（SV），横轴饱和度、纵轴明度
 *  - 下方：色相条（彩虹）
 *  - 右侧：当前颜色预览 + hex
 *
 * 用 Canvas 画渐变：ArkUI 的 CanvasRenderingContext2D 支持线性渐变，
 * 足够画出标准的 HSV 取色板，不需要第三方库。
 */
@Component
export struct ColorPlate {
  /** 当前色相 0-360 */
  @Prop hue: number = 0;
  /** 当前饱和度 0-100 */
  @Prop sat: number = 100;
  /** 当前明度 0-100 */
  @Prop val: number = 100;
  /** 文字/边框颜色，由外部主题决定 */
  @Prop labelColor: string = '#F1EBE1';
  @Prop frameColor: string = '#3A3330';
  /** 拖动或点击取色时回调 */
  onPick: (h: number, s: number, v: number) => void = () => {};

  private svSettings: RenderingContextSettings = new RenderingContextSettings(true);
  private svCtx: CanvasRenderingContext2D = new CanvasRenderingContext2D(this.svSettings);
  private hueSettings: RenderingContextSettings = new RenderingContextSettings(true);
  private hueCtx: CanvasRenderingContext2D = new CanvasRenderingContext2D(this.hueSettings);
  private svWidth: number = 0;
  private svHeight: number = 0;
  private hueWidth: number = 0;
  private hueHeight: number = 0;

  build() {
    Column() {
      Row() {
        Row()
          .width(34)
          .height(34)
          .borderRadius(17)
          .backgroundColor(ColorUtil.hslToHex(this.hue, this.sat, this.val / 2 + 25))
          .border({ width: 1, color: this.frameColor })

        Text(ColorUtil.hslToHex(this.hue, this.sat, this.val / 2 + 25))
          .fontSize(12)
          .fontColor(this.labelColor)
          .margin({ left: 10 })

        Blank()

        Text('饱和 ' + Math.round(this.sat) + ' · 明度 ' + Math.round(this.val))
          .fontSize(11)
          .fontColor(this.labelColor)
      }
      .width('100%')
      .alignItems(VerticalAlign.Center)
      .margin({ bottom: 6 })

      // 饱和度 / 明度方块
      Canvas(this.svCtx)
        .width('100%')
        .height(150)
        .borderRadius(8)
        .onReady(() => {
          this.svWidth = this.svCtx.width;
          this.svHeight = this.svCtx.height;
          this.drawSv();
        })
        .onTouch((event: TouchEvent) => {
          if (event.touches.length === 0) {
            return;
          }
          const x = event.touches[0].x;
          const y = event.touches[0].y;
          const s = Math.max(0, Math.min(100, (x / this.svWidth) * 100));
          const v = Math.max(0, Math.min(100, (1 - y / this.svHeight) * 100));
          this.onPick(this.hue, s, v);
        })

      // 色相条
      Canvas(this.hueCtx)
        .width('100%')
        .height(26)
        .borderRadius(13)
        .margin({ top: 10 })
        .onReady(() => {
          this.hueWidth = this.hueCtx.width;
          this.hueHeight = this.hueCtx.height;
          this.drawHue();
        })
        .onTouch((event: TouchEvent) => {
          if (event.touches.length === 0) {
            return;
          }
          const x = event.touches[0].x;
          const h = Math.max(0, Math.min(360, (x / this.hueWidth) * 360));
          this.onPick(h, this.sat, this.val);
        })
    }
    .width('100%')
    .padding(10)
    .borderRadius(10)
    .backgroundColor('#22000000')
    .border({ width: 1, color: this.frameColor })
  }

  /** 画 SV 方块：底色是当前色相，再叠白→透明、透明→黑 */
  private drawSv(): void {
    const ctx = this.svCtx;
    const w = this.svWidth;
    const h = this.svHeight;
    if (w <= 0 || h <= 0) {
      return;
    }

    ctx.clearRect(0, 0, w, h);

    // 纯色相底
    ctx.fillStyle = ColorUtil.hslToHex(this.hue, 100, 50);
    ctx.fillRect(0, 0, w, h);

    // 横向：白 → 透明
    const white = ctx.createLinearGradient(0, 0, w, 0);
    white.addColorStop(0, 'rgba(255,255,255,1)');
    white.addColorStop(1, 'rgba(255,255,255,0)');
    ctx.fillStyle = white;
    ctx.fillRect(0, 0, w, h);

    // 纵向：透明 → 黑
    const black = ctx.createLinearGradient(0, 0, 0, h);
    black.addColorStop(0, 'rgba(0,0,0,0)');
    black.addColorStop(1, 'rgba(0,0,0,1)');
    ctx.fillStyle = black;
    ctx.fillRect(0, 0, w, h);

    // 当前选点
    const cx = (this.sat / 100) * w;
    const cy = (1 - this.val / 100) * h;
    ctx.beginPath();
    ctx.arc(cx, cy, 7, 0, Math.PI * 2);
    ctx.strokeStyle = '#FFFFFF';
    ctx.lineWidth = 2;
    ctx.stroke();
  }

  /** 画色相彩虹条 */
  private drawHue(): void {
    const ctx = this.hueCtx;
    const w = this.hueWidth;
    const h = this.hueHeight;
    if (w <= 0 || h <= 0) {
      return;
    }

    const steps = 72;
    const stepWidth = w / steps;
    for (let i = 0; i < steps; i++) {
      const hue = (i / steps) * 360;
      ctx.fillStyle = ColorUtil.hslToHex(hue, 100, 50);
      ctx.fillRect(i * stepWidth, 0, stepWidth + 1, h);
    }

    // 当前位置指示
    const cx = (this.hue / 360) * w;
    ctx.beginPath();
    ctx.arc(cx, h / 2, 9, 0, Math.PI * 2);
    ctx.strokeStyle = '#FFFFFF';
    ctx.lineWidth = 2;
    ctx.stroke();
  }
}
