import { fileIo, picker, fileShare, fileUri } from '@kit.CoreFileKit';
import { preferences } from '@kit.ArkData';
import { abilityAccessCtrl, common, Permissions } from '@kit.AbilityKit';
import { media } from '@kit.MediaKit';
import { BusinessError } from '@kit.BasicServicesKit';
import { hilog } from '@kit.PerformanceAnalysisKit';
import { util } from '@kit.ArkTS';
import { image } from '@kit.ImageKit';
import { Book, Chapter, LibraryScanner } from '../model/Library';
import { BookStore } from '../model/BookStore';
import { MediaSession } from '../model/MediaSession';
import { LiveSubtitle } from '../model/LiveSubtitle';
import { THEME_PRESETS, ThemeColors, ThemePreset, ThemeStore } from '../model/Theme';
import { SubtitleLine, SrtParser } from '../model/SrtParser';
import { SubtitleSynchronizer } from '../model/SubtitleSynchronizer';
import { Timecode } from '../model/Timecode';
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
  private lastProgressSaveMs: number = 0;
  private currentAudioFile: fileIo.File | null = null;
  private mediaSession: MediaSession = new MediaSession();
  private liveSubtitle: LiveSubtitle = new LiveSubtitle();
  @State liveViewAvailable: boolean = false;
  @State themeIndex: number = 0;
  @State theme: ThemeColors = THEME_PRESETS[0].colors;
  @State searchText: string = '';
  @State showFavoritesOnly: boolean = false;
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
  @State fontChoices: FontChoice[] = [
    new FontChoice('系统默认', 'HarmonyOS Sans SC'),
    new FontChoice('黑体', 'HarmonyOS Sans'),
    new FontChoice('思源宋体', 'KataruSerif'),
    new FontChoice('衬线', 'serif'),
    new FontChoice('等宽', 'monospace'),
  ];

  aboutToAppear(): void {
    this.registerBundledFonts();
    this.loadSettings();
    this.loadFavorites();

    // 书库只认"用户选过的文件夹"，不再往沙箱复制。
    // 授权会在重装 App 后失效，那时提示用户重新选一次。
    const saved = this.loadSavedRoot();
    if (saved.length > 0) {
      this.libraryRoot = saved;
      // 重新激活持久授权（不调用的话重启后权限会失效）
      this.reactivate(saved);
      this.refreshLibrary();
    } else {
      this.libraryRoot = '';
      this.books = [];
      this.covers = [];
      this.statusText = '还没有选书库文件夹 —— 到设置页点「选择书库文件夹」，选到放书的那一层';
      hilog.info(0x0000, Index.TAG, 'no library folder yet');
    }

    // 探测实况窗（灵动胶囊）能力：没审批通过会返回 false
    this.liveSubtitle.isEnabled().then((ok: boolean) => {
      this.liveViewAvailable = ok;
    });
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
      this.showSecondary = store.getSync('showSecondary', true) as boolean;
      this.autoNext = store.getSync('autoNext', true) as boolean;
      this.volume = store.getSync('volume', 1) as number;
      hilog.info(0x0000, Index.TAG, 'settings loaded: fontSize=%{public}d font=%{public}s',
        this.fontSize, this.fontFamilyValue);
    } catch (error) {
      hilog.warn(0x0000, Index.TAG, 'load settings failed: %{public}s', JSON.stringify(error));
    }
  }

  private saveSettings(): void {
    try {
      const context = this.getUIContext().getHostContext() as common.UIAbilityContext;
      const store = preferences.getPreferencesSync(context, { name: 'kataru_settings' });
      store.putSync('fontSize', this.fontSize);
      store.putSync('fontFamily', this.fontFamilyValue);
      store.putSync('subtitleColor', this.subtitleColor);
      store.putSync('subtitleBold', this.subtitleBold);
      store.putSync('showSecondary', this.showSecondary);
      store.putSync('autoNext', this.autoNext);
      store.putSync('volume', this.volume);
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

  /** 读上次选的书库文件夹；读不出来（授权失效）就返回空 */
  private loadSavedRoot(): string {
    try {
      const context = this.getUIContext().getHostContext() as common.UIAbilityContext;
      const store = preferences.getPreferencesSync(context, { name: 'kataru_settings' });
      const saved = store.getSync('library_uri', '') as string;
      if (saved.length === 0) {
        return '';
      }

      // 存的是 URI，但列目录要用真实路径，所以这里也走一遍解析
      const resolved = this.resolveReadableDir(saved);
      if (resolved.length === 0) {
        hilog.warn(0x0000, Index.TAG, 'saved root unusable (permission lost?): %{public}s', saved);
        return '';
      }

      hilog.info(0x0000, Index.TAG, 'saved root OK: %{public}s', resolved);
      return resolved;
    } catch (error) {
      hilog.warn(0x0000, Index.TAG, 'load saved root failed: %{public}s', JSON.stringify(error));
      return '';
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
      const pixel = await this.loadPixelMap(book.coverPath);
      if (pixel !== undefined) {
        list.push(new CoverItem(book.coverPath, pixel));
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

      // 用真实路径当书库根：这样下面所有的 listFile / openSync / Image 都能直接用
      this.libraryRoot = resolved;
      this.saveRoot(uri);
      this.refreshLibrary();

      if (this.books.length === 0) {
        this.statusText = '这个文件夹里没找到书（每本书应该是一个子文件夹，里面放音频和字幕）';
      } else {
        this.statusText = '书库文件夹已设为：' + resolved;
      }
    } catch (error) {
      const err = error as BusinessError;
      hilog.error(0x0000, Index.TAG, 'choose folder failed: code=%{public}d msg=%{public}s',
        err.code, err.message);

      // 只有真的"能力不存在"才降级；用户取消不会走到这里
      this.statusText = '选文件夹失败（' + err.message + '），已切换成多选文件';
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
    Column() {
      // 顶栏
      Row() {
        Text('KATARU')
          .fontSize(19)
          .fontWeight(FontWeight.Bold)
          .fontColor(this.theme.textPrimary)

        Blank()

        if (this.currentTab === 0) {
        }

        if (this.currentTab === 2 && this.lines.length > 0) {
          Text(`${this.currentIndex + 1} / ${this.lines.length}`)
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
      .onChange((index: number) => {
        this.currentTab = index;
      })
    }
    .width('100%')
    .height('100%')
    .backgroundColor(this.theme.pageBackground)
  }

  @Builder
  TabBarItem(label: string, index: number) {
    Text(label)
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
          Text(this.scanning ? '正在扫描…' : '书架是空的')
            .fontSize(16)
            .fontColor(this.theme.textPrimary)

          Text(`把「一本书一个文件夹」放进这个目录：\n${this.libraryRoot}`)
            .fontSize(12)
            .lineHeight(20)
            .textAlign(TextAlign.Center)
            .fontColor(this.theme.textSecondary)
            .margin({ top: 12, left: 24, right: 24 })


          Text('一次多选这本的：音频 (.m4b/.mp3) + 字幕 (.srt) + 封面 (png)')
            .fontSize(11)
            .fontColor(this.theme.textSecondary)
            .textAlign(TextAlign.Center)
            .margin({ top: 10, left: 24, right: 24 })
        }
        .width('100%')
        .justifyContent(FlexAlign.Center)
        .layoutWeight(1)
      } else {
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

          Text(this.showFavoritesOnly ? '★' : '☆')
            .fontSize(20)
            .width(44)
            .height(34)
            .textAlign(TextAlign.Center)
            .fontColor(this.showFavoritesOnly ? this.theme.gold : this.theme.textSecondary)
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
                      .objectFit(ImageFit.Cover)
                      .borderRadius(6)
                  } else {
                    Column() {
                      Text('📖')
                        .fontSize(30)
                      Text(book.title.substring(0, 2))
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
                  Text(this.isFavorite(book) ? '★' : '☆')
                    .fontSize(18)
                    .fontColor(this.isFavorite(book) ? this.theme.gold : '#CCFFFFFF')
                    .padding(6)
                    .onClick(() => this.toggleFavorite(book))
                }
                .width('100%')

                Text(book.title)
                  .fontSize(13)
                  .fontColor(this.theme.textPrimary)
                  .maxLines(1)
                  .textOverflow({ overflow: TextOverflow.Ellipsis })
                  .margin({ top: 6 })

                Text(`${book.chapterCount} 章`)
                  .fontSize(11)
                  .fontColor(this.theme.textSecondary)
              }
              .width('100%')
              .onClick(() => this.selectBook(book))
            }
          }, (book: Book) => book.folderPath)
        }
        .columnsTemplate('1fr 1fr 1fr')
        .columnsGap(10)
        .rowsGap(14)
        .width('100%')
        .layoutWeight(1)
        .padding({ left: 16, right: 16, bottom: 12 })
      }

      Text(this.statusText)
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
          .width(150)
          .aspectRatio(0.83)
          .objectFit(ImageFit.Cover)
          .borderRadius(8)
          .margin({ top: 8 })
      } else {
        Column() {
          Text('📖').fontSize(36)
        }
        .width(150)
        .aspectRatio(0.83)
        .justifyContent(FlexAlign.Center)
        .backgroundColor(this.theme.panelBackground)
        .borderRadius(8)
        .margin({ top: 8 })
      }

      Text(this.selectedBook === null ? '未选择书' : this.selectedBook.title)
        .fontSize(16)
        .fontWeight(FontWeight.Medium)
        .fontColor(this.theme.textPrimary)
        .margin({ top: 10 })
        .maxLines(1)
        .textOverflow({ overflow: TextOverflow.Ellipsis })

      Text(this.selectedChapter === null ? '选一本书开始' : this.selectedChapter.title)
        .fontSize(12)
        .fontColor(this.theme.textSecondary)
        .margin({ top: 4 })
        .maxLines(1)
        .textOverflow({ overflow: TextOverflow.Ellipsis })

      Text(this.currentText.length > 0 ? this.currentText : '（这里显示当前这句）')
        .fontFamily(this.fontFamilyValue)
        .fontSize(this.fontSize)
        .lineHeight(this.fontSize * 1.5)
        .textAlign(TextAlign.Center)
        .fontColor(this.currentText.length > 0 ? this.subtitleColor : this.theme.textSecondary)
        .maxLines(4)
        .textOverflow({ overflow: TextOverflow.Ellipsis })
        .width('100%')
        .padding({ left: 20, right: 20, top: 16 })

      // 双语：主字幕下面跟一行译文
      if (this.showSecondary && this.secondaryText.length > 0) {
        Text(this.secondaryText)
          .fontFamily(this.fontFamilyValue)
          .fontSize(this.fontSize * 0.68)
          .lineHeight(this.fontSize * 1.05)
          .textAlign(TextAlign.Center)
          .fontColor(this.theme.textSecondary)
          .maxLines(3)
          .textOverflow({ overflow: TextOverflow.Ellipsis })
          .width('100%')
          .padding({ left: 24, right: 24, top: 4 })
      }

      // 章节切换（多章的书才显示）
      if (this.selectedBook !== null && this.selectedBook.chapters.length > 1) {
        Scroll() {
          Row() {
            ForEach(this.selectedBook.chapters, (chapter: Chapter, index: number) => {
              Text(chapter.title)
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
        Text('🔈')
          .fontSize(14)
          .fontColor(this.theme.textSecondary)

        Slider({ value: this.volume * 100, min: 0, max: 100, step: 1 })
          .layoutWeight(1)
          .margin({ left: 8, right: 8 })
          .selectedColor(this.theme.gold)
          .onChange((value: number) => {
            this.volume = value / 100;
            this.saveSettings();
            if (this.player !== null) {
              this.player.setVolume(this.volume);
            }
          })

        Text(Math.round(this.volume * 100) + '%')
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
          .fontSize(11)
          .fontColor(this.theme.textSecondary)
      }
      .width('100%')
      .padding({ left: 20, right: 20 })

      Row() {
        Button('−10s')
          .fontSize(13)
          .height(40)
          .backgroundColor(this.theme.panelBackground)
          .fontColor(this.theme.textPrimary)
          .onClick(() => this.skip(-10000))

        Button(this.isPlaying ? '暂停' : '播放')
          .fontSize(15)
          .height(40)
          .width(104)
          .margin({ left: 14, right: 14 })
          .backgroundColor(this.theme.accent)
          .fontColor(Color.White)
          .enabled(this.selectedChapter !== null)
          .onClick(() => this.togglePlay())

        Button('+10s')
          .fontSize(13)
          .height(40)
          .backgroundColor(this.theme.panelBackground)
          .fontColor(this.theme.textPrimary)
          .onClick(() => this.skip(10000))
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
            .fontColor(this.theme.textPrimary)

          Text('字幕要和音频放同一个目录、文件名相同（01.mp3 配 01.srt）')
            .fontSize(12)
            .fontColor(this.theme.textSecondary)
            .textAlign(TextAlign.Center)
            .margin({ top: 10, left: 28, right: 28 })
        }
        .width('100%')
        .layoutWeight(1)
        .justifyContent(FlexAlign.Center)
      } else {
        List({ space: 4, scroller: this.readingScroller }) {
          ForEach(this.lines, (line: SubtitleLine, index: number) => {
            ListItem() {
              Row() {
                Text(line.timeText())
                  .fontSize(11)
                  .width(50)
                  .fontColor(this.theme.textSecondary)

                Column() {
                  Text(line.text)
                    .fontSize(16)
                    .lineHeight(24)
                    .width('100%')
                    .fontColor(index === this.currentIndex ? this.theme.textPrimary : this.theme.textSecondary)
                    .fontWeight(index === this.currentIndex ? FontWeight.Medium : FontWeight.Normal)

                  // 双语：这一条对应的译文
                  if (this.showSecondary && this.secondaryLines.length > 0) {
                    Text(this.secondaryTextAt(line.startMs))
                      .fontSize(13)
                      .lineHeight(19)
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
              .backgroundColor(index === this.currentIndex ? '#22C9A227' : Color.Transparent)
              .borderRadius(8)
              .onClick(() => this.seekTo(line.startMs))
            }
          }, (line: SubtitleLine) => `${line.index}-${line.startMs}`)
        }
        .width('100%')
        .layoutWeight(1)
        .padding({ left: 8, right: 8 })
        .scrollBar(BarState.Auto)
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
        this.SectionTitle('媒体库')

        Row() {
          Text('书本存放位置')
            .fontSize(13)
            .fontColor(this.theme.textPrimary)
          Blank()
          Text('App 内部目录（导入的书）')
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

        Text('提示：在文件管理器里选中书本文件，直接「分享」给 KATARU 也行。')
          .fontSize(11)
          .fontColor(this.theme.textSecondary)
          .width('100%')
          .margin({ bottom: 6 })
        Blank().height(10)

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

        Blank().height(10)

        this.SectionTitle('字幕外观')

        Row() {
          Text('字号')
            .fontSize(13)
            .fontColor(this.theme.textPrimary)
          Text(`${Math.round(this.fontSize)}`)
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
            this.saveSettings();
          })

        Text('字体')
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

        // 字幕颜色（对应桌面版的配色/字幕颜色设置）
        Text('颜色')
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
          Text('加粗字幕')
            .fontSize(13)
            .fontColor(this.theme.textPrimary)
          Blank()
          Toggle({ type: ToggleType.Switch, isOn: this.subtitleBold })
            .selectedColor(this.theme.accent)
            .onChange((isOn: boolean) => {
              this.subtitleBold = isOn;
              this.saveSettings();
            })
        }
        .width('100%')
        .padding({ top: 4, bottom: 4 })

        Blank().height(6)
        this.SectionTitle('播放')

        Row() {
          Text('双语对照')
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
          Text('播完自动下一章')
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

        this.SectionTitle('状态')
        Text(this.statusText)
          .fontSize(11)
          .fontColor(this.theme.textSecondary)
          .width('100%')
          .lineHeight(18)
      }
      .width('100%')
      .padding({ left: 16, right: 16, top: 8, bottom: 20 })
      .alignItems(HorizontalAlign.Start)
    }
    .width('100%')
    .height('100%')
    .scrollBar(BarState.Auto)
  }

  @Builder
  SectionTitle(text: string) {
    Text(text)
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

  private async openChapter(chapter: Chapter): Promise<void> {
    this.selectedChapter = chapter;
    this.currentText = '';
    this.secondaryText = '';
    this.currentIndex = -1;
    this.lines = [];
    this.secondaryLines = [];
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

    const resumeAt = this.loadProgress(chapter);

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
          hilog.info(0x0000, Index.TAG, 'prepared: duration=%{public}d resumeAt=%{public}d',
            this.durationMs, resumeAt);
          if (resumeAt > 0 && resumeAt < this.durationMs - 3000) {
            player.seek(resumeAt, media.SeekMode.SEEK_CLOSEST);
            this.positionMs = resumeAt;
            this.synchronizer.seek(resumeAt);
            this.applyCurrentLine();
          }
          player.setVolume(this.volume);
          // 自动开始播放（点书即听，和桌面版一致）
          player.play();

          // 接上系统播控：后台继续播放 + 锁屏/播控中心显示当前这句字幕
          this.startMediaSession();
        } else if (state === 'playing') {
          this.isPlaying = true;
          this.startTicker();
        } else if (state === 'paused') {
          this.isPlaying = false;
          this.stopTicker();
          this.saveProgress(false);
          this.syncMediaSession(true);
        } else if (state === 'completed') {
          this.isPlaying = false;
          this.stopTicker();
          this.onChapterCompleted();
        }
      });

      player.on('error', (error: BusinessError) => {
        this.isPlaying = false;
        this.stopTicker();
        this.statusText = `播放失败：${error.message}`;
        hilog.error(0x0000, Index.TAG, 'player error: %{public}s', JSON.stringify(error));
      });

      // 用数据源回调播放，而不是 fdSrc。
      //
      // 原因：AVPlayer 会按**扩展名**判断格式，.m4b 直接被拒（实测 5400106 不支持格式），
      // 而 m4b 内容就是 MP4/AAC。走 AVDataSrcDescriptor 直接喂字节流，它就按内容解析，
      // 既不用改用户文件的名字，也不用复制一份。
      const file = fileIo.openSync(chapter.audioPath, fileIo.OpenMode.READ_ONLY);
      const stat = fileIo.statSync(file.fd);
      this.currentAudioFile = file;
      hilog.info(0x0000, Index.TAG, 'open audio: %{public}s (%{public}d bytes)',
        chapter.audioPath, stat.size);

      const fd = file.fd;
      const dataSrc: media.AVDataSrcDescriptor = {
        fileSize: stat.size,
        callback: (buffer: ArrayBuffer, length: number, pos?: number): number => {
          const offset = pos === undefined ? 0 : pos;
          try {
            return fileIo.readSync(fd, buffer, { offset: offset, length: length });
          } catch (readError) {
            hilog.error(0x0000, Index.TAG, 'read audio failed: %{public}s',
              JSON.stringify(readError));
            return -2;
          }
        }
      };

      const mediaSource = media.createMediaSourceWithDataSource(dataSrc);
      if (mediaSource === undefined) {
        this.statusText = '无法创建音频数据源';
        hilog.error(0x0000, Index.TAG, 'createMediaSourceWithDataSource returned undefined');
        return;
      }

      hilog.info(0x0000, Index.TAG, 'setMediaSource(dataSrc) for %{public}s', chapter.title);
      await player.setMediaSource(mediaSource);
    } catch (error) {
      const err = error as BusinessError;
      this.statusText = `打开音频失败：${err.message}`;
      hilog.error(0x0000, Index.TAG, 'open failed: %{public}s', JSON.stringify(err));
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

  // ==================== 逻辑：字幕 ====================

  /** 找某条主字幕对应的译文（按时间区间重叠匹配） */
  private secondaryTextAt(startMs: number): string {
    for (const line of this.secondaryLines) {
      if (startMs >= line.startMs - 300 && startMs <= line.endMs + 300) {
        return line.text;
      }
    }
    return '';
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
      const content = this.readTextByUri(path);
      const warnings: string[] = [];
      const lines = SrtParser.parse(content, warnings);

      this.synchronizer.load(lines);
      this.lines = lines;
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
      this.currentIndex = index;
      if (index >= 0) {
        this.readingScroller.scrollToIndex(index, true, ScrollAlign.CENTER);
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

      this.positionMs = player.currentTime;
      this.synchronizer.update(player.currentTime);
      this.secondarySynchronizer.update(player.currentTime);
      this.applyCurrentLine();
      this.saveProgress(false);
      this.syncMediaSession(false);
    }, 100);
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
      this.isPlaying
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

  private stopTicker(): void {
    if (this.tickTimer !== -1) {
      clearInterval(this.tickTimer);
      this.tickTimer = -1;
    }
  }
}
