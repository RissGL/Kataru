# KATARU · HarmonyOS

桌面版 KATARU 有声书播放器的鸿蒙重写版。技术栈：**ArkTS / ArkUI（Stage 模型）**，不依赖任何第三方库。
算法和数据结构沿用桌面版（桌面版有 269 项自检覆盖）。

## 构建

```powershell
.\build.ps1
# 产出：entry\build\default\outputs\default\entry-default-signed.hap
```

构建脚本用 DevEco 自带的 JBR / Node / ohpm / hvigor，并设置 `DEVECO_SDK_HOME`。

## 安装到真机

```powershell
hdc install -r entry\build\default\outputs\default\entry-default-signed.hap
```

实测设备：MIA-AL00 · HarmonyOS 6.1.0 · API 24 · `compatibleSdkVersion` 用 `6.0.0(20)`。

## 书库：一次授权，永久生效

鸿蒙手机**不给第三方 App 持久访问文件夹**，全部实测过：

| 尝试 | 结果 |
| --- | --- |
| 选文件夹 | ✅ 系统 picker 支持（日志里是 `PathSelectTree`） |
| 把授权持久化 | ❌ `persistPermission` → `URI forbid to be persisted!` |
| App 重启后重读原路径 | ❌ `13900001` |
| 按路径直读公共 Download | ❌ `13900001`；`READ_WRITE_DOWNLOAD_DIRECTORY` 申请返回 `2`（系统不授予） |
| hdc / shell 访问 App 沙箱 | ❌ `Permission denied` |

所以采用**同步**方案：

```
选择书库文件夹（一次）
   ↓
复制进 App 私有目录 filesDir/books/<书名>/
   ↓
之后永久可用（不依赖任何授权，重启/重装都不丢）
```

- **同步规则**：源里有什么就有什么。新书复制、改动过（大小不同）覆盖、**源里删掉的书在本地也删除**，不留指向已消失文件的"幽灵书"。
- **移动 / 删除 / 改名**：点一次「选择书库文件夹」重新同步即可，书架按磁盘实际内容整表重建（`refreshLibrary`）。
- **空间**：会多占一份。删掉源文件不影响 App 使用。

## 功能对照（桌面版 → 鸿蒙版）

| 功能 | 状态 |
| --- | --- |
| 媒体库（一本书一个文件夹，自动往下找一层） | ✅ |
| 封面（同名图 / cover.png；0.83 比例裁切，不拉伸） | ✅ |
| 换封面 / 恢复自动封面 | ✅ |
| 播放 / 暂停 / 拖动进度 / 快退快进（秒数可配 5–30s） | ✅ |
| 音量 | ✅ |
| 章节切换 | ✅ |
| m4b 内嵌章节表（chpl）解析 + 跳章 | ✅ 实测 106 章 |
| m4b 直接播放（`AVDataSrcDescriptor`，不需要改名或复制） | ✅ |
| 阅读页逐句高亮、自动滚动、点击跳转 | ✅ |
| 双语对照（主字幕 + 译文，按开始时间最近匹配） | ✅ |
| 字号 / 字体 / 颜色 / 字重 5 档 / 描边粗细 / 描边颜色 / 译文大小 | ✅ |
| 内置思源宋体（Regular + Bold，约 47 MB） | ✅ |
| 界面缩放 80%–140%（对应桌面版三处 zoom） | ✅ |
| 5 套预设配色 | ✅ |
| **自定义配色**：背景/主色/金色/文字/字幕色/描边色 6 个色位，H-S-L 滑杆任意调 | ✅ |
| 自定义配色持久化 | ✅ |
| 收藏 / 书名搜索 / 只看收藏 / 只看有字幕 | ✅ |
| 进度记忆 / 启动恢复上次位置 / 播完自动下一章 | ✅ |
| 书架卡片进度条 + 顶栏总体进度 + 继续收听卡片 | ✅ |
| 系统播控（锁屏与播控中心显示当前字幕） | ✅ |
| 通知栏常驻字幕（设置里开关，需通知权限） | ✅ |
| 后台播放长时任务（`backgroundModes: audioPlayback`） | ✅ 随通知开关启用 |
| 实况窗 / 灵动胶囊字幕（`LiveViewKit`） | ⚠️ 代码完成、参数校验通过；需华为侧开通实况窗权限（`1003500005`） |
| 清除播放记录 / 恢复默认设置 | ✅ |
| 播放倍速 | ⛔ 桌面版已移除，保持一致 |
| 全局热键 | ⛔ 桌面专有 |

### 悬浮字幕的边界（重要）

| 方案 | 可行性 |
| --- | --- |
| 悬浮窗覆盖其他 App | ❌ `SYSTEM_FLOAT_WINDOW` 是系统权限，普通应用拿不到 |
| 画中画显示文字 | ❌ PiP 只接受 `XComponent` 视频，没有文字模板 |
| **实况窗 / 灵动胶囊** | ⚠️ 代码已接；需在 AppGallery Connect 申请实况窗权限（本机返回 `1003500005 The right of liveView is not enabled`） |
| **AVSession 播控中心** | ✅ 锁屏/播控卡片显示"书名 + 当前这句字幕"，无需审批 |
| **通知栏常驻字幕** | ✅ 设置里打开开关即可（需要通知权限），下拉通知栏能看到当前这句 |

## 目录

