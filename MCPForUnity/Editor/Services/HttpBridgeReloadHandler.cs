using System;
using System.Threading;
using System.Threading.Tasks;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services.Transport;
using MCPForUnity.Editor.Windows;
using UnityEditor;

namespace MCPForUnity.Editor.Services
{
    /// <summary>
    /// Ensures HTTP transports resume after domain reloads similar to the legacy stdio bridge.
    /// </summary>
    [InitializeOnLoad]
    internal static class HttpBridgeReloadHandler
    {
        private static readonly TimeSpan[] ResumeRetrySchedule =
        {
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30)
        };

        // After the bounded schedule above is exhausted, keep retrying at this interval
        // until cancelled. Mirrors WebSocketTransportClient.ReconnectTailInterval so the
        // reload path is as durable as the steady-state reconnect path.
        private static readonly TimeSpan ResumeTailInterval = TimeSpan.FromSeconds(30);

        private static CancellationTokenSource s_resumeCts;

        /// <summary>
        /// True while a post-reload resume cycle (initial schedule or tail retry) is in
        /// flight. The connection UI reads this to show "Resuming..." instead of
        /// "No Session" while the bridge is still attempting to reconnect.
        /// </summary>
        public static bool IsResuming => s_resumeCts != null;

        static HttpBridgeReloadHandler()
        {
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
            EditorApplication.quitting += CancelActiveResume;
        }

        private static void OnBeforeAssemblyReload()
        {
            try
            {
                // A new reload owns the next resume cycle; stop any in-flight tail retry
                // from a previous reload so it doesn't race the new client instance.
                CancelActiveResume();

                bool useHttp = EditorConfigurationCache.Instance.UseHttpTransport;
                bool userStopped = SessionState.GetBool(SessionStateKeys.HttpUserStopped, false);
                bool shouldResume = useHttp && !userStopped;

                if (shouldResume)
                {
                    EditorPrefs.SetBool(EditorPrefKeys.ResumeHttpAfterReload, true);
                }
                else
                {
                    EditorPrefs.DeleteKey(EditorPrefKeys.ResumeHttpAfterReload);
                }

                var transport = MCPServiceLocator.TransportManager;
                if (transport.GetClient(TransportMode.Http) != null)
                {
                    // beforeAssemblyReload is synchronous; force a synchronous teardown so we do not
                    // leave an orphaned socket due to an unfinished async close handshake.
                    // We tear down whenever a client exists, not only when "running", so transient
                    // mid-reconnect states cannot leak sockets across the reload boundary.
                    transport.ForceStop(TransportMode.Http);
                }
            }
            catch (Exception ex)
            {
                McpLog.Warn($"Failed to evaluate HTTP bridge reload state: {ex.Message}");
            }
        }

        private static void OnAfterAssemblyReload()
        {
            bool resume = false;
            try
            {
                // Honor the user's transport choice and "I explicitly stopped" intent.
                bool useHttp = EditorConfigurationCache.Instance.UseHttpTransport;
                bool userStopped = SessionState.GetBool(SessionStateKeys.HttpUserStopped, false);
                resume = useHttp
                         && !userStopped
                         && EditorPrefs.GetBool(EditorPrefKeys.ResumeHttpAfterReload, false);
                if (resume)
                {
                    EditorPrefs.DeleteKey(EditorPrefKeys.ResumeHttpAfterReload);
                }
            }
            catch (Exception ex)
            {
                McpLog.Warn($"Failed to read HTTP bridge reload flag: {ex.Message}");
                resume = false;
            }

            if (!resume)
            {
                return;
            }

            // Schedule on the editor loop so we don't fight asset import / shader compile
            // immediately after the domain reload settles.
            var cts = new CancellationTokenSource();
            s_resumeCts = cts;
            EditorApplication.delayCall += () => _ = RunResumeAsync(cts);
        }

        private static async Task RunResumeAsync(CancellationTokenSource cts)
        {
            try
            {
                await ResumeHttpWithRetriesAsync(cts.Token).ConfigureAwait(false);
            }
            finally
            {
                if (s_resumeCts == cts) s_resumeCts = null;
                try { cts.Dispose(); } catch { }
            }
        }

