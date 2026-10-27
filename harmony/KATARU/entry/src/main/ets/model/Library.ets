import { fileIo } from '@kit.CoreFileKit';
import { SubtitleLanguage } from './SubtitleLanguage';

/**
 * 一本书的一个章节（一个音频文件 + 可能有的字幕）。
 */
export class Chapter {
  audioPath: string = '';
  title: string = '';
  /** 主字幕（显示/同步用） */
  subtitlePath: string = '';
  /** 对照字幕（双语用，可能为空） */
  secondarySubtitlePath: string = '';
  /** 配到的字幕语言代码（'' = 原文） */
  languageCode: string = '';

  constructor(audioPath: string, title: string) {
    this.audioPath = audioPath;
    this.title = title;
  }

  get hasSubtitle(): boolean {
    return this.subtitlePath.length > 0;
  }
}

/**
 * 一本书 = 一个子文件夹。
 */
export class Book {
  folderPath: string = '';
  title: string = '';
  coverPath: string = '';
  chapters: Chapter[] = [];

  constructor(folderPath: string, title: string) {
    this.folderPath = folderPath;
    this.title = title;
  }

  get chapterCount(): number {
    return this.chapters.length;
  }

  get hasCover(): boolean {
    return this.coverPath.length > 0;
  }

  /** 第一个有字幕的章节（选书时直接用它） */
  firstPlayable(): Chapter | null {
    for (const chapter of this.chapters) {
      return chapter;
    }
    return null;
  }
}

/**
 * 媒体库扫描器（对应桌面版 MediaLibraryScanner）。
 *
 * 约定和桌面版一致：
 *  - 根目录下**每个子文件夹算一本书**
 *  - 文件夹里的音频文件按文件名自然顺序排成章节
 *  - 同名（或同名 + 语言后缀）的 .srt 自动配给该章节
 *  - 封面优先级：cover.png/jpg/jpeg → folder.* → 第一张图片
 */
export class LibraryScanner {
  /** 支持的音频扩展名 */
  private static readonly AUDIO_EXT: string[] = ['.mp3', '.m4a', '.m4b', '.aac', '.wav', '.flac', '.ogg', '.mp4'];
  private static readonly SUBTITLE_EXT: string = '.srt';
  private static readonly COVER_NAMES: string[] = ['cover', 'folder', 'front', 'poster', '封面'];
  private static readonly COVER_EXT: string[] = ['.png', '.jpg', '.jpeg', '.webp', '.bmp'];

  /**
   * 扫描根目录。
   * @param rootPath 例如 /storage/Users/currentUser/Download/KATARU Book
   */
  static scan(rootPath: string): Book[] {
    const books = LibraryScanner.scanLevel(rootPath, 0);
    books.sort((a: Book, b: Book): number => a.title.localeCompare(b.title));
    return books;
  }

  /**
   * 扫一层；某个子文件夹里没有音频时，**自动再往下找一层**。
   *
   * 这样用户选到 KATARU 还是选到 KATARU/Book 都能出书 ——
   * 手机上的目录层级经常多一层（Download/KATARU/Book/书名/），
   * 让人精确选到"该选哪一层"太容易错。
   */
  private static scanLevel(rootPath: string, depth: number): Book[] {
    const books: Book[] = [];

    let folders: string[] = [];
    try {
      folders = fileIo.listFileSync(rootPath);
    } catch (error) {
      return books;
    }

    for (const folder of folders) {
      const folderPath = `${rootPath}/${folder}`;

      let isDirectory = false;
      try {
        isDirectory = fileIo.statSync(folderPath).isDirectory();
      } catch (error) {
        isDirectory = false;
      }

      if (!isDirectory) {
        continue;
      }

      const book = LibraryScanner.scanBook(folderPath, folder);
      if (book.chapters.length > 0) {
        books.push(book);
      } else if (depth < 1) {
        // 这一层没有音频 → 当作"用户选高了一层"，往下找
        const nested = LibraryScanner.scanLevel(folderPath, depth + 1);
        for (const item of nested) {
          books.push(item);
        }
      }
    }

    return books;
  }

