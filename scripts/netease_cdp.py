#!/usr/bin/env python3
"""网易云桌面客户端的 CDP 控制客户端（验证版）。

参考社区实现 Seraph310/cloudmusic-desktop-mcp（MIT）的思路，用 CDP 在播放器页面里
注入 webpack require，然后调用客户端自己的 store / dispatch / 搜索接口。

用法：
    python scripts/netease_cdp.py state
    python scripts/netease_cdp.py search "晴天 周杰伦"
    python scripts/netease_cdp.py play 186016
    python scripts/netease_cdp.py playurl 186016      # 只打印 deep link，不执行
    python scripts/netease_cdp.py queue 186016 186016
"""
import base64
import json
import sys
import urllib.request

import websocket

PORT = 9223
CDP_PORT = 9223

# 客户端内部模块 id **不能硬编码** —— 网易云升级后 id 会变
# （社区实现测的 3.1.24 是 12/14，本机 3.1.41 是 8/15）。
# 改为运行时扫描 webpack 模块表，认"导出里有没有 getStore/getDispatch/V"，
# 而不是认数字。扫描结果缓存在页面里，重复调用不再扫。
SHIM = (
    "webpackJsonp.push([[987660],{987660:function(module,exports,require){"
    "window.__gpt_cm_require__=require;}},[[987660]]]);true"
)

DISCOVER = r"""
(()=>{
  if (window.__gpt_cm_mods__) return JSON.stringify(window.__gpt_cm_mods__);
  const rq = window.__gpt_cm_require__;
  const m = rq.m || {};
  const cands = Object.keys(m).filter(id => { try { return /getStore|getDispatch/.test(String(m[id])); } catch(e){ return false; } });
  let store = null, search = null;
  for (const id of cands) {
    try {
      const ex = rq(Number(id));
      if (ex && ex.a && typeof ex.a.getStore === 'function' && typeof ex.a.getDispatch === 'function') store = Number(id);
      if (ex && typeof ex.V === 'function' && search === null) search = Number(id);
    } catch(e) {}
  }
  window.__gpt_cm_mods__ = { store, search };
  return JSON.stringify(window.__gpt_cm_mods__);
})()
"""


def ensure_runtime():
    """注入 require 钩子，并**运行时发现**内部模块 id。"""
    evaluate(SHIM)
    raw = evaluate(DISCOVER)
    mods = json.loads(raw) if raw else {}
    if mods.get("store") is None:
        raise RuntimeError(
            "没找到网易云的 store 模块（客户端版本可能变了，升级了网易云就会这样）"
        )
    return mods


def find_target():
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    with opener.open(f"http://127.0.0.1:{PORT}/json/list", timeout=6) as r:
        targets = json.load(r)
    for t in targets:
        if t.get("type") == "page" and str(t.get("url", "")).startswith("orpheus://"):
            return t
    raise RuntimeError("找不到网易云的 orpheus:// 页面（客户端是否用 --remote-debugging-port 启动？）")


def evaluate(expr, await_promise=False):
    target = find_target()
    ws = websocket.create_connection(
        target["webSocketDebuggerUrl"], timeout=20, origin="http://127.0.0.1"
    )
    try:
        ws.send(json.dumps({"id": 1, "method": "Runtime.evaluate", "params": {
            "expression": expr, "returnByValue": True, "awaitPromise": await_promise}}))
        while True:
            msg = json.loads(ws.recv())
            if msg.get("id") != 1:
                continue
            result = msg.get("result") or {}
            if result.get("exceptionDetails"):
                d = result["exceptionDetails"]
                raise RuntimeError((d.get("exception") or {}).get("description") or d.get("text"))
            return (result.get("result") or {}).get("value")
    finally:
        ws.close()


def client_state():
    m = ensure_runtime()
    return evaluate(
        f"(()=>{{const s=__gpt_cm_require__({m['store']}).a.getStore();"
        "return {mode:s.playing.playingMode,status:s.playing.playingState,"
        "volumePercent:Math.round(Number(s.playing.playingVolume||0)*100),"
        "desktopLyricsVisible:Boolean(s.setting.showLyric),"
        "current:s.playing.curPlaying?{id:Number(s.playing.curPlaying.resourceId),"
        "name:(s.playing.curPlaying.track||{}).name||''}:null,"
        "queue:s.playingList.curPlayingList.map(x=>({id:Number(x.resourceId),"
        "name:(x.track||{}).name||''}))}})()"
    )


