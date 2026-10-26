package songlink.remote.data.audio

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import android.util.Log
import androidx.core.app.NotificationCompat
import dagger.hilt.android.AndroidEntryPoint
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import songlink.remote.MainActivity
import songlink.remote.data.transport.AudioTransportManager
import javax.inject.Inject

/**
 * 前台服务 (架构 §3.9 audio:start / 契约 §4):
 *   1. 持有 [android.media.projection.MediaProjection] token (API 29+ 系统要求捕获音频必须前台)
 *   2. 启动 [PhoneAudioCapturer] 的真实 AudioRecord + AudioPlaybackCaptureConfiguration 路径
 *   3. 经 [AudioTransportManager] 推 PCM 到 Host UDP
 *
 * Activity (CaptureProjectionActivity) 通过 [ACTION_BIND_PROJECTION] 把 MediaProjection
 * Intent 的 resultCode/data 投递给本 Service,本 Service 用 startForeground 上锁通知图标
 * (系统权限要求),之后才允许 AudioPlaybackCapture 配置生效。
 *
 * 真机使用: 该 Service 不需要界面; UI Activity (CaptureProjectionActivity) 收到用户的
 * 媒体投影同意后立即切到本 Service 跑前台。
 */
@AndroidEntryPoint
class ProjectionService : Service() {

    companion object {
        private const val TAG = "ProjectionService"
        const val CHANNEL_ID = "songlink_projection"
        const val NOTIF_ID = 4101

        const val ACTION_BIND_PROJECTION = "songlink.remote.action.BIND_PROJECTION"
        const val ACTION_STOP = "songlink.remote.action.STOP_PROJECTION"
        const val EXTRA_RESULT_CODE = "result_code"
        const val EXTRA_RESULT_DATA = "result_data"
        const val EXTRA_HOST = "host"
        const val EXTRA_PORT = "port"

        /**
         * 启动 ProjectionService,带 MediaProjection resultCode/data 与目标 Host/Port。
         * 通常由 [CaptureProjectionActivity] 在拿到用户授权后调用。
         */
        fun start(
            ctx: Context,
            resultCode: Int,
            resultData: Intent,
            host: String,
            port: Int
        ) {
            val i = Intent(ctx, ProjectionService::class.java).apply {
                action = ACTION_BIND_PROJECTION
                putExtra(EXTRA_RESULT_CODE, resultCode)
                putExtra(EXTRA_RESULT_DATA, resultData)
                putExtra(EXTRA_HOST, host)
                putExtra(EXTRA_PORT, port)
            }
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) ctx.startForegroundService(i)
            else ctx.startService(i)
        }

