using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using AudioBookPlayer.Audio;
using AudioBookPlayer.Core;
using AudioBookPlayer.Windows;

namespace AudioBookPlayer.ViewModels
{
    /// <summary>主界面的模式。</summary>
    public enum AppMode
    {
        /// <summary>书库：封面网格，挑书。</summary>
        Library,

        /// <summary>播放：封面 + 正在听的章节 + 当前字幕。</summary>
        Player,

        /// <summary>阅读：整篇字幕文本，当前朗读的一行自动高亮并滚动。</summary>
        Reading,

        /// <summary>设置：媒体库 / 播放 / 字幕 / 悬浮窗 / 数据存储。</summary>
        Settings,
    }

    /// <summary>阅读模式里的一行（包一层是为了能单独标记"当前朗读"）。</summary>
    public sealed class ReadingLine : ObservableObject
    {
        private bool _isCurrent;
        private string _secondaryText = string.Empty;

        public ReadingLine(SubtitleLine line)
        {
            Line = line;
        }

        public SubtitleLine Line { get; }

        public string Text => Line.Text;

        /// <summary>对照语言的那一句（阅读双语用，可能为 null）。</summary>
        public string SecondaryText
        {
            get => _secondaryText;
            set
            {
                if (SetProperty(ref _secondaryText, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(HasSecondaryText));
                }
            }
        }

        public bool HasSecondaryText => _secondaryText.Length > 0;

        public string TimeText => Timecode.Format(Line.StartTime);

        public bool HasSubtitle => Line.Text.Length > 0;

        public bool IsCurrent
        {
            get => _isCurrent;
            set => SetProperty(ref _isCurrent, value);
        }
    }

    /// <summary>
    /// 主窗口 ViewModel：媒体库（书 / 章节）→ 音频 → 字幕 → 悬浮窗口，全部在这里接线。
    ///
    /// 字幕同步的唯一时间基准是 <see cref="IAudioPlayer.Position"/>，不使用 DateTime.Now / Stopwatch。
    /// </summary>
    public sealed class MainViewModel : ObservableObject, IDisposable
    {
        private const int TickIntervalMilliseconds = 50;

        private readonly IAudioPlayer _audio;
        private readonly SubtitleSynchronizer _synchronizer = new SubtitleSynchronizer();

        /// <summary>副字幕的同步器（双语模式用）。</summary>
        private readonly SubtitleSynchronizer _secondarySynchronizer = new SubtitleSynchronizer();
        private readonly DispatcherTimer _ticker;
        private readonly AppSettings _settings;
        private readonly PlaybackStore _playback;


        private ISubtitleOverlay? _overlayWindow;
        private string? _libraryRoot;
        private LibraryBook? _selectedBook;
        private LibraryEntry? _selectedEntry;
        private ReadingLine? _currentReadingLine;
        private ReadingLine? _selectedReadingLine;
        private bool _updatingReadingSelection;
        private bool _suppressModeSwitch;
        private AppMode _currentMode = AppMode.Library;
        private string _libraryFilter = string.Empty;
        private bool _showOnlyWithSubtitle;
        private bool _showOnlyFavorites;
        private bool _minimizeToTray = true;
        private bool _loadingEntry;

        /// <summary>真正装载在播放器里的那一条（换书时它和 _selectedEntry 会短暂不一致）。</summary>
        private LibraryEntry? _loadedEntry;
        private bool _autoContinue = true;
        private bool _rememberPlaybackPosition = true;
        private bool _resumeLastOnStartup = true;
        private DateTime _lastRememberUtc = DateTime.MinValue;
        private TimeSpan? _resumeOnOpen;
        private readonly ThemeSettings _theme;
        private GlobalHotkeyService? _hotkeys;
        private bool _enableGlobalHotkeys = true;
        private AudioChapter? _currentEmbeddedChapter;
        private bool _isScanning;
        private bool _pendingAutoPlay;
        private string? _audioPath;
        private string? _subtitlePath;
        private string _statusText = "先选择一个媒体库文件夹，程序会递归扫描所有子目录，按文件夹整理成书并自动配对同名 .srt 字幕。";
        private double _positionSeconds;
        private double _durationSeconds;
        private bool _isPlaying;
        private bool _isSeeking;
        private bool _updatingFromTick;
        private double _volume = 1.0;
        private double _skipSeconds = 10;
        private bool _disposed;

        public MainViewModel(Func<IAudioPlayer>? audioPlayerFactory = null, AppSettings? settings = null, PlaybackStore? playbackStore = null)
        {
            _settings = settings ?? AppSettings.Load();
            _playback = playbackStore ?? PlaybackStore.Load();

            _audio = (audioPlayerFactory ?? (() => new MediaPlayerAudioPlayer()))();
            _audio.MediaOpened += OnAudioOpened;
            _audio.MediaEnded += OnAudioEnded;
            _audio.Failed += OnAudioFailed;

            _ticker = new DispatcherTimer(DispatcherPriority.Normal)
            {
                Interval = TimeSpan.FromMilliseconds(TickIntervalMilliseconds),
            };
            _ticker.Tick += OnTick;

            BooksView = new ListCollectionView(Books) { CustomSort = new BookComparer() };
            BooksView.Filter = FilterBook;

            ChooseFolderCommand = new RelayCommand(() => _ = ChooseFolderAsync(), () => !IsScanning);
            RefreshLibraryCommand = new RelayCommand(() => _ = RescanAsync(), () => !IsScanning && !string.IsNullOrEmpty(LibraryRoot));
            SwitchModeCommand = new RelayCommand(parameter =>
            {
                if (parameter is AppMode mode)
                {
                    CurrentMode = mode;
                }
            });

            SelectBookCommand = new RelayCommand(parameter =>
            {
                if (parameter is LibraryBook book)
                {
                    SelectedBook = book;
                    CurrentMode = AppMode.Player;
                }
            });

            ToggleSettingsCommand = new RelayCommand(() => CurrentMode = AppMode.Settings);

            TogglePlayPauseCommand = new RelayCommand(TogglePlayPause, () => _audio.IsOpen || _selectedBook != null);
            PlaySelectedCommand = new RelayCommand(PlaySelected, () => _selectedEntry != null || _selectedBook != null);
            StopCommand = new RelayCommand(Stop, () => _audio.IsOpen);
            SkipBackCommand = new RelayCommand(() => Skip(-_skipSeconds), () => _audio.IsOpen);
            SkipForwardCommand = new RelayCommand(() => Skip(_skipSeconds), () => _audio.IsOpen);
            PreviousChapterCommand = new RelayCommand(() => PlayNextEntry(-1), () => HasSelectableNeighbour(-1));
            NextChapterCommand = new RelayCommand(() => PlayNextEntry(1), () => HasSelectableNeighbour(1));
            ResetPlacementCommand = new RelayCommand(() => Overlay.ResetPlacement());
            ResetAppearanceCommand = new RelayCommand(() => Overlay.ResetAppearance());

            ContinueCommand = new RelayCommand(ContinueListening, () => HasContinue);
            OpenDataFolderCommand = new RelayCommand(OpenDataFolder);
            ClearPlaybackDataCommand = new RelayCommand(ClearPlaybackData);
            ResetAllSettingsCommand = new RelayCommand(ResetAllSettings);
            ToggleFavoriteCommand = new RelayCommand(parameter =>
            {
                var folder = parameter as string ?? _selectedBook?.FolderPath;
                ToggleFavorite(folder);
            });

            ChooseCoverCommand = new RelayCommand(parameter => ChooseCover(parameter as LibraryBook ?? _selectedBook));
            ClearCoverCommand = new RelayCommand(parameter => ClearCover(parameter as LibraryBook ?? _selectedBook));
            ToggleOverlayMoveCommand = new RelayCommand(() => Overlay.IsMovable = !Overlay.IsMovable);
            JumpChapterCommand = new RelayCommand(parameter =>
            {
                if (parameter is AudioChapter chapter)
                {
                    SeekTo(chapter.Start);
                }
            });
            ResetThemeCommand = new RelayCommand(ResetTheme);

            _theme = new ThemeSettings
            {
                Background = _settings.ThemeBackground,
                Foreground = _settings.ThemeForeground,
                Accent = _settings.ThemeAccent,
                Gold = _settings.ThemeGold,
                PresetName = _settings.ThemePresetName,
            };

            Overlay.PropertyChanged += OnOverlayPropertyChanged;

            ApplySettings();
        }

        // ---------------- 模式 ----------------

        public AppMode CurrentMode
        {
            get => _currentMode;
            set
            {
                if (SetProperty(ref _currentMode, value))
                {
                    OnPropertyChanged(nameof(IsLibraryMode));
                    OnPropertyChanged(nameof(IsPlayerMode));
                    OnPropertyChanged(nameof(IsReadingMode));
                    OnPropertyChanged(nameof(IsSettingsMode));
                    NotifyZoomChanged();
                }
            }
        }

        public bool IsLibraryMode => _currentMode == AppMode.Library;

        public bool IsPlayerMode => _currentMode == AppMode.Player;

        public bool IsReadingMode => _currentMode == AppMode.Reading;

        public RelayCommand SwitchModeCommand { get; }

        public RelayCommand SelectBookCommand { get; }

        public RelayCommand ToggleSettingsCommand { get; }



        // ---------------- 媒体库 ----------------

        /// <summary>媒体库里的所有书（按文件夹归并）。</summary>
        public ObservableCollection<LibraryBook> Books { get; } = new ObservableCollection<LibraryBook>();

        /// <summary>书库视图（搜索过滤 + 自然排序）。</summary>
        public ICollectionView BooksView { get; }

        /// <summary>当前书的章节列表。</summary>
        public ObservableCollection<LibraryEntry> Chapters { get; } = new ObservableCollection<LibraryEntry>();

        public string? LibraryRoot
        {
            get => _libraryRoot;
            private set
            {
                if (SetProperty(ref _libraryRoot, value))
                {
                    OnPropertyChanged(nameof(HasLibrary));
                    OnPropertyChanged(nameof(LibraryRootDisplay));
                }
            }
        }

        public string LibraryRootDisplay => string.IsNullOrEmpty(_libraryRoot) ? "（还没有选择文件夹）" : _libraryRoot;

        public bool HasLibrary => Books.Count > 0;

        public bool HasChapters => Chapters.Count > 0;

        /// <summary>侧栏右上角的章节计数。</summary>
        public string ChapterListLabel => Chapters.Count == 0 ? "—" : $"共 {Chapters.Count} 章";

        /// <summary>搜索框：匹配书名、文件夹与章节名。</summary>
        public string LibraryFilter
        {
            get => _libraryFilter;
            set
            {
                if (SetProperty(ref _libraryFilter, value ?? string.Empty))
                {
                    BooksView.Refresh();
                    RefreshChapters();
                    OnPropertyChanged(nameof(VisibleCountText));
                }
            }
        }

        public bool ShowOnlyWithSubtitle
        {
            get => _showOnlyWithSubtitle;
            set
            {
                if (SetProperty(ref _showOnlyWithSubtitle, value))
                {
                    BooksView.Refresh();
                    RefreshChapters();
                    OnPropertyChanged(nameof(VisibleCountText));
                }
            }
        }

        public bool IsScanning
        {
            get => _isScanning;
            private set => SetProperty(ref _isScanning, value);
        }

        public string VisibleCountText
        {
            get
            {
                var visible = 0;
                foreach (var _ in BooksView)
                {
                    visible++;
                }

                return $"共 {visible} 本 / {Books.Count} 本";
            }
        }

