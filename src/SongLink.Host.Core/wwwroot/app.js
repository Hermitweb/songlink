// SongLink 桌面控制面板 — vanilla JS WebSocket 客户端
// 协议 §3: hello/authResult/state/lyric/cmd/ack/srcMeta

(function () {
  'use strict';

  // 状态
  let ws = null;
  let seq = 0;
  let cur = { title: '', artist: '', album: '', dur: 0, pos: 0, status: '', sourceMode: '', artRef: '' };
  let lines = [];        // 当前曲歌词 [{t, x}]
  let lineIdx = -1;
  let pinCode = (new URLSearchParams(location.search).get('pin') || '').trim();
  let searchRef = 0;

  const $ = id => document.getElementById(id);
  const fmtTime = s => { const m = Math.floor(s / 60), sec = Math.floor(s % 60); return `${m}:${sec.toString().padStart(2, '0')}`; };
  const nextSeq = () => ++seq;
  const uuid = () => 'web-' + Math.random().toString(36).slice(2, 10);

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
    const dot = $('status');
    dot.className = 'dot ' + status;
    $('mode').textContent = cur.sourceMode || '';
    $('time').textContent = `${fmtTime(cur.pos)} / ${fmtTime(cur.dur)}`;

    const art = $('art');
    const ph = $('art-placeholder');
    if (cur.artRef) {
      art.src = `/art/${encodeURIComponent(cur.artRef)}`;
      art.style.display = '';
      ph.style.display = 'none';
    } else {
      art.removeAttribute('src');
      art.style.display = 'none';
      ph.style.display = '';
    }

    // play/pause 按钮文字
    $('btn-play').textContent = status === 'playing' ? '⏸' : '▶';
  }

  function renderLyrics(now) {
    if (!lines.length) {
      $('lyrics').innerHTML = '<div class="empty">暂无歌词</div>';
      return;
    }
    const html = lines.map((l, i) =>
      `<div class="line${i === lineIdx ? ' active' : (lineIdx > i ? ' passed' : '')}" data-i="${i}">${esc(l.x)}</div>`).join('');
    $('lyrics').innerHTML = html;
    const active = $('lyrics').querySelector('.line.active');
    if (active) active.scrollIntoView({ block: 'center', behavior: 'smooth' });
  }

  function esc(s) { return (s || '').replace(/[<>&"']/g, c => ({ '<': '<', '>': '>', '&': '&', '"': '"', "'": '&#39;' }[c])); }

  function tick() {
    if (cur.status === 'playing') {
      cur.pos = Math.min(cur.pos + 1, cur.dur);
      $('time').textContent = `${fmtTime(cur.pos)} / ${fmtTime(cur.dur)}`;
      // 推进歌词高亮
      const newIdx = findLineIdx(cur.pos * 1000);
      if (newIdx !== lineIdx) {
        const oldIdx = lineIdx;
        lineIdx = newIdx;
        // 仅更新变化行,避免全文重绘
        const all = $('lyrics').querySelectorAll('.line');
        if (all[oldIdx]) { all[oldIdx].classList.remove('active'); all[oldIdx].classList.add('passed'); }
        if (all[newIdx]) {
          all[newIdx].classList.remove('passed');
          all[newIdx].classList.add('active');
          all[newIdx].scrollIntoView({ block: 'center', behavior: 'smooth' });
        }
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
    const url = `${proto}://${location.host}/`;
    ws = new WebSocket(url);
    ws.onopen = () => {
      send({ type: 'hello', id: uuid(), role: 'remote', device: 'web-panel', os: navigator.platform, caps: ['cmd', 'state', 'lyric'], pin: pinCode });
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
        } else {
          setConn('disconnected');
          alert('鉴权失败: ' + (f.code || 'AUTH_FAILED'));
        }
        break;
      case 'state':
        if (f.now) {
          cur = Object.assign(cur, f.now);
          renderNow();
          // 状态变更触发一次歌词重拉
          if (cur.title && cur.artist) requestLyric();
        }
        break;
      case 'lyric':
        if (f.lines && f.lines.length) {
          lines = f.lines;
          lineIdx = -1;
          renderLyrics();
        } else {
          lines = []; renderLyrics();
        }
        break;
      case 'searchResults':
        if (f.ref !== searchRef) break;
        renderResults(f.results || []);
        break;
      case 'event':
        if (f.name === 'source-mode-changed') cur.sourceMode = f.mode || '';
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

  function renderResults(results) {
    if (!results.length) { $('results').innerHTML = '<div class="empty">无结果</div>'; return; }
    $('results').innerHTML = results.map(r =>
      `<div class="hit" data-t="${esc(r.title || '')}" data-a="${esc(r.artist || '')}">
         <span class="t">${esc(r.title)}</span><span class="a">${esc(r.artist || '')}</span>
       </div>`).join('');
    $('results').querySelectorAll('.hit').forEach(h => h.onclick = () => {
      send({ type: 'cmd', id: uuid(), action: 'pick', args: { title: h.dataset.t, artist: h.dataset.a } });
    });
  }

  // 按钮
  $('btn-connect').onclick = () => connect($('pin').value);
  $('btn-prev').onclick = () => send({ type: 'cmd', id: uuid(), action: 'prev' });
  $('btn-play').onclick = () => send({ type: 'cmd', id: uuid(), action: cur.status === 'playing' ? 'pause' : 'play' });
  $('btn-next').onclick = () => send({ type: 'cmd', id: uuid(), action: 'next' });
  $('btn-search').onclick = () => {
    const kw = $('kw').value.trim();
    if (!kw) return;
    searchRef = uuid();
    send({ type: 'cmd', id: searchRef, action: 'search', args: { kw } });
  };
  $('kw').addEventListener('keydown', e => { if (e.key === 'Enter') $('btn-search').click(); });

  // URL 里有 PIN 就自动连
  if (pinCode) connect(pinCode);
  else $('pin-wrap').hidden = false;

  setConn('disconnected');
  renderNow();
  tick();
})();
