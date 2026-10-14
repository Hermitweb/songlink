// SongLink 点歌台 — 重构版 WebSocket 客户端
// 亮色主题 / 双列卡片 / 自绘 SVG 图标 / 无 emoji
(function () {
  'use strict';

  // ---------- 状态 ----------
  let ws = null;
  let seq = 0;
  let cur = { title: '', artist: '', album: '', dur: 0, pos: 0, status: '', sourceMode: '', artRef: '', volume: 1.0 };
  let lines = [];
  let lineIdx = -1;
  let queue = [];
  let queueIdx = -1;
  let queueShuffled = false;
  let history = [];
  let pinCode = (new URLSearchParams(location.search).get('pin') || '').trim();
  let userName = localStorage.getItem('songlink.user') || '';
  let reconnectTimer = null;
  let reconnectDelay = 0;
  let volumeMuted = false;
  let preMuteVolume = 1.0;

  // 主题 / 模式
  const THEME_KEY = 'songlink.theme';
  const ELDER_KEY = 'songlink.elderMode';
  const ONBOARD_KEY = 'songlink.onboarded';
  let theme = localStorage.getItem(THEME_KEY) || 'light';
  document.body.setAttribute('data-theme', theme);
  let elderMode = localStorage.getItem(ELDER_KEY) === 'on';

  // ---------- 工具 ----------
  const $ = id => document.getElementById(id);
  const fmtTime = s => { const m = Math.floor(s / 60), sec = Math.floor(s % 60); return `${m}:${sec.toString().padStart(2, '0')}`; };
  const nextSeq = () => ++seq;
  const uuid = () => 'web-' + Math.random().toString(36).slice(2, 10);
  const esc = s => (s || '').replace(/[<>&"']/g, c => ({ '<': '<', '>': '>', '&': '&', '"': '"', "'": '&#39;' }[c]));
  const who = () => (userName || '').trim() || '匿名';

  // SVG icon helper — inline `<svg><use href="#i-foo"/></svg>`
  const icon = (id, cls) => `<svg class="ico ${cls || ''}"><use href="#i-${id}"/></svg>`;

  // Toast
  function toast(msg, kind) {
    const el = $('toast');
    el.textContent = msg;
    el.className = 'toast' + (kind ? ' ' + kind : '');
    el.hidden = false;
    clearTimeout(toast._t);
    toast._t = setTimeout(() => { el.hidden = true; }, 2500);
  }

  function setConn(state) {
    const el = $('conn');
    el.className = 'conn-pill ' + state;
    $('conn-text').textContent = state === 'on' ? '已连接' : (state === 'connecting' ? '连接中' : '未连接');
  }

  // ---------- 渲染 ----------
  function renderNow() {
    $('title').textContent = cur.title || '—';
    $('artist').textContent = cur.artist || '—';
    $('album').textContent = cur.album || '';

    const status = cur.status || '';
    const statePill = $('status-pill');
    statePill.textContent = status === 'playing' ? '播放中' : (status === 'paused' ? '已暂停' : (status === 'stopped' ? '已停止' : '空闲'));
    statePill.className = 'state-pill' + (status ? ' ' + status : '');

    $('time').textContent = `${fmtTime(cur.pos)} / ${fmtTime(cur.dur)}`;
    const pct = (cur.dur > 0) ? Math.min(100, (cur.pos / cur.dur) * 100) : 0;
    $('progress-fill').style.width = pct + '%';

    // 播放/暂停图标
    $('play-icon').innerHTML = `<use href="#i-${status === 'playing' ? 'pause' : 'play'}"/>`;

    // 封面图
    const wrap = $('art-wrap');
    if (cur.artRef) {
      wrap.innerHTML = `<img alt="" src="/art/${encodeURIComponent(cur.artRef)}" onerror="this.remove()">`;
    } else {
      wrap.innerHTML = icon('headset', 'ico-xl ico-mute');
    }

    // 音量
    if (typeof cur.volume === 'number') {
      const vpct = Math.round(cur.volume * 100);
      const vol = $('vol'); if (vol && document.activeElement !== vol) vol.value = vpct;
      $('vol-num').textContent = vpct;
      const vi = $('vol-icon');
      vi.innerHTML = cur.volume > 0 ? '<use href="#i-vhigh"/>' : '<use href="#i-vmute"/>';
    }
  }

  function renderLyrics() {
    if (!lines.length) { $('lyrics').innerHTML = '<div class="empty">暂无歌词</div>'; return; }
    $('lyrics').innerHTML = lines.map((l, i) =>
      `<div class="line${i === lineIdx ? ' active' : (lineIdx > i ? ' passed' : '')}" data-i="${i}">${esc(l.x)}</div>`).join('');
    const active = $('lyrics').querySelector('.line.active');
    if (active) active.scrollIntoView({ block: 'center', behavior: 'smooth' });
  }

  function renderQueue() {
    $('qcount').textContent = queue.length;
    if (!queue.length) {
      $('queue-list').innerHTML = '';
      $('queue-empty').style.display = '';
      return;
    }
    $('queue-empty').style.display = 'none';
    $('queue-list').innerHTML = queue.map((it, i) => {
      const cls = i === queueIdx ? ' current' : (queueIdx > i ? ' played' : '');
      const canUp = i > 0;
      const canDown = i < queue.length - 1;
      const ops = [
        canUp ? `<button class="iconbtn sm" data-act="up" data-i="${i}" title="上移">${icon('up', 'ico-sm')}</button>` : '',
        canDown ? `<button class="iconbtn sm" data-act="down" data-i="${i}" title="下移">${icon('down', 'ico-sm')}</button>` : '',
        i > 0 ? `<button class="iconbtn sm" data-act="top" data-i="${i}" title="置顶">${icon('pin', 'ico-sm')}</button>` : '',
        `<button class="iconbtn sm danger" data-act="remove" data-i="${i}" title="移除">${icon('close', 'ico-sm')}</button>`
      ].join('');
      return `<li class="${cls.trim()}" data-i="${i}"${queue.length > 1 ? ' draggable="true"' : ''}>
        <span class="handle" title="拖动排序">${icon('handle', 'ico-sm')}</span>
        <span class="idx">${i + 1}.</span>
        <div class="info">
          <span class="t">${esc(it.title || '(无标题)')}</span>
          <span class="a">${esc(it.artist || '')}</span>
          <span class="by">点歌人:${esc(it.by || '匿名')}</span>
        </div>
        <div class="ops">${ops}</div>
      </li>`;
    }).join('');
  }

  function renderTrending(results) {
    if (!results.length) { $('trending').innerHTML = '<div class="empty">无数据</div>'; return; }
    $('trending').innerHTML = results.map(r =>
      `<div class="chip" data-t="${esc(r.title)}" data-a="${esc(r.artist || '')}">
         ${esc(r.title)}<small>${esc(r.artist || '')}</small>
       </div>`).join('');
  }

  function renderResults(results) {
    if (!results.length) { $('results').innerHTML = '<div class="empty">无结果</div>'; return; }
    $('results').innerHTML = results.map(r =>
      `<div class="hit" data-t="${esc(r.title || '')}" data-a="${esc(r.artist || '')}">
         <div class="info">
           <span class="t">${esc(r.title)}</span><span class="a">${esc(r.artist || '')}</span>
         </div>
         <button class="add" data-act="add" title="加入队列">${icon('plus', 'ico-sm')}</button>
         <button class="add" data-act="pick" title="立即播放">${icon('play', 'ico-sm')}</button>
       </div>`).join('');
  }

  function renderHistory() {
    if (!history.length) { $('history-list').innerHTML = ''; $('history-empty').style.display = ''; return; }
    $('history-empty').style.display = 'none';
    $('history-list').innerHTML = history.map(h => {
      const when = h.playedAt ? new Date(h.playedAt).toLocaleString('zh-CN', { month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit' }) : '';
      return `<li>
        <div class="info">
          <span class="t">${esc(h.title || '(无标题)')}</span>
          <span class="a">${esc(h.artist || '')}</span>
          <span class="by">${when}</span>
        </div>
        <button class="add" data-act="play" data-t="${esc(h.title)}" data-a="${esc(h.artist || '')}" title="再听一次">${icon('play', 'ico-sm')}</button>
      </li>`;
    }).join('');
  }

  // 1s 心跳(进度条推进 + 歌词滚动)
  function tick() {
    if (cur.status === 'playing' && cur.dur > 0) {
      cur.pos = Math.min(cur.pos + 1, cur.dur);
      $('progress-fill').style.width = ((cur.pos / cur.dur) * 100) + '%';
      $('time').textContent = `${fmtTime(cur.pos)} / ${fmtTime(cur.dur)}`;
      const ni = findLineIdx(cur.pos * 1000);
      if (ni !== lineIdx) {
        const all = $('lyrics').querySelectorAll('.line');
        if (all[lineIdx]) { all[lineIdx].classList.remove('active'); all[lineIdx].classList.add('passed'); }
        if (all[ni]) { all[ni].classList.remove('passed'); all[ni].classList.add('active'); all[ni].scrollIntoView({ block: 'center', behavior: 'smooth' }); }
        lineIdx = ni;
      }
    }
    setTimeout(tick, 1000);
  }

  function findLineIdx(ms) {
    let lo = 0, hi = lines.length - 1, ans = -1;
    while (lo <= hi) {
      const mid = (lo + hi) >> 1;
      if (lines[mid].t <= ms) { ans = mid; lo = mid + 1; } else { hi = mid - 1; }
    }
    return ans;
  }

  function send(frame) {
    if (!ws || ws.readyState !== WebSocket.OPEN) return;
    ws.send(JSON.stringify(Object.assign({ v: 1, seq: nextSeq() }, frame)));
  }

  // ---------- 连接 ----------
  function connect(pin) {
    pinCode = (pin || '').trim();
    if (!pinCode) { toast('请输入 PIN', 'error'); return; }
    if (reconnectTimer) { clearTimeout(reconnectTimer); reconnectTimer = null; }
    setConn('connecting');
    const proto = location.protocol === 'https:' ? 'wss' : 'ws';
    ws = new WebSocket(`${proto}://${location.host}/`);
    ws.onopen = () => {
      reconnectDelay = 0;
      send({ type: 'hello', id: uuid(), role: 'remote', device: 'web-panel', os: navigator.platform, caps: ['cmd', 'state', 'lyric', 'queue'], pin: pinCode });
    };
    ws.onmessage = ev => { try { handle(JSON.parse(ev.data)); } catch (e) { console.warn('bad frame', e); } };
    ws.onclose = () => { setConn('off'); scheduleReconnect(); };
    ws.onerror = () => { try { ws && ws.close(); } catch { /* 忽略 */ } };
  }

  function scheduleReconnect() {
    if (reconnectTimer || !pinCode) return;
    reconnectDelay = reconnectDelay === 0 ? 3 : Math.min(reconnectDelay * 1.6 + 1, 15);
    reconnectTimer = setTimeout(() => {
      reconnectTimer = null;
      setConn('connecting');
      connect(pinCode);
    }, reconnectDelay * 1000);
  }

  function handle(f) {
    switch (f.type) {
      case 'authResult':
        if (f.ok) {
          setConn('on');
          $('pin-wrap').hidden = true;
          send({ type: 'cmd', id: uuid(), action: 'trending', args: { limit: 24 } });
          send({ type: 'cmd', id: uuid(), action: 'history.list', args: { limit: 12 } });
          if (cur.title && cur.artist) requestLyric();
          showWizardIfNeeded();
        } else {
          setConn('off');
          toast('鉴权失败: ' + (f.code || 'AUTH_FAILED'), 'error');
        }
        break;
      case 'state':
        if (f.now) {
          cur = Object.assign(cur, f.now);
          renderNow();
          if (cur.title && cur.artist) requestLyric();
        }
        break;
      case 'lyric':
        if (f.lines && f.lines.length) { lines = f.lines; lineIdx = -1; renderLyrics(); }
        else { lines = []; renderLyrics(); }
        break;
      case 'searchResults':
        if (f.query === 'trending') { renderTrending(f.results || []); break; }
        renderResults(f.results || []);
        break;
      case 'event':
        if (f.name === 'queue-updated') {
          queue = (f.queue || []).slice();
          queueIdx = (queue.length > 0 && cur.title) ? queue.findIndex(x => x.title === cur.title && x.artist === cur.artist) : -1;
          renderQueue();
        }
        if (f.name === 'source-mode-changed') cur.sourceMode = f.mode || '';
        if (f.name === 'queue-shuffled') queueShuffled = true;
        if (f.name === 'queue-unshuffled') queueShuffled = false;
        if (f.name === 'volume-changed') {
          if (typeof f.volume === 'number') cur.volume = f.volume;
          renderNow();
        }
        if (f.name === 'history-list') {
          history = (f.queue || []).slice();
          renderHistory();
        }
        if (f.name === 'import-result') {
          const r = (f.queue && f.queue[0]) || {};
          showImportResult(r);
        }
        renderNow();
        break;
      case 'error':
        if (f.code === 'DUPLICATE') toast(f.hint || '已在队列中', 'error');
        else if (f.code === 'PICK_UNSUPPORTED' || f.code === 'APP_NOT_FOUND') toast('点歌失败: ' + (f.hint || f.code), 'error');
        else if (f.code === 'PATH_INVALID' || f.code === 'SCAN_FAILED') toast(f.hint || f.code, 'error');
        else console.warn('server error', f);
        break;
    }
  }

  function requestLyric() {
    send({ type: 'cmd', id: uuid(), action: 'getLyric', args: { title: cur.title, artist: cur.artist } });
  }

  // ---------- 主题切换 ----------
  function toggleTheme() {
    theme = theme === 'light' ? 'dark' : 'light';
    localStorage.setItem(THEME_KEY, theme);
    document.body.setAttribute('data-theme', theme);
    $('btn-theme').innerHTML = icon(theme === 'light' ? 'moon' : 'sun', 'ico-md');
  }

  // ---------- 长辈模式 ----------
  function applyElderMode() {
    document.body.classList.toggle('elder-mode', elderMode);
    $('btn-mode').querySelector('span').textContent = elderMode ? '标准模式' : '长辈模式';
  }
  function toggleElderMode() {
    elderMode = !elderMode;
    localStorage.setItem(ELDER_KEY, elderMode ? 'on' : 'off');
    applyElderMode();
    toast(elderMode ? '已切换长辈模式' : '已切换标准模式', 'ok');
  }

  // ---------- 导入音乐 ----------
  function openImportModal() { $('import-modal').classList.remove('hidden'); $('import-path').focus(); }
  function closeImportModal() { $('import-modal').classList.add('hidden'); $('import-result').hidden = true; }

  async function pickFolder() {
    if (window.showDirectoryPicker) {
      try {
        const dir = await window.showDirectoryPicker();
        $('import-path').value = dir.name;
        toast('已选文件夹 — 请补全完整路径(如 D:\\' + dir.name + ')', 'ok');
      } catch { /* 用户取消 */ }
    } else {
      $('import-path').focus();
      toast('请直接输入文件夹路径', 'ok');
    }
  }

  function doImport() {
    const path = $('import-path').value.trim();
    if (!path) { toast('请输入文件夹路径', 'error'); return; }
    $('import-result').hidden = false;
    $('import-result').innerHTML = '正在扫描文件夹…';
    send({ type: 'cmd', id: uuid(), action: 'import.folder', args: { path } });
  }

  function showImportResult(r) {
    const el = $('import-result');
    const cls = r.failed > 0 ? 'ok' : 'ok';
    el.innerHTML = `<span class="${cls}">导入完成</span> — 扫到 ${r.total} 个,成功 ${r.imported} 首,失败 ${r.failed}`;
    toast('导入完成 — ' + r.imported + ' 首', 'ok');
  }

  // ---------- QR 弹窗(轻量 QR 生成,纯 JS)----------
  // 极简实现:用纯文本提示 + 在线 URL(浏览器 / WebView2 都支持扫码);这里先简单实现
  // 生成一个占位 QR(用纯文本),实际生成需要 qrcode 库 — 暂时只显示 URL + PIN。
  function openQrModal() {
    const url = location.origin + '/?pin=' + pinCode;
    $('qr-url').textContent = url;
    $('qr-pin').textContent = pinCode || '-';
    $('qr-img').innerHTML = '<div class="muted">用手机浏览器打开上面 URL 即可</div>';
    $('qr-modal').classList.remove('hidden');
  }
  function closeQrModal() { $('qr-modal').classList.add('hidden'); }

  // ---------- Wizard ----------
  function showWizardIfNeeded() {
    if (localStorage.getItem(ONBOARD_KEY) === 'yes') return;
    $('wizard').classList.remove('hidden');
    wizardShowStep(1);
  }
  function wizardShowStep(n) {
    document.querySelectorAll('.wizard-step').forEach(s => s.classList.toggle('hidden', s.dataset.step !== String(n)));
    $('wiz-progress').textContent = `第 ${n} 步 / 共 4 步`;
  }
  function setupWizard() {
    // 等连上再展示
    const waitConn = setInterval(() => {
      if (ws && ws.readyState === WebSocket.OPEN) {
        clearInterval(waitConn);
        showWizardIfNeeded();
      }
    }, 300);
    setTimeout(() => clearInterval(waitConn), 30000);

    $('wiz-1-next').onclick = () => wizardShowStep(2);
    $('wiz-2-back').onclick = () => wizardShowStep(1);
    $('wiz-3-back').onclick = () => wizardShowStep(2);
    $('wiz-4-finish').onclick = () => { $('wizard').classList.add('hidden'); localStorage.setItem(ONBOARD_KEY, 'yes'); };

    document.querySelectorAll('#wizard .opt-card').forEach(btn => {
      btn.onclick = () => {
        const src = btn.dataset.source;
        if (src === 'auto') { toast('已设置:自动接管电脑正在播放的', 'ok'); wizardShowStep(3); }
        else if (src === 'local') { window._wizWantsImport = true; toast('已选本地音乐', 'ok'); wizardShowStep(3); }
      };
    });

    const wizVol = $('wiz-vol');
    const wizVolNum = $('wiz-vol-num');
    function sendWizVol() {
      const pct = parseInt(wizVol.value, 10);
      wizVolNum.textContent = pct;
      send({ type: 'cmd', id: uuid(), action: 'volume', args: { level: pct / 100 } });
      $('vol').value = pct; $('vol-num').textContent = pct;
      localStorage.setItem('songlink.volume', String(pct));
    }
    wizVol.oninput = sendWizVol;
    wizVol.onchange = sendWizVol;
    const sv = parseInt(localStorage.getItem('songlink.volume') || '60', 10);
    wizVol.value = sv; wizVolNum.textContent = sv;

    $('wiz-3-next').onclick = () => { sendWizVol(); wizardShowStep(4); if (window._wizWantsImport) setTimeout(openImportModal, 400); };
  }

  // ---------- 事件绑定 ----------
  $('btn-connect').onclick = () => connect($('pin').value);
  $('btn-prev').onclick = () => send({ type: 'cmd', id: uuid(), action: 'prev' });
  $('btn-play').onclick = () => send({ type: 'cmd', id: uuid(), action: cur.status === 'playing' ? 'pause' : 'play' });
  $('btn-next').onclick = () => send({ type: 'cmd', id: uuid(), action: 'next' });
  $('btn-shuffle').onclick = () => send({ type: 'cmd', id: uuid(), action: queueShuffled ? 'queue.unshuffle' : 'queue.shuffle' });
  $('btn-qclear').onclick = () => { if (confirm('清空队列?')) send({ type: 'cmd', id: uuid(), action: 'queue.clear' }); };
  $('btn-hreload').onclick = () => send({ type: 'cmd', id: uuid(), action: 'history.list', args: { limit: 12 } });
  $('btn-search').onclick = () => {
    const kw = $('kw').value.trim(); if (!kw) return;
    send({ type: 'cmd', id: uuid(), action: 'search', args: { kw } });
  };
  $('kw').addEventListener('keydown', e => { if (e.key === 'Enter') $('btn-search').click(); });
  $('user').addEventListener('change', e => { userName = e.target.value.trim(); localStorage.setItem('songlink.user', userName); });
  $('btn-theme').onclick = toggleTheme;
  $('btn-mode').onclick = toggleElderMode;
  $('btn-import').onclick = openImportModal;
  $('btn-import-cancel').onclick = closeImportModal;
  $('btn-import-pick').onclick = pickFolder;
  $('btn-import-go').onclick = doImport;
  $('btn-qr').onclick = openQrModal;
  $('btn-qr-close').onclick = closeQrModal;

  // 音量条 / 静音按钮
  function sendVolume(level) { send({ type: 'cmd', id: uuid(), action: 'volume', args: { level } }); }
  $('vol').addEventListener('input', e => {
    const pct = parseInt(e.target.value, 10);
    const level = Math.max(0, Math.min(1, pct / 100));
    volumeMuted = (level === 0);
    $('vol-num').textContent = pct;
    $('vol-icon').innerHTML = `<use href="#i-${level > 0 ? 'vhigh' : 'vmute'}"/>`;
    if (level > 0) preMuteVolume = level;
    localStorage.setItem('songlink.volume', String(pct));
    if (sendVolume._t) clearTimeout(sendVolume._t);
    sendVolume._t = setTimeout(() => sendVolume(level), 80);
  });
  $('btn-mute').onclick = () => {
    if (volumeMuted || cur.volume === 0) {
      const restore = preMuteVolume > 0 ? preMuteVolume : 1.0;
      volumeMuted = false;
      const pct = Math.round(restore * 100);
      $('vol').value = pct; $('vol-num').textContent = pct;
      $('vol-icon').innerHTML = '<use href="#i-vhigh"/>';
      localStorage.setItem('songlink.volume', String(pct));
      sendVolume(restore);
    } else {
      preMuteVolume = cur.volume || 1.0;
      volumeMuted = true;
      $('vol').value = 0; $('vol-num').textContent = 0;
      $('vol-icon').innerHTML = '<use href="#i-vmute"/>';
      localStorage.setItem('songlink.volume', '0');
      sendVolume(0);
    }
  };

  // 进度条点击 seek
  $('progress-track').addEventListener('click', e => {
    if (!cur.dur) return;
    const rect = e.currentTarget.getBoundingClientRect();
    const pct = (e.clientX - rect.left) / rect.width;
    const seekTo = Math.max(0, Math.min(1, pct)) * cur.dur;
    // 没有 seek cmd,先给个 toast 提示
    toast('暂不支持跳转进度', 'error');
  });

  // 队列操作委托
  $('queue-list').addEventListener('click', e => {
    const btn = e.target.closest('button'); if (!btn) return;
    const i = parseInt(btn.dataset.i, 10);
    const act = btn.dataset.act;
    if (act === 'up') send({ type: 'cmd', id: uuid(), action: 'queue.move', args: { from: i, to: i - 1 } });
    else if (act === 'down') send({ type: 'cmd', id: uuid(), action: 'queue.move', args: { from: i, to: i + 1 } });
    else if (act === 'top') send({ type: 'cmd', id: uuid(), action: 'queue.top', args: { index: i } });
    else if (act === 'remove') send({ type: 'cmd', id: uuid(), action: 'queue.remove', args: { index: i } });
  });

  // 队列拖拽排序
  let dragSrcIdx = -1;
  $('queue-list').addEventListener('dragstart', e => {
    const li = e.target.closest('li'); if (!li) return;
    dragSrcIdx = parseInt(li.dataset.i, 10);
    li.classList.add('dragging');
    e.dataTransfer.effectAllowed = 'move';
    e.dataTransfer.setData('text/plain', String(dragSrcIdx));
  });
  $('queue-list').addEventListener('dragend', e => {
    const li = e.target.closest('li'); if (li) li.classList.remove('dragging');
    document.querySelectorAll('.queue-body li.drag-over').forEach(x => x.classList.remove('drag-over'));
    dragSrcIdx = -1;
  });
  $('queue-list').addEventListener('dragover', e => {
    e.preventDefault();
    const li = e.target.closest('li'); if (!li) return;
    document.querySelectorAll('.queue-body li.drag-over').forEach(x => x.classList.remove('drag-over'));
    li.classList.add('drag-over');
  });
  $('queue-list').addEventListener('drop', e => {
    e.preventDefault();
    const li = e.target.closest('li'); if (!li) return;
    li.classList.remove('drag-over');
    const to = parseInt(li.dataset.i, 10);
    if (dragSrcIdx < 0 || dragSrcIdx === to) return;
    send({ type: 'cmd', id: uuid(), action: 'queue.move', args: { from: dragSrcIdx, to } });
  });

  // 搜索结果点击
  $('results').addEventListener('click', e => {
    const btn = e.target.closest('button.add'); if (!btn) return;
    const hit = e.target.closest('.hit');
    if (!hit) return;
    const t = hit.dataset.t, a = hit.dataset.a;
    if (btn.dataset.act === 'add') send({ type: 'cmd', id: uuid(), action: 'queue.add', args: { title: t, artist: a, by: who() } });
    else if (btn.dataset.act === 'pick') send({ type: 'cmd', id: uuid(), action: 'pick', args: { title: t, artist: a } });
  });

  // 历史再听
  $('history-list').addEventListener('click', e => {
    const btn = e.target.closest('button.add'); if (!btn) return;
    send({ type: 'cmd', id: uuid(), action: 'pick', args: { title: btn.dataset.t, artist: btn.dataset.a } });
  });

  // 热门点歌
  $('trending').addEventListener('click', e => {
    const chip = e.target.closest('.chip'); if (!chip) return;
    send({ type: 'cmd', id: uuid(), action: 'queue.add', args: { title: chip.dataset.t, artist: chip.dataset.a, by: who() } });
    chip.style.background = 'var(--accent)'; chip.style.color = 'var(--accent-text)';
    setTimeout(() => { chip.style.background = ''; chip.style.color = ''; }, 300);
  });

  // ---------- 启动 ----------
  applyElderMode();
  if (userName) { $('user').value = userName; $('user-wrap').hidden = false; }
  $('btn-theme').innerHTML = icon(theme === 'light' ? 'moon' : 'sun', 'ico-md');
  const savedVol = parseInt(localStorage.getItem('songlink.volume') || '100', 10);
  if (!Number.isNaN(savedVol) && savedVol >= 0 && savedVol <= 100) {
    $('vol').value = savedVol; $('vol-num').textContent = savedVol;
    $('vol-icon').innerHTML = `<use href="#i-${savedVol > 0 ? 'vhigh' : 'vmute'}"/>`;
    preMuteVolume = savedVol / 100; cur.volume = preMuteVolume;
    if (savedVol === 0) volumeMuted = true;
  }
  if (pinCode) connect(pinCode);
  else $('pin-wrap').hidden = false;
  setConn('off');
  renderNow(); renderQueue(); renderLyrics(); renderHistory();
  setupWizard();
  tick();
})();
