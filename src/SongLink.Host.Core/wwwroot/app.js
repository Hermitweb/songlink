// SongLink 点歌台 — vanilla JS WebSocket 客户端
(function () {
  'use strict';

  let ws = null;
  let seq = 0;
  let cur = { title: '', artist: '', album: '', dur: 0, pos: 0, status: '', sourceMode: '', artRef: '' };
  let lines = [];
  let lineIdx = -1;
  let queue = [];
  let queueIdx = -1;     // 当前在播的 index
  let queueShuffled = false;
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
      return `<li class="${cls.trim()}" data-i="${i}">
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
          // 拉首屏数据:队列 + 热门 + 当前曲歌词
          send({ type: 'cmd', id: uuid(), action: 'trending', args: { limit: 24 } });
          trendingRef = 'first';
          if (cur.title && cur.artist) requestLyric();
        } else {
          setConn('disconnected');
          alert('鉴权失败: ' + (f.code || 'AUTH_FAILED'));
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
        renderNow();
        break;
      case 'error':
        console.warn('server error', f);
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

  // 队列操作委托
  $('queue-list').addEventListener('click', e => {
    const btn = e.target.closest('button'); if (!btn) return;
    const i = parseInt(btn.dataset.i, 10);
    if (btn.dataset.act === 'top') send({ type: 'cmd', id: uuid(), action: 'queue.top', args: { index: i } });
    else if (btn.dataset.act === 'remove') send({ type: 'cmd', id: uuid(), action: 'queue.remove', args: { index: i } });
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
  renderNow(); renderQueue(); renderLyrics();
  tick();
})();
