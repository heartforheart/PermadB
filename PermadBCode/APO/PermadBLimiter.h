// PermadBLimiter.h - PermadB Real-Time Lookahead Brickwall Safety Limiter
#pragma once

#include <vector>
#include <cmath>
#include <algorithm>
#include <cstdint>
#include <cstring>

class CPermadBLimiter
{
public:
    struct Config
    {
        float ceilingDbfs;    // e.g. -1.0f dBFS
        float lookaheadMs;    // e.g. 5.0f ms
        float releaseMs;      // e.g. 50.0f ms

        Config() :
            ceilingDbfs(-1.0f),
            lookaheadMs(5.0f),
            releaseMs(50.0f)
        {}
    };

    struct Metrics
    {
        float configuredCeilingDbfs;
        float configuredCeilingLinear;
        float lastPeakInLinear;
        float lastPeakOutLinear;
        float lastPeakInDbfs;
        float lastPeakOutDbfs;
        float maxObservedInDbfs;
        float maxObservedOutDbfs;
        float currentGainLinear;
        float currentGainReductionDb;
        float maxGainReductionDb;
        uint64_t limiterActivations;
    };

private:
    Config m_config;
    float m_ceilingLinear;

    uint32_t m_sampleRate;
    uint32_t m_channels;
    uint32_t m_lookaheadFrames; // D = sampleRate * lookaheadMs / 1000

    // Delay buffer for audio: interleaved float samples [m_lookaheadFrames * m_channels]
    std::vector<float> m_delayBuffer;
    uint32_t m_delayIndex; // Circular buffer write/read pointer (in frames)

    // O(1) Monotonic Deque for running maximum of input peak over lookahead window
    struct PeakItem
    {
        uint64_t frameIndex;
        float peak;
    };
    std::vector<PeakItem> m_peakDeque;
    uint32_t m_dequeHead;
    uint32_t m_dequeTail;
    uint32_t m_dequeCount;
    uint32_t m_dequeCapacity;

    uint64_t m_frameCounter;

    // Gain smoothing state
    float m_currentGain;
    float m_releaseCoeff;
    float m_attackCoeff;

    // Metrics / Statistics
    float m_maxObservedInLinear;
    float m_maxObservedOutLinear;
    float m_minGainLinear;
    uint64_t m_limiterActivations;
    bool m_bEnabled;

    static inline float LinearToDbfs(float lin)
    {
        return lin > 0.00001f ? 20.0f * std::log10(lin) : -96.0f;
    }

    static inline float DbfsToLinear(float db)
    {
        return std::pow(10.0f, db / 20.0f);
    }

public:
    CPermadBLimiter() :
        m_ceilingLinear(0.891250938f),
        m_sampleRate(48000),
        m_channels(2),
        m_lookaheadFrames(240),
        m_delayIndex(0),
        m_dequeHead(0),
        m_dequeTail(0),
        m_dequeCount(0),
        m_dequeCapacity(0),
        m_frameCounter(0),
        m_currentGain(1.0f),
        m_releaseCoeff(0.0f),
        m_attackCoeff(0.0f),
        m_maxObservedInLinear(0.0f),
        m_maxObservedOutLinear(0.0f),
        m_minGainLinear(1.0f),
        m_limiterActivations(0),
        m_bEnabled(true)
    {
    }

    void Init(uint32_t sampleRate, uint32_t channels, const Config& config = Config())
    {
        m_sampleRate = (sampleRate > 0) ? sampleRate : 48000;
        m_channels = (channels > 0) ? channels : 2;
        m_config = config;

        // Calculate linear ceiling: 10^(ceilingDbfs / 20)
        m_ceilingLinear = DbfsToLinear(m_config.ceilingDbfs);

        // Calculate lookahead frames
        float lookaheadSec = m_config.lookaheadMs / 1000.0f;
        m_lookaheadFrames = static_cast<uint32_t>(std::round(m_sampleRate * lookaheadSec));
        if (m_lookaheadFrames < 1) m_lookaheadFrames = 1;

        // Allocate audio delay buffer (zero allocations during Process)
        size_t totalSamples = static_cast<size_t>(m_lookaheadFrames) * m_channels;
        m_delayBuffer.assign(totalSamples, 0.0f);
        m_delayIndex = 0;

        // Allocate monotonic deque buffer (capacity = m_lookaheadFrames + 16)
        m_dequeCapacity = m_lookaheadFrames + 16;
        m_peakDeque.resize(m_dequeCapacity);
        m_dequeHead = 0;
        m_dequeTail = 0;
        m_dequeCount = 0;

        m_frameCounter = 0;
        m_currentGain = 1.0f;

        // Release coefficient: 1-pole filter for exponential gain release
        float releaseSec = (m_config.releaseMs > 0.0f ? m_config.releaseMs : 50.0f) / 1000.0f;
        m_releaseCoeff = std::exp(-1.0f / (m_sampleRate * releaseSec));

        // Attack coefficient: ramps gain smoothly down over lookahead duration
        float attackSec = (m_config.lookaheadMs * 0.5f) / 1000.0f;
        if (attackSec < 0.0001f) attackSec = 0.0001f;
        m_attackCoeff = std::exp(-1.0f / (m_sampleRate * attackSec));

        ResetStats();
    }