        private static void CancelActiveResume()
        {
            CancellationTokenSource cts = s_resumeCts;
            s_resumeCts = null;
            if (cts == null) return;
            // Don't Dispose here: the resume task may still observe the token. The task's
            // finally block disposes the CTS once it has finished using it.
            try { cts.Cancel(); } catch { }
        }

        private static async Task WaitForEditorIdleAsync(CancellationToken token)
        {
            // Wait until the editor is no longer compiling or importing assets before the
            // first connect attempt. Bounded so we never block forever — if the editor is
            // genuinely busy for a long time, fall through and rely on the retry loop.
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (!token.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                bool busy = EditorApplication.isCompiling || EditorApplication.isUpdating;
                try
                {
                    var pipeline = Type.GetType("UnityEditor.Compilation.CompilationPipeline, UnityEditor");
                    var prop = pipeline?.GetProperty("isCompiling", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (prop != null) busy |= (bool)prop.GetValue(null);
                }
                catch { }

                if (!busy) return;

                try { await Task.Delay(TimeSpan.FromMilliseconds(250), token).ConfigureAwait(false); }
                catch { return; }
            }
        }

        private static async Task ResumeHttpWithRetriesAsync(CancellationToken token)
        {
            await WaitForEditorIdleAsync(token).ConfigureAwait(false);
            if (token.IsCancellationRequested) return;

            for (int i = 0; i < ResumeRetrySchedule.Length; i++)
            {
                if (token.IsCancellationRequested) return;

                int attempt = i + 1;
                TimeSpan delay = ResumeRetrySchedule[i];
                if (delay > TimeSpan.Zero)
                {
                    McpLog.Debug($"[HTTP Reload] Waiting {delay.TotalSeconds:0.#}s before resume attempt {attempt}");
                    try { await Task.Delay(delay, token).ConfigureAwait(false); }
                    catch { return; }
                }

                McpLog.Debug($"[HTTP Reload] Resume attempt {attempt}/{ResumeRetrySchedule.Length}");

                if (ShouldAbortResume()) return;

                if (await TryStartAsync(token).ConfigureAwait(false))
                {
                    McpLog.Debug($"[HTTP Reload] Resume succeeded on attempt {attempt}");
                    return;
                }

                var state = MCPServiceLocator.TransportManager.GetState(TransportMode.Http);
                string reason = string.IsNullOrWhiteSpace(state?.Error) ? "no error detail" : state.Error;
                McpLog.Debug($"[HTTP Reload] Resume attempt {attempt} failed: {reason}");
            }

            if (token.IsCancellationRequested) return;

            // Schedule exhausted but we still want HTTP up: tail-retry indefinitely on a
            // fixed cadence until something cancels us (next reload, user stop, transport
            // switch, editor quit). Without this the bridge stays dead forever after the
            // 6 quick attempts, which is the actual user-visible bug.
            McpLog.Warn($"[HTTP Reload] Initial resume schedule exhausted. Retrying every {ResumeTailInterval.TotalSeconds:0}s until cancelled.");

            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(ResumeTailInterval, token).ConfigureAwait(false); }
                catch { return; }

                if (ShouldAbortResume()) return;

                if (await TryStartAsync(token).ConfigureAwait(false))
                {
                    McpLog.Info("[HTTP Reload] Tail-retry reconnected to MCP server.");
                    return;
                }
            }
        }

        /// <summary>
        /// Returns true if external state says we should stop trying to resume:
        /// user switched transport away from HTTP, user explicitly stopped, or the
        /// bridge is already running (something else brought it up).
        /// </summary>
        private static bool ShouldAbortResume()
        {
            try
            {
                if (!EditorConfigurationCache.Instance.UseHttpTransport) return true;
                if (SessionState.GetBool(SessionStateKeys.HttpUserStopped, false)) return true;
                if (MCPServiceLocator.TransportManager.IsRunning(TransportMode.Http)) return true;
            }
            catch { }
            return false;
        }

        private static async Task<bool> TryStartAsync(CancellationToken token)
        {
            try
            {
                bool started = await MCPServiceLocator.TransportManager.StartAsync(TransportMode.Http).ConfigureAwait(false);
                if (started && !token.IsCancellationRequested)
                {
                    MCPForUnityEditorWindow.RequestHealthVerification();
                }
                return started;
            }
            catch (Exception ex)
            {
                McpLog.Debug($"[HTTP Reload] Resume start threw: {ex.Message}");
                return false;
            }
        }
    }
}
