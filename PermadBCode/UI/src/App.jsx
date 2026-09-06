import React, { useState, useEffect, useRef } from 'react';
import { 
  Shield, 
  ShieldAlert, 
  ShieldCheck, 
  Headphones, 
  Speaker, 
  Volume2, 
  VolumeX, 
  Sliders, 
  Activity, 
  Moon, 
  Sparkles, 
  CheckCircle2, 
  Zap, 
  HelpCircle,
  RefreshCw,
  Power,
  Gamepad2
} from 'lucide-react';

export default function App() {
  const [metrics, setMetrics] = useState({
    masterPeak: 0,
    peakDbfs: -96,
    estimatedDbSpl: 40,
    currentVolumePercent: 50,
    safeCeilingPercent: 65,
    isClamping: false,
    spikesClampedTotal: 0,
    channelPeaks: [0, 0],
    activeDeviceId: '',
    activeDeviceName: 'Detecting Audio Device...',
    activeDeviceType: 'headphones',
    enabled: true,
    appSessions: []
  });

  const [settings, setSettings] = useState({
    enabled: true,
    startWithWindows: true,
    activePreset: 'safe',
    strictVolumeLock: true,
    dynamicLimiterEnabled: true,
    notificationsEnabled: true,
    applyToAllDevices: true,
    protectMicrophoneInputs: true,
    micCeilingPercent: 80,
    appMixerGuardEnabled: true
  });

  const [activeProfile, setActiveProfile] = useState({
    deviceId: '',
    deviceName: 'Headphones',
    deviceType: 'headphones',
    safeCeilingPercent: 65,
    targetSafeDbSpl: 75,
    estimatedMaxDbSpl: 102,
    isCalibrated: false
  });

  const [devices, setDevices] = useState([]);
  const [connected, setConnected] = useState(false);
  const [showCalibration, setShowCalibration] = useState(false);
  const [calibTonePlaying, setCalibTonePlaying] = useState(false);

  // Web Audio context for reference calibration tone
  const audioCtxRef = useRef(null);
  const oscRef = useRef(null);
  const gainRef = useRef(null);

  // Fetch initial data
  const fetchData = async () => {
    try {
      const res = await fetch('/api/status');
      if (res.ok) {
        const data = await res.json();
        if (data.metrics) setMetrics(prev => ({ ...prev, ...data.metrics }));
        if (data.settings) setSettings(data.settings);
        if (data.activeProfile) setActiveProfile(data.activeProfile);
        setConnected(true);
      }
    } catch (e) {
      console.warn('API fetch error:', e);
      setConnected(false);
    }

    try {
      const devRes = await fetch('/api/devices');
      if (devRes.ok) {
        const devData = await devRes.json();
        if (devData.devices) setDevices(devData.devices);
      }
    } catch (e) {}
  };

  useEffect(() => {
    fetchData();
    const interval = setInterval(fetchData, 4000);
    return () => clearInterval(interval);
  }, []);

  // Server-Sent Events (SSE) for 50Hz live metering
  useEffect(() => {
    let evtSource = null;

    const connectSse = () => {
      evtSource = new EventSource('/api/meter');

      evtSource.onmessage = (event) => {
        try {
          const data = JSON.parse(event.data);
          setMetrics(data);
          setConnected(true);
        } catch (err) {}
      };

      evtSource.onerror = () => {
        setConnected(false);
        evtSource.close();
        setTimeout(connectSse, 2500);
      };
    };

    connectSse();
    return () => {
      if (evtSource) evtSource.close();
    };
  }, []);

  // Toggle Master Guard
  const toggleMasterGuard = async () => {
    const nextState = !settings.enabled;
    setSettings(prev => ({ ...prev, enabled: nextState }));
    try {
      await fetch('/api/settings', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ enabled: nextState })
      });
      fetchData();
    } catch (e) {}
  };

  // Change Safe Ceiling
  const handleCeilingChange = async (newVal) => {
    const val = parseFloat(newVal);
    setMetrics(prev => ({ ...prev, safeCeilingPercent: val }));
    setActiveProfile(prev => ({ ...prev, safeCeilingPercent: val }));
    setSettings(prev => ({ ...prev, activePreset: 'custom' }));

    try {
      await fetch('/api/settings', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ safeCeilingPercent: val, activePreset: 'custom' })
      });
    } catch (e) {}
  };

  // Select Preset
  const handlePresetSelect = async (preset) => {
    setSettings(prev => ({ ...prev, activePreset: preset }));
    try {
      await fetch('/api/settings', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ activePreset: preset })
      });
      fetchData();
    } catch (e) {}
  };

  // Toggle Windows Autostart
  const toggleStartWithWindows = async () => {
    const nextVal = !settings.startWithWindows;
    setSettings(prev => ({ ...prev, startWithWindows: nextVal }));
    try {
      await fetch('/api/settings', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ startWithWindows: nextVal })
      });
    } catch (e) {}
  };

  // Toggle Dynamic Limiter
  const toggleDynamicLimiter = async () => {
    const nextVal = !settings.dynamicLimiterEnabled;
    setSettings(prev => ({ ...prev, dynamicLimiterEnabled: nextVal }));
    try {
      await fetch('/api/settings', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ dynamicLimiterEnabled: nextVal })
      });
    } catch (e) {}
  };

  // Toggle Strict Volume Lock
  const toggleStrictVolumeLock = async () => {
    const nextVal = !settings.strictVolumeLock;
    setSettings(prev => ({ ...prev, strictVolumeLock: nextVal }));
    try {
      await fetch('/api/settings', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ strictVolumeLock: nextVal })
      });
    } catch (e) {}
  };

  // Toggle Multi-Device Broadcast Guard (DJ / PA / All connected outputs)
  const toggleApplyToAllDevices = async () => {
    const nextVal = !settings.applyToAllDevices;
    setSettings(prev => ({ ...prev, applyToAllDevices: nextVal }));
    try {
      await fetch('/api/settings', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ applyToAllDevices: nextVal })
      });
      fetchData();
    } catch (e) {}
  };

  // Toggle Microphone Feedback & Peak Protection
  const toggleProtectMicrophoneInputs = async () => {
    const nextVal = !settings.protectMicrophoneInputs;
    setSettings(prev => ({ ...prev, protectMicrophoneInputs: nextVal }));
    try {
      await fetch('/api/settings', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ protectMicrophoneInputs: nextVal })
      });
      fetchData();
    } catch (e) {}
  };

  // Toggle App Sound Mixer Guard (Level Games & Applications)
  const toggleAppMixerGuard = async () => {
    const nextVal = !settings.appMixerGuardEnabled;
    setSettings(prev => ({ ...prev, appMixerGuardEnabled: nextVal }));
    try {
      await fetch('/api/settings', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ appMixerGuardEnabled: nextVal })
      });
      fetchData();
    } catch (e) {}
  };

  // Set individual app volume in Windows Sound Mixer
  const handleSetAppVolume = async (processName, val) => {
    const v = parseFloat(val);
    setMetrics(prev => ({
      ...prev,
      appSessions: (prev.appSessions || []).map(a => 
        a.processName === processName ? { ...a, volumePercent: v } : a
      )
    }));
    try {
      await fetch('/api/apps/volume', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ processName, volumePercent: v })
      });
    } catch (e) {}
  };

  // Set Master Volume
  const handleVolumeChange = async (val) => {
    const v = parseFloat(val);
    try {
      await fetch('/api/volume', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ volumePercent: v })
      });
    } catch (e) {}
  };

  // Calibration Tone (Web Audio)
  const toggleCalibrationTone = () => {
    if (calibTonePlaying) {
      if (oscRef.current) {
        oscRef.current.stop();
        oscRef.current.disconnect();
      }
      setCalibTonePlaying(false);
    } else {
      try {
        const AudioContext = window.AudioContext || window.webkitAudioContext;
        const ctx = new AudioContext();
        audioCtxRef.current = ctx;

        // Gentle 440Hz harmonic sine at -18 dBFS (comfortable reference)
        const osc = ctx.createOscillator();
        const gain = ctx.createGain();

        osc.type = 'sine';
        osc.frequency.setValueAtTime(440, ctx.currentTime);

        // -18 dBFS is standard professional reference dialogue level (approx 0.125 amplitude)
        gain.gain.setValueAtTime(0.12, ctx.currentTime);

        osc.connect(gain);
        gain.connect(ctx.destination);
        osc.start();

        oscRef.current = osc;
        gainRef.current = gain;
        setCalibTonePlaying(true);
      } catch (e) {
        console.error('AudioContext error:', e);
      }
    }
  };

  const saveCalibration = async () => {
    if (calibTonePlaying) toggleCalibrationTone();
    try {
      if (activeProfile.deviceId) {
        await fetch(`/api/devices/${encodeURIComponent(activeProfile.deviceId)}/profile`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            safeCeilingPercent: metrics.safeCeilingPercent,
            isCalibrated: true
          })
        });
      }
      setActiveProfile(prev => ({ ...prev, isCalibrated: true }));
      setShowCalibration(false);
    } catch (e) {}
  };

  // Decibel level visual categorization
  const getSplColorClass = (spl) => {
    if (spl <= 75) return 'safe';
    if (spl <= 85) return 'caution';
    return 'danger';
  };

  const leftBarPercent = Math.min(100, Math.max(0, (metrics.channelPeaks?.[0] || metrics.masterPeak) * 100));
  const rightBarPercent = Math.min(100, Math.max(0, (metrics.channelPeaks?.[1] || metrics.masterPeak) * 100));
  const isHeadphones = metrics.activeDeviceType === 'headphones' || activeProfile.deviceType === 'headphones';

  return (
    <div className="app-container">
      {/* Header */}
      <header className="app-header">
        <div className="brand-section">
          <div className={`brand-logo ${!settings.enabled ? 'disabled' : ''}`}>
            <Shield size={28} color="#ffffff" />
          </div>
          <div>
            <h1 className="brand-title">
              PermadB <span>Shield</span>
            </h1>
            <p className="brand-subtitle">
              Permanent Decibel & Ear Protection Guard for Windows
            </p>
          </div>
        </div>

        <div className="header-controls">
          <div className="card-badge green" style={{ display: 'flex', alignItems: 'center', gap: '6px' }}>
            <span style={{ 
              width: '8px', 
              height: '8px', 
              borderRadius: '50%', 
              backgroundColor: connected ? '#10b981' : '#ef4444',
              display: 'inline-block' 
            }} />
            {connected ? 'Engine Connected' : 'Connecting...'}
          </div>

          {settings.applyToAllDevices && (
            <div className="card-badge" style={{ background: 'rgba(56, 189, 248, 0.12)', color: '#38bdf8', border: '1px solid rgba(56, 189, 248, 0.3)' }}>
              🌐 All-Device Guard ({metrics.protectedDevicesCount || devices.length || 1} Connected)
            </div>
          )}

          <button 
            className={`master-guard-btn ${settings.enabled ? 'active' : 'bypassed'}`}
            onClick={toggleMasterGuard}
          >
            <Power size={18} />
            {settings.enabled ? 'SHIELD: ACTIVE' : 'SHIELD: BYPASSED'}
          </button>
        </div>
      </header>

      {/* Main Grid */}
      <main className="dashboard-grid">
        {/* Left Column: Live Audio Console & VU Meter */}
        <section className="panel-card">
          <div className="card-header">
            <h2 className="card-title">
              <Activity size={20} color="#10b981" />
              Live Decibel Console
            </h2>
            <span className="card-badge">Real-time WASAPI 50Hz</span>
          </div>

          <div className="meter-console">
            {/* Clamping Alert Indicator */}
            {metrics.isClamping && (
              <div className="clamping-banner">
                <Zap size={18} />
                Loud Peak Clamped — Eardrums Protected!
              </div>
            )}

            {/* Readouts */}
            <div className="meter-readout-row">
              <div className="readout-box">
                <span className="readout-label">Estimated Real-World dB SPL</span>
                <div className={`readout-val ${getSplColorClass(metrics.estimatedDbSpl)}`}>
                  {metrics.estimatedDbSpl > 25 ? metrics.estimatedDbSpl.toFixed(1) : '< 30'}
                  <small>dBA</small>
                </div>
                <span style={{ fontSize: '0.75rem', color: 'var(--text-muted)', marginTop: '4px' }}>
                  {metrics.estimatedDbSpl <= 75 ? '🟢 Safe all day' : (metrics.estimatedDbSpl <= 85 ? '🟡 Moderate level' : '🔴 Danger Clamped')}
                </span>
              </div>

              <div className="readout-box">
                <span className="readout-label">Digital Peak Level</span>
                <div className={`readout-val ${metrics.peakDbfs > -3 ? 'danger' : (metrics.peakDbfs > -12 ? 'caution' : 'safe')}`}>
                  {metrics.peakDbfs > -90 ? metrics.peakDbfs.toFixed(1) : '-∞'}
                  <small>dBFS</small>
                </div>
                <span style={{ fontSize: '0.75rem', color: 'var(--text-muted)', marginTop: '4px' }}>
                  Ceiling Cap: {metrics.safeCeilingPercent.toFixed(0)}%
                </span>
              </div>
            </div>

            {/* Stereo VU Bars */}
            <div className="vu-meter-section">
              <div className="vu-channel">
                <span className="channel-label">L</span>
                <div className="vu-track">
                  <div 
                    className="vu-fill" 
                    style={{ width: `${leftBarPercent}%` }} 
                  />
                  <div 
                    className="vu-ceiling-marker" 
                    style={{ left: `${metrics.safeCeilingPercent}%` }} 
                    title={`Safe Hardware Ceiling (${metrics.safeCeilingPercent}%)`}
                  />
                </div>
              </div>

              <div className="vu-channel">
                <span className="channel-label">R</span>
                <div className="vu-track">
                  <div 
                    className="vu-fill" 
                    style={{ width: `${rightBarPercent}%` }} 
                  />
                  <div 
                    className="vu-ceiling-marker" 
                    style={{ left: `${metrics.safeCeilingPercent}%` }} 
                    title={`Safe Hardware Ceiling (${metrics.safeCeilingPercent}%)`}
                  />
                </div>
              </div>

              <div className="vu-scale-ticks">
                <span>-60 dB</span>
                <span>-36 dB</span>
                <span>-18 dB</span>
                <span>-6 dB</span>
                <span style={{ color: '#ef4444' }}>0 dBFS</span>
              </div>
            </div>

            {/* Hearing Stats Card */}
            <div style={{
              display: 'grid', 
              gridTemplateColumns: '1fr 1fr', 
              gap: '0.75rem', 
              marginTop: '0.5rem',
              background: 'var(--bg-card-sub)',
              padding: '1rem',
              borderRadius: '12px',
              border: '1px solid var(--border-card)'
            }}>
              <div>
                <span style={{ fontSize: '0.75rem', color: 'var(--text-muted)', textTransform: 'uppercase' }}>
                  Dangerous Spikes Clamped
                </span>
                <p style={{ fontSize: '1.4rem', fontWeight: '700', color: 'var(--safe-green)', fontFamily: 'var(--font-mono)' }}>
                  {metrics.spikesClampedTotal}
                </p>
              </div>
              <div>
                <span style={{ fontSize: '0.75rem', color: 'var(--text-muted)', textTransform: 'uppercase' }}>
                  WHO Safe Listening Limit
                </span>
                <p style={{ fontSize: '0.9rem', fontWeight: '600', color: 'var(--text-main)', marginTop: '4px' }}>
                  &lt; 80 dBA (40 hrs/wk)
                </p>
              </div>
            </div>
          </div>
        </section>

        {/* Right Column: Safe Ceiling & Device Controls */}
        <section className="panel-card">
          <div className="card-header">
            <h2 className="card-title">
              {isHeadphones ? <Headphones size={20} color="#10b981" /> : <Speaker size={20} color="#10b981" />}
              Active Output Device
            </h2>
            <span className="card-badge green">
              {isHeadphones ? 'Headphones Profile' : 'Speakers Profile'}
            </span>
          </div>

          <div style={{ marginBottom: '1.25rem' }}>
            <p style={{ fontSize: '1.05rem', fontWeight: '700', color: 'var(--text-main)' }}>
              {metrics.activeDeviceName || activeProfile.deviceName}
            </p>
            <p style={{ fontSize: '0.8rem', color: 'var(--text-muted)', marginTop: '2px' }}>
              {activeProfile.isCalibrated ? '✓ Calibrated to your ears' : '⚡ Using automatic safe profile'}
            </p>
          </div>

          {/* Preset Buttons */}
          <div className="presets-row">
            <button 
              className={`preset-btn ${settings.activePreset === 'safe' ? 'active' : ''}`}
              onClick={() => handlePresetSelect('safe')}
            >
              <ShieldCheck size={20} color={settings.activePreset === 'safe' ? '#10b981' : '#94a3b8'} />
              <span className="preset-btn-name">Safe Ears</span>
              <span className="preset-btn-desc">65% / ~75 dB</span>
            </button>

            <button 
              className={`preset-btn ${settings.activePreset === 'night' ? 'active' : ''}`}
              onClick={() => handlePresetSelect('night')}
            >
              <Moon size={20} color={settings.activePreset === 'night' ? '#10b981' : '#94a3b8'} />
              <span className="preset-btn-name">Night / Relaxed</span>
              <span className="preset-btn-desc">45% / ~60 dB</span>
            </button>

            <button 
              className={`preset-btn ${settings.activePreset === 'studio' ? 'active' : ''}`}
              onClick={() => handlePresetSelect('studio')}
            >
              <Sparkles size={20} color={settings.activePreset === 'studio' ? '#10b981' : '#94a3b8'} />
              <span className="preset-btn-name">Studio Dynamic</span>
              <span className="preset-btn-desc">80% / ~85 dB</span>
            </button>
          </div>

          {/* Safe Ceiling Limit Slider */}
          <div className="slider-container">
            <div className="slider-header">
              <span className="slider-label">Hardware Volume Ceiling</span>
              <span className="slider-value-pill">{metrics.safeCeilingPercent.toFixed(0)}% Max</span>
            </div>
            <input 
              type="range" 
              min="20" 
              max="95" 
              value={metrics.safeCeilingPercent} 
              onChange={(e) => handleCeilingChange(e.target.value)}
            />
            <p style={{ fontSize: '0.75rem', color: 'var(--text-muted)' }}>
              PermadB locks your Windows volume to never exceed this ceiling, no matter what hotkey, game, or video tries to raise it.
            </p>
          </div>

          {/* Current System Master Volume */}
          <div className="slider-container" style={{ marginTop: '1rem' }}>
            <div className="slider-header">
              <span className="slider-label" style={{ display: 'flex', alignItems: 'center', gap: '6px' }}>
                <Volume2 size={16} color="var(--text-muted)" /> Current Output Volume
              </span>
              <span style={{ fontFamily: 'var(--font-mono)', fontSize: '0.85rem', color: 'var(--text-muted)' }}>
                {metrics.currentVolumePercent.toFixed(0)}% (Capped at {metrics.safeCeilingPercent.toFixed(0)}%)
              </span>
            </div>
            <input 
              type="range" 
              min="0" 
              max="100" 
              value={metrics.currentVolumePercent} 
              onChange={(e) => handleVolumeChange(e.target.value)}
            />
          </div>

          {/* Calibration Wizard Section */}
          <div style={{ marginTop: '1.5rem', paddingTop: '1.25rem', borderTop: '1px solid var(--border-card)' }}>
            {!showCalibration ? (
              <button 
                className="btn-secondary" 
                style={{ width: '100%' }}
                onClick={() => setShowCalibration(true)}
              >
                <Sliders size={16} />
                10-Second Headphone Calibration Wizard
              </button>
            ) : (
              <div className="calibration-box">
                <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '0.75rem' }}>
                  <h3 style={{ fontSize: '0.95rem', fontWeight: '700', color: '#38bdf8' }}>
                    🎧 Headphone Ear Sensitivity Calibration
                  </h3>
                  <button 
                    onClick={() => {
                      if (calibTonePlaying) toggleCalibrationTone();
                      setShowCalibration(false);
                    }}
                    style={{ background: 'none', border: 'none', color: '#94a3b8', cursor: 'pointer' }}
                  >
                    ✕
                  </button>
                </div>

                <p className="calibration-step">
                  Every pair of headphones has different physical sensitivity (ohms & dB/mW). 
                  Calibrating sets your exact comfortable physical volume as the 100% safe reference.
                </p>

                <div style={{ display: 'flex', gap: '0.75rem', marginBottom: '1rem' }}>
                  <button 
                    className={`btn-secondary ${calibTonePlaying ? 'active' : ''}`}
                    onClick={toggleCalibrationTone}
                    style={{ flex: 1, borderColor: calibTonePlaying ? '#38bdf8' : undefined }}
                  >
                    <Volume2 size={16} />
                    {calibTonePlaying ? 'Stop Comfort Tone' : 'Play Reference Tone'}
                  </button>

                  <button 
                    className="btn-primary" 
                    onClick={saveCalibration}
                    style={{ flex: 1 }}
                  >
                    <CheckCircle2 size={16} />
                    Save as 75 dB Baseline
                  </button>
                </div>

                <p style={{ fontSize: '0.75rem', color: '#94a3b8' }}>
                  Adjust the ceiling slider above while the reference tone is playing until it feels like a relaxed, comfortable speaking voice.
                </p>

                {/* Multi-Device Option in Wizard */}
                <div style={{ 
                  marginTop: '0.85rem', 
                  padding: '0.75rem 0.85rem', 
                  background: 'rgba(56, 189, 248, 0.08)', 
                  borderRadius: '8px', 
                  border: '1px solid rgba(56, 189, 248, 0.25)', 
                  display: 'flex', 
                  alignItems: 'center', 
                  justifyContent: 'space-between',
                  gap: '0.75rem'
                }}>
                  <div>
                    <div style={{ fontSize: '0.82rem', fontWeight: '600', color: '#e2e8f0', display: 'flex', alignItems: 'center', gap: '6px' }}>
                      🌐 Enforce Across All Connected Devices
                    </div>
                    <div style={{ fontSize: '0.72rem', color: '#94a3b8', marginTop: '2px' }}>
                      Locks PA speakers, DJ cue headphones & interfaces to this safe baseline
                    </div>
                  </div>
                  <input 
                    type="checkbox" 
                    className="toggle-checkbox" 
                    checked={settings.applyToAllDevices ?? true} 
                    onChange={toggleApplyToAllDevices} 
                  />
                </div>
              </div>
            )}
          </div>
        </section>

        {/* Full Row: Active Application Sound Mixer & Leveler */}
        <section className="panel-card" style={{ gridColumn: '1 / -1' }}>
          <div className="card-header">
            <div style={{ display: 'flex', alignItems: 'center', gap: '0.6rem' }}>
              <Gamepad2 size={20} color="#10b981" />
              <h2 className="card-title" style={{ margin: 0 }}>
                Application Sound Mixer Guard
              </h2>
            </div>
            <div style={{ display: 'flex', alignItems: 'center', gap: '0.75rem' }}>
              <span className={`card-badge ${(metrics.appSessions?.length || 0) > 0 ? 'green' : 'gray'}`}>
                {metrics.appSessions?.length || 0} Apps Monitored
              </span>
              <div style={{ display: 'flex', alignItems: 'center', gap: '0.4rem', fontSize: '0.8rem', color: '#94a3b8' }}>
                <span>Auto-Level</span>
                <input 
                  type="checkbox" 
                  className="toggle-checkbox" 
                  checked={settings.appMixerGuardEnabled ?? true} 
                  onChange={toggleAppMixerGuard} 
                />
              </div>
            </div>
          </div>

          <p style={{ fontSize: '0.82rem', color: '#94a3b8', margin: '0 0 1rem 0' }}>
            Actively scans the Windows Sound Mixer. If any game (like CS2 at <code>volume 1</code>) or software pushes sound pressure into an unsafe dB range, PermadB automatically levels its mixer volume down to your safe ceiling.
          </p>

          {(!metrics.appSessions || metrics.appSessions.length === 0) ? (
            <div style={{
              padding: '1.5rem',
              textAlign: 'center',
              color: '#64748b',
              background: 'rgba(15, 23, 42, 0.4)',
              borderRadius: '10px',
              border: '1px dashed rgba(255, 255, 255, 0.08)',
              fontSize: '0.85rem'
            }}>
              🎧 No audio-producing applications active. Launch a game or media app (CS2, Discord, Chrome, Spotify) to see real-time leveling!
            </div>
          ) : (
            <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(280px, 1fr))', gap: '1rem' }}>
              {metrics.appSessions.map((app, idx) => {
                const isClamped = app.isClamped;
                const isUnsafe = app.isUnsafe;
                const peakPct = Math.min(100, Math.round(app.peakValue * 100));

                return (
                  <div key={app.processId || idx} style={{
                    padding: '1rem',
                    background: isClamped ? 'rgba(245, 158, 11, 0.08)' : 'rgba(15, 23, 42, 0.6)',
                    borderRadius: '12px',
                    border: isClamped ? '1px solid rgba(245, 158, 11, 0.35)' : '1px solid rgba(255, 255, 255, 0.06)',
                    display: 'flex',
                    flexDirection: 'column',
                    gap: '0.65rem'
                  }}>
                    <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                      <div style={{ display: 'flex', alignItems: 'center', gap: '0.5rem', overflow: 'hidden' }}>
                        <div style={{
                          width: '28px',
                          height: '28px',
                          borderRadius: '6px',
                          background: '#1e293b',
                          display: 'flex',
                          alignItems: 'center',
                          justifyContent: 'center',
                          color: '#38bdf8',
                          fontSize: '0.75rem',
                          fontWeight: 'bold',
                          flexShrink: 0
                        }}>
                          {app.processName.substring(0, 2).toUpperCase()}
                        </div>
                        <div style={{ overflow: 'hidden' }}>
                          <div style={{ fontSize: '0.88rem', fontWeight: '600', color: '#f8fafc', whiteSpace: 'nowrap', textOverflow: 'ellipsis', overflow: 'hidden' }}>
                            {app.processName}
                          </div>
                          <div style={{ fontSize: '0.7rem', color: '#64748b' }}>
                            PID {app.processId} {app.isMuted ? '• Muted' : ''}
                          </div>
                        </div>
                      </div>

                      <span style={{
                        fontSize: '0.7rem',
                        fontWeight: '600',
                        padding: '0.2rem 0.5rem',
                        borderRadius: '9999px',
                        background: isClamped ? 'rgba(245, 158, 11, 0.2)' : isUnsafe ? 'rgba(239, 68, 68, 0.2)' : 'rgba(16, 185, 129, 0.15)',
                        color: isClamped ? '#fbbf24' : isUnsafe ? '#f87171' : '#34d399',
                        display: 'flex',
                        alignItems: 'center',
                        gap: '4px'
                      }}>
                        {isClamped ? '🛡️ Leveled' : isUnsafe ? '⚠️ Peak' : '🟢 Safe'}
                      </span>
                    </div>

                    {/* App Peak Level Bar */}
                    <div>
                      <div style={{ display: 'flex', justifyContent: 'space-between', fontSize: '0.72rem', color: '#94a3b8', marginBottom: '3px' }}>
                        <span>Acoustic Output</span>
                        <span style={{ color: app.estimatedDbSpl > 78 ? '#f59e0b' : '#38bdf8', fontWeight: 'bold' }}>
                          ~{app.estimatedDbSpl} dBA
                        </span>
                      </div>
                      <div style={{ height: '6px', background: '#334155', borderRadius: '3px', overflow: 'hidden' }}>
                        <div style={{
                          height: '100%',
                          width: `${peakPct}%`,
                          background: peakPct > 80 ? '#ef4444' : peakPct > 50 ? '#f59e0b' : '#10b981',
                          transition: 'width 0.05s ease'
                        }} />
                      </div>
                    </div>

                    {/* Sound Mixer Volume Slider */}
                    <div>
                      <div style={{ display: 'flex', justifyContent: 'space-between', fontSize: '0.72rem', color: '#94a3b8', marginBottom: '2px' }}>
                        <span>Mixer Volume</span>
                        <span style={{ fontWeight: '600', color: '#e2e8f0' }}>{app.volumePercent}%</span>
                      </div>
                      <input 
                        type="range"
                        min="0"
                        max="100"
                        value={app.volumePercent}
                        onChange={(e) => handleSetAppVolume(app.processName, e.target.value)}
                        style={{ width: '100%', accentColor: '#10b981', cursor: 'pointer' }}
                      />
                    </div>
                  </div>
                );
              })}
            </div>
          )}
        </section>

        {/* Bottom Full Row: System Preferences & Background Guard Status */}
        <section className="panel-card" style={{ gridColumn: '1 / -1' }}>
          <div className="card-header">
            <h2 className="card-title">
              <Sliders size={20} color="#10b981" />
              Protection Rules & System Preferences
            </h2>
          </div>

          <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(280px, 1fr))', gap: '1.5rem' }}>
            {/* Startup switch */}
            <div className="switch-row">
              <div className="switch-label">
                <span className="switch-title">Start with Windows</span>
                <span className="switch-desc">Launch PermadB silently in the system tray when PC turns on</span>
              </div>
              <input 
                type="checkbox" 
                className="toggle-checkbox" 
                checked={settings.startWithWindows} 
                onChange={toggleStartWithWindows} 
              />
            </div>

            {/* Dynamic Limiter switch */}
            <div className="switch-row">
              <div className="switch-label">
                <span className="switch-title">Dynamic Transient Limiter</span>
                <span className="switch-desc">Instantly ducks sudden jumpscares, gunshot bursts, or loud ads</span>
              </div>
              <input 
                type="checkbox" 
                className="toggle-checkbox" 
                checked={settings.dynamicLimiterEnabled} 
                onChange={toggleDynamicLimiter} 
              />
            </div>

            {/* Strict Ceiling Guard switch */}
            <div className="switch-row">
              <div className="switch-label">
                <span className="switch-title">🔒 Strict Ceiling Guard</span>
                <span className="switch-desc">Strictly caps volume to safe ceiling; freely allows turning down to quieter levels</span>
              </div>
              <input 
                type="checkbox" 
                className="toggle-checkbox" 
                checked={settings.strictVolumeLock} 
                onChange={toggleStrictVolumeLock} 
              />
            </div>

            {/* Multi-Device Guard switch */}
            <div className="switch-row">
              <div className="switch-label">
                <span className="switch-title">🌐 Protect All Connected Devices</span>
                <span className="switch-desc">Enforces safe ceiling across all connected outputs (PA speakers, DJ cue, interfaces)</span>
              </div>
              <input 
                type="checkbox" 
                className="toggle-checkbox" 
                checked={settings.applyToAllDevices ?? true} 
                onChange={toggleApplyToAllDevices} 
              />
            </div>

            {/* Microphone Input Protection switch */}
            <div className="switch-row">
              <div className="switch-label">
                <span className="switch-title">🎤 Microphone Feedback & Spikes Guard</span>
                <span className="switch-desc">Caps mic input gain so singing peaks and feedback squeals never blast speakers</span>
              </div>
              <input 
                type="checkbox" 
                className="toggle-checkbox" 
                checked={settings.protectMicrophoneInputs ?? true} 
                onChange={toggleProtectMicrophoneInputs} 
              />
            </div>

            {/* App Sound Mixer Guard switch */}
            <div className="switch-row">
              <div className="switch-label">
                <span className="switch-title">🎮 App Sound Mixer Guard</span>
                <span className="switch-desc">Actively levels games and apps in Windows Sound Mixer under your safe preset limit</span>
              </div>
              <input 
                type="checkbox" 
                className="toggle-checkbox" 
                checked={settings.appMixerGuardEnabled ?? true} 
                onChange={toggleAppMixerGuard} 
              />
            </div>

            {/* Device Auto-Switch */}
            <div className="switch-row">
              <div className="switch-label">
                <span className="switch-title">Smart Device Auto-Switch</span>
                <span className="switch-desc">Remembers individual safe volumes for headphones vs speakers</span>
              </div>
              <span className="card-badge green">Always Active</span>
            </div>
          </div>

          {/* Audio Safety Guard Info Box */}
          <div style={{
            marginTop: '1.5rem',
            padding: '1rem 1.25rem',
            background: 'rgba(16, 185, 129, 0.04)',
            borderRadius: '12px',
            border: '1px dashed rgba(16, 185, 129, 0.3)',
            display: 'flex',
            alignItems: 'flex-start',
            gap: '0.85rem'
          }}>
            <ShieldCheck size={22} color="#10b981" style={{ flexShrink: 0, marginTop: '2px' }} />
            <div style={{ fontSize: '0.82rem', color: '#cbd5e1', lineHeight: '1.5' }}>
              <strong style={{ color: '#34d399' }}>Permanent Audio Decibel Guard:</strong> PermadB monitors hardware endpoints at 50Hz, actively locking volumes to safe ceilings and protecting hearing across headphones, PA speakers, and multi-channel audio devices.
            </div>
          </div>
        </section>
      </main>
    </div>
  );
}
