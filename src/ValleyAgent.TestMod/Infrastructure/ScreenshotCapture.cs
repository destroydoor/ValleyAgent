#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace ValleyAgent.TestMod.Infrastructure;

/// <summary>
///     Screenshot capture utility for visual tests.
///     Key design decisions:
///     1. All captures are stored by (testName, label) key in dictionaries,
///     eliminating the single-slot overwrite bug that caused multi-capture tests
///     to analyze the wrong screenshot.
///     2. World screenshots use GetBackBufferData() called from ProcessCaptures()
///     (invoked during game Update), NOT from RenderedHud event, because
///     GetBackBufferData() returns black pixels when called during RenderedHud.
///     3. Menu screenshots use the proven CaptureMenu() offscreen render target approach.
///     4. Video recording streams PNG frames to an ffmpeg subprocess via stdin,
///     producing a single MP4 directly — no intermediate PNG files on disk.
/// </summary>
public sealed class ScreenshotCapture : IDisposable
{
    private static readonly JsonSerializerOptions s_manifestJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    ///     Cached ffmpeg executable path (computed once, thread-safely). null
    ///     means ffmpeg was not found on PATH or in common install locations.
    /// </summary>
    private static readonly Lazy<string?> s_cachedFfmpegPath =
        new(FindFfmpegCore, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    ///     Common Windows install locations to probe when ffmpeg is not on PATH.
    /// </summary>
    private static readonly string[] s_windowsCommonPaths =
    {
        @"C:\ffmpeg\bin\ffmpeg.exe",
        @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
        @"C:\Program Files (x86)\ffmpeg\bin\ffmpeg.exe",
        @"C:\ProgramData\chocolatey\bin\ffmpeg.exe",
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "ffmpeg", "bin", "ffmpeg.exe")
    };

    /// <summary>
    ///     Common Unix install locations to probe when ffmpeg is not on PATH.
    /// </summary>
    private static readonly string[] s_unixCommonPaths =
    {
        "/usr/bin/ffmpeg",
        "/usr/local/bin/ffmpeg",
        "/opt/homebrew/bin/ffmpeg"
    };

    /// <summary>Newline characters used to split multi-line command output.</summary>
    private static readonly char[] s_newLineChars = { '\r', '\n' };

    private readonly IMonitor _monitor;

    private readonly string _outputDir;

    // ── Pending capture queue ──
    // Requests are queued by Request() and processed by ProcessCaptures()
    // which is called from VisualTestRunner.Update() during the game Update phase.
    private readonly List<(string testName, string label)> _pendingCaptures = new();

    // ── Named capture storage ──
    private readonly Dictionary<string, ScreenshotResult> _results = new();
    private readonly string _visualAnalysisDir;
    private string? _currentRecordingLabel;

    // ── Visual-analysis recording state ──
    // Mirrors the legacy fields under the names required by the visual-analysis
    // pipeline and tracks additional data for CaptureAtInterval / UpdateRecording.
    private string? _currentRecordingTestName;
    private bool _ffmpegAvailable;
    private string? _ffmpegLogPath;

    // ── ffmpeg streaming state ──
    // The ffmpeg subprocess receives PNG frames via stdin and produces a single
    // MP4 at _recordingOutputPath. Frames are written by CaptureRecordingFrame().
    private Process? _ffmpegProcess;
    private int _frameCounter;
    private int _intervalCallCounter;

    // ── Recording state ──

    // ── Backward compatibility ──
    private string? _lastCaptureKey;
    private int _lastIntervalCaptureTick = -1;

    // ── RenderedHud flag ──
    // Set during RenderedHud to signal that we need to process captures on next Update.
    // We do NOT read backbuffer during RenderedHud because GetBackBufferData() returns
    // black pixels at that point in the frame.
    private bool _needsCapture;
    private int _recordingFrameIndex;
    private int _recordingHeight;
    private int _recordingInterval;
    private string? _recordingLabel;
    private int _recordingMaxFrames;
    private string? _recordingTestName;
    private int _recordingTickCounter;
    private int _recordingWidth;
    private Task? _stderrDrainTask;

    public ScreenshotCapture(IModHelper helper, IMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(helper);
        ArgumentNullException.ThrowIfNull(monitor);
        _monitor = monitor;
        var projectRoot = V3TestBase.FindProjectRoot(helper);
        _outputDir = Path.Combine(projectRoot, "logs", "screenshots");
        _visualAnalysisDir = Path.Combine(projectRoot, "logs", "visual_analysis");
        _ = Directory.CreateDirectory(_outputDir);
        _ = Directory.CreateDirectory(_visualAnalysisDir);
    }

