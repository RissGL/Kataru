import { fileIo, picker, fileShare, fileUri } from '@kit.CoreFileKit';
import { preferences } from '@kit.ArkData';
import { abilityAccessCtrl, common, Permissions } from '@kit.AbilityKit';
import { media } from '@kit.MediaKit';
import { BusinessError } from '@kit.BasicServicesKit';
import { hilog } from '@kit.PerformanceAnalysisKit';
import { util } from '@kit.ArkTS';
import { image } from '@kit.ImageKit';
import { backgroundTaskManager } from '@kit.BackgroundTasksKit';
import { wantAgent, Want } from '@kit.AbilityKit';
import type { WantAgent } from '@ohos.wantAgent';
import { Book, Chapter, LibraryScanner } from '../model/Library';
import { BookStore } from '../model/BookStore';
import { MediaSession } from '../model/MediaSession';
import { LiveSubtitle } from '../model/LiveSubtitle';
import { NowPlayingNotification } from '../model/NowPlayingNotification';
import { THEME_PRESETS, ThemeColors, ThemePreset, ThemeStore } from '../model/Theme';
import { AudioChapterMark, Mp4Chapters } from '../model/Mp4Chapters';
import { ColorUtil, Hsl } from '../model/ColorUtil';
import { ColorPlate } from '../controls/ColorPlate';
import { photoAccessHelper } from '@kit.MediaLibraryKit';
import { display } from '@kit.ArkUI';
import { http } from '@kit.NetworkKit';
import { request } from '@kit.BasicServicesKit';
import { SubtitleLine, SrtParser } from '../model/SrtParser';
import { SubtitleSynchronizer } from '../model/SubtitleSynchronizer';
import { Timecode } from '../model/Timecode';

/** 电脑端 /api/library 返回的一本书 */
class RemoteBook {
  index: number = 0;
  title: string = '';
  totalSize: number = 0;
  chapterCount: number = 0;

  constructor(index: number, title: string, totalSize: number, chapterCount: number) {
    this.index = index;
    this.title = title;
    this.totalSize = totalSize;
    this.chapterCount = chapterCount;
  }
}
/** 电脑端 /api/library 的响应结构（只要用到的那几个字段） */
class ShareChapter {
  index: number = 0;
  title: string = '';
  size: number = 0;
  hasSubtitle: boolean = false;
  audioName: string = '';
}

class ShareBookMeta {
  index: number = 0;
  title: string = '';
  totalSize: number = 0;
  hasCover: boolean = false;
  chapters: ShareChapter[] = [];
}

class ShareLibraryResponse {
  name: string = '';
  version: number = 0;
  books: ShareBookMeta[] = [];
}

const PREFS_NAME: string = 'kataru_settings';
const PREF_KEY_ROOT: string = 'library_uri';

/** 一个字幕颜色选项 */
class ColorChoice {
  name: string = '';
  value: string = '';

  constructor(name: string, value: string) {
    this.name = name;
    this.value = value;
  }
}

const SUB_COLORS: ColorChoice[] = [
  new ColorChoice('白', '#FFFFFF'),
  new ColorChoice('米白', '#F5EFE6'),
  new ColorChoice('金', '#C9A227'),
  new ColorChoice('暗红', '#A83B3B'),
  new ColorChoice('浅蓝', '#8FB8DE'),
  new ColorChoice('灰', '#B9B3A9'),
];

/** 一本书的封面（解码成 PixelMap，Image 才能画出来） */
class CoverItem {
  path: string = '';
  pixel: image.PixelMap | undefined = undefined;

  constructor(path: string, pixel: image.PixelMap) {
    this.path = path;
    this.pixel = pixel;
  }
}

/** 一个可调色的槽位 */
class ThemeSlot {
  key: string = '';
  label: string = '';

  constructor(key: string, label: string) {
    this.key = key;
    this.label = label;
  }
}

/** 内置背景渐变预设 */
class BackgroundGradient {
  name: string = '';
  from: string = '';
  to: string = '';

  constructor(name: string, from: string, to: string) {
    this.name = name;
    this.from = from;
    this.to = to;
  }
}

const BACKGROUND_GRADIENTS: BackgroundGradient[] = [
  new BackgroundGradient('深空', '#12141C', '#2A2233'),
  new BackgroundGradient('暖纸', '#F3EBDD', '#D9C7A8'),
  new BackgroundGradient('夜色', '#0B1020', '#1C3A5E'),
  new BackgroundGradient('苔青', '#101A16', '#26463A'),
  new BackgroundGradient('暗酒', '#1A1012', '#3E1C24'),
];

const THEME_SLOTS: ThemeSlot[] = [
  new ThemeSlot('bg', '背景'),
  new ThemeSlot('accent', '主色'),
  new ThemeSlot('gold', '金色'),
  new ThemeSlot('text', '文字'),
  new ThemeSlot('subtitle', '字幕色'),
  new ThemeSlot('outline', '描边色'),
];

/** 阅读页的字幕列表数据源（LazyForEach 用，避免一次性创建上万条 ListItem） */
class SubtitleDataSource implements IDataSource {
  private lines: SubtitleLine[] = [];
  private listeners: DataChangeListener[] = [];

  totalCount(): number {
    return this.lines.length;
  }

  getData(index: number): SubtitleLine {
    return this.lines[index];
  }

  registerDataChangeListener(listener: DataChangeListener): void {
    if (this.listeners.indexOf(listener) < 0) {
      this.listeners.push(listener);
    }
  }

  unregisterDataChangeListener(listener: DataChangeListener): void {
    const index = this.listeners.indexOf(listener);
    if (index >= 0) {
      this.listeners.splice(index, 1);
    }
  }

  /** 换字幕文件时整表刷新 */
  setLines(lines: SubtitleLine[]): void {
    this.lines = lines;
    for (const listener of this.listeners) {
      listener.onDataReloaded();
    }
  }

  /** 高亮行变化时只刷新那两行 */
  notifyChanged(index: number): void {
    if (index < 0 || index >= this.lines.length) {
      return;
    }
    for (const listener of this.listeners) {
      listener.onDataChange(index);
    }
  }
}

/** 自定义主题（用户调过的配色，存 preferences） */
class CustomTheme {
  pageBackground: string = '#121011';
  panelBackground: string = '#1A1718';
  textPrimary: string = '#F1EBE1';
  textSecondary: string = '#B5AFA6';
  accent: string = '#9E2B25';
  gold: string = '#C9A227';
  borderSoft: string = '#3A3330';
}

/** 可选字体（不存在时 ArkUI 会自动回退到系统字体） */
class FontChoice {  name: string = '';
  value: string = '';

  constructor(name: string, value: string) {
    this.name = name;
    this.value = value;
  }
}

/**
 * KATARU · HarmonyOS 主界面
 *
 * 四个模式对齐桌面版：书库 / 播放 / 阅读 / 设置。
 */
@Entry
@Component
struct Index {
  private static readonly TAG: string = 'KATARU';

  private player: media.AVPlayer | null = null;
  private synchronizer: SubtitleSynchronizer = new SubtitleSynchronizer();
  private secondarySynchronizer: SubtitleSynchronizer = new SubtitleSynchronizer();
  private tickTimer: number = -1;
  private readingScroller: Scroller = new Scroller();
  /** 阅读页的大列表用 LazyForEach（否则 1 万条字幕会卡死） */
  private lineSource: SubtitleDataSource = new SubtitleDataSource();
  private lastProgressSaveMs: number = 0;
  private currentAudioFile: fileIo.File | null = null;
  /**
   * 数据源描述符必须**长期持有**。
   * 之前只在局部变量里，GC 之后原生线程回调会拿到 NULL 函数句柄 → SIGSEGV（10 次 cppcrash）。
   */
  private keepAliveDataSource: media.AVDataSrcDescriptor | null = null;
  /** .m4b 降级只做一次，避免死循环 */
  private m4bFallbackDone: boolean = false;
  /** 切章去抖：这个时间点之前忽略新的切章请求（连点不再并发建播放器） */
  private chapterSwitchLockUntil: number = 0;
  /** 切章序号：每次切章 +1，异步回来后发现序号变了就丢弃（防串台） */
  private chapterOpenSeq: number = 0;
  /** seek 去抖：连点进度条/章节胶囊不再连发 */
  private seekLockUntil: number = 0;
  /** 设置落盘去抖：拖滑杆时不要每次 flush 到磁盘 */
  private saveSettingsTimer: number = -1;
  /** m4b 章节表缓存（同一份文件切章不必重复读 8MB moov） */
  private chapterCachePath: string = '';
  private chapterCacheMarks: AudioChapterMark[] = [];
  /** 字幕缓存（多条目，来回切章不用反复解析） */
  private subtitleCache: Map<string, SubtitleLine[]> = new Map<string, SubtitleLine[]>();
  /** 封面显示方式：false = 完整显示（Contain，不裁切），true = 裁切填充（Cover） */
  private backgroundTaskActive: boolean = false;
  private backgroundWantAgent: WantAgent | null = null;

  private mediaSession: MediaSession = new MediaSession();
  private liveSubtitle: LiveSubtitle = new LiveSubtitle();
  private nowPlaying: NowPlayingNotification = new NowPlayingNotification();
  @State liveViewAvailable: boolean = false;
  @State themeIndex: number = 0;
  @State theme: ThemeColors = THEME_PRESETS[0].colors;
  @State searchText: string = '';
  @State showFavoritesOnly: boolean = false;
  @State needsReauth: boolean = false;
  @State syncing: boolean = false;
  @State syncProgress: string = '';
  @State embeddedChapters: AudioChapterMark[] = [];
  @State embeddedIndex: number = -1;
  @State resumeLast: boolean = true;
  @State showOnlyWithSubtitle: boolean = false;
  @State skipSeconds: number = 10;
  @State subtitleOutlineWidth: number = 2;
  @State subtitleOutlineColor: string = '#000000';
  @State secondaryScale: number = 0.68;
  /** 当前展开调色的槽位：'' | 'subtitle' | 'outline' | 'bg' | 'accent' | 'gold' | 'text' */
  @State editingSlot: string = '';
  @State editH: number = 0;
  @State editS: number = 50;
  @State editL: number = 50;
  /** 调色盘的 HSV（和 HSL 分开，避免互相转换误差） */
  @State pickH: number = 0;
  @State pickS: number = 100;
  @State pickV: number = 100;
  /** 字重档位：300/400/500/700/900 */
  @State fontWeights: number[] = [300, 400, 500, 700, 900];
  @State favorites: string[] = [];

  // ---------- 媒体库 ----------
  @State books: Book[] = [];
  @State selectedBook: Book | null = null;
  @State selectedChapter: Chapter | null = null;
  @State libraryRoot: string = '';
  @State scanning: boolean = false;
  @State statusText: string = '点右上角「刷新书库」扫描 Download/KATARU Book';

  // ---------- 播放 ----------
  @State isPlaying: boolean = false;
  @State positionMs: number = 0;
  @State durationMs: number = 0;
  @State currentText: string = '';
  @State secondaryText: string = '';
  @State currentTextTranslated: boolean = false;
  @State lines: SubtitleLine[] = [];
  @State secondaryLines: SubtitleLine[] = [];
  @State currentIndex: number = -1;
  @State covers: CoverItem[] = [];

  // ---------- 播放设置 ----------
  @State volume: number = 1;
  @State autoNext: boolean = true;
  @State showSecondary: boolean = true;

  // ---------- 界面 ----------
  @State currentTab: number = 0;
  @State fontSize: number = 26;
  @State fontFamilyValue: string = 'HarmonyOS Sans SC';
  @State subtitleColor: string = '#FFFFFF';
  @State subtitleBold: boolean = false;
  @State subtitleWeight: number = 400;
  /** 通知栏/锁屏显示字幕（需要通知权限，默认关） */
  @State showNotification: boolean = false;
  /** 界面缩放 0.8–1.4（对应桌面版三处 zoom） */
  @State uiScale: number = 1;
  @State coverFill: boolean = false;
  /** 界面文字是否跟随字幕字体（阅读页/书名/标题） */
  @State uiFontFollow: boolean = true;
  /** 自定义背景图 */
  @State backgroundPath: string = '';
  @State backgroundPixel: image.PixelMap | null = null;
  @State backgroundDim: number = 40;
  @State backgroundBlur: number = 14;
  /** 睡眠定时：剩余分钟（0 = 关闭） */
  @State sleepMinutes: number = 0;
  private sleepDeadline: number = 0;
  /** 阅读页行高 */
  @State readingLineHeight: number = 24;
  /** 阅读页段间距 */
  @State readingParagraphGap: number = 4;
  /** 背景渐变预设下标（-1 = 不用渐变） */
  @State backgroundGradient: number = -1;
  /** 设置页当前分组：0 外观 1 字幕 2 播放 3 其他 */
  @State settingsGroup: number = 0;
  /** 书架布局：列数(2/3/4)、封面圆角、是否显示卡片进度条 */
  @State libraryColumns: number = 3;
  @State bookRadius: number = 6;
  @State showCardProgress: boolean = true;
  /** 阅读页独立字号（默认跟字幕字号分开调） */
  @State readingFontSize: number = 16;
  /** 阅读页译文（双语小字）字号 */
  @State readingSecondarySize: number = 13;
  /** 首页「继续收听」卡片是否显示 */
  @State showResumeCard: boolean = true;
  /** 局域网拉书：服务器地址、连接状态、远端书列表、下载进度 */
  @State shareUrl: string = 'http://192.168.43.1:8765';
  @State shareStatus: string = '';
  @State shareBooks: RemoteBook[] = [];
  @State shareBusy: boolean = false;
  @State fontChoices: FontChoice[] = [
    new FontChoice('系统默认', 'HarmonyOS Sans SC'),
    new FontChoice('黑体', 'HarmonyOS Sans'),
    new FontChoice('思源宋体', 'KataruSerif'),
    new FontChoice('衬线', 'serif'),
    new FontChoice('等宽', 'monospace'),
  ];

  aboutToAppear(): void {
    this.registerBundledFonts();

    // 主题：预设 → 用户自定义过的配色优先
    const themeContext = this.getUIContext().getHostContext() as common.UIAbilityContext;
    this.themeIndex = ThemeStore.load(themeContext);
    this.theme = THEME_PRESETS[this.themeIndex].colors;
    this.loadCustomTheme();

    this.loadSettings();
    this.loadFavorites();

    // 打开本地书库（App 私有目录）：不依赖任何权限，重启/重装都不会丢。
    // 「书库文件夹」只是同步源，书架空的时候选一次即可。
    this.openLocalLibrary();

    // 实况窗：先准备 clickAction（liveView 必填），再探测能力
    const uiContext = this.getUIContext().getHostContext() as common.UIAbilityContext;
    this.liveSubtitle.setupIcon(uiContext).then(() => {
      return this.liveSubtitle.setupClickAction(uiContext);
    }).then(() => {
      return this.liveSubtitle.isEnabled();
    }).then((ok: boolean) => {
      this.liveViewAvailable = ok;
    });
  }

  // ==================== 自定义调色 ====================

  /** 取某个槽位当前的颜色 */
  private slotColor(slot: string): string {
    if (slot === 'subtitle') {
      return this.subtitleColor;
    }
    if (slot === 'outline') {
      return this.subtitleOutlineColor;
    }
    if (slot === 'bg') {
      return this.theme.pageBackground;
    }
    if (slot === 'accent') {
      return this.theme.accent;
    }
    if (slot === 'gold') {
      return this.theme.gold;
    }
    return this.theme.textPrimary;
  }

  /** 解码背景图（沙箱路径 Image 无法直接加载，要转 PixelMap） */
  private async loadBackgroundPixel(): Promise<void> {
    if (this.backgroundPath.length === 0) {
      this.backgroundPixel = null;
      return;
    }
    try {
      const file = fileIo.openSync(this.backgroundPath, fileIo.OpenMode.READ_ONLY);
      const source = image.createImageSource(file.fd);
      const info = await source.getImageInfo();
      const screenWidth = display.getDefaultDisplaySync().width;
      const options: image.DecodingOptions = {
        desiredSize: {
          width: Math.round(screenWidth),
          height: Math.round(screenWidth * info.size.height / info.size.width)
        }
      };
      this.backgroundPixel = await source.createPixelMap(options);
      source.release();
      fileIo.closeSync(file);
      hilog.info(0x0000, Index.TAG, 'background loaded');
    } catch (error) {
      hilog.warn(0x0000, Index.TAG, 'background load failed: %{public}s', JSON.stringify(error));
      this.backgroundPixel = null;
    }
  }

  /** 选一张图当背景（拷进沙箱） */
  private async chooseBackgroundImage(): Promise<void> {
    try {
      const context = this.getUIContext().getHostContext() as common.UIAbilityContext;
      const options: photoAccessHelper.PhotoSelectOptions = new photoAccessHelper.PhotoSelectOptions();
      options.MIMEType = photoAccessHelper.PhotoViewMIMETypes.IMAGE_TYPE;
      options.maxSelectNumber = 1;
      const picker: photoAccessHelper.PhotoViewPicker = new photoAccessHelper.PhotoViewPicker();
      const result: photoAccessHelper.PhotoSelectResult = await picker.select(options);
      if (result.photoUris.length === 0) {
        return;
      }

      const target = context.filesDir + '/background_' + Date.now() + '.img';
      const source = fileIo.openSync(result.photoUris[0], fileIo.OpenMode.READ_ONLY);
      fileIo.copyFileSync(source.fd, target);
      fileIo.closeSync(source);

      if (this.backgroundPath.length > 0) {
        try {
          fileIo.unlinkSync(this.backgroundPath);
        } catch (error) {
          hilog.warn(0x0000, Index.TAG, 'remove old background failed');
        }
      }

      this.backgroundPath = target;
      this.saveSettings();
      await this.loadBackgroundPixel();
      this.statusText = '背景已更新';
    } catch (error) {
      const err = error as BusinessError;
      this.statusText = '选背景失败：' + err.message;
    }
  }

  /** 清除背景 */
  private clearBackgroundImage(): void {
    if (this.backgroundPath.length > 0) {
      try {
        fileIo.unlinkSync(this.backgroundPath);
      } catch (error) {
        hilog.warn(0x0000, Index.TAG, 'unlink background failed');
      }
    }
    this.backgroundPath = '';
    this.backgroundPixel = null;
    this.saveSettings();
    this.statusText = '背景已清除';
  }

  /** 设定睡眠定时（分钟，0 = 关闭） */
  private setSleepTimer(minutes: number): void {
    this.sleepMinutes = minutes;
    if (minutes <= 0) {
      this.sleepDeadline = 0;
      this.statusText = '睡眠定时已关闭';
      return;
    }
    this.sleepDeadline = Date.now() + minutes * 60 * 1000;
    this.statusText = `睡前定时：${minutes} 分钟后暂停`;
  }

  /** 每帧检查睡眠定时是否到点 */
  private checkSleepTimer(): void {
    if (this.sleepDeadline <= 0) {
      return;
    }
    const remaining = this.sleepDeadline - Date.now();
    if (remaining > 0) {
      this.sleepMinutes = Math.ceil(remaining / 60000);
      return;
    }
    this.sleepDeadline = 0;
    this.sleepMinutes = 0;
    this.statusText = '睡眠定时到点，已暂停';
    this.togglePlay();
  }

