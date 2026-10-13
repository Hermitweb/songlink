// SongLink Web Admin - Client-side logic
class WebAdminClient {
    constructor() {
        this.ws = null;
        this.reconnectAttempts = 0;
        this.maxReconnectAttempts = 5;
        this.connect();
    }
    
    async connect() {
        try {
            const wsUrl = 'ws://localhost:51999';
            console.log('Connecting to:', wsUrl);
            
            this.ws = new WebSocket(wsUrl);
            
            this.ws.onopen = () => {
                console.log('Connected!');
                this.updateStatus('已连接', '#10b981');
                
                // Send Hello frame
                this.sendFrame({
                    v: 1,
                    type: 'hello',
                    role: 'remote',
                    device: 'web-admin',
                    id: 'init'
                });
                
                // Start listening for events
                this.startEventListener();
            };
            
            this.ws.onmessage = (event) => {
                try {
                    const data = JSON.parse(event.data);
                    this.handleMessage(data);
                } catch (e) {
                    console.error('Parse error:', e);
                }
            };
            
            this.ws.onerror = (error) => {
                console.error('WebSocket error:', error);
                this.updateStatus('连接错误', '#ef4444');
            };
            
            this.ws.onclose = () => {
                console.log('Disconnected. Reconnecting...');
                this.updateStatus('断开中', '#f59e0b');
                
                if (this.reconnectAttempts < this.maxReconnectAttempts) {
                    setTimeout(() => this.connect(), 3000);
                    this.reconnectAttempts++;
                } else {
                    this.updateStatus('连接失败，请检查 Host', '#ef4444');
                }
            };
        } catch (e) {
            console.error('Connection failed:', e);
        }
    }
    
    sendFrame(frame) {
        if (!this.ws || this.ws.readyState !== WebSocket.OPEN) {
            console.warn('Not connected');
            return;
        }
        
        const json = JSON.stringify(frame);
        this.ws.send(json);
    }
    
    handleMessage(data) {
        switch (data.type) {
            case 'authResult':
                if (data.ok) {
                    console.log('Authenticated successfully');
                } else {
                    console.error('Auth failed:', data.code);
                }
                break;
                
            case 'state':
                this.renderNowPlaying(data.now);
                break;
                
            case 'event':
                if (data.name === 'queue-updated') {
                    this.renderQueue(data.queue);
                }
                break;
                
            case 'lyric':
                console.log('Lyric received:', data.lines.length, 'lines');
                break;
                
            case 'searchResults':
                console.log('Search results:', data.results);
                break;
        }
    }
    
    renderNowPlaying(np) {
        if (!np || !np.title) {
            document.getElementById('currentPlaying').innerHTML = 
                '<p style="color: #6b7280; font-style: italic;">暂无播放内容...</p>';
            return;
        }
        
        const html = `
            <div style="margin-bottom: 1rem;">
                <h3 style="font-size: 1.25rem; margin-bottom: 0.25rem;">${np.title}</h3>
                <p class="song-artist">${np.artist}</p>
            </div>
            <p><strong>时长:</strong> ${Math.floor(np.dur)}s | <strong>进度:</strong> ${Math.floor(np.pos)}s (${np.status})</p>
        `;
        
        document.getElementById('currentPlaying').innerHTML = html;
    }
    
    renderQueue(queueItems) {
        document.getElementById('queueCount').textContent = queueItems.length;
        
        if (queueItems.length === 0) {
            document.getElementById('queueList').innerHTML = 
                '<p style="color: #6b7280; padding: 1rem; text-align: center;">队列已清空</p>';
            return;
        }
        
        const html = queueItems.map(item => `
            <div class="queue-item">
                <div>
                    <div class="song-title">${item.title}</div>
                    <div class="song-artist">${item.artist} · 由 ${item.by} 添加</div>
                </div>
                <button class="btn btn-primary" onclick="topItem('${item.title}', '${item.artist}')">置顶</button>
            </div>
        `).join('');
        
        document.getElementById('queueList').innerHTML = html;
    }
    
    startEventListener() {
        // Event listeners will be added as needed
        console.log('Event listener started');
    }
    
    updateStatus(text, color) {
        const badge = document.getElementById('connectionStatus');
        badge.textContent = text;
        badge.style.background = color + '40'; // Add transparency
        badge.style.color = color;
    }
    
    // Command handlers
    async sendCmd(action, args = {}) {
        const cmd = {
            v: 1,
            type: 'cmd',
            action: action,
            id: Date.now().toString(),
            args: Object.keys(args).length > 0 ? args : undefined
        };
        
        this.sendFrame(cmd);
        
        // Show confirmation toast
        showToast(`${action} command sent`);
    }
    
    togglePlay() {
        this.sendCmd('toggle');
    }
    
    clearQueue() {
        if (confirm('确定要清空整个播放队列吗？')) {
            this.sendCmd('queue.clear');
        }
    }
    
    searchSong() {
        const query = document.getElementById('searchInput').value.trim();
        if (!query) {
            alert('请输入搜索关键词');
            return;
        }
        
        this.sendCmd('search', { kw: query });
    }
}

function topItem(title, artist) {
    showToast(`置顶：${title}`);
    // TODO: Implement top item functionality
}

function showToast(message) {
    const toast = document.createElement('div');
    toast.style.cssText = `
        position: fixed;
        bottom: 20px;
        right: 20px;
        background: #1f2937;
        color: white;
        padding: 1rem 1.5rem;
        border-radius: 8px;
        box-shadow: 0 4px 6px rgba(0,0,0,0.1);
        z-index: 1000;
        animation: slideIn 0.3s ease;
    `;
    toast.textContent = message;
    
    document.body.appendChild(toast);
    
    setTimeout(() => {
        toast.style.opacity = '0';
        setTimeout(() => toast.remove(), 300);
    }, 3000);
}

document.addEventListener('DOMContentLoaded', () => {
    new WebAdminClient();
});