    void Reset()
    {
        if (!m_delayBuffer.empty())
        {
            std::fill(m_delayBuffer.begin(), m_delayBuffer.end(), 0.0f);
        }
        m_delayIndex = 0;
        m_dequeHead = 0;
        m_dequeTail = 0;
        m_dequeCount = 0;
        m_frameCounter = 0;
        m_currentGain = 1.0f;
    }

    void ResetStats()
    {
        m_maxObservedInLinear = 0.0f;
        m_maxObservedOutLinear = 0.0f;
        m_minGainLinear = 1.0f;
        m_limiterActivations = 0;
    }

    // Process audio buffer in-place or from pIn to pOut.
    // Real-time audio safe: ZERO memory allocations, NO mutexes, NO blocking syscalls.
    void Process(const float* pIn, float* pOut, uint32_t frameCount)
    {
        if (!pIn || !pOut || frameCount == 0 || m_delayBuffer.empty())
            return;

        if (!m_bEnabled)
        {
            if (pIn != pOut)
            {
                std::memcpy(pOut, pIn, sizeof(float) * frameCount * m_channels);
            }
            return;
        }

        for (uint32_t f = 0; f < frameCount; ++f)
        {
            uint32_t frameOffset = f * m_channels;

            // 1. Linked peak detection across all channels for incoming frame
            float inLinkedPeak = 0.0f;
            for (uint32_t c = 0; c < m_channels; ++c)
            {
                float absSample = std::abs(pIn[frameOffset + c]);
                if (absSample > inLinkedPeak) inLinkedPeak = absSample;
            }

            if (inLinkedPeak > m_maxObservedInLinear)
                m_maxObservedInLinear = inLinkedPeak;

            // 2. Push inLinkedPeak into O(1) monotonic deque for running window maximum
            uint64_t currentFrame = m_frameCounter++;

            // Pop elements from back that are <= current incoming peak
            while (m_dequeCount > 0)
            {
                uint32_t backIdx = (m_dequeTail == 0) ? (m_dequeCapacity - 1) : (m_dequeTail - 1);
                if (m_peakDeque[backIdx].peak <= inLinkedPeak)
                {
                    m_dequeTail = backIdx;
                    m_dequeCount--;
                }
                else
                {
                    break;
                }
            }

            // Push incoming peak to back
            m_peakDeque[m_dequeTail].frameIndex = currentFrame;
            m_peakDeque[m_dequeTail].peak = inLinkedPeak;
            m_dequeTail = (m_dequeTail + 1) % m_dequeCapacity;
            m_dequeCount++;

            // Pop expired elements from front (older than m_lookaheadFrames)
            while (m_dequeCount > 0)
            {
                if (m_peakDeque[m_dequeHead].frameIndex + m_lookaheadFrames <= currentFrame)
                {
                    m_dequeHead = (m_dequeHead + 1) % m_dequeCapacity;
                    m_dequeCount--;
                }
                else
                {
                    break;
                }
            }

            // The front of the deque is the exact maximum peak across the lookahead window
            float lookaheadPeak = (m_dequeCount > 0) ? m_peakDeque[m_dequeHead].peak : inLinkedPeak;

            // 3. Compute target gain for lookahead peak
            float targetGain = 1.0f;
            if (lookaheadPeak > m_ceilingLinear)
            {
                targetGain = m_ceilingLinear / lookaheadPeak;
            }

            // 4. Smooth gain envelope (Attack downwards, Release upwards)
            if (targetGain < m_currentGain)
            {
                // Attack: smoothly pull gain down in advance of the peak
                m_currentGain = m_currentGain * m_attackCoeff + targetGain * (1.0f - m_attackCoeff);
            }
            else
            {
                // Release: smoothly recover gain back towards 1.0
                m_currentGain = m_currentGain * m_releaseCoeff + targetGain * (1.0f - m_releaseCoeff);
            }

            // 5. Read delayed sample from circular delay buffer
            uint32_t delaySampleOffset = m_delayIndex * m_channels;

            // Find peak of exiting delayed frame to enforce strict brickwall safety clamp
            float delayedPeak = 0.0f;
            for (uint32_t c = 0; c < m_channels; ++c)
            {
                float absDel = std::abs(m_delayBuffer[delaySampleOffset + c]);
                if (absDel > delayedPeak) delayedPeak = absDel;
            }

            // BRICKWALL GUARANTEE:
            // Ensure that m_currentGain NEVER allows delayedPeak to exceed m_ceilingLinear
            if (delayedPeak > 0.00001f && (delayedPeak * m_currentGain > m_ceilingLinear))
            {
                m_currentGain = m_ceilingLinear / delayedPeak;
            }

            if (m_currentGain < 0.9999f)
            {
                m_limiterActivations++;
                if (m_currentGain < m_minGainLinear)
                    m_minGainLinear = m_currentGain;
            }

            // 6. Apply gain to delayed samples and write to output, then store incoming to delay buffer
            // Note: In an in-place APO (pIn == pOut), read incoming sample FIRST before writing to pOut!
            for (uint32_t c = 0; c < m_channels; ++c)
            {
                float inSample = pIn[frameOffset + c];
                float delayedSample = m_delayBuffer[delaySampleOffset + c];
                float outSample = delayedSample * m_currentGain;

                // Absolute safety clamp to protect against floating-point epsilon rounding
                if (outSample > m_ceilingLinear) outSample = m_ceilingLinear;
                else if (outSample < -m_ceilingLinear) outSample = -m_ceilingLinear;

                pOut[frameOffset + c] = outSample;
                m_delayBuffer[delaySampleOffset + c] = inSample;

                float absOut = std::abs(outSample);
                if (absOut > m_maxObservedOutLinear)
                    m_maxObservedOutLinear = absOut;
            }

            // Advance circular buffer frame pointer
            m_delayIndex = (m_delayIndex + 1) % m_lookaheadFrames;
        }
    }

    Metrics GetMetrics(float lastInLinear = 0.0f, float lastOutLinear = 0.0f) const
    {
        Metrics m;
        m.configuredCeilingDbfs = m_config.ceilingDbfs;
        m.configuredCeilingLinear = m_ceilingLinear;
        m.lastPeakInLinear = lastInLinear;
        m.lastPeakOutLinear = lastOutLinear;
        m.lastPeakInDbfs = LinearToDbfs(lastInLinear);
        m.lastPeakOutDbfs = LinearToDbfs(lastOutLinear);
        m.maxObservedInDbfs = LinearToDbfs(m_maxObservedInLinear);
        m.maxObservedOutDbfs = LinearToDbfs(m_maxObservedOutLinear);
        m.currentGainLinear = m_currentGain;
        m.currentGainReductionDb = (m_currentGain < 0.9999f) ? LinearToDbfs(m_currentGain) : 0.0f;
        m.maxGainReductionDb = (m_minGainLinear < 0.9999f) ? LinearToDbfs(m_minGainLinear) : 0.0f;
        m.limiterActivations = m_limiterActivations;
        return m;
    }

    uint32_t GetLookaheadFrames() const { return m_lookaheadFrames; }
    float GetCeilingLinear() const { return m_ceilingLinear; }
    float GetCeilingDbfs() const { return m_config.ceilingDbfs; }

    void SetCeilingLinear(float linearCeiling)
    {
        linearCeiling = std::clamp(linearCeiling, 0.001f, 1.0f);
        m_ceilingLinear = linearCeiling;
        m_config.ceilingDbfs = LinearToDbfs(linearCeiling);
        m_maxObservedInLinear = 0.0f;
        m_maxObservedOutLinear = 0.0f;
    }

    void SetCeilingDbfs(float dbfsCeiling)
    {
        dbfsCeiling = std::clamp(dbfsCeiling, -60.0f, 0.0f);
        m_config.ceilingDbfs = dbfsCeiling;
        m_ceilingLinear = DbfsToLinear(dbfsCeiling);
        m_maxObservedInLinear = 0.0f;
        m_maxObservedOutLinear = 0.0f;
    }

    void SetEnabled(bool enabled)
    {
        m_bEnabled = enabled;
    }

    bool IsEnabled() const { return m_bEnabled; }
};
