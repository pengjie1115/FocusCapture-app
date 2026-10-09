using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using NAudio.Wave;
using SherpaOnnx;

namespace FocusCapture.Services;

/// <summary>
/// 纯 C# 语音识别服务。
/// 使用 NAudio 采集麦克风 → sherpa-onnx VAD 断句 → OfflineRecognizer 识别。
/// 完全替代原 Python 子进程方案，无需 Python 环境。
/// </summary>
public class VoiceService : IDisposable
{
    private bool _disposed;
    private bool _isRunning;

    // ── 音频采集 ──
    private WaveInEvent? _waveIn;

    // ── sherpa-onnx ──
    private VoiceActivityDetector? _vad;
    private OfflineRecognizer? _recognizer;

    // ── 工作线程 ──
    private CancellationTokenSource? _cts;
    private Task? _processTask;

    // ── 音频缓冲（线程安全） ──
    private readonly object _bufferLock = new();
    private readonly Queue<float[]> _audioQueue = new();

    // ── 常量 ──
    private const int SampleRate = 16000;
    private const int Channels = 1;
    private const int BufferMilliseconds = 100; // 100ms 块

    // ── 事件 ──
    /// <summary>收到部分识别结果（状态提示）</summary>
    public event Action<string>? PartialText;

    /// <summary>一段话识别完成</summary>
    public event Action<string>? FinalText;

    /// <summary>ASR 就绪</summary>
    public event Action<string>? Ready;

    /// <summary>错误</summary>
    public event Action<string>? Error;

    /// <summary>状态更新（下载进度、模型就绪等）</summary>
    public event Action<string>? StatusChanged;

    /// <summary>音量电平（0.0~1.0）</summary>
    public event Action<double>? VolumeLevel;

    /// <summary>是否正在运行</summary>
    public bool IsRunning => _isRunning;

    // ── 模型路径 ──
    private static readonly string ModelDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FocusCapture", "models", "firered-asr2-ctc");

    private static string ModelPath => Path.Combine(ModelDir, "model.int8.onnx");
    private static string TokensPath => Path.Combine(ModelDir, "tokens.txt");
    private static string VadModelPath => Path.Combine(ModelDir, "silero_vad.onnx");

    // ── 模型下载源（FireRedASR2 CTC INT8，约 740MB） ──
    private const string HfMirrorBase = "https://hf-mirror.com/csukuangfj2/sherpa-onnx-fire-red-asr2-ctc-zh_en-int8-2026-02-25/resolve/main";
    private const string HfBase = "https://huggingface.co/csukuangfj2/sherpa-onnx-fire-red-asr2-ctc-zh_en-int8-2026-02-25/resolve/main";

    private static readonly Dictionary<string, string[]> DownloadSources = new()
    {
        ["model.int8.onnx"] = new[]
        {
            $"{HfMirrorBase}/model.int8.onnx",
            $"{HfBase}/model.int8.onnx",
        },
        ["tokens.txt"] = new[]
        {
            $"{HfMirrorBase}/tokens.txt",
            $"{HfBase}/tokens.txt",
        },
        ["silero_vad.onnx"] = new[]
        {
            "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/silero_vad.onnx",
            "https://hf-mirror.com/csukuangfj/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17/resolve/main/silero_vad.onnx",
        },
    };

