// SongLink Audio Spectrum Visualization (M2.5 P2)
class AudioSpectrum {
    constructor(canvasId) {
        this.canvas = document.getElementById(canvasId);
        this.ctx = this.canvas.getContext('2d');
        
        // Configuration
        this.frequencyBinCount = 64;
        this.barWidth = 8;
        this.barGap = 3;
        this.animationId = null;
        this.isRunning = false;
        
        // Colors
        this.colors = [
            '#667eea', '#764ba2', '#f093fb', '#f5576c', 
            '#4facfe', '#00f2fe', '#43e97b', '#38f9d7'
        ];
        
        this.resize();
        window.addEventListener('resize', () => this.resize());
    }
    
    resize() {
        const dpr = window.devicePixelRatio || 1;
        const rect = this.canvas.getBoundingClientRect();
        
        this.canvas.width = rect.width * dpr;
        this.canvas.height = rect.height * dpr;
        
        this.ctx.scale(dpr, dpr);
        this.canvas.style.width = `${rect.width}px`;
        this.canvas.style.height = `${rect.height}px`;
    }
    
    start() {
        if (this.isRunning) return;
        this.isRunning = true;
        this.animate();
    }
    
    stop() {
        this.isRunning = false;
        if (this.animationId) {
            cancelAnimationFrame(this.animationId);
            this.animationId = null;
        }
    }
    
    toggle() {
        if (this.isRunning) {
            this.stop();
        } else {
            this.start();
        }
    }
    
    animate() {
        if (!this.isRunning) return;
        
        this.draw();
        this.animationId = requestAnimationFrame(() => this.animate());
    }
    
    draw() {
        const width = this.canvas.width / (window.devicePixelRatio || 1);
        const height = this.canvas.height / (window.devicePixelRatio || 1);
        
        // Clear canvas
        this.ctx.clearRect(0, 0, width, height);
        
        // Generate frequency data (simulated for demo)
        const frequencies = this.generateSimulatedFrequencies();
        
        // Draw bars
        const totalBars = Math.floor(width / (this.barWidth + this.barGap));
        const maxBarHeight = height - 40;
        
        for (let i = 0; i < totalBars; i++) {
            const barHeight = frequencies[i] ? frequencies[i] * maxBarHeight : 0;
            const x = i * (this.barWidth + this.barGap) + 20;
            const y = height - barHeight - 20;
            
            // Gradient color
            const gradient = this.ctx.createLinearGradient(0, y, 0, height - 20);
            gradient.addColorStop(0, this.colors[i % this.colors.length]);
            gradient.addColorStop(1, this.colors[(i + 4) % this.colors.length]);
            
            this.ctx.fillStyle = gradient;
            
            // Rounded bar top
            this.roundRect(x, y, this.barWidth, barHeight, 4, true);
        }
        
        // Draw volume meter
        this.drawVolumeMeter(frequencies);
    }
    
    generateSimulatedFrequencies() {
        // Simulate audio spectrum data
        const frequencies = new Array(this.frequencyBinCount).fill(0);
        
        for (let i = 0; i < this.frequencyBinCount; i++) {
            const t = Date.now() / 1000;
            // Create wave patterns
            const freq = i * 0.5 + Math.sin(t * 2 + i * 0.1) * 30;
            frequencies[i] = Math.max(0, Math.min(1, (freq + 50) / 100));
        }
        
        return frequencies;
    }
    
    roundRect(x, y, width, height, radius, fill) {
        this.ctx.beginPath();
        this.ctx.moveTo(x + radius, y);
        this.ctx.lineTo(x + width - radius, y);
        this.ctx.quadraticCurveTo(x + width, y, x + width, y + radius);
        this.ctx.lineTo(x + width, y + height - radius);
        this.ctx.quadraticCurveTo(x + width, y + height, x + width - radius, y + height);
        this.ctx.lineTo(x + radius, y + height);
        this.ctx.quadraticCurveTo(x, y + height, x, y + height - radius);
        this.ctx.lineTo(x, y + radius);
        this.ctx.quadraticCurveTo(x, y, x + radius, y);
        this.ctx.closePath();
        
        if (fill) this.ctx.fill();
    }
    
    drawVolumeMeter(frequencies) {
        const avgVolume = frequencies.reduce((a, b) => a + b, 0) / frequencies.length;
        const volumePercent = Math.round(avgVolume * 100);
        
        this.ctx.fillStyle = '#fff';
        this.ctx.font = '20px Arial';
        this.ctx.fillText(`${volumePercent}%`, 20, 30);
        
        // Volume bar
        const barWidth = 200;
        const barHeight = 8;
        const barX = 20;
        const barY = 40;
        
        this.ctx.fillStyle = 'rgba(255,255,255,0.2)';
        this.roundRect(barX, barY, barWidth, barHeight, 4, true);
        
        this.ctx.fillStyle = '#4facfe';
        const filledWidth = (volumePercent / 100) * barWidth;
        this.roundRect(barX, barY, filledWidth, barHeight, 4, true);
    }
}

// Initialize visualization
const spectrum = new AudioSpectrum('spectrumCanvas');

// Update stats display
setInterval(() => {
    document.getElementById('frequencyCount').textContent = spectrum.frequencyBinCount;
}, 1000);

// Global functions for button controls
function toggleSimulation() {
    spectrum.toggle();
}

function changeMode() {
    spectrum.colors.reverse();
}

// Start automatically
document.addEventListener('DOMContentLoaded', () => {
    spectrum.start();
});
