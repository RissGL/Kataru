import { fileIo, picker, fileShare } from '@kit.CoreFileKit';
import { preferences } from '@kit.ArkData';
import { abilityAccessCtrl, common, Permissions } from '@kit.AbilityKit';
import { media } from '@kit.MediaKit';
import { BusinessError } from '@kit.BasicServicesKit';
import { hilog } from '@kit.PerformanceAnalysisKit';
import { util } from '@kit.ArkTS';
import { Book, Chapter, LibraryScanner } from '../model/Library';
import { BookStore } from '../model/BookStore';
import { SubtitleLine, SrtParser } from '../model/SrtParser';
import { SubtitleSynchronizer } from '../model/SubtitleSynchronizer';
import { Timecode } from '../model/Timecode';
const PREFS_NAME: string = 'kataru_settings';
const PREF_KEY_ROOT: string = 'library_uri';

/** 可选字体（不存在时 ArkUI 会自动回退到系统字体） */
class FontChoice {
  name: string = '';
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
  private tickTimer: number = -1;
  private readingScroller: Scroller = new Scroller();

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
  @State lines: SubtitleLine[] = [];
  @State currentIndex: number = -1;

  // ---------- 界面 ----------
  @State currentTab: number = 0;
  @State fontSize: number = 26;
  @State fontFamilyValue: string = 'HarmonyOS Sans SC';
  @State fontChoices: FontChoice[] = [
    new FontChoice('系统默认', 'HarmonyOS Sans SC'),
    new FontChoice('黑体', 'HarmonyOS Sans'),
    new FontChoice('衬线', 'serif'),
    new FontChoice('等宽', 'monospace'),
  ];

  aboutToAppear(): void {
    // 优先用"用户选过的书库文件夹"（授权需要每次启动重新激活一次），
    // 没有就回落到 App 沙箱（导入的书放这里）。
    const context = this.getUIContext().getHostContext() as common.UIAbilityContext;
    const sandboxRoot = context.filesDir + '/books';

    try {
      fileIo.mkdirSync(sandboxRoot, true);
    } catch (error) {
      // 已存在会报错，忽略
    }

    const saved = this.loadSavedRoot();
    if (saved.length > 0) {
      this.libraryRoot = saved;
      // 重新激活持久授权（不调用的话重启后权限可能失效）
      this.reactivate(saved);
    } else {
      this.libraryRoot = sandboxRoot;
    }

    const books = LibraryScanner.scan(this.libraryRoot);
    this.books = books;
    this.statusText = books.length > 0
      ? ('书库：' + books.length + ' 本')
      : '点「选择书库文件夹」或「添加书本」';
    hilog.info(0x0000, Index.TAG, 'startup scan: %{public}d book(s) at %{public}s',
      books.length, this.libraryRoot);
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

      const entries = fileIo.listFileSync(saved);
      hilog.info(0x0000, Index.TAG, 'saved root OK: %{public}s (%{public}d entries)', saved, entries.length);
      return saved;
    } catch (error) {
      hilog.warn(0x0000, Index.TAG, 'saved root unusable: %{public}s', JSON.stringify(error));
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

  /** 重新扫描当前书库（沙箱目录，或用户选的文件夹 URI） */
  private rescanSandbox(): void {
    const context = this.getUIContext().getHostContext() as common.UIAbilityContext;
    const sandboxRoot = context.filesDir + '/books';

    try {
      fileIo.mkdirSync(sandboxRoot, true);
    } catch (error) {
      // 目录已存在会报错，忽略
    }

    const root = this.libraryRoot.length > 0 ? this.libraryRoot : sandboxRoot;
    const books = LibraryScanner.scan(root);
    this.books = books;
    this.libraryRoot = root;
    this.statusText = books.length > 0 ? ('书库：' + books.length + ' 本') : '还没有书，点「添加书本」导入';
    hilog.info(0x0000, Index.TAG, 'rescan: %{public}d book(s) at %{public}s', books.length, root);
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

      // 先确认这个目录真的能读，否则后面扫描是空的
      let entries: string[] = [];
      try {
        entries = fileIo.listFileSync(uri);
      } catch (error) {
        hilog.error(0x0000, Index.TAG, 'list picked folder failed: %{public}s', JSON.stringify(error));
        this.statusText = '这个文件夹读不了（系统没给权限），换个位置再试';
        return;
      }
      hilog.info(0x0000, Index.TAG, 'picked folder has %{public}d entries', entries.length);

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

      this.libraryRoot = uri;
      this.saveRoot(uri);
      this.statusText = '书库文件夹已设为：' + uri;

      const pickedBooks = LibraryScanner.scan(uri);
      this.books = pickedBooks;
      hilog.info(0x0000, Index.TAG, 'scan picked folder: %{public}d book(s)', pickedBooks.length);

      if (pickedBooks.length === 0) {
        this.statusText = '这个文件夹里没有找到书（每本书应该是一个子文件夹，里面放音频和字幕）';
      }
    } catch (error) {
      const err = error as BusinessError;
      hilog.error(0x0000, Index.TAG, 'choose folder failed: code=%{public}d msg=%{public}s',
        err.code, err.message);

      // 只有真的"能力不存在"才降级；用户取消不会走到这里
      this.statusText = '选文件夹失败（' + err.message + '），已切换成多选文件';
      await this.importBook();
    }
  }

