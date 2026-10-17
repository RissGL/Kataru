import { picker, fileIo } from '@kit.CoreFileKit';
import { media } from '@kit.MediaKit';
import { common } from '@kit.AbilityKit';
import { BusinessError } from '@kit.BasicServicesKit';
import { hilog } from '@kit.PerformanceAnalysisKit';
import { SubtitleLine, SrtParser } from '../model/SrtParser';
import { SubtitleSynchronizer } from '../model/SubtitleSynchronizer';
import { Timecode } from '../model/Timecode';

/**
 * KATARU 主界面（HarmonyOS 版 MVP）。
 *
 * 这一版做的是"能用的最小闭环"：选音频 + 选字幕 → 播放 → 大字幕跟随 →
 * 下面整篇阅读列表自动高亮并滚动。和桌面版共享同一套算法（SRT 解析 / 同步器）。
 *
 * 还没做的（下一步）：多语言双语轨道、媒体库扫描、锁屏播控（AVSession）、进度记忆。
 */
@Entry
@Component
struct Index {
  private static readonly TAG: string = 'KATARU';

  private player: media.AVPlayer | null = null;
  private synchronizer: SubtitleSynchronizer = new SubtitleSynchronizer();
  private tickTimer: number = -1;
  private scroller: Scroller = new Scroller();

  @State audioName: string = '';
  @State subtitleName: string = '';
  @State currentText: string = '';
  @State isPlaying: boolean = false;
  @State positionMs: number = 0;
  @State durationMs: number = 0;
  @State lines: SubtitleLine[] = [];
  @State currentIndex: number = -1;
  @State statusText: string = '先选一个音频文件，再选和它同名的 .srt 字幕';
  @State isLoading: boolean = false;

  aboutToDisappear(): void {
    this.stopTicker();
    this.releasePlayer();
  }

  build() {
    Column() {
      // ---------- 顶部：标题 + 文件 ----------
      Row() {
        Text('KATARU')
          .fontSize(20)
          .fontWeight(FontWeight.Bold)
          .fontColor($r('app.color.text_primary'))

        Blank()

        Text(this.durationMs > 0 ? Timecode.formatShort(this.positionMs) + ' / ' + Timecode.formatShort(this.durationMs) : '')
          .fontSize(12)
          .fontColor($r('app.color.text_secondary'))
      }
      .width('100%')
      .padding({ left: 20, right: 20, top: 16, bottom: 8 })

      // ---------- 当前字幕（大字） ----------
      Column() {
        Text(this.currentText.length > 0 ? this.currentText : '（这里显示当前这句）')
          .fontSize(26)
          .fontWeight(FontWeight.Medium)
          .lineHeight(40)
          .textAlign(TextAlign.Center)
          .fontColor(this.currentText.length > 0 ? $r('app.color.text_primary') : $r('app.color.text_secondary'))
          .maxLines(4)
          .textOverflow({ overflow: TextOverflow.Ellipsis })
      }
      .width('100%')
      .height(150)
      .justifyContent(FlexAlign.Center)
      .padding({ left: 16, right: 16 })

      // ---------- 进度 + 控制 ----------
      Column() {
        Slider({
          value: this.positionMs,
          min: 0,
          max: this.durationMs > 0 ? this.durationMs : 1,
          step: 200,
          style: SliderStyle.OutSet
        })
          .width('100%')
          .selectedColor($r('app.color.accent'))
          .onChange((value: number, mode: SliderChangeMode) => {
            if (mode === SliderChangeMode.End || mode === SliderChangeMode.Click) {
              this.seekTo(value);
            }
          })

        Row() {
          Button('−10s')
            .fontSize(13)
            .height(36)
            .backgroundColor($r('app.color.panel_background'))
            .fontColor($r('app.color.text_primary'))
            .onClick(() => this.skip(-10000))

          Button(this.isPlaying ? '暂停' : '播放')
            .fontSize(15)
            .height(36)
            .width(96)
            .backgroundColor($r('app.color.accent'))
            .fontColor(Color.White)
            .margin({ left: 12, right: 12 })
            .enabled(this.audioName.length > 0)
            .onClick(() => this.togglePlay())

          Button('+10s')
            .fontSize(13)
            .height(36)
            .backgroundColor($r('app.color.panel_background'))
            .fontColor($r('app.color.text_primary'))
            .onClick(() => this.skip(10000))

          Blank()

          Button('选音频')
            .fontSize(13)
            .height(36)
            .backgroundColor($r('app.color.panel_background'))
            .fontColor($r('app.color.gold'))
            .onClick(() => this.pickAudio())

          Button('选字幕')
            .fontSize(13)
            .height(36)
            .margin({ left: 8 })
            .backgroundColor($r('app.color.panel_background'))
            .fontColor($r('app.color.gold'))
            .onClick(() => this.pickSubtitle())
        }
        .width('100%')
        .margin({ top: 4 })
      }
      .width('100%')
      .padding({ left: 20, right: 20, top: 8, bottom: 8 })

      // ---------- 状态 / 文件名 ----------
      Text(this.statusText)
        .fontSize(11)
        .fontColor($r('app.color.text_secondary'))
        .width('100%')
        .padding({ left: 20, right: 20, bottom: 6 })
        .maxLines(2)
        .textOverflow({ overflow: TextOverflow.Ellipsis })

      // ---------- 阅读列表（自动高亮并滚动） ----------
      List({ space: 6, scroller: this.scroller }) {
        ForEach(this.lines, (line: SubtitleLine, index: number) => {
          ListItem() {
            Row() {
              Text(line.timeText())
                .fontSize(11)
                .width(52)
                .fontColor($r('app.color.text_secondary'))

              Text(line.text)
                .fontSize(16)
                .lineHeight(24)
                .layoutWeight(1)
                .fontColor(index === this.currentIndex ? $r('app.color.text_primary') : $r('app.color.text_secondary'))
                .fontWeight(index === this.currentIndex ? FontWeight.Medium : FontWeight.Normal)
            }
            .width('100%')
            .alignItems(VerticalAlign.Top)
            .padding({ left: 12, right: 12, top: 8, bottom: 8 })
            .backgroundColor(index === this.currentIndex ? '#22C9A227' : Color.Transparent)
            .borderRadius(8)
            .onClick(() => this.seekTo(line.startMs))
          }
        }, (line: SubtitleLine) => `${line.index}-${line.startMs}`)
      }
      .width('100%')
      .layoutWeight(1)
      .padding({ left: 8, right: 8, bottom: 12 })
      .scrollBar(BarState.Auto)
    }
    .width('100%')
    .height('100%')
    .backgroundColor($r('app.color.page_background'))
  }