    public bool IsRecording { get; private set; }

    public int RecordingFrameCount { get; private set; }

    /// <summary>
    ///     Current MP4 output path when recording is active, otherwise null.
    ///     Points at the MP4 file being produced by the ffmpeg subprocess.
    /// </summary>
    public string? CurrentRecordingPath { get; private set; }

    /// <summary>
    ///     Check whether ALL pending captures have completed.
    /// </summary>
    public bool AllCapturesComplete
    {
        get => _pendingCaptures.Count == 0 && _lastCaptureKey != null;
    }

    public void Dispose()
    {
        if (IsRecording)
        {
            _ = StopRecording();
        }

        var proc = _ffmpegProcess;
        if (proc != null)
        {
            try
            {
                if (!proc.HasExited)
                {
                    try
                    {
                        proc.Kill(true);
                    }
                    catch (InvalidOperationException)
                    {
                    }
                    catch (Win32Exception)
                    {
                    }
                }

                proc.Dispose();
            }
            catch (InvalidOperationException)
            {
            }
            catch (Win32Exception)
            {
            }

            _ffmpegProcess = null;
        }
    }

    /// <summary>Generate a unique key for a (testName, label) pair.</summary>
    private static string CaptureKey(string testName, string label) => $"{testName}::{label}";

    // ── Request-based capture (world rendering) ──

    /// <summary>
    ///     Queue a world-render screenshot to be captured on the next ProcessCaptures() call.
    ///     The result is stored by (testName, label) and can be retrieved with
    ///     <see cref="GetResult" />.
    /// </summary>
    public void Request(string testName, string label)
    {
        _pendingCaptures.Add((testName, label));
        // Remove any previous result with the same key (allow re-captures)
        var key = CaptureKey(testName, label);
        _results.Remove(key);
    }

    /// <summary>
    ///     Check whether a specific named capture has completed.
    /// </summary>
    public bool IsCaptureComplete(string testName, string label)
    {
        var key = CaptureKey(testName, label);
        return _results.ContainsKey(key);
    }

    /// <summary>
    ///     Get the result of a specific named capture.
    /// </summary>
    public ScreenshotResult? GetResult(string testName, string label)
    {
        var key = CaptureKey(testName, label);
        return _results.TryGetValue(key, out var r) ? r : null;
    }

    // ── Backward compatible accessors ──

    /// <summary>
    ///     Get the result of the most recent capture (backward compatible).
    /// </summary>
    public ScreenshotResult? GetLastResult()
    {
        if (_lastCaptureKey != null && _results.TryGetValue(_lastCaptureKey, out var r))
        {
            return r;
        }

        return null;
    }

    /// <summary>
    ///     Get all saved screenshot information as (testName, label, path) tuples.
    ///     Used for post-test multimodal visual analysis.
    /// </summary>
    public List<(string TestName, string Label, string Path)> GetAllSavedScreenshots()
    {
        var list = new List<(string, string, string)>();
        foreach (var kvp in _results)
        {
            if (kvp.Value.Success && kvp.Value.Path != null)
            {
                // Parse key format: "testName::label"
                var parts = kvp.Key.Split("::", 2);
                var testName = parts.Length > 0 ? parts[0] : kvp.Key;
                var label = parts.Length > 1 ? parts[1] : "unknown";
                list.Add((testName, label, kvp.Value.Path));
            }
        }

        return list;
    }

    // ── Menu capture (offscreen render target — proven reliable) ──

