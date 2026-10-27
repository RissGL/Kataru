import { preferences } from '@kit.ArkData';
import { common } from '@kit.AbilityKit';
import { hilog } from '@kit.PerformanceAnalysisKit';

/**
 * 配色主题（对应桌面版的"黑 / 米白 / 暗红 / 少量金色"配色）。
 *
 * ArkUI 的 $r('app.color.x') 是编译期资源，运行时改不了，
 * 所以这里用一组普通字符串字段，界面里直接绑定 this.theme.xxx。
 */
export class ThemeColors {
  pageBackground: string = '#121011';
  panelBackground: string = '#1A1718';
  textPrimary: string = '#F1EBE1';
  textSecondary: string = '#B5AFA6';
  accent: string = '#9E2B25';
  gold: string = '#C9A227';
  borderSoft: string = '#3A3330';

  constructor(
    pageBackground: string,
    panelBackground: string,
    textPrimary: string,
    textSecondary: string,
    accent: string,
    gold: string,
    borderSoft: string
  ) {
    this.pageBackground = pageBackground;
    this.panelBackground = panelBackground;
    this.textPrimary = textPrimary;
    this.textSecondary = textSecondary;
    this.accent = accent;
    this.gold = gold;
    this.borderSoft = borderSoft;
  }
}

/** 一套可选的配色 */
export class ThemePreset {
  name: string = '';
  colors: ThemeColors;

  constructor(name: string, colors: ThemeColors) {
    this.name = name;
    this.colors = colors;
  }
}

export const THEME_PRESETS: ThemePreset[] = [
  // 桌面版默认：黑底 + 米白字 + 暗红 + 金
  new ThemePreset('墨黑', new ThemeColors('#121011', '#1A1718', '#F1EBE1', '#B5AFA6', '#9E2B25', '#C9A227', '#3A3330')),
  // 米白（纸张感）
  new ThemePreset('米白', new ThemeColors('#F2EBDD', '#FBF6EA', '#2B2320', '#6B5F55', '#8C2F2A', '#B98A2E', '#D9CDB9')),
  // 深蓝墨
  new ThemePreset('夜蓝', new ThemeColors('#0E1420', '#18202E', '#E8EDF5', '#9AA7BA', '#3B6EA8', '#C9A227', '#2C3648')),
  // 森绿
  new ThemePreset('松绿', new ThemeColors('#0F1613', '#18211C', '#E9F0EA', '#9FB1A5', '#2F6B4F', '#C9A227', '#2B3A32')),
  // 纯黑（OLED）
  new ThemePreset('纯黑', new ThemeColors('#000000', '#121212', '#EDEDED', '#9E9E9E', '#B0362E', '#C9A227', '#2A2A2A')),
];

export class ThemeStore {
  private static readonly TAG: string = 'KATARU';
  private static readonly PREFS: string = 'kataru_settings';
  private static readonly KEY: string = 'theme_index';

  static load(context: common.UIAbilityContext): number {
    try {
      const store = preferences.getPreferencesSync(context, { name: ThemeStore.PREFS });
      const index = store.getSync(ThemeStore.KEY, 0) as number;
      if (index >= 0 && index < THEME_PRESETS.length) {
        return index;
      }
      return 0;
    } catch (error) {
      hilog.warn(0x0000, ThemeStore.TAG, 'load theme failed: %{public}s', JSON.stringify(error));
      return 0;
    }
  }

  static save(context: common.UIAbilityContext, index: number): void {
    try {
      const store = preferences.getPreferencesSync(context, { name: ThemeStore.PREFS });
      store.putSync(ThemeStore.KEY, index);
      store.flush();
    } catch (error) {
      hilog.warn(0x0000, ThemeStore.TAG, 'save theme failed: %{public}s', JSON.stringify(error));
    }
  }
}