  // ==================== 选文件 ====================

  private async pickAudio(): Promise<void> {
    try {
      const options = new picker.DocumentSelectOptions();
      options.maxSelectNumber = 1;
      options.fileSuffixFilters = ['音频|.mp3,.m4a,.m4b,.aac,.wav,.mp4,.flac,.ogg'];

      const documentPicker = new picker.DocumentViewPicker(this.getUIContext().getHostContext() as common.UIAbilityContext);
      const uris: string[] = await documentPicker.select(options);
      if (uris === null || uris.length === 0) {
        return;
      }

      await this.openAudio(uris[0]);
    } catch (error) {
      const err = error as BusinessError;
      hilog.error(0x0000, Index.TAG, 'pickAudio failed: %{public}s', JSON.stringify(err));
      this.statusText = `选音频失败：${err.message}`;
    }
  }

  private async pickSubtitle(): Promise<void> {
    try {
      const options = new picker.DocumentSelectOptions();
      options.maxSelectNumber = 1;
      options.fileSuffixFilters = ['字幕|.srt'];

      const documentPicker = new picker.DocumentViewPicker(this.getUIContext().getHostContext() as common.UIAbilityContext);
      const uris: string[] = await documentPicker.select(options);
      if (uris === null || uris.length === 0) {
        return;
      }

      this.loadSubtitle(uris[0]);
    } catch (error) {
      const err = error as BusinessError;
      this.statusText = `选字幕失败：${err.message}`;
    }
  }

  // ==================== 音频 ====================