  /**
   * 导入一本（或几本）书：一次多选【音频 + 字幕 + 封面】。
   *
   * 这是鸿蒙上唯一稳的路子：用系统选择器授权具体文件，App 记住 URI，
   * 之后直接按 URI 播放/读字幕，不需要任何目录权限。
   */
  private async importBook(): Promise<void> {
    try {
      const options = new picker.DocumentSelectOptions();
      options.maxSelectNumber = 0; // 不限个数
      options.fileSuffixFilters = ['音频|.mp3,.m4a,.m4b,.aac,.wav,.flac,.ogg,.mp4',
        '字幕|.srt', '封面|.png,.jpg,.jpeg,.webp'];

      const documentPicker = new picker.DocumentViewPicker(this.getUIContext().getHostContext() as common.UIAbilityContext);
      const uris: string[] = await documentPicker.select(options);
      if (uris === null || uris.length === 0) {
        return;
      }

      hilog.info(0x0000, Index.TAG, 'import: picked %{public}d file(s)', uris.length);

      // 持久化授权（否则重启就失效）
      const policies: fileShare.PolicyInfo[] = [];
      for (const uri of uris) {
        const policy: fileShare.PolicyInfo = {
          uri: uri,
          operationMode: (fileShare.OperationMode.READ_MODE | fileShare.OperationMode.WRITE_MODE),
        };
        policies.push(policy);
      }
      try {
        await fileShare.persistPermission(policies);
        hilog.info(0x0000, Index.TAG, 'import: persisted %{public}d permission(s)', policies.length);
      } catch (error) {
        hilog.warn(0x0000, Index.TAG, 'import: persist failed %{public}s', JSON.stringify(error));
      }

      // 关键：把文件**复制进应用沙箱**。
      // 不复制的话，重启后 persistPermission 的授权会失效（实测 code 13900001），
      // 音频和字幕全都打不开。复制到沙箱后就不需要任何权限了。
      const copied = this.copyToSandbox(uris);
      if (copied === 0) {
        this.statusText = '复制文件失败，看看日志';
        return;
      }

      const context0 = this.getUIContext().getHostContext() as common.UIAbilityContext;
      const sandboxRoot = `${context0.filesDir}/books`;
      const scanned = LibraryScanner.scan(sandboxRoot);
      if (scanned.length > 0) {
        this.books = scanned;
        this.libraryRoot = sandboxRoot;
        this.currentTab = 0; // 导入完自动跳到书库，立刻看到结果
        this.statusText = `已导入 ${scanned.length} 本书`;
      }

      let chapters = 0;
      let subs = 0;
      for (const book of this.books) {
        chapters += book.chapters.length;
        for (const chapter of book.chapters) {
          if (chapter.hasSubtitle) {
            subs++;
          }
        }
      }
      this.statusText = `书库：${this.books.length} 本 / ${chapters} 章（${subs} 章有字幕）`;

      for (const book of this.books) {
        hilog.info(0x0000, Index.TAG, '  book: %{public}s chapters=%{public}d cover=%{public}s',
          book.title, book.chapters.length, book.coverPath.length > 0 ? 'yes' : 'no');
      }
    } catch (error) {
      const err = error as BusinessError;
      this.statusText = `导入失败：${err.message}`;
      hilog.error(0x0000, Index.TAG, 'import failed: %{public}s', JSON.stringify(err));
    }
  }

