// ==UserScript==
// @name         Audible → Bangumi 批量导入助手
// @namespace    https://bgm.tv/
// @version      3.5.2
// @description  Audible 批量导入 Bangumi，支持多任务并行、独立封面组件与自动人物关联（需 bangumi-auto-relate-infobox）
// @match        https://www.audible.co.jp/*
// @match        https://audible.co.jp/*
// @match        https://bgm.tv/new_subject/6*
// @match        https://bangumi.tv/new_subject/6*
// @match        https://chii.in/new_subject/6*
// @match        https://bgm.tv/subject/*
// @match        https://bangumi.tv/subject/*
// @match        https://chii.in/subject/*
// @run-at       document-idle
// @grant        GM_getValue
// @grant        GM_setValue
// @grant        GM_registerMenuCommand
// @grant        GM_openInTab
// @grant        GM_xmlhttpRequest
// @grant        unsafeWindow
// @connect      *
// ==/UserScript==

(async function () {
    'use strict';

    const PREFIX = '[Audible → Bangumi]';
    const KEYS = {
        autoSubmit: 'audible_bangumi_auto_submit',
        tags: 'audible_bangumi_tags',
        oldTask: 'audible_bangumi_task',
        oldBusy: 'audible_bangumi_busy',
        taskPrefix: 'audible_bangumi_task_',
        relateSubjectPrefix: 'audible_bangumi_relate_subject_',
        autoUploadCover: 'audible_bangumi_auto_upload_cover',
        autoRelate: 'audible_bangumi_auto_relate',
        debug: 'audible_bangumi_debug'
    };
    const DEFAULT_TAGS = '日本 小说改 有声书';
    const BANGUMI_URL = 'https://bgm.tv/new_subject/6';
    const RELATE_PENDING_KEY = 'audiblePendingRelate';
    const RELATE_SAVE_PENDING_KEY = 'audibleRelateSavePending';
    const RELATE_PENDING_MAX_AGE = 24 * 60 * 60 * 1000;
    const RELATE_SUBMIT_MAX_AGE = 15 * 60 * 1000;

    // 诊断日志默认关闭，可在 Tampermonkey 菜单里打开。
    let debugEnabled = false;

    function debugLog(label, data) {
        if (!debugEnabled) return;

        let text = '';

        try {
            text = typeof data === 'string' ? data : JSON.stringify(data);
        } catch (_) {
            text = String(data);
        }

        const time = new Date().toLocaleTimeString('zh-CN', { hour12: false });
        const line = `${time} ${label}${text ? ' ' + text : ''}`;

        try {
            console.log(`${PREFIX}[debug] ${line}`);
        } catch (_) {}

        try {
            const panel = ensureDebugPanel();
            const pre = panel.querySelector('pre');
            const lines = pre.textContent ? pre.textContent.split('\n') : [];
            lines.push(line);

            while (lines.length > 45) lines.shift();

            pre.textContent = lines.join('\n');
            pre.scrollTop = pre.scrollHeight;
        } catch (error) {
            console.warn(`${PREFIX} 诊断面板更新失败。`, error);
        }
    }

    function ensureDebugPanel() {
        let panel = document.getElementById('audible-bangumi-debug-panel');
        if (panel) return panel;

        panel = document.createElement('div');
        panel.id = 'audible-bangumi-debug-panel';
        Object.assign(panel.style, {
            position: 'fixed',
            left: '8px',
            bottom: '8px',
            zIndex: '2147483647',
            width: 'min(560px, calc(100vw - 16px))',
            maxHeight: '38vh',
            padding: '8px',
            borderRadius: '6px',
            background: 'rgba(0,0,0,.84)',
            color: '#fff',
            font: '11px/1.45 Consolas, "Courier New", monospace',
            boxShadow: '0 2px 12px rgba(0,0,0,.4)'
        });

        const title = document.createElement('div');
        title.textContent = 'Audible → Bangumi 诊断（双击标题可隐藏）';
        title.style.cssText = 'font-weight:bold;margin-bottom:4px;color:#f09199;cursor:pointer;';
        title.addEventListener('dblclick', () => {
            panel.style.display = 'none';
        });

        const pre = document.createElement('pre');
        pre.style.cssText =
            'margin:0;white-space:pre-wrap;word-break:break-all;overflow:auto;max-height:32vh;';

        const copy = document.createElement('button');
        copy.type = 'button';
        copy.textContent = '复制日志';
        copy.style.cssText =
            'float:right;margin-left:8px;padding:1px 6px;border:1px solid #f09199;' +
            'border-radius:3px;background:transparent;color:#f09199;cursor:pointer;' +
            'font:inherit;';
        copy.addEventListener('click', async () => {
            try {
                await navigator.clipboard.writeText(pre.textContent || '');
                copy.textContent = '已复制';
            } catch (_) {
                copy.textContent = '复制失败';
            }
            setTimeout(() => {
                copy.textContent = '复制日志';
            }, 1500);
        });
        title.appendChild(copy);

        panel.appendChild(title);
        panel.appendChild(pre);
        document.body.appendChild(panel);
        return panel;
    }

    function createTaskId() {
        return `${Date.now().toString(36)}_${Math.random().toString(36).slice(2, 10)}`;
    }

    function taskStorageKey(taskId) {
        return `${KEYS.taskPrefix}${taskId}`;
    }

    function relateSubjectKey(subjectId) {
        return `${KEYS.relateSubjectPrefix}${subjectId}`;
    }

    function getTaskIdFromCurrentUrl() {
        const url = new URL(location.href);
        const queryId = url.searchParams.get('ab_task');
        if (queryId) return queryId;

        const match = location.hash.match(/(?:^#|&)ab_task=([^&]+)/);
        return match ? decodeURIComponent(match[1]) : '';
    }

    function getRelateTaskIdFromCurrentUrl() {
        const url = new URL(location.href);
        const queryId = url.searchParams.get('ab_relate');
        if (queryId) return queryId;

        const match = location.hash.match(/(?:^#|&)ab_relate=([^&]+)/);
        return match ? decodeURIComponent(match[1]) : '';
    }

    async function clearRelateTask(taskId) {
        if (!taskId) return;
        await clearTask(taskId);
        debugLog('relateTaskCleared', taskId);
    }

    function bangumiUrlForTask(taskId) {
        return `${BANGUMI_URL}?ab_task=${encodeURIComponent(taskId)}`;
    }

    function audibleUrlForTask(url, taskId) {
        return `${url.split('#')[0]}#ab_task=${encodeURIComponent(taskId)}`;
    }

    async function getTask(taskId) {
        return taskId ? await GM_getValue(taskStorageKey(taskId), null) : null;
    }

    async function setTask(taskId, task) {
        if (taskId) await GM_setValue(taskStorageKey(taskId), task);
    }

    async function clearTask(taskId) {
        if (taskId) await GM_setValue(taskStorageKey(taskId), null);
    }

    const host = location.hostname;
    const isAudible = /(^|\.)audible\.co\.jp$/i.test(host);
    const isBangumi = /^(bgm\.tv|bangumi\.tv|chii\.in)$/i.test(host);
    if (!isAudible && !isBangumi) return;

    try {
        debugEnabled = Boolean(await GM_getValue(KEYS.debug, false));
    } catch (_) {}

    async function getAutoSubmit() {
        return Boolean(await GM_getValue(KEYS.autoSubmit, false));
    }

    async function getTags() {
        return await GM_getValue(KEYS.tags, DEFAULT_TAGS);
    }

    async function getAutoRelate() {
        return Boolean(await GM_getValue(KEYS.autoRelate, true));
    }

    GM_registerMenuCommand(
        `后台自动提交：${await getAutoSubmit() ? '已开启' : '已关闭'}`,
        async () => {
            const next = !(await getAutoSubmit());
            await GM_setValue(KEYS.autoSubmit, next);
            alert(`${PREFIX}\n后台自动提交已${next ? '开启' : '关闭'}。`);
        }
    );

    GM_registerMenuCommand('设置默认标签', async () => {
        const current = await getTags();
        const value = prompt('输入默认标签，以空格分隔：', current);
        if (value === null) return;
        await GM_setValue(KEYS.tags, value.trim());
        alert(`${PREFIX}\n默认标签已保存。`);
    });

    GM_registerMenuCommand(
        `自动上传封面：${Boolean(await GM_getValue(KEYS.autoUploadCover, true)) ? '已开启' : '已关闭'}`,
        async () => {
            const next = !Boolean(await GM_getValue(KEYS.autoUploadCover, true));
            await GM_setValue(KEYS.autoUploadCover, next);
            alert(`${PREFIX}\n自动上传封面已${next ? '开启' : '关闭'}。需要独立封面上传组件处于启用状态。`);
        }
    );

    GM_registerMenuCommand(
        `自动关联人物：${Boolean(await GM_getValue(KEYS.autoRelate, true)) ? '已开启' : '已关闭'}`,
        async () => {
            const next = !Boolean(await GM_getValue(KEYS.autoRelate, true));
            await GM_setValue(KEYS.autoRelate, next);
            alert(
                `${PREFIX}\n自动关联人物已${next ? '开启' : '关闭'}。\n` +
                `需要安装并启用 bangumi-auto-relate-infobox 脚本。`
            );
        }
    );

    GM_registerMenuCommand(
        `诊断日志：${debugEnabled ? '已开启' : '已关闭'}`,
        async () => {
            debugEnabled = !debugEnabled;
            await GM_setValue(KEYS.debug, debugEnabled);

            if (debugEnabled) {
                const panel = ensureDebugPanel();
                panel.style.display = 'block';
                debugLog('debug', '诊断日志已开启');
            } else {
                const panel = document.getElementById(
                    'audible-bangumi-debug-panel'
                );
                if (panel) panel.remove();
            }

            alert(`${PREFIX}\n诊断日志已${debugEnabled ? '开启' : '关闭'}。`);
        }
    );

    GM_registerMenuCommand('为当前条目补做自动关联', async () => {
        const match = location.pathname.match(/^\/subject\/(\d+)\/?$/i);

        if (!match) {
            alert(`${PREFIX}\n请在 Bangumi 条目页使用这个菜单。`);
            return;
        }

        const subjectId = String(match[1]);
        const titleElement = document.querySelector(
            '#headerSubject h1.nameSingle a,#headerSubject h1,h1.nameSingle'
        );
        const title = cleanText(titleElement?.textContent || document.title);
        const relateTaskId = createTaskId();

        await setTask(relateTaskId, {
            id: relateTaskId,
            stage: 'relate',
            subjectId,
            title,
            createdAt: Date.now()
        });

        setPendingRelate(title);

        const pending = readPendingRelate();
        if (pending) {
            pending.subjectId = subjectId;
            pending.taskId = relateTaskId;
            savePendingRelate(pending);
        }

        const lastFill = readLastFill() || {};
        lastFill.relateTaskId = relateTaskId;
        lastFill.relateTitle = title;
        lastFill.relateOpenedTask = '';
        saveLastFill(lastFill);

        debugLog('manualRelate', {
            subjectId,
            title,
            relateTaskId,
            pending: readPendingRelate()
        });

        const action = await tryNavigateToRelatePage(subjectId);

        if (!action) {
            alert(
                `${PREFIX}\n未能打开自动关联页，请检查“自动关联人物”开关和页面标题。`
            );
        }
    });

    GM_registerMenuCommand('查看上次填充信息', () => {
        const info = readLastFill();
        alert(
            `${PREFIX}\n上次填充信息：\n` +
            (info ? JSON.stringify(info, null, 2) : '（无记录）')
        );
    });

    GM_registerMenuCommand('清理旧版卡住的任务状态', async () => {
        await GM_setValue(KEYS.oldTask, null);
        await GM_setValue(KEYS.oldBusy, false);
        alert(`${PREFIX}\n已清理旧版本的全局任务状态。新版每本书独立运行。`);
    });

    // =========================================================
    // 跨页面状态（全部使用 sessionStorage，按标签页隔离，可并行）
    // =========================================================

    function setPendingRelate(title) {
        try {
            sessionStorage.setItem(RELATE_PENDING_KEY, JSON.stringify({
                title: cleanText(title),
                subjectId: '',
                createdAt: Date.now()
            }));
        } catch (error) {
            console.warn(`${PREFIX} 无法保存待关联状态。`, error);
        }
    }

    function savePendingRelate(value) {
        try {
            sessionStorage.setItem(RELATE_PENDING_KEY, JSON.stringify(value));
        } catch (error) {
            console.warn(`${PREFIX} 无法更新待关联状态。`, error);
        }
    }

    function readPendingRelate() {
        let raw = '';

        try {
            raw = sessionStorage.getItem(RELATE_PENDING_KEY) || '';
        } catch (_) {
            return null;
        }

        if (!raw) return null;

        try {
            const value = JSON.parse(raw);

            if (!value || typeof value !== 'object' || !value.title) {
                throw new Error('invalid relate state');
            }

            if (
                !value.createdAt ||
                Date.now() - value.createdAt > RELATE_PENDING_MAX_AGE
            ) {
                clearPendingRelate();
                return null;
            }

            return value;
        } catch (_) {
            clearPendingRelate();
            return null;
        }
    }

    function clearPendingRelate() {
        try {
            sessionStorage.removeItem(RELATE_PENDING_KEY);
        } catch (_) {}
    }

    function setRelateSavePending(subjectId) {
        try {
            sessionStorage.setItem(RELATE_SAVE_PENDING_KEY, JSON.stringify({
                subjectId: String(subjectId),
                createdAt: Date.now()
            }));
        } catch (error) {
            console.warn(`${PREFIX} 无法保存关联提交状态。`, error);
        }
    }

    function readRelateSavePending() {
        let raw = '';

        try {
            raw = sessionStorage.getItem(RELATE_SAVE_PENDING_KEY) || '';
        } catch (_) {
            return null;
        }

        if (!raw) return null;

        try {
            const value = JSON.parse(raw);

            if (
                !value ||
                !value.subjectId ||
                !value.createdAt ||
                Date.now() - value.createdAt > 5 * 60 * 1000
            ) {
                clearRelateSavePending();
                return null;
            }

            return value;
        } catch (_) {
            clearRelateSavePending();
            return null;
        }
    }

    function clearRelateSavePending() {
        try {
            sessionStorage.removeItem(RELATE_SAVE_PENDING_KEY);
        } catch (_) {}
    }

    function hasPendingCover() {
        try {
            return Boolean(sessionStorage.getItem('pendingCoverUrl'));
        } catch (_) {
            return false;
        }
    }

    function saveLastFill(info) {
        try {
            sessionStorage.setItem('audibleLastFill', JSON.stringify({
                ...info,
                ts: Date.now()
            }));
        } catch (error) {
            debugLog('lastFillError', String(error && error.message || error));
        }
    }

    function readLastFill() {
        try {
            return JSON.parse(sessionStorage.getItem('audibleLastFill') || 'null');
        } catch (_) {
            return null;
        }
    }

    function updateLastFill(info) {
        saveLastFill({
            ...(readLastFill() || {}),
            ...info
        });
    }

    // 如果用户在提交前手动改了标题，提交时同步待关联/封面任务的标题，
    // 避免条目创建后因标题不一致而跳过自动关联或封面上传。
    function bindSubmitTitleSync() {
        const titleInput = document.querySelector('input[name="subject_title"]');
        const form = titleInput?.closest('form');

        if (!form || form.dataset.audibleRelateSyncBound === '1') return;

        form.dataset.audibleRelateSyncBound = '1';
        form.addEventListener('submit', () => {
            const currentTitle = cleanText(titleInput.value || '');
            if (!currentTitle) return;

            const pending = readPendingRelate();
            if (pending) {
                pending.title = currentTitle;
                // 表单可能停留很久，提交时刷新时间，避免被当成过期状态。
                pending.createdAt = Date.now();
                pending.submitted = true;
                pending.submittedAt = Date.now();
                savePendingRelate(pending);
            }

            const lastFill = readLastFill();
            if (lastFill) {
                lastFill.submitted = true;
                lastFill.submittedAt = Date.now();
                if (lastFill.relateTaskId) {
                    lastFill.relateTitle = currentTitle;
                }
                saveLastFill(lastFill);
            }

            if (hasPendingCover()) {
                sessionStorage.setItem('audiblePendingCoverTitle', currentTitle);
            }
        }, true);
    }

    function cleanText(value) {
        return (value || '').replace(/\s+/g, ' ').trim();
    }

    function normalizeJapaneseNames(value) {
        return (value || '')
            .split(/[、，,]/)
            .map(name => name.replace(/[\s\u3000]+/g, '').trim())
            .filter(Boolean)
            .join('、');
    }

    function stripHtml(html) {
        if (typeof html !== 'string') return '';
        const doc = new DOMParser().parseFromString(html, 'text/html');
        doc.body.querySelectorAll('script,style,noscript,iframe').forEach(el => el.remove());
        doc.body.querySelectorAll('br').forEach(el => el.replaceWith('\n'));
        doc.body.querySelectorAll('p,div,li,section').forEach(el => el.append('\n'));
        return (doc.body.textContent || '')
            .replace(/\u00a0/g, ' ')
            .replace(/\r/g, '')
            .replace(/[ \t]+\n/g, '\n')
            .replace(/\n[ \t]+/g, '\n')
            .replace(/[ \t]{2,}/g, ' ')
            .replace(/\n{3,}/g, '\n\n')
            .trim();
    }

    function normalizeProductUrl(url) {
        try {
            const parsed = new URL(url, location.origin);
            if (!/(^|\.)audible\.co\.jp$/i.test(parsed.hostname)) return '';
            if (!parsed.pathname.includes('/pd/')) return '';
            return `${parsed.origin}${parsed.pathname}`;
        } catch (_) {
            return '';
        }
    }

    function getAsin(url) {
        try {
            const parsed = new URL(url, location.origin);
            const match = parsed.pathname.match(/\/([A-Z0-9]{10})(?:\/?$)/i);
            return match ? match[1].toUpperCase() : '';
        } catch (_) {
            return '';
        }
    }

    function getAudibleTitle() {
        const candidates = [
            document.querySelector('h1'),
            document.querySelector('[data-testid*="title"]'),
            document.querySelector('meta[property="og:title"]')
        ];

        for (const el of candidates) {
            const value = el ? ('content' in el ? el.content : el.textContent) : '';
            const title = cleanText(value);
            if (title) {
                return title
                    .replace(/\s*[|｜]\s*オーディオブック.*$/i, '')
                    .replace(/\s*-\s*オーディオブック.*$/i, '')
                    .trim();
            }
        }

        return document.title.replace(/\s*[|｜]\s*Audible.*$/i, '').trim();
    }

    function getAudibleAuthor() {
        const scopes = [
            '#center-3',
            '#center-2',
            '#productDetails',
            '#product-details',
            '[data-testid="product-details"]',
            '[data-testid*="product-detail"]',
            'main'
        ].map(selector => document.querySelector(selector)).filter(Boolean);

        for (const scope of scopes) {
            const labels = [...scope.querySelectorAll('dt,th,span,div,p,h2,h3,h4')];

            for (const label of labels) {
                const labelText = cleanText(label.textContent).replace(/[：:]$/, '');
                if (labelText !== '著者' && labelText !== '作者') continue;
                if (label.children.length > 2) continue;

                const parent = label.parentElement;
                if (!parent) continue;

                const names = [...parent.querySelectorAll('a')]
                    .map(a => cleanText(a.textContent))
                    .filter(Boolean);

                if (names.length && names.length <= 3) {
                    return normalizeJapaneseNames([...new Set(names)].join('、'));
                }

                const next = label.nextElementSibling;
                if (next) {
                    const siblingNames = [...next.querySelectorAll('a')]
                        .map(a => cleanText(a.textContent))
                        .filter(Boolean);

                    if (siblingNames.length && siblingNames.length <= 3) {
                        return normalizeJapaneseNames([...new Set(siblingNames)].join('、'));
                    }
                }
            }
        }

        const normalize = value => cleanText(value)
            .replace(/[「」『』【】\[\]（）()]/g, '')
            .replace(/[\s　]+/g, '')
            .toLocaleLowerCase();

        const currentTitle = normalize(getAudibleTitle());
        const asin = getAsin(location.href);
        const entities = [];

        function walk(value) {
            if (!value || typeof value !== 'object') return;
            if (Array.isArray(value)) {
                value.forEach(walk);
                return;
            }

            const type = Array.isArray(value['@type'])
                ? value['@type'].join(' ')
                : String(value['@type'] || '');
            const title = normalize(value.name || value.headline || '');
            const url = String(value.url || value['@id'] || '');
            const productId = String(value.productID || value.sku || value.mpn || '');

            if (
                /Book|Audiobook|Product|CreativeWork/i.test(type) &&
                title &&
                (title === currentTitle || (asin && (url.includes(asin) || productId.includes(asin))))
            ) {
                entities.push(value);
            }

            Object.values(value).forEach(child => {
                if (child && typeof child === 'object') walk(child);
            });
        }

        document.querySelectorAll('script[type="application/ld+json"]').forEach(script => {
            try { walk(JSON.parse(script.textContent)); } catch (_) {}
        });

        for (const entity of entities) {
            const author = entity.author || entity.creator;
            const list = Array.isArray(author) ? author : (author ? [author] : []);
            const names = list
                .map(item => typeof item === 'string' ? cleanText(item) : cleanText(item?.name || ''))
                .filter(Boolean);

            if (names.length && names.length <= 3) {
                return normalizeJapaneseNames([...new Set(names)].join('、'));
            }
        }

        return '';
    }

    function getAudibleNarrator() {
        const selectors = [
            '[data-testid*="narrator"] a',
            'a[href*="/narrator/"]',
            '[aria-label*="ナレーター"] a'
        ];

        for (const selector of selectors) {
            const names = [...document.querySelectorAll(selector)]
                .map(el => cleanText(el.textContent))
                .filter(Boolean);
            if (names.length) return normalizeJapaneseNames([...new Set(names)].join('、'));
        }

        const bodyText = document.body.innerText || '';
        const match = bodyText.match(/(?:ナレーター|朗読|ナレーション)\s*[:：]?\s*([^\n]{1,100})/);

        if (match) {
            return normalizeJapaneseNames(
                cleanText(match[1]).replace(/\s+(?:出版社|再生時間|配信日|発売日).*$/, '')
            );
        }

        return '';
    }

    function getAudibleReleaseDate() {
        const selectors = [
            'meta[property="book:release_date"]',
            'meta[property="og:release_date"]',
            'meta[itemprop="datePublished"]',
            'time[datetime]'
        ];

        for (const selector of selectors) {
            const el = document.querySelector(selector);
            const value = cleanText(el?.content || el?.getAttribute('datetime') || '');
            const match = value.match(/\b(20\d{2}|19\d{2})[-/年.](\d{1,2})[-/月.](\d{1,2})/);

            if (match) {
                return `${match[1]}-${match[2].padStart(2, '0')}-${match[3].padStart(2, '0')}`;
            }
        }

        const match = (document.body.innerText || '').match(
            /(?:配信日|発売日|出版日|リリース日)\s*[:：]?\s*(20\d{2}|19\d{2})年(\d{1,2})月(\d{1,2})日/
        );

        return match
            ? `${match[1]}-${match[2].padStart(2, '0')}-${match[3].padStart(2, '0')}`
            : '';
    }

    function getAudibleDescription() {
        const descriptions = [];

        function walk(data) {
            if (!data || typeof data !== 'object') return;
            if (typeof data.description === 'string' && data.description.trim().length >= 30) {
                descriptions.push(data.description.trim());
            }
            if (Array.isArray(data)) data.forEach(walk);
            else Object.values(data).forEach(value => {
                if (value && typeof value === 'object') walk(value);
            });
        }

        document.querySelectorAll('script[type="application/ld+json"]').forEach(script => {
            try { walk(JSON.parse(script.textContent)); } catch (_) {}
        });

        for (const value of descriptions) {
            const text = stripHtml(value);
            if (text.length >= 30) return text;
        }

        const og = document.querySelector('meta[property="og:description"]')?.content || '';
        const cleaned = stripHtml(og);
        return cleaned.length >= 30 ? cleaned : '';
    }

    function buildInfobox(data) {
        return [
            '{{Infobox real/Television',
            '|中文名=',
            '|别名=',
            '|季数=',
            '|放送星期=',
            `|开始=${data.releaseDate}`,
            '|结束=',
            `|主演=${data.narrator}`,
            '|导演=',
            '|音乐=',
            `|原作=${data.author}`,
            '|制作=Audible Studios',
            '|类型=有声书',
            '|国家/地区=日本',
            '|语言=日本語',
            '|每集长=',
            '|在线播放平台=Audible',
            '|电视网=',
            '|电视台=',
            '|频道=',
            '|视频制式=',
            '|音频制式=',
            '|首播国家=',
            '|首播地区=',
            '|台湾名称=',
            '|港澳名称=',
            '|马新名称=',
            '|官方网站=',
            `|链接={[Audible|${data.url}]}`,
            '|imdb_id=',
            '|tvdb_id=',
            '}}'
        ].join('\n');
    }

    function getAudibleCoverCandidates() {
        const candidates = [];
        const knownCovers = {
            'B0C58LD5WG': 'https://m.media-amazon.com/images/I/51p4U9l3-LL._SL500_.jpg'
        };
        const currentAsin = getAsin(location.href);

        if (currentAsin && knownCovers[currentAsin]) {
            candidates.push({ url: knownCovers[currentAsin], score: 10000 });
        }

        const add = (value, priority = 0) => {
            if (!value || typeof value !== 'string') return;

            try {
                const url = new URL(value.replace(/&amp;/g, '&'), location.href);
                if (url.protocol !== 'https:') return;
                if (!/(^|\.)audible\.co\.jp$|(^|\.)media-amazon\.com$|(^|\.)ssl-images-amazon\.com$|(^|\.)images-amazon\.com$/i.test(url.hostname)) return;
                if (/logo|banner|advert|sprite|icon/i.test(url.href)) return;

                const isAmazonCover =
                    /(^|\.)m\.media-amazon\.com$/i.test(url.hostname) &&
                    /\/images\/I\//i.test(url.pathname);
                const has500 = /\._SL500_\./i.test(url.href);

                candidates.push({
                    url: url.href,
                    score: priority + (isAmazonCover ? 100 : 0) + (has500 ? 50 : 0)
                });
            } catch (_) {}
        };

        const mainImageSelectors = [
            '[data-testid*="cover"] img',
            '[data-testid*="image"] img',
            'img[alt*="カバーアート"]',
            'img[alt*="表紙"]',
            'img[src*="m.media-amazon.com/images/I/"]',
            'img[data-src*="m.media-amazon.com/images/I/"]'
        ];

        for (const selector of mainImageSelectors) {
            document.querySelectorAll(selector).forEach(img => {
                add(img.currentSrc || img.getAttribute('data-src') || img.src || '', 20);
            });
        }

        document.querySelectorAll('script[type="application/ld+json"]').forEach(script => {
            try {
                const walk = value => {
                    if (!value || typeof value !== 'object') return;

                    if (typeof value.image === 'string') add(value.image, 5);
                    else if (Array.isArray(value.image)) {
                        value.image.forEach(item => add(
                            typeof item === 'string' ? item : item && (item.url || item.contentUrl),
                            5
                        ));
                    } else if (value.image && typeof value.image === 'object') {
                        add(value.image.url || value.image.contentUrl, 5);
                    }

                    if (Array.isArray(value)) value.forEach(walk);
                    else Object.values(value).forEach(item => {
                        if (item && typeof item === 'object') walk(item);
                    });
                };

                walk(JSON.parse(script.textContent));
            } catch (_) {}
        });

        document.querySelectorAll('meta[property="og:image"],meta[name="twitter:image"]').forEach(el => add(el.content, 0));

        const unique = new Map();
        for (const item of candidates) {
            const old = unique.get(item.url);
            if (!old || item.score > old.score) unique.set(item.url, item);
        }

        return [...unique.values()].sort((a, b) => b.score - a.score).map(item => item.url);
    }

    function extractAudibleData() {
        const data = {
            title: getAudibleTitle(),
            author: getAudibleAuthor(),
            narrator: getAudibleNarrator(),
            releaseDate: getAudibleReleaseDate(),
            description: getAudibleDescription(),
            coverCandidates: getAudibleCoverCandidates(),
            coverUrl: '',
            url: location.href.split('#')[0].split('?')[0]
        };

        data.coverUrl = data.coverCandidates[0] || '';
        data.infobox = buildInfobox(data);
        return data;
    }

    function isListingPage() {
        const path = location.pathname;
        return path === '/search' || path.startsWith('/search/') ||
            path === '/tag' || path.startsWith('/tag/') ||
            path === '/series' || path.startsWith('/series/');
    }

    function createListButton(productUrl) {
        const button = document.createElement('button');
        button.type = 'button';
        button.textContent = '添加到 Bangumi';
        button.className = 'audible-bangumi-list-button';
        button.dataset.productUrl = productUrl;

        Object.assign(button.style, {
            display: 'inline-block',
            margin: '5px 4px',
            padding: '5px 9px',
            border: '0',
            borderRadius: '5px',
            background: '#f09199',
            color: '#fff',
            fontSize: '12px',
            lineHeight: '1.5',
            cursor: 'pointer',
            position: 'relative',
            zIndex: '20'
        });

        button.addEventListener('mouseenter', () => {
            button.style.background = '#e77c86';
        });
        button.addEventListener('mouseleave', () => {
            button.style.background = '#f09199';
        });

        button.addEventListener('click', async event => {
            event.preventDefault();
            event.stopPropagation();
            if (button.disabled) return;

            const url = button.dataset.productUrl;
            if (!url) return;

            // 每次点击创建独立任务；不检查全局 busy 状态。
            const taskId = createTaskId();
            const asin = getAsin(url);

            await setTask(taskId, {
                id: taskId,
                stage: 'extract',
                url,
                asin,
                createdAt: Date.now()
            });

            button.disabled = true;
            button.textContent = '处理中…';

            try {
                GM_openInTab(audibleUrlForTask(url, taskId), {
                    active: false,
                    insert: true,
                    setParent: false
                });
                button.textContent = '已开始处理';
            } catch (error) {
                await clearTask(taskId);
                button.disabled = false;
                button.textContent = '添加到 Bangumi';
                alert(`${PREFIX}\n无法打开商品详情页：${error.message || error}`);
            }
        });

        return button;
    }

    function addButtonsToListing() {
        if (!isListingPage()) return;

        for (const anchor of document.querySelectorAll('a[href*="/pd/"]')) {
            if (anchor.dataset.bangumiButtonAdded === '1') continue;

            const productUrl = normalizeProductUrl(anchor.href);
            if (!productUrl) continue;
            if (!cleanText(anchor.innerText || anchor.textContent)) continue;

            anchor.dataset.bangumiButtonAdded = '1';
            const button = createListButton(productUrl);
            anchor.insertAdjacentElement('afterend', button);
        }
    }

    function startListingObserver() {
        addButtonsToListing();
        const observer = new MutationObserver(addButtonsToListing);
        observer.observe(document.body, { childList: true, subtree: true });
    }

    async function handleProductTask() {
        const taskId = getTaskIdFromCurrentUrl();
        const task = await getTask(taskId);

        if (!taskId || !task || task.stage !== 'extract') return;

        const currentAsin = getAsin(location.href);
        if (task.asin && currentAsin && task.asin !== currentAsin) return;

        if (
            task.url &&
            normalizeProductUrl(task.url) !== normalizeProductUrl(location.href)
        ) {
            return;
        }

        await new Promise(resolve => setTimeout(resolve, 1200));

        const data = extractAudibleData();

        if (!data.title) {
            console.error(`${PREFIX} 未提取到标题，任务中止。`);
            await clearTask(taskId);
            alert(`${PREFIX}\n商品详情页未能提取标题，请检查页面是否加载完成。`);
            return;
        }

        await setTask(taskId, {
            ...task,
            stage: 'fill',
            data,
            updatedAt: Date.now()
        });

        GM_openInTab(bangumiUrlForTask(taskId), {
            active: !(await getAutoSubmit()),
            insert: true,
            setParent: true
        });

        console.info(`${PREFIX} 已提取商品信息，正在打开 Bangumi。`, {
            taskId,
            title: data.title,
            author: data.author,
            narrator: data.narrator,
            descriptionLength: data.description.length
        });
    }

    function setInputValue(element, value) {
        if (!element || value == null) return false;

        const prototype =
            element instanceof HTMLTextAreaElement
                ? HTMLTextAreaElement.prototype
                : element instanceof HTMLInputElement
                    ? HTMLInputElement.prototype
                    : element instanceof HTMLSelectElement
                        ? HTMLSelectElement.prototype
                        : null;

        const setter = prototype
            ? Object.getOwnPropertyDescriptor(prototype, 'value')?.set
            : null;

        if (setter) setter.call(element, String(value));
        else element.value = String(value);

        element.dispatchEvent(new Event('input', { bubbles: true }));
        element.dispatchEvent(new Event('change', { bubbles: true }));
        return true;
    }

    function selectOtherType() {
        const radio = document.querySelector(
            'input[type="radio"]#cat_misc[name="platform"]'
        );

        if (!radio) {
            console.warn(`${PREFIX} 未找到“其他”类型单选框。`);
            return false;
        }

        radio.checked = true;
        radio.dispatchEvent(new Event('input', { bubbles: true }));
        radio.dispatchEvent(new Event('change', { bubbles: true }));
        return true;
    }

    async function fillBangumiForm(data) {
        const fields = [
            {
                name: '标题',
                element: document.querySelector('input[name="subject_title"]'),
                value: data.title
            },
            {
                name: '条目信息',
                element: document.querySelector('#subject_infobox'),
                value: data.infobox
            },
            {
                name: '简介',
                element: document.querySelector('#subject_summary'),
                value: data.description
            },
            {
                name: '标签',
                element: document.querySelector('#tags[name="subject_meta_tags"]'),
                value: await getTags()
            }
        ];

        const missing = [];

        for (const field of fields) {
            if (!field.element) {
                missing.push(field.name);
                continue;
            }
            setInputValue(field.element, field.value);
        }

        if (!selectOtherType()) missing.push('类型（其他）');

        return {
            success: missing.length === 0,
            missing,
            titleFilled: Boolean(
                document.querySelector('input[name="subject_title"]')?.value.trim()
            )
        };
    }

    function findSubmitButton() {
        const title = document.querySelector('input[name="subject_title"]');
        const form = title?.closest('form');
        const scope = form || document;

        const candidates = [
            ...scope.querySelectorAll(
                'button[type="submit"],input[type="submit"],button:not([type])'
            )
        ];

        const exact = candidates.find(el => {
            const text = cleanText(el.textContent || el.value || '');
            return /^(添加|提交|新建条目|添加条目|登録|作成)$/.test(text);
        });

        if (exact) return exact;

        return candidates.find(el => {
            const text = cleanText(el.textContent || el.value || '');
            return /添加|提交|登録|作成/.test(text);
        }) || null;
    }

    async function handleBangumiPage() {
        const taskId = getTaskIdFromCurrentUrl();
        const task = await getTask(taskId);

        if (!taskId || !task || task.stage !== 'fill' || !task.data) return;

        const data = task.data;
        const autoCover = Boolean(await GM_getValue(KEYS.autoUploadCover, true));
        const autoRelateEnabled = await getAutoRelate();
        const autoSubmitEnabled = await getAutoSubmit();

        const fillInfo = {
            title: data.title,
            author: data.author,
            narrator: data.narrator,
            autoRelate: autoRelateEnabled,
            autoSubmit: autoSubmitEnabled,
            autoCover,
            coverUrl: Boolean(data.coverUrl)
        };

        debugLog('fill', fillInfo);
        saveLastFill(fillInfo);

        if (autoCover && data.coverUrl) {
            sessionStorage.setItem('pendingCoverUrl', data.coverUrl);
            sessionStorage.setItem(
                'pendingCoverCandidates',
                JSON.stringify(data.coverCandidates || [data.coverUrl])
            );
            sessionStorage.setItem('audiblePendingCoverTitle', data.title || '');
        }

        // 先创建 GM 关联任务并写 lastFill，再填表。
        // 关联页会在新标签页打开，通过 GM 存储 + URL 里的 ab_relate 读取任务，
        // 不再依赖原标签页的 sessionStorage，也不会被封面刷新打断。
        if (autoRelateEnabled) {
            if (cleanText(data.author) || cleanText(data.narrator)) {
                const oldFill = readLastFill();
                if (oldFill?.relateTaskId) {
                    await clearTask(oldFill.relateTaskId);
                }

                const relateTaskId = createTaskId();

                try {
                    await setTask(relateTaskId, {
                        id: relateTaskId,
                        stage: 'relate',
                        subjectId: '',
                        title: data.title,
                        createdAt: Date.now()
                    });
                } catch (error) {
                    debugLog(
                        'relateTaskError',
                        String(error && error.message || error)
                    );
                }

                setPendingRelate(data.title);
                const pendingNow = readPendingRelate();

                saveLastFill({
                    ...fillInfo,
                    pendingSet: Boolean(pendingNow),
                    relateTaskId,
                    relateTitle: data.title
                });

                debugLog('pendingRelate', pendingNow);
                debugLog('relateTaskCreated', {
                    relateTaskId,
                    pendingOk: Boolean(pendingNow)
                });
                console.info(
                    `${PREFIX} 已记录自动关联任务，条目创建后将打开人物关联页。`,
                    taskId
                );
            } else {
                debugLog('skipRelate', '作者和声优都为空');
                saveLastFill({ ...fillInfo, pendingSet: false, reason: 'noStaff' });
                console.info(`${PREFIX} 没有可关联的人物信息，跳过自动关联。`, taskId);
            }
        } else {
            debugLog('skipRelate', '自动关联开关已关闭');
            saveLastFill({ ...fillInfo, pendingSet: false, reason: 'autoRelateOff' });
        }

        const deadline = Date.now() + 15000;

        while (
            !document.querySelector('input[name="subject_title"]') &&
            Date.now() < deadline
        ) {
            await new Promise(resolve => setTimeout(resolve, 250));
        }

        if (!document.querySelector('input[name="subject_title"]')) {
            console.error(`${PREFIX} 未找到 Bangumi 新建条目表单。`, taskId);
            await clearTask(taskId);
            alert(`${PREFIX}\n未找到新建条目表单，任务已取消。`);
            return;
        }

        const result = await fillBangumiForm(data);

        if (!result.titleFilled) {
            updateLastFill({
                ...fillInfo,
                pendingSet: Boolean(readPendingRelate()),
                reason: 'titleNotFilled'
            });
            await clearTask(taskId);
            alert(`${PREFIX}\n标题填入失败，任务已取消。`);
            return;
        }

        bindSubmitTitleSync();

        if (!autoSubmitEnabled) {
            updateLastFill({
                ...fillInfo,
                pendingSet: Boolean(readPendingRelate()),
                manualMode: true
            });
            await clearTask(taskId);
            console.info(`${PREFIX} 已填好表单，请检查后手动提交。`, taskId);
            alert(`${PREFIX}\n已自动提取并填写表单。\n请检查条目信息后手动提交。`);
            return;
        }

        if (!result.success) {
            updateLastFill({
                ...fillInfo,
                pendingSet: Boolean(readPendingRelate()),
                reason: 'missingFields',
                missing: result.missing
            });
            await clearTask(taskId);
            alert(`${PREFIX}\n表单元素缺失，已取消自动提交：\n${result.missing.join('、')}`);
            return;
        }

        const submitButton = findSubmitButton();

        if (!submitButton || submitButton.disabled) {
            updateLastFill({
                ...fillInfo,
                pendingSet: Boolean(readPendingRelate()),
                reason: 'noSubmitButton'
            });
            await clearTask(taskId);
            alert(`${PREFIX}\n未找到可用的提交按钮，请手动检查。`);
            return;
        }

        // 只清理当前任务，不影响其他并行任务。
        await clearTask(taskId);
        await new Promise(resolve => setTimeout(resolve, 800));
        updateLastFill({
            ...fillInfo,
            pendingSet: Boolean(readPendingRelate()),
            submittedByScript: true,
            submittedAt: Date.now()
        });
        submitButton.click();

        console.info(`${PREFIX} 已提交条目表单。`, taskId);
    }

    function delay(ms) {
        return new Promise(resolve => setTimeout(resolve, ms));
    }

    function waitForElement(selector, timeout = 20000) {
        return new Promise(resolve => {
            const started = Date.now();

            const check = () => {
                const element = document.querySelector(selector);
                if (element) return resolve(element);
                if (Date.now() - started >= timeout) return resolve(null);
                setTimeout(check, 300);
            };

            check();
        });
    }

    function pinCoverUploadFormForAutomation() {
        const uploadButton = document.querySelector('#coverUploadButton');
        const formContainer = document.getElementById('coverUploadFormContainer');

        if (
            uploadButton &&
            formContainer &&
            formContainer.dataset.audibleAutomationPinned !== '1'
        ) {
            formContainer.dataset.audibleAutomationPinned = '1';
            uploadButton.click();
        }

        if (formContainer) {
            formContainer.dataset.audibleAutomationPinned = '1';
            formContainer.style.display = 'block';
        }

        return formContainer;
    }

    async function ensureCoverUploadFormReady(timeout = 75000) {
        const started = Date.now();

        while (Date.now() - started < timeout) {
            try {
                if (typeof window.__bgmaInitCoverUpload === 'function') {
                    await window.__bgmaInitCoverUpload();
                }
            } catch (error) {
                console.warn(`${PREFIX} 初始化封面组件失败，将重试。`, error);
            }

            const uploadButton = document.querySelector('#coverUploadButton');

            if (uploadButton) {
                uploadButton.dispatchEvent(new MouseEvent('mouseenter', { bubbles: true }));
                uploadButton.dispatchEvent(new MouseEvent('mouseover', { bubbles: true }));
            }

            pinCoverUploadFormForAutomation();

            const fileInput = document.querySelector('#coverUploadForm input[type="file"]');
            if (fileInput) return fileInput;

            await delay(650);
        }

        return null;
    }

    function downloadImage(url, timeout = 35000) {
        return new Promise((resolve, reject) => {
            GM_xmlhttpRequest({
                method: 'GET',
                url,
                responseType: 'blob',
                timeout,
                headers: {
                    Accept: 'image/avif,image/webp,image/png,image/jpeg,image/*,*/*;q=0.8'
                },
                onload: response => {
                    if (response.status >= 200 && response.status < 300) {
                        resolve(response.response);
                    } else {
                        reject(new Error(`封面下载失败：HTTP ${response.status}`));
                    }
                },
                onerror: () => reject(new Error('封面下载网络错误')),
                ontimeout: () => reject(new Error('封面下载超时'))
            });
        });
    }

    async function downloadImageWithRetry(url, maxAttempts = 3) {
        let lastError;

        for (let attempt = 1; attempt <= maxAttempts; attempt++) {
            try {
                const blob = await downloadImage(url);

                if (!blob || blob.size < 1024) {
                    throw new Error('下载到的封面文件为空或过小');
                }

                if (
                    blob.type &&
                    !blob.type.toLowerCase().startsWith('image/') &&
                    blob.type !== 'application/octet-stream'
                ) {
                    throw new Error(`下载内容不是图片：${blob.type}`);
                }

                return blob;
            } catch (error) {
                lastError = error;
                if (attempt < maxAttempts) {
                    await delay(1200 * Math.pow(2, attempt - 1));
                }
            }
        }

        throw lastError || new Error('封面下载失败');
    }

    function waitForSubmitButton(timeout = 60000) {
        return new Promise(resolve => {
            const started = Date.now();

            const check = () => {
                pinCoverUploadFormForAutomation();

                const form = document.querySelector('#coverUploadForm');
                const fileInput = document.querySelector('#coverUploadForm input[type="file"]');
                const button = document.querySelector('#coverUploadForm input[type="submit"]');

                if (
                    form &&
                    fileInput?.files?.length &&
                    button?.isConnected &&
                    button.form === form
                ) {
                    button.style.display = 'block';
                    button.disabled = false;
                    resolve(button);
                    return;
                }

                if (Date.now() - started >= timeout) {
                    resolve(null);
                    return;
                }

                setTimeout(check, 500);
            };

            check();
        });
    }

    function normalizeTitleForMatch(value) {
        return cleanText(value)
            .replace(/[\s　]+/g, '')
            .toLocaleLowerCase();
    }

    function titlesMatch(actual, expected) {
        const a = normalizeTitleForMatch(actual);
        const e = normalizeTitleForMatch(expected);

        if (!a || !e) return false;
        if (a === e) return true;

        // Bangumi 有时会在 h1 标题后追加状态文字，例如“类型追加提交”。
        const statusSuffixes = [
            '类型追加提交',
            '类型变更提交',
            '类型追加',
            '类型变更',
            '已锁定',
            '锁定'
        ];

        for (const suffix of statusSuffixes) {
            const s = normalizeTitleForMatch(suffix);
            if (s && a.endsWith(s) && a.slice(0, -s.length) === e) {
                return true;
            }
        }

        // 兼容标题被追加/截断的情况；两边都足够长时前缀匹配是安全的。
        return a.startsWith(e) || e.startsWith(a);
    }

    async function autoUploadCoverOnSubjectPage() {
        if (!Boolean(await GM_getValue(KEYS.autoUploadCover, true))) return;

        const coverUrl = sessionStorage.getItem('pendingCoverUrl');
        if (!coverUrl) return;

        if (document.getElementById('audible-auto-cover-started')) return;

        const expectedTitle = sessionStorage.getItem('audiblePendingCoverTitle') || '';
        const titleElement = await waitForElement(
            '#headerSubject h1.nameSingle a,#headerSubject h1,h1.nameSingle',
            12000
        );
        const actualTitle = cleanText(titleElement?.textContent || '');
        const coverTitleMatch = Boolean(
            actualTitle &&
            (!expectedTitle || titlesMatch(actualTitle, expectedTitle))
        );

        debugLog('coverTitleCheck', {
            expectedTitle,
            actualTitle,
            coverTitleMatch
        });

        if (!coverTitleMatch) {
            console.warn(
                `${PREFIX} 条目标题与封面任务不匹配，跳过自动上传。`,
                { expectedTitle, actualTitle }
            );
            return;
        }

        const marker = document.createElement('div');
        marker.id = 'audible-auto-cover-started';
        marker.style.display = 'none';
        document.body.appendChild(marker);

        let savedCandidates = [];

        try {
            savedCandidates = JSON.parse(
                sessionStorage.getItem('pendingCoverCandidates') || '[]'
            );
        } catch (_) {}

        const candidates = [...new Set([...savedCandidates, coverUrl].filter(Boolean))];

        try {
            console.info(`${PREFIX} 等待封面上传组件...`);

            const fileInput = await ensureCoverUploadFormReady(75000);

            if (!fileInput) {
                throw new Error(
                    '封面组件上传表单未加载。请确认独立封面组件已启用。'
                );
            }

            let blob = null;
            let chosenUrl = '';
            let lastError = null;

            for (const candidate of candidates) {
                try {
                    blob = await downloadImageWithRetry(candidate, 3);
                    chosenUrl = candidate;
                    break;
                } catch (error) {
                    lastError = error;
                    console.warn(`${PREFIX} 封面候选下载失败`, candidate, error);
                }
            }

            if (!blob) {
                throw lastError || new Error('所有封面候选下载失败。');
            }

            const fileName =
                (chosenUrl.split('/').pop() || 'audible-cover.jpg')
                    .split('?')[0] || 'audible-cover.jpg';

            const mimeType =
                blob.type && blob.type.startsWith('image/')
                    ? blob.type
                    : (/\.png(?:\?|$)/i.test(chosenUrl) ? 'image/png' : 'image/jpeg');

            const file = new File([blob], fileName, { type: mimeType });
            const transfer = new DataTransfer();
            transfer.items.add(file);
            fileInput.files = transfer.files;

            const submitButton = await waitForSubmitButton(60000);

            if (!submitButton) {
                throw new Error('图片已下载，但组件未出现可用的上传按钮。');
            }

            console.info(`${PREFIX} 已将封面交给独立组件，正在提交...`, chosenUrl);

            // 实际上传及投票仍由独立封面组件处理。
            submitButton.click();

            sessionStorage.removeItem('pendingCoverUrl');
            sessionStorage.removeItem('pendingCoverCandidates');
            sessionStorage.removeItem('audiblePendingCoverTitle');
        } catch (error) {
            console.error(`${PREFIX} 自动上传封面失败：`, error);
            marker.remove();

            alert(
                `${PREFIX}\n条目已创建，但自动上传封面失败：\n` +
                `${error.message || error}`
            );
        }
    }

    // =========================================================
    // 条目创建后：跳到人物关联页
    // =========================================================

    async function resolvePendingRelate(subjectId) {
        let pending = readPendingRelate();

        if (pending) return pending;

        // sessionStorage 里的待关联状态可能因为封面刷新/标签页差异丢失，
        // 此时用 lastFill 里的 GM 任务 ID 恢复。
        const lastFill = readLastFill();
        const taskId = lastFill && lastFill.relateTaskId;

        if (!taskId) return null;

        const task = await getTask(taskId);

        if (!task || task.stage !== 'relate' || !task.title) {
            debugLog('resolveRelateTask', { taskId, task, reason: 'invalid' });
            return null;
        }

        if (
            !task.createdAt ||
            Date.now() - task.createdAt > RELATE_PENDING_MAX_AGE
        ) {
            debugLog('resolveRelateTask', { taskId, reason: 'expired' });
            await clearRelateTask(taskId);
            return null;
        }

        pending = {
            title: lastFill.relateTitle || task.title,
            subjectId: task.subjectId || '',
            createdAt: task.createdAt,
            taskId
        };

        debugLog('resolveRelateTask', pending);
        return pending;
    }

    function markRelateOpened(taskId) {
        const lastFill = readLastFill();
        if (!lastFill) return;

        lastFill.relateTaskId = '';
        lastFill.relateOpenedTask = taskId;
        lastFill.relateOpenedAt = Date.now();
        saveLastFill(lastFill);
    }

    async function tryNavigateToRelatePage(subjectId) {
        const pending = await resolvePendingRelate(subjectId);
        debugLog('tryRelate', { subjectId, pending });

        if (!pending) return false;

        if (!(await getAutoRelate())) {
            debugLog('tryRelate', '自动关联开关已关闭');
            clearPendingRelate();
            return false;
        }

        if (pending.subjectId && String(pending.subjectId) !== String(subjectId)) {
            debugLog('tryRelate', 'subjectId 不匹配');
            return false;
        }

        const lastFillBefore = readLastFill();
        if (
            pending.taskId &&
            lastFillBefore?.relateOpenedTask === pending.taskId
        ) {
            debugLog('tryRelate', '该任务已经打开过关联页');
            clearPendingRelate();
            return false;
        }

        // 关联流程不再用标题做门槛：条目 ID 才是唯一标识。
        // 只要求“表单确实提交过”或者“这是针对当前条目的手动补做”，
        // 避免一个 abandoned 的待关联状态误触发到别的条目。
        const explicitSubject = Boolean(
            pending.subjectId &&
            String(pending.subjectId) === String(subjectId)
        );
        const submittedAt =
            pending.submittedAt || lastFillBefore?.submittedAt || 0;
        const freshSubmit = Boolean(
            !submittedAt ||
            Date.now() - submittedAt <= RELATE_SUBMIT_MAX_AGE
        );
        const submitted = Boolean(
            (
                pending.submitted ||
                lastFillBefore?.submitted ||
                lastFillBefore?.submittedByScript
            ) &&
            freshSubmit
        ) || explicitSubject;

        debugLog('relateIntent', {
            subjectId,
            pendingSubjectId: pending.subjectId || '',
            explicitSubject,
            submittedAt,
            freshSubmit,
            submitted
        });

        if (!submitted) {
            debugLog('tryRelate', '表单尚未提交且不是当前条目补做，跳过');
            return false;
        }

        const titleElement = document.querySelector(
            '#headerSubject h1.nameSingle a,#headerSubject h1,h1.nameSingle'
        );
        const actualTitle = cleanText(titleElement?.textContent || '');
        const expectedTitle = cleanText(pending.title);

        debugLog('titleInfo', {
            subjectId,
            expectedTitle,
            actualTitle,
            note: 'title 仅用于日志，不参与关联匹配'
        });

        const taskId = pending.taskId || createTaskId();
        const task = {
            id: taskId,
            stage: 'relate',
            subjectId: String(subjectId),
            title: actualTitle || expectedTitle,
            createdAt: Date.now()
        };

        try {
            await setTask(taskId, task);
        } catch (error) {
            debugLog('relateTaskError', String(error && error.message || error));
        }

        debugLog('relateTaskReady', task);

        // 任务按 subjectId 存到 GM 存储，关联页用完全裸的 URL 打开。
        // 不要带 query，也不要带 hash：这个页面带任何后缀时，
        // bangumi-auto-relate-infobox 的“自动关联”按钮都不会出现。
        let pointerStored = false;

        try {
            await GM_setValue(relateSubjectKey(subjectId), {
                taskId,
                subjectId: String(subjectId),
                title: task.title,
                createdAt: Date.now()
            });
            pointerStored = true;
        } catch (error) {
            debugLog(
                'relateSubjectTaskError',
                String(error && error.message || error)
            );
        }

        if (!pointerStored) {
            console.warn(`${PREFIX} 无法保存关联任务指针，已停止本次自动关联。`);
            debugLog('relateSubjectTaskError', 'pointer store failed');
            return false;
        }

        clearPendingRelate();
        markRelateOpened(taskId);

        const relateUrl = `${location.origin}/subject/${subjectId}/add_related/person`;

        console.info(`${PREFIX} 已创建关联任务，正在新标签页打开人物关联页。`, {
            subjectId,
            relateUrl
        });
        debugLog('openRelateTab', relateUrl);

        try {
            GM_openInTab(relateUrl, {
                active: false,
                insert: true,
                setParent: false
            });
            return 'tab';
        } catch (error) {
            console.warn(`${PREFIX} 无法打开新标签页，改在当前页继续。`, error);
            debugLog('openRelateTabError', String(error && error.message || error));
            try {
                sessionStorage.setItem('audibleRelateSameTab', '1');
            } catch (_) {}
            location.href = relateUrl;
            return 'same';
        }
    }

    async function handleSubjectPage(subjectId, isUploadPage = false) {
        // 从人物关联页保存后回到条目页时，清掉中转状态。
        clearRelateSavePending();

        let pendingRaw = '';
        try {
            pendingRaw = sessionStorage.getItem(RELATE_PENDING_KEY) || '';
        } catch (_) {}

        const lastFill = readLastFill();
        debugLog('lastFill', lastFill);
        debugLog('subjectPage', {
            subjectId,
            isUploadPage,
            pending: readPendingRelate(),
            pendingRaw: pendingRaw.slice(0, 300),
            pendingCover: hasPendingCover(),
            lastFillTaskId: lastFill?.relateTaskId || ''
        });

        // 这个标签页是专门打开关联页的：保存后回到条目页时不要重复上传封面，
        // 封面由原来的条目标签页负责。
        let isRelateOnlyTab = false;
        try {
            isRelateOnlyTab = sessionStorage.getItem('audibleRelateTab') === '1';
        } catch (_) {}

        if (isRelateOnlyTab) {
            try {
                sessionStorage.removeItem('audibleRelateTab');
                sessionStorage.removeItem('pendingCoverUrl');
                sessionStorage.removeItem('pendingCoverCandidates');
                sessionStorage.removeItem('audiblePendingCoverTitle');
            } catch (_) {}
            debugLog('subjectPage', '关联标签页返回，跳过封面');
            return;
        }

        if (isUploadPage) {
            await autoUploadCoverOnSubjectPage();
            return;
        }

        // 先启动封面流程，让封面组件开始等标题/上传表单；
        // 同时去解析并打开关联任务的新标签页。两边并行。
        const coverTask = autoUploadCoverOnSubjectPage().catch(error => {
            console.warn(`${PREFIX} 封面流程异常。`, error);
            debugLog('coverTaskError', String(error && error.message || error));
        });

        const relateAction = await tryNavigateToRelatePage(subjectId);

        if (relateAction === 'same') {
            // 当前页马上跳到关联页；封面留到保存后回条目页再做。
            return;
        }

        await coverTask;
    }

    // =========================================================
    // 人物关联页：触发 bangumi-auto-relate-infobox 的“自动关联”并保存
    // =========================================================

    function waitForAutoRelateFinish(button, options = {}) {
        const timeout = options.timeout || 180000;
        const startedAt = Date.now();

        return new Promise(resolve => {
            let sawProgress = Boolean(options.alreadyRunning);
            let idleSince = 0;
            let lastState = '';

            const finish = (completed, value) => {
                resolve({
                    completed,
                    value: String(value || '').trim()
                });
            };

            const tick = () => {
                const value = String(button.value || '').trim();
                const state = `disabled=${button.disabled} value=${value}`;

                if (state !== lastState) {
                    lastState = state;
                    debugLog('autoRelateState', state);
                }

                if (/^进度/.test(value)) sawProgress = true;

                // 组件只在任务结束时把按钮重新启用；
                // 不要依赖按钮文字恢复为“自动关联”，因为那还依赖 #robot 元素。
                if (button.disabled) {
                    idleSince = 0;
                } else {
                    if (!idleSince) idleSince = Date.now();

                    const idleFor = Date.now() - idleSince;
                    const grace = sawProgress ? 1200 : 3200;

                    if (idleFor >= grace) {
                        finish(true, value);
                        return;
                    }
                }

                if (Date.now() - startedAt >= timeout) {
                    finish(false, value);
                    return;
                }

                setTimeout(tick, 400);
            };

            tick();
        });
    }

    function findSaveRelatedButton() {
        const exact = document.querySelector(
            'input[name="submit"][value="保存关联数据"],' +
            'input[type="submit"][name="submit"][value="保存关联数据"]'
        );
        if (exact) return exact;

        const candidates = [
            ...document.querySelectorAll(
                '#columnCrtRelatedA input[type="submit"],' +
                '#columnCrtRelatedA button[type="submit"],' +
                'form input[type="submit"],' +
                'form button[type="submit"],' +
                'form input[name="submit"],' +
                'form button[name="submit"]'
            )
        ];

        return candidates.find(element => {
            const text = cleanText(element.value || element.textContent || '');
            return /保存关联数据|保存关联|Save related/i.test(text);
        }) || null;
    }

    function neutralizePageWindowClose() {
        try {
            const pageWindow = typeof unsafeWindow !== 'undefined' ? unsafeWindow : window;

            if (!pageWindow || typeof pageWindow.close !== 'function') {
                debugLog('windowCloseGuard', 'unavailable');
                return;
            }

            pageWindow.close = function () {
                console.warn(
                    `${PREFIX} window.close() 已被拦截（当前标签页不是脚本打开的窗口）。`
                );
                debugLog('windowCloseBlocked', location.href);
            };

            debugLog('windowCloseGuard', 'installed');
        } catch (error) {
            debugLog('windowCloseGuardError', String(error && error.message || error));
        }

        // 再补一层页面上下文注入，防止 unsafeWindow 赋值被沙箱拦截。
        try {
            const script = document.createElement('script');
            script.textContent = `(function () {
                try {
                    window.close = function () {
                        console.warn('[Audible → Bangumi] window.close() intercepted');
                    };
                } catch (error) {}
            })();`;
            (document.head || document.documentElement).appendChild(script);
            script.remove();
            debugLog('windowCloseGuard', 'inline installed');
        } catch (error) {
            debugLog(
                'windowCloseGuardInlineError',
                String(error && error.message || error)
            );
        }
    }

    function scheduleSaveReturn(subjectId) {
        const startedAt = Date.now();

        const timer = setInterval(() => {
            if (!/\/add_related\/person\/?$/i.test(location.pathname)) {
                clearInterval(timer);
                return;
            }

            if (Date.now() - startedAt < 12000) return;

            clearInterval(timer);
            debugLog('saveFallback', '保存后仍停留在关联页，返回条目页');
            location.href = `${location.origin}/subject/${subjectId}`;
        }, 1000);
    }

    async function handleRelatePersonPage(subjectId) {
        let relateTaskId = getRelateTaskIdFromCurrentUrl();
        let relateTask = relateTaskId ? await getTask(relateTaskId) : null;
        let isNewRelateTab = false;

        if (relateTaskId) {
            const currentUrl = new URL(location.href);
            const currentHashParams = new URLSearchParams(
                currentUrl.hash.replace(/^#/, '')
            );
            isNewRelateTab =
                currentUrl.searchParams.get('ab_relate_new') === '1' ||
                currentHashParams.get('ab_relate_new') === '1';
        }

        // 新方式：关联页用完全裸的 URL 打开，任务按 subjectId 存在 GM 里。
        if (!relateTask) {
            let subjectTask = null;

            try {
                subjectTask = await GM_getValue(
                    relateSubjectKey(subjectId),
                    null
                );
            } catch (_) {}

            debugLog('subjectRelateTask', { subjectId, subjectTask });

            if (subjectTask && subjectTask.taskId) {
                relateTaskId = subjectTask.taskId;
                relateTask = await getTask(relateTaskId);

                if (!relateTask) {
                    relateTask = {
                        id: relateTaskId,
                        stage: 'relate',
                        subjectId: String(subjectId),
                        title: subjectTask.title || '',
                        createdAt: subjectTask.createdAt || Date.now()
                    };
                }

                isNewRelateTab = true;

                try {
                    sessionStorage.setItem(
                        'audibleRelateTask',
                        JSON.stringify(relateTask)
                    );
                } catch (_) {}

                try {
                    await GM_setValue(relateSubjectKey(subjectId), null);
                } catch (_) {}
            }
        }

        // 关联页在保存前刷新时，从当前标签页的 sessionStorage 恢复。
        if (!relateTask) {
            try {
                const stored = JSON.parse(
                    sessionStorage.getItem('audibleRelateTask') || 'null'
                );

                if (
                    stored &&
                    stored.id &&
                    String(stored.subjectId) === String(subjectId)
                ) {
                    relateTaskId = stored.id;
                    relateTask = stored;
                    isNewRelateTab = true;
                }
            } catch (_) {}
        }

        if (relateTask) isNewRelateTab = true;

        let sameTab = false;
        try {
            sameTab = sessionStorage.getItem('audibleRelateSameTab') === '1';
            sessionStorage.removeItem('audibleRelateSameTab');
        } catch (_) {}

        if (isNewRelateTab && !sameTab) {
            try {
                sessionStorage.setItem('audibleRelateTab', '1');
            } catch (_) {}
        }

        const savePending = readRelateSavePending();

        debugLog('relatePage', {
            subjectId,
            href: location.href,
            isNewRelateTab,
            sameTab,
            relateTaskId,
            relateTask,
            pending: readPendingRelate(),
            savePending
        });

        // 保存后如果服务器仍返回关联页，自动回条目页。
        // 不再要求当前标签页有封面任务：关联页在独立标签页里运行。
        if (
            savePending &&
            String(savePending.subjectId) === String(subjectId)
        ) {
            clearRelateSavePending();
            console.info(
                `${PREFIX} 关联数据已提交，返回条目页。`,
                { subjectId }
            );
            debugLog('saveReturn', '服务器返回关联页，回条目页');
            location.href = `${location.origin}/subject/${subjectId}`;
            return;
        }

        let pending = null;

        if (relateTaskId) {
            if (
                !relateTask ||
                relateTask.stage !== 'relate' ||
                String(relateTask.subjectId) !== String(subjectId)
            ) {
                debugLog('relatePage', 'ab_relate 任务无效', relateTask);
                await clearRelateTask(relateTaskId);
                alert(
                    `${PREFIX}\n自动关联任务已失效，请回到条目页重新操作。`
                );
                return;
            }

            if (
                !relateTask.createdAt ||
                Date.now() - relateTask.createdAt > RELATE_PENDING_MAX_AGE
            ) {
                debugLog('relatePage', 'ab_relate 任务已过期');
                await clearRelateTask(relateTaskId);
                alert(
                    `${PREFIX}\n自动关联任务已过期，请回到条目页重新操作。`
                );
                return;
            }

            pending = {
                title: relateTask.title || '',
                subjectId: String(subjectId),
                createdAt: relateTask.createdAt,
                taskId: relateTaskId
            };
        } else {
            pending = readPendingRelate();
        }

        if (!pending) return;

        if (String(pending.subjectId || '') !== String(subjectId)) {
            debugLog('relatePage', 'subjectId 不匹配，跳过');
            return;
        }

        if (!(await getAutoRelate())) {
            debugLog('relatePage', '自动关联开关已关闭');
            clearPendingRelate();
            await clearRelateTask(relateTaskId);
            return;
        }

        // 这个页面在 Bangumi 里可能是弹窗/Thickbox；
        // 顶层标签页直接保存时，页面或别的脚本可能调用 window.close() 而报错。
        // 先拦掉，避免它中断保存后的回调。
        neutralizePageWindowClose();

        console.info(`${PREFIX} 已进入人物关联页，等待“自动关联”组件...`, {
            subjectId
        });

        const relateButton = await waitForElement('#autoRelate', 30000);

        if (!relateButton) {
            debugLog('autoRelateMissing', {
                column: Boolean(document.querySelector('#columnCrtRelatedA')),
                board: Boolean(document.querySelector('#columnCrtRelatedA .board')),
                infobox: Boolean(document.querySelector('#infobox')),
                title: document.title,
                bodySnippet: cleanText(document.body.innerText || '').slice(0, 180)
            });
            clearPendingRelate();
            await clearRelateTask(relateTaskId);
            alert(
                `${PREFIX}\n未找到“自动关联”按钮，已停止自动关联。\n` +
                `请确认已安装并启用 bangumi-auto-relate-infobox 脚本，` +
                `然后在人物关联页手动关联并保存。`
            );
            return;
        }

        // 给页面原生脚本和自动关联组件留出初始化时间。
        await delay(800);

        const relateForm = document.querySelector('#crtRelateSubjects');

        if (!relateForm) {
            debugLog('relateFormMissing', {
                column: Boolean(document.querySelector('#columnCrtRelatedA')),
                board: Boolean(document.querySelector('#columnCrtRelatedA .board')),
                autoRelate: Boolean(document.querySelector('#autoRelate'))
            });
            clearPendingRelate();
            await clearRelateTask(relateTaskId);
            alert(`${PREFIX}\n人物关联表单未加载，已停止自动关联。`);
            return;
        }

        const beforeRelateCount =
            document.querySelectorAll('#crtRelateSubjects .clearit').length;

        debugLog('autoRelateButton', {
            value: String(relateButton.value || ''),
            disabled: relateButton.disabled,
            clearit: beforeRelateCount,
            infoboxLi: document.querySelectorAll('#infobox li').length,
            noExist: document.querySelectorAll('#infobox .noExist').length,
            hasRobot: Boolean(document.querySelector('#robot'))
        });

        const initialValue = String(relateButton.value || '').trim();
        let alreadyRunning = false;
        let completedValue = '';

        if (/^重复/.test(initialValue)) {
            completedValue = initialValue;
        } else if (relateButton.disabled || /^进度/.test(initialValue)) {
            alreadyRunning = true;
            console.info(`${PREFIX} “自动关联”已在运行，等待其完成...`);
        } else {
            relateButton.click();
            console.info(`${PREFIX} 已点击“自动关联”，等待其完成...`);
        }

        debugLog('autoRelateStart', { initialValue, alreadyRunning, clicked: !completedValue });

        if (!completedValue) {
            const result = await waitForAutoRelateFinish(relateButton, {
                timeout: 180000,
                alreadyRunning
            });

            if (!result.completed) {
                clearPendingRelate();
                await clearRelateTask(relateTaskId);
                alert(
                    `${PREFIX}\n“自动关联”未在预期时间内完成，已停止自动保存。\n` +
                    `请在页面上检查关联进度，确认无误后手动保存。`
                );
                return;
            }

            completedValue = result.value;

            console.info(`${PREFIX} “自动关联”已完成。`, {
                value: completedValue
            });
        }

        const afterRelateCount =
            document.querySelectorAll('#crtRelateSubjects .clearit').length;
        const noExistNames = [...document.querySelectorAll('#infobox .noExist')]
            .map(element => cleanText(element.textContent))
            .filter(Boolean);

        debugLog('autoRelateResult', {
            value: completedValue,
            beforeRelateCount,
            afterRelateCount,
            added: afterRelateCount - beforeRelateCount,
            noExistNames
        });

        if (noExistNames.length) {
            console.warn(`${PREFIX} 未匹配到的人物：`, noExistNames);
        }

        if (/^重复/.test(completedValue)) {
            clearPendingRelate();
            await clearRelateTask(relateTaskId);
            alert(
                `${PREFIX}\n自动关联已完成，但检测到重复人物：${completedValue}。\n` +
                `请先在页面上处理重复项，再手动点击“保存关联数据”。`
            );
            return;
        }

        const saveButton = findSaveRelatedButton();

        debugLog('saveButton', {
            found: Boolean(saveButton),
            value: saveButton?.value || saveButton?.textContent || '',
            formAction: saveButton?.form?.action || '',
            formMethod: saveButton?.form?.method || '',
            outerHTML: (saveButton?.outerHTML || '').slice(0, 300)
        });

        if (!saveButton) {
            debugLog('saveButtonCandidates',
                [...document.querySelectorAll(
                    'input[type="submit"],button[type="submit"],input[name="submit"]'
                )].slice(0, 12).map(element => ({
                    tag: element.tagName,
                    type: element.type,
                    name: element.name,
                    value: element.value || cleanText(element.textContent || '')
                }))
            );
            clearPendingRelate();
            await clearRelateTask(relateTaskId);
            alert(`${PREFIX}\n未找到“保存关联数据”按钮，请手动保存。`);
            return;
        }

        if (saveButton.disabled) {
            clearPendingRelate();
            await clearRelateTask(relateTaskId);
            alert(`${PREFIX}\n“保存关联数据”按钮不可用，请手动检查。`);
            return;
        }

        console.info(`${PREFIX} 正在保存人物关联数据...`, {
            subjectId,
            autoRelateValue: completedValue
        });

        await clearRelateTask(relateTaskId);
        setRelateSavePending(subjectId);
        clearPendingRelate();
        debugLog('saveClick', { subjectId, href: location.href, relateTaskId });
        saveButton.click();
        scheduleSaveReturn(subjectId);
    }

    function addDetailButton() {
        if (document.querySelector('#audible-bangumi-button')) return;

        const button = document.createElement('button');
        button.id = 'audible-bangumi-button';
        button.textContent = '添加到 Bangumi';

        Object.assign(button.style, {
            position: 'fixed',
            right: '24px',
            bottom: '24px',
            zIndex: '999999',
            padding: '12px 18px',
            border: 'none',
            borderRadius: '8px',
            background: '#f09199',
            color: '#fff',
            fontSize: '14px',
            fontWeight: 'bold',
            cursor: 'pointer',
            boxShadow: '0 2px 12px rgba(0,0,0,.25)'
        });

        button.addEventListener('click', async () => {
            if (button.disabled) return;

            button.disabled = true;
            button.textContent = '正在提取…';

            try {
                const data = extractAudibleData();

                if (!data.title) {
                    throw new Error('没有提取到书名。');
                }

                // 商品详情页的手动入口也使用独立任务 ID。
                const taskId = createTaskId();

                await setTask(taskId, {
                    id: taskId,
                    stage: 'fill',
                    data,
                    createdAt: Date.now()
                });

                const autoSubmit = await getAutoSubmit();

                GM_openInTab(bangumiUrlForTask(taskId), {
                    active: !autoSubmit,
                    insert: true,
                    setParent: false
                });

                button.textContent = '已发送';
            } catch (error) {
                button.disabled = false;
                button.textContent = '添加到 Bangumi';
                alert(`${PREFIX}\n${error.message || error}`);
            }
        });

        document.body.appendChild(button);
    }

    // =========================================================
    // 入口
    // =========================================================

    if (isBangumi) {
        debugLog('page', { href: location.href, readyState: document.readyState });

        const relatePageMatch = location.pathname.match(
            /^\/subject\/(\d+)\/add_related\/person\/?$/i
        );

        if (relatePageMatch) {
            await handleRelatePersonPage(relatePageMatch[1]);
            return;
        }

        const subjectPageMatch = location.pathname.match(
            /^\/subject\/(\d+)(\/upload_img)?\/?$/i
        );

        if (subjectPageMatch) {
            await handleSubjectPage(
                subjectPageMatch[1],
                Boolean(subjectPageMatch[2])
            );
            return;
        }

        await handleBangumiPage();
        return;
    }

    if (!isAudible) return;

    if (/\/pd\//i.test(location.pathname)) {
        addDetailButton();
        await handleProductTask();
        return;
    }

    if (isListingPage()) {
        startListingObserver();
    }
})();
