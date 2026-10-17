/**
 * 从字幕文件名里认语言（从桌面版 Core/SubtitleLanguage.cs 移植）。
 *
 * 支持：第01话.zh.srt / 第01话.chs.srt / 第01话.zh-CN.srt / 第01话.ja.srt /
 *       第01话.中文.srt / 第01话.eng.srt；没有语言标记的当成"原文"。
 */
export class SubtitleLanguage {
  static readonly UNKNOWN_CODE: string = '';
  static readonly UNKNOWN_NAME: string = '原文';

  /** 语言别名 → [代码, 显示名]。 */
  private static readonly aliases: Map<string, string[]> = SubtitleLanguage.buildAliases();

  private static buildAliases(): Map<string, string[]> {
    const map = new Map<string, string[]>();

    SubtitleLanguage.put(map, ['ja', 'jp', 'jpn', 'japanese', '日', '日语', '日語', '日本語'], 'ja', '日本語');
    SubtitleLanguage.put(map, ['zh', 'cn', 'chi', 'zho', 'chinese', '中', '中文'], 'zh', '中文');
    SubtitleLanguage.put(map, ['chs', 'sc', 'zhcn', 'zhhans', '简', '简体', '简体中文'], 'zh', '简体中文');
    SubtitleLanguage.put(map, ['cht', 'tc', 'zhtw', 'zhhant', '繁', '繁体', '繁體中文'], 'zh', '繁體中文');
    SubtitleLanguage.put(map, ['en', 'eng', 'english', '英', '英语', '英文'], 'en', 'English');
    SubtitleLanguage.put(map, ['ko', 'kr', 'kor', 'korean', '韩', '韩语', '한국어'], 'ko', '한국어');

    return map;
  }

  private static put(map: Map<string, string[]>, keys: string[], code: string, name: string): void {
    for (const key of keys) {
      map.set(key, [code, name]);
    }
  }

  /** 判断某个字幕文件是不是这条音频的（同名，或同名 + 语言后缀）。 */
  static isRelatedSubtitle(audioStem: string, subtitleStem: string): boolean {
    if (audioStem.length === 0 || subtitleStem.length === 0) {
      return false;
    }

    const audio = audioStem.toLowerCase();
    const subtitle = subtitleStem.toLowerCase();

    if (audio === subtitle) {
      return true;
    }

    if (!subtitle.startsWith(audio)) {
      return false;
    }

    // 后面必须紧跟分隔符，避免 01话 匹配到 01话extra
    const separator = subtitle.charAt(audio.length);
    return separator === '.' || separator === '-' || separator === '_' || separator === ' ' ||
      separator === '(' || separator === '（';
  }

  /**
   * 从字幕文件名里解析语言。
   * @param audioStem 音频文件名（不含扩展名）
   * @param subtitleFileName 字幕文件名（不含扩展名，或含扩展名都行——会去掉）
   */
  static detect(audioStem: string, subtitleFileName: string): string[] {
    let stem = subtitleFileName;
    const dot = stem.lastIndexOf('.');
    if (dot > 0) {
      stem = stem.substring(0, dot);
    }

    if (stem.toLowerCase() === audioStem.toLowerCase()) {
      return [SubtitleLanguage.UNKNOWN_CODE, SubtitleLanguage.UNKNOWN_NAME];
    }

    let tag = stem.toLowerCase().startsWith(audioStem.toLowerCase())
      ? stem.substring(audioStem.length)
      : stem;

    tag = SubtitleLanguage.normalize(tag);
    if (tag.length === 0) {
      return [SubtitleLanguage.UNKNOWN_CODE, SubtitleLanguage.UNKNOWN_NAME];
    }

    const direct = SubtitleLanguage.aliases.get(tag);
    if (direct !== undefined) {
      return direct;
    }

    // zh-CN / zh_TW / zh.Hans：去掉分隔符再试
    const compact = tag.replace(/-/g, '').replace(/_/g, '').replace(/\./g, '');
    const known = SubtitleLanguage.aliases.get(compact);
    if (known !== undefined) {
      return known;
    }

    // 认不出的标记原样显示，至少能区分
    return [tag.toLowerCase(), tag.toUpperCase()];
  }

  private static normalize(tag: string): string {
    let text = tag.trim();
    while (text.length > 0) {
      const first = text.charAt(0);
      if (first === '.' || first === '-' || first === '_' || first === ' ') {
        text = text.substring(1);
      } else {
        break;
      }
    }

    while (text.length > 0) {
      const last = text.charAt(text.length - 1);
      if (last === ')' || last === ']' || last === '）' || last === '】') {
        text = text.substring(0, text.length - 1);
      } else {
        break;
      }
    }

    return text.trim();
  }

  /** 语言代码 → 显示名。 */
  static displayName(code: string): string {
    if (code === '') {
      return SubtitleLanguage.UNKNOWN_NAME;
    }
    const known = SubtitleLanguage.aliases.get(code);
    return known !== undefined ? known[1] : code.toUpperCase();
  }
}