    /// <summary>启动语音识别</summary>
    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(VoiceService));
        if (_isRunning) return;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        // 在后台线程初始化（模型加载 + 下载可能耗时）
        _processTask = Task.Run(() => InitializeAndRun(ct), ct);
    }

    /// <summary>停止语音识别</summary>
    public void Stop()
    {
        if (!_isRunning) return;

        // 只发取消信号，让后台线程自行退出并清理资源
        _cts?.Cancel();

        try { _processTask?.Wait(5000); } catch { }

        _processTask = null;
        _cts?.Dispose();
        _cts = null;
        _isRunning = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    // ══════════════════════════════════════════════
    //  初始化 + 主循环
    // ══════════════════════════════════════════════

    private void InitializeAndRun(CancellationToken ct)
    {
        try
        {
            // 1. 确保模型就绪
            if (!EnsureModelsReady(ct)) return;

            // 2. 初始化 VAD（降低阈值避免首字裁剪）
            StatusChanged?.Invoke("正在加载语音模型…");
            var vadConfig = new VadModelConfig();
            vadConfig.SileroVad.Model = VadModelPath;
            vadConfig.SileroVad.Threshold = 0.3f;          // 降低阈值，捕捉轻声起始音
            vadConfig.SileroVad.MinSilenceDuration = 0.6f;  // 尾部多留 0.6s 静音
            vadConfig.SileroVad.MinSpeechDuration = 0.15f;
            vadConfig.SileroVad.MaxSpeechDuration = 30.0f;
            vadConfig.SampleRate = SampleRate;
            _vad = new VoiceActivityDetector(vadConfig, bufferSizeInSeconds: 60);

            // 3. 初始化离线识别器（FireRedASR2 CTC）
            var config = new OfflineRecognizerConfig();
            config.FeatConfig.SampleRate = SampleRate;
            config.FeatConfig.FeatureDim = 80;
            config.ModelConfig.Tokens = TokensPath;
            config.ModelConfig.NumThreads = 2;
            config.ModelConfig.Provider = "cpu";
            config.ModelConfig.FireRedAsrCtc.Model = ModelPath;
            _recognizer = new OfflineRecognizer(config);

            // 4. 启动麦克风
            StartAudioCapture();

            _isRunning = true;
            Ready?.Invoke(GetMicrophoneName());
            StatusChanged?.Invoke("聆听中…");

            // 5. 处理循环
            bool wasSpeaking = false;
            while (!ct.IsCancellationRequested)
            {
                float[]? samples = null;
                lock (_bufferLock)
                {
                    if (_audioQueue.Count > 0)
                        samples = _audioQueue.Dequeue();
                }

                if (samples == null)
                {
                    Thread.Sleep(10);
                    continue;
                }

                // 音量计算
                float rms = CalculateRms(samples);
                if (rms > 0.002f)
                    VolumeLevel?.Invoke(Math.Min(1.0, rms * 8));

                // 送入 VAD
                _vad.AcceptWaveform(samples);

                // 语音活动状态
                bool isSpeaking = _vad.IsSpeechDetected();
                if (isSpeaking && !wasSpeaking)
                {
                    wasSpeaking = true;
                    PartialText?.Invoke("正在识别…");
                }
                else if (!isSpeaking && wasSpeaking)
                {
                    wasSpeaking = false;
                }

                // 处理 VAD 切出的完整语音段
                ProcessVadSegments();
            }

            // ── 停止时：处理剩余音频，避免尾部丢失 ──
            lock (_bufferLock)
            {
                while (_audioQueue.Count > 0)
                {
                    var remaining = _audioQueue.Dequeue();
                    _vad.AcceptWaveform(remaining);
                }
            }
            // 送入一段静音强制 VAD 截断当前语音段
            _vad.AcceptWaveform(new float[SampleRate / 2]); // 0.5s 静音
            ProcessVadSegments();
        }
        catch (OperationCanceledException) { /* 正常停止 */ }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VoiceService] 初始化/运行错误: {ex}");
            Error?.Invoke(ex.Message);
        }
        finally
        {
            // 所有资源在后台线程统一释放，避免与 UI 线程竞争
            StopAudioCapture();
            try { _vad?.Dispose(); } catch { }
            _vad = null;
            try { _recognizer?.Dispose(); } catch { }
            _recognizer = null;
            lock (_bufferLock) _audioQueue.Clear();
            _isRunning = false;
        }
    }

    /// <summary>处理 VAD 切出的完整语音段</summary>
    private void ProcessVadSegments()
    {
        while (!_vad!.IsEmpty())
        {
            var segment = _vad.Front();
            var segSamples = segment.Samples;
            _vad.Pop();

            if (segSamples.Length < 1600) continue; // < 0.1s 跳过

            var stream = _recognizer!.CreateStream();
            stream.AcceptWaveform(SampleRate, segSamples);
            _recognizer.Decode(stream);

            var text = stream.Result.Text?.Trim() ?? "";
            stream.Dispose();

            if (!string.IsNullOrEmpty(text))
            {
                FinalText?.Invoke(text);
                PartialText?.Invoke("聆听中…");
            }
        }
    }

    // ══════════════════════════════════════════════
    //  麦克风采集（NAudio）
    // ══════════════════════════════════════════════

    private void StartAudioCapture()
    {
        // 使用 16-bit PCM 格式（兼容性最好，所有声卡都支持）
        _waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(SampleRate, 16, Channels),
            BufferMilliseconds = BufferMilliseconds,
        };
        _waveIn.DataAvailable += OnAudioDataAvailable;
        _waveIn.RecordingStopped += OnRecordingStopped;
        _waveIn.StartRecording();
        Debug.WriteLine($"[VoiceService] 麦克风已启动: {SampleRate}Hz 16-bit PCM");
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            Debug.WriteLine($"[VoiceService] 录音异常: {e.Exception.Message}");
            Error?.Invoke($"麦克风错误: {e.Exception.Message}");
        }
    }

    private void StopAudioCapture()
    {
        if (_waveIn == null) return;
        try
        {
            _waveIn.StopRecording();
            _waveIn.DataAvailable -= OnAudioDataAvailable;
            _waveIn.RecordingStopped -= OnRecordingStopped;
            _waveIn.Dispose();
        }
        catch { }
        _waveIn = null;
    }

    private void OnAudioDataAvailable(object? sender, WaveInEventArgs e)
    {
        // 16-bit PCM → float32（除以 32768 归一化到 -1.0~1.0）
        int sampleCount = e.BytesRecorded / 2; // 16-bit = 2 bytes per sample
        if (sampleCount <= 0) return;

        var samples = new float[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            short s16 = BitConverter.ToInt16(e.Buffer, i * 2);
            samples[i] = s16 / 32768f;
        }

        lock (_bufferLock)
        {
            _audioQueue.Enqueue(samples);
            // 防止队列过大（最多缓存 10 秒）
            while (_audioQueue.Count > 100)
                _audioQueue.Dequeue();
        }
    }

    private static string GetMicrophoneName()
    {
        try
        {
            int count = WaveInEvent.DeviceCount;
            if (count > 0)
                return WaveInEvent.GetCapabilities(0).ProductName;
        }
        catch { }
        return "默认麦克风";
    }

    // ══════════════════════════════════════════════
    //  模型下载
    // ══════════════════════════════════════════════

    private bool EnsureModelsReady(CancellationToken ct)
    {
        Directory.CreateDirectory(ModelDir);

        // 尝试复用旧 SenseVoice 目录的 silero_vad.onnx
        var oldVadPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FocusCapture", "models", "sensevoice", "silero_vad.onnx");
        if (!File.Exists(VadModelPath) && File.Exists(oldVadPath))
        {
            File.Copy(oldVadPath, VadModelPath);
            Debug.WriteLine("[VoiceService] 已复用旧目录的 silero_vad.onnx");
        }

        var missing = new List<string>();
        if (!File.Exists(ModelPath)) missing.Add("model.int8.onnx");
        if (!File.Exists(TokensPath)) missing.Add("tokens.txt");
        if (!File.Exists(VadModelPath)) missing.Add("silero_vad.onnx");

        if (missing.Count == 0)
        {
            // 验证文件大小（FireRedASR2 CTC INT8 约 740MB）
            if (new FileInfo(ModelPath).Length < 700L * 1024 * 1024)
            {
                StatusChanged?.Invoke("模型文件不完整，重新下载…");
                File.Delete(ModelPath);
                missing.Add("model.int8.onnx");
            }
            else return true;
        }

        StatusChanged?.Invoke($"缺失 {missing.Count} 个模型文件（语音模型约 740MB），开始下载…");

        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromMinutes(10);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("FocusCapture/3.0");

        foreach (var fileName in missing)
        {
            if (ct.IsCancellationRequested) return false;

            if (!DownloadSources.TryGetValue(fileName, out var sources))
                continue;

            bool ok = false;
            foreach (var url in sources)
            {
                if (ct.IsCancellationRequested) return false;

                var sourceName = url.Contains("hf-mirror") ? "HF镜像" 
                    : url.Contains("huggingface") ? "HuggingFace" 
                    : url.Contains("github") ? "GitHub" : "ModelScope";
                StatusChanged?.Invoke($"正在下载 {fileName}（{sourceName}）…");

                try
                {
                    DownloadFileAsync(http, url, Path.Combine(ModelDir, fileName), fileName, ct)
                        .GetAwaiter().GetResult();
                    ok = true;
                    break;
                }
                catch (OperationCanceledException) { return false; }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[VoiceService] 下载失败 {sourceName}: {ex.Message}");
                    StatusChanged?.Invoke($"  ✗ {sourceName} 失败: {ex.Message}");
                }
            }

            if (!ok)
            {
                Error?.Invoke($"下载 {fileName} 失败，请检查网络后重试");
                return false;
            }
        }

        StatusChanged?.Invoke("所有模型就绪");
        return true;
    }

    private async Task DownloadFileAsync(HttpClient http, string url, string outPath,
        string label, CancellationToken ct)
    {
        // 清理残留
        if (File.Exists(outPath))
        {
            try { File.Delete(outPath); } catch { }
        }

        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        // 检查是否被重定向到 HTML 错误页
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
        var totalBytes = response.Content.Headers.ContentLength ?? -1;
        if (contentType.Contains("text/html") && totalBytes < 1024)
            throw new Exception("服务器返回 HTML 错误页");

        await using var fileStream = new FileStream(outPath, FileMode.Create, FileAccess.Write,
            FileShare.None, 65536, useAsync: true);
        await using var httpStream = await response.Content.ReadAsStreamAsync(ct);

        var buffer = new byte[65536];
        long downloaded = 0;
        int lastPct = -1;

        while (true)
        {
            int read = await httpStream.ReadAsync(buffer, ct);
            if (read == 0) break;

            await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
            downloaded += read;

            if (totalBytes > 0)
            {
                int pct = (int)Math.Min(99, downloaded * 100 / totalBytes);
                if (pct != lastPct && pct % 5 == 0)
                {
                    StatusChanged?.Invoke($"下载 {label} {pct}%…");
                    lastPct = pct;
                }
            }
        }

        var sizeMb = new FileInfo(outPath).Length / (1024.0 * 1024.0);
        StatusChanged?.Invoke($"  ✓ {label} ({sizeMb:F0}MB) 下载完成");
    }

    // ══════════════════════════════════════════════
    //  工具方法
    // ══════════════════════════════════════════════

    private static float CalculateRms(float[] samples)
    {
        if (samples.Length == 0) return 0;
        double sum = 0;
        for (int i = 0; i < samples.Length; i++)
            sum += (double)samples[i] * samples[i];
        return (float)Math.Sqrt(sum / samples.Length);
    }
}