        fun stop(ctx: Context) {
            ctx.startService(Intent(ctx, ProjectionService::class.java).apply { action = ACTION_STOP })
        }
    }

    @Inject lateinit var transport: AudioTransportManager

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private var projectionData: Intent? = null
    private var projectionCode: Int = -1
    private var host: String = "127.0.0.1"
    private var port: Int = 51_000
    private var started = false

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        Log.i(TAG, "onCreate; foreground service alive")
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_BIND_PROJECTION -> handleBind(intent)
            ACTION_STOP -> handleStop()
            else -> Log.w(TAG, "ignore unknown action: ${intent?.action}")
        }
        return START_NOT_STICKY
    }

    private fun handleBind(intent: Intent) {
        projectionCode = intent.getIntExtra(EXTRA_RESULT_CODE, -1)
        // EXTRA_RESULT_DATA 是 Parcelable,需要从 Intent 取
        @Suppress("DEPRECATION")
        projectionData = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU)
            intent.getParcelableExtra(EXTRA_RESULT_DATA, Intent::class.java)
        else
            intent.getParcelableExtra(EXTRA_RESULT_DATA)
        host = intent.getStringExtra(EXTRA_HOST) ?: "127.0.0.1"
        port = intent.getIntExtra(EXTRA_PORT, 51_000)

        // 前台通知 (Android 14+: 必须显式声明 foregroundServiceType=mediaProjection)
        startForegroundCompat()

        val data = projectionData
        Log.i(TAG, "BIND code=$projectionCode dataNull=${data == null} started=$started host=$host:$port")

        // 只判 data：系统 MediaProjection 授权成功时回调的 resultCode 就是
        // Activity.RESULT_OK，其字面值是 -1；getMediaProjection(-1, data) 是合法调用。
        // 曾经写成 `projectionCode < 0 || data == null`，把每次正常授权都当成无效，
        // token 永远喂不进 capturer → 真机 0 收包。
        if (data == null) {
            Log.w(TAG, "no result data; capturing not possible (retry allowed)")
            return
        }

        if (started) {
            // 已经在推流：只重绑 token（用户可能换了新的一次授权），不重开采集。
            Log.i(TAG, "already started; re-binding MediaProjection token only")
            scope.launch {
                val mp = (application.getSystemService(Context.MEDIA_PROJECTION_SERVICE)
                        as android.media.projection.MediaProjectionManager)
                    .getMediaProjection(projectionCode, data)
                transport.bindMediaProjection(mp)
                Log.i(TAG, "MediaProjection token re-bound to PhoneAudioCapturer")
            }
            return
        }
        started = true

        scope.launch {
            // 1) 喂 MediaProjection token 到 capturer
            //    经 [AudioTransportManager.bindMediaProjection] 走 Hilt 单例的 capturer，
            //    不能再直接 @Inject 第二个 capturer 实例 —— 那样 token 喂给 A 实例、
            //    推流却由 B 实例发，B 的 projection 永远为 null → NEED_PROJECTION 死循环。
            val mp = (application.getSystemService(Context.MEDIA_PROJECTION_SERVICE)
                    as android.media.projection.MediaProjectionManager)
                .getMediaProjection(projectionCode, data)
            transport.bindMediaProjection(mp)
            Log.i(TAG, "MediaProjection token bound to PhoneAudioCapturer")

            // 2) 启动 PCM 推流
            transport.startAudioCapture(host, port) { ok, err ->
                if (!ok) {
                    Log.w(TAG, "startAudioCapture failed: $err")
                    stopAndCleanup()
                } else {
                    Log.i(TAG, "PCM streaming → $host:$port")
                }
            }
        }
    }

    private fun handleStop() {
        Log.i(TAG, "STOP requested")
        stopAndCleanup()
        stopSelf()
    }

    private fun stopAndCleanup() {
        transport.stopAudioCapture()
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N) stopForeground(STOP_FOREGROUND_REMOVE)
        else @Suppress("DEPRECATION") stopForeground(true)
    }

    private fun startForegroundCompat() {
        val nm = getSystemService(NotificationManager::class.java)
        val channel = NotificationChannel(
            CHANNEL_ID,
            "SongLink 音频投射",
            NotificationManager.IMPORTANCE_LOW
        ).apply { description = "音频采集前台服务" }
        nm.createNotificationChannel(channel)

        val tapIntent = PendingIntent.getActivity(
            this, 0,
            Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        )

        val notif: Notification = NotificationCompat.Builder(this, CHANNEL_ID)
            .setContentTitle("SongLink Remote")
            .setContentText("正在采集系统音频 → $host:$port")
            .setSmallIcon(android.R.drawable.stat_sys_speakerphone)
            .setContentIntent(tapIntent)
            .setOngoing(true)
            .setCategory(NotificationCompat.CATEGORY_SERVICE)
            .build()

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
            startForeground(
                NOTIF_ID, notif,
                ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION
            )
        } else if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            startForeground(
                NOTIF_ID, notif,
                ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION
            )
        } else {
            startForeground(NOTIF_ID, notif)
        }
    }

    override fun onDestroy() {
        Log.i(TAG, "onDestroy")
        scope.cancel()
        transport.stopAudioCapture()
        super.onDestroy()
    }
}