  /**
   * 把选中的文件复制进沙箱：filesDir/books/<书名>/<文件名>
   * 返回复制成功的文件数。
   */
  private copyToSandbox(uris: string[]): number {
    const context = this.getUIContext().getHostContext() as common.UIAbilityContext;
    const root = `${context.filesDir}/books`;

    // 书名取第一个音频的文件名
    let bookName = '';
    for (const uri of uris) {
      const ext = BookStore.extFromUri(uri);
      if (ext !== '.srt' && ext !== '.png' && ext !== '.jpg' && ext !== '.jpeg' && ext !== '.webp') {
        bookName = BookStore.titleFromUri(uri);
        break;
      }
    }

    if (bookName.length === 0) {
      bookName = 'book';
    }

    // 名字里的 / 和 \ 要清掉
    const safeName = bookName.replace(/[\/\\:*?"<>|]/g, '_');
    const bookDir = `${root}/${safeName}`;

    try {
      fileIo.mkdirSync(bookDir, true);
    } catch (error) {
      hilog.warn(0x0000, Index.TAG, 'mkdir sandbox failed: %{public}s', JSON.stringify(error));
    }

    let copied = 0;
    for (const uri of uris) {
      const fileName = BookStore.fileFromUri(uri);
      let safeFileName = fileName.replace(/[\/\\:*?"<>|]/g, '_');

      // 关键：AVPlayer 按扩展名认格式，.m4b 它不认（日志实测 code 5400106 不支持格式），
      // 而 m4b 内容就是 MP4/AAC —— 落盘时直接存成 .m4a，播放器才认得。
      // （桌面版用同样的思路：给 .m4b 建一个 .m4a 的硬链接别名）
      if (safeFileName.toLowerCase().endsWith('.m4b')) {
        safeFileName = safeFileName.substring(0, safeFileName.length - 4) + '.m4a';
      }

      const target = `${bookDir}/${safeFileName}`;

      try {
        // 已存在就跳过（重复导入同一本时省时间）
        if (fileIo.accessSync(target)) {
          copied++;
          continue;
        }

        const src = fileIo.openSync(uri, fileIo.OpenMode.READ_ONLY);
        try {
          fileIo.copyFile(src.fd, target);
          copied++;
          hilog.info(0x0000, Index.TAG, 'copied: %{public}s', safeFileName);
        } finally {
          fileIo.closeSync(src);
        }
      } catch (error) {
        hilog.error(0x0000, Index.TAG, 'copy failed: %{public}s %{public}s',
          safeFileName, JSON.stringify(error));
      }
    }

    hilog.info(0x0000, Index.TAG, 'sandbox copy done: %{public}d/%{public}d files -> %{public}s',
      copied, uris.length, bookDir);

    return copied;
  }

  /**
   * 把选中的一堆 URI 组装成 Book：
   *  - 音频 → 章节（标题取文件名）
   *  - .srt → 按文件名配给对应章节（同名 / 同名+语言后缀）
   *  - 图片 → 封面
   */
  private buildBooksFromUris(uris: string[]): Book[] {
    const audioUris: string[] = [];
    const subtitleUris: string[] = [];
    const coverUris: string[] = [];

    for (const uri of uris) {
      const ext = BookStore.extFromUri(uri);
      if (ext === '.srt') {
        subtitleUris.push(uri);
      } else if (ext === '.png' || ext === '.jpg' || ext === '.jpeg' || ext === '.webp') {
        coverUris.push(uri);
      } else {
        audioUris.push(uri);
      }
    }

    if (audioUris.length === 0) {
      return [];
    }

    // 这次导入当成一本书：书名取第一个音频的文件名
    const title = BookStore.titleFromUri(audioUris[0]);
    const book = new Book('', title);

    for (const audio of audioUris) {
      const chapterTitle = BookStore.titleFromUri(audio);
      const chapter = new Chapter(audio, chapterTitle);

      // 配对字幕：同名优先，其次同名 + 语言后缀
      let exact = '';
      const related: string[] = [];
      for (const subtitle of subtitleUris) {
        const subStem = BookStore.titleFromUri(subtitle);
        if (subStem.toLowerCase() === chapterTitle.toLowerCase()) {
          exact = subtitle;
        } else if (subStem.toLowerCase().startsWith(chapterTitle.toLowerCase())) {
          related.push(subtitle);
        }
      }

      chapter.subtitlePath = exact.length > 0 ? exact : (related.length > 0 ? related[0] : '');
      book.chapters.push(chapter);
    }

    if (coverUris.length > 0) {
      // 优先和书名同名的图片
      let chosen = coverUris[0];
      for (const cover of coverUris) {
        if (BookStore.titleFromUri(cover).toLowerCase() === title.toLowerCase()) {
          chosen = cover;
          break;
        }
      }
      book.coverPath = chosen;
    }

    return [book];
  }

  aboutToDisappear(): void {
    this.stopTicker();
    this.releasePlayer();
  }

  build() {
    Column() {
      // 顶栏
      Row() {
        Text('KATARU')
          .fontSize(19)
          .fontWeight(FontWeight.Bold)
          .fontColor($r('app.color.text_primary'))

        Blank()

        if (this.currentTab === 0) {
          Button('添加书本')
            .fontSize(13)
            .height(32)
            .backgroundColor($r('app.color.panel_background'))
            .fontColor($r('app.color.gold'))
            .onClick(() => this.importBook())
        }

        if (this.currentTab === 2 && this.lines.length > 0) {
          Text(`${this.currentIndex + 1} / ${this.lines.length}`)
            .fontSize(12)
            .fontColor($r('app.color.text_secondary'))
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
    .backgroundColor($r('app.color.page_background'))
  }

  @Builder
  TabBarItem(label: string, index: number) {
    Text(label)
      .fontSize(13)
      .fontWeight(this.currentTab === index ? FontWeight.Medium : FontWeight.Normal)
      .fontColor(this.currentTab === index ? $r('app.color.gold') : $r('app.color.text_secondary'))
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
            .fontColor($r('app.color.text_primary'))

          Text(`把「一本书一个文件夹」放进这个目录：\n${this.libraryRoot}`)
            .fontSize(12)
            .lineHeight(20)
            .textAlign(TextAlign.Center)
            .fontColor($r('app.color.text_secondary'))
            .margin({ top: 12, left: 24, right: 24 })

          Button('添加书本')
            .margin({ top: 20 })
            .backgroundColor($r('app.color.accent'))
            .fontColor(Color.White)
            .onClick(() => this.importBook())

          Text('一次多选这本的：音频 (.m4b/.mp3) + 字幕 (.srt) + 封面 (png)')
            .fontSize(11)
            .fontColor($r('app.color.text_secondary'))
            .textAlign(TextAlign.Center)
            .margin({ top: 10, left: 24, right: 24 })
        }
        .width('100%')
        .justifyContent(FlexAlign.Center)
        .layoutWeight(1)
      } else {
        Grid() {
          ForEach(this.books, (book: Book) => {
            GridItem() {
              Column() {
                Stack() {
                  if (book.hasCover) {
                    Image(book.coverPath)
                      .width('100%')
                      .height(150)
                      .objectFit(ImageFit.Cover)
                      .borderRadius(8)
                  } else {
                    Column() {
                      Text('📖')
                        .fontSize(30)
                      Text(book.title.substring(0, 2))
                        .fontSize(20)
                        .fontColor($r('app.color.text_secondary'))
                        .margin({ top: 6 })
                    }
                    .width('100%')
                    .height(150)
                    .justifyContent(FlexAlign.Center)
                    .backgroundColor($r('app.color.panel_background'))
                    .borderRadius(8)
                  }
                }
                .width('100%')

                Text(book.title)
                  .fontSize(13)
                  .fontColor($r('app.color.text_primary'))
                  .maxLines(1)
                  .textOverflow({ overflow: TextOverflow.Ellipsis })
                  .margin({ top: 6 })

                Text(`${book.chapterCount} 章`)
                  .fontSize(11)
                  .fontColor($r('app.color.text_secondary'))
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
        .fontColor($r('app.color.text_secondary'))
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
      if (this.selectedBook !== null && this.selectedBook.hasCover) {
        Image(this.selectedBook.coverPath)
          .width(140)
          .aspectRatio(0.72)
          .objectFit(ImageFit.Cover)
          .borderRadius(10)
          .margin({ top: 8 })
      } else {
        Column() {
          Text('📖').fontSize(36)
        }
        .width(140)
        .aspectRatio(0.72)
        .justifyContent(FlexAlign.Center)
        .backgroundColor($r('app.color.panel_background'))
        .borderRadius(10)
        .margin({ top: 8 })
      }

      Text(this.selectedBook === null ? '未选择书' : this.selectedBook.title)
        .fontSize(16)
        .fontWeight(FontWeight.Medium)
        .fontColor($r('app.color.text_primary'))
        .margin({ top: 10 })
        .maxLines(1)
        .textOverflow({ overflow: TextOverflow.Ellipsis })

      Text(this.selectedChapter === null ? '选一本书开始' : this.selectedChapter.title)
        .fontSize(12)
        .fontColor($r('app.color.text_secondary'))
        .margin({ top: 4 })
        .maxLines(1)
        .textOverflow({ overflow: TextOverflow.Ellipsis })

      Text(this.currentText.length > 0 ? this.currentText : '（这里显示当前这句）')
        .fontFamily(this.fontFamilyValue)
        .fontSize(this.fontSize)
        .lineHeight(this.fontSize * 1.5)
        .textAlign(TextAlign.Center)
        .fontColor(this.currentText.length > 0 ? $r('app.color.text_primary') : $r('app.color.text_secondary'))
        .maxLines(4)
        .textOverflow({ overflow: TextOverflow.Ellipsis })
        .width('100%')
        .padding({ left: 20, right: 20, top: 16 })

      Blank()

      Row() {
        Text(Timecode.formatShort(this.positionMs))
          .fontSize(11)
          .fontColor($r('app.color.text_secondary'))

        Slider({
          value: this.positionMs,
          min: 0,
          max: this.durationMs > 0 ? this.durationMs : 1,
          step: 500,
          style: SliderStyle.InSet
        })
          .layoutWeight(1)
          .margin({ left: 8, right: 8 })
          .selectedColor($r('app.color.accent'))
          .onChange((value: number, mode: SliderChangeMode) => {
            if (mode === SliderChangeMode.End || mode === SliderChangeMode.Click) {
              this.seekTo(value);
            }
          })

        Text(Timecode.formatShort(this.durationMs))
          .fontSize(11)
          .fontColor($r('app.color.text_secondary'))
      }
      .width('100%')
      .padding({ left: 20, right: 20 })

      Row() {
        Button('−10s')
          .fontSize(13)
          .height(40)
          .backgroundColor($r('app.color.panel_background'))
          .fontColor($r('app.color.text_primary'))
          .onClick(() => this.skip(-10000))

        Button(this.isPlaying ? '暂停' : '播放')
          .fontSize(15)
          .height(40)
          .width(104)
          .margin({ left: 14, right: 14 })
          .backgroundColor($r('app.color.accent'))
          .fontColor(Color.White)
          .enabled(this.selectedChapter !== null)
          .onClick(() => this.togglePlay())

        Button('+10s')
          .fontSize(13)
          .height(40)
          .backgroundColor($r('app.color.panel_background'))
          .fontColor($r('app.color.text_primary'))
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
            .fontColor($r('app.color.text_primary'))

          Text('字幕要和音频放同一个目录、文件名相同（01.mp3 配 01.srt）')
            .fontSize(12)
            .fontColor($r('app.color.text_secondary'))
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
            .fontColor($r('app.color.text_primary'))
          Blank()
          Text('App 内部目录（导入的书）')
            .fontSize(11)
            .fontColor($r('app.color.text_secondary'))
            .maxLines(1)
        }
        .width('100%')
        .padding({ top: 6, bottom: 6 })

        Button('选择书库文件夹')
          .width('100%')
          .height(40)
          .margin({ top: 8, bottom: 6 })
          .backgroundColor($r('app.color.accent'))
          .fontColor(Color.White)
          .onClick(() => this.chooseLibraryFolder())

        Button('添加书本')
          .width('100%')
          .height(40)
          .margin({ top: 8, bottom: 6 })
          .backgroundColor($r('app.color.accent'))
          .fontColor(Color.White)
          .onClick(() => this.importBook())

        Button('重新扫描')
          .width('100%')
          .height(36)
          .margin({ top: 0, bottom: 6 })
          .backgroundColor($r('app.color.panel_background'))
          .fontColor($r('app.color.text_primary'))
          .onClick(() => this.rescanSandbox())

        Text('提示：在文件管理器里选中书本文件，直接「分享」给 KATARU 也行。')
          .fontSize(11)
          .fontColor($r('app.color.text_secondary'))
          .width('100%')
          .margin({ bottom: 6 })
        Blank().height(10)

        this.SectionTitle('字幕外观')

        Row() {
          Text('字号')
            .fontSize(13)
            .fontColor($r('app.color.text_primary'))
          Text(`${Math.round(this.fontSize)}`)
            .fontSize(12)
            .fontColor($r('app.color.text_secondary'))
            .layoutWeight(1)
            .textAlign(TextAlign.End)
        }
        .width('100%')
        .padding({ top: 8 })

        Slider({ value: this.fontSize, min: 14, max: 48, step: 1 })
          .width('100%')
          .selectedColor($r('app.color.accent'))
          .onChange((value: number) => {
            this.fontSize = value;
          })

        Text('字体')
          .fontSize(13)
          .fontColor($r('app.color.text_primary'))
          .margin({ top: 10, bottom: 6 })

        Flex({ wrap: FlexWrap.Wrap }) {
          ForEach(this.fontChoices, (choice: FontChoice) => {
            Button(choice.name)
              .fontSize(12)
              .height(32)
              .margin({ right: 8, bottom: 8 })
              .backgroundColor(this.fontFamilyValue === choice.value ? $r('app.color.accent') : $r('app.color.panel_background'))
              .fontColor(this.fontFamilyValue === choice.value ? Color.White : $r('app.color.text_primary'))
              .onClick(() => {
                this.fontFamilyValue = choice.value;
              })
          }, (choice: FontChoice) => choice.value)
        }
        .width('100%')

        Blank().height(10)

        this.SectionTitle('状态')
        Text(this.statusText)
          .fontSize(11)
          .fontColor($r('app.color.text_secondary'))
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
      .fontColor($r('app.color.gold'))
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
    this.currentIndex = -1;
    this.lines = [];
    this.synchronizer.clear();
    this.positionMs = 0;
    this.durationMs = 0;
    this.stopTicker();

    if (chapter.hasSubtitle) {
      this.loadSubtitle(chapter.subtitlePath);
    }

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
          this.statusText = `已就绪，时长 ${Timecode.formatShort(this.durationMs)}`;
        } else if (state === 'playing') {
          this.isPlaying = true;
          this.startTicker();
        } else if (state === 'paused' || state === 'completed') {
          this.isPlaying = false;
          this.stopTicker();
        }
      });

      player.on('error', (error: BusinessError) => {
        this.isPlaying = false;
        this.stopTicker();
        this.statusText = `播放失败：${error.message}`;
        hilog.error(0x0000, Index.TAG, 'player error: %{public}s', JSON.stringify(error));
      });

      const file = fileIo.openSync(chapter.audioPath, fileIo.OpenMode.READ_ONLY);
      const stat = fileIo.statSync(file.fd);
      hilog.info(0x0000, Index.TAG, 'open audio: %{public}s (%{public}d bytes)',
        chapter.audioPath, stat.size);
      player.fdSrc = { fd: file.fd, offset: 0, length: stat.size };
    } catch (error) {
      const err = error as BusinessError;
      this.statusText = `打开音频失败：${err.message}`;
      hilog.error(0x0000, Index.TAG, 'open failed: %{public}s', JSON.stringify(err));
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
      this.statusText = `字幕：${name}（${lines.length} 条${warnings.length > 0 ? `，告警 ${warnings.length}` : ''}）`;

      this.synchronizer.seek(this.positionMs);
      this.applyCurrentLine();
    } catch (error) {
      const err = error as BusinessError;
      this.statusText = `读字幕失败：${err.message}（这个字幕可能没授权成功，重新导入一次试试）`;
      hilog.error(0x0000, Index.TAG, 'load subtitle failed: uri=%{public}s %{public}s',
        path, JSON.stringify(err));
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
