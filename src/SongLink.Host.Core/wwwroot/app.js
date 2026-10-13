// SongLink 点歌台 — vanilla JS WebSocket 客户端
(function () {
  'use strict';

  let ws = null;
  let seq = 0;
  let cur = { title: '', artist: '', album: '', dur: 0, pos: 0, status: '', sourceMode: '', artRef: '' };
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
      return `<li class="${cls.trim()}" data-i="${i}"${draggable}>
        <span class="handle" title="拖动排序">⇅</span>
        <span class="idx">${i + 1}.</span>
        <div class="info">
          <span class="t">${esc(it.title || '(无标题)')}</span>
          <span class="a">${esc(it.artist || '')}</span>
          <span class="by">点歌人: ${esc(it.by || '匿名')}</span>
        </div>
        <div class="ops">
          ${i > 0 ? `<button data-act="top" data-i="${i}" title="置顶">⬆</button>` : ''}
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
    setConn('connecting');
    const proto = location.protocol === 'https:' ? 'wss' : 'ws';
    ws = new WebSocket(`${proto}://${location.host}/`);
    ws.onopen = () => {
      send({ type: 'hello', id: uuid(), role: 'remote', device: 'web-panel', os: navigator.platform, caps: ['cmd', 'state', 'lyric', 'queue'], pin: pinCode });
    };
    ws.onmessage = ev => { try { handle(JSON.parse(ev.data)); } catch (e) { console.warn('bad frame', e); } };
    ws.onclose = () => setConn('disconnected');
    ws.onerror = () => setConn('disconnected');
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
        if (f.name === 'history-list') {
          history = (f.queue || []).slice();   // 历史也用 Queue 字段携带 (JSON 数组)
          renderHistory();
        }
        renderNow();
        break;
      case 'error':
        if (f.code === 'DUPLICATE') toast(f.hint || '已在队列中', 'error');
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

  // 队列操作委托
  $('queue-list').addEventListener('click', e => {
    const btn = e.target.closest('button'); if (!btn) return;
    const i = parseInt(btn.dataset.i, 10);
    if (btn.dataset.act === 'top') send({ type: 'cmd', id: uuid(), action: 'queue.top', args: { index: i } });
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

  // 启动
  if (userName) { $('user').value = userName; $('user-wrap').hidden = false; }
  if (pinCode) connect(pinCode);
  else $('pin-wrap').hidden = false;
  setConn('disconnected');
  renderNow(); renderQueue(); renderLyrics(); renderHistory();
  tick();
})();
