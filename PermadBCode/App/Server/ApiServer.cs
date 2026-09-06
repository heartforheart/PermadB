using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PermadB.Audio;
using PermadB.Config;

namespace PermadB.Server;

public class ApiServer : IDisposable
{
    private readonly ConfigManager _config;
    private readonly AudioEngine _engine;
    private readonly HttpListener _listener;
    private readonly string _wwwroot;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentBag<HttpListenerResponse> _sseClients = new();
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public int Port => _config.Settings.ApiPort;

    public ApiServer(ConfigManager config, AudioEngine engine, string? customWwwroot = null)
    {
        _config = config;
        _engine = engine;
        _wwwroot = customWwwroot ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot");

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");

        _engine.OnMetricsUpdated += BroadcastMetricsToSse;
    }

    public void Start()
    {
        try
        {
            _listener.Start();
            Console.WriteLine($"[ApiServer] Listening at http://127.0.0.1:{Port}/");
            Task.Run(() => AcceptLoop(_cts.Token));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ApiServer] Error starting server: {ex.Message}");
        }
    }

    private async Task AcceptLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _listener.IsListening)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleRequest(context));
            }
            catch (HttpListenerException) when (token.IsCancellationRequested || !_listener.IsListening)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ApiServer] Request error: {ex.Message}");
            }
        }
    }

    private async Task HandleRequest(HttpListenerContext context)
    {
        var req = context.Request;
        var res = context.Response;

        // CORS headers
        res.AddHeader("Access-Control-Allow-Origin", "*");
        res.AddHeader("Access-Control-Allow-Methods", "GET, POST, PUT, DELETE, OPTIONS");
        res.AddHeader("Access-Control-Allow-Headers", "Content-Type, Authorization");

        if (req.HttpMethod == "OPTIONS")
        {
            res.StatusCode = 200;
            res.Close();
            return;
        }

        var path = req.Url?.AbsolutePath.TrimEnd('/') ?? "";

        try
        {
            if (path == "/api/status" && req.HttpMethod == "GET")
            {
                await SendJson(res, new
                {
                    metrics = _engine.LatestMetrics,
                    settings = _config.Settings,
                    activeProfile = _engine.GetActiveProfile(),
                    isStartupEnabled = StartupManager.IsStartupEnabled()
                });
                return;
            }

            if (path == "/api/settings" && req.HttpMethod == "POST")
            {
                using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
                var body = await reader.ReadToEndAsync();
                var incoming = JsonSerializer.Deserialize<JsonElement>(body);

                if (incoming.TryGetProperty("enabled", out var enabledProp))
                {
                    _config.Settings.Enabled = enabledProp.GetBoolean();
                    if (_config.Settings.Enabled) _engine.EnforceVolumeCeiling();
                }

                if (incoming.TryGetProperty("activePreset", out var presetProp))
                {
                    _engine.ApplyPreset(presetProp.GetString() ?? "safe");
                }

                if (incoming.TryGetProperty("safeCeilingPercent", out var ceilingProp))
                {
                    _engine.SetSafeCeiling((float)ceilingProp.GetDouble());
                }

                if (incoming.TryGetProperty("dynamicLimiterEnabled", out var dynProp))
                {
                    _config.Settings.DynamicLimiterEnabled = dynProp.GetBoolean();
                }

                if (incoming.TryGetProperty("strictVolumeLock", out var strictProp))
                {
                    _config.Settings.StrictVolumeLock = strictProp.GetBoolean();
                    if (_config.Settings.StrictVolumeLock) _engine.EnforceVolumeCeiling();
                }

                if (incoming.TryGetProperty("startWithWindows", out var startProp))
                {
                    var target = startProp.GetBoolean();
                    StartupManager.SetStartup(target);
                    _config.Settings.StartWithWindows = target;
                }

                if (incoming.TryGetProperty("applyToAllDevices", out var allProp))
                {
                    _config.Settings.ApplyToAllDevices = allProp.GetBoolean();
                    if (_config.Settings.Enabled) _engine.EnforceVolumeCeiling();
                }

                if (incoming.TryGetProperty("protectMicrophoneInputs", out var micProp))
                {
                    _config.Settings.ProtectMicrophoneInputs = micProp.GetBoolean();
                    if (_config.Settings.Enabled) _engine.EnforceVolumeCeiling();
                }

                if (incoming.TryGetProperty("micCeilingPercent", out var micCeilProp))
                {
                    _config.Settings.MicCeilingPercent = (float)micCeilProp.GetDouble();
                    if (_config.Settings.Enabled) _engine.EnforceVolumeCeiling();
                }

                if (incoming.TryGetProperty("appMixerGuardEnabled", out var appGuardProp))
                {
                    _config.Settings.AppMixerGuardEnabled = appGuardProp.GetBoolean();
                }

                _config.Save();

                await SendJson(res, new { success = true, settings = _config.Settings });
                return;
            }

            if (path == "/api/devices" && req.HttpMethod == "GET")
            {
                var devices = _engine.GetAllRenderDevices();
                await SendJson(res, new { devices });
                return;
            }

            if (path.StartsWith("/api/devices/") && req.HttpMethod == "POST")
            {
                var deviceId = Uri.UnescapeDataString(path.Substring("/api/devices/".Length));
                using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
                var body = await reader.ReadToEndAsync();
                var update = JsonSerializer.Deserialize<JsonElement>(body);

                if (_config.Settings.DeviceProfiles.TryGetValue(deviceId, out var profile))
                {
                    if (update.TryGetProperty("safeCeilingPercent", out var ceil))
                        profile.SafeCeilingPercent = (float)ceil.GetDouble();
                    if (update.TryGetProperty("targetSafeDbSpl", out var db))
                        profile.TargetSafeDbSpl = (float)db.GetDouble();
                    if (update.TryGetProperty("estimatedMaxDbSpl", out var maxSpl))
                        profile.EstimatedMaxDbSpl = (float)maxSpl.GetDouble();
                    if (update.TryGetProperty("deviceType", out var dType))
                        profile.DeviceType = dType.GetString() ?? profile.DeviceType;
                    if (update.TryGetProperty("isCalibrated", out var calib))
                        profile.IsCalibrated = calib.GetBoolean();

                    _config.Save();
                    _engine.EnforceVolumeCeiling();
                    await SendJson(res, new { success = true, profile });
                }
                else
                {
                    res.StatusCode = 404;
                    await SendJson(res, new { error = "Device not found" });
                }
                return;
            }

            if (path == "/api/volume" && req.HttpMethod == "POST")
            {
                using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
                var body = await reader.ReadToEndAsync();
                var data = JsonSerializer.Deserialize<JsonElement>(body);
                if (data.TryGetProperty("volumePercent", out var volProp))
                {
                    _engine.SetMasterVolume((float)volProp.GetDouble());
                }
                await SendJson(res, new { success = true });
                return;
            }

            if (path == "/api/apps/volume" && req.HttpMethod == "POST")
            {
                using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
                var body = await reader.ReadToEndAsync();
                var data = JsonSerializer.Deserialize<JsonElement>(body);
                if (data.TryGetProperty("processName", out var procProp) &&
                    data.TryGetProperty("volumePercent", out var appVolProp))
                {
                    var pName = procProp.GetString() ?? "";
                    var vol = (float)appVolProp.GetDouble();
                    _engine.SetAppVolume(pName, vol);
                    await SendJson(res, new { success = true, processName = pName, volumePercent = vol });
                    return;
                }
            }

            if (path == "/api/meter" && req.HttpMethod == "GET")
            {
                // Server-Sent Events (SSE) stream
                res.ContentType = "text/event-stream";
                res.Headers.Add("Cache-Control", "no-cache");
                res.Headers.Add("Connection", "keep-alive");
                _sseClients.Add(res);
                return;
            }

            // Static File Serving
            await ServeStaticFile(path, res);
        }
        catch (Exception ex)
        {
            res.StatusCode = 500;
            try
            {
                await SendJson(res, new { error = ex.Message });
            }
            catch { }
        }
    }

    private void BroadcastMetricsToSse(AudioMetrics metrics)
    {
        if (_sseClients.IsEmpty) return;

        var json = JsonSerializer.Serialize(metrics, JsonOpts);
        var bytes = Encoding.UTF8.GetBytes($"data: {json}\n\n");

        foreach (var client in _sseClients.ToArray())
        {
            try
            {
                client.OutputStream.Write(bytes, 0, bytes.Length);
                client.OutputStream.Flush();
            }
            catch
            {
                // Client disconnected
                try { client.Close(); } catch { }
            }
        }
    }

    private async Task ServeStaticFile(string relativePath, HttpListenerResponse res)
    {
        if (string.IsNullOrEmpty(relativePath) || relativePath == "/")
        {
            relativePath = "index.html";
        }
        else
        {
            relativePath = relativePath.TrimStart('/');
        }

        var filePath = Path.Combine(_wwwroot, relativePath);

        // If file not found and doesn't look like an asset, serve index.html (SPA routing)
        if (!File.Exists(filePath))
        {
            var fallback = Path.Combine(_wwwroot, "index.html");
            if (File.Exists(fallback))
            {
                filePath = fallback;
            }
            else
            {
                // Serve built-in fallback status page if UI not yet built
                res.ContentType = "text/html; charset=utf-8";
                res.StatusCode = 200;
                var html = GetDefaultStatusHtml();
                var data = Encoding.UTF8.GetBytes(html);
                await res.OutputStream.WriteAsync(data);
                res.Close();
                return;
            }
        }

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        res.ContentType = ext switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" => "application/javascript",
            ".css" => "text/css",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".ico" => "image/x-icon",
            ".json" => "application/json",
            _ => "application/octet-stream"
        };

        using var fs = File.OpenRead(filePath);
        await fs.CopyToAsync(res.OutputStream);
        res.Close();
    }

    private static async Task SendJson(HttpListenerResponse res, object obj)
    {
        res.ContentType = "application/json; charset=utf-8";
        var json = JsonSerializer.Serialize(obj, JsonOpts);
        var bytes = Encoding.UTF8.GetBytes(json);
        await res.OutputStream.WriteAsync(bytes);
        res.Close();
    }

    private string GetDefaultStatusHtml()
    {
        return $@"<!DOCTYPE html>
<html lang=""en"">
<head>
    <meta charset=""UTF-8"">
    <title>PermadB - Decibel Guard</title>
    <style>
        body {{ font-family: system-ui, -apple-system, sans-serif; background: #0f172a; color: #f8fafc; display: flex; align-items: center; justify-content: center; height: 100vh; margin: 0; }}
        .card {{ background: #1e293b; padding: 2rem; border-radius: 1rem; border: 1px solid #334155; text-align: center; max-width: 480px; box-shadow: 0 10px 25px rgba(0,0,0,0.5); }}
        h1 {{ margin: 0 0 0.5rem; color: #10b981; display: flex; align-items: center; justify-content: center; gap: 0.5rem; }}
        p {{ color: #94a3b8; line-height: 1.5; }}
        .badge {{ display: inline-block; background: #064e3b; color: #34d399; padding: 0.25rem 0.75rem; border-radius: 9999px; font-weight: bold; margin: 1rem 0; }}
    </style>
</head>
<body>
    <div class=""card"">
        <h1>🛡️ PermadB Guard</h1>
        <div class=""badge"">Active & Protecting</div>
        <p>Your audio decibel ceiling is actively monitored and enforced.</p>
        <p>Loading full interactive dashboard...</p>
    </div>
</body>
</html>";
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch { }
        _engine.OnMetricsUpdated -= BroadcastMetricsToSse;
    }
}