    /// <summary>
    ///     Capture an IClickableMenu by rendering it to an offscreen render target.
    ///     This approach works because it doesn't depend on backbuffer state.
    ///     Called synchronously from game Update(), where GetBackBufferData() works.
    /// </summary>
    public void CaptureMenu(IClickableMenu menu, string testName, string label)
    {
        ArgumentNullException.ThrowIfNull(menu);

        try
        {
            var device = Game1.graphics.GraphicsDevice;
            var width = device.PresentationParameters.BackBufferWidth;
            var height = device.PresentationParameters.BackBufferHeight;

            // Capture world background from backbuffer (works during Update phase)
            var worldData = new Color[width * height];
            device.GetBackBufferData(worldData);
            var worldTexture = new Texture2D(device, width, height);
            worldTexture.SetData(worldData);

            using var rt = new RenderTarget2D(device, width, height, false,
                device.PresentationParameters.BackBufferFormat, DepthFormat.None);

            device.SetRenderTarget(rt);
            device.Clear(Color.Transparent);

            using var sb = new SpriteBatch(device);

            // Draw world background first
            sb.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied);
            sb.Draw(worldTexture, Vector2.Zero, Color.White);
            sb.End();

            // Draw menu on top
            sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
            menu.draw(sb);
            sb.End();

            device.SetRenderTarget(null);
            worldTexture.Dispose();

            var safeName = $"{testName}_{label}_menu".Replace(' ', '_');
            var filename = $"{safeName}_{DateTime.Now:HHmmss}.png";
            var path = Path.Combine(_outputDir, filename);

            using (var stream = File.Create(path))
            {
                rt.SaveAsPng(stream, width, height);
            }

            var key = CaptureKey(testName, label);
            _results[key] = new ScreenshotResult(path, width, height);
            _lastCaptureKey = key;
            _monitor.Log($"[ScreenshotCapture] Menu saved: {path}", LogLevel.Info);
        }
        catch (IOException ex)
        {
            RecordMenuCaptureError(ex, testName, label);
        }
        catch (UnauthorizedAccessException ex)
        {
            RecordMenuCaptureError(ex, testName, label);
        }
        catch (InvalidOperationException ex)
        {
            RecordMenuCaptureError(ex, testName, label);
        }
        catch (ArgumentException ex)
        {
            RecordMenuCaptureError(ex, testName, label);
        }
    }

    private void RecordMenuCaptureError(Exception ex, string testName, string label)
    {
        var key = CaptureKey(testName, label);
        _results[key] = new ScreenshotResult(null, 0, 0, ex.Message);
        _lastCaptureKey = key;
        _monitor.Log($"[ScreenshotCapture] Menu capture failed: {ex.Message}", LogLevel.Warn);
    }

    // ── Process pending captures from Update phase ──

    /// <summary>
    ///     Process all pending capture requests by reading the backbuffer.
    ///     MUST be called during game Update(), NOT during RenderedHud,
    ///     because GetBackBufferData() returns black during RenderedHud.
    ///     Also drives video recording frame capture when recording is active.
    /// </summary>
    public void ProcessCaptures()
    {
        // Drive recording frame capture every tick. Recording maintains its own
        // tick counter and interval; this does not interfere with single-frame
        // captures queued via Request().
        if (IsRecording)
        {
            UpdateRecording();
        }

        if (_pendingCaptures.Count == 0 && !_needsCapture)
        {
            return;
        }

        _needsCapture = false;

        if (_pendingCaptures.Count == 0)
        {
            return;
        }

        try
        {
            var device = Game1.graphics.GraphicsDevice;
            var width = device.PresentationParameters.BackBufferWidth;
            var height = device.PresentationParameters.BackBufferHeight;

            var backBufferData = new Color[width * height];
            device.GetBackBufferData(backBufferData);

            foreach (var (testName, label) in _pendingCaptures)
            {
                var key = CaptureKey(testName, label);
                var safeName = $"{testName}_{label}".Replace(' ', '_');
                var filename = $"{safeName}_{DateTime.Now:HHmmss}.png";
                var path = Path.Combine(_outputDir, filename);

                try
                {
                    using var tex = new Texture2D(device, width, height);
                    tex.SetData(backBufferData);
                    using (var stream = File.Create(path))
                    {
                        tex.SaveAsPng(stream, width, height);
                    }

                    _results[key] = new ScreenshotResult(path, width, height);
                    _lastCaptureKey = key;
                    _monitor.Log($"[ScreenshotCapture] Saved: {path}", LogLevel.Info);
                }
                catch (IOException ex)
                {
                    RecordSaveError(ex, key, safeName);
                }
                catch (UnauthorizedAccessException ex)
                {
                    RecordSaveError(ex, key, safeName);
                }
                catch (InvalidOperationException ex)
                {
                    RecordSaveError(ex, key, safeName);
                }
                catch (ArgumentException ex)
                {
                    RecordSaveError(ex, key, safeName);
                }
            }

            _pendingCaptures.Clear();
        }
        catch (InvalidOperationException ex)
        {
            RecordBackbufferError(ex);
        }
        catch (ArgumentException ex)
        {
            RecordBackbufferError(ex);
        }
        catch (NullReferenceException ex)
        {
            RecordBackbufferError(ex);
        }
        catch (OutOfMemoryException ex)
        {
            RecordBackbufferError(ex);
        }
    }

    private void RecordSaveError(Exception ex, string key, string safeName)
    {
        _results[key] = new ScreenshotResult(null, 0, 0, ex.Message);
        _lastCaptureKey = key;
        _monitor.Log($"[ScreenshotCapture] Failed to save {safeName}: {ex.Message}", LogLevel.Warn);
    }

    private void RecordBackbufferError(Exception ex)
    {
        foreach (var (testName, label) in _pendingCaptures)
        {
            var key = CaptureKey(testName, label);
            _results[key] = new ScreenshotResult(null, 0, 0, ex.Message);
            _lastCaptureKey = key;
        }

        _pendingCaptures.Clear();
        _monitor.Log($"[ScreenshotCapture] Backbuffer read failed: {ex.Message}", LogLevel.Warn);
    }

    /// <summary>
    ///     Called from RenderedHud event. Does NOT capture — just sets a flag
    ///     to process captures on the next Update tick.
    /// </summary>
    public void OnFrameRendered()
    {
        if (_pendingCaptures.Count > 0)
        {
            // Signal that we need to process captures on next Update
            _needsCapture = true;
        }

        // Handle recording frames (unchanged)
        if (IsRecording)
        {
            _recordingTickCounter++;
            // Recording frames are processed separately in Update phase
        }
    }

    /// <summary>
    ///     Process recording frames during Update phase (same as captures).
    ///     Relies on <see cref="OnFrameRendered" /> to increment the tick counter
    ///     between captures. The counter resets each time a frame is captured.
    /// </summary>
    public void ProcessRecording()
    {
        if (!IsRecording || _ffmpegProcess == null)
        {
            return;
        }

        if (_recordingTickCounter < _recordingInterval)
        {
            return;
        }

        _recordingTickCounter = 0;
        CaptureRecordingFrame();
    }

    /// <summary>
    ///     Self-contained recording update: increments its own tick counter and
    ///     captures a frame when the configured interval elapses. Call this from
    ///     the same Update phase as <see cref="ProcessCaptures" />; it does NOT
    ///     depend on <see cref="OnFrameRendered" /> being called first.
    ///     Do not call both <see cref="ProcessRecording" /> and
    ///     <see cref="UpdateRecording" /> on the same tick — both advance the
    ///     shared <c>_recordingTickCounter</c> and would double the capture rate.
    /// </summary>
    public void UpdateRecording()
    {
        if (!IsRecording || _ffmpegProcess == null)
        {
            return;
        }

        _recordingTickCounter++;
        if (_recordingTickCounter < _recordingInterval)
        {
            return;
        }

        _recordingTickCounter = 0;
        CaptureRecordingFrame();
    }

    /// <summary>
    ///     Capture a single frame and pipe it to the ffmpeg subprocess as PNG
    ///     bytes via stdin. Shared by <see cref="ProcessRecording" /> and
    ///     <see cref="UpdateRecording" />. If ffmpeg has exited (crashed or was
    ///     closed), the recording is finalized and no further frames are captured.
    /// </summary>
    private void CaptureRecordingFrame()
    {
        var proc = _ffmpegProcess;
        if (proc == null)
        {
            return;
        }

        if (proc.HasExited)
        {
            _monitor.Log("[ScreenshotCapture] ffmpeg process exited unexpectedly; finalizing recording.",
                LogLevel.Warn);
            _ = StopRecording();
            return;
        }

        try
        {
            var device = Game1.graphics.GraphicsDevice;
            var width = device.PresentationParameters.BackBufferWidth;
            var height = device.PresentationParameters.BackBufferHeight;

            var data = new Color[width * height];
            device.GetBackBufferData(data);

            _recordingFrameIndex++;
            _frameCounter++;

            using var tex = new Texture2D(device, width, height);
            tex.SetData(data);

            // Save frame as PNG to a MemoryStream, then pipe the bytes to
            // ffmpeg's stdin. image2pipe demuxer expects concatenated PNG files.
            using var ms = new MemoryStream();
            tex.SaveAsPng(ms, width, height);
            var bytes = ms.GetBuffer();
            var length = (int)ms.Length;

            proc.StandardInput.BaseStream.Write(bytes, 0, length);
            proc.StandardInput.BaseStream.Flush();

            RecordingFrameCount++;

            if (RecordingFrameCount >= _recordingMaxFrames)
            {
                _monitor.Log($"[ScreenshotCapture] Recording reached max frames ({_recordingMaxFrames}), stopping.",
                    LogLevel.Info);
                _ = StopRecording();
            }
        }
        catch (InvalidOperationException ex)
        {
            _monitor.Log($"[ScreenshotCapture] Recording frame failed: {ex.Message}", LogLevel.Warn);
        }
        catch (IOException ex)
        {
            _monitor.Log($"[ScreenshotCapture] Recording frame failed (pipe write): {ex.Message}", LogLevel.Warn);
            // ffmpeg stdin pipe is broken — finalize the recording.
            _ = StopRecording();
        }
        catch (ArgumentException ex)
        {
            _monitor.Log($"[ScreenshotCapture] Recording frame failed: {ex.Message}", LogLevel.Warn);
        }
    }

    /// <summary>
    ///     Schedule a periodic screenshot. Call this every Update tick; an actual
    ///     capture is queued via <see cref="Request" /> only when
    ///     <paramref name="intervalTicks" /> ticks have elapsed since the last
    ///     interval capture. Independent from video recording — works whether or
    ///     not <see cref="StartRecording(string,string,int,int)" /> is active.
    /// </summary>
    public void CaptureAtInterval(int intervalTicks, string label)
    {
        if (intervalTicks <= 0)
        {
            return;
        }

        _intervalCallCounter++;

        if (_lastIntervalCaptureTick < 0 ||
            _intervalCallCounter - _lastIntervalCaptureTick >= intervalTicks)
        {
            _lastIntervalCaptureTick = _intervalCallCounter;
            var testName = _currentRecordingTestName ?? _recordingTestName ?? "interval";
            Request(testName, label);
        }
    }

    // ── Recording (ffmpeg streaming) ──

    /// <summary>
    ///     Begin a streaming video recording. Launches an ffmpeg subprocess
    ///     that reads PNG frames from stdin and encodes them to a single MP4
    ///     at <c>logs/visual_analysis/{timestamp}/{testName}/{label}.mp4</c>.
    ///     Frames are captured on subsequent <see cref="ProcessRecording" /> /
    ///     <see cref="UpdateRecording" /> calls (the latter is driven automatically
    ///     by <see cref="ProcessCaptures" /> when recording is active).
    ///     If ffmpeg is not available, the recording is silently disabled —
    ///     <see cref="StopRecording" /> will return null and tests should treat
    ///     the visual assertion as skipped.
    ///     Defaults: <paramref name="intervalFrames" />=5, <paramref name="maxFrames" />=600.
    /// </summary>
    public void StartRecording(string testName, string label, int intervalFrames, int maxFrames)
    {
        if (IsRecording)
        {
            _ = StopRecording();
        }

        _recordingTestName = testName;
        _recordingLabel = label;
        _currentRecordingTestName = testName;
        _currentRecordingLabel = label;
        _recordingInterval = Math.Max(1, intervalFrames);
        _recordingMaxFrames = maxFrames;
        _recordingFrameIndex = 0;
        _frameCounter = 0;
        _recordingTickCounter = 0;
        RecordingFrameCount = 0;

        // Cache backbuffer dimensions so all frames are the same size (ffmpeg
        // image2pipe requires consistent dimensions).
        var device = Game1.graphics.GraphicsDevice;
        _recordingWidth = device.PresentationParameters.BackBufferWidth;
        _recordingHeight = device.PresentationParameters.BackBufferHeight;

        // Output MP4 path: logs/visual_analysis/{timestamp}/{testName}/{label}.mp4
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var safeTest = SanitizePathSegment(testName);
        var safeLabel = SanitizePathSegment(label);
        var outputDir = Path.Combine(_visualAnalysisDir, timestamp, safeTest);
        _ = Directory.CreateDirectory(outputDir);
        var outputPath = Path.Combine(outputDir, $"{safeLabel}.mp4");

        var intervalFramesSafe = _recordingInterval;
        var fps = 60.0 / intervalFramesSafe;

        if (!EnsureFfmpegAvailable())
        {
            _monitor.Log("[ScreenshotCapture] ffmpeg not found, recording disabled.", LogLevel.Error);
            IsRecording = false;
            CurrentRecordingPath = null;
            return;
        }

        try
        {
            StartFfmpegProcess(outputPath, (int)Math.Round(fps), _recordingWidth, _recordingHeight);
        }
        catch (InvalidOperationException ex)
        {
            _monitor.Log($"[ScreenshotCapture] Failed to start ffmpeg: {ex.Message}", LogLevel.Error);
            IsRecording = false;
            CurrentRecordingPath = null;
            _ffmpegProcess = null;
            return;
        }
        catch (Win32Exception ex)
        {
            _monitor.Log(
                $"[ScreenshotCapture] Failed to start ffmpeg: {ex.Message} (NativeErrorCode={ex.NativeErrorCode})",
                LogLevel.Error);
            IsRecording = false;
            CurrentRecordingPath = null;
            _ffmpegProcess = null;
            return;
        }

        CurrentRecordingPath = outputPath;
        IsRecording = true;
        _monitor.Log(
            $"[ScreenshotCapture] Recording started: {outputPath} " +
            $"(interval={intervalFrames}, max={maxFrames}, fps={fps:F2}, {_recordingWidth}x{_recordingHeight})",
            LogLevel.Info);
    }

    /// <summary>
    ///     Convenience overload that begins a recording with default
    ///     <paramref name="intervalFrames" />=5 and <paramref name="maxFrames" />=600.
    /// </summary>
    public void StartRecording(string testName, string label)
        => StartRecording(testName, label, 5, 600);

    /// <summary>
    ///     Stop the active recording. Closes ffmpeg's stdin (signaling EOF),
    ///     waits for ffmpeg to finalize the MP4, and verifies the output file.
    ///     On success returns the MP4 path; on any failure (ffmpeg crashed, no
    ///     output file, empty file) returns null. A small <c>manifest.json</c>
    ///     is written next to the MP4 on success.
    /// </summary>
    public string? StopRecording()
    {
        if (!IsRecording)
        {
            return null;
        }

        IsRecording = false;

        var proc = _ffmpegProcess;
        var outputPath = CurrentRecordingPath;
        var testName = _recordingTestName ?? "unknown";
        var label = _recordingLabel ?? "unknown";
        var frameCount = RecordingFrameCount;
        var intervalFrames = _recordingInterval > 0 ? _recordingInterval : 1;
        var fps = 60.0 / intervalFrames;

        if (proc == null || outputPath == null)
        {
            ClearRecordingState();
            return null;
        }

        // Close stdin to signal EOF to ffmpeg, then wait for it to finalize.
        try
        {
            proc.StandardInput.BaseStream.Flush();
            proc.StandardInput.BaseStream.Close();
        }
        catch (ObjectDisposedException)
        {
            // Already closed — ignore.
        }
        catch (InvalidOperationException ex)
        {
            _monitor.Log($"[ScreenshotCapture] Error closing ffmpeg stdin: {ex.Message}", LogLevel.Warn);
        }
        catch (IOException ex)
        {
            _monitor.Log($"[ScreenshotCapture] Error closing ffmpeg stdin: {ex.Message}", LogLevel.Warn);
        }

        var exited = false;
        try
        {
            exited = proc.WaitForExit(10_000);
            if (!exited)
            {
                _monitor.Log("[ScreenshotCapture] ffmpeg did not exit within 10s; killing.", LogLevel.Warn);
                try
                {
                    proc.Kill(true);
                }
                catch (InvalidOperationException)
                {
                }
                catch (Win32Exception)
                {
                }

                try
                {
                    proc.WaitForExit(2_000);
                }
                catch (InvalidOperationException)
                {
                }
            }
        }
        catch (InvalidOperationException ex)
        {
            _monitor.Log($"[ScreenshotCapture] Error waiting for ffmpeg exit: {ex.Message}", LogLevel.Warn);
        }

        // Wait briefly for the stderr drain task to finish writing the log.
        if (_stderrDrainTask != null && !_stderrDrainTask.IsCompleted)
        {
            try
            {
                _stderrDrainTask.Wait(5_000);
            }
            catch (AggregateException)
            {
            }
        }

        // Verify output file exists and is non-empty.
        var success = false;
        try
        {
            success = exited && proc.ExitCode == 0
                             && File.Exists(outputPath)
                             && new FileInfo(outputPath).Length > 0;
        }
        catch (InvalidOperationException)
        {
            success = false;
        }
        catch (IOException)
        {
            success = false;
        }
        catch (UnauthorizedAccessException)
        {
            success = false;
        }

        try
        {
            proc.Dispose();
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }

        _ffmpegProcess = null;
        _stderrDrainTask = null;

        if (success)
        {
            WriteRecordingManifest(outputPath, testName, label, frameCount, fps);
            _monitor.Log(
                $"[ScreenshotCapture] Recording stopped: {frameCount} frames → {outputPath} (exit=0)",
                LogLevel.Info);
            ClearRecordingState();
            return outputPath;
        }

        _monitor.Log(
            $"[ScreenshotCapture] Recording failed (exit={exited}, path={outputPath}). " +
            "MP4 may be missing or empty.",
            LogLevel.Warn);

        // Clean up partial/empty MP4.
        try
        {
            if (File.Exists(outputPath) && new FileInfo(outputPath).Length == 0)
            {
                File.Delete(outputPath);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        ClearRecordingState();
        return null;
    }

    private void ClearRecordingState()
    {
        _recordingTestName = null;
        _recordingLabel = null;
        CurrentRecordingPath = null;
        _currentRecordingTestName = null;
        _currentRecordingLabel = null;
        _ffmpegLogPath = null;
    }

    /// <summary>
    ///     Write <c>manifest.json</c> next to the MP4 file. Captured metadata
    ///     describes the recording for downstream analysis tools.
    /// </summary>
    private void WriteRecordingManifest(
        string mp4Path, string testName, string label, int frameCount, double fps)
    {
        if (string.IsNullOrEmpty(mp4Path))
        {
            return;
        }

        try
        {
            var dir = Path.GetDirectoryName(mp4Path);
            if (string.IsNullOrEmpty(dir))
            {
                return;
            }

            var manifest = new
            {
                test_name = testName,
                label,
                frame_count = frameCount,
                fps = Math.Round(fps, 2),
                interval_frames = _recordingInterval,
                max_frames = _recordingMaxFrames,
                mp4_path = mp4Path,
                width = _recordingWidth,
                height = _recordingHeight,
                created_at = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss")
            };
            var manifestJson = JsonSerializer.Serialize(manifest, s_manifestJsonOptions);
            var manifestPath = Path.ChangeExtension(mp4Path, ".manifest.json");
            File.WriteAllText(manifestPath, manifestJson);
            _monitor.Log($"[ScreenshotCapture] Manifest written: {manifestPath}", LogLevel.Info);
        }
        catch (IOException ex)
        {
            _monitor.Log($"[ScreenshotCapture] Manifest write failed: {ex.Message}", LogLevel.Warn);
        }
        catch (UnauthorizedAccessException ex)
        {
            _monitor.Log($"[ScreenshotCapture] Manifest write failed: {ex.Message}", LogLevel.Warn);
        }
        catch (JsonException ex)
        {
            _monitor.Log($"[ScreenshotCapture] Manifest write failed: {ex.Message}", LogLevel.Warn);
        }
        catch (InvalidOperationException ex)
        {
            _monitor.Log($"[ScreenshotCapture] Manifest write failed: {ex.Message}", LogLevel.Warn);
        }
    }

    // ── ffmpeg discovery and process management ──

    /// <summary>
    ///     Return the cached ffmpeg path (null if not found). The lookup is
    ///     performed once and cached thread-safely via <see cref="Lazy{T}" />.
    /// </summary>
    private static string? FindFfmpeg() => s_cachedFfmpegPath.Value;

    /// <summary>
    ///     Search PATH (via <c>where</c> on Windows / <c>which</c> on Unix) and
    ///     common install locations for the ffmpeg executable. Returns the
    ///     absolute path on success, or null if ffmpeg cannot be located.
    /// </summary>
    private static string? FindFfmpegCore()
    {
        var finder = OperatingSystem.IsWindows() ? "where" : "which";
        var found = ProbePathLookup(finder);
        if (found != null && File.Exists(found))
        {
            return found;
        }

        foreach (var p in OperatingSystem.IsWindows() ? s_windowsCommonPaths : s_unixCommonPaths)
        {
            if (File.Exists(p))
            {
                return p;
            }
        }

        return null;
    }

    /// <summary>
    ///     Run <c>where ffmpeg</c> / <c>which ffmpeg</c> and return the first
    ///     match (or null). All failures are swallowed — this is a best-effort
    ///     probe and missing <c>where</c>/<c>which</c> is treated as
    ///     "ffmpeg not on PATH".
    /// </summary>
    private static string? ProbePathLookup(string finder)
    {
        try
        {
            // 参数走 ArgumentList（无 shell 参数边界）；finder 是内部常量 where/which
            var psi = new ProcessStartInfo
            {
                FileName = finder,
                ArgumentList = { "ffmpeg" },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null)
            {
                return null;
            }

            var stdout = proc.StandardOutput.ReadToEnd();
            // Wait briefly for exit; if it times out, treat as not found.
            if (!proc.WaitForExit(2000))
            {
                try
                {
                    proc.Kill(true);
                }
                catch (InvalidOperationException)
                {
                    // ignore — best-effort kill
                }

                return null;
            }

            if (proc.ExitCode != 0)
            {
                return null;
            }

            var lines = stdout.Split(s_newLineChars, StringSplitOptions.RemoveEmptyEntries);
            return lines.Length > 0 ? lines[0].Trim() : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (Win32Exception)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Check ffmpeg availability (cached). Returns true if ffmpeg was found.
    ///     On first call, logs a warning if ffmpeg is not available.
    /// </summary>
    private bool EnsureFfmpegAvailable()
    {
        if (_ffmpegAvailable)
        {
            return true;
        }

        var path = FindFfmpeg();
        if (path == null)
        {
            _ffmpegAvailable = false;
            return false;
        }

        _ffmpegAvailable = true;
        return true;
    }

    /// <summary>
    ///     Launch the ffmpeg subprocess configured to read PNG frames from stdin
    ///     and encode them to H.264 MP4. Stores the process in <see cref="_ffmpegProcess" />.
    ///     Throws on failure to start.
    /// </summary>
    private void StartFfmpegProcess(string outputPath, int fps, int width, int height)
    {
        var ffmpeg = FindFfmpeg()
                     ?? throw new InvalidOperationException("ffmpeg not available");

        // Args:
        //   -y                        overwrite output
        //   -f image2pipe             read PNG images from stdin pipe
        //   -vcodec png               input codec (PNG)
        //   -r {fps}                  input framerate
        //   -i -                      read from stdin
        //   -c:v libx264              encode to H.264
        //   -pix_fmt yuv420p          pixel format (broad compatibility)
        //   -vf scale={w}:{h}         enforce consistent dimensions
        //   "{outputPath}"            output MP4
        var args =
            $"-y -f image2pipe -vcodec png -r {fps} -i - " +
            $"-c:v libx264 -pix_fmt yuv420p -vf scale={width}:{height} " +
            $"\"{outputPath}\"";

        var psi = new ProcessStartInfo(ffmpeg, args)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = false,
            CreateNoWindow = true
        };

        var proc = Process.Start(psi)
                   ?? throw new InvalidOperationException("Process.Start returned null for ffmpeg");

        _ffmpegProcess = proc;

        // Drain stderr asynchronously to avoid buffer-deadlock and capture it
        // to a log file for debugging. The task completes naturally when ffmpeg
        // closes stderr (i.e. when it exits).
        _ffmpegLogPath = Path.ChangeExtension(outputPath, ".ffmpeg.log");
        var logPath = _ffmpegLogPath;
        _stderrDrainTask = Task.Run(() =>
        {
            try
            {
                var stderr = proc.StandardError.ReadToEnd();
                try
                {
                    File.WriteAllText(logPath, stderr);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
                catch (InvalidOperationException)
                {
                }
            }
            catch (ObjectDisposedException)
            {
                // Process already disposed — ignore.
            }
            catch (InvalidOperationException)
            {
                // Process already exited — ignore.
            }
        });

        _monitor.Log($"[ScreenshotCapture] ffmpeg started: {ffmpeg} {args}", LogLevel.Debug);
    }

    /// <summary>
    ///     Replace characters invalid in path segments with underscores so the
    ///     testName / label can be used directly as directory / file names.
    /// </summary>
    private static string SanitizePathSegment(string segment)
    {
        if (string.IsNullOrEmpty(segment))
        {
            return "unknown";
        }

        var invalid = Path.GetInvalidPathChars();
        var replaced = segment;
        foreach (var c in invalid)
        {
            replaced = replaced.Replace(c, '_');
        }

        return replaced.Replace(' ', '_');
    }
}