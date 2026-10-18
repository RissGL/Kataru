import { preferences } from '@kit.ArkData';
import { common } from '@kit.AbilityKit';
import { hilog } from '@kit.PerformanceAnalysisKit';
import { Book, Chapter } from './Library';

/**
 * 书本持久化（导入式书库）。
 *
 * 为什么是"导入式"而不是"扫描目录"：
 *   鸿蒙上第三方 App **读不到公共 Download 目录**（实测 READ_WRITE_DOWNLOAD_DIRECTORY
 *   权限被系统拒绝，返回 2），也没法按路径遍历。
 *   唯一可行的是用系统文件选择器授权——而且这台机型的"选文件夹"也不支持。
 *   所以：用户选一次这本书的文件（音频 + 字幕 + 封面），App 记住这些 URI
 *   （fileShare.persistPermission 持久化），之后每次启动直接从 URI 读取播放。
 */

/** 序列化用的结构 */
class StoredChapter {
  audioUri: string = '';
  title: string = '';
  subtitleUri: string = '';
}

class StoredBook {
  title: string = '';
  coverUri: string = '';
  chapters: StoredChapter[] = [];
}

class StoredLibrary {
  books: StoredBook[] = [];
}

export class BookStore {
  private static readonly TAG: string = 'KATARU';
  private static readonly PREFS_NAME: string = 'kataru_settings';
  private static readonly KEY_LIBRARY: string = 'imported_library';

  /** 读取导入过的书 */
  static load(context: common.UIAbilityContext): Book[] {
    const books: Book[] = [];

    try {
      const store = preferences.getPreferencesSync(context, { name: BookStore.PREFS_NAME });
      const json = store.getSync(BookStore.KEY_LIBRARY, '') as string;
      if (json.length === 0) {
        return books;
      }

      const parsed = JSON.parse(json) as StoredLibrary;
      if (parsed === null || parsed.books === undefined) {
        return books;
      }

      for (const stored of parsed.books) {
        const book = new Book('', stored.title);
        book.coverPath = stored.coverUri;
        if (stored.chapters !== undefined) {
          for (const chapter of stored.chapters) {
            const item = new Chapter(chapter.audioUri, chapter.title);
            item.subtitlePath = chapter.subtitleUri;
            book.chapters.push(item);
          }
        }
        books.push(book);
      }

      hilog.info(0x0000, BookStore.TAG, 'loaded %{public}d book(s) from store', books.length);
    } catch (error) {
      hilog.error(0x0000, BookStore.TAG, 'load library failed: %{public}s', JSON.stringify(error));
    }

    return books;
  }

  /** 保存导入的书 */
  static save(context: common.UIAbilityContext, books: Book[]): boolean {
    try {
      const payload = new StoredLibrary();
      for (const book of books) {
        const stored = new StoredBook();
        stored.title = book.title;
        stored.coverUri = book.coverPath;
        stored.chapters = [];
        for (const chapter of book.chapters) {
          const item = new StoredChapter();
          item.audioUri = chapter.audioPath;
          item.title = chapter.title;
          item.subtitleUri = chapter.subtitlePath;
          stored.chapters.push(item);
        }
        payload.books.push(stored);
      }

      const store = preferences.getPreferencesSync(context, { name: BookStore.PREFS_NAME });
      store.putSync(BookStore.KEY_LIBRARY, JSON.stringify(payload));
      store.flush();
      hilog.info(0x0000, BookStore.TAG, 'saved %{public}d book(s)', books.length);
      return true;
    } catch (error) {
      hilog.error(0x0000, BookStore.TAG, 'save library failed: %{public}s', JSON.stringify(error));
      return false;
    }
  }

  /** 按音频文件名取书名（去掉扩展名） */
  static titleFromUri(uri: string): string {
    const slash = uri.lastIndexOf('/');
    const fileName = slash >= 0 ? uri.substring(slash + 1) : uri;
    const decoded = decodeURIComponent(fileName);
    const dot = decoded.lastIndexOf('.');
    return dot > 0 ? decoded.substring(0, dot) : decoded;
  }

  /** 取文件名（带扩展名，已解码） */
  static fileFromUri(uri: string): string {
    const slash = uri.lastIndexOf('/');
    const fileName = slash >= 0 ? uri.substring(slash + 1) : uri;
    return decodeURIComponent(fileName);
  }

  /** 取扩展名（小写，含点） */
  static extFromUri(uri: string): string {
    const file = BookStore.fileFromUri(uri).toLowerCase();
    const dot = file.lastIndexOf('.');
    return dot > 0 ? file.substring(dot) : '';
  }
}