        /// <summary>当前选中的书。选中后自动载入第一章（不自动播放）。</summary>
        public LibraryBook? SelectedBook
        {
            get => _selectedBook;
            set
            {
                if (!SetProperty(ref _selectedBook, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(CurrentBookTitle));
                OnPropertyChanged(nameof(HasSelectedBook));
                RefreshChapters();

                if (value != null)
                {
                    var first = PickFirstChapter(value);
                    if (first != null)
                    {
                        _suppressModeSwitch = true;
                        try
                        {
                            SelectedEntry = first;
                        }
                        finally
                        {
                            _suppressModeSwitch = false;
                        }
                    }
                }
            }
        }

        public bool HasSelectedBook => _selectedBook != null;

        public string CurrentBookTitle => _selectedBook?.Title ?? "（未选择书）";

        /// <summary>当前章节（列表里的条目）。</summary>
        public LibraryEntry? SelectedEntry
        {
            get => _selectedEntry;
            set
            {
                if (!SetProperty(ref _selectedEntry, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(CurrentChapterTitle));
                OnPropertyChanged(nameof(ChapterProgressText));
                OnPropertyChanged(nameof(CurrentSubtitleInfo));

                if (value == null)
                {
                    return;
                }

                LoadEntry(value, _pendingAutoPlay);

                if (!_suppressModeSwitch && _currentMode == AppMode.Library)
                {
                    CurrentMode = AppMode.Player;
                }
            }
        }

        public string CurrentChapterTitle => _selectedEntry?.Title ?? "（未选择章节）";

        public string ChapterProgressText
        {
            get
            {
                if (_selectedEntry == null || Chapters.Count == 0)
                {
                    return string.Empty;
                }

                var index = Chapters.IndexOf(_selectedEntry);
                return index < 0 ? string.Empty : $"第 {index + 1} / {Chapters.Count} 章";
            }
        }

        public bool AutoContinue
        {
            get => _autoContinue;
            set => SetProperty(ref _autoContinue, value);
        }

        // ---------------- 命令 ----------------

        public RelayCommand ChooseFolderCommand { get; }

        public RelayCommand RefreshLibraryCommand { get; }

        public RelayCommand TogglePlayPauseCommand { get; }

        public RelayCommand PlaySelectedCommand { get; }

        public RelayCommand StopCommand { get; }

        public RelayCommand SkipBackCommand { get; }

        public RelayCommand SkipForwardCommand { get; }

        public RelayCommand PreviousChapterCommand { get; }

        public RelayCommand NextChapterCommand { get; }

        public RelayCommand ResetPlacementCommand { get; }

        public RelayCommand ResetAppearanceCommand { get; }

        public RelayCommand ContinueCommand { get; }

        public RelayCommand OpenDataFolderCommand { get; }

        public RelayCommand ClearPlaybackDataCommand { get; }

        public RelayCommand ResetAllSettingsCommand { get; }

        public RelayCommand ToggleFavoriteCommand { get; }

        public RelayCommand ChooseCoverCommand { get; }

        public RelayCommand ClearCoverCommand { get; }

        /// <summary>移动字幕 / 锁定鼠标穿透。</summary>
        public RelayCommand ToggleOverlayMoveCommand { get; }

        public RelayCommand JumpChapterCommand { get; }

        public RelayCommand ResetThemeCommand { get; }

        // ---------------- 配色方案 ----------------

        public System.Collections.Generic.IReadOnlyList<ThemeSettings> ThemePresets => ThemeSettings.Presets;

        /// <summary>选一个预设配色（改完立刻生效）。</summary>
        public ThemeSettings? SelectedThemePreset
        {
            get => ThemeSettings.Presets.FirstOrDefault(p =>
                string.Equals(p.PresetName, _theme.PresetName, StringComparison.Ordinal));
            set
            {
                if (value == null)
                {
                    return;
                }

                _theme.Background = value.Background;
                _theme.Foreground = value.Foreground;
                _theme.Accent = value.Accent;
                _theme.Gold = value.Gold;
                _theme.PresetName = value.PresetName;

                ApplyTheme();
                OnPropertyChanged(nameof(ThemeBackground));
                OnPropertyChanged(nameof(ThemeForeground));
                OnPropertyChanged(nameof(ThemeAccent));
                OnPropertyChanged(nameof(ThemeGold));
            }
        }

        public string ThemeBackground
        {
            get => _theme.Background;
            set => SetThemeColor(value, c => _theme.Background = c, nameof(ThemeBackground));
        }

        public string ThemeForeground
        {
            get => _theme.Foreground;
            set => SetThemeColor(value, c => _theme.Foreground = c, nameof(ThemeForeground));
        }

        public string ThemeAccent
        {
            get => _theme.Accent;
            set => SetThemeColor(value, c => _theme.Accent = c, nameof(ThemeAccent));
        }

        public string ThemeGold
        {
            get => _theme.Gold;
            set => SetThemeColor(value, c => _theme.Gold = c, nameof(ThemeGold));
        }

        /// <summary>可选的背景色 / 强调色快捷色块。</summary>
        public IReadOnlyList<string> ThemeBackgroundChoices { get; } = new[]
        {
            "#121011", "#0E1116", "#0F1411", "#120F16", "#1A1A1C", "#2B2622", "#F4F1EC", "#FFFFFF",
        };

        public IReadOnlyList<string> ThemeAccentChoices { get; } = new[]
        {
            "#9E2B25", "#C0392B", "#E67E22", "#3B82F6", "#2F7D5B", "#7C4DFF", "#D81B60", "#00838F",
        };

        public IReadOnlyList<string> ThemeGoldChoices { get; } = new[]
        {
            "#C9A227", "#E0B341", "#A8791A", "#D8B4FE", "#6FB6D9", "#9AA0A6", "#E25830", "#F1EBE1",
        };

        // ---------------- 全局热键 ----------------

        /// <summary>是否启用全局热键。</summary>
        public bool EnableGlobalHotkeys
        {
            get => _enableGlobalHotkeys;
            set
            {
                if (SetProperty(ref _enableGlobalHotkeys, value))
                {
                    _settings.EnableGlobalHotkeys = value;
                    RegisterHotkeys();
                    PersistSettings();
                }
            }
        }

        /// <summary>设置页里展示的热键表（显示实际生效的组合）。</summary>
        public IReadOnlyList<RegisteredHotkey> HotkeyList { get; private set; } =
            DefaultHotkeys.Create(10).Select(d => new RegisteredHotkey(d, d.Candidates[0].Modifiers, d.Candidates[0].Key)).ToList();

        public string HotkeyStatusText => _hotkeys == null
            ? "全局热键会在主窗口打开后注册；下面是默认组合。"
            : !EnableGlobalHotkeys
                ? "全局热键已关闭"
                : _hotkeys.Failed.Count == 0
                    ? $"已注册 {HotkeyList.Count} 个全局热键"
                    : $"{_hotkeys.Failed.Count} 个热键的所有候选组合都被占用：{string.Join("、", _hotkeys.Failed.Select(h => h.Description))}";

        // ---------------- 文件内章节（m4b chpl） ----------------

        /// <summary>当前音频文件内部的章节标记。</summary>
        public System.Collections.ObjectModel.ObservableCollection<AudioChapter> EmbeddedChapters { get; } = new();

        public bool HasEmbeddedChapters => EmbeddedChapters.Count > 0;

        /// <summary>当前正在播的那一条内嵌章节。</summary>
        public AudioChapter? CurrentEmbeddedChapter
        {
            get => _currentEmbeddedChapter;
            private set
            {
                if (SetProperty(ref _currentEmbeddedChapter, value))
                {
                    OnPropertyChanged(nameof(CurrentEmbeddedChapterTitle));
                }
            }
        }

        public string CurrentEmbeddedChapterTitle => _currentEmbeddedChapter?.Title ?? string.Empty;

        // ---------------- 设置项 ----------------

        public bool IsSettingsMode => _currentMode == AppMode.Settings;

        /// <summary>记住每个章节听到哪儿。</summary>
        public bool RememberPlaybackPosition
        {
            get => _rememberPlaybackPosition;
            set
            {
                if (SetProperty(ref _rememberPlaybackPosition, value))
                {
                    _settings.RememberPlaybackPosition = value;
                    PersistSettings();
                }
            }
        }

        /// <summary>启动时自动恢复上次听的那一章。</summary>
        public bool ResumeLastOnStartup
        {
            get => _resumeLastOnStartup;
            set
            {
                if (SetProperty(ref _resumeLastOnStartup, value))
                {
                    _settings.ResumeLastOnStartup = value;
                    PersistSettings();
                }
            }
        }

        // ---------------- 界面缩放（Ctrl + 滚轮，像浏览器那样） ----------------

        public const double MinZoom = 0.7;
        public const double MaxZoom = 2.0;
        private const double ZoomStep = 0.1;

        private double _libraryZoom = 1.0;
        private double _playerZoom = 1.0;
        private double _readingZoom = 1.0;

        /// <summary>书库视图缩放。</summary>
        public double LibraryZoom
        {
            get => _libraryZoom;
            set => SetZoom(ref _libraryZoom, value, nameof(LibraryZoom));
        }

        /// <summary>播放视图缩放。</summary>
        public double PlayerZoom
        {
            get => _playerZoom;
            set => SetZoom(ref _playerZoom, value, nameof(PlayerZoom));
        }

        /// <summary>阅读视图缩放。</summary>
        public double ReadingZoom
        {
            get => _readingZoom;
            set => SetZoom(ref _readingZoom, value, nameof(ReadingZoom));
        }

        /// <summary>当前视图的缩放（状态栏显示用）。</summary>
        public double ActiveZoom => CurrentMode switch
        {
            AppMode.Player => _playerZoom,
            AppMode.Reading => _readingZoom,
            AppMode.Library => _libraryZoom,
            _ => 1.0,
        };

        /// <summary>缩放百分比文字（100% 时不显示）。</summary>
        public string ZoomText => Math.Abs(ActiveZoom - 1.0) < 0.001
            ? string.Empty
            : $"{ActiveZoom * 100:0}%";

        public bool IsZoomed => ZoomText.Length > 0;

        /// <summary>Ctrl + 滚轮：正数放大。</summary>
        public void AdjustZoom(int direction)
        {
            var delta = direction > 0 ? ZoomStep : -ZoomStep;

            switch (CurrentMode)
            {
                case AppMode.Player:
                    PlayerZoom = _playerZoom + delta;
                    break;
                case AppMode.Reading:
                    ReadingZoom = _readingZoom + delta;
                    break;
                case AppMode.Library:
                    LibraryZoom = _libraryZoom + delta;
                    break;
                default:
                    return; // 设置页不缩放
            }
        }

        /// <summary>Ctrl + 0：回到 100%。</summary>
        public void ResetZoom()
        {
            switch (CurrentMode)
            {
                case AppMode.Player:
                    PlayerZoom = 1.0;
                    break;
                case AppMode.Reading:
                    ReadingZoom = 1.0;
                    break;
                case AppMode.Library:
                    LibraryZoom = 1.0;
                    break;
            }
        }

        private void SetZoom(ref double field, double value, string propertyName)
        {
            var clamped = Math.Clamp(Math.Round(value, 2), MinZoom, MaxZoom);
            if (Math.Abs(field - clamped) < 0.0001)
            {
                return;
            }

            field = clamped;
            OnPropertyChanged(propertyName);
            NotifyZoomChanged();
            PersistSettings();
        }

        private void NotifyZoomChanged()
        {
            OnPropertyChanged(nameof(ActiveZoom));
            OnPropertyChanged(nameof(ZoomText));
            OnPropertyChanged(nameof(IsZoomed));
        }
        // ---------------- 界面字体 ----------------

        /// <summary>内置默认界面字体（和主题资源里的初值一致）。</summary>
        public const string DefaultUiFontName = "Yu Gothic UI";

        private string _uiFontName = string.Empty;

        /// <summary>界面字体名（空 = 用默认）。改完立即生效并存盘。</summary>
        public string UiFontName
        {
            get => _uiFontName;
            set
            {
                var name = (value ?? string.Empty).Trim();
                if (!SetProperty(ref _uiFontName, name))
                {
                    return;
                }

                ApplyUiFont();
                OnPropertyChanged(nameof(UiFontDisplay));
                OnPropertyChanged(nameof(IsDefaultUiFont));
                _settings.UiFontFamilyName = name;
                PersistSettings();
            }
        }

        /// <summary>
        /// 给输入框显示用：没设置时显示内置默认字体名（而不是空白框），
        /// 用户把默认名字填回来等于"恢复默认"。
        /// </summary>
        public string UiFontDisplay
        {
            get => string.IsNullOrWhiteSpace(_uiFontName) ? DefaultUiFontName : _uiFontName;
            set
            {
                var name = (value ?? string.Empty).Trim();
                UiFontName = string.Equals(name, DefaultUiFontName, StringComparison.OrdinalIgnoreCase)
                    ? string.Empty
                    : name;
            }
        }
        /// <summary>设置界面字体（供界面调用；传默认名等于恢复默认）。</summary>
        public void SetUiFont(string? name) => UiFontDisplay = name ?? string.Empty;
        /// <summary>界面字体是不是默认的。</summary>
        public bool IsDefaultUiFont => string.IsNullOrWhiteSpace(_uiFontName);

        /// <summary>把界面字体写进应用资源（DynamicResource 引用它，所以会实时刷新）。</summary>
        private void ApplyUiFont()
        {
            try
            {
                var family = string.IsNullOrWhiteSpace(_uiFontName)
                    ? SubtitleOverlayViewModel.ResolveFont(DefaultUiFontName)
                    : SubtitleOverlayViewModel.ResolveFont(_uiFontName);

                var app = System.Windows.Application.Current;
                if (app == null)
                {
                    return;
                }

                // ① 换资源：以后新建的窗口会用它
                app.Resources["AppFontFamily"] = family;

                // ② 直接设到"已经打开的窗口"上。
                //    样式 Setter 里的 DynamicResource 不保证会回头刷新已经生效的属性，
                //    所以改完当场没反应 —— 这一步才保证立刻生效。
                foreach (System.Windows.Window window in app.Windows)
                {
                    window.FontFamily = family;

                    // 光设窗口还不够：内容根元素自己也要设一次。
                    // TextElement.FontFamily 是继承属性，设在根上，整棵树里没有显式指定字体的控件都会跟着变。
                    if (window.Content is DependencyObject root)
                    {
                        System.Windows.Documents.TextElement.SetFontFamily(root, family);
                    }
                }
            }
            catch (Exception)
            {
                // 字体名无效就维持原样
            }
        }
        /// <summary>关主窗口时收进托盘（而不是退出程序）。</summary>
        public bool MinimizeToTray
        {
            get => _minimizeToTray;
            set
            {
                if (SetProperty(ref _minimizeToTray, value))
                {
                    PersistSettings();
                }
            }
        }

        /// <summary>书库只显示收藏的书。</summary>
        public bool ShowOnlyFavorites
        {
            get => _showOnlyFavorites;
            set
            {
                if (SetProperty(ref _showOnlyFavorites, value))
                {
                    _settings.ShowOnlyFavorites = value;
                    BooksView.Refresh();
                    RefreshChapters();
                    OnPropertyChanged(nameof(VisibilityStats));
                    PersistSettings();
                }
            }
        }

        public string SettingsFilePath => _settings.FilePath ?? AppSettings.DefaultPath;

        public string PlaybackFilePath => _playback.FilePath ?? PlaybackStore.DefaultPath;

        public string StorageSummary => $"播放记录 {_playback.Count} 条 · 收藏 {FavoriteCount} 本";

        public int FavoriteCount
        {
            get
            {
                var count = 0;
                foreach (var book in Books)
                {
                    if (_playback.IsFavorite(book.FolderPath))
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        public const string AppName = "KATARU";

        public string VersionText => "0.5.0";

        public string RuntimeText => $".NET {Environment.Version} · WPF · Win32 (P/Invoke) · 零第三方依赖";

        // ---------------- 阅读 ----------------

        /// <summary>整篇字幕，阅读模式用。</summary>
        public ObservableCollection<ReadingLine> ReadingLines { get; } = new ObservableCollection<ReadingLine>();

        public bool HasReadingLines => ReadingLines.Count > 0;

        /// <summary>阅读模式里被选中 / 正在朗读的那一行；点击某一行会跳到对应时间。</summary>
        public ReadingLine? SelectedReadingLine
        {
            get => _selectedReadingLine;
            set
            {
                if (!SetProperty(ref _selectedReadingLine, value))
                {
                    return;
                }

                if (!_updatingReadingSelection && value != null)
                {
                    SeekTo(value.Line.StartTime);
                }
            }
        }

        // ---------------- 字幕语言（多轨 / 双语） ----------------

        /// <summary>当前章节配到的所有字幕轨。</summary>
        public System.Collections.ObjectModel.ObservableCollection<SubtitleTrack> AvailableTracks { get; } = new();

        /// <summary>副字幕下拉里的"不显示"哨兵。</summary>
        public static SubtitleTrack NoSecondaryTrack { get; } = new SubtitleTrack(string.Empty, "__none__", "（不显示）");

        /// <summary>副字幕可选项：不显示 + 当前章节的所有轨。</summary>
        public System.Collections.ObjectModel.ObservableCollection<SubtitleTrack> SecondaryOptions { get; } = new();

        private SubtitleTrack? _primaryTrack;
        private SubtitleTrack? _secondaryTrack;
        private SubtitleTrack? _readingTrack;
        private SubtitleTrack? _readingSecondaryTrack;

        /// <summary>主字幕轨。</summary>
        public SubtitleTrack? PrimaryTrack
        {
            get => _primaryTrack;
            set
            {
                if (!SetProperty(ref _primaryTrack, value) || value == null)
                {
                    return;
                }

                _settings.PreferredPrimaryLanguage = value.LanguageCode;

                // 只换悬浮字幕的语言；阅读模式有自己的选择，不动它
                LoadTrackLines(value, _synchronizer);

                SubtitlePath = value.FilePath;
                OnPropertyChanged(nameof(HasSubtitle));
                OnPropertyChanged(nameof(SubtitleCount));

                RefreshSubtitle(_audio.Position);

                if (_secondaryTrack != null && _secondaryTrack.LanguageCode == value.LanguageCode)
                {
                    SecondaryTrack = NoSecondaryTrack; // 主副同语言没意义
                }

                PersistSettings();
            }
        }

        // ---------------- 阅读模式的语言（和悬浮字幕解耦） ----------------

        /// <summary>阅读模式的主语言轨。</summary>
        public SubtitleTrack? ReadingTrack
        {
            get => _readingTrack;
            set
            {
                if (SetProperty(ref _readingTrack, value) && value != null)
                {
                    _settings.PreferredReadingLanguage = value.LanguageCode;

                    if (_readingSecondaryTrack != null && _readingSecondaryTrack.LanguageCode == value.LanguageCode)
                    {
                        ReadingSecondaryTrack = NoSecondaryTrack; // 主副同语言没意义
                    }

                    RebuildReadingLines();
                    PersistSettings();
                }
            }
        }

        /// <summary>阅读模式的对照语言轨（阅读双语）。</summary>
        public SubtitleTrack? ReadingSecondaryTrack
        {
            get => _readingSecondaryTrack;
            set
            {
                var track = value == NoSecondaryTrack ? null : value;
                if (!SetProperty(ref _readingSecondaryTrack, track))
                {
                    return;
                }

                _settings.PreferredReadingSecondaryLanguage = track?.LanguageCode ?? "__none__";
                RebuildReadingLines();
                OnPropertyChanged(nameof(ReadingSecondarySelection));
                OnPropertyChanged(nameof(HasReadingSecondary));
                PersistSettings();
            }
        }

        /// <summary>给下拉用：没选对照语言时显示「（不显示）」。</summary>
        public SubtitleTrack ReadingSecondarySelection
        {
            get => _readingSecondaryTrack ?? NoSecondaryTrack;
            set => ReadingSecondaryTrack = value ?? NoSecondaryTrack;
        }

        public bool HasReadingSecondary => _readingSecondaryTrack != null;

        /// <summary>阅读模式可选的对照语言（和副字幕共用一份选项，但选择互不影响）。</summary>
        public System.Collections.ObjectModel.ObservableCollection<SubtitleTrack> ReadingOptions { get; } = new();

        /// <summary>按当前的两条阅读轨重建阅读列表。</summary>
        private void RebuildReadingLines()
        {
            var primaryLines = _readingTrack == null ? null : LoadLinesOnly(_readingTrack);
            var secondaryLines = _readingSecondaryTrack == null ? null : LoadLinesOnly(_readingSecondaryTrack);

            if (primaryLines == null || primaryLines.Count == 0)
            {
                BuildReadingLines(Array.Empty<SubtitleLine>());
                return;
            }

            BuildReadingLines(primaryLines, secondaryLines);
        }

        /// <summary>只解析不装载（阅读轨用它，不动播放同步器）。</summary>
        private static IReadOnlyList<SubtitleLine> LoadLinesOnly(SubtitleTrack track)
        {
            try
            {
                return SrtParser.ParseFile(track.FilePath).Lines;
            }
            catch (Exception)
            {
                return Array.Empty<SubtitleLine>();
            }
        }
        /// <summary>副字幕轨（双语模式的第二行）。</summary>
        public SubtitleTrack? SecondaryTrack
        {
            get => _secondaryTrack;
            set
            {
                var track = value == NoSecondaryTrack ? null : value;
                if (!SetProperty(ref _secondaryTrack, track))
                {
                    return;
                }

                _settings.PreferredSecondaryLanguage = track?.LanguageCode ?? "__none__";

                if (track == null)
                {
                    _secondarySynchronizer.Clear();
                    Overlay.SecondaryText = string.Empty;
                }
                else
                {
                    LoadTrackLines(track, _secondarySynchronizer);
                    RefreshSubtitle(_audio.Position);
                }

                OnPropertyChanged(nameof(HasSecondaryTrack));
                OnPropertyChanged(nameof(SecondarySelection));
                PersistSettings();
            }
        }

        /// <summary>给下拉用的选择项：没选副字幕时显示"（不显示）"这个哨兵，而不是空白。</summary>
        public SubtitleTrack SecondarySelection
        {
            get => _secondaryTrack ?? NoSecondaryTrack;
            set => SecondaryTrack = value ?? NoSecondaryTrack;
        }

        public bool HasSecondaryTrack => _secondaryTrack != null;

        /// <summary>当前章节有没有多种语言可选。</summary>
        public bool HasMultipleLanguages => AvailableTracks.Count > 1;

        /// <summary>解析一条字幕轨并装进对应的同步器，返回解析出来的行（阅读模式要用同一份）。</summary>
        private IReadOnlyList<SubtitleLine> LoadTrackLines(SubtitleTrack track, SubtitleSynchronizer synchronizer)
        {
            try
            {
                var result = SrtParser.ParseFile(track.FilePath);
                synchronizer.Load(result.Lines);
                return result.Lines;
            }
            catch (Exception ex)
            {
                StatusText = $"加载字幕失败（{track.DisplayName}）：{ex.Message}";
                return Array.Empty<SubtitleLine>();
            }
        }

        /// <summary>按当前章节的字幕轨刷新两个下拉，并按用户偏好挑主/副轨。</summary>
        private void LoadSubtitleTracks(LibraryEntry entry)
        {
            AvailableTracks.Clear();
            foreach (var track in entry.SubtitleTracks)
            {
                AvailableTracks.Add(track);
            }

            SecondaryOptions.Clear();
            SecondaryOptions.Add(NoSecondaryTrack);
            ReadingOptions.Clear();
            ReadingOptions.Add(NoSecondaryTrack);
            foreach (var track in entry.SubtitleTracks)
            {
                SecondaryOptions.Add(track);
                ReadingOptions.Add(track);
            }

            OnPropertyChanged(nameof(HasMultipleLanguages));

            // 主轨：优先用户偏好，其次第一条
            var preferredPrimary = entry.FindTrack(_settings.PreferredPrimaryLanguage)
                                   ?? (entry.SubtitleTracks.Count > 0 ? entry.SubtitleTracks[0] : null);
            _primaryTrack = preferredPrimary;
            OnPropertyChanged(nameof(PrimaryTrack));

            // 悬浮字幕轨：装进同步器
            if (preferredPrimary != null)
            {
                LoadTrackLines(preferredPrimary, _synchronizer);
                SubtitlePath = preferredPrimary.FilePath;
            }
            else
            {
                _synchronizer.Clear();
                SubtitlePath = null;
            }

            // 阅读轨：独立选择（默认跟随悬浮字幕的语言，之后就各改各的）
            var preferredReading = entry.FindTrack(_settings.PreferredReadingLanguage)
                                   ?? preferredPrimary;

            _readingTrack = preferredReading;
            OnPropertyChanged(nameof(ReadingTrack));

            var preferredReadingSecondary = _settings.PreferredReadingSecondaryLanguage is null or "__none__"
                ? null
                : entry.FindTrack(_settings.PreferredReadingSecondaryLanguage);

            if (preferredReadingSecondary != null &&
                preferredReadingSecondary.LanguageCode == preferredReading?.LanguageCode)
            {
                preferredReadingSecondary = null;
            }

            _readingSecondaryTrack = preferredReadingSecondary;
            OnPropertyChanged(nameof(ReadingSecondaryTrack));
            OnPropertyChanged(nameof(ReadingSecondarySelection));
            OnPropertyChanged(nameof(HasReadingSecondary));

            RebuildReadingLines();

            // 副轨：偏好且不能和主轨同语言
            var preferredSecondary = _settings.PreferredSecondaryLanguage is null or "__none__"
                ? null
                : entry.FindTrack(_settings.PreferredSecondaryLanguage);

            if (preferredSecondary != null && preferredSecondary.LanguageCode == preferredPrimary?.LanguageCode)
            {
                preferredSecondary = null;
            }

            _secondaryTrack = preferredSecondary;
            OnPropertyChanged(nameof(SecondaryTrack));
            OnPropertyChanged(nameof(HasSecondaryTrack));
            OnPropertyChanged(nameof(SecondarySelection));

            if (preferredSecondary != null)
            {
                LoadTrackLines(preferredSecondary, _secondarySynchronizer);
            }
            else
            {
                _secondarySynchronizer.Clear();
            }
        }
        // ---------------- 播放状态 ----------------

        public SubtitleOverlayViewModel Overlay { get; } = new SubtitleOverlayViewModel();

        public string? AudioPath
        {
            get => _audioPath;
            private set
            {
                if (SetProperty(ref _audioPath, value))
                {
                    OnPropertyChanged(nameof(AudioFileName));
                }
            }
        }

        public string? SubtitlePath
        {
            get => _subtitlePath;
            private set
            {
                if (SetProperty(ref _subtitlePath, value))
                {
                    OnPropertyChanged(nameof(SubtitleFileName));
                }
            }
        }

        public string AudioFileName => string.IsNullOrEmpty(_audioPath) ? "（未打开）" : Path.GetFileName(_audioPath);

        public string SubtitleFileName => string.IsNullOrEmpty(_subtitlePath) ? "（无字幕）" : Path.GetFileName(_subtitlePath);

        public string CurrentSubtitleInfo => _selectedEntry == null
            ? "（未选择章节）"
            : (_selectedEntry.HasSubtitle ? Path.GetFileName(_selectedEntry.SubtitlePath!) : "本章没有字幕");

        public string StatusText
        {
            get => _statusText;
            private set => SetProperty(ref _statusText, value);
        }

        public double PositionSeconds
        {
            get => _positionSeconds;
            set
            {
                if (!SetProperty(ref _positionSeconds, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(PositionText));

                if (!_updatingFromTick && !_isSeeking && _audio.IsOpen)
                {
                    SeekTo(TimeSpan.FromSeconds(value));
                }
            }
        }

        public double DurationSeconds
        {
            get => _durationSeconds;
            private set
            {
                if (SetProperty(ref _durationSeconds, value))
                {
                    OnPropertyChanged(nameof(DurationText));
                    OnPropertyChanged(nameof(HasDuration));
                }
            }
        }

        public bool HasDuration => _durationSeconds > 0;

        public string PositionText => Timecode.Format(TimeSpan.FromSeconds(_positionSeconds));

        public string DurationText => _durationSeconds > 0
            ? Timecode.Format(TimeSpan.FromSeconds(_durationSeconds))
            : "--:--:--";

        public bool IsPlaying
        {
            get => _isPlaying;
            private set
            {
                if (SetProperty(ref _isPlaying, value))
                {
                    OnPropertyChanged(nameof(PlayPauseText));
                    OnPropertyChanged(nameof(PlayPauseGlyph));
                }
            }
        }

        public string PlayPauseText => _isPlaying ? "暂停" : "播放";

        /// <summary>底部播放条上的大按钮图标。</summary>
        public string PlayPauseGlyph => _isPlaying ? "⏸" : "▶";

        public bool IsSeeking
        {
            get => _isSeeking;
            private set => SetProperty(ref _isSeeking, value);
        }

        // ---------------- 播放倍速 ----------------

        public const double MinSpeed = 0.3;
        public const double MaxSpeed = 3.0;


        /// <summary>音量是不是 0（界面会提示"已静音"，避免被误当成"暂停后没声音"的 bug）。</summary>
        public bool IsMuted => _volume <= 0.001;

        public double Volume
        {
            get => _volume;
            set
            {
                if (SetProperty(ref _volume, Math.Clamp(value, 0, 1)))
                {
                    _audio.Volume = _volume;
                    OnPropertyChanged(nameof(IsMuted));
                }
            }
        }

        public double SkipSeconds
        {
            get => _skipSeconds;
            set
            {
                if (SetProperty(ref _skipSeconds, Math.Clamp(value, 1, 600)))
                {
                    OnPropertyChanged(nameof(SkipBackLabel));
                    OnPropertyChanged(nameof(SkipForwardLabel));
                }
            }
        }

        public string SkipBackLabel => $"⟲ {_skipSeconds:0}s";

        public string SkipForwardLabel => $"⟳ {_skipSeconds:0}s";

        public bool HasSubtitle => _synchronizer.Count > 0;

        public int SubtitleCount => _synchronizer.Count;

        public SubtitleLine? CurrentSubtitle => _synchronizer.Current;

        // ---------------- 生命周期 ----------------

        public void AttachOverlay(ISubtitleOverlay overlay)
        {
            _overlayWindow = overlay ?? throw new ArgumentNullException(nameof(overlay));
            overlay.ApplyPlacement();
        }

        public void Start()
        {
            if (!_ticker.IsEnabled)
            {
                _ticker.Start();
            }
        }

        public void StopTicker()
        {
            if (_ticker.IsEnabled)
            {
                _ticker.Stop();
            }
        }

        /// <summary>启动时恢复上次的媒体库并扫描一次。</summary>
        public void RestoreLibrary()
        {
            var folder = _settings.LibraryFolder;
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
            {
                _ = ScanLibraryAsync(folder);
            }
            else if (!string.IsNullOrEmpty(folder))
            {
                StatusText = $"上次的媒体库目录已不存在：{folder}";
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            StopTicker();
            _ticker.Tick -= OnTick;
            Overlay.PropertyChanged -= OnOverlayPropertyChanged;
            _audio.MediaOpened -= OnAudioOpened;
            _audio.MediaEnded -= OnAudioEnded;
            _audio.Failed -= OnAudioFailed;
            _audio.Dispose();

            _hotkeys?.Dispose();
            _hotkeys = null;

            RememberCurrentPosition(force: true);
            _playback.Save(force: true);
            CaptureSettings().Save();
        }

        // ---------------- 扫描 ----------------

        private async Task ChooseFolderAsync()
        {
            try
            {
                var dialog = new Microsoft.Win32.OpenFolderDialog
                {
                    Title = "选择媒体库文件夹（会递归扫描子目录）",
                    Multiselect = false,
                };

                if (!string.IsNullOrEmpty(LibraryRoot) && Directory.Exists(LibraryRoot))
                {
                    dialog.InitialDirectory = LibraryRoot;
                }

                if (dialog.ShowDialog() == true)
                {
                    await ScanLibraryAsync(dialog.FolderName);
                }
            }
            catch (Exception ex)
            {
                StatusText = "选择文件夹失败：" + ex.Message;
            }
        }

        private Task RescanAsync()
        {
            return string.IsNullOrEmpty(LibraryRoot) ? Task.CompletedTask : ScanLibraryAsync(LibraryRoot);
        }

        /// <summary>递归扫描媒体库（后台线程做 IO，扫完回 UI 线程刷新）。</summary>
        public async Task ScanLibraryAsync(string folder)
        {
            IsScanning = true;
            StatusText = $"正在扫描：{folder} …";

            try
            {
                var result = await Task.Run(() => MediaLibraryScanner.Scan(folder, _playback, CoversDirectory));

                var previousBook = _selectedBook?.FolderPath;
                var previousEntry = _selectedEntry?.AudioPath;

                Books.Clear();
                foreach (var book in result.Books)
                {
                    Books.Add(book);
                }

                LibraryRoot = result.Root;
                BooksView.Refresh();
                OnPropertyChanged(nameof(HasLibrary));
                OnPropertyChanged(nameof(VisibleCountText));

                UpdateContinueEntry();
                OnPropertyChanged(nameof(StorageSummary));
                OnPropertyChanged(nameof(FavoriteCount));

                // 重新扫描后尽量回到原来那本书 / 那一章
                RestoreSelection(previousBook, previousEntry);

                if (result.Entries.Count == 0)
                {
                    StatusText = "这个文件夹里没有找到音频文件（支持 mp3 / wav / m4a / m4b / wma / aac / mp4 / flac / aiff / ogg / opus）。";
                }
                else
                {
                    var covers = 0;
                    foreach (var book in result.Books)
                    {
                        if (book.CoverPath != null)
                        {
                            covers++;
                        }
                    }

                    var message = $"媒体库：{result.Books.Count} 本书 / {result.Entries.Count} 个章节" +
                                  $"（{result.CountWithSubtitle} 章有字幕，{covers} 本找到封面），用时 {result.Elapsed.TotalMilliseconds:0} ms。";

                    if (result.SkippedFolders.Count > 0)
                    {
                        message += $" {result.SkippedFolders.Count} 个目录没有读取权限，已跳过。";
                    }

                    StatusText = message;
                }

                // 立刻把媒体库目录写进设置：以前只在退出时保存，
                // 程序被强杀 / 崩溃，或者写盘悄悄失败，都会表现为"下次打开又要重新选文件夹"。
                _settings.LibraryFolder = result.Root;
                PersistSettings();

                if (HasSettingsSaveError)
                {
                    StatusText += $" ⚠ 设置写盘失败，媒体库目录可能不会被记住：{SettingsSaveErrorText}";
                }

                // 启动时接着上次那一章
                if (ResumeLastOnStartup && _selectedEntry == null && ContinueEntry != null)
                {
                    SelectedBook = ContinueBook;
                    _suppressModeSwitch = true;
                    try
                    {
                        SelectedEntry = ContinueEntry;
                    }
                    finally
                    {
                        _suppressModeSwitch = false;
                    }
                }
            }
            catch (Exception ex)
            {
                StatusText = "扫描失败：" + ex.Message;
            }
            finally
            {
                IsScanning = false;
            }
        }

        private void RestoreSelection(string? previousBook, string? previousEntry)
        {
            if (!string.IsNullOrEmpty(previousEntry))
            {
                foreach (var book in Books)
                {
                    foreach (var entry in book.Entries)
                    {
                        if (string.Equals(entry.AudioPath, previousEntry, StringComparison.OrdinalIgnoreCase))
                        {
                            _suppressModeSwitch = true;
                            try
                            {
                                SelectedBook = book;
                                SelectedEntry = entry;
            NotifyChapterNavigation();
                            }
                            finally
                            {
                                _suppressModeSwitch = false;
                            }

                            return;
                        }
                    }
                }
            }

            if (!string.IsNullOrEmpty(previousBook))
            {
                foreach (var book in Books)
                {
                    if (string.Equals(book.FolderPath, previousBook, StringComparison.OrdinalIgnoreCase))
                    {
                        SelectedBook = book;
                        return;
                    }
                }
            }

            if (_selectedBook == null && Books.Count > 0)
            {
                SelectedBook = Books[0];
            }
        }

        private void RefreshChapters()
        {
            Chapters.Clear();

            if (_selectedBook != null)
            {
                var query = _libraryFilter.Trim();
                foreach (var entry in _selectedBook.Entries)
                {
                    if (_showOnlyWithSubtitle && !entry.HasSubtitle)
                    {
                        continue;
                    }

                    if (query.Length > 0 &&
                        entry.SearchText.IndexOf(query.ToLowerInvariant(), StringComparison.Ordinal) < 0)
                    {
                        continue;
                    }

                    Chapters.Add(entry);
                }
            }

            OnPropertyChanged(nameof(HasChapters));
            OnPropertyChanged(nameof(ChapterListLabel));
            OnPropertyChanged(nameof(ChapterProgressText));
            OnPropertyChanged(nameof(VisibilityStats));
        }

        /// <summary>书库右上角的统计文字。</summary>
        public string VisibilityStats
        {
            get
            {
                var visible = 0;
                foreach (var _ in BooksView)
                {
                    visible++;
                }

                return visible == Books.Count ? $"共 {Books.Count} 本" : $"{visible} / {Books.Count} 本";
            }
        }

        private LibraryEntry? PickFirstChapter(LibraryBook book)
        {
            var query = _libraryFilter.Trim().ToLowerInvariant();
            foreach (var entry in book.Entries)
            {
                if (_showOnlyWithSubtitle && !entry.HasSubtitle)
                {
                    continue;
                }

                if (query.Length > 0 && entry.SearchText.IndexOf(query, StringComparison.Ordinal) < 0)
                {
                    continue;
                }

                return entry;
            }

            return null;
        }

        private bool FilterBook(object item)
        {
            if (item is not LibraryBook book)
            {
                return false;
            }

            if (_showOnlyFavorites && !_playback.IsFavorite(book.FolderPath))
            {
                return false;
            }

            var query = _libraryFilter.Trim().ToLowerInvariant();
            if (query.Length == 0)
            {
                return true;
            }

            if (book.Title.ToLowerInvariant().Contains(query, StringComparison.Ordinal) ||
                book.RelativeFolder.ToLowerInvariant().Contains(query, StringComparison.Ordinal))
            {
                return true;
            }

            foreach (var entry in book.Entries)
            {
                if (entry.SearchText.Contains(query, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        // ---------------- 条目与播放 ----------------

        /// <summary>
        /// 加载一章：外面套一层重入保护和耗时记录。
        /// 重入保护很关键——LoadEntry 会改 SelectedEntry，而 SelectedEntry 的 setter 又可能回头调 LoadEntry，
        /// 递归下去界面就"卡死"了（其实是在无限打转）。
        /// </summary>
        public void LoadEntry(LibraryEntry entry, bool autoPlay)
        {
            if (_loadingEntry)
            {
                Diagnostics.ActivityLog.Note($"⚠ 忽略重入的加载请求：{entry.Title}");
                return;
            }

            _loadingEntry = true;
            using var activity = Diagnostics.ActivityLog.Enter($"加载章节：{entry.Title}");

            try
            {
                LoadEntryCore(entry, autoPlay);
            }
            catch (Exception ex)
            {
                StatusText = $"加载这一章失败：{ex.Message}";
                Diagnostics.ActivityLog.Note($"✗ 加载章节失败：{ex}");
            }
            finally
            {
                _loadingEntry = false;
            }
        }

        private void LoadEntryCore(LibraryEntry entry, bool autoPlay)
        {
            if (entry == null)
            {
                return;
            }

            // 换章之前先把上一章听到哪儿存下来
            RememberCurrentPosition(force: true);

            _resumeOnOpen = null;
            if (RememberPlaybackPosition && _playback.TryGetResumePosition(entry.AudioPath, out var resumeAt))
            {
                _resumeOnOpen = resumeAt;
            }

            try
            {
                _audio.Open(entry.AudioPath);
                // 注意：这里**不能**设倍速。紧跟 Open() 之后管线还没起来，改速率会丢声音。
                // 倍速由 MediaPlayerAudioPlayer 在播放开始后延迟应用。
                AudioPath = entry.AudioPath;
                _loadedEntry = entry;
                StatusText = $"正在加载：{entry.Title} …";
            }
            catch (Exception ex)
            {
                StatusText = $"打开音频失败：{ex.Message}";
                return;
            }

            ResetPositionDisplay();
            LoadEmbeddedChapters(entry);

            if (entry.SubtitleTracks.Count > 0)
            {
                LoadSubtitleTracks(entry);

                var primary = _primaryTrack;
                var message = primary == null
                    ? string.Empty
                    : $"已加载字幕：{primary.Description}";
                if (_secondaryTrack != null)
                {
                    message += $"　+　双语：{_secondaryTrack.DisplayName}";
                }

                if (message.Length > 0)
                {
                    StatusText = message;
                }

                OnPropertyChanged(nameof(HasSubtitle));
                OnPropertyChanged(nameof(SubtitleCount));
            }
            else
            {
                _synchronizer.Clear();
                _secondarySynchronizer.Clear();
                AvailableTracks.Clear();
                SecondaryOptions.Clear();
                _primaryTrack = null;
                _secondaryTrack = null;
                _readingTrack = null;
                _readingSecondaryTrack = null;
                OnPropertyChanged(nameof(ReadingTrack));
                OnPropertyChanged(nameof(ReadingSecondaryTrack));
                OnPropertyChanged(nameof(ReadingSecondarySelection));
                OnPropertyChanged(nameof(PrimaryTrack));
                OnPropertyChanged(nameof(SecondaryTrack));
                OnPropertyChanged(nameof(HasSecondaryTrack));
                SubtitlePath = null;
                Overlay.Text = string.Empty;
                Overlay.SecondaryText = string.Empty;
                ReadingLines.Clear();
    
                _currentReadingLine = null;
                _selectedReadingLine = null;
                OnPropertyChanged(nameof(SelectedReadingLine));
                OnPropertyChanged(nameof(HasSubtitle));
                OnPropertyChanged(nameof(SubtitleCount));
                OnPropertyChanged(nameof(HasReadingLines));
                StatusText = $"{entry.Title}：没有找到同名 .srt，只播放音频。";
            }

            if (autoPlay)
            {
                Play();
            }
        }

        public void LoadSubtitle(string path)
        {
            try
            {
                var result = SrtParser.ParseFile(path);
                _synchronizer.Load(result.Lines);
                SubtitlePath = path;
                BuildReadingLines(result.Lines);

                var message = $"已加载字幕：{Path.GetFileName(path)}，共 {result.Lines.Count} 条（{result.EncodingName}）。";
                if (result.HasWarnings)
                {
                    message += " 警告：" + string.Join(" / ", Trim(result.Warnings));
                }

                StatusText = message;
                OnPropertyChanged(nameof(HasSubtitle));
                OnPropertyChanged(nameof(SubtitleCount));

                RefreshSubtitle(_audio.Position);
            }
            catch (Exception ex)
            {
                StatusText = $"加载字幕失败：{ex.Message}";
            }
        }

        private void BuildReadingLines(IReadOnlyList<SubtitleLine> lines, IReadOnlyList<SubtitleLine>? secondaryLines = null)
        {
            ReadingLines.Clear();

            _currentReadingLine = null;
            _selectedReadingLine = null;

            foreach (var line in lines)
            {
                if (line.Text.Length == 0)
                {
                    continue;
                }

                var readingLine = new ReadingLine(line);

                if (secondaryLines is { Count: > 0 })
                {
                    var match = FindClosestLine(secondaryLines, line.StartTime);
                    if (match != null)
                    {
                        readingLine.SecondaryText = match.Text;
                    }
                }

                ReadingLines.Add(readingLine);

            }

            OnPropertyChanged(nameof(HasReadingLines));
            OnPropertyChanged(nameof(SelectedReadingLine));
        }

        /// <summary>
        /// 在对照轨里找时间上最贴近的一句。
        /// 两份翻译的时间轴不一定一一对应（条数都可能不同），所以按开始时间就近匹配，而不是按序号。
        /// </summary>
        private static SubtitleLine? FindClosestLine(IReadOnlyList<SubtitleLine> candidates, TimeSpan startTime)
        {
            SubtitleLine? best = null;
            var bestDelta = TimeSpan.MaxValue;

            foreach (var candidate in candidates)
            {
                if (candidate.Text.Length == 0)
                {
                    continue;
                }

                // 时间区间有重叠就是最理想的匹配
                if (startTime >= candidate.StartTime && startTime < candidate.EndTime)
                {
                    return candidate;
                }

                var delta = candidate.StartTime - startTime;
                if (delta < TimeSpan.Zero)
                {
                    delta = -delta;
                }

                if (delta < bestDelta)
                {
                    bestDelta = delta;
                    best = candidate;
                }
            }

            return best;
        }

        /// <summary>命令行参数 / 拖到 exe 上的单文件（保留给调试用）。</summary>
        public void LoadFromCommandLine(IReadOnlyList<string> paths)
        {
            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path) || path.StartsWith('-') || !File.Exists(path))
                {
                    continue;
                }

                var extension = Path.GetExtension(path);
                if (extension.Equals(".srt", StringComparison.OrdinalIgnoreCase))
                {
                    LoadSubtitle(path);
                    continue;
                }

                try
                {
                    _audio.Open(path);
                    AudioPath = path;
                    ResetPositionDisplay();
                    StatusText = $"正在加载：{Path.GetFileName(path)} …";

                    var sibling = Path.ChangeExtension(path, ".srt");
                    if (File.Exists(sibling))
                    {
                        LoadSubtitle(sibling);
                    }
                }
                catch (Exception ex)
                {
                    StatusText = $"打开音频失败：{ex.Message}";
                }

                return;
            }
        }

        public void PlaySelected()
        {
            if (_selectedEntry != null)
            {
                LoadEntry(_selectedEntry, autoPlay: true);
                CurrentMode = AppMode.Player;
            }
            else if (_selectedBook != null)
            {
                var first = PickFirstChapter(_selectedBook);
                if (first != null)
                {
                    SelectedEntry = first;
                    PlaySelected();
                }
            }
        }

        public void Play()
        {
            if (!_audio.IsOpen)
            {
                if (_selectedEntry != null)
                {
                    LoadEntry(_selectedEntry, autoPlay: true);
                }
                else
                {
                    StatusText = "请先在书库里选择一本书。";
                }

                return;
            }

            if (_audio.Duration > TimeSpan.Zero && _audio.Position >= _audio.Duration - TimeSpan.FromMilliseconds(200))
            {
                SeekTo(TimeSpan.Zero);
            }

            _audio.Play();
            IsPlaying = true;
            Start();
        }

        /// <summary>
        /// 重新加载当前音频（在当前位置继续）。
        ///
        /// 专门用来救"没声音"：MediaPlayer 的音频渲染器有时候会静默失效
        /// （最典型的是**播放设备变了**——蓝牙耳机断开/重连、默认设备被切换，
        /// 或者独占模式被别的程序抢走），这时位置照常往前走，但一点声音都没有，
        /// 单纯 Pause/Play 是救不回来的，只有把媒体整个重新打开才行。
        /// </summary>
        public void ReloadAudio()
        {
            if (!_audio.IsOpen || string.IsNullOrEmpty(AudioPath))
            {
                return;
            }

            var entry = _loadedEntry ?? _selectedEntry;
            if (entry == null)
            {
                return;
            }

            var position = _audio.Position;
            var wasPlaying = IsPlaying || _audio.IsPlaying;

            Diagnostics.ActivityLog.Note($"重新加载音频：{position.TotalSeconds:0.#}s（播放中={wasPlaying}）");

            try
            {
                _audio.Open(entry.AudioPath);
                // 不在这里设倍速：紧跟 Open 之后设会丢声音（由播放器在播放开始后延迟应用）
            }
            catch (Exception ex)
            {
                StatusText = $"重新加载音频失败：{ex.Message}";
                return;
            }

            // Open 是异步的，等 MediaOpened 之后才能定位；这里记下待恢复的位置
            _reloadTo = position;
            _reloadPlay = wasPlaying;
        }

        private TimeSpan? _reloadTo;
        private bool _reloadPlay;

        /// <summary>重新加载完成后恢复位置与播放状态。</summary>
        private void ApplyPendingReload()
        {
            if (!_reloadTo.HasValue)
            {
                return;
            }

            var position = _reloadTo.Value;
            var play = _reloadPlay;
            _reloadTo = null;

            SeekTo(position);

            if (play)
            {
                _audio.Play();
                IsPlaying = true;
                Start();
            }

            StatusText = $"已重新加载音频，从 {Timecode.Format(position)} 继续。";
        }

        /// <summary>播放设备变了（插拔耳机 / 切换默认设备），音频渲染器可能已经失效。</summary>
        public void OnAudioDeviceChanged()
        {
            if (!_audio.IsOpen)
            {
                return;
            }

            Diagnostics.ActivityLog.Note("检测到播放设备变化，重新加载音频以恢复声音");
            ReloadAudio();
        }
        public void Pause()
        {
            if (!_audio.IsOpen)
            {
                return;
            }

            _audio.Pause();
            IsPlaying = false;
            RememberCurrentPosition(force: true);
        }

        public void TogglePlayPause()
        {
            if (IsPlaying)
            {
                Pause();
            }
            else
            {
                Play();
            }
        }

        public void Stop()
        {
            if (!_audio.IsOpen)
            {
                return;
            }

            _audio.Stop();
            IsPlaying = false;
            RememberCurrentPosition(force: true);
            SeekTo(TimeSpan.Zero);
        }

        public void Skip(double seconds)
        {
            if (!_audio.IsOpen)
            {
                return;
            }

            SeekTo(_audio.Position + TimeSpan.FromSeconds(seconds));
        }

        /// <summary>跳转并立刻重新同步字幕（按新时间重新二分定位，而不是从旧字幕往后扫）。</summary>
        public void SeekTo(TimeSpan position)
        {
            if (!_audio.IsOpen)
            {
                return;
            }

            if (position < TimeSpan.Zero)
            {
                position = TimeSpan.Zero;
            }

            var duration = _audio.Duration;
            if (duration > TimeSpan.Zero && position > duration)
            {
                position = duration;
            }

            _audio.Position = position;
            RefreshSubtitle(position);
        }

        public void BeginSeek()
        {
            IsSeeking = true;
        }

        public void EndSeek()
        {
            IsSeeking = false;
            if (_audio.IsOpen)
            {
                SeekTo(TimeSpan.FromSeconds(_positionSeconds));
            }
        }

        /// <summary>还有上一章可跳（工具条 / 托盘菜单用来决定按钮灰不灰）。</summary>
        public bool CanGoPrevious => HasSelectableNeighbour(-1);

        /// <summary>还有下一章可跳。</summary>
        public bool CanGoNext => HasSelectableNeighbour(1);

        /// <summary>跳到上一章（托盘菜单 / 字幕工具条用）。</summary>
        public void GoPreviousChapter() => PlayNextEntry(-1);

        /// <summary>跳到下一章。</summary>
        public void GoNextChapter() => PlayNextEntry(1);

        /// <summary>章节或选中项变了，通知上下章按钮刷新可用状态。</summary>
        private void NotifyChapterNavigation()
        {
            OnPropertyChanged(nameof(CanGoPrevious));
            OnPropertyChanged(nameof(CanGoNext));
        }
        /// <summary>上一章 / 下一章。</summary>
        public bool PlayNextEntry(int direction)
        {
            if (Chapters.Count == 0)
            {
                return false;
            }

            NotifyChapterNavigation();

            var index = _selectedEntry == null ? -1 : Chapters.IndexOf(_selectedEntry);
            var next = index + direction;

            if (next < 0 || next >= Chapters.Count)
            {
                return false;
            }

            _pendingAutoPlay = true;
            try
            {
                SelectedEntry = Chapters[next];
            }
            finally
            {
                _pendingAutoPlay = false;
            }

            return true;
        }

        private bool HasSelectableNeighbour(int direction)
        {
            if (Chapters.Count == 0)
            {
                return false;
            }

            NotifyChapterNavigation();

            var index = _selectedEntry == null ? -1 : Chapters.IndexOf(_selectedEntry);
            var next = index + direction;
            return next >= 0 && next < Chapters.Count;
        }

        // ---------------- 内部实现 ----------------

        // ---------------- 继续收听 ----------------

        public LibraryBook? ContinueBook { get; private set; }

        public bool HasContinue => ContinueEntry != null;

        public string ContinueBookTitle => ContinueBook?.Title ?? string.Empty;

        public string ContinueChapterTitle => ContinueEntry?.Title ?? string.Empty;

        /// <summary>"继续收听"指向的章节（播放数据的最近一次记录）。</summary>
        public LibraryEntry? ContinueEntry { get; private set; }

        public string ContinueDetail
        {
            get
            {
                var record = _playback.Get(ContinueEntry?.AudioPath);
                if (record == null)
                {
                    return string.Empty;
                }

                return record.Completed
                    ? $"已听完 · {Timecode.Format(record.Position)}"
                    : $"听到 {Timecode.Format(record.Position)} · {record.Progress * 100:0}%";
            }
        }

        private void UpdateContinueEntry()
        {
            LibraryEntry? entry = null;
            LibraryBook? book = null;

            var last = _playback.LastPlayedPath;
            if (!string.IsNullOrEmpty(last))
            {
                foreach (var candidate in Books)
                {
                    foreach (var chapter in candidate.Entries)
                    {
                        if (string.Equals(chapter.AudioPath, last, StringComparison.OrdinalIgnoreCase))
                        {
                            entry = chapter;
                            book = candidate;
                            break;
                        }
                    }

                    if (entry != null)
                    {
                        break;
                    }
                }
            }

            ContinueEntry = entry;
            ContinueBook = book;

            OnPropertyChanged(nameof(ContinueEntry));
            OnPropertyChanged(nameof(ContinueBook));
            OnPropertyChanged(nameof(HasContinue));
            OnPropertyChanged(nameof(ContinueBookTitle));
            OnPropertyChanged(nameof(ContinueChapterTitle));
            OnPropertyChanged(nameof(ContinueDetail));
        }

        private void ContinueListening()
        {
            if (ContinueEntry == null)
            {
                return;
            }

            SelectedBook = ContinueBook;
            _suppressModeSwitch = true;
            try
            {
                SelectedEntry = ContinueEntry;
            }
            finally
            {
                _suppressModeSwitch = false;
            }

            CurrentMode = AppMode.Player;
            Play();
        }

        // ---------------- 配色方案 ----------------

        private void SetThemeColor(string? value, Action<string> assign, string propertyName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var current = propertyName switch
            {
                nameof(ThemeBackground) => _theme.Background,
                nameof(ThemeForeground) => _theme.Foreground,
                nameof(ThemeAccent) => _theme.Accent,
                _ => _theme.Gold,
            };

            if (string.Equals(current, value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            assign(value.Trim());
            _theme.PresetName = "自定义";
            ApplyTheme();

            OnPropertyChanged(propertyName);
            OnPropertyChanged(nameof(SelectedThemePreset));
        }

        /// <summary>把当前配色写进应用资源（立刻生效）并保存。</summary>
        // ---------------- 配色方案：调色盘 ----------------

        private System.Collections.ObjectModel.ObservableCollection<ThemeColorSlot>? _themeSlots;

        /// <summary>四个颜色槽（背景 / 强调 / 金色 / 文字），每个都带调色盘。</summary>
        public System.Collections.ObjectModel.ObservableCollection<ThemeColorSlot> ThemeSlots => _themeSlots ??= BuildThemeSlots();

        /// <summary>通用调色盘（XAML 里给每个槽复用同一份）。</summary>

        private System.Collections.ObjectModel.ObservableCollection<ThemeColorSlot> BuildThemeSlots()
        {
            var slots = new System.Collections.ObjectModel.ObservableCollection<ThemeColorSlot>
            {
                new("background", "背景色", "窗口整体底色", ThemeBackground, hex => ThemeBackground = hex),
                new("accent", "强调色", "按钮 / 进度条 / 选中态", ThemeAccent, hex => ThemeAccent = hex),
                new("gold", "金色点缀", "当前章节 / 字幕角标", ThemeGold, hex => ThemeGold = hex),
                new("foreground", "文字色", "正文主色（浅色主题用深字）", ThemeForeground, hex => ThemeForeground = hex, isLightColor: true),
            };

            return slots;
        }

        /// <summary>切换预设后，把颜色同步回调色盘（不触发回写）。</summary>
        private void SyncThemeSlots()
        {
            if (_themeSlots == null)
            {
                return;
            }

            foreach (var slot in _themeSlots)
            {
                switch (slot.Key)
                {
                    case "background":
                        slot.SyncFrom(ThemeBackground);
                        break;
                    case "accent":
                        slot.SyncFrom(ThemeAccent);
                        break;
                    case "gold":
                        slot.SyncFrom(ThemeGold);
                        break;
                    case "foreground":
                        slot.SyncFrom(ThemeForeground);
                        break;
                }
            }
        }
        public void ApplyTheme()
        {
            Themes.ThemeService.Apply(_theme);
            SyncThemeSlots();

            _settings.ThemeBackground = _theme.Background;
            _settings.ThemeForeground = _theme.Foreground;
            _settings.ThemeAccent = _theme.Accent;
            _settings.ThemeGold = _theme.Gold;
            _settings.ThemePresetName = _theme.PresetName;
            PersistSettings();

            // 浅色方案下把悬浮字幕的默认字色换过来，不然白字贴在浅色背景上什么都看不见
            if (_theme.IsLightTheme && Overlay.ForegroundHex.Equals("#FFFFFF", StringComparison.OrdinalIgnoreCase))
            {
                Overlay.ForegroundHex = "#1E1B18";
                Overlay.OutlineHex = "#FFFFFF";
            }
            else if (!_theme.IsLightTheme && Overlay.ForegroundHex.Equals("#1E1B18", StringComparison.OrdinalIgnoreCase))
            {
                Overlay.ForegroundHex = "#FFFFFF";
                Overlay.OutlineHex = "#000000";
            }
        }

        private void ResetTheme()
        {
            SelectedThemePreset = ThemeSettings.Presets[0];
            StatusText = "已恢复默认配色。";
        }

        // ---------------- 全局热键 ----------------

        /// <summary>把全局热键挂到主窗口上（由 MainWindow 在创建句柄后调用）。</summary>
        public void AttachHotkeys(Window window)
        {
            _hotkeys?.Dispose();
            _hotkeys = new GlobalHotkeyService();
            _hotkeys.Pressed += OnHotkeyPressed;

            if (!_hotkeys.Attach(window))
            {
                StatusText = "全局热键注册失败：拿不到窗口句柄。";
                return;
            }

            RegisterHotkeys();
            OnPropertyChanged(nameof(HotkeyStatusText));
        }

        private void RegisterHotkeys()
        {
            if (_hotkeys == null)
            {
                return;
            }

            var definitions = DefaultHotkeys.Create(SkipSeconds);

            if (EnableGlobalHotkeys)
            {
                _hotkeys.Register(definitions);
                HotkeyList = _hotkeys.Registered;
            }
            else
            {
                _hotkeys.UnregisterAll();
                HotkeyList = definitions
                    .Select(d => new RegisteredHotkey(d, d.Candidates[0].Modifiers, d.Candidates[0].Key))
                    .ToList();
            }

            OnPropertyChanged(nameof(HotkeyList));
            OnPropertyChanged(nameof(HotkeyStatusText));
        }

        private void OnHotkeyPressed(object? sender, string action)
        {
            switch (action)
            {
                case DefaultHotkeys.TogglePlay:
                    TogglePlayPause();
                    break;

                case DefaultHotkeys.SkipBack:
                    Skip(-SkipSeconds);
                    break;

                case DefaultHotkeys.SkipForward:
                    Skip(SkipSeconds);
                    break;

                case DefaultHotkeys.ToggleOverlay:
                    Overlay.IsVisible = !Overlay.IsVisible;
                    StatusText = Overlay.IsVisible ? "已显示桌面字幕。" : "已隐藏桌面字幕。";
                    break;

                case DefaultHotkeys.ToggleMoveMode:
                    Overlay.IsMovable = !Overlay.IsMovable;
                    StatusText = Overlay.IsMovable
                        ? "字幕已解锁：拖动摆位、滚轮调字号，按 Ctrl+Alt+M 或点「完成」锁定。"
                        : "字幕已锁定：鼠标重新穿透。";
                    break;

                case DefaultHotkeys.ToggleReading:
                    CurrentMode = _currentMode == AppMode.Reading ? AppMode.Player : AppMode.Reading;
                    break;
            }
        }

        // ---------------- 封面 ----------------

        /// <summary>封面缓存目录（内嵌封面解出来会存在这里）。</summary>
        public static string CoversDirectory => AppPaths.CoversDirectory;

        /// <summary>手动给一本书挑封面。</summary>
        private void ChooseCover(LibraryBook? book)
        {
            if (book == null)
            {
                StatusText = "先选一本书再换封面。";
                return;
            }

            try
            {
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = $"给《{book.Title}》选择封面图片",
                    Filter = "图片|*.jpg;*.jpeg;*.png;*.bmp;*.gif|所有文件|*.*",
                    CheckFileExists = true,
                };

                if (dialog.ShowDialog() != true)
                {
                    return;
                }

                // 复制进缓存目录，这样原图挪走了封面也还在
                var data = File.ReadAllBytes(dialog.FileName);
                var mime = Path.GetExtension(dialog.FileName).ToLowerInvariant() switch
                {
                    ".png" => "image/png",
                    ".gif" => "image/gif",
                    ".bmp" => "image/bmp",
                    _ => "image/jpeg",
                };

                var key = "custom_" + Math.Abs(book.FolderPath.ToLowerInvariant().GetHashCode()).ToString("X8");
                var stored = MediaTagReader.SaveCoverToFile(data, mime, CoversDirectory, key);
                if (stored == null)
                {
                    stored = dialog.FileName; // 缓存写不进去就直接引用原图
                }

                _playback.SetCustomCover(book.FolderPath, stored);
                _playback.Save();

                book.ApplyCover(stored);
                StatusText = $"已更新《{book.Title}》的封面。";
            }
            catch (Exception ex)
            {
                StatusText = "设置封面失败：" + ex.Message;
            }
        }

        /// <summary>清除手动指定的封面，回到自动解析（文件夹图片 / 内嵌封面）。</summary>
        private void ClearCover(LibraryBook? book)
        {
            if (book == null)
            {
                return;
            }

            _playback.SetCustomCover(book.FolderPath, null);
            _playback.Save();

            var fresh = MediaLibraryScanner.Scan(book.FolderPath, null, CoversDirectory);
            var auto = fresh.Books.Count > 0 ? fresh.Books[0].CoverPath : null;
            book.ApplyCover(auto);

            OnPropertyChanged(nameof(StorageSummary));
            StatusText = auto == null
                ? $"已清除《{book.Title}》的自定义封面（没有找到自动封面）。"
                : $"已恢复《{book.Title}》的自动封面。";
        }

        // ---------------- 文件内章节 ----------------

        private void LoadEmbeddedChapters(LibraryEntry entry)
        {
            EmbeddedChapters.Clear();
            CurrentEmbeddedChapter = null;

            var tags = MediaTagReader.TryReadCached(entry.AudioPath);
            if (tags != null)
            {
                foreach (var chapter in tags.Chapters)
                {
                    EmbeddedChapters.Add(chapter);
                }
            }

            OnPropertyChanged(nameof(HasEmbeddedChapters));
            UpdateCurrentEmbeddedChapter(TimeSpan.Zero);
        }

        private void UpdateCurrentEmbeddedChapter(TimeSpan position)
        {
            if (EmbeddedChapters.Count == 0)
            {
                return;
            }

            AudioChapter? found = null;
            foreach (var chapter in EmbeddedChapters)
            {
                if (chapter.Start <= position)
                {
                    found = chapter;
                }
                else
                {
                    break;
                }
            }

            if (!ReferenceEquals(found, CurrentEmbeddedChapter))
            {
                CurrentEmbeddedChapter = found;
            }
        }

        // ---------------- 设置 / 数据 ----------------

        /// <summary>把当前设置写盘（界面上的按钮改完设置后调用）。</summary>
        public void PersistOverlaySettings() => PersistSettings();
        private void PersistSettings()
        {
            // 启动时正在"应用设置"，这时内存里的状态还不完整（比如媒体库还没扫描，
            // LibraryRoot 是 null）。此时回写会把刚读出来的媒体库目录抹成 null ——
            // 表现就是"每次打开都要重新选文件夹"。
            if (_initializing)
            {
                return;
            }

            var saved = CaptureSettings().Save();
            _settingsSaveError = saved ? null : _settings.LastError;
            OnPropertyChanged(nameof(StorageSummary));
            OnPropertyChanged(nameof(SettingsSaveErrorText));
            OnPropertyChanged(nameof(HasSettingsSaveError));
        }

        private string? _settingsSaveError;
        private bool _initializing = true;

        /// <summary>设置保存失败的原因（保存正常时为 null）。</summary>
        public string? SettingsSaveErrorText => _settingsSaveError;

        public bool HasSettingsSaveError => !string.IsNullOrEmpty(_settingsSaveError);

        public string DataDirectory => AppPaths.DataDirectory;

        private void OpenDataFolder()
        {
            try
            {
                var directory = Path.GetDirectoryName(SettingsFilePath) ?? AppContext.BaseDirectory;
                Directory.CreateDirectory(directory);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = directory,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                StatusText = "打开数据目录失败：" + ex.Message;
            }
        }

        /// <summary>把播放数据重新灌进媒体库（清空数据、外部改动之后调用）。</summary>
        public void RefreshPlaybackData()
        {
            foreach (var book in Books)
            {
                book.ApplyPlaybackStore(_playback);
                book.RefreshProgress();
            }

            UpdateContinueEntry();
            OnPropertyChanged(nameof(StorageSummary));
            OnPropertyChanged(nameof(FavoriteCount));
        }

        private void ClearPlaybackData()
        {
            _playback.ClearAll();
            _playback.Save(force: true);
            RefreshPlaybackData();
            StatusText = "已清空播放记录（听到哪儿、最近播放、收藏）。";
        }

        private void ResetAllSettings()
        {
            var defaults = new AppSettings { FilePath = SettingsFilePath };
            defaults.Save();

            AutoContinue = defaults.AutoContinue;
            ShowOnlyWithSubtitle = defaults.ShowOnlyWithSubtitle;
            ShowOnlyFavorites = defaults.ShowOnlyFavorites;
            RememberPlaybackPosition = defaults.RememberPlaybackPosition;
            ResumeLastOnStartup = defaults.ResumeLastOnStartup;
            Volume = defaults.Volume;
            SkipSeconds = defaults.SkipSeconds;

            Overlay.ResetAppearance();
            Overlay.ResetPlacement();
            Overlay.IsVisible = defaults.OverlayVisible;

            PersistSettings();
            StatusText = HasSettingsSaveError ? $"已恢复默认设置，但写盘失败：{SettingsSaveErrorText}" : "已恢复默认设置。";
        }

        private void ToggleFavorite(string? folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return;
            }

            var isFavorite = _playback.ToggleFavorite(folderPath);
            _playback.Save();

            if (ShowOnlyFavorites)
            {
                BooksView.Refresh();
                RefreshChapters();
            }

            OnPropertyChanged(nameof(StorageSummary));
            OnPropertyChanged(nameof(FavoriteCount));

            var name = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar));
            StatusText = isFavorite ? $"已收藏：{name}" : $"已取消收藏：{name}";
        }

        /// <summary>这本书是否被收藏（卡片上的星标用）。</summary>
        public bool IsFavorite(string? folderPath) => _playback.IsFavorite(folderPath);

        /// <summary>把当前位置写进播放记录（播放中节流、暂停 / 切章 / 退出时强制写）。</summary>
        private void RememberCurrentPosition(bool force)
        {
            // 注意：这里必须用 _loadedEntry（真正装载在播放器里的那一条），不能用 _selectedEntry。
            // 换书/换章时 _selectedEntry 已经先指向新的一条了，而 _audio.Position 还是旧的，
            // 用 _selectedEntry 就会把"上一本书的时间"写进新书的记录里 ——
            // 表现就是"换本书结果从上一本的时间接着播"，而且进度数据被写脏。
            var target = _loadedEntry ?? _selectedEntry;

            if (!RememberPlaybackPosition || target == null || !_audio.IsOpen)
            {
                return;
            }

            var now = DateTime.UtcNow;
            if (!force && (now - _lastRememberUtc).TotalSeconds < 5)
            {
                return;
            }

            _lastRememberUtc = now;

            var duration = _audio.Duration;
            _playback.Update(target.AudioPath, _audio.Position, duration);

            var record = _playback.Get(target.AudioPath);
            target.ApplyPlaybackState(
                record?.PositionSeconds ?? 0,
                record?.DurationSeconds ?? 0,
                record?.Completed ?? false);

            target.Book?.RefreshProgress();
            _selectedBook?.RefreshProgress();
            UpdateContinueEntry();

            if (force)
            {
                _playback.Save();
            }
            else
            {
                _playback.SaveThrottled(TimeSpan.FromSeconds(15));
            }
        }

        private void ApplySettings()
        {
            AutoContinue = _settings.AutoContinue;
            ShowOnlyWithSubtitle = _settings.ShowOnlyWithSubtitle;
            ShowOnlyFavorites = _settings.ShowOnlyFavorites;
            RememberPlaybackPosition = _settings.RememberPlaybackPosition;
            ResumeLastOnStartup = _settings.ResumeLastOnStartup;
            Volume = _settings.Volume;
            SkipSeconds = _settings.SkipSeconds;
            Overlay.IsVisible = _settings.OverlayVisible;
            Overlay.ShowHandle = _settings.OverlayShowHandle;
            Overlay.AutoHideToolbar = _settings.OverlayAutoHideToolbar;
            MinimizeToTray = _settings.MinimizeToTray;
            _libraryZoom = Math.Clamp(_settings.LibraryZoom, MinZoom, MaxZoom);
            _playerZoom = Math.Clamp(_settings.PlayerZoom, MinZoom, MaxZoom);
            _readingZoom = Math.Clamp(_settings.ReadingZoom, MinZoom, MaxZoom);
            Overlay.IsMovable = _settings.OverlayMovable;
            _enableGlobalHotkeys = _settings.EnableGlobalHotkeys;

            if (_settings.OverlayCustomX.HasValue && _settings.OverlayCustomY.HasValue)
            {
                Overlay.CustomX = _settings.OverlayCustomX.Value;
                Overlay.CustomY = _settings.OverlayCustomY.Value;
            }

            Themes.ThemeService.Apply(_theme);

            Overlay.FontFamilyName = _settings.FontFamilyName;
            Overlay.FontSize = _settings.FontSize;
            Overlay.FontWeightName = _settings.FontWeightName;
            Overlay.ExtraFontsFolder = _settings.ExtraFontsFolder;
            _uiFontName = _settings.UiFontFamilyName ?? string.Empty;
            ApplyUiFont();
            Overlay.ForegroundHex = _settings.ForegroundHex;
            Overlay.OutlineHex = _settings.OutlineHex;
            Overlay.OutlineThickness = _settings.OutlineThickness;
            Overlay.OffsetX = _settings.OffsetX;
            Overlay.OffsetY = _settings.OffsetY;
            Overlay.OverlayWidth = _settings.OverlayWidth;
            Overlay.OverlayHeight = _settings.OverlayHeight;

            if (Enum.TryParse<OverlayAnchor>(_settings.Anchor, ignoreCase: true, out var anchor))
            {
                Overlay.Anchor = anchor;
            }

            // 设置已经应用完，之后的所有改动都可以安全回写
            _initializing = false;
        }

        private AppSettings CaptureSettings()
        {
            // 只有当前确实有媒体库时才覆盖，避免把存好的目录写成 null
            if (!string.IsNullOrEmpty(LibraryRoot))
            {
                _settings.LibraryFolder = LibraryRoot;
            }
            _settings.AutoContinue = AutoContinue;
            _settings.ShowOnlyWithSubtitle = ShowOnlyWithSubtitle;
            _settings.ShowOnlyFavorites = ShowOnlyFavorites;
            _settings.RememberPlaybackPosition = RememberPlaybackPosition;
            _settings.ResumeLastOnStartup = ResumeLastOnStartup;
            _settings.Volume = Volume;
            _settings.SkipSeconds = SkipSeconds;
            _settings.OverlayVisible = Overlay.IsVisible;
            _settings.OverlayShowHandle = Overlay.ShowHandle;
            _settings.OverlayAutoHideToolbar = Overlay.AutoHideToolbar;
            _settings.MinimizeToTray = MinimizeToTray;
            _settings.LibraryZoom = _libraryZoom;
            _settings.PlayerZoom = _playerZoom;
            _settings.ReadingZoom = _readingZoom;
            _settings.OverlayMovable = Overlay.IsMovable;
            _settings.EnableGlobalHotkeys = EnableGlobalHotkeys;
            _settings.OverlayCustomX = Overlay.CustomX;
            _settings.OverlayCustomY = Overlay.CustomY;

            _settings.FontFamilyName = Overlay.FontFamilyName;
            _settings.FontSize = Overlay.FontSize;
            _settings.FontWeightName = Overlay.FontWeightName;
            _settings.ExtraFontsFolder = Overlay.ExtraFontsFolder;
            _settings.ForegroundHex = Overlay.ForegroundHex;
            _settings.OutlineHex = Overlay.OutlineHex;
            _settings.OutlineThickness = Overlay.OutlineThickness;
            _settings.Anchor = Overlay.Anchor.ToString();
            _settings.OffsetX = Overlay.OffsetX;
            _settings.OffsetY = Overlay.OffsetY;
            _settings.OverlayWidth = Overlay.OverlayWidth;
            _settings.OverlayHeight = Overlay.OverlayHeight;

            return _settings;
        }

        private void OnOverlayPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {

            if (e.PropertyName == nameof(SubtitleOverlayViewModel.CustomY))
            {
                // 拖动结束才会以 persist=true 触发这个通知，这时才写盘
                PersistSettings();
                return;
            }

            if (e.PropertyName == nameof(SubtitleOverlayViewModel.ShowHandle) && _overlayWindow != null)
            {
                PersistSettings();
                return;
            }

            if (e.PropertyName == nameof(SubtitleOverlayViewModel.AutoHideToolbar))
            {
                PersistSettings();
                return;
            }

            if (e.PropertyName == nameof(SubtitleOverlayViewModel.IsMovable))
            {
                // 锁定 / 解锁状态要记住，下次启动保持原样
                PersistSettings();
                return;
            }

            if (e.PropertyName != nameof(SubtitleOverlayViewModel.IsVisible) || _overlayWindow == null)
            {
                return;
            }

            if (Overlay.IsVisible)
            {
                _overlayWindow.ShowOverlay();
            }
            else
            {
                _overlayWindow.HideOverlay();
            }
        }

        private void OnTick(object? sender, EventArgs e)
        {
            if (!_audio.IsOpen)
            {
                return;
            }

            var position = _audio.Position;

            if (_synchronizer.Update(position))
            {
                var text = _synchronizer.Current?.Text ?? string.Empty;
                if (Overlay.Text != text)
                {
                    Overlay.Text = text;
                }

                SyncReadingLine(position);
                OnPropertyChanged(nameof(CurrentSubtitle));
            }

            UpdateSecondarySubtitle(position, seek: false);

            RememberCurrentPosition(force: false);

            if (_isSeeking)
            {
                return; // 用户正在拖动进度条
            }

            _updatingFromTick = true;
            try
            {
                PositionSeconds = position.TotalSeconds;
            }
            finally
            {
                _updatingFromTick = false;
            }
        }

        private void RefreshSubtitle(TimeSpan position)
        {
            _synchronizer.Seek(position);
            Overlay.Text = _synchronizer.Current?.Text ?? string.Empty;
            SyncReadingLine(position);
            UpdateCurrentEmbeddedChapter(position);
            UpdateSecondarySubtitle(position, seek: true);
            OnPropertyChanged(nameof(CurrentSubtitle));
        }

        /// <summary>更新副字幕（双语模式的第二行）。</summary>
        private void UpdateSecondarySubtitle(TimeSpan position, bool seek)
        {
            if (_secondaryTrack == null)
            {
                if (Overlay.SecondaryText.Length > 0)
                {
                    Overlay.SecondaryText = string.Empty;
                }

                return;
            }

            if (seek)
            {
                _secondarySynchronizer.Seek(position);
            }
            else
            {
                _secondarySynchronizer.Update(position);
            }

            var text = _secondarySynchronizer.Current?.Text ?? string.Empty;
            if (Overlay.SecondaryText != text)
            {
                Overlay.SecondaryText = text;
            }
        }

        /// <summary>把"当前朗读行"同步到阅读列表，并让列表选中它（界面据此滚动到可见位置）。</summary>
        /// <summary>
        /// 按播放位置高亮阅读列表里对应的那一句。
        ///
        /// 这里必须按**时间**找，不能按字幕对象找：阅读列表是用阅读轨自己的解析结果建的，
        /// 和悬浮字幕同步器里的那些 SubtitleLine 不是同一批实例，
        /// 用对象当字典键永远匹配不上（表现就是"播放时不再自动跳到对应的话"）。
        /// </summary>
        private void SyncReadingLine(TimeSpan position)
        {
            var target = FindReadingLineAt(position);

            if (!ReferenceEquals(target, _currentReadingLine))
            {
                if (_currentReadingLine != null)
                {
                    _currentReadingLine.IsCurrent = false;
                }

                _currentReadingLine = target;

                if (target != null)
                {
                    target.IsCurrent = true;
                }
            }

            if (target != null && !ReferenceEquals(target, _selectedReadingLine))
            {
                _updatingReadingSelection = true;
                try
                {
                    SelectedReadingLine = target;
                }
                finally
                {
                    _updatingReadingSelection = false;
                }
            }
        }

        /// <summary>二分查找当前播放位置落在哪一条阅读行上（不在任何区间内就返回 null）。</summary>
        private ReadingLine? FindReadingLineAt(TimeSpan position)
        {
            var lines = ReadingLines;
            if (lines.Count == 0)
            {
                return null;
            }

            var low = 0;
            var high = lines.Count - 1;
            var candidate = -1;

            while (low <= high)
            {
                var mid = low + ((high - low) / 2);
                if (lines[mid].Line.StartTime <= position)
                {
                    candidate = mid;
                    low = mid + 1;
                }
                else
                {
                    high = mid - 1;
                }
            }

            if (candidate < 0)
            {
                return null;
            }

            var line = lines[candidate];

            // 超过这句的结束时间就是空档期，不高亮（和悬浮字幕的行为保持一致）
            return position < line.Line.EndTime ? line : null;
        }
        private void ResetPositionDisplay()
        {
            _positionSeconds = 0;
            _durationSeconds = 0;
            OnPropertyChanged(nameof(PositionSeconds));
            OnPropertyChanged(nameof(PositionText));
            OnPropertyChanged(nameof(DurationSeconds));
            OnPropertyChanged(nameof(DurationText));
            OnPropertyChanged(nameof(HasDuration));
        }

        private void OnAudioOpened(object? sender, EventArgs e)
        {
            // 重新加载（换设备后救声音）时，打开完成要回到原来的位置
            ApplyPendingReload();

            DurationSeconds = _audio.Duration.TotalSeconds;
            IsPlaying = _audio.IsPlaying;

            var note = (_audio as MediaPlayerAudioPlayer)?.LastCompatibilityNote;

            if (_resumeOnOpen.HasValue)
            {
                var resumeAt = _resumeOnOpen.Value;
                _resumeOnOpen = null;

                _audio.Position = resumeAt;
                RefreshSubtitle(resumeAt);

                _updatingFromTick = true;
                try
                {
                    PositionSeconds = resumeAt.TotalSeconds;
                }
                finally
                {
                    _updatingFromTick = false;
                }

                StatusText = $"从 {Timecode.Format(resumeAt)} 继续播放：{CurrentBookTitle} · {CurrentChapterTitle}";
                return;
            }

            StatusText = $"正在播放：{CurrentBookTitle} · {CurrentChapterTitle}，时长 {DurationText}。" + (note ?? string.Empty);
            RefreshSubtitle(TimeSpan.Zero);
        }

        private void OnAudioEnded(object? sender, EventArgs e)
        {
            // WPF 的 MediaPlayer 偶尔会"假结束"（比如在被 Close / 重新 seek / 重新起播的时候），
            // 一假结束就 PlayNextEntry 的话，表现就是"这一章没听完自己跳到下一章"。
            // 所以这里先确认播放位置真的到结尾了，才认为是真结束。
            var duration = _audio.Duration;
            var position = _audio.Position;

            if (duration > TimeSpan.Zero && position < duration - TimeSpan.FromSeconds(2))
            {
                Diagnostics.ActivityLog.Note(
                    $"忽略可疑的结束事件：位置 {position.TotalSeconds:0.#}s / 时长 {duration.TotalSeconds:0.#}s");

                // 还没到结尾就当没结束，继续放着
                if (IsPlaying)
                {
                    _audio.Play();
                }

                return;
            }

            IsPlaying = false;

            if (AutoContinue)
            {
                _suppressModeSwitch = true; // 连播时不要打断阅读模式
                try
                {
                    if (PlayNextEntry(1))
                    {
                        return;
                    }
                }
                finally
                {
                    _suppressModeSwitch = false;
                }
            }

            StatusText = "播放结束。";
        }

        private void OnAudioFailed(object? sender, AudioPlayerErrorEventArgs e)
        {
            IsPlaying = false;
            StatusText = e.Message;
        }

        private static IEnumerable<string> Trim(IReadOnlyList<string> warnings)
        {
            const int max = 3;
            for (var i = 0; i < warnings.Count && i < max; i++)
            {
                yield return warnings[i];
            }

            if (warnings.Count > max)
            {
                yield return $"还有 {warnings.Count - max} 条警告";
            }
        }

        private sealed class BookComparer : IComparer
        {
            public int Compare(object? x, object? y)
            {
                var left = (x as LibraryBook)?.RelativeFolder;
                var right = (y as LibraryBook)?.RelativeFolder;
                return NaturalStringComparer.Instance.Compare(left, right);
            }
        }
    }
}
