using System;
using System.Diagnostics;
using System.Threading;
using UnityEngine;
using UnityEngine.PlayerLoop;

namespace UnityEssentials
{
    /// <summary>
    /// Provides global functionality for managing and limiting the refresh rate of the application.
    /// </summary>
    /// <remarks>This class allows for setting a target refresh rate and provides an event to notify subscribers
    /// of frame updates. It is initialized automatically before the first scene load and integrates with the Unity
    /// player loop.</remarks>
    public static partial class GlobalRefreshRate
    {
        public static Action OnTick;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        public static void Initialize()
        {
            SetTarget(_targetRefreshRate);
            _frequency = Stopwatch.Frequency;

            long now = Stopwatch.GetTimestamp();
            _nextFrameTicks = now + _targetFrameTimeTicks;

            PlayerLoopHook.Add<Update>(Tick);
        }

        /// <summary>
        /// Sets the target refresh rate for the update loop.
        /// </summary>
        /// <remarks>This method adjusts the internal timing calculations to achieve the specified frame
        /// rate.</remarks>
        /// <param name="refreshRate">The desired target FPS. Must be greater than 0.</param>
        public static void SetTarget(float refreshRate)
        {
            // Guard against invalid values to prevent division by zero / NaN.
            if (float.IsNaN(refreshRate) || float.IsInfinity(refreshRate) || refreshRate <= 0f)
            {
                UnityEngine.Debug.LogWarning($"GlobalRefreshRate: Invalid refreshRate {refreshRate}. Falling back to 60 FPS.");
                refreshRate = 60f;
            }

            _targetRefreshRate = refreshRate;
            _frequency = Stopwatch.Frequency;
            _targetFrameTimeTicks = (long)(_frequency / (double)_targetRefreshRate);

            // If we're already running, reschedule from now to avoid carrying an old cadence across rate changes.
            if (_frequency > 0)
            {
                long now = Stopwatch.GetTimestamp();
                _nextFrameTicks = now + _targetFrameTimeTicks;
            }
        }

        private static void Tick()
        {
            FrameLimiter();

            if (!Application.isPlaying)
                Clear();
        }

        private static void Clear()
        {
            OnTick = null;
            PlayerLoopHook.Remove<Update>(Tick);
        }
    }

    public static partial class GlobalRefreshRate
    {
        private static float _targetRefreshRate = 1000f;
        private static long _targetFrameTimeTicks;
        private static long _nextFrameTicks;
        private static long _frequency;

        /// <summary>
        /// Runs work, then waits until the next fixed frame boundary (drift-free).
        /// </summary>
        private static void FrameLimiter()
        {
            // 1) Run simulation / user tick.
            OnTick?.Invoke();

            // 2) Wait until the pre-scheduled boundary.
            //    Do NOT compute the next boundary from "now"; keep a fixed schedule to avoid drift.
            long now = Stopwatch.GetTimestamp();

            // If uninitialized, schedule the first boundary one frame ahead.
            if (_nextFrameTicks <= 0)
                _nextFrameTicks = now + _targetFrameTimeTicks;

            if (now < _nextFrameTicks)
                HighPrecisionWait.WaitUntil(_nextFrameTicks, _frequency);

            // 3) Advance cadence deterministically.
            _nextFrameTicks += _targetFrameTimeTicks;

            // 4) If we fell far behind (e.g., breakpoint/GC spike), resync so we don't try to "catch up" forever.
            long afterWait = Stopwatch.GetTimestamp();
            if (afterWait > _nextFrameTicks + _targetFrameTimeTicks)
                _nextFrameTicks = afterWait + _targetFrameTimeTicks;
        }

        [Console("globalRefreshRate", "Gets/sets GlobalRefreshRate target FPS. Usage: globalRefreshRate or globalRefreshRate <fps>")]
        private static string ConsoleGlobalRefreshRate(string args)
        {
            args = (args ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(args))
                return $"GlobalRefreshRate = {_targetRefreshRate:0.###} FPS";

            if (!float.TryParse(args, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fps))
                return "Invalid number. Usage: globalRefreshRate <fps>";

            SetTarget(fps);
            return $"GlobalRefreshRate = {_targetRefreshRate:0.###} FPS";
        }
    }

    // Cross-platform high precision wait helpers
    internal static class HighPrecisionWait
    {
        // Spin threshold in nanoseconds: we sleep until this near the target, then busy-spin to finish precisely
        private const long SpinThresholdNs = 80_000; // 0.08 ms

        public static void WaitUntil(long targetTimestamp, long stopwatchFrequency)
        {
            while (true)
            {
                long now = Stopwatch.GetTimestamp();
                long remainingTicks = targetTimestamp - now;
                if (remainingTicks <= 0)
                    return;

                // Convert remaining to nanoseconds using Stopwatch frequency
                long remainingNs = (long)((remainingTicks * 1_000_000_000.0) / stopwatchFrequency);

                if (remainingNs > SpinThresholdNs)
                {
                    // Sleep most of the remaining time, keep a small margin for an accurate final spin
                    long sleepNs = remainingNs - SpinThresholdNs;
                    SleepNs(sleepNs);
                    continue;
                }

                // Final tight spin (very short)
                SpinUntil(targetTimestamp);
                return;
            }
        }

        private static void SpinUntil(long targetTimestamp)
        {
            // Tiny spin loop for the last ~0.08ms
            while (Stopwatch.GetTimestamp() < targetTimestamp)
            {
                // Hint to CPU that we're in a spin-wait loop
                Thread.SpinWait(20);
            }
        }

        private static void SleepNs(long ns)
        {
#if (UNITY_STANDALONE_LINUX || UNITY_EDITOR_LINUX)
            LinuxSleepNs(ns);
#elif (UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX)
            // macOS: best we can from managed is Thread.Sleep for millisecond parts, then rely on spin for sub-ms
            if (ns >= 1_000_000)
            {
                int ms = (int)(ns / 1_000_000);
                if (ms > 0)
                    Thread.Sleep(ms);
            }
            else
            {
                // For very small sleeps, yield the remainder of the time slice
                Thread.Sleep(0);
            }
#else
            // Windows and other platforms: use Thread.Sleep for ms part, then yield once for sub-ms, final spin does the rest
            if (ns >= 1_000_000)
            {
                int ms = (int)(ns / 1_000_000);
                if (ms > 0)
                    Thread.Sleep(ms);
            }
            else
            {
                Thread.Sleep(0);
            }
#endif
        }

#if (UNITY_STANDALONE_LINUX || UNITY_EDITOR_LINUX)
        // P/Invoke nanosleep for higher precision relative sleeps on Linux
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct Timespec
        {
            public long tv_sec;   // seconds
            public long tv_nsec;  // nanoseconds
        }

        [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
        private static extern int nanosleep(ref Timespec req, IntPtr rem);

        private static void LinuxSleepNs(long ns)
        {
            if (ns <= 0)
            {
                Thread.Sleep(0);
                return;
            }

            var req = new Timespec
            {
                tv_sec = ns / 1_000_000_000,
                tv_nsec = ns % 1_000_000_000
            };

            // We ignore the remainder arg (rem) and EINTR handling for simplicity; if interrupted, we just return
            _ = nanosleep(ref req, IntPtr.Zero);
        }
#endif
    }
}