def client_search(query, limit=5):
    m = ensure_runtime()
    payload = {"s": query, "type": 1, "offset": 0, "total": True, "limit": limit}
    r = evaluate(
        f"__gpt_cm_require__({m['search']}).V({json.dumps(payload, ensure_ascii=False)})",
        await_promise=True,
    )
    result = (r or {}).get("result") or {}
    songs = result.get("songs") or []
    return [{
        "id": int(s["id"]),
        "name": s.get("name", ""),
        "artists": [a.get("name", "") for a in (s.get("artists") or s.get("ar") or [])],
        "album": (s.get("album") or s.get("al") or {}).get("name", ""),
    } for s in songs[:limit]]


def build_play_url(song_id):
    cmd = {"type": "song", "id": str(int(song_id)), "cmd": "play"}
    enc = base64.b64encode(
        json.dumps(cmd, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    ).decode("ascii")
    return f"orpheus://{enc}"


def fetch_tracks(song_ids):
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    params = urllib.parse.urlencode({"ids": json.dumps(song_ids, separators=(",", ":"))})
    req = urllib.request.Request(
        f"https://music.163.com/api/song/detail?{params}",
        headers={"User-Agent": "Mozilla/5.0", "Referer": "https://music.163.com/"},
    )
    with opener.open(req, timeout=10) as r:
        payload = json.load(r)
    by_id = {int(t["id"]): t for t in payload.get("songs") or []}
    return [by_id[int(i)] for i in song_ids if int(i) in by_id]


def set_queue(song_ids, clear=True, play=True, start_id=0):
    tracks = fetch_tracks(song_ids)
    selected = int(start_id) if start_id else int(song_ids[0])
    track_from = {
        "text": "SongLink", "href": "", "resourceType": "track", "scene": "search",
        "fromInfo": {"originalScene": "search", "originalResourceType": "track",
                     "computeSourceResourceType": "track", "sourceData": {}},
    }
    options = {"clear": clear, "play": play, "playId": selected, "offset": -1}
    if clear:
        payload = {"tracks": tracks, "from": track_from, "options": options,
                   "triggerScene": "search", "triggerAction": "playAll"}
        action_type = "playing/play"
    else:
        payload = {"trackList": tracks, "trackFrom": track_from, "options": options,
                   "triggerScene": "search", "triggerAction": "addToPlayList"}
        action_type = "playingList/addItemToCurPlayingList"
    ensure_runtime()
    m = json.loads(evaluate(DISCOVER))
    # 用普通字符串拼接而不是嵌套 f-string：payload 里本身就有 { }，
    # 跟 f-string 的 {{ }} 转义规则混在一起极易多/少一个花括号。
    body = (
        "(async()=>{"
        f"const d=__gpt_cm_require__({m['store']}).a.getDispatch();"
        "await d(" + json.dumps(
            {"type": action_type, "payload": payload},
            ensure_ascii=False, separators=(",", ":"),
        ) + ");"
        "return true})()"
    )
    evaluate(body, await_promise=True)
    return client_state()


def main():
    cmd = sys.argv[1] if len(sys.argv) > 1 else "state"
    if cmd == "state":
        print(json.dumps(client_state(), ensure_ascii=False, indent=2))
    elif cmd == "search":
        q = sys.argv[2] if len(sys.argv) > 2 else "晴天"
        lim = int(sys.argv[3]) if len(sys.argv) > 3 else 5
        print(json.dumps(client_search(q, lim), ensure_ascii=False, indent=2))
    elif cmd == "playurl":
        print(build_play_url(sys.argv[2]))
    elif cmd == "play":
        url = build_play_url(sys.argv[2])
        import os
        os.startfile(url)
        print("已下发:", url)
    elif cmd == "queue":
        ids = [int(x) for x in sys.argv[2:]]
        print(json.dumps(set_queue(ids), ensure_ascii=False, indent=2))
    else:
        print("未知命令", cmd)


if __name__ == "__main__":
    import urllib.parse
    main()