  private async openAudio(uri: string): Promise<void> {
    this.isLoading = true;
    this.statusText = '正在打开音频…';

    try {
      await this.releasePlayer();

      const player = await media.createAVPlayer();
      this.player = player;

      player.on('stateChange', (state: string) => {
        hilog.info(0x0000, Index.TAG, 'state: %{public}s', state);
        if (state === 'initialized') {
          player.prepare();
        } else if (state === 'prepared') {
          this.durationMs = player.duration;
          this.isLoading = false;
          this.statusText = `已就绪：${this.durationMs > 0 ? Timecode.formatShort(this.durationMs) : '未知时长'}`;
        } else if (state === 'playing') {
          this.isPlaying = true;
          this.startTicker();
        } else if (state === 'paused' || state === 'completed') {
          this.isPlaying = false;
          if (state === 'completed') {
            this.stopTicker();
          }
        }
      });

      player.on('error', (error: BusinessError) => {
        this.isLoading = false;
        this.isPlaying = false;
        this.stopTicker();
        this.statusText = `播放失败：${error.message}（这个格式可能不被支持）`;
        hilog.error(0x0000, Index.TAG, 'player error: %{public}s', JSON.stringify(error));
      });

      // 用 fd 打开：对 picker 返回的 URI 最可靠
      const file = fileIo.openSync(uri, fileIo.OpenMode.READ_ONLY);
      const stat = fileIo.statSync(file.fd);

      this.audioName = uri;
      const slash = uri.lastIndexOf('/');
      if (slash >= 0) {
        this.audioName = decodeURIComponent(uri.substring(slash + 1));
      }

      player.fdSrc = { fd: file.fd, offset: 0, length: stat.size };
    } catch (error) {
      const err = error as BusinessError;
      this.isLoading = false;
      this.statusText = `打开音频失败：${err.message}`;
      hilog.error(0x0000, Index.TAG, 'openAudio failed: %{public}s', JSON.stringify(err));
    }
  }

  private async releasePlayer(): Promise<void> {
    this.stopTicker();
    const player = this.player;
    this.player = null;
    this.isPlaying = false;

    if (player === null) {
      return;
    }

    try {
      player.off('stateChange');
      player.off('error');
      await player.release();
    } catch (error) {
      hilog.warn(0x0000, Index.TAG, 'release failed: %{public}s', JSON.stringify(error));
    }
  }

  private togglePlay(): void {
    const player = this.player;
    if (player === null) {
      return;
    }

    if (this.isPlaying) {
      player.pause();
      this.isPlaying = false;
      this.stopTicker();
    } else {
      player.play();
      this.isPlaying = true;
      this.startTicker();
    }
  }

  private skip(deltaMs: number): void {
    this.seekTo(this.positionMs + deltaMs);
  }

  private seekTo(positionMs: number): void {
    const player = this.player;
    if (player === null) {
      return;
    }

    let target = Math.max(0, positionMs);
    if (this.durationMs > 0) {
      target = Math.min(target, this.durationMs);
    }

    try {
      player.seek(target, media.SeekMode.SEEK_CLOSEST);
      this.positionMs = target;
      this.synchronizer.seek(target);
      this.applyCurrentLine();
    } catch (error) {
      hilog.warn(0x0000, Index.TAG, 'seek failed: %{public}s', JSON.stringify(error));
    }
  }

  // ==================== 字幕 ====================

  private loadSubtitle(uri: string): void {
    try {
      const content = fileIo.readTextSync(uri);
      const warnings: string[] = [];
      const lines = SrtParser.parse(content, warnings);

      if (lines.length === 0) {
        this.statusText = '这个字幕文件里没有解析出任何一条，检查一下格式';
        return;
      }

      this.synchronizer.load(lines);
      this.lines = lines;
      this.currentIndex = -1;
      this.currentText = '';

      const slash = uri.lastIndexOf('/');
      this.subtitleName = slash >= 0 ? decodeURIComponent(uri.substring(slash + 1)) : uri;

      const warn = warnings.length > 0 ? `，${warnings.length} 条告警` : '';
      this.statusText = `字幕已载入：${lines.length} 条${warn}`;

      this.synchronizer.seek(this.positionMs);
      this.applyCurrentLine();
    } catch (error) {
      const err = error as BusinessError;
      this.statusText = `读字幕失败：${err.message}`;
      hilog.error(0x0000, Index.TAG, 'loadSubtitle failed: %{public}s', JSON.stringify(err));
    }
  }

  private applyCurrentLine(): void {
    const current = this.synchronizer.current;
    const text = current === null ? '' : current.text;

    if (this.currentText !== text) {
      this.currentText = text;
    }

    const index = this.synchronizer.currentIndex;
    if (this.currentIndex !== index) {
      this.currentIndex = index;

      // 自动滚动到当前行
      if (index >= 0) {
        this.scroller.scrollToIndex(index, true, ScrollAlign.CENTER);
      }
    }
  }

  // ==================== 计时器 ====================

  private startTicker(): void {
    if (this.tickTimer !== -1) {
      return;
    }

    this.tickTimer = setInterval(() => {
      const player = this.player;
      if (player === null) {
        return;
      }

      this.positionMs = player.currentTime;
      this.synchronizer.update(player.currentTime);
      this.applyCurrentLine();
    }, 100);
  }

  private stopTicker(): void {
    if (this.tickTimer !== -1) {
      clearInterval(this.tickTimer);
      this.tickTimer = -1;
    }
  }
}