  /** 调色盘取色：HSV → 十六进制，并写回对应色位 */
  private applyHsvColor(): void {
    const hex = ColorUtil.hsvToHex(this.pickH, this.pickS, this.pickV);
    const slot = this.editingSlot;

    if (slot === 'subtitle') {
      this.subtitleColor = hex;
    } else if (slot === 'outline') {
      this.subtitleOutlineColor = hex;
    } else if (slot === 'bg') {
      this.theme.pageBackground = hex;
      const panel = ColorUtil.hexToHsl(hex);
      this.theme.panelBackground = ColorUtil.hslToHex(panel.h, panel.s, Math.min(100, panel.l + 5));
      this.persistTheme();
    } else if (slot === 'accent') {
      this.theme.accent = hex;
      this.persistTheme();
    } else if (slot === 'gold') {
      this.theme.gold = hex;
      this.persistTheme();
    } else if (slot === 'text') {
      this.theme.textPrimary = hex;
      const textHsl = ColorUtil.hexToHsl(hex);
      this.theme.textSecondary = ColorUtil.hslToHex(textHsl.h, Math.max(0, textHsl.s - 10),
        Math.min(100, textHsl.l - 20));
      this.persistTheme();
    }

    this.saveSettings();
  }

  /** 展开/收起某个色位的调色盘 */
  private toggleSlot(slot: string): void {
    if (this.editingSlot === slot) {
      this.editingSlot = '';
      return;
    }
    const hsl = ColorUtil.hexToHsl(this.slotColor(slot));
    this.editH = hsl.h;
    this.editS = hsl.s;
    this.editL = hsl.l;

    // 同步给调色盘（HSV）
    const hsv = ColorUtil.hslToHsv(hsl.h, hsl.s, hsl.l);
    this.pickH = hsv.h;
    this.pickS = hsv.s;
    this.pickV = hsv.l;

    this.editingSlot = slot;
  }

  /** 滑杆变化：写回颜色并持久化 */
  private applySlotColor(): void {
    const hex = ColorUtil.hslToHex(this.editH, this.editS, this.editL);
    const slot = this.editingSlot;

    if (slot === 'subtitle') {
      this.subtitleColor = hex;
    } else if (slot === 'outline') {
      this.subtitleOutlineColor = hex;
    } else if (slot === 'bg') {
      this.theme.pageBackground = hex;
      this.theme.panelBackground = ColorUtil.hslToHex(this.editH, this.editS, Math.min(100, this.editL + 5));
      this.persistTheme();
    } else if (slot === 'accent') {
      this.theme.accent = hex;
      this.persistTheme();
    } else if (slot === 'gold') {
      this.theme.gold = hex;
      this.persistTheme();
    } else if (slot === 'text') {
      this.theme.textPrimary = hex;
      this.theme.textSecondary = ColorUtil.hslToHex(this.editH, Math.max(0, this.editS - 10), Math.min(100, this.editL - 20));
      this.persistTheme();
    }

    this.saveSettings();
  }

  /** 把当前主题（可能是自定义过的）存下来 */
  private persistTheme(): void {
    try {
      const context = this.getUIContext().getHostContext() as common.UIAbilityContext;
      const store = preferences.getPreferencesSync(context, { name: 'kataru_settings' });
      const custom = new CustomTheme();
      custom.pageBackground = this.theme.pageBackground;
      custom.panelBackground = this.theme.panelBackground;
      custom.textPrimary = this.theme.textPrimary;
      custom.textSecondary = this.theme.textSecondary;
      custom.accent = this.theme.accent;
      custom.gold = this.theme.gold;
      custom.borderSoft = this.theme.borderSoft;
      store.putSync('custom_theme', JSON.stringify(custom));
      // 标记"用户确实自定义过"：只有为 true 时启动才应用自定义，
      // 否则旧的 custom_theme 会把用户选的预设覆盖掉
      store.putSync('theme_custom_active', true);
      store.flush();
    } catch (error) {
      hilog.warn(0x0000, Index.TAG, 'persist theme failed: %{public}s', JSON.stringify(error));
    }
  }

  /** 清掉自定义配色覆盖 */
  private clearCustomTheme(): void {
    try {
      const context = this.getUIContext().getHostContext() as common.UIAbilityContext;
      const store = preferences.getPreferencesSync(context, { name: 'kataru_settings' });
      store.putSync('custom_theme', '');
      store.putSync('theme_custom_active', false);
      store.flush();
      hilog.info(0x0000, Index.TAG, 'custom theme cleared');
    } catch (error) {
      hilog.warn(0x0000, Index.TAG, 'clear custom theme failed: %{public}s', JSON.stringify(error));
    }
  }

  /** 切到某个预设主题（同时清掉自定义覆盖） */
  private applyThemePreset(index: number): void {
    if (index < 0 || index >= THEME_PRESETS.length) {
      return;
    }
    this.themeIndex = index;
    this.theme = THEME_PRESETS[index].colors;
    const context = this.getUIContext().getHostContext() as common.UIAbilityContext;
    ThemeStore.save(context, index);
    this.clearCustomTheme();
  }

  /**
   * 背景与主题的对比度联动：
   * 选浅色渐变（暖纸）时自动换浅色主题，选深色渐变时换回深色主题，
   * 否则白字压在米色背景上根本看不清。
   */
  private syncThemeWithBackground(gradientIndex: number): void {
    const LIGHT_GRADIENT = 1;   // 暖纸
    if (gradientIndex === LIGHT_GRADIENT && this.themeIndex === 0) {
      this.applyThemePreset(1);
      this.statusText = '已切换到浅色主题，保证文字可读';
    } else if (gradientIndex >= 0 && gradientIndex !== LIGHT_GRADIENT && this.themeIndex === 1) {
      this.applyThemePreset(0);
      this.statusText = '已切换到深色主题';
    }
  }

  /** 恢复默认配色（清除自定义并回到墨黑） */
  private resetThemeToDefault(): void {
    this.clearCustomTheme();
    this.themeIndex = 0;
    this.theme = THEME_PRESETS[0].colors;
    const context = this.getUIContext().getHostContext() as common.UIAbilityContext;
    ThemeStore.save(context, 0);
    this.statusText = '配色已恢复默认';
  }

  /** 读回自定义主题（只有上次是自定义状态才应用，否则保持预设） */
  private loadCustomTheme(): boolean {
    try {
      const context = this.getUIContext().getHostContext() as common.UIAbilityContext;
      const store = preferences.getPreferencesSync(context, { name: 'kataru_settings' });

      const active = store.getSync('theme_custom_active', false) as boolean;
      if (!active) {
        hilog.info(0x0000, Index.TAG, 'custom theme inactive, keep preset');
        return false;
      }

      const raw = store.getSync('custom_theme', '') as string;
      if (raw.length === 0) {
        return false;
      }
      const parsed = JSON.parse(raw) as CustomTheme;
      if (parsed === null) {
        return false;
      }
      this.theme = new ThemeColors(
        parsed.pageBackground, parsed.panelBackground, parsed.textPrimary,
        parsed.textSecondary, parsed.accent, parsed.gold, parsed.borderSoft
      );
      return true;
    } catch (error) {
      return false;
    }
  }

  /** 字重档位的中文名 */
  private weightLabel(weight: number): string {
    if (weight <= 300) {
      return '细';
    }
    if (weight <= 400) {
      return '常规';
    }
    if (weight <= 500) {
      return '中';
    }
    if (weight <= 700) {
      return '粗';
    }
    return '特粗';
  }
  // ==================== 设置持久化 ====================

  private loadSettings(): void {
    try {
      const context = this.getUIContext().getHostContext() as common.UIAbilityContext;
      const store = preferences.getPreferencesSync(context, { name: 'kataru_settings' });

      this.fontSize = store.getSync('fontSize', 26) as number;
      this.fontFamilyValue = store.getSync('fontFamily', 'HarmonyOS Sans SC') as string;
      this.subtitleColor = store.getSync('subtitleColor', '#FFFFFF') as string;
      this.subtitleBold = store.getSync('subtitleBold', false) as boolean;
      this.subtitleWeight = store.getSync('subtitleWeight', 400) as number;
      this.showNotification = store.getSync('showNotification', false) as boolean;
      this.uiScale = store.getSync('uiScale', 1) as number;
      this.coverFill = store.getSync('coverFill', false) as boolean;
      this.uiFontFollow = store.getSync('uiFontFollow', true) as boolean;
      this.backgroundPath = store.getSync('backgroundPath', '') as string;
      this.backgroundDim = store.getSync('backgroundDim', 40) as number;
      this.backgroundBlur = store.getSync('backgroundBlur', 14) as number;
      this.readingLineHeight = store.getSync('readingLineHeight', 24) as number;
      this.readingParagraphGap = store.getSync('readingParagraphGap', 4) as number;
      this.readingFontSize = store.getSync('readingFontSize', 16) as number;
      this.readingSecondarySize = store.getSync('readingSecondarySize', 13) as number;
      this.showResumeCard = store.getSync('showResumeCard', true) as boolean;
      this.libraryColumns = store.getSync('libraryColumns', 3) as number;
      this.bookRadius = store.getSync('bookRadius', 6) as number;
      this.showCardProgress = store.getSync('showCardProgress', true) as boolean;
      this.backgroundGradient = store.getSync('backgroundGradient', -1) as number;
      if (this.backgroundPath.length > 0) {
        this.loadBackgroundPixel();
      }
      // 启动时校正一次对比度：浅色渐变配深色主题会看不清
      if (this.backgroundPath.length === 0) {
        if (this.backgroundGradient === 1 && this.themeIndex === 0) {
          this.applyThemePreset(1);
        } else if (this.backgroundGradient >= 0 && this.backgroundGradient !== 1 && this.themeIndex === 1) {
          this.applyThemePreset(0);
        }
      }
      this.showSecondary = store.getSync('showSecondary', true) as boolean;
      this.autoNext = store.getSync('autoNext', true) as boolean;
      this.volume = store.getSync('volume', 1) as number;
      this.resumeLast = store.getSync('resumeLast', true) as boolean;
      this.skipSeconds = store.getSync('skipSeconds', 10) as number;
      this.subtitleOutlineWidth = store.getSync('subtitleOutlineWidth', 2) as number;
      this.subtitleOutlineColor = store.getSync('subtitleOutlineColor', '#000000') as string;
      this.secondaryScale = store.getSync('secondaryScale', 0.68) as number;
      this.showOnlyWithSubtitle = store.getSync('showOnlyWithSubtitle', false) as boolean;
      hilog.info(0x0000, Index.TAG, 'settings loaded: fontSize=%{public}d font=%{public}s',
        this.fontSize, this.fontFamilyValue);
    } catch (error) {
      hilog.warn(0x0000, Index.TAG, 'load settings failed: %{public}s', JSON.stringify(error));
    }
  }

  /** 去抖保存：拖动滑杆连续触发，停手 400ms 后真正写盘 */
  private saveSettingsSoon(): void {
    if (this.saveSettingsTimer >= 0) {
      clearTimeout(this.saveSettingsTimer);
    }
    this.saveSettingsTimer = setTimeout(() => {
      this.saveSettingsTimer = -1;
      this.saveSettings();
    }, 400);
  }

  private saveSettings(): void {
    try {
      const context = this.getUIContext().getHostContext() as common.UIAbilityContext;
      const store = preferences.getPreferencesSync(context, { name: 'kataru_settings' });
      store.putSync('fontSize', this.fontSize);
      store.putSync('fontFamily', this.fontFamilyValue);
      store.putSync('subtitleColor', this.subtitleColor);
      store.putSync('subtitleBold', this.subtitleBold);
      store.putSync('subtitleWeight', this.subtitleWeight);
      store.putSync('showNotification', this.showNotification);
      store.putSync('uiScale', this.uiScale);
      store.putSync('coverFill', this.coverFill);
      store.putSync('uiFontFollow', this.uiFontFollow);
      store.putSync('backgroundPath', this.backgroundPath);
      store.putSync('backgroundDim', this.backgroundDim);
      store.putSync('backgroundBlur', this.backgroundBlur);
      store.putSync('readingLineHeight', this.readingLineHeight);
      store.putSync('readingParagraphGap', this.readingParagraphGap);
      store.putSync('readingFontSize', this.readingFontSize);
      store.putSync('readingSecondarySize', this.readingSecondarySize);
      store.putSync('showResumeCard', this.showResumeCard);
      store.putSync('libraryColumns', this.libraryColumns);
      store.putSync('bookRadius', this.bookRadius);
      store.putSync('showCardProgress', this.showCardProgress);
      store.putSync('backgroundGradient', this.backgroundGradient);
      store.putSync('showSecondary', this.showSecondary);
      store.putSync('autoNext', this.autoNext);
      store.putSync('volume', this.volume);
      store.putSync('resumeLast', this.resumeLast);
      store.putSync('skipSeconds', this.skipSeconds);
      store.putSync('subtitleOutlineWidth', this.subtitleOutlineWidth);
      store.putSync('subtitleOutlineColor', this.subtitleOutlineColor);
      store.putSync('secondaryScale', this.secondaryScale);
      store.putSync('showOnlyWithSubtitle', this.showOnlyWithSubtitle);
      store.flush();
    } catch (error) {
      hilog.warn(0x0000, Index.TAG, 'save settings failed: %{public}s', JSON.stringify(error));
    }
  }

  /**
   * 注册内置字体：思源宋体 Source Han Serif SC。
   *
   * 文件在 resources/rawfile/fonts/ 下。注册成 fontFamily 之后，
   * Text(...).fontFamily('KataruSerif') 就能用；加粗用单独的 Bold 字族
   * （自定义字体没有字重变体，靠 fontWeight 不会变粗）。
   */
  private registerBundledFonts(): void {
    try {
      const fontManager = this.getUIContext().getFont();
      fontManager.registerFont({
        familyName: 'KataruSerif',
        familySrc: $rawfile('fonts/SourceHanSerifSC-Regular.otf')
      });
      fontManager.registerFont({
        familyName: 'KataruSerifBold',
        familySrc: $rawfile('fonts/SourceHanSerifSC-Bold.otf')
      });
      hilog.info(0x0000, Index.TAG, 'bundled fonts registered (Source Han Serif SC)');
    } catch (error) {
      hilog.error(0x0000, Index.TAG, 'registerFont failed: %{public}s', JSON.stringify(error));
    }
  }

  /**
   * 界面文字用的字族。
   * 开启「界面跟随字幕字体」后，阅读页正文、书名、播放页标题都会用同一个字体，
   * 这样选了思源宋体就是整本书的排版风格，而不是只有大字幕变了。
   */
  private uiFont(): string {
    return this.uiFontFollow ? this.fontFamilyValue : 'HarmonyOS Sans SC';
  }

  /** 按当前设置挑字族：思源宋体 + 加粗时用内置 Bold 字族 */
  private subtitleFamily(): string {
    if (this.subtitleBold && this.fontFamilyValue === 'KataruSerif') {
      return 'KataruSerifBold';
    }
    return this.fontFamilyValue;
  }

  /**
   * 统一的书库刷新：**整表重建**，不是往里追加。
   *
   * 之前是"扫到就 push"，所以用户把书移动/删除之后，书架里会残留已经不存在的条目。
   * 现在每次都从磁盘重新扫，扫到什么就是什么，并且把指向已消失书本的选中状态清掉。
   */
  private refreshLibrary(): void {
    const books = LibraryScanner.scan(this.libraryRoot);
    this.books = books;
    this.rebuildCovers();
    this.pruneSelection();

    this.statusText = books.length > 0
      ? ('书库：' + books.length + ' 本')
      : '还没选书库文件夹 —— 去设置页点「选择书库文件夹」';
    hilog.info(0x0000, Index.TAG, 'refresh: %{public}d book(s) at %{public}s',
      books.length, this.libraryRoot);
  }

  // ==================== 收藏 / 搜索 ====================

  private favoriteKey(book: Book): string {
    return book.folderPath.length > 0 ? book.folderPath : book.title;
  }

  private isFavorite(book: Book): boolean {
    const key = this.favoriteKey(book);
    for (const item of this.favorites) {
      if (item === key) {
        return true;
      }
    }
    return false;
  }

  private toggleFavorite(book: Book): void {
    const key = this.favoriteKey(book);
    const rest: string[] = [];
    let removed = false;
    for (const item of this.favorites) {
      if (item === key) {
        removed = true;
      } else {
        rest.push(item);
      }
    }

    if (!removed) {
      rest.push(key);
    }

    this.favorites = rest;
    this.saveFavorites();
    hilog.info(0x0000, Index.TAG, 'favorite toggled: %{public}s -> %{public}s',
      key, removed ? 'off' : 'on');
  }

  private saveFavorites(): void {
    try {
      const context = this.getUIContext().getHostContext() as common.UIAbilityContext;
      const store = preferences.getPreferencesSync(context, { name: 'kataru_settings' });
      store.putSync('favorites', JSON.stringify(this.favorites));
      store.flush();
    } catch (error) {
      hilog.warn(0x0000, Index.TAG, 'save favorites failed: %{public}s', JSON.stringify(error));
    }
  }

  private loadFavorites(): void {
    try {
      const context = this.getUIContext().getHostContext() as common.UIAbilityContext;
      const store = preferences.getPreferencesSync(context, { name: 'kataru_settings' });
      const raw = store.getSync('favorites', '[]') as string;
      const parsed = JSON.parse(raw) as string[];
      if (parsed !== null && parsed !== undefined) {
        this.favorites = parsed;
      }
    } catch (error) {
      this.favorites = [];
    }
  }

  /** 搜索 + 收藏过滤后的书（收藏的排前面） */
  private filteredBooks(): Book[] {
    const keyword = this.searchText.trim().toLowerCase();
    const favorite: Book[] = [];
    const normal: Book[] = [];

    for (const book of this.books) {
      if (this.showFavoritesOnly && !this.isFavorite(book)) {
        continue;
      }
      if (keyword.length > 0 && book.title.toLowerCase().indexOf(keyword) < 0) {
        continue;
      }
      if (this.isFavorite(book)) {
        favorite.push(book);
      } else {
        normal.push(book);
      }
    }

    const result: Book[] = [];
    for (const book of favorite) {
      result.push(book);
    }
    for (const book of normal) {
      result.push(book);
    }
    return result;
  }
  /** 书/章被移动或删除后，清掉已经失效的选中状态，避免点到不存在的文件 */
  private pruneSelection(): void {
    const book = this.selectedBook;
    if (book === null) {
      return;
    }

    let found = false;
    for (const item of this.books) {
      if (item.folderPath === book.folderPath && item.title === book.title) {
        found = true;
        break;
      }
    }

    if (found) {
      return;
    }

    hilog.info(0x0000, Index.TAG, 'prune: book gone, clearing selection');
    this.stopTicker();
    this.releasePlayer();
    this.selectedBook = null;
    this.selectedChapter = null;
    this.lines = [];
    this.currentText = '';
    this.currentIndex = -1;
    this.positionMs = 0;
    this.durationMs = 0;
    this.isPlaying = false;
  }


  /** 记住真实路径（授权失效时还能用它做起始位置） */
  private saveRootPath(path: string): void {
    try {
      const context = this.getUIContext().getHostContext() as common.UIAbilityContext;
      const store = preferences.getPreferencesSync(context, { name: 'kataru_settings' });
      store.putSync('library_path', path);
      store.flush();
    } catch (error) {
      hilog.warn(0x0000, Index.TAG, 'save root path failed: %{public}s', JSON.stringify(error));
    }
  }

