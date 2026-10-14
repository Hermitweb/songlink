// SongLink 点歌台 — vanilla JS WebSocket 客户端
(function () {
  'use strict';

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
  let searchRef = 0;
  let trendingRef = 0;
  let reconnectTimer = null;
  let reconnectDelay = 0;     // 退避秒(0/3/5/8,最大 15)
  let volumeMuted = false;
  let preMuteVolume = 1.0;
  // 长辈模式 / 首次引导 / 导入
  const ELDER_KEY = 'songlink.elderMode';
  const ONBOARD_KEY = 'songlink.onboarded';
  let elderMode = localStorage.getItem(ELDER_KEY);
  if (elderMode === null) { elderMode = 'on'; localStorage.setItem(ELDER_KEY, 'on'); }
  else elderMode = elderMode === 'on' ? 'on' : 'off';

  const $ = id => document.getElementById(id);
  const fmtTime = s => { const m = Math.floor(s / 60), sec = Math.floor(s % 60); return `${m}:${sec.toString().padStart(2, '0')}`; };
  const nextSeq = () => ++seq;
  const uuid = () => 'web-' + Math.random().toString(36).slice(2, 10);
  const esc = s => (s || '').replace(/[<>&"']/g, c => ({ '<': '<', '>': '>', '&': '&', '"': '"', "'": '&#39;' }[c]));
  const who = () => (userName || '').trim() || '匿名';

  // Toast (3s auto-hide)
  function toast(msg, kind) {
    const el = $('toast');
    el.textContent = msg;
    el.className = 'toast ' + (kind || '');
    el.hidden = false;
    clearTimeout(toast._t);
    toast._t = setTimeout(() => { el.hidden = true; }, 2500);
  }

  function setConn(state) {
    const el = $('conn');
    el.className = 'badge ' + state;
    el.textContent = state === 'connected' ? '已连接' : (state === 'connecting' ? '连接中…' : '未连接');
  }

  function renderNow() {
    $('title').textContent = cur.title || '—';
    $('artist').textContent = cur.artist || '—';
    $('album').textContent = cur.album || '';
    const status = cur.status || '—';
    $('status').className = 'dot ' + status;
    $('mode').textContent = cur.sourceMode || '';
    $('time').textContent = `${fmtTime(cur.pos)} / ${fmtTime(cur.dur)}`;
    const art = $('art'); const ph = $('art-placeholder');
    if (cur.artRef) { art.src = `/art/${encodeURIComponent(cur.artRef)}`; art.style.display = ''; ph.style.display = 'none'; }
    else { art.removeAttribute('src'); art.style.display = 'none'; ph.style.display = ''; }
    $('btn-play').textContent = status === 'playing' ? '⏸' : '▶';
    // 音量同步(状态推送时):把 cur.volume(0~1)反映到 slider + 数字;不抢用户拖动中焦点
    if (typeof cur.volume === 'number') {
      const pct = Math.round(cur.volume * 100);
      const vol = $('vol'); if (vol && document.activeElement !== vol) vol.value = pct;
      const num = $('vol-num'); if (num) num.textContent = pct;
      const mute = $('btn-mute'); if (mute) mute.textContent = cur.volume > 0 ? '🔊' : '🔇';
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
    $('qcount').textContent = `(${queue.length})`;
    if (!queue.length) { $('queue-list').innerHTML = ''; $('queue-empty').style.display = ''; return; }
    $('queue-empty').style.display = 'none';
    $('queue-list').innerHTML = queue.map((it, i) => {
      const cls = i === queueIdx ? ' current' : (queueIdx > i ? ' played' : '');
      const draggable = queue.length > 1 ? ' draggable="true"' : '';
      const canUp = i > 0;
      const canDown = i < queue.length - 1;
      return `<li class="${cls.trim()}" data-i="${i}"${draggable}>
        <span class="handle" title="拖动排序">⇅</span>
        <span class="idx">${i + 1}.</span>
        <div class="info">
          <span class="t">${esc(it.title || '(无标题)')}</span>
          <span class="a">${esc(it.artist || '')}</span>
          <span class="by">点歌人: ${esc(it.by || '匿名')}</span>
        </div>
        <div class="ops">
          ${canUp ? `<button data-act="up" data-i="${i}" title="上移">▲</button>` : ''}
          ${canDown ? `<button data-act="down" data-i="${i}" title="下移">▼</button>` : ''}
          ${i > 0 ? `<button data-act="top" data-i="${i}" title="置顶">⤒</button>` : ''}
          <button data-act="remove" data-i="${i}" title="移除" class="danger">✕</button>
        </div>
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
         <span class="t">${esc(r.title)}</span><span class="a">${esc(r.artist || '')}</span>
         <button class="add" data-act="add" data-t="${esc(r.title)}" data-a="${esc(r.artist || '')}">⏩ 加入队列</button>
       </div>`).join('');
  }

  function renderHistory() {
    $('hcount').textContent = `(${history.length})`;
    if (!history.length) { $('history-list').innerHTML = ''; $('history-empty').style.display = ''; return; }
    $('history-empty').style.display = 'none';
    $('history-list').innerHTML = history.map((h, i) => {
      const when = h.playedAt ? new Date(h.playedAt).toLocaleString('zh-CN', { month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit' }) : '';
      return `<li>
        <div class="info">
          <span class="t">${esc(h.title || '(无标题)')}</span>
          <span class="a">${esc(h.artist || '')}</span>
          <span class="when">${when}</span>
        </div>
        <button class="add" data-act="play" data-t="${esc(h.title)}" data-a="${esc(h.artist || '')}">▶ 再听</button>
      </li>`;
    }).join('');
  }

  function tick() {
    if (cur.status === 'playing') {
      cur.pos = Math.min(cur.pos + 1, cur.dur);
      $('time').textContent = `${fmtTime(cur.pos)} / ${fmtTime(cur.dur)}`;
      const newIdx = findLineIdx(cur.pos * 1000);
      if (newIdx !== lineIdx) {
        const oldIdx = lineIdx;
        lineIdx = newIdx;
        const all = $('lyrics').querySelectorAll('.line');
        if (all[oldIdx]) { all[oldIdx].classList.remove('active'); all[oldIdx].classList.add('passed'); }
        if (all[newIdx]) { all[newIdx].classList.remove('passed'); all[newIdx].classList.add('active'); all[newIdx].scrollIntoView({ block: 'center', behavior: 'smooth' }); }
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

  function connect(pin) {
    pinCode = (pin || '').trim();
    if (!pinCode) { alert('请输入 PIN'); return; }
    if (reconnectTimer) { clearTimeout(reconnectTimer); reconnectTimer = null; }
    setConn('connecting');
    const proto = location.protocol === 'https:' ? 'wss' : 'ws';
    ws = new WebSocket(`${proto}://${location.host}/`);
    ws.onopen = () => {
      reconnectDelay = 0;
      send({ type: 'hello', id: uuid(), role: 'remote', device: 'web-panel', os: navigator.platform, caps: ['cmd', 'state', 'lyric', 'queue'], pin: pinCode });
    };
    ws.onmessage = ev => { try { handle(JSON.parse(ev.data)); } catch (e) { console.warn('bad frame', e); } };
    ws.onclose = () => {
      setConn('disconnected');
      scheduleReconnect();
    };
    ws.onerror = () => {
      // onclose 紧跟着 onerror,统一在 close 处理重连
      try { ws && ws.close(); } catch { /* 忽略 */ }
    };
  }

  // 网络抖动/后端重启后自动重连:3s → 5s → 8s → 15s(封顶)
  function scheduleReconnect() {
    if (reconnectTimer || !pinCode) return;
    reconnectDelay = reconnectDelay === 0 ? 3 : Math.min(reconnectDelay * 1.6 + 1, 15);
    reconnectTimer = setTimeout(() => {
      reconnectTimer = null;
      const b = $('conn'); if (b) b.textContent = `重连中…(${reconnectDelay}s)`;
      connect(pinCode);
    }, reconnectDelay * 1000);
  }

  function handle(f) {
    switch (f.type) {
      case 'authResult':
        if (f.ok) {
          setConn('connected');
          $('pin-wrap').hidden = true;
          send({ type: 'cmd', id: uuid(), action: 'trending', args: { limit: 24 } });
          send({ type: 'cmd', id: uuid(), action: 'history.list', args: { limit: 20 } });
          if (cur.title && cur.artist) requestLyric();
        } else {
          setConn('disconnected');
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
        if (f.ref !== searchRef) break;
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
          // 状态推送里有最新 volume,这里无需动作(renderNow 已经从 state 拿过)
          if (typeof f.volume === 'number') cur.volume = f.volume;
          renderNow();
        }
        if (f.name === 'history-list') {
          history = (f.queue || []).slice();   // 历史也用 Queue 字段携带 (JSON 数组)
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
        else console.warn('server error', f);
        break;
    }
  }

  function requestLyric() {
    send({ type: 'cmd', id: uuid(), action: 'getLyric', args: { title: cur.title, artist: cur.artist } });
  }

  // 事件
  $('btn-connect').onclick = () => connect($('pin').value);
  $('btn-prev').onclick = () => send({ type: 'cmd', id: uuid(), action: 'prev' });
  $('btn-play').onclick = () => send({ type: 'cmd', id: uuid(), action: cur.status === 'playing' ? 'pause' : 'play' });
  $('btn-next').onclick = () => send({ type: 'cmd', id: uuid(), action: 'next' });
  $('btn-shuffle').onclick = () => send({ type: 'cmd', id: uuid(), action: queueShuffled ? 'queue.unshuffle' : 'queue.shuffle' });
  $('btn-qclear').onclick = () => { if (confirm('清空队列?')) send({ type: 'cmd', id: uuid(), action: 'queue.clear' }); };
  $('btn-search').onclick = () => {
    const kw = $('kw').value.trim();
    if (!kw) return;
    searchRef = uuid();
    send({ type: 'cmd', id: searchRef, action: 'search', args: { kw } });
  };
  $('kw').addEventListener('keydown', e => { if (e.key === 'Enter') $('btn-search').click(); });
  $('user').addEventListener('change', e => { userName = e.target.value.trim(); localStorage.setItem('songlink.user', userName); });
  $('btn-hreload').onclick = () => send({ type: 'cmd', id: uuid(), action: 'history.list', args: { limit: 20 } });

  // 音量条 / 静音按钮
  function sendVolume(level) {
    send({ type: 'cmd', id: uuid(), action: 'volume', args: { level } });
  }
  $('vol').addEventListener('input', e => {
    const pct = parseInt(e.target.value, 10);
    const level = Math.max(0, Math.min(1, pct / 100));
    volumeMuted = (level === 0);
    $('vol-num').textContent = pct;
    $('btn-mute').textContent = level > 0 ? '🔊' : '🔇';
    if (level > 0) preMuteVolume = level;
    localStorage.setItem('songlink.volume', String(pct));
    // 拖动中节流发送(每 80ms 一次),减少 cmd 风暴
    if (sendVolume._t) clearTimeout(sendVolume._t);
    sendVolume._t = setTimeout(() => sendVolume(level), 80);
  });
  $('btn-mute').onclick = () => {
    if (volumeMuted || cur.volume === 0) {
      const restore = preMuteVolume > 0 ? preMuteVolume : 1.0;
      volumeMuted = false;
      const pct = Math.round(restore * 100);
      $('vol').value = pct;
      $('vol-num').textContent = pct;
      $('btn-mute').textContent = '🔊';
      localStorage.setItem('songlink.volume', String(pct));
      sendVolume(restore);
    } else {
      preMuteVolume = cur.volume || 1.0;
      volumeMuted = true;
      $('vol').value = 0;
      $('vol-num').textContent = 0;
      $('btn-mute').textContent = '🔇';
      localStorage.setItem('songlink.volume', '0');
      sendVolume(0);
    }
  };

  // 队列操作委托
  $('queue-list').addEventListener('click', e => {
    const btn = e.target.closest('button'); if (!btn) return;
    const i = parseInt(btn.dataset.i, 10);
    if (btn.dataset.act === 'up') send({ type: 'cmd', id: uuid(), action: 'queue.move', args: { from: i, to: i - 1 } });
    else if (btn.dataset.act === 'down') send({ type: 'cmd', id: uuid(), action: 'queue.move', args: { from: i, to: i + 1 } });
    else if (btn.dataset.act === 'top') send({ type: 'cmd', id: uuid(), action: 'queue.top', args: { index: i } });
    else if (btn.dataset.act === 'remove') send({ type: 'cmd', id: uuid(), action: 'queue.remove', args: { index: i } });
  });

  // 队列 HTML5 drag&drop 排序
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
    document.querySelectorAll('.queue li.drag-over').forEach(x => x.classList.remove('drag-over'));
    dragSrcIdx = -1;
  });
  $('queue-list').addEventListener('dragover', e => {
    e.preventDefault();
    const li = e.target.closest('li'); if (!li) return;
    document.querySelectorAll('.queue li.drag-over').forEach(x => x.classList.remove('drag-over'));
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

  // 搜索结果点击 = 立即播放 / ⏩ 按钮 = 加入队列
  $('results').addEventListener('click', e => {
    const btn = e.target.closest('button.add');
    const hit = e.target.closest('.hit');
    if (!hit) return;
    const t = hit.dataset.t, a = hit.dataset.a;
    if (btn) send({ type: 'cmd', id: uuid(), action: 'queue.add', args: { title: t, artist: a, by: who() } });
    else send({ type: 'cmd', id: uuid(), action: 'pick', args: { title: t, artist: a } });
  });

  // 历史再听
  $('history-list').addEventListener('click', e => {
    const btn = e.target.closest('button.add');
    const li = e.target.closest('li');
    if (!btn || !li) return;
    send({ type: 'cmd', id: uuid(), action: 'pick', args: { title: btn.dataset.t, artist: btn.dataset.a } });
  });

  // 热门点击 = 加入队列
  $('trending').addEventListener('click', e => {
    const chip = e.target.closest('.chip'); if (!chip) return;
    send({ type: 'cmd', id: uuid(), action: 'queue.add', args: { title: chip.dataset.t, artist: chip.dataset.a, by: who() } });
    chip.style.background = '#1e6e3a';
    setTimeout(() => { chip.style.background = ''; }, 300);
  });

  // ============== 长辈模式 ==============
  function applyElderMode() {
    document.body.classList.toggle('elder-mode', elderMode === 'on');
    $('btn-mode').textContent = elderMode === 'on' ? '⚙️ 完整模式' : '👴 长辈模式';
    // 长辈模式下标题简化
    const h1 = document.querySelector('header h1');
    if (h1) h1.textContent = elderMode === 'on' ? '🎵 点歌台' : '🎤 SongLink 点歌台';
  }
  function toggleElderMode() {
    elderMode = elderMode === 'on' ? 'off' : 'on';
    localStorage.setItem(ELDER_KEY, elderMode);
    applyElderMode();
    toast(elderMode === 'on' ? '已切换长辈模式 — 大字大按钮' : '已切换完整模式', 'ok');
  }

  // ============== 导入音乐 ==============
  function openImportModal() { $('import-modal').classList.remove('hidden'); $('import-path').focus(); }
  function closeImportModal() { $('import-modal').classList.add('hidden'); $('import-result').hidden = true; }

  async function pickFolder() {
    // Chromium / WebView2 支持 showDirectoryPicker;浏览器原生不支持时回退到手工输入
    if (window.showDirectoryPicker) {
      try {
        const dir = await window.showDirectoryPicker();
        // 在浏览器里我们拿不到本地绝对路径,只能给个文件夹名 → 让用户填完整路径
        $('import-path').value = dir.name;
        toast('文件夹名已填入 — 请补全完整路径(如 D:\\' + dir.name + ')', 'ok');
      } catch (e) {
        // 用户取消或权限拒绝
      }
    } else {
      toast('请直接在输入框里粘贴文件夹路径', 'ok');
      $('import-path').focus();
    }
  }

  function doImport() {
    const path = $('import-path').value.trim();
    if (!path) { toast('请输入文件夹路径', 'error'); return; }
    $('import-result').hidden = false;
    $('import-result').innerHTML = '<span class="hint">正在扫描文件夹…</span>';
    send({ type: 'cmd', id: uuid(), action: 'import.folder', args: { path } });
  }

  function showImportResult(r) {
    const el = $('import-result');
    if (r.failed > 0) {
      el.innerHTML = `<span class="ok-msg">✅ 导入完成</span><br>扫到 ${r.total} 个文件,成功导入 ${r.imported} 首,失败 ${r.failed} 个`;
    } else {
      el.innerHTML = `<span class="ok-msg">✅ 导入完成</span><br>扫到 ${r.total} 个文件,成功导入 ${r.imported} 首`;
    }
    toast('导入完成 — ' + r.imported + ' 首新歌', 'ok');
  }

  // ============== 首次启动 wizard ==============
  function setupWizard() {
    // 已走过就不再走
    if (localStorage.getItem(ONBOARD_KEY) === 'yes') return;
    // 等连上 WS 再展示(没连接就走不出第 3 步调音量)
    const waitConn = setInterval(() => {
      if (ws && ws.readyState === WebSocket.OPEN) {
        clearInterval(waitConn);
        showWizard();
      }
    }, 300);
    // 30s 还没连上(没 PIN)就不强求了
    setTimeout(() => clearInterval(waitConn), 30000);

    // 步骤切换
    function showStep(n) {
      [1, 2, 3, 4].forEach(i => { const el = $('wiz-step-' + i); if (el) el.classList.toggle('hidden', i !== n); });
      $('wiz-progress').textContent = `第 ${n} 步 / 共 4 步`;
    }
    function showWizard() { $('wizard').classList.remove('hidden'); showStep(1); }

    $('wiz-1-next').onclick = () => showStep(2);
    $('wiz-2-back').onclick = () => showStep(1);
    $('wiz-3-back').onclick = () => showStep(2);
    $('wiz-4-finish').onclick = () => { $('wizard').classList.add('hidden'); localStorage.setItem(ONBOARD_KEY, 'yes'); };

    // 步骤 2:选音源
    document.querySelectorAll('#wiz-step-2 .opts button').forEach(btn => {
      btn.onclick = () => {
        const src = btn.dataset.source;
        if (src === 'auto') {
          // 不需要服务端做任何事 — SMTC 已经接管了正在播放的 App
          toast('已设置:自动接管电脑正在播放的', 'ok');
          showStep(3);
        } else if (src === 'local') {
          // 跳到步骤 3,然后结束时弹导入 modal
          toast('已选本地音乐 — 设置完成后会提示导入', 'ok');
          window._wizWantsImport = true;
          showStep(3);
        }
      };
    });

    // 步骤 3:音量
    const wizVol = $('wiz-vol');
    const wizVolNum = $('wiz-vol-num');
    function sendWizVol() {
      const pct = parseInt(wizVol.value, 10);
      wizVolNum.textContent = pct;
      send({ type: 'cmd', id: uuid(), action: 'volume', args: { level: pct / 100 } });
      // 同步到主面板
      $('vol').value = pct;
      $('vol-num').textContent = pct;
      localStorage.setItem('songlink.volume', String(pct));
    }
    wizVol.oninput = sendWizVol;
    wizVol.onchange = sendWizVol;
    // 用 savedVol 初始化
    const sv = parseInt(localStorage.getItem('songlink.volume') || '60', 10);
    wizVol.value = sv; wizVolNum.textContent = sv;

    $('wiz-3-next').onclick = () => {
      sendWizVol();
      showStep(4);
      // 若步骤 2 选了 local,跳到完成时弹导入
      if (window._wizWantsImport) {
        setTimeout(() => openImportModal(), 400);
      }
    };
  }

  // 启动
  applyElderMode();
  if (userName) { $('user').value = userName; $('user-wrap').hidden = false; }
  // 恢复音量
  const savedVol = parseInt(localStorage.getItem('songlink.volume') || '100', 10);
  if (!Number.isNaN(savedVol) && savedVol >= 0 && savedVol <= 100) {
    $('vol').value = savedVol;
    $('vol-num').textContent = savedVol;
    $('btn-mute').textContent = savedVol > 0 ? '🔊' : '🔇';
    preMuteVolume = savedVol / 100;
    cur.volume = preMuteVolume;
    if (savedVol === 0) volumeMuted = true;
  }
  if (pinCode) connect(pinCode);
  else $('pin-wrap').hidden = false;
  setConn('disconnected');
  renderNow(); renderQueue(); renderLyrics(); renderHistory();
  tick();

  // 长辈模式 / 导入 / 引导 钩子
  $('btn-mode').onclick = toggleElderMode;
  $('btn-import').onclick = openImportModal;
  $('btn-import-cancel').onclick = closeImportModal;
  $('btn-import-pick').onclick = pickFolder;
  $('btn-import-go').onclick = doImport;
  setupWizard();
})();
