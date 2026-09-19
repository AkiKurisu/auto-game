#nullable disable
using System;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

namespace AutoGame
{
    public static class Entry
    {
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        static readonly object Sync = new object();
        static int pending;
        static string connectionPath;
        static uint bootstrapThread;
        static Camera.CameraCallback cameraCallback;
        static UnityEngine.Events.UnityAction beforeRenderCallback;
        static Canvas.WillRenderCanvases canvasCallback;

        public static void Initialize(string config)
        {
            if (Interlocked.Exchange(ref pending, 1) != 0) return;
            connectionPath = config;
            bootstrapThread = GetCurrentThreadId();
            cameraCallback = _ => StartOnMainThread();
            beforeRenderCallback = StartOnMainThread;
            canvasCallback = StartOnMainThread;
            try
            {
                lock (Sync)
                {
                    Camera.onPreCull += cameraCallback;
                    Application.onBeforeRender += beforeRenderCallback;
                    Canvas.willRenderCanvases += canvasCallback;
                }
            }
            catch (Exception error)
            {
                lock (Sync)
                {
                    Unregister();
                    Interlocked.Exchange(ref pending, 0);
                }
                Bridge.WriteBootstrapError(connectionPath, error);
            }
        }

        static void StartOnMainThread()
        {
            lock (Sync)
            {
                if (Interlocked.Exchange(ref pending, 0) == 0) return;
                Unregister();
            }
            try { Bridge.Start(connectionPath, bootstrapThread, GetCurrentThreadId()); }
            catch (Exception error) { Bridge.WriteBootstrapError(connectionPath, error); }
        }

        static void Unregister()
        {
            try { Camera.onPreCull -= cameraCallback; } catch { }
            try { Application.onBeforeRender -= beforeRenderCallback; } catch { }
            try { Canvas.willRenderCanvases -= canvasCallback; } catch { }
        }

        public static string Ping()
        {
            return "auto-game-ok|" + Application.unityVersion + "|" + Application.dataPath
                + "|domain=" + AppDomain.CurrentDomain.FriendlyName + "|thread=" + Thread.CurrentThread.ManagedThreadId;
        }
    }
}