  /** 重新激活持久授权（每次启动都要做一次） */
  private async reactivate(uri: string): Promise<void> {
    try {
      const policy: fileShare.PolicyInfo = {
        uri: uri,
        operationMode: (fileShare.OperationMode.READ_MODE | fileShare.OperationMode.WRITE_MODE),
      };
      await fileShare.persistPermission([policy]);
      hilog.info(0x0000, Index.TAG, 'reactivate permission ok');
    } catch (error) {
      hilog.warn(0x0000, Index.TAG, 'reactivate permission failed: %{public}s', JSON.stringify(error));
    }
  }

  /**
   * 打开本地书库（App 私有目录）。
   * 这里不需要任何权限，所以永远可用 —— 这是"重启不丢"的关键。
   */
  private openLocalLibrary(): void {
    const context = this.getUIContext().getHostContext() as common.UIAbilityContext;
    const sandboxRoot = context.filesDir + '/books';

    try {
      fileIo.mkdirSync(sandboxRoot, true);
    } catch (error) {
      // 已存在
    }

    this.libraryRoot = sandboxRoot;
    this.refreshLibrary();

    if (this.books.length === 0) {
      this.needsReauth = false;
      this.statusText = '还没有书 —— 点下面的按钮选书库文件夹（选一次会自动同步到本地）';
      hilog.info(0x0000, Index.TAG, 'local library empty');
    } else {
      hilog.info(0x0000, Index.TAG, 'local library: %{public}d book(s)', this.books.length);
      this.resumeLastEntry();
    }
  }

  /**
   * 把沙箱里旧的 `.m4b` 副本改名成 `.m4a`。
   *
   * AVPlayer 按扩展名认格式，不认 .m4b；这些副本是 App 自己复制进来的，
   * 改名安全（用户的原文件在 Download 里完全不动）。
   */
  private migrateM4bCopies(root: string): void {
    let renamed = 0;
    try {
      const bookDirs = fileIo.listFileSync(root);
      for (const bookDir of bookDirs) {
        const dirPath = root + '/' + bookDir;
        let isDir = false;
        try {
          isDir = fileIo.statSync(dirPath).isDirectory();
        } catch (error) {
          isDir = false;
        }
        if (!isDir) {
          continue;
        }

        let files: string[] = [];
        try {
          files = fileIo.listFileSync(dirPath);
        } catch (error) {
          continue;
        }

        for (const file of files) {
          if (!file.toLowerCase().endsWith('.m4b')) {
            continue;
          }
          const from = dirPath + '/' + file;
          const to = dirPath + '/' + file.substring(0, file.length - 4) + '.m4a';
          try {
            fileIo.renameSync(from, to);
            renamed++;
          } catch (error) {
            hilog.warn(0x0000, Index.TAG, 'rename m4b failed: %{public}s', JSON.stringify(error));
          }
        }
      }
    } catch (error) {
      hilog.warn(0x0000, Index.TAG, 'migrate m4b failed: %{public}s', JSON.stringify(error));
    }

    if (renamed > 0) {
      hilog.info(0x0000, Index.TAG, 'migrated %{public}d .m4b -> .m4a', renamed);
    }
  }

  // ==================== 书库同步（文件夹 → App 自己的目录） ====================