  /** 扫描单本书（一个文件夹） */
  static scanBook(folderPath: string, title: string): Book {
    const book = new Book(folderPath, title);

    let files: string[] = [];
    try {
      files = fileIo.listFileSync(folderPath);
    } catch (error) {
      return book;
    }

    const audioFiles: string[] = [];
    const subtitleFiles: string[] = [];

    for (const file of files) {
      const lower = file.toLowerCase();
      if (LibraryScanner.matches(lower, LibraryScanner.AUDIO_EXT)) {
        audioFiles.push(file);
      } else if (lower.endsWith(LibraryScanner.SUBTITLE_EXT)) {
        subtitleFiles.push(file);
      }
    }

    audioFiles.sort(LibraryScanner.naturalCompare);
    subtitleFiles.sort(LibraryScanner.naturalCompare);

    for (const audio of audioFiles) {
      const audioPath = `${folderPath}/${audio}`;

      // 跳过 0 字节文件：之前 copyFile 坏掉时留下过空文件，
      // 这种"书"扫进来也放不了，不如不显示（否则用户会以为播放坏了）。
      try {
        const size = fileIo.statSync(audioPath).size;
        if (size <= 0) {
          continue;
        }
      } catch (error) {
        continue;
      }

      const stem = LibraryScanner.stemOf(audio);
      const chapter = new Chapter(audioPath, stem);

      // 配对字幕：同名优先，其次同名 + 语言后缀
      let exact = '';
      const related: string[] = [];
      for (const subtitle of subtitleFiles) {
        const subStem = LibraryScanner.stemOf(subtitle);
        if (subStem.toLowerCase() === stem.toLowerCase()) {
          exact = subtitle;
        } else if (SubtitleLanguage.isRelatedSubtitle(stem, subStem)) {
          related.push(subtitle);
        }
      }

      const chosen = exact.length > 0 ? exact : (related.length > 0 ? related[0] : '');
      if (chosen.length > 0) {
        chapter.subtitlePath = `${folderPath}/${chosen}`;
        const detected = SubtitleLanguage.detect(stem, chosen);
        chapter.languageCode = detected[0];

        // 双语：再找一条不同语言的（优先 .zh / .中文）
        for (const candidate of related) {
          if (candidate === chosen) {
            continue;
          }
          const other = SubtitleLanguage.detect(stem, candidate);
          if (other[0] !== chapter.languageCode) {
            chapter.secondarySubtitlePath = `${folderPath}/${candidate}`;
            break;
          }
        }
      }

      book.chapters.push(chapter);
    }

    book.coverPath = LibraryScanner.findCover(folderPath, files);
    return book;
  }

  /** 找封面 */
  private static findCover(folderPath: string, files: string[]): string {
    // ① 标准封面名优先（cover.png 是"换封面"功能写进来的，用户选择优先）
    for (const name of LibraryScanner.COVER_NAMES) {
      for (const ext of LibraryScanner.COVER_EXT) {
        const candidate = name + ext;
        for (const file of files) {
          if (file.toLowerCase() === candidate) {
            return `${folderPath}/${file}`;
          }
        }
      }
    }

    // ② 和文件夹同名 / 和音频同名的图片
    const folderName = folderPath.substring(folderPath.lastIndexOf('/') + 1);
    for (const file of files) {
      const lower = file.toLowerCase();
      for (const ext of LibraryScanner.COVER_EXT) {
        if (!lower.endsWith(ext)) {
          continue;
        }
        const stem = LibraryScanner.stemOf(file);
        if (stem.toLowerCase() === folderName.toLowerCase() ||
            folderName.toLowerCase().startsWith(stem.toLowerCase()) ||
            stem.toLowerCase().startsWith(folderName.toLowerCase())) {
          return `${folderPath}/${file}`;
        }
      }
    }

    // ③ 随便一张图片
    for (const file of files) {
      const lower = file.toLowerCase();
      for (const ext of LibraryScanner.COVER_EXT) {
        if (lower.endsWith(ext)) {
          return `${folderPath}/${file}`;
        }
      }
    }

    return '';
  }

  private static matches(lowerFileName: string, extensions: string[]): boolean {
    for (const ext of extensions) {
      if (lowerFileName.endsWith(ext)) {
        return true;
      }
    }
    return false;
  }

  /** 去掉扩展名 */
  static stemOf(fileName: string): string {
    const dot = fileName.lastIndexOf('.');
    return dot > 0 ? fileName.substring(0, dot) : fileName;
  }

  /**
   * 自然排序：让 "第2章" 排在 "第10章" 前面。
   * 做法是把数字段当数值比较，其余按字符串比。
   */
  static naturalCompare(a: string, b: string): number {
    const left = LibraryScanner.splitParts(a.toLowerCase());
    const right = LibraryScanner.splitParts(b.toLowerCase());
    const length = Math.min(left.length, right.length);

    for (let i = 0; i < length; i++) {
      const l = left[i];
      const r = right[i];
      const lNum = Number.parseInt(l, 10);
      const rNum = Number.parseInt(r, 10);
      const lIsNum = !Number.isNaN(lNum);
      const rIsNum = !Number.isNaN(rNum);

      if (lIsNum && rIsNum) {
        if (lNum !== rNum) {
          return lNum - rNum;
        }
      } else if (l !== r) {
        return l < r ? -1 : 1;
      }
    }

    return left.length - right.length;
  }

  private static splitParts(text: string): string[] {
    const parts: string[] = [];
    let buffer = '';
    let lastWasDigit = false;

    for (let i = 0; i < text.length; i++) {
      const ch = text.charAt(i);
      const isDigit = ch >= '0' && ch <= '9';
      if (buffer.length > 0 && isDigit !== lastWasDigit) {
        parts.push(buffer);
        buffer = '';
      }
      buffer += ch;
      lastWasDigit = isDigit;
    }

    if (buffer.length > 0) {
      parts.push(buffer);
    }

    return parts;
  }
}