```
entry/src/main/ets/
├── entryability/EntryAbility.ets     入口（含分享接收）
├── model/
│   ├── Library.ets                   书库扫描（自然排序、字幕配对、封面查找）
│   ├── SrtParser.ets                 SRT 解析
│   ├── SubtitleSynchronizer.ets      时间轴同步
│   ├── SubtitleLanguage.ets          语言识别（.zh.srt 之类）
│   ├── Timecode.ets                  时间格式化
│   ├── Mp4Chapters.ets               MP4/M4B chpl 章节表解析
│   ├── Theme.ets                     5 套配色 + 持久化
│   ├── MediaSession.ets              AVSession（后台播放 / 锁屏字幕）
│   └── LiveSubtitle.ets              实况窗 / 灵动胶囊字幕
└── pages/Index.ets                   四个模式页（书库 / 播放 / 阅读 / 设置）
```

## 已知平台坑（都踩过并绕过）

1. `fileIo.copyFile(fd, path)` **会成功但写出 0 字节文件** → 一律用 4 MB 分块读写 + 字节数校验。
2. `createPixelMapSync` 在真机上返回 `undefined` → 用异步 `createPixelMap`。
3. `Image('路径字符串')` 对沙箱外路径静默失败 → 先解码成 `PixelMap`。
4. `listFileSync` 不认 `file://docs/...` URI → 用 `fileUri.FileUri(uri).path` 转真实路径。
5. `tabIndex`、`outlineColor` 等 ArkTS 内置属性名不能当成员名。
6. ArkTS 禁用 `for..in`、`Function.apply`、无类型对象字面量。
7. **Tabs 里所有横向 Slider 都拖不动** —— Tabs 默认用 Swiper 承载页面，左右滑动手势会抢走滑杆的拖动。
   解法：`Tabs(...).scrollable(false)`，只保留点底部标签切页。
8. `startBackgroundRunning` 报 `9800005 bgMode is invalid`：要在 **`module.json5` 的 abilities 里声明**
   `"backgroundModes": ["audioPlayback"]`，并且用 `ContinuousTaskRequest` + `SUBMODE_NORMAL_NOTIFICATION`（2），
   老的枚举/字符串重载在这台机器上都不被接受。
9. 系统权限弹窗（`requestEnableNotification`）**不响应 `uinput` 注入**，卡住时只能真人点，
   或用 `bm uninstall -n <bundle> -k`（保留数据）重装清掉。
10. 实况窗 `startLiveView` 的必填项是逐个报出来的：`primary.clickAction`(WantAgent) →
    `primary.layoutData`(ProgressLayout) → `layoutData.color` 必须 `#AARRGGBB` →
    `layoutData.nodeIcons`(≤30KB，用 PixelMap) → `capsule.icon`。全部补齐后才会走到权限校验。

## 调试

```powershell
# 应用日志
hdc shell "hilog -x | grep 'A00000/com.rissgl.kataru'"

# 清日志后重启
hdc shell "hilog -r"; hdc shell aa force-stop com.rissgl.kataru; hdc shell aa start -a EntryAbility -b com.rissgl.kataru

# 截图
hdc shell "snapshot_display -f /data/local/tmp/s.jpeg"; hdc file recv /data/local/tmp/s.jpeg .
```

关键日志行：

```
refresh: N book(s) at /data/storage/el2/base/haps/entry/files/books
open audio: ... (710181178 bytes)
setMediaSource(dataSrc) for ...
state: prepared / prepared: duration=43864295
subtitle parse: 1153487 chars -> N lines in M ms
cover info: 1000x1000 -> 640x640 / covers: 1/1 loaded
liveView enabled: yes
avsession activated
```

## 局域网拉书（电脑当服务器，手机无线下载）

不上公网、不经过任何云，电脑开一个 HTTP 服务，手机连同一个 Wi-Fi（或把电脑连到手机热点）直接拉整本书。

### 电脑端（WPF）

设置 → **局域网传输（手机拉书）** → 「启动共享」，界面会显示形如 `http://10.39.152.64:8765` 的地址。

也可以无头运行（不开窗口，只当服务器）：

```
KATARU.exe --serve
KATARU.exe --serve "D:\有声书"     # 指定书库目录
```

服务是裸 `TcpListener` 实现（不引入任何第三方库，也不需要管理员权限建 URL ACL），只实现手机端用到的三个接口：

| 接口 | 作用 |
| --- | --- |
| `GET /api/library` | 返回书库 JSON（书名 / 章数 / 每章大小） |
| `GET /api/file?book=0&chapter=0&kind=audio` | 下发音频（支持 `Range`，可断点续传） |
| `GET /api/cover?book=0` | 下发封面图 |
| `GET /` | 浏览器里能打开的简易书单页 |

### 手机端（鸿蒙）

设置 → **其他** → 局域网拉书：填电脑上显示的地址 → 「连接」→ 列表出现电脑上的书 → 点「下载」，整本（音频 + 字幕）拉进沙箱并自动刷新书架。

- 需要 `ohos.permission.INTERNET`
- 下载走系统 `@ohos.request.downloadFile`，710 MB 的文件也不会占 JS 内存
- 拉完直接进 `filesDir/books/<书名>/`，和「选择书库文件夹」同步进来的书完全等价

### 连不上怎么办

1. 手机和电脑必须在同一网段（电脑连手机热点时，地址通常是 `192.168.43.x`）
2. Windows 防火墙要允许本程序的**专用网络**入站（首次启动共享时系统会弹窗）
3. 手机浏览器打开 `http://<电脑IP>:8765/` 能出书单，就说明网络通了，剩下是 App 侧的事