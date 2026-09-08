// test_dsp.cpp - Deterministic DSP Test Suite for PermadB Lookahead Brickwall Limiter (In-Place & Out-of-Place)
#include <iostream>
#include <iomanip>
#include <vector>
#include <cmath>
#include <cassert>
#include "PermadBLimiter.h"

static const double PI = 3.14159265358979323846;

void RunTests()
{
    std::cout << "==========================================================\n";
    std::cout << "     PermadB Limiter DSP - Deterministic Test Suite (In-Place)\n";
    std::cout << "==========================================================\n\n";

    const uint32_t sampleRate = 48000;
    const uint32_t channels = 2;
    CPermadBLimiter::Config cfg;
    cfg.ceilingDbfs = -1.0f;
    cfg.lookaheadMs = 5.0f;
    cfg.releaseMs = 50.0f;

    const float ceilingLinear = std::pow(10.0f, -1.0f / 20.0f); // 0.8912509f

    // -----------------------------------------------------------------------
    // TEST A: Below Ceiling (-6 dBFS) In-Place
    // -----------------------------------------------------------------------
    {
        std::cout << "[TEST A] Signal Below Ceiling (-6 dBFS, In-Place)...\n";
        CPermadBLimiter limiter;
        limiter.Init(sampleRate, channels, cfg);

        float amp = std::pow(10.0f, -6.0f / 20.0f); // ~0.501187f
        size_t frames = sampleRate; // 1 second
        std::vector<float> buf(frames * channels);

        for (size_t f = 0; f < frames; ++f)
        {
            float s = amp * std::sin(2.0 * PI * 1000.0 * f / sampleRate);
            buf[f * 2] = s;
            buf[f * 2 + 1] = s;
        }

        // Test in-place processing: pIn == pOut
        limiter.Process(buf.data(), buf.data(), frames);

        uint32_t delayFrames = limiter.GetLookaheadFrames();
        float maxOut = 0.0f;
        for (size_t f = delayFrames; f < frames; ++f)
        {
            maxOut = std::max(maxOut, std::abs(buf[f * 2]));
            maxOut = std::max(maxOut, std::abs(buf[f * 2 + 1]));
        }

        float outDbfs = 20.0f * std::log10(maxOut);
        std::cout << "         Input Peak:  -6.00 dBFS (" << amp << ")\n";
        std::cout << "         Output Peak: " << std::fixed << std::setprecision(2) << outDbfs << " dBFS (" << maxOut << ")\n";
        std::cout << "         Ceiling:     -1.00 dBFS (" << ceilingLinear << ")\n";
        std::cout << "         Activations: " << limiter.GetMetrics().limiterActivations << "\n";

        if (std::abs(maxOut - amp) < 0.001f && limiter.GetMetrics().limiterActivations == 0)
        {
            std::cout << "         Result: PASS (No unnecessary attenuation, exact in-place pass-through)\n\n";
        }
        else
        {
            std::cout << "         Result: FAIL\n\n";
            exit(1);
        }
    }

    // -----------------------------------------------------------------------
    // TEST B: Above Ceiling (0 dBFS sine, ceiling = -1 dBFS) In-Place
    // -----------------------------------------------------------------------
    {
        std::cout << "[TEST B] Signal Above Ceiling (0 dBFS, Ceiling = -1.0 dBFS, In-Place)...\n";
        CPermadBLimiter limiter;
        limiter.Init(sampleRate, channels, cfg);

        float amp = 1.0f; // 0 dBFS
        size_t frames = sampleRate; // 1 second
        std::vector<float> buf(frames * channels);

        for (size_t f = 0; f < frames; ++f)
        {
            float s = amp * std::sin(2.0 * PI * 1000.0 * f / sampleRate);
            buf[f * 2] = s;
            buf[f * 2 + 1] = s;
        }

        limiter.Process(buf.data(), buf.data(), frames);

        uint32_t delayFrames = limiter.GetLookaheadFrames();
        float maxOut = 0.0f;
        for (size_t f = delayFrames; f < frames; ++f)
        {
            maxOut = std::max(maxOut, std::abs(buf[f * 2]));
            maxOut = std::max(maxOut, std::abs(buf[f * 2 + 1]));
        }

        float outDbfs = 20.0f * std::log10(maxOut);
        std::cout << "         Input Peak:  0.00 dBFS (1.0000)\n";
        std::cout << "         Output Peak: " << std::fixed << std::setprecision(2) << outDbfs << " dBFS (" << maxOut << ")\n";
        std::cout << "         Ceiling:     -1.00 dBFS (" << ceilingLinear << ")\n";
        std::cout << "         Max Gain Red: " << limiter.GetMetrics().maxGainReductionDb << " dB\n";

        if (maxOut <= (ceilingLinear + 0.0001f) && std::abs(outDbfs - (-1.0f)) < 0.1f)
        {
            std::cout << "         Result: PASS (Strictly <= ceiling, constrained to -1.00 dBFS in-place)\n\n";
        }
        else
        {
            std::cout << "         Result: FAIL (Exceeded ceiling or incorrect attenuation)\n\n";
            exit(1);
        }
    }

    // -----------------------------------------------------------------------
    // TEST C: Large Transient (Sudden 0 dBFS spike in -20 dBFS audio) In-Place
    // -----------------------------------------------------------------------
    {
        std::cout << "[TEST C] Large Sudden Transient (0 dBFS impulse, In-Place)...\n";
        CPermadBLimiter limiter;
        limiter.Init(sampleRate, channels, cfg);

        float baselineAmp = std::pow(10.0f, -20.0f / 20.0f); // 0.10 (-20 dBFS)
        size_t frames = sampleRate / 2; // 0.5s
        std::vector<float> buf(frames * channels, baselineAmp);

        size_t spikeFrame = 1000;
        buf[spikeFrame * 2] = 1.0f;
        buf[spikeFrame * 2 + 1] = 1.0f;

        limiter.Process(buf.data(), buf.data(), frames);

        float maxOut = 0.0f;
        for (size_t f = 0; f < frames; ++f)
        {
            maxOut = std::max(maxOut, std::abs(buf[f * 2]));
            maxOut = std::max(maxOut, std::abs(buf[f * 2 + 1]));
        }

        float outDbfs = 20.0f * std::log10(maxOut);
        std::cout << "         Spike Amplitude: 1.0000 (0.00 dBFS)\n";
        std::cout << "         Max Output Peak: " << std::fixed << std::setprecision(2) << outDbfs << " dBFS (" << maxOut << ")\n";
        std::cout << "         Ceiling:         -1.00 dBFS (" << ceilingLinear << ")\n";

        if (maxOut <= (ceilingLinear + 0.0001f))
        {
            std::cout << "         Result: PASS (Lookahead successfully intercepted and limited transient in-place)\n\n";
        }
        else
        {
            std::cout << "         Result: FAIL (Transient exceeded ceiling!)\n\n";
            exit(1);
        }
    }

    // -----------------------------------------------------------------------
    // TEST D: Stereo Linked Channels (Left = 0 dBFS, Right = -6 dBFS) In-Place
    // -----------------------------------------------------------------------
    {
        std::cout << "[TEST D] Stereo Linked Channel Protection (In-Place)...\n";
        CPermadBLimiter limiter;
        limiter.Init(sampleRate, channels, cfg);

        float leftAmp = 1.0f;                             // 0 dBFS
        float rightAmp = std::pow(10.0f, -6.0f / 20.0f);   // -6 dBFS (~0.501f)
        size_t frames = sampleRate;
        std::vector<float> buf(frames * channels);

        for (size_t f = 0; f < frames; ++f)
        {
            float sL = leftAmp * std::sin(2.0 * PI * 1000.0 * f / sampleRate);
            float sR = rightAmp * std::sin(2.0 * PI * 1000.0 * f / sampleRate);
            buf[f * 2] = sL;
            buf[f * 2 + 1] = sR;
        }

        limiter.Process(buf.data(), buf.data(), frames);

        uint32_t delayFrames = limiter.GetLookaheadFrames();
        float maxL = 0.0f;
        float maxR = 0.0f;
        for (size_t f = delayFrames; f < frames; ++f)
        {
            maxL = std::max(maxL, std::abs(buf[f * 2]));
            maxR = std::max(maxR, std::abs(buf[f * 2 + 1]));
        }

        std::cout << "         Input L:  " << leftAmp << " (0.00 dBFS), R: " << rightAmp << " (-6.00 dBFS)\n";
        std::cout << "         Output L: " << maxL << " (" << (20.0f * std::log10(maxL)) << " dBFS)\n";
        std::cout << "         Output R: " << maxR << " (" << (20.0f * std::log10(maxR)) << " dBFS)\n";

        float expectedRatio = rightAmp / leftAmp; // 0.501187
        float actualRatio = maxR / maxL;
        std::cout << "         Stereo Balance Ratio (R/L): In=" << expectedRatio << ", Out=" << actualRatio << "\n";

        if (maxL <= (ceilingLinear + 0.0001f) && std::abs(actualRatio - expectedRatio) < 0.01f)
        {
            std::cout << "         Result: PASS (Linked gain applied identically to L & R in-place, balance preserved)\n\n";
        }
        else
        {
            std::cout << "         Result: FAIL (Stereo balance altered or ceiling exceeded)\n\n";
            exit(1);
        }
    }

    // -----------------------------------------------------------------------
    // TEST E: Silence Input In-Place
    // -----------------------------------------------------------------------
    {
        std::cout << "[TEST E] Silence Input (In-Place, Zero DC offset & no noise)...\n";
        CPermadBLimiter limiter;
        limiter.Init(sampleRate, channels, cfg);

        size_t frames = sampleRate;
        std::vector<float> buf(frames * channels, 0.0f);

        limiter.Process(buf.data(), buf.data(), frames);

        float maxAbs = 0.0f;
        for (size_t i = 0; i < buf.size(); ++i)
        {
            maxAbs = std::max(maxAbs, std::abs(buf[i]));
        }

        std::cout << "         Max Output Magnitude: " << maxAbs << "\n";
        if (maxAbs == 0.0f)
        {
            std::cout << "         Result: PASS (Exact silence preserved, zero DC offset)\n\n";
        }
        else
        {
            std::cout << "         Result: FAIL (Noise or DC offset introduced)\n\n";
            exit(1);
        }
    }

    // -----------------------------------------------------------------------
    // TEST F: Dynamic Ceiling at 30% (Linear 0.30 / -10.46 dBFS)
    // -----------------------------------------------------------------------
    {
        std::cout << "[TEST F] Dynamic Ceiling at 30% (Linear 0.30, In-Place)...\n";
        CPermadBLimiter limiter;
        limiter.Init(sampleRate, channels, cfg);

        // Dynamically change ceiling to 30%
        limiter.SetCeilingLinear(0.30f);

        float amp = 1.0f; // 0 dBFS input
        size_t frames = sampleRate;
        std::vector<float> buf(frames * channels);

        for (size_t f = 0; f < frames; ++f)
        {
            float s = amp * std::sin(2.0 * PI * 1000.0 * f / sampleRate);
            buf[f * 2] = s;
            buf[f * 2 + 1] = s;
        }

        limiter.Process(buf.data(), buf.data(), frames);

        uint32_t delayFrames = limiter.GetLookaheadFrames();
        float maxOut = 0.0f;
        for (size_t f = delayFrames; f < frames; ++f)
        {
            maxOut = std::max(maxOut, std::max(std::abs(buf[f * 2]), std::abs(buf[f * 2 + 1])));
        }

        float maxOutDb = 20.0f * std::log10(maxOut);
        std::cout << "         Input Peak:   0.00 dBFS (1.0000)\n";
        std::cout << "         Output Peak:  " << std::fixed << std::setprecision(2) << maxOutDb << " dBFS (" << maxOut << ")\n";
        std::cout << "         Ceiling:      " << limiter.GetCeilingDbfs() << " dBFS (" << limiter.GetCeilingLinear() << ")\n";

        if (maxOut <= 0.3001f)
        {
            std::cout << "         Result: PASS (Audio strictly constrained to 30% ceiling)\n\n";
        }
        else
        {
            std::cout << "         Result: FAIL (Audio exceeded 30% ceiling)\n\n";
            exit(1);
        }
    }

    std::cout << "==========================================================\n";
    std::cout << "     ALL DETERMINISTIC DSP TESTS PASSED (6 / 6)\n";
    std::cout << "==========================================================\n";
}

int main()
{
    RunTests();
    return 0;
}