  /**
   * 把选中的书库文件夹同步进 App 私有目录。
   *
   * 为什么必须同步：鸿蒙手机上的文件夹授权是**临时**的（实测 persistPermission
   * 返回 "URI forbid to be persisted!"，重启 App 就丢），所以直接按原路径读
   * 每次启动都要重新授权。同步一份到 App 目录后：不需要任何权限、重启不丢、
   * 重装 App 也不丢（除了卸载）。
   *
   * 同步规则：源目录里有什么就有什么 —— 新书复制进来，改过的（大小不同）覆盖，
   * 源里删掉的书在本地也删掉，保证不会留"幽灵书"。
   */
  private async syncFromFolder(sourceRoot: string): Promise<void> {
    const context = this.getUIContext().getHostContext() as common.UIAbilityContext;
    const sandboxRoot = context.filesDir + '/books';

    try {
      fileIo.mkdirSync(sandboxRoot, true);
    } catch (error) {
      // 已存在
    }

    this.syncing = true;
    this.syncProgress = '正在同步书库…';

    try {
      const sourceBooks = LibraryScanner.scan(sourceRoot);
      hilog.info(0x0000, Index.TAG, 'sync: source has %{public}d book(s)', sourceBooks.length);

      let copied = 0;
      let skipped = 0;
      const keepNames: string[] = [];

      for (const book of sourceBooks) {
        const bookName = book.title.replace(/[\/\\:*?"<>|]/g, '_');
        keepNames.push(bookName);
        const bookDir = `${sandboxRoot}/${bookName}`;

        try {
          fileIo.mkdirSync(bookDir, true);
        } catch (error) {
          // 已存在
        }

        // 章节音频 + 字幕
        for (const chapter of book.chapters) {
          this.syncProgress = '同步：' + book.title;

          if (await this.syncOneFile(chapter.audioPath, bookDir)) {
            copied++;
          } else {
            skipped++;
          }

          if (chapter.subtitlePath.length > 0) {
            if (await this.syncOneFile(chapter.subtitlePath, bookDir)) {
              copied++;
            } else {
              skipped++;
            }
          }

          if (chapter.secondarySubtitlePath.length > 0) {
            if (await this.syncOneFile(chapter.secondarySubtitlePath, bookDir)) {
              copied++;
            } else {
              skipped++;
            }
          }
        }

        // 封面
        if (book.coverPath.length > 0) {
          await this.syncOneFile(book.coverPath, bookDir);
        }
      }

      // 删掉源里已经不存在的书（移动/删除后不留残留）
      let removed = 0;
      try {
        const existing = fileIo.listFileSync(sandboxRoot);
        for (const name of existing) {
          let isDir = false;
          try {
            isDir = fileIo.statSync(`${sandboxRoot}/${name}`).isDirectory();
          } catch (error) {
            isDir = false;
          }
          if (!isDir) {
            continue;
          }

          let keep = false;
          for (const nameToKeep of keepNames) {
            if (nameToKeep === name) {
              keep = true;
              break;
            }
          }

          if (!keep) {
            fileIo.rmdirSync(`${sandboxRoot}/${name}`);
            removed++;
            hilog.info(0x0000, Index.TAG, 'sync: removed obsolete book %{public}s', name);
          }
        }
      } catch (error) {
        hilog.warn(0x0000, Index.TAG, 'sync cleanup failed: %{public}s', JSON.stringify(error));
      }

      hilog.info(0x0000, Index.TAG,
        'sync done: copied=%{public}d skipped=%{public}d removed=%{public}d', copied, skipped, removed);

      this.libraryRoot = sandboxRoot;
      this.saveRootPath(sandboxRoot);
      this.refreshLibrary();

      if (this.books.length > 0) {
        this.statusText = `已同步 ${this.books.length} 本书（本地）`;
      } else {
        this.statusText = '这个文件夹里没找到书（每本书一个子文件夹，里面放音频和字幕）';
      }
    } catch (error) {
      this.statusText = '同步失败：' + JSON.stringify(error);
      hilog.error(0x0000, Index.TAG, 'sync failed: %{public}s', JSON.stringify(error));
    } finally {
      this.syncing = false;
      this.syncProgress = '';
    }
  }

  /** 同步单个文件；返回是否真的复制了（false = 已是最新） */
  private async syncOneFile(sourcePath: string, targetDir: string): Promise<boolean> {
    let fileName = sourcePath.substring(sourcePath.lastIndexOf('/') + 1);
    fileName = fileName.replace(/[\/\\:*?"<>|]/g, '_');

    // AVPlayer 按扩展名认格式，.m4b 不认；同步时落成 .m4a（内容不变）
    if (fileName.toLowerCase().endsWith('.m4b')) {
      fileName = fileName.substring(0, fileName.length - 4) + '.m4a';
    }

    const target = `${targetDir}/${fileName}`;

    try {
      const sourceSize = fileIo.statSync(sourcePath).size;
      if (sourceSize <= 0) {
        return false;
      }

      if (fileIo.accessSync(target)) {
        let existing = 0;
        try {
          existing = fileIo.statSync(target).size;
        } catch (error) {
          existing = -1;
        }
        if (existing === sourceSize) {
          return false;
        }
        try {
          fileIo.unlinkSync(target);
        } catch (error) {
          // 覆盖写
        }
      }

      const written = await this.copyFileChunked(sourcePath, target);
      return written === sourceSize;
    } catch (error) {
      hilog.error(0x0000, Index.TAG, 'sync file failed: %{public}s %{public}s',
        fileName, JSON.stringify(error));
      return false;
    }
  }

  /** 让出一次事件循环，保证同步大文件时界面不卡 */
  private yieldToUi(): Promise<void> {
    return new Promise<void>((resolve: () => void) => {
      setTimeout(() => {
        resolve();
      }, 0);
    });
  }

  /** 分块复制（不能用 fileIo.copyFile：实测会写出 0 字节空文件） */
  private async copyFileChunked(sourcePath: string, targetPath: string): Promise<number> {
    const chunk = 4 * 1024 * 1024;
    const buffer = new ArrayBuffer(chunk);

    const src = fileIo.openSync(sourcePath, fileIo.OpenMode.READ_ONLY);
    let total = 0;
    let written = 0;

    try {
      total = fileIo.statSync(src.fd).size;
      const dst = fileIo.openSync(targetPath,
        fileIo.OpenMode.READ_WRITE | fileIo.OpenMode.CREATE | fileIo.OpenMode.TRUNC);

      try {
        let readOffset = 0;
        let sinceYield = 0;
        while (readOffset < total) {
          const want = Math.min(chunk, total - readOffset);
          const read = fileIo.readSync(src.fd, buffer, { offset: readOffset, length: want });
          if (read <= 0) {
            break;
          }
          const wrote = fileIo.writeSync(dst.fd, buffer, { offset: written, length: read });
          written += wrote;
          readOffset += read;

          // 每 32 MB 让出一次，避免 700 MB 的音频把界面卡住
          sinceYield += read;
          if (sinceYield >= 32 * 1024 * 1024) {
            sinceYield = 0;
            await this.yieldToUi();
          }
        }
      } finally {
        fileIo.closeSync(dst);
      }
    } finally {
      fileIo.closeSync(src);
    }

    hilog.info(0x0000, Index.TAG, 'copied %{public}d/%{public}d bytes -> %{public}s',
      written, total, targetPath.substring(targetPath.lastIndexOf('/') + 1));
    return written;
  }
  /** 把路径解码成 PixelMap（Image 组件对沙箱外路径不认，必须先解码） */
  private async loadPixelMap(path: string): Promise<image.PixelMap | undefined> {
    let file: fileIo.File | null = null;
    let source: image.ImageSource | null = null;

    try {
      // 先试路径重载（沙箱内路径最稳）；失败再退回 fd 版本
      try {
        source = image.createImageSource(path);
        hilog.info(0x0000, Index.TAG, 'cover source by path: %{public}s', path);
      } catch (pathError) {
        hilog.warn(0x0000, Index.TAG, 'cover source by path failed: %{public}s',
          JSON.stringify(pathError));
        source = null;
      }

      if (source === null) {
        file = fileIo.openSync(path, fileIo.OpenMode.READ_ONLY);
        source = image.createImageSource(file.fd);
        hilog.info(0x0000, Index.TAG, 'cover source by fd: %{public}d', file.fd);
      }

      // 按原图比例降采样：只限制最长边，两个方向都传死尺寸会把图压扁（拉伸的根源）。
      // 注意必须用异步 createPixelMap —— createPixelMapSync 在真机上会返回 undefined。
      let options: image.DecodingOptions = {};
      try {
        const info = await source.getImageInfo();
        const maxEdge = 640;
        const scale = Math.max(info.size.width, info.size.height) / maxEdge;
        if (scale > 1) {
          options.desiredSize = {
            width: Math.round(info.size.width / scale),
            height: Math.round(info.size.height / scale)
          };
        }
        hilog.info(0x0000, Index.TAG, 'cover info: %{public}dx%{public}d -> %{public}s',
          info.size.width, info.size.height, JSON.stringify(options.desiredSize));
      } catch (error) {
        hilog.warn(0x0000, Index.TAG, 'cover info failed, decode as-is');
      }

      const pixel = await source.createPixelMap(options);
      hilog.info(0x0000, Index.TAG, 'cover OK: %{public}s (%{public}d bytes)',
        path, pixel.getPixelBytesNumber());
      return pixel;
    } catch (error) {
      const err = error as BusinessError;
      hilog.error(0x0000, Index.TAG, 'cover FAILED: %{public}s code=%{public}d msg=%{public}s',
        path, err.code, err.message);
      return undefined;
    } finally {
      if (source !== null) {
        try {
          await source.release();
        } catch (error) {
          // 忽略
        }
      }
      if (file !== null) {
        try {
          fileIo.closeSync(file);
        } catch (error) {
          // 忽略
        }
      }
    }
  }

  /** 重建封面缓存（每次扫描/导入后调用；异步解码完再更新状态） */
  private rebuildCovers(): void {
    this.rebuildCoversAsync();
  }

  private async rebuildCoversAsync(): Promise<void> {
    const list: CoverItem[] = [];
    for (const book of this.books) {
      if (book.coverPath.length === 0) {
        continue;
      }

      // 已在缓存里就复用，不重复解码（刷新/切页时省 CPU）
      let cached: image.PixelMap | undefined = undefined;
      for (const item of this.covers) {
        if (item.path === book.coverPath) {
          cached = item.pixel;
          break;
        }
      }

      if (cached !== undefined) {
        list.push(new CoverItem(book.coverPath, cached));
        continue;
      }

      const pixel = await this.loadPixelMap(book.coverPath);
      if (pixel !== undefined) {
        list.push(new CoverItem(book.coverPath, pixel));
      }
    }

    // 释放不再需要的封面（书被删掉或换了封面）
    for (const old of this.covers) {
      let keep = false;
      for (const item of list) {
        if (item.path === old.path) {
          keep = true;
          break;
        }
      }
      if (!keep && old.pixel !== undefined) {
        try {
          await old.pixel.release();
        } catch (error) {
          // 忽略
        }
      }
    }

    this.covers = list;
    hilog.info(0x0000, Index.TAG, 'covers: %{public}d/%{public}d loaded', list.length, this.books.length);
  }

  /** 按路径取封面 */
  private coverFor(path: string): image.PixelMap | undefined {
    if (path.length === 0) {
      return undefined;
    }
    for (const item of this.covers) {
      if (item.path === path) {
        return item.pixel;
      }
    }
    return undefined;
  }

  /** 记住用户选的书库文件夹 */
  private saveRoot(uri: string): void {
    try {
      const context = this.getUIContext().getHostContext() as common.UIAbilityContext;
      const store = preferences.getPreferencesSync(context, { name: 'kataru_settings' });
      store.putSync('library_uri', uri);
      store.flush();
    } catch (error) {
      hilog.warn(0x0000, Index.TAG, 'save root failed: %{public}s', JSON.stringify(error));
    }
  }

  /** 手动重新扫描（书移动/删除后点这个，或启动时用） */
  private rescanSandbox(): void {
    const context = this.getUIContext().getHostContext() as common.UIAbilityContext;
    const sandboxRoot = context.filesDir + '/books';

    try {
      fileIo.mkdirSync(sandboxRoot, true);
    } catch (error) {
      // 目录已存在会报错，忽略
    }

    if (this.libraryRoot.length === 0) {
      this.libraryRoot = sandboxRoot;
    }

    this.refreshLibrary();
  }

  /**
   * 把一个 picker 返回的目录 URI 变成"真的能列目录"的路径。
   *
   * picker 给的是 file://docs/storage/Users/currentUser/Download/... 这种 URI，
   * 直接喂给 fileIo.listFileSync 会报 13900001（没权限）。用 FileUri 取出 path
   * （/storage/Users/currentUser/Download/...）之后就能正常列了。
   *
   * 依次尝试：原始 URI → FileUri.path → getFullDirectoryUri，哪个能列就用哪个。
   */
  private resolveReadableDir(uri: string): string {
    const candidates: string[] = [uri];

    try {
      const parsed = new fileUri.FileUri(uri);
      candidates.push(parsed.path);
    } catch (error) {
      hilog.warn(0x0000, Index.TAG, 'FileUri(uri).path failed: %{public}s', JSON.stringify(error));
    }

    try {
      const parsed = new fileUri.FileUri(uri);
      const full = parsed.getFullDirectoryUri();
      if (full !== undefined && full.length > 0) {
        candidates.push(full);
        candidates.push(new fileUri.FileUri(full).path);
      }
    } catch (error) {
      hilog.warn(0x0000, Index.TAG, 'getFullDirectoryUri failed: %{public}s', JSON.stringify(error));
    }

    for (const candidate of candidates) {
      try {
        const entries = fileIo.listFileSync(candidate);
        hilog.info(0x0000, Index.TAG, 'list OK: %{public}s (%{public}d entries)',
          candidate, entries.length);
        return candidate;
      } catch (error) {
        const err = error as BusinessError;
        hilog.warn(0x0000, Index.TAG, 'list FAILED: %{public}s code=%{public}d',
          candidate, err.code);
      }
    }

    return '';
  }

  /**
   * 让用户用系统文件选择器挑一个文件夹（鸿蒙上访问公共目录的正规方式），
   * 并把授权持久化，下次启动直接用。
   */
  private async chooseLibraryFolder(): Promise<void> {
    try {
      const options = new picker.DocumentSelectOptions();
      options.maxSelectNumber = 1;
      // 从上次的目录开始挑，省得每次翻（有些版本不接受，忽略即可）
      if (this.libraryRoot.startsWith('file://')) {
        try {
          options.defaultFilePathUri = this.libraryRoot;
        } catch (error) {
          // 忽略
        }
      }
      // 官方文档：selectMode 的系统能力
      //   API 26.0.0+  → UserFileService（通用）
      //   API 11 ~ 24  → UserFileService.FolderSelection（仅 2-in-1）
      // 所以手机上（≤24）会抛「该功能暂不支持」，这里降级为多选文件。
      options.selectMode = picker.DocumentSelectMode.FOLDER;

      const documentPicker = new picker.DocumentViewPicker(this.getUIContext().getHostContext() as common.UIAbilityContext);
      const uris: string[] = await documentPicker.select(options);
      hilog.info(0x0000, Index.TAG, 'folder picker returned %{public}d uri(s)',
        uris === null ? -1 : uris.length);

      if (uris === null || uris.length === 0) {
        // 用户取消了（系统返回 errorcode -1 / 空数组），别静默返回，界面上要说清楚
        this.statusText = '已取消选择文件夹';
        return;
      }

      const uri = uris[0];
      hilog.info(0x0000, Index.TAG, 'picked folder uri: %{public}s', uri);

      // 关键：picker 返回的是 file://docs/... 这种 URI，fileIo.listFileSync 对 URI
      // 形式的目录支持不好（实测报 13900001 没权限），必须用 FileUri 转成真实路径
      // /storage/Users/currentUser/... 再去列目录。
      const resolved = this.resolveReadableDir(uri);
      if (resolved.length === 0) {
        this.statusText = '这个文件夹列不出来（试了 URI 和真实路径都不行），看日志';
        return;
      }

      hilog.info(0x0000, Index.TAG, 'resolved dir: %{public}s', resolved);

      // 持久化授权：不加这一步，App 重启后就又没权限了
      try {
        const policy: fileShare.PolicyInfo = {
          uri: uri,
          operationMode: (fileShare.OperationMode.READ_MODE | fileShare.OperationMode.WRITE_MODE),
        };
        await fileShare.persistPermission([policy]);
        hilog.info(0x0000, Index.TAG, 'persist permission ok: %{public}s', uri);
      } catch (error) {
        hilog.warn(0x0000, Index.TAG, 'persist permission failed: %{public}s', JSON.stringify(error));
      }

      // 记下源文件夹，然后立刻同步进 App 目录：
      // 之后不需要任何授权、重启不丢（这是持久化的关键）
      this.needsReauth = false;
      this.saveRoot(uri);
      this.saveRootPath(resolved);
      await this.syncFromFolder(resolved);
    } catch (error) {
      const err = error as BusinessError;
      hilog.error(0x0000, Index.TAG, 'choose folder failed: code=%{public}d msg=%{public}s',
        err.code, err.message);

      this.statusText = '选文件夹失败：' + err.message;
    }
  }

  /** 退到后台时保存进度（切走 App 也不会丢位置） */
  onPageHide(): void {
    this.saveProgress(true);
  }

  aboutToDisappear(): void {
    this.saveProgress(true);
    this.stopTicker();
    this.mediaSession.stop();
    this.liveSubtitle.stop(this.selectedChapter === null ? '' : this.selectedChapter.title);
    this.releasePlayer();
  }

  build() {
    Stack() {
      // 内置渐变背景（没有自定义图时生效）
      if (this.backgroundPixel === null && this.backgroundGradient >= 0 &&
        this.backgroundGradient < BACKGROUND_GRADIENTS.length) {
        Column()
          .width('100%')
          .height('100%')
          .linearGradient({
            angle: 160,
            colors: [
              [BACKGROUND_GRADIENTS[this.backgroundGradient].from, 0],
              [BACKGROUND_GRADIENTS[this.backgroundGradient].to, 1]
            ]
          })
      }
        // 渐变上再压一层主题底色：浅色渐变配深色文本、深色渐变配浅色文本都能看清
        Column()
          .width('100%')
          .height('100%')
          .backgroundColor(this.theme.pageBackground)
          .opacity(0.45)


      // 自定义背景图：铺满 + 模糊，再盖一层黑色压暗，保证文字清晰
      if (this.backgroundPixel !== null) {
        Image(this.backgroundPixel)
          .width('100%')
          .height('100%')
          .objectFit(ImageFit.Cover)
          .blur(this.backgroundBlur)

        Column()
          .width('100%')
          .height('100%')
          .backgroundColor('#000000')
          .opacity(this.backgroundDim / 100)
      }

    Column() {
      // 顶栏
      Row() {
        Text('KATARU')
        .fontFamily(this.uiFont())
          .fontSize(19)
          .fontWeight(FontWeight.Bold)
          .fontColor(this.theme.textPrimary)

        Blank()

        if (this.currentTab === 0) {
        }

        if (this.currentTab === 2 && this.lines.length > 0) {
          Text(`${this.currentIndex + 1} / ${this.lines.length}`)
          .fontFamily(this.uiFont())
            .fontSize(12)
            .fontColor(this.theme.textSecondary)
            .margin({ left: 8 })
        }
      }
      .width('100%')
      .padding({ left: 16, right: 16, top: 10, bottom: 6 })

      // 四个模式
      Tabs({ index: this.currentTab, barPosition: BarPosition.End }) {
        TabContent() {
          this.LibraryPage()
        }
        .tabBar(this.TabBarItem('书库', 0))

        TabContent() {
          this.PlayerPage()
        }
        .tabBar(this.TabBarItem('播放', 1))

        TabContent() {
          this.ReadingPage()
        }
        .tabBar(this.TabBarItem('阅读', 2))

        TabContent() {
          this.SettingsPage()
        }
        .tabBar(this.TabBarItem('设置', 3))
      }
      .layoutWeight(1)
      .barHeight(56)
      // 关掉左右滑动切页：否则页面里的滑杆（音量/字号/调色）会被 Tabs 的滑动手势抢走
      .scrollable(false)
      .onChange((index: number) => {
        this.currentTab = index;
        // 切到阅读页时立刻定位到正在播的那一句（否则要等下一句才会跳）
        if (index === 2) {
          this.scrollReadingToCurrentSoon();
        }
      })
    }
    .width('100%')
    .height('100%')
    .backgroundColor(this.backgroundPixel === null && this.backgroundGradient < 0 ? this.theme.pageBackground : Color.Transparent)
    }
    .width('100%')
    .height('100%')
    .backgroundColor(this.theme.pageBackground)
  }

  @Builder
  TabBarItem(label: string, index: number) {
    Text(label)
    .fontFamily(this.uiFont())
      .fontSize(13)
      .fontWeight(this.currentTab === index ? FontWeight.Medium : FontWeight.Normal)
      .fontColor(this.currentTab === index ? this.theme.gold : this.theme.textSecondary)
      .textAlign(TextAlign.Center)
      .width('100%')
  }

  // ==================== ① 书库 ====================
  @Builder
  LibraryPage() {
    Column() {
      if (this.books.length === 0) {
        Column() {
          Text(this.needsReauth ? '书库授权失效了' : '书架是空的')
          .fontFamily(this.uiFont())
            .fontSize(17)
            .fontWeight(FontWeight.Medium)
            .fontColor(this.theme.textPrimary)

          Text(this.needsReauth
            ? `上次选的是：\n${this.libraryRoot}\n\n重装 App 会让系统收回文件夹授权，点下面按钮重新授权一次就好（书不会动）。`
            : '把「一本书一个文件夹」放进手机里，然后点下面的按钮选那一层文件夹：\nDownload/KATARU/Book')
            .fontSize(12)
            .lineHeight(20)
            .textAlign(TextAlign.Center)
            .fontColor(this.theme.textSecondary)
            .margin({ top: 12, left: 24, right: 24 })

          Button(this.needsReauth ? '一键重新授权' : '选择书库文件夹')
            .margin({ top: 18 })
            .height(40)
            .backgroundColor(this.theme.accent)
            .fontColor('#FFFFFF')
            .onClick(() => this.chooseLibraryFolder())
        }
        .width('100%')
        .justifyContent(FlexAlign.Center)
        .layoutWeight(1)
      } else {
        // 继续收听（桌面版 ContinueCommand 的等价物）
        if (this.showResumeCard && this.selectedBook !== null && this.selectedChapter !== null) {
          Row() {
            if (this.coverFor(this.selectedBook.coverPath) !== undefined) {
              Image(this.coverFor(this.selectedBook.coverPath))
                .width(36)
                .height(44)
                .objectFit(this.coverFill ? ImageFit.Cover : ImageFit.Contain)
                .borderRadius(4)
            }

            Column() {
              Text('继续收听')
              .fontFamily(this.uiFont())
                .fontSize(10)
                .fontColor(this.theme.gold)
              Text(this.selectedChapter.title)
                .fontSize(12)
                .fontFamily(this.uiFont())
                .maxLines(1)
                .textOverflow({ overflow: TextOverflow.Ellipsis })
                .fontColor(this.theme.textPrimary)
            }
            .alignItems(HorizontalAlign.Start)
            .layoutWeight(1)
            .margin({ left: 10 })

            Text(Timecode.formatShort(this.loadPosition(this.selectedChapter)) + ' / ' +
              Timecode.formatShort(this.loadDuration(this.selectedChapter)))
              .fontSize(10)
              .fontColor(this.theme.textSecondary)
          }
          .width('100%')
          .padding({ left: 16, right: 16, top: 4, bottom: 8 })
          .onClick(() => {
            this.currentTab = 1;
          })
        }

        // 搜索 + 只看收藏
        Row() {
          TextInput({ placeholder: '搜索书名…', text: this.searchText })
            .layoutWeight(1)
            .height(34)
            .fontSize(13)
            .placeholderColor(this.theme.textSecondary)
            .fontColor(this.theme.textPrimary)
            .backgroundColor(this.theme.panelBackground)
            .borderRadius(17)
            .onChange((value: string) => {
              this.searchText = value;
            })

          Row() {
            Image(this.showFavoritesOnly ? $r('app.media.ic_star') : $r('app.media.ic_star_outline'))
              .width(22)
              .height(22)
              .fillColor(this.showFavoritesOnly ? this.theme.gold : this.theme.textSecondary)
          }
          .width(44)
          .height(38)
          .justifyContent(FlexAlign.Center)
          .onClick(() => {
            this.showFavoritesOnly = !this.showFavoritesOnly;
          })
            .onClick(() => {
              this.showFavoritesOnly = !this.showFavoritesOnly;
            })
        }
        .width('100%')
        .padding({ left: 16, right: 16, bottom: 8 })

        Grid() {
          ForEach(this.filteredBooks(), (book: Book) => {
            GridItem() {
              Column() {
                Stack({ alignContent: Alignment.TopStart }) {
                  if (this.coverFor(book.coverPath) !== undefined) {
                    Image(this.coverFor(book.coverPath))
                      .width('100%')
                      .aspectRatio(0.83)
                      .objectFit(this.coverFill ? ImageFit.Cover : ImageFit.Contain)
                      .borderRadius(6)
                  } else {
                    Column() {
                      Image($r('app.media.ic_book'))
                        .width(34)
                        .height(34)
                        .fillColor(this.theme.textSecondary)
                      Text(book.title.substring(0, 2))
                      .fontFamily(this.uiFont())
                        .fontSize(20)
                        .fontColor(this.theme.textSecondary)
                        .margin({ top: 6 })
                    }
                    .width('100%')
                    .aspectRatio(0.83)
                    .justifyContent(FlexAlign.Center)
                    .backgroundColor(this.theme.panelBackground)
                    .borderRadius(6)
                  }

                  // 收藏星标（桌面版有收藏功能）
                  Image(this.isFavorite(book) ? $r('app.media.ic_star') : $r('app.media.ic_star_outline'))
                    .width(18)
                    .height(18)
                    .fillColor(this.isFavorite(book) ? this.theme.gold : '#CCFFFFFF')
                    .margin(6)
                    .onClick(() => this.toggleFavorite(book))
                }
                .width('100%')

                Text(book.title)
                .fontFamily(this.uiFont())
                  .fontSize(13)
                  .fontColor(this.theme.textPrimary)
                  .maxLines(1)
                  .textOverflow({ overflow: TextOverflow.Ellipsis })
                  .margin({ top: 6 })

                Row() {
                  Text(book.chapterCount + ' 章')
                  .fontFamily(this.uiFont())
                    .fontSize(11)
                    .fontColor(this.theme.textSecondary)
                  Blank()
                  Text(this.bookProgressText(book))
                  .fontFamily(this.uiFont())
                    .fontSize(11)
                    .fontColor(this.theme.gold)
                }
                .width('100%')
                .padding({ left: 2, right: 2 })

                // 收听进度条（桌面版卡片上那条 3px 的）
                if (this.showCardProgress && this.bookProgress(book) > 0) {
                  Progress({
                    value: Math.round(this.bookProgress(book) * 100),
                    total: 100,
                    type: ProgressType.Linear
                  })
                    .width('100%')
                    .height(3)
                    .color(this.theme.gold)
                    .backgroundColor(this.theme.borderSoft)
                    .margin({ top: 4 })
                }
              }
              .width('100%')
              .onClick(() => this.selectBook(book))
            }
          }, (book: Book) => book.folderPath)
        }
        .columnsTemplate(this.libraryColumnsTemplate())
        .columnsGap(10)
        .rowsGap(14)
        .width('100%')
        .layoutWeight(1)
        .padding({ left: 16, right: 16, bottom: 12 })
      }

      Text(this.statusText)
      .fontFamily(this.uiFont())
        .fontSize(11)
        .fontColor(this.theme.textSecondary)
        .width('100%')
        .padding({ left: 16, right: 16, bottom: 8 })
        .maxLines(2)
        .textOverflow({ overflow: TextOverflow.Ellipsis })
    }
    .width('100%')
    .height('100%')
  }

  // ==================== ② 播放 ====================
  @Builder
  PlayerPage() {
    Column() {
      if (this.selectedBook !== null && this.coverFor(this.selectedBook.coverPath) !== undefined) {
        Image(this.coverFor(this.selectedBook.coverPath))
          .width(150 * this.uiScale)
          .aspectRatio(0.83)
          .objectFit(this.coverFill ? ImageFit.Cover : ImageFit.Contain)
          .borderRadius(8)
          .margin({ top: 8 })
      } else {
        Column() {
          Image($r('app.media.ic_book'))
            .width(48)
            .height(48)
            .fillColor(this.theme.textSecondary)
        }
        .width(150 * this.uiScale)
        .aspectRatio(0.83)
        .justifyContent(FlexAlign.Center)
        .backgroundColor(this.theme.panelBackground)
        .borderRadius(8)
        .margin({ top: 8 })
      }

      if (this.selectedBook !== null) {
        Row() {
          Text('换封面')
          .fontFamily(this.uiFont())
            .fontSize(11)
            .fontColor(this.theme.textSecondary)
            .padding({ left: 10, right: 10, top: 4, bottom: 4 })
            .backgroundColor(this.theme.panelBackground)
            .borderRadius(12)
            .onClick(() => this.chooseCoverImage())

          Text('恢复自动封面')
          .fontFamily(this.uiFont())
            .fontSize(11)
            .fontColor(this.theme.textSecondary)
            .padding({ left: 10, right: 10, top: 4, bottom: 4 })
            .margin({ left: 8 })
            .backgroundColor(this.theme.panelBackground)
            .borderRadius(12)
            .onClick(() => this.clearCoverImage())
        }
        .margin({ top: 6 })
      }

      Text(this.selectedBook === null ? '未选择书' : this.selectedBook.title)
        .fontSize(16)
        .fontFamily(this.uiFont())
        .fontWeight(FontWeight.Medium)
        .fontColor(this.theme.textPrimary)
        .margin({ top: 10 })
        .maxLines(1)
        .textOverflow({ overflow: TextOverflow.Ellipsis })

      Text(this.selectedChapter === null ? '选一本书开始' : this.currentChapterProgressText())
      .fontFamily(this.uiFont())
        .fontSize(12)
        .fontColor(this.theme.textSecondary)
        .margin({ top: 4 })
        .maxLines(1)
        .textOverflow({ overflow: TextOverflow.Ellipsis })

      Text(this.currentText.length > 0 ? this.currentText : '（这里显示当前这句）')
        .fontFamily(this.subtitleFamily())
        .fontSize(this.fontSize)
        .lineHeight(this.fontSize * 1.5)
        .textAlign(TextAlign.Center)
        .fontColor(this.currentText.length > 0 ? this.subtitleColor : this.theme.textSecondary)
        .fontWeight(this.subtitleWeight)
        // 描边：ArkUI 没有文字描边，用同色的零偏移阴影模拟（对应桌面版的 OutlineThickness）
        .textShadow({
          radius: this.subtitleOutlineWidth,
          color: this.subtitleOutlineColor,
          offsetX: 0,
          offsetY: 0
        })
        .maxLines(4)
        .textOverflow({ overflow: TextOverflow.Ellipsis })
        .width('100%')
        .padding({ left: 20, right: 20, top: 16 })

      // 双语：主字幕下面跟一行译文
      if (this.showSecondary && this.secondaryText.length > 0) {
        Text(this.secondaryText)
          .fontFamily(this.fontFamilyValue)
          .fontSize(this.fontSize * this.secondaryScale)
          .lineHeight(this.fontSize * 1.05)
          .textAlign(TextAlign.Center)
          .fontColor(this.theme.textSecondary)
          .maxLines(3)
          .textOverflow({ overflow: TextOverflow.Ellipsis })
          .width('100%')
          .padding({ left: 24, right: 24, top: 4 })
      }

      // 内嵌章节（m4b 的 chpl）：单文件 12 小时也能跳章
      if (this.embeddedChapters.length > 0) {
        Scroll() {
          Row() {
            ForEach(this.embeddedChapters, (mark: AudioChapterMark, index: number) => {
              Text(mark.title.length > 0 ? mark.title : Timecode.formatShort(mark.startMs))
              .fontFamily(this.uiFont())
                .fontSize(12)
                .maxLines(1)
                .textOverflow({ overflow: TextOverflow.Ellipsis })
                .constraintSize({ maxWidth: 180 })
                .padding({ left: 10, right: 10, top: 6, bottom: 6 })
                .margin({ right: 8 })
                .borderRadius(14)
                .backgroundColor(index === this.embeddedIndex
                  ? this.theme.gold : this.theme.panelBackground)
                .fontColor(index === this.embeddedIndex
                  ? '#1A1718' : this.theme.textPrimary)
                .onClick(() => this.seekTo(mark.startMs))
            }, (mark: AudioChapterMark, index: number) => index + '-' + mark.startMs)
          }
          .padding({ left: 16, right: 16 })
        }
        .scrollable(ScrollDirection.Horizontal)
        .scrollBar(BarState.Off)
        .width('100%')
        .margin({ top: 8 })
      }

      // 章节切换（多文件的书才显示）
      if (this.selectedBook !== null && this.selectedBook.chapters.length > 1) {
        Scroll() {
          Row() {
            ForEach(this.selectedBook.chapters, (chapter: Chapter, index: number) => {
              Text(chapter.title)
              .fontFamily(this.uiFont())
                .fontSize(12)
                .maxLines(1)
                .textOverflow({ overflow: TextOverflow.Ellipsis })
                .constraintSize({ maxWidth: 160 })
                .padding({ left: 10, right: 10, top: 6, bottom: 6 })
                .margin({ right: 8 })
                .borderRadius(14)
                .backgroundColor(this.selectedChapter !== null &&
                  this.selectedChapter.audioPath === chapter.audioPath
                  ? this.theme.accent : this.theme.panelBackground)
                .fontColor(this.selectedChapter !== null &&
                  this.selectedChapter.audioPath === chapter.audioPath
                  ? Color.White : this.theme.textPrimary)
                .onClick(() => this.openChapter(chapter))
            }, (chapter: Chapter) => chapter.audioPath)
          }
          .padding({ left: 16, right: 16 })
        }
        .scrollable(ScrollDirection.Horizontal)
        .scrollBar(BarState.Off)
        .width('100%')
        .margin({ top: 8 })
      }

      Blank()

      // 音量
      Row() {
        Image($r('app.media.ic_volume'))
          .width(16)
          .height(16)
          .fillColor(this.theme.textSecondary)

        Slider({ value: this.volume * 100, min: 0, max: 100, step: 1 })
          .layoutWeight(1)
          .margin({ left: 8, right: 8 })
          .selectedColor(this.theme.gold)
          .onChange((value: number) => {
            this.volume = value / 100;
            this.saveSettingsSoon();
            if (this.player !== null) {
              this.player.setVolume(this.volume);
            }
          })

        Text(Math.round(this.volume * 100) + '%')
        .fontFamily(this.uiFont())
          .fontSize(11)
          .width(38)
          .textAlign(TextAlign.End)
          .fontColor(this.theme.textSecondary)
      }
      .width('100%')
      .padding({ left: 20, right: 20 })
      .margin({ bottom: 6 })

      Row() {
        Text(Timecode.formatShort(this.positionMs))
        .fontFamily(this.uiFont())
          .fontSize(11)
          .fontColor(this.theme.textSecondary)

        Slider({
          value: this.positionMs,
          min: 0,
          max: this.durationMs > 0 ? this.durationMs : 1,
          step: 500,
          style: SliderStyle.InSet
        })
          .layoutWeight(1)
          .margin({ left: 8, right: 8 })
          .selectedColor(this.theme.accent)
          .onChange((value: number, mode: SliderChangeMode) => {
            if (mode === SliderChangeMode.End || mode === SliderChangeMode.Click) {
              this.seekTo(value);
            }
          })

        Text(Timecode.formatShort(this.durationMs))
        .fontFamily(this.uiFont())
          .fontSize(11)
          .fontColor(this.theme.textSecondary)
      }
      .width('100%')
      .padding({ left: 20, right: 20 })

      Row() {
        Button('−' + this.skipSeconds + 's')
          .fontSize(13)
          .height(40)
          .backgroundColor(this.theme.panelBackground)
          .fontColor(this.theme.textPrimary)
          .onClick(() => this.skip(-this.skipSeconds * 1000))

        Button(this.isPlaying ? '暂停' : '播放')
          .fontSize(15)
          .height(40)
          .width(104)
          .margin({ left: 14, right: 14 })
          .backgroundColor(this.theme.accent)
          .fontColor(Color.White)
          .enabled(this.selectedChapter !== null)
          .onClick(() => this.togglePlay())

        Button('+' + this.skipSeconds + 's')
          .fontSize(13)
          .height(40)
          .backgroundColor(this.theme.panelBackground)
          .fontColor(this.theme.textPrimary)
          .onClick(() => this.skip(this.skipSeconds * 1000))
      }
      .margin({ top: 10, bottom: 18 })
    }
    .width('100%')
    .height('100%')
  }

  // ==================== ③ 阅读 ====================
  @Builder
  ReadingPage() {
    Column() {
      if (this.lines.length === 0) {
        Column() {
          Text(this.selectedBook === null ? '先到书库选一本书' : '这一章没有配到字幕')
            .fontSize(15)
            .fontFamily(this.uiFont())
            .fontColor(this.theme.textPrimary)

          Text('字幕要和音频放同一个目录、文件名相同（01.mp3 配 01.srt）')
          .fontFamily(this.uiFont())
            .fontSize(12)
            .fontColor(this.theme.textSecondary)
            .textAlign(TextAlign.Center)
            .margin({ top: 10, left: 28, right: 28 })
        }
        .width('100%')
        .layoutWeight(1)
        .justifyContent(FlexAlign.Center)
      } else {
        List({ space: this.readingParagraphGap, scroller: this.readingScroller }) {
          // 用 LazyForEach：这一章有 11444 条字幕，ForEach 会一次性建出全部
          // ListItem，直接卡死十几秒。LazyForEach 只建可见的几条。
          LazyForEach(this.lineSource, (line: SubtitleLine, index: number) => {
            ListItem() {
              Row() {
                Text(line.timeText())
                .fontFamily(this.uiFont())
                  .fontSize(11)
                  .width(50)
                  .fontColor(this.theme.textSecondary)

                Column() {
                  Text(line.text)
                    .fontSize(this.readingFontSize * this.uiScale)
                    .fontFamily(this.uiFont())
                    .lineHeight(this.readingLineHeight * this.uiScale)
                    .width('100%')
                    .fontColor(index === this.currentIndex ? this.theme.textPrimary : this.theme.textSecondary)
                    .fontWeight(index === this.currentIndex ? FontWeight.Medium : FontWeight.Normal)

                  // 双语：这一条对应的译文
                  if (this.showSecondary && this.secondaryLines.length > 0) {
                    Text(this.secondaryTextAt(line.startMs))
                      .fontSize(this.readingSecondarySize * this.uiScale)
                      .fontFamily(this.uiFont())
                      .lineHeight(19 * this.uiScale)
                      .width('100%')
                      .fontColor(this.theme.textSecondary)
                      .margin({ top: 2 })
                  }
                }
                .layoutWeight(1)
                .alignItems(HorizontalAlign.Start)
              }
              .width('100%')
              .alignItems(VerticalAlign.Top)
              .padding({ left: 10, right: 10, top: 8, bottom: 8 })
              .backgroundColor(index === this.currentIndex ? this.currentLineTint() : Color.Transparent)
              .borderRadius(8)
              .onClick(() => this.seekTo(line.startMs))
            }
          }, (line: SubtitleLine) => `${line.index}-${line.startMs}`)
        }
        .width('100%')
        .layoutWeight(1)
        .padding({ left: 8, right: 8 })
        .scrollBar(BarState.Auto)

        // 阅读页的播放控制条（边看边听，不用切回播放页）
        Row() {
          Row() {
            Image($r('app.media.ic_target'))
              .width(20)
              .height(20)
              .fillColor(this.currentIndex >= 0 ? this.theme.gold : this.theme.textSecondary)
          }
          .width(40)
          .height(44)
          .justifyContent(FlexAlign.Center)
          .onClick(() => this.jumpToCurrentLine())

          Row() {
            Image($r('app.media.ic_prev'))
              .width(22)
              .height(22)
              .fillColor(this.theme.textPrimary)
          }
          .width(46)
          .height(44)
          .justifyContent(FlexAlign.Center)
          .onClick(() => this.playPreviousChapter())

          Text('−' + this.skipSeconds)
          .fontFamily(this.uiFont())
            .fontSize(13)
            .height(40)
            .textAlign(TextAlign.Center)
            .padding({ left: 10, right: 10 })
            .fontColor(this.theme.textSecondary)
            .onClick(() => this.skip(-this.skipSeconds * 1000))

          Row() {
            Image(this.isPlaying ? $r('app.media.ic_pause') : $r('app.media.ic_play'))
              .width(24)
              .height(24)
              .fillColor('#FFFFFF')
          }
          .width(56)
          .height(56)
          .borderRadius(28)
          .justifyContent(FlexAlign.Center)
          .backgroundColor(this.theme.accent)
          .margin({ left: 8, right: 8 })
          .onClick(() => this.togglePlay())

          Text('+' + this.skipSeconds)
          .fontFamily(this.uiFont())
            .fontSize(13)
            .height(40)
            .textAlign(TextAlign.Center)
            .padding({ left: 10, right: 10 })
            .fontColor(this.theme.textSecondary)
            .onClick(() => this.skip(this.skipSeconds * 1000))

          Row() {
            Image($r('app.media.ic_next'))
              .width(22)
              .height(22)
              .fillColor(this.theme.textPrimary)
          }
          .width(46)
          .height(44)
          .justifyContent(FlexAlign.Center)
          .onClick(() => this.playNextChapter())
        }
        .width('100%')
        .justifyContent(FlexAlign.Center)
        .alignItems(VerticalAlign.Center)
        .padding({ top: 6, bottom: 8 })
        // 半透明 + 顶部细线：叠在渐变/图片背景上也不突兀
        .backgroundColor(this.translucentPanel('B3'))
        .border({ width: { top: 0.5 }, color: this.theme.borderSoft })
      }
    }
    .width('100%')
    .height('100%')
  }

  // ==================== ④ 设置 ====================
  @Builder
  SettingsPage() {
    Scroll() {
      Column() {
        this.SettingsTabs()

        // 分段显示：一次只构建一组，既好找也不卡
        if (this.settingsGroup === 0) {
        this.SectionTitle('背景')
        Row() {
          Text(this.backgroundPath.length > 0 ? '已设置自定义背景' : '使用主题纯色背景')
            .fontFamily(this.uiFont())
            .fontSize(13)
            .fontColor(this.theme.textPrimary)
          Blank()
        }
        .width('100%')
        .padding({ top: 4, bottom: 4 })

        Row() {
          Button('选择背景图')
            .height(36)
            .layoutWeight(1)
            .fontSize(13)
            .backgroundColor(this.theme.panelBackground)
            .fontColor(this.theme.textPrimary)
            .onClick(() => this.chooseBackgroundImage())

          Button('清除')
            .height(36)
            .width(80)
            .fontSize(13)
            .margin({ left: 8 })
            .backgroundColor(this.theme.panelBackground)
            .fontColor(this.theme.textSecondary)
            .onClick(() => this.clearBackgroundImage())
        }
        .width('100%')
        .margin({ top: 6 })

        Text('内置渐变背景')
          .fontFamily(this.uiFont())
          .fontSize(12)
          .fontColor(this.theme.textSecondary)
          .margin({ top: 10, bottom: 4 })

        Flex({ wrap: FlexWrap.Wrap }) {
          ForEach(BACKGROUND_GRADIENTS, (gradient: BackgroundGradient, index: number) => {
            Row() {
              Column()
                .width(24)
                .height(24)
                .borderRadius(6)
                .linearGradient({
                  angle: 160,
                  colors: [[gradient.from, 0], [gradient.to, 1]]
                })
              Text(gradient.name)
                .fontFamily(this.uiFont())
                .fontSize(12)
                .fontColor(this.theme.textPrimary)
                .margin({ left: 6 })
            }
            .padding({ left: 8, right: 10, top: 5, bottom: 5 })
            .margin({ right: 8, bottom: 8 })
            .borderRadius(8)
            .backgroundColor(this.theme.panelBackground)
            .border({
              width: this.backgroundGradient === index ? 2 : 1,
              color: this.backgroundGradient === index ? this.theme.gold : this.theme.borderSoft
            })
            .onClick(() => {
              // 选了渐变就撤掉自定义图，避免两个背景打架
              this.backgroundGradient = index;
              if (this.backgroundPath.length > 0) {
                this.clearBackgroundImage();
              }
              this.syncThemeWithBackground(index);
              this.saveSettings();
            })
          }, (gradient: BackgroundGradient) => gradient.name)

          Row() {
            Text('无渐变')
              .fontFamily(this.uiFont())
              .fontSize(12)
              .fontColor(this.theme.textPrimary)
          }
          .padding({ left: 10, right: 10, top: 10, bottom: 10 })
          .margin({ right: 8, bottom: 8 })
          .borderRadius(8)
          .backgroundColor(this.theme.panelBackground)
          .border({
            width: this.backgroundGradient < 0 ? 2 : 1,
            color: this.backgroundGradient < 0 ? this.theme.gold : this.theme.borderSoft
          })
          .onClick(() => {
            this.backgroundGradient = -1;
            this.saveSettings();
          })
        }
        .width('100%')

        if (this.backgroundPath.length > 0) {
          Row() {
            Text('压暗')
              .fontFamily(this.uiFont())
              .fontSize(12)
              .width(36)
              .fontColor(this.theme.textSecondary)
            Slider({ value: this.backgroundDim, min: 0, max: 85, step: 5 })
              .layoutWeight(1)
              .selectedColor(this.theme.gold)
              .onChange((value: number) => {
                this.backgroundDim = value;
                this.saveSettingsSoon();
              })
          }
          .width('100%')

          Row() {
            Text('模糊')
              .fontFamily(this.uiFont())
              .fontSize(12)
              .width(36)
              .fontColor(this.theme.textSecondary)
            Slider({ value: this.backgroundBlur, min: 0, max: 40, step: 2 })
              .layoutWeight(1)
              .selectedColor(this.theme.gold)
              .onChange((value: number, mode: SliderChangeMode) => {
                if (mode === SliderChangeMode.Moving) {
                  return;
                }
                this.backgroundBlur = value;
                this.saveSettingsSoon();
              })
          }
          .width('100%')
        }

        Blank().height(6)
        this.SectionTitle('书架')

        Row() {
          Text('阅读页字号')
            .fontFamily(this.uiFont())
            .fontSize(13)
            .fontColor(this.theme.textPrimary)
          Text(`${Math.round(this.readingFontSize)}`)
            .fontFamily(this.uiFont())
            .fontSize(12)
            .fontColor(this.theme.textSecondary)
            .layoutWeight(1)
            .textAlign(TextAlign.End)
        }
        .width('100%')
        .padding({ top: 4 })

        Slider({ value: this.readingFontSize, min: 12, max: 30, step: 1 })
          .width('100%')
          .selectedColor(this.theme.accent)
          .onChange((value: number) => {
            this.readingFontSize = value;
            this.saveSettingsSoon();
          })

        Row() {
          Text('阅读页译文字号')
            .fontFamily(this.uiFont())
            .fontSize(13)
            .fontColor(this.theme.textPrimary)
          Text(`${Math.round(this.readingSecondarySize)}`)
            .fontFamily(this.uiFont())
            .fontSize(12)
            .fontColor(this.theme.textSecondary)
            .layoutWeight(1)
            .textAlign(TextAlign.End)
        }
        .width('100%')
        .padding({ top: 4 })

        Slider({ value: this.readingSecondarySize, min: 10, max: 24, step: 1 })
          .width('100%')
          .selectedColor(this.theme.accent)
          .onChange((value: number) => {
            this.readingSecondarySize = value;
            this.saveSettingsSoon();
          })

        Row() {
          Text('首页显示「继续收听」')
            .fontFamily(this.uiFont())
            .fontSize(13)
            .fontColor(this.theme.textPrimary)
          Blank()
          Toggle({ type: ToggleType.Switch, isOn: this.showResumeCard })
            .selectedColor(this.theme.accent)
            .onChange((isOn: boolean) => {
              this.showResumeCard = isOn;
              this.saveSettings();
            })
        }
        .width('100%')
        .padding({ top: 8, bottom: 4 })

        Row() {
          Text('每行书数')
            .fontFamily(this.uiFont())
            .fontSize(13)
            .fontColor(this.theme.textPrimary)
          Blank()
          Row() {
            ForEach([2, 3, 4], (count: number) => {
              Text(`${count}`)
                .fontFamily(this.uiFont())
                .fontSize(12)
                .padding({ left: 12, right: 12, top: 5, bottom: 5 })
                .margin({ left: 6 })
                .borderRadius(11)
                .backgroundColor(this.libraryColumns === count ? this.theme.accent : this.theme.panelBackground)
                .fontColor(this.libraryColumns === count ? '#FFFFFF' : this.theme.textPrimary)
                .onClick(() => {
                  this.libraryColumns = count;
                  this.saveSettings();
                })
            }, (count: number) => count.toString())
          }
        }
        .width('100%')
        .padding({ top: 8, bottom: 4 })

        Row() {
          Text('封面圆角')
            .fontFamily(this.uiFont())
            .fontSize(13)
            .fontColor(this.theme.textPrimary)
          Text(`${Math.round(this.bookRadius)}`)
            .fontFamily(this.uiFont())
            .fontSize(12)
            .fontColor(this.theme.textSecondary)
            .layoutWeight(1)
            .textAlign(TextAlign.End)
        }
        .width('100%')
        .padding({ top: 4 })

        Slider({ value: this.bookRadius, min: 0, max: 16, step: 1 })
          .width('100%')
          .selectedColor(this.theme.accent)
          .onChange((value: number) => {
            this.bookRadius = value;
            this.saveSettingsSoon();
          })

        Row() {
          Text('卡片显示进度条')
            .fontFamily(this.uiFont())
            .fontSize(13)
            .fontColor(this.theme.textPrimary)
          Blank()
          Toggle({ type: ToggleType.Switch, isOn: this.showCardProgress })
            .selectedColor(this.theme.accent)
            .onChange((isOn: boolean) => {
              this.showCardProgress = isOn;
              this.saveSettings();
            })
        }
        .width('100%')
        .padding({ top: 4, bottom: 4 })

        Blank().height(6)
        this.SectionTitle('配色')

        Flex({ wrap: FlexWrap.Wrap }) {
          ForEach(THEME_PRESETS, (preset: ThemePreset, index: number) => {
            Row() {
              Row()
                .width(12)
                .height(22)
                .borderRadius(3)
                .backgroundColor(preset.colors.accent)
                .margin({ right: 4 })
              Row()
                .width(12)
                .height(22)
                .borderRadius(3)
                .backgroundColor(preset.colors.gold)
                .margin({ right: 4 })
              Row()
                .width(12)
                .height(22)
                .borderRadius(3)
                .backgroundColor(preset.colors.pageBackground)
                .border({ width: 1, color: preset.colors.borderSoft })

              Text(preset.name)
              .fontFamily(this.uiFont())
                .fontSize(12)
                .fontColor(this.theme.textPrimary)
                .margin({ left: 6 })
            }
            .padding({ left: 8, right: 10, top: 6, bottom: 6 })
            .margin({ right: 8, bottom: 8 })
            .borderRadius(8)
            .backgroundColor(preset.colors.panelBackground)
            .border({
              width: this.themeIndex === index ? 2 : 1,
              color: this.themeIndex === index ? preset.colors.gold : this.theme.borderSoft
            })
            .onClick(() => {
              this.themeIndex = index;
              this.theme = THEME_PRESETS[index].colors;
              const context0 = this.getUIContext().getHostContext() as common.UIAbilityContext;
              ThemeStore.save(context0, index);
            })
          }, (preset: ThemePreset) => preset.name)
        }
        .width('100%')

        Blank().height(6)

        // 自定义配色：四个色位点开就是 H/S/L 滑杆（对应桌面版的主题取色器）
        Text('自定义配色（点色块调）')
        .fontFamily(this.uiFont())
          .fontSize(12)
          .fontColor(this.theme.textSecondary)
          .margin({ top: 4, bottom: 6 })

        Flex({ wrap: FlexWrap.Wrap }) {
          ForEach(THEME_SLOTS, (slot: ThemeSlot) => {
            Row() {
              Row()
                .width(18)
                .height(18)
                .borderRadius(9)
                .backgroundColor(this.slotColor(slot.key))
                .border({ width: 1, color: this.theme.borderSoft })
              Text(slot.label)
              .fontFamily(this.uiFont())
                .fontSize(11)
                .fontColor(this.theme.textPrimary)
                .margin({ left: 6 })
            }
            .padding({ left: 8, right: 10, top: 5, bottom: 5 })
            .margin({ right: 8, bottom: 8 })
            .borderRadius(8)
            .backgroundColor(this.theme.panelBackground)
            .border({
              width: this.editingSlot === slot.key ? 2 : 1,
              color: this.editingSlot === slot.key ? this.theme.gold : this.theme.borderSoft
            })
            .onClick(() => this.toggleSlot(slot.key))
          }, (slot: ThemeSlot) => slot.key)
        }
        .width('100%')

        if (this.editingSlot.length > 0) {
          Column() {
            // 调色盘（HSV：饱和/明度方块 + 色相条）
            ColorPlate({
              hue: this.pickH,
              sat: this.pickS,
              val: this.pickV,
              labelColor: this.theme.textPrimary,
              frameColor: this.theme.borderSoft,
              onPick: (h: number, s: number, v: number, done: boolean) => {
                if (!done) {
                  return;
                }
                this.pickH = h;
                this.pickS = s;
                this.pickV = v;
                this.applyHsvColor();
              }
            })

            Row() {
              Text('完成')
              .fontFamily(this.uiFont())
                .fontSize(12)
                .fontColor(this.theme.gold)
                .onClick(() => {
                  this.editingSlot = '';
                })
            }
            .width('100%')
            .justifyContent(FlexAlign.End)
            .margin({ top: 6 })
          }
          .padding({ left: 4, right: 4, top: 4, bottom: 4 })
          .margin({ bottom: 8 })
        }

        Blank().height(10)

        }

        if (this.settingsGroup === 1) {
        this.SectionTitle('字幕外观')

        Row() {
          Text('字号')
          .fontFamily(this.uiFont())
            .fontSize(13)
            .fontColor(this.theme.textPrimary)
          Text(`${Math.round(this.fontSize)}`)
          .fontFamily(this.uiFont())
            .fontSize(12)
            .fontColor(this.theme.textSecondary)
            .layoutWeight(1)
            .textAlign(TextAlign.End)
        }
        .width('100%')
        .padding({ top: 8 })

        Slider({ value: this.fontSize, min: 14, max: 48, step: 1 })
          .width('100%')
          .selectedColor(this.theme.accent)
          .onChange((value: number) => {
            this.fontSize = value;
            this.saveSettingsSoon();
          })

        Row() {
          Text('行距')
            .fontFamily(this.uiFont())
            .fontSize(13)
            .fontColor(this.theme.textPrimary)
          Text(`${Math.round(this.readingLineHeight)}`)
            .fontFamily(this.uiFont())
            .fontSize(12)
            .fontColor(this.theme.textSecondary)
            .layoutWeight(1)
            .textAlign(TextAlign.End)
        }
        .width('100%')
        .padding({ top: 8 })

        Slider({ value: this.readingLineHeight, min: 18, max: 44, step: 2 })
          .width('100%')
          .selectedColor(this.theme.accent)
          .onChange((value: number) => {
            this.readingLineHeight = value;
            this.saveSettingsSoon();
          })

        Row() {
          Text('段距')
            .fontFamily(this.uiFont())
            .fontSize(13)
            .fontColor(this.theme.textPrimary)
          Text(`${Math.round(this.readingParagraphGap)}`)
            .fontFamily(this.uiFont())
            .fontSize(12)
            .fontColor(this.theme.textSecondary)
            .layoutWeight(1)
            .textAlign(TextAlign.End)
        }
        .width('100%')
        .padding({ top: 8 })

        Slider({ value: this.readingParagraphGap, min: 0, max: 20, step: 2 })
          .width('100%')
          .selectedColor(this.theme.accent)
          .onChange((value: number) => {
            this.readingParagraphGap = value;
            this.saveSettingsSoon();
          })

        Text('字体')
        .fontFamily(this.uiFont())
          .fontSize(13)
          .fontColor(this.theme.textPrimary)
          .margin({ top: 10, bottom: 6 })

        Flex({ wrap: FlexWrap.Wrap }) {
          ForEach(this.fontChoices, (choice: FontChoice) => {
            Button(choice.name)
              .fontSize(12)
              .height(32)
              .margin({ right: 8, bottom: 8 })
              .backgroundColor(this.fontFamilyValue === choice.value ? this.theme.accent : this.theme.panelBackground)
              .fontColor(this.fontFamilyValue === choice.value ? Color.White : this.theme.textPrimary)
              .onClick(() => {
                this.fontFamilyValue = choice.value;
                this.saveSettings();
              })
          }, (choice: FontChoice) => choice.value)
        }
        .width('100%')

        Row() {
          Text('界面文字也用这个字体（阅读页/书名/标题）')
          .fontFamily(this.uiFont())
            .fontSize(13)
            .fontColor(this.theme.textPrimary)
          Blank()
          Toggle({ type: ToggleType.Switch, isOn: this.uiFontFollow })
            .selectedColor(this.theme.accent)
            .onChange((isOn: boolean) => {
              this.uiFontFollow = isOn;
              this.saveSettings();
            })
        }
        .width('100%')
        .padding({ top: 6, bottom: 2 })

        // 字幕颜色（对应桌面版的配色/字幕颜色设置）
        Text('颜色')
        .fontFamily(this.uiFont())
          .fontSize(13)
          .fontColor(this.theme.textPrimary)
          .margin({ top: 6, bottom: 6 })

        Flex({ wrap: FlexWrap.Wrap }) {
          ForEach(SUB_COLORS, (item: ColorChoice) => {
            Row()
              .width(34)
              .height(34)
              .margin({ right: 10, bottom: 8 })
              .borderRadius(17)
              .backgroundColor(item.value)
              .border({
                width: this.subtitleColor === item.value ? 3 : 1,
                color: this.subtitleColor === item.value ? this.theme.gold : this.theme.borderSoft
              })
              .onClick(() => {
                this.subtitleColor = item.value;
                this.saveSettings();
              })
          }, (item: ColorChoice) => item.value)
        }
        .width('100%')

        Row() {
          Text('字重')
            .fontFamily(this.uiFont())
            .fontSize(13)
            .fontColor(this.theme.textPrimary)
          Blank()
          Row() {
            ForEach([300, 400, 500, 700, 900], (weight: number) => {
              Text(this.weightLabel(weight))
                .fontFamily(this.uiFont())
                .fontSize(12)
                .padding({ left: 7, right: 7, top: 5, bottom: 5 })
                .margin({ left: 4 })
                .borderRadius(11)
                .backgroundColor(this.subtitleWeight === weight ? this.theme.accent : this.theme.panelBackground)
                .fontColor(this.subtitleWeight === weight ? '#FFFFFF' : this.theme.textPrimary)
                .onClick(() => {
                  this.subtitleWeight = weight;
                  this.subtitleBold = weight >= 700;
                  this.saveSettings();
                })
            }, (weight: number) => weight.toString())
          }
        }
        .width('100%')
        .padding({ top: 4, bottom: 4 })

        Blank().height(6)
        }

        if (this.settingsGroup === 2) {
        this.SectionTitle('睡眠定时')

        Row() {
          Text(this.sleepMinutes > 0 ? `还剩 ${this.sleepMinutes} 分钟` : '未启用')
            .fontFamily(this.uiFont())
            .fontSize(12)
            .fontColor(this.theme.textSecondary)
          Blank()
          Row() {
            ForEach([0, 15, 30, 45, 60, 90], (minutes: number) => {
              Text(minutes === 0 ? '关' : `${minutes}分`)
                .fontFamily(this.uiFont())
                .fontSize(12)
                .padding({ left: 9, right: 9, top: 5, bottom: 5 })
                .margin({ left: 5 })
                .borderRadius(12)
                .backgroundColor(minutes > 0 && this.sleepMinutes === minutes ? this.theme.accent : this.theme.panelBackground)
                .fontColor(minutes > 0 && this.sleepMinutes === minutes ? '#FFFFFF' : this.theme.textPrimary)
                .onClick(() => this.setSleepTimer(minutes))
            }, (minutes: number) => minutes.toString())
          }
        }
        .width('100%')
        .padding({ top: 4, bottom: 4 })

        Blank().height(6)
        this.SectionTitle('播放')

        Row() {
          Text('封面裁切填充（关 = 完整显示，不裁左右）')
          .fontFamily(this.uiFont())
            .fontSize(13)
            .fontColor(this.theme.textPrimary)
          Blank()
          Toggle({ type: ToggleType.Switch, isOn: this.coverFill })
            .selectedColor(this.theme.accent)
            .onChange((isOn: boolean) => {
              this.coverFill = isOn;
              this.saveSettings();
            })
        }
        .width('100%')
        .padding({ top: 4, bottom: 4 })

        Row() {
          Text('通知栏/锁屏显示字幕')
          .fontFamily(this.uiFont())
            .fontSize(13)
            .fontColor(this.theme.textPrimary)
          Blank()
          Toggle({ type: ToggleType.Switch, isOn: this.showNotification })
            .selectedColor(this.theme.accent)
            .onChange((isOn: boolean) => {
              this.showNotification = isOn;
              this.saveSettings();
              if (isOn) {
                const notifContext = this.getUIContext().getHostContext() as common.UIAbilityContext;
                this.nowPlaying.ensurePermission(notifContext);
              } else {
                this.nowPlaying.cancel();
              }
            })
        }
        .width('100%')
        .padding({ top: 4, bottom: 4 })

        Row() {
          Text('双语对照')
          .fontFamily(this.uiFont())
            .fontSize(13)
            .fontColor(this.theme.textPrimary)
          Blank()
          Toggle({ type: ToggleType.Switch, isOn: this.showSecondary })
            .selectedColor(this.theme.accent)
            .onChange((isOn: boolean) => {
              this.showSecondary = isOn;
              this.saveSettings();
            })
        }
        .width('100%')
        .padding({ top: 4, bottom: 4 })

        Row() {
          Text('快进/快退秒数')
          .fontFamily(this.uiFont())
            .fontSize(13)
            .fontColor(this.theme.textPrimary)
          Blank()
          Row() {
            ForEach([5, 10, 15, 30], (seconds: number) => {
              Text(seconds + 's')
              .fontFamily(this.uiFont())
                .fontSize(12)
                .padding({ left: 9, right: 9, top: 5, bottom: 5 })
                .margin({ left: 6 })
                .borderRadius(12)
                .backgroundColor(this.skipSeconds === seconds ? this.theme.accent : this.theme.panelBackground)
                .fontColor(this.skipSeconds === seconds ? '#FFFFFF' : this.theme.textPrimary)
                .onClick(() => {
                  this.skipSeconds = seconds;
                  this.saveSettings();
                })
            }, (seconds: number) => seconds.toString())
          }
        }
        .width('100%')
        .padding({ top: 4, bottom: 4 })

        Row() {
          Text('启动时回到上次收听的位置')
          .fontFamily(this.uiFont())
            .fontSize(13)
            .fontColor(this.theme.textPrimary)
          Blank()
          Toggle({ type: ToggleType.Switch, isOn: this.resumeLast })
            .selectedColor(this.theme.accent)
            .onChange((isOn: boolean) => {
              this.resumeLast = isOn;
              this.saveSettings();
            })
        }
        .width('100%')
        .padding({ top: 4, bottom: 4 })

        Row() {
          Text('书架只显示有字幕的书')
          .fontFamily(this.uiFont())
            .fontSize(13)
            .fontColor(this.theme.textPrimary)
          Blank()
          Toggle({ type: ToggleType.Switch, isOn: this.showOnlyWithSubtitle })
            .selectedColor(this.theme.accent)
            .onChange((isOn: boolean) => {
              this.showOnlyWithSubtitle = isOn;
              this.saveSettings();
            })
        }
        .width('100%')
        .padding({ top: 4, bottom: 4 })

        Row() {
          Text('播完自动下一章')
          .fontFamily(this.uiFont())
            .fontSize(13)
            .fontColor(this.theme.textPrimary)
          Blank()
          Toggle({ type: ToggleType.Switch, isOn: this.autoNext })
            .selectedColor(this.theme.accent)
            .onChange((isOn: boolean) => {
              this.autoNext = isOn;
              this.saveSettings();
            })
        }
        .width('100%')
        .padding({ top: 4, bottom: 4 })

        Blank().height(10)

        }

        if (this.settingsGroup === 3) {
        this.SectionTitle('局域网拉书')

        Text('电脑上点「启动共享」，把显示的地址填这里（同一 Wi-Fi 或电脑连手机热点）')
          .fontFamily(this.uiFont())
          .fontSize(11)
          .fontColor(this.theme.textSecondary)
          .margin({ bottom: 4 })

        Row() {
          TextInput({ text: this.shareUrl, placeholder: 'http://192.168.43.1:8765' })
            .layoutWeight(1)
            .height(38)
            .fontSize(13)
            .fontColor(this.theme.textPrimary)
            .backgroundColor(this.theme.panelBackground)
            .onChange((value: string) => {
              this.shareUrl = value;
            })

          Button(this.shareBusy ? '…' : '连接')
            .height(38)
            .width(72)
            .fontSize(13)
            .margin({ left: 8 })
            .backgroundColor(this.theme.accent)
            .fontColor('#FFFFFF')
            .enabled(!this.shareBusy)
            .onClick(() => this.connectShareServer())
        }
        .width('100%')

        if (this.shareStatus.length > 0) {
          Text(this.shareStatus)
            .fontFamily(this.uiFont())
            .fontSize(11)
            .fontColor(this.theme.gold)
            .margin({ top: 6, bottom: 4 })
            .width('100%')
        }

        ForEach(this.shareBooks, (book: RemoteBook) => {
          Row() {
            Column() {
              Text(book.title)
                .fontFamily(this.uiFont())
                .fontSize(13)
                .fontColor(this.theme.textPrimary)
                .maxLines(1)
                .textOverflow({ overflow: TextOverflow.Ellipsis })
              Text(book.chapterCount + ' 章 · ' + Math.round(book.totalSize / 1048576) + ' MB')
                .fontFamily(this.uiFont())
                .fontSize(11)
                .fontColor(this.theme.textSecondary)
                .margin({ top: 2 })
            }
            .layoutWeight(1)
            .alignItems(HorizontalAlign.Start)

            Button('下载')
              .height(32)
              .width(64)
              .fontSize(12)
              .backgroundColor(this.theme.panelBackground)
              .fontColor(this.theme.textPrimary)
              .enabled(!this.shareBusy)
              .onClick(() => this.downloadRemoteBook(book))
          }
          .width('100%')
          .padding({ top: 8, bottom: 8 })
          .border({ width: { bottom: 0.5 }, color: this.theme.borderSoft })
        }, (book: RemoteBook) => book.index.toString())

        Blank().height(6)
        this.SectionTitle('媒体库')

        this.SectionTitle('媒体库')

        Row() {
          Text('书本存放位置')
          .fontFamily(this.uiFont())
            .fontSize(13)
            .fontColor(this.theme.textPrimary)
          Blank()
          Text('App 内部目录（导入的书）')
          .fontFamily(this.uiFont())
            .fontSize(11)
            .fontColor(this.theme.textSecondary)
            .maxLines(1)
        }
        .width('100%')
        .padding({ top: 6, bottom: 6 })

        Button('选择书库文件夹')
          .width('100%')
          .height(40)
          .margin({ top: 8, bottom: 6 })
          .backgroundColor(this.theme.accent)
          .fontColor(Color.White)
          .onClick(() => this.chooseLibraryFolder())


        Button('重新扫描')
          .width('100%')
          .height(36)
          .margin({ top: 0, bottom: 6 })
          .backgroundColor(this.theme.panelBackground)
          .fontColor(this.theme.textPrimary)
          .onClick(() => this.rescanSandbox())

        Text('选一次文件夹就会同步进 App，之后书架直接可用；源里新增/删除书后再点一次即可。')
        .fontFamily(this.uiFont())
          .fontSize(11)
          .fontColor(this.theme.textSecondary)
          .width('100%')
          .margin({ bottom: 6 })
        Blank().height(10)

        this.SectionTitle('数据')

        Button('清除播放记录')
          .width('100%')
          .height(36)
          .margin({ bottom: 6 })
          .backgroundColor(this.theme.panelBackground)
          .fontColor(this.theme.textPrimary)
          .onClick(() => this.clearPlaybackData())

        Button('恢复默认设置')
          .width('100%')
          .height(36)
          .margin({ bottom: 6 })
          .backgroundColor(this.theme.panelBackground)
          .fontColor(this.theme.textPrimary)
          .onClick(() => this.resetSettings())

        this.SectionTitle('状态')

        Row() {
          Text('实况窗（灵动胶囊）')
          .fontFamily(this.uiFont())
            .fontSize(12)
            .fontColor(this.theme.textPrimary)
          Blank()
          Text(this.liveViewAvailable ? '可用' : '未开通（需在华为后台申请实况窗权限）')
          .fontFamily(this.uiFont())
            .fontSize(11)
            .fontColor(this.liveViewAvailable ? this.theme.gold : this.theme.textSecondary)
        }
        .width('100%')
        .padding({ top: 2, bottom: 6 })

        Text(this.statusText)
        .fontFamily(this.uiFont())
          .fontSize(11)
          .fontColor(this.theme.textSecondary)
          .width('100%')
          .lineHeight(18)
        }
      }
      .width('100%')
      .padding({ left: 16, right: 16, top: 8, bottom: 20 })
      .alignItems(HorizontalAlign.Start)
    }
    .width('100%')
    .height('100%')
    .scrollBar(BarState.Auto)
  }

  /** 设置页分组切换条 */
  @Builder
  SettingsTabs() {
    Row() {
      ForEach(['外观', '字幕', '播放', '其他'], (label: string, index: number) => {
        Text(label)
          .fontFamily(this.uiFont())
          .fontSize(13)
          .layoutWeight(1)
          .textAlign(TextAlign.Center)
          .padding({ top: 8, bottom: 8 })
          .borderRadius(8)
          .backgroundColor(this.settingsGroup === index ? this.theme.accent : this.theme.panelBackground)
          .fontColor(this.settingsGroup === index ? '#FFFFFF' : this.theme.textPrimary)
          .onClick(() => {
            this.settingsGroup = index;
          })
      }, (label: string) => label)
    }
    .width('100%')
    .padding({ top: 6, bottom: 10 })
  }


  @Builder
  SectionTitle(text: string) {
    Text(text)
    .fontFamily(this.uiFont())
      .fontSize(12)
      .fontColor(this.theme.gold)
      .margin({ top: 10, bottom: 4 })
  }

  // ==================== 逻辑：书库 ====================

  private async selectBook(book: Book): Promise<void> {
    this.selectedBook = book;
    const first = book.firstPlayable();
    if (first === null) {
      return;
    }

    await this.openChapter(first);
    this.currentTab = 1;
  }

  // ==================== 逻辑：播放 ====================

  private async openChapter(chapter: Chapter, autoPlay: boolean = true): Promise<void> {
    // 连点章节：300ms 内的重复请求直接忽略。
    // 之前会并发创建多个 AVPlayer，旧的不一定 release 完，既卡又容易崩。
    const now = Date.now();
    if (now < this.chapterSwitchLockUntil) {
      hilog.info(0x0000, Index.TAG, 'chapter switch ignored (debounce)');
      return;
    }
    this.chapterSwitchLockUntil = now + 300;

    // 切章序号：异步回来后发现序号变了就丢弃这次结果，避免串台
    const seq = ++this.chapterOpenSeq;

    this.selectedChapter = chapter;
    this.currentText = '';
    this.secondaryText = '';
    this.currentIndex = -1;
    this.lines = [];
    this.secondaryLines = [];
    this.lineSource.setLines([]);
    this.synchronizer.clear();
    this.secondarySynchronizer.clear();
    this.positionMs = 0;
    this.durationMs = 0;
    this.stopTicker();

    if (chapter.hasSubtitle) {
      this.loadSubtitle(chapter.subtitlePath);
    }
    if (chapter.secondarySubtitlePath.length > 0) {
      this.loadSecondarySubtitle(chapter.secondarySubtitlePath);
    }

    if (seq !== this.chapterOpenSeq) {
      return;
    }

    // 读音频内部章节表（m4b 的 chpl）：有缓存，同一份文件切章不再重复读 8MB
    this.embeddedChapters = Mp4Chapters.read(chapter.audioPath);
    this.embeddedIndex = -1;
    hilog.info(0x0000, Index.TAG, 'embedded chapters: %{public}d', this.embeddedChapters.length);

    this.m4bFallbackDone = false;
    const resumeAt = this.loadProgress(chapter);

    try {
      await this.releasePlayer();

      if (seq !== this.chapterOpenSeq) {
        hilog.info(0x0000, Index.TAG, 'chapter open superseded, abort');
        return;
      }

      const player = await media.createAVPlayer();
      this.player = player;


      player.on('stateChange', (state: string) => {
        // 已被换掉/释放的播放器，忽略它的回调。
        // 原生回调里抛未捕获异常会导致 cppcrash，所以整个函数体都包 try/catch。
        if (player !== this.player) {
          return;
        }
        try {
          this.handlePlayerState(player, state, chapter, resumeAt, autoPlay);
        } catch (handlerError) {
          hilog.error(0x0000, Index.TAG, 'stateChange handler failed: %{public}s',
            JSON.stringify(handlerError));
        }
      });

      player.on('timeUpdate', (time: number) => {
        if (player !== this.player) {
          return;
        }
        try {
          this.onPlaybackTick(time);
        } catch (tickError) {
          hilog.error(0x0000, Index.TAG, 'timeUpdate handler failed: %{public}s',
            JSON.stringify(tickError));
        }
      });

      player.on('error', (error: BusinessError) => {
        if (player !== this.player) {
          return;
        }
        try {
          this.isPlaying = false;
          this.stopTicker();
          hilog.error(0x0000, Index.TAG, 'player error: %{public}s', JSON.stringify(error));

          // .m4b 走数据源如果被系统拒绝，自动改名成 .m4a 再试一次
          //（保留 m4b 与"一定能播"两个目标都满足，且只降级一次）
          if (chapter.audioPath.toLowerCase().endsWith('.m4b') && !this.m4bFallbackDone) {
            this.m4bFallbackDone = true;
            const renamed = this.renameToM4a(chapter.audioPath);
            if (renamed.length > 0) {
              chapter.audioPath = renamed;
              this.statusText = '播放器不认这个 .m4b，已自动用 .m4a 副本重试';
              this.openChapter(chapter, true);
              return;
            }
          }

          this.statusText = '播放失败：' + error.message;
        } catch (handlerError) {
          hilog.error(0x0000, Index.TAG, 'error handler failed: %{public}s',
            JSON.stringify(handlerError));
        }
      });

      // 用官方的 fdSrc 打开音频（不涉及任何 JS 回调）。
      //
      // 之前用 AVDataSrcDescriptor 的数据源回调，虽然能绕过 .m4b 扩展名限制，
      // 但真机上会闪退：崩溃栈是
      //   libmedia_avplayer → napi_call_function → panda::JSValueRef::IsFunction → SIGSEGV
      // 也就是原生线程回调一个已经失效的 JS 函数句柄。10 次崩溃全是这个签名。
      // 现在改成：同步时把 .m4b 落盘成 .m4a，播放走 fdSrc，彻底不碰 JS 回调。
      const file = fileIo.openSync(chapter.audioPath, fileIo.OpenMode.READ_ONLY);
      const stat = fileIo.statSync(file.fd);
      this.currentAudioFile = file;
      hilog.info(0x0000, Index.TAG, 'open audio: %{public}s (%{public}d bytes)',
        chapter.audioPath, stat.size);

      if (chapter.audioPath.toLowerCase().endsWith('.m4b')) {
        // 保留 .m4b 原名：AVPlayer 按扩展名不认它，只能用数据源喂字节流。
        // 关键是把 descriptor 挂到字段上长期持有，否则 GC 后原生回调会崩（cppcrash）。
        const fd = file.fd;
        const descriptor: media.AVDataSrcDescriptor = {
          fileSize: stat.size,
          callback: (buffer: ArrayBuffer, length: number, pos?: number): number => {
            const offset = pos === undefined ? 0 : pos;
            try {
              return fileIo.readSync(fd, buffer, { offset: offset, length: length });
            } catch (readError) {
              return -2;
            }
          }
        };
        this.keepAliveDataSource = descriptor;
        const source = media.createMediaSourceWithDataSource(descriptor);
        if (source === undefined) {
          this.statusText = '无法创建音频数据源（.m4b）';
          return;
        }
        hilog.info(0x0000, Index.TAG, 'open .m4b via data source: %{public}s', chapter.title);
        await player.setMediaSource(source);
      } else {
        player.fdSrc = { fd: file.fd, offset: 0, length: stat.size };
      }
    } catch (error) {
      const err = error as BusinessError;
      this.statusText = `打开音频失败：${err.message}`;
      hilog.error(0x0000, Index.TAG, 'open failed: %{public}s', JSON.stringify(err));
    }
  }

  /** 播放器状态机（从 stateChange 回调里抽出来，便于统一 try/catch） */
  private handlePlayerState(
    player: media.AVPlayer,
    state: string,
    chapter: Chapter,
    resumeAt: number,
    autoPlay: boolean
  ): void {
    hilog.info(0x0000, Index.TAG, 'state: %{public}s', state);

    if (state === 'initialized') {
      player.prepare();
      return;
    }

    if (state === 'prepared') {
      this.durationMs = player.duration;
      hilog.info(0x0000, Index.TAG, 'prepared: duration=%{public}d resumeAt=%{public}d',
        this.durationMs, resumeAt);
      this.saveDuration(chapter, this.durationMs);

      if (resumeAt > 0 && resumeAt < this.durationMs - 3000) {
        player.seek(resumeAt, media.SeekMode.SEEK_CLOSEST);
        this.positionMs = resumeAt;
        this.synchronizer.seek(resumeAt);
        this.applyCurrentLine();
      }

      player.setVolume(this.volume);
      if (autoPlay) {
        player.play();
      }

      this.startMediaSession();
      this.saveLastPlayed(chapter);
      this.setupNowPlayingNotification(chapter);

      const liveBook = this.selectedBook;
      if (liveBook !== null) {
        this.liveSubtitle.start(liveBook.title, chapter.title, this.currentText);
      }
      return;
    }

    if (state === 'playing') {
      this.isPlaying = true;
      this.startTicker();
      this.startBackgroundPlayback();
      return;
    }

    if (state === 'paused') {
      this.isPlaying = false;
      this.stopTicker();
      this.saveProgress(false);
      this.syncMediaSession(true);
      this.stopBackgroundPlayback();
      return;
    }

    if (state === 'completed') {
      this.isPlaying = false;
      this.stopTicker();
      this.stopBackgroundPlayback();
      this.onChapterCompleted();
    }
  }

  /** 把 .m4b 副本改名成 .m4a，返回新路径（失败返回空） */
  private renameToM4a(path: string): string {
    try {
      const target = path.substring(0, path.length - 4) + '.m4a';
      fileIo.renameSync(path, target);
      hilog.info(0x0000, Index.TAG, 'renamed to .m4a: %{public}s', target);
      return target;
    } catch (error) {
      hilog.warn(0x0000, Index.TAG, 'rename to .m4a failed: %{public}s', JSON.stringify(error));
      return '';
    }
  }

  /** 记住最后听的章节（下次启动直接回到这里） */
  private saveLastPlayed(chapter: Chapter): void {
    try {
      const context = this.getUIContext().getHostContext() as common.UIAbilityContext;
      const store = preferences.getPreferencesSync(context, { name: 'kataru_settings' });
      store.putSync('last_audio', chapter.audioPath);
      store.flush();
    } catch (error) {
      hilog.warn(0x0000, Index.TAG, 'save last played failed: %{public}s', JSON.stringify(error));
    }
  }

  /** 启动时恢复上次那本书（不自动播放，停在上次的位置） */
  private resumeLastEntry(): void {
    if (!this.resumeLast || this.books.length === 0) {
      return;
    }

    try {
      const context = this.getUIContext().getHostContext() as common.UIAbilityContext;
      const store = preferences.getPreferencesSync(context, { name: 'kataru_settings' });
      const lastAudio = store.getSync('last_audio', '') as string;
      if (lastAudio.length === 0) {
        return;
      }

      for (const book of this.books) {
        for (const chapter of book.chapters) {
          if (chapter.audioPath === lastAudio) {
            hilog.info(0x0000, Index.TAG, 'resume last: %{public}s / %{public}s',
              book.title, chapter.title);
            this.selectedBook = book;
            this.openChapter(chapter, false);
            return;
          }
        }
      }
      hilog.info(0x0000, Index.TAG, 'resume last: not found (%{public}s)', lastAudio);
    } catch (error) {
      hilog.warn(0x0000, Index.TAG, 'resume last failed: %{public}s', JSON.stringify(error));
    }
  }

  /** 一章播完：自动下一章（和桌面版一致） */
  private onChapterCompleted(): void {
    this.saveProgress(false);
    if (!this.autoNext) {
      hilog.info(0x0000, Index.TAG, 'completed, autoNext off');
      return;
    }

    const book = this.selectedBook;
    const chapter = this.selectedChapter;
    if (book === null || chapter === null) {
      return;
    }

    const index = this.chapterIndexOf(chapter);
    if (index < 0 || index + 1 >= book.chapters.length) {
      hilog.info(0x0000, Index.TAG, 'completed, no next chapter');
      return;
    }

    hilog.info(0x0000, Index.TAG, 'completed, auto open next chapter %{public}d', index + 1);
    this.openChapter(book.chapters[index + 1]);
  }

  /** 当前章在书里的序号 */
  private chapterIndexOf(chapter: Chapter): number {
    const book = this.selectedBook;
    if (book === null) {
      return -1;
    }
    for (let i = 0; i < book.chapters.length; i++) {
      if (book.chapters[i].audioPath === chapter.audioPath) {
        return i;
      }
    }
    return -1;
  }

  // ==================== 进度记忆 ====================

  private progressKey(chapter: Chapter): string {
    return 'pos_' + chapter.audioPath;
  }

  /** 读上次听到的位置（毫秒） */
  private loadProgress(chapter: Chapter): number {
    try {
      const context = this.getUIContext().getHostContext() as common.UIAbilityContext;
      const store = preferences.getPreferencesSync(context, { name: 'kataru_settings' });
      const value = store.getSync(this.progressKey(chapter), 0) as number;
      hilog.info(0x0000, Index.TAG, 'load progress: %{public}d ms', value);
      return value;
    } catch (error) {
      hilog.warn(0x0000, Index.TAG, 'load progress failed: %{public}s', JSON.stringify(error));
      return 0;
    }
  }

  /** 记住章节时长（用来算书架上的进度条） */
  private saveDuration(chapter: Chapter, durationMs: number): void {
    if (durationMs <= 0) {
      return;
    }
    try {
      const context = this.getUIContext().getHostContext() as common.UIAbilityContext;
      const store = preferences.getPreferencesSync(context, { name: 'kataru_settings' });
      store.putSync('dur_' + chapter.audioPath, durationMs);
    } catch (error) {
      // 忽略
    }
  }

  private loadDuration(chapter: Chapter): number {
    try {
      const context = this.getUIContext().getHostContext() as common.UIAbilityContext;
      const store = preferences.getPreferencesSync(context, { name: 'kataru_settings' });
      return store.getSync('dur_' + chapter.audioPath, 0) as number;
    } catch (error) {
      return 0;
    }
  }

  private loadPosition(chapter: Chapter): number {
    try {
      const context = this.getUIContext().getHostContext() as common.UIAbilityContext;
      const store = preferences.getPreferencesSync(context, { name: 'kataru_settings' });
      return store.getSync('pos_' + chapter.audioPath, 0) as number;
    } catch (error) {
      return 0;
    }
  }

  /** 一本书的收听进度 0..1（按有记录的章节累加） */
  private bookProgress(book: Book): number {
    let listened = 0;
    let total = 0;
    for (const chapter of book.chapters) {
      const duration = this.loadDuration(chapter);
      if (duration <= 0) {
        continue;
      }
      total += duration;
      listened += Math.min(this.loadPosition(chapter), duration);
    }
    if (total <= 0) {
      return 0;
    }
    return listened / total;
  }

  private bookProgressText(book: Book): string {
    const percent = this.bookProgress(book);
    if (percent <= 0) {
      return '';
    }
    return Math.round(percent * 100) + '%';
  }

  /** 全部书的总体进度 */
  private overallProgress(): number {
    if (this.books.length === 0) {
      return 0;
    }
    let sum = 0;
    let counted = 0;
    for (const book of this.books) {
      const percent = this.bookProgress(book);
      if (percent > 0) {
        sum += percent;
        counted++;
      }
    }
    return counted === 0 ? 0 : sum / counted;
  }

  /** 存当前位置；force=false 时最多 5 秒存一次，避免刷盘太频繁 */
  private saveProgress(force: boolean): void {
    const chapter = this.selectedChapter;
    if (chapter === null) {
      return;
    }

    const now = Date.now();
    if (!force && now - this.lastProgressSaveMs < 5000) {
      return;
    }
    this.lastProgressSaveMs = now;

    try {
      const context = this.getUIContext().getHostContext() as common.UIAbilityContext;
      const store = preferences.getPreferencesSync(context, { name: 'kataru_settings' });
      store.putSync(this.progressKey(chapter), this.positionMs);
      store.flush();
    } catch (error) {
      hilog.warn(0x0000, Index.TAG, 'save progress failed: %{public}s', JSON.stringify(error));
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
      player.off('timeUpdate');
      await player.release();
      // release 完成后再丢数据源引用，避免原生还在回调时 JS 句柄消失（cppcrash）
      this.keepAliveDataSource = null;
    } catch (error) {
      hilog.warn(0x0000, Index.TAG, 'release failed: %{public}s', JSON.stringify(error));
      this.keepAliveDataSource = null;
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

  /** 书架的列模板 */
  private libraryColumnsTemplate(): string {
    if (this.libraryColumns <= 2) {
      return '1fr 1fr';
    }
    if (this.libraryColumns >= 4) {
      return '1fr 1fr 1fr 1fr';
    }
    return '1fr 1fr 1fr';
  }

  /** 面板色的半透明版本（'#AARRGGBB' 风格），让浮层贴合背景 */
  private translucentPanel(alphaHex: string): string {
    const hex = this.theme.panelBackground;
    if (hex.length === 7) {
      return '#' + alphaHex + hex.substring(1);
    }
    return hex;
  }

  /** 当前句高亮色：浅色背景用淡黑，深色背景用金色 */
  private currentLineTint(): string {
    const hsl = ColorUtil.hexToHsl(this.theme.pageBackground);
    return hsl.l > 60 ? '#14000000' : '#22C9A227';
  }

  /** 连电脑的共享服务，取回书单 */
  private async connectShareServer(): Promise<void> {
    const base = this.shareUrl.trim().replace(/\/+$/, '');
    if (base.length === 0) {
      this.shareStatus = '先填电脑上显示的地址';
      return;
    }

    this.shareBusy = true;
    this.shareStatus = '连接中…';
    try {
      const client = http.createHttp();
      const response = await client.request(base + '/api/library', {
        method: http.RequestMethod.GET,
        connectTimeout: 8000,
        readTimeout: 15000
      });
      client.destroy();

      if (response.responseCode !== 200) {
        this.shareStatus = `连接失败：HTTP ${response.responseCode}`;
        return;
      }

      const text = response.result as string;
      const parsed = JSON.parse(text) as ShareLibraryResponse;
      const books: RemoteBook[] = [];
      for (const book of parsed.books) {
        books.push(new RemoteBook(book.index, book.title, book.totalSize, book.chapters.length));
      }
      this.shareBooks = books;
      this.shareStatus = `已连接，共 ${books.length} 本`;
      hilog.info(0x0000, Index.TAG, 'share: %{public}d books from %{public}s', books.length, base);
    } catch (error) {
      const err = error as BusinessError;
      this.shareStatus = '连接失败：' + err.message;
      hilog.error(0x0000, Index.TAG, 'share connect failed: %{public}s', JSON.stringify(error));
    } finally {
      this.shareBusy = false;
    }
  }

  /** 从电脑下载一整本书（音频 + 字幕）到沙箱，然后刷新书架 */
  private async downloadRemoteBook(book: RemoteBook): Promise<void> {
    if (this.shareBusy) {
      return;
    }

    const base = this.shareUrl.trim().replace(/\/+$/, '');
    const context = this.getUIContext().getHostContext() as common.UIAbilityContext;
    const safeName = book.title.replace(/[\/\\:*?"<>|]/g, '_');
    const bookDir = context.filesDir + '/books/' + safeName;

    this.shareBusy = true;
    this.shareStatus = `下载《${book.title}》…`;
    try {
      if (!fileIo.accessSync(bookDir)) {
        fileIo.mkdirSync(bookDir, true);
      }

      const chapters = await this.fetchRemoteChapters(base, book.index);
      let done = 0;
      for (const chapter of chapters) {
        const audioTarget = bookDir + '/' + chapter.audioName;
        await this.downloadTo(base, `/api/file?book=${book.index}&chapter=${chapter.index}&kind=audio`, audioTarget);
        done++;
        this.shareStatus = `《${book.title}》 ${done}/${chapters.length} 章`;
      }

      this.shareStatus = `《${book.title}》下载完成，正在刷新书架`;
      this.refreshLibrary();
      this.shareStatus = `《${book.title}》已加入书架`;
      this.shareBooks = this.shareBooks.filter((item: RemoteBook) => item.index !== book.index);
    } catch (error) {
      const err = error as BusinessError;
      this.shareStatus = '下载失败：' + err.message;
      hilog.error(0x0000, Index.TAG, 'share download failed: %{public}s', JSON.stringify(error));
    } finally {
      this.shareBusy = false;
    }
  }

  /** 取某本书的章节明细（含文件名、是否有字幕） */
  private async fetchRemoteChapters(base: string, bookIndex: number): Promise<ShareChapter[]> {
    const client = http.createHttp();
    const response = await client.request(base + '/api/library', {
      method: http.RequestMethod.GET,
      connectTimeout: 8000,
      readTimeout: 15000
    });
    client.destroy();
    const parsed = JSON.parse(response.result as string) as ShareLibraryResponse;
    const target = parsed.books[bookIndex];
    if (target === undefined) {
      throw new Error('书不存在');
    }
    return target.chapters;
  }

  /** 用系统下载管理器把单个文件拉到沙箱（大文件不占 JS 内存） */
  private async downloadTo(base: string, path: string, target: string): Promise<void> {
    const context = this.getUIContext().getHostContext() as common.UIAbilityContext;
    if (fileIo.accessSync(target)) {
      fileIo.unlinkSync(target);
    }
    const task = await request.downloadFile(context, {
      url: base + path,
      filePath: target
    });
    await new Promise<void>((resolve, reject) => {
      task.on('complete', () => resolve());
      task.on('fail', (err: number) => reject(new Error('下载失败 code=' + err)));
    });
  }

  /** 切到阅读页后延迟一点滚动（LazyForEach 首帧还没建好） */
  private scrollReadingToCurrentSoon(): void {
    if (this.currentIndex < 0 || this.currentIndex >= this.lines.length) {
      return;
    }
    const target = this.currentIndex;
    setTimeout(() => {
      this.readingScroller.scrollToIndex(target, false, ScrollAlign.CENTER);
    }, 80);
  }

  /** 阅读页回到当前正在播的那一句 */
  private jumpToCurrentLine(): void {
    if (this.currentIndex < 0 || this.currentIndex >= this.lines.length) {
      this.statusText = '还没开始播放，没有可跳的位置';
      return;
    }
    this.readingScroller.scrollToIndex(this.currentIndex, false, ScrollAlign.CENTER);
  }

  private skip(deltaMs: number): void {
    this.seekTo(this.positionMs + deltaMs);
  }

  private seekTo(positionMs: number): void {
    const player = this.player;
    if (player === null) {
      return;
    }

    // 连点进度条/章节胶囊先去抖：12 小时的单文件连发 seek 会把播放器拖死
    const now = Date.now();
    if (now < this.seekLockUntil) {
      hilog.info(0x0000, Index.TAG, 'seek ignored (debounce)');
      return;
    }
    this.seekLockUntil = now + 250;

    let target = Math.max(0, positionMs);
    if (this.durationMs > 0) {
      target = Math.min(target, this.durationMs);
    }

    try {
      // SEEK_PREVIOUS_SYNC 跳到前一个关键帧，比 SEEK_CLOSEST 快很多（长音频尤其明显）
      player.seek(target, media.SeekMode.SEEK_PREV_SYNC);
      this.positionMs = target;
      this.synchronizer.seek(target);
      this.applyCurrentLine();
    } catch (error) {
      hilog.warn(0x0000, Index.TAG, 'seek failed: %{public}s', JSON.stringify(error));
    }
  }

  // ==================== 逻辑：字幕 ====================

  /**
   * 找某条主字幕对应的译文。
   *
   * 不能用"时间区间重叠"：两份 SRT 的断句时间不完全一致，
   * 重叠匹配会拿到上一句（实测 0:16「神原骏河」配到了 0:14 的「001」）。
   * 改成按**开始时间最接近**匹配，超过 2.5 秒才认为没有对应译文。
   */
  private secondaryTextAt(startMs: number): string {
    let best = '';
    let bestDistance = 2500;

    for (const line of this.secondaryLines) {
      const distance = Math.abs(line.startMs - startMs);
      if (distance < bestDistance) {
        bestDistance = distance;
        best = line.text;
      }
    }

    return best;
  }

  /**
   * 用 fd 读字幕文本。
   *
   * 不能用 fileIo.readTextSync(uri)：对选择器返回的 URI 它会报 13900002，
   * 而同一个 URI 用 openSync 却完全正常（音频就是这么打开的）。
   * 所以统一走 openSync → readSync → UTF-8 解码。
   */
  private readTextByUri(uri: string): string {
    const file = fileIo.openSync(uri, fileIo.OpenMode.READ_ONLY);
    try {
      const stat = fileIo.statSync(file.fd);
      const buffer = new ArrayBuffer(stat.size);
      const read = fileIo.readSync(file.fd, buffer);
      const bytes = new Uint8Array(buffer, 0, read);
      const decoder = util.TextDecoder.create('utf-8', { ignoreBOM: true });
      return decoder.decodeToString(bytes);
    } finally {
      fileIo.closeSync(file);
    }
  }

  private loadSubtitle(path: string): void {
    try {
      // 多条目缓存：来回切章不用反复解析上万条字幕
      let lines: SubtitleLine[] = [];
      const cachedLines = this.subtitleCache.get(path);
      if (cachedLines !== undefined && cachedLines.length > 0) {
        lines = cachedLines;
        hilog.info(0x0000, Index.TAG, 'subtitle cache hit: %{public}d lines', lines.length);
      } else {
        const parseStart = Date.now();
        const content = this.readTextByUri(path);
        const warnings: string[] = [];
        lines = SrtParser.parse(content, warnings);
        if (this.subtitleCache.size >= 4) {
          this.subtitleCache.clear();
        }
        this.subtitleCache.set(path, lines);
        hilog.info(0x0000, Index.TAG, 'subtitle parse: %{public}d chars -> %{public}d lines in %{public}d ms',
          content.length, lines.length, Date.now() - parseStart);
      }

      this.synchronizer.load(lines);
      this.lines = lines;
      // LazyForEach 的数据源必须同步，否则阅读页是空的
      this.lineSource.setLines(lines);
      this.currentIndex = -1;
      this.currentText = '';

      const name = path.substring(path.lastIndexOf('/') + 1);
      this.statusText = '字幕：' + name + '（' + lines.length + ' 条）';
      hilog.info(0x0000, Index.TAG, 'subtitle loaded: %{public}s (%{public}d lines)',
        name, lines.length);

      this.synchronizer.seek(this.positionMs);
      this.applyCurrentLine();
    } catch (error) {
      const err = error as BusinessError;
      this.statusText = '读字幕失败：' + err.message;
      hilog.error(0x0000, Index.TAG, 'load subtitle failed: uri=%{public}s %{public}s',
        path, JSON.stringify(err));
    }
  }

  /** 读对照字幕（双语） */
  private loadSecondarySubtitle(path: string): void {
    try {
      const content = this.readTextByUri(path);
      const warnings: string[] = [];
      const lines = SrtParser.parse(content, warnings);

      this.secondarySynchronizer.load(lines);
      this.secondaryLines = lines;
      hilog.info(0x0000, Index.TAG, 'secondary subtitle loaded: %{public}d lines', lines.length);

      this.secondarySynchronizer.seek(this.positionMs);
      this.applyCurrentLine();
    } catch (error) {
      hilog.warn(0x0000, Index.TAG, 'load secondary subtitle failed: %{public}s', JSON.stringify(error));
    }
  }

  private applyCurrentLine(): void {
    const current = this.synchronizer.current;
    const text = current === null ? '' : current.text;

    if (this.currentText !== text) {
      this.currentText = text;
    }

    // 双语：对照字幕按同一时间点匹配
    const secondary = this.secondarySynchronizer.current;
    const secondText = secondary === null ? '' : secondary.text;
    if (this.secondaryText !== secondText) {
      this.secondaryText = secondText;
    }

    const index = this.synchronizer.currentIndex;
    if (this.currentIndex !== index) {
      const previous = this.currentIndex;
      this.currentIndex = index;
      hilog.info(0x0000, Index.TAG, 'line -> %{public}d @ %{public}dms: %{public}s',
        index, this.positionMs, text.length > 40 ? text.substring(0, 40) : text);
      // LazyForEach 不随状态自动重画：手动刷新旧行与新行
      this.lineSource.notifyChanged(previous);
      this.lineSource.notifyChanged(index);
      // 只有阅读页在前台时才滚动（对 1 万条列表滚动很贵，播放页不需要）
      if (index >= 0 && this.currentTab === 2) {
        this.readingScroller.scrollToIndex(index, false, ScrollAlign.CENTER);
      }
    }
  }

  private startTicker(): void {
    if (this.tickTimer !== -1) {
      return;
    }

    this.tickTimer = setInterval(() => {
      const player = this.player;
      if (player === null) {
        return;
      }

      this.onPlaybackTick(player.currentTime);
    }, 200);
  }

  /**
   * 统一的"播放进度刷新"。
   *
   * 前台由 200ms 定时器驱动，后台由播放器原生 timeUpdate 驱动 ——
   * App 退后台时 JS 定时器会被冻结，但原生事件还会来，
   * 播放状态/字幕才能继续更新（否则系统判定没在播、收回长时任务）。
   */
  private onPlaybackTick(currentMs: number): void {
    if (currentMs < 0) {
      return;
    }

    // 睡眠定时：到点自动暂停
    this.checkSleepTimer();

    this.positionMs = currentMs;
    this.synchronizer.update(currentMs);
    this.secondarySynchronizer.update(currentMs);
    this.applyCurrentLine();
    this.saveProgress(false);
    this.syncMediaSession(false);
    this.syncLiveSubtitle();
    this.syncNowPlaying();
    this.updateEmbeddedIndex();
  }

  /** 申请通知权限并发第一条通知（长时任务的必要配套 + 通知栏字幕） */
  private async setupNowPlayingNotification(chapter: Chapter): Promise<void> {
    // 默认关：不能在用户没准备的时候弹系统权限框
    if (!this.showNotification) {
      return;
    }

    const context = this.getUIContext().getHostContext() as common.UIAbilityContext;
    const ok = await this.nowPlaying.ensurePermission(context);
    if (!ok) {
      hilog.warn(0x0000, Index.TAG, 'notification not enabled, background may be killed');
      return;
    }

    const book = this.selectedBook;
    await this.nowPlaying.update(
      context,
      book === null ? 'KATARU' : book.title,
      chapter.title,
      this.currentText,
      this.isPlaying
    );
  }

  /** 字幕变化时更新通知栏文字 */
  private syncNowPlaying(): void {
    if (!this.showNotification) {
      return;
    }
    const book = this.selectedBook;
    const chapter = this.selectedChapter;
    if (book === null || chapter === null) {
      return;
    }
    const context = this.getUIContext().getHostContext() as common.UIAbilityContext;
    this.nowPlaying.update(context, book.title, chapter.title, this.currentText, this.isPlaying);
  }

  /** 字幕变了就更新实况窗胶囊（全在别的 App 上也能看到这句） */
  private syncLiveSubtitle(): void {
    const book = this.selectedBook;
    const chapter = this.selectedChapter;
    if (book === null || chapter === null) {
      return;
    }
    this.liveSubtitle.update(book.title, chapter.title, this.currentText);
  }

  // ==================== 封面 ====================

  /** 换封面：选一张图，存成书本目录下的 cover.png */
  private async chooseCoverImage(): Promise<void> {
    const book = this.selectedBook;
    if (book === null) {
      return;
    }

    try {
      const options = new picker.DocumentSelectOptions();
      options.maxSelectNumber = 1;
      options.fileSuffixFilters = ['图片|.png,.jpg,.jpeg,.webp'];

      const pickerInstance = new picker.DocumentViewPicker(this.getUIContext().getHostContext() as common.UIAbilityContext);
      const uris: string[] = await pickerInstance.select(options);
      if (uris === null || uris.length === 0) {
        return;
      }

      const ext = BookStore.extFromUri(uris[0]);
      const targetName = ext.length > 0 ? ('cover' + ext) : 'cover.png';
      const target = `${book.folderPath}/${targetName}`;

      const written = await this.copyFileChunked(uris[0], target);
      hilog.info(0x0000, Index.TAG, 'cover replaced: %{public}s (%{public}d bytes)', target, written);

      // 顺带删除旧格式的 cover，避免两套并存
      for (const other of ['cover.png', 'cover.jpg', 'cover.jpeg', 'cover.webp']) {
        const path = `${book.folderPath}/${other}`;
        if (path !== target && fileIo.accessSync(path)) {
          try {
            fileIo.unlinkSync(path);
          } catch (error) {
            // 忽略
          }
        }
      }

      this.refreshLibrary();
      this.statusText = '封面已更新';
    } catch (error) {
      this.statusText = '换封面失败：' + JSON.stringify(error);
      hilog.error(0x0000, Index.TAG, 'choose cover failed: %{public}s', JSON.stringify(error));
    }
  }

  /** 恢复自动封面：删掉自定义 cover，回到同名图/第一张图 */
  private clearCoverImage(): void {
    const book = this.selectedBook;
    if (book === null) {
      return;
    }

    let removed = 0;
    for (const name of ['cover.png', 'cover.jpg', 'cover.jpeg', 'cover.webp']) {
      const path = `${book.folderPath}/${name}`;
      try {
        if (fileIo.accessSync(path)) {
          fileIo.unlinkSync(path);
          removed++;
        }
      } catch (error) {
        // 忽略
      }
    }

    this.refreshLibrary();
    this.statusText = removed > 0 ? '已恢复自动封面' : '没有自定义封面';
  }

  // ==================== 数据管理 ====================

  /** 清除所有播放位置记录 */
  private clearPlaybackData(): void {
    try {
      const context = this.getUIContext().getHostContext() as common.UIAbilityContext;
      const store = preferences.getPreferencesSync(context, { name: 'kataru_settings' });
      // ArkTS 不支持 for..in，用 Object.keys 取 key 数组
      const allValues: Object = store.getAllSync();
      const keys: string[] = Object.keys(allValues);
      let cleared = 0;
      for (const key of keys) {
        if (key.startsWith('pos_')) {
          store.deleteSync(key);
          cleared++;
        }
      }
      store.putSync('last_audio', '');
      store.flush();
      this.positionMs = 0;
      this.statusText = '已清除 ' + cleared + ' 条播放记录';
      hilog.info(0x0000, Index.TAG, 'cleared %{public}d progress entries', cleared);
    } catch (error) {
      this.statusText = '清除失败：' + JSON.stringify(error);
    }
  }

  /** 恢复默认设置 */
  private resetSettings(): void {
    this.fontSize = 26;
    this.fontFamilyValue = 'HarmonyOS Sans SC';
    this.subtitleColor = '#FFFFFF';
    this.subtitleBold = false;
    this.showSecondary = true;
    this.autoNext = true;
    this.resumeLast = true;
    this.showOnlyWithSubtitle = false;
    this.volume = 1;
    this.skipSeconds = 10;
    this.themeIndex = 0;
    this.theme = THEME_PRESETS[0].colors;
    this.clearCustomTheme();
    this.saveSettings();
    this.statusText = '已恢复默认设置';
  }

  // ==================== 后台播放（长时任务） ====================

  /**
   * 申请"音频播放"长时任务：不申请的话 App 退到后台会被系统冻结，
   * 声音会停（日志里是 FROZEN_AFTER_ENTER_BG）。需要 KEEP_BACKGROUND_RUNNING 权限。
   */
  private async startBackgroundPlayback(): Promise<void> {
    if (this.backgroundTaskActive) {
      return;
    }

    try {
      const context = this.getUIContext().getHostContext() as common.UIAbilityContext;

      if (this.backgroundWantAgent === null) {
        const wants: Want[] = [{
          bundleName: context.abilityInfo.bundleName,
          abilityName: context.abilityInfo.name,
        }];
        const info: wantAgent.WantAgentInfo = {
          wants: wants,
          actionType: wantAgent.OperationType.START_ABILITY,
          requestCode: 100,
          actionFlags: [wantAgent.WantAgentFlags.UPDATE_PRESENT_FLAG]
        };
        this.backgroundWantAgent = await wantAgent.getWantAgent(info);
      }

      // 依次尝试几种写法：不同 ROM 接受的参数形式不一样，把失败原因都打出来，成功即停
      const submodeCandidates: number[] = [
        backgroundTaskManager.BackgroundTaskSubmode.SUBMODE_NORMAL_NOTIFICATION,
        backgroundTaskManager.BackgroundTaskSubmode.SUBMODE_AUDIO_PLAYBACK_NORMAL_NOTIFICATION,
      ];

      for (const submode of submodeCandidates) {
        try {
          const request = new backgroundTaskManager.ContinuousTaskRequest();
          request.backgroundTaskModes = [backgroundTaskManager.BackgroundTaskMode.MODE_AUDIO_PLAYBACK];
          request.backgroundTaskSubmodes = [submode];
          request.wantAgent = this.backgroundWantAgent;
          await backgroundTaskManager.startBackgroundRunning(context, request);
          this.backgroundTaskActive = true;
          hilog.info(0x0000, Index.TAG, 'background task started (submode=%{public}d)', submode);
          return;
        } catch (error) {
          hilog.warn(0x0000, Index.TAG, 'background submode=%{public}d failed: %{public}s',
            submode, JSON.stringify(error));
        }
      }
    } catch (error) {
      hilog.warn(0x0000, Index.TAG, 'startBackgroundRunning failed: %{public}s', JSON.stringify(error));
    }
  }

  private async stopBackgroundPlayback(): Promise<void> {
    if (!this.backgroundTaskActive) {
      return;
    }
    try {
      const context = this.getUIContext().getHostContext() as common.UIAbilityContext;
      await backgroundTaskManager.stopBackgroundRunning(context);
      this.backgroundTaskActive = false;
      hilog.info(0x0000, Index.TAG, 'background task stopped');
    } catch (error) {
      hilog.warn(0x0000, Index.TAG, 'stopBackgroundRunning failed: %{public}s', JSON.stringify(error));
    }
  }

  // ==================== 系统播控（后台播放 / 锁屏字幕） ====================

  /** 启动媒体会话，并把系统的播放/暂停/切章/拖动接回来 */
  private async startMediaSession(): Promise<void> {
    const context = this.getUIContext().getHostContext() as common.UIAbilityContext;
    await this.mediaSession.start(
      context,
      () => this.resumeFromSystem(),
      () => this.pauseFromSystem(),
      () => this.playNextChapter(),
      () => this.playPreviousChapter(),
      (positionMs: number) => this.seekTo(positionMs)
    );
    await this.mediaSession.setLaunchAbility(context);
    this.syncMediaSession(true);
  }

  /** 把当前书本/章节/字幕/进度推到播控中心（锁屏就能看到字幕） */
  private syncMediaSession(force: boolean): void {
    const book = this.selectedBook;
    const chapter = this.selectedChapter;
    if (book === null || chapter === null) {
      return;
    }

    this.mediaSession.update(
      book.title,
      chapter.title,
      this.currentText,
      this.positionMs,
      this.durationMs,
      this.isPlaying,
      this.coverFor(book.coverPath) === undefined ? null : this.coverFor(book.coverPath)
    );
  }

  private resumeFromSystem(): void {
    const player = this.player;
    if (player === null) {
      return;
    }
    player.play();
  }

  private pauseFromSystem(): void {
    const player = this.player;
    if (player === null) {
      return;
    }
    player.pause();
    this.saveProgress(true);
  }

  private playNextChapter(): void {
    const book = this.selectedBook;
    const chapter = this.selectedChapter;
    if (book === null || chapter === null) {
      return;
    }
    const index = this.chapterIndexOf(chapter);
    if (index >= 0 && index + 1 < book.chapters.length) {
      this.openChapter(book.chapters[index + 1]);
    }
  }

  private playPreviousChapter(): void {
    const book = this.selectedBook;
    const chapter = this.selectedChapter;
    if (book === null || chapter === null) {
      return;
    }
    const index = this.chapterIndexOf(chapter);
    // 听过 3 秒以上就回到本段开头，否则上一章（和桌面版一致）
    if (this.positionMs > 3000) {
      this.seekTo(0);
      return;
    }
    if (index > 0) {
      this.openChapter(book.chapters[index - 1]);
    }
  }

  /** 播放页的"第 N/M 章 · xx%" */
  private currentChapterProgressText(): string {
    const book = this.selectedBook;
    const chapter = this.selectedChapter;
    if (chapter === null) {
      return '';
    }

    let text = chapter.title;
    if (book !== null && book.chapters.length > 1) {
      const index = this.chapterIndexOf(chapter);
      if (index >= 0) {
        text = '第 ' + (index + 1) + '/' + book.chapters.length + ' 章 · ' + chapter.title;
      }
    }

    if (this.durationMs > 0) {
      text += '  ·  ' + Math.round(this.positionMs / this.durationMs * 100) + '%';
    }
    return text;
  }

  /** 按播放位置算当前在哪个内嵌章节 */
  private updateEmbeddedIndex(): void {
    if (this.embeddedChapters.length === 0) {
      return;
    }

    let index = -1;
    for (let i = 0; i < this.embeddedChapters.length; i++) {
      if (this.positionMs >= this.embeddedChapters[i].startMs) {
        index = i;
      } else {
        break;
      }
    }

    if (index !== this.embeddedIndex) {
      this.embeddedIndex = index;
    }
  }

  private stopTicker(): void {
    if (this.tickTimer !== -1) {
      clearInterval(this.tickTimer);
      this.tickTimer = -1;
    }
  }
}
