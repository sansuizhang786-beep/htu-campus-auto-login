using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace HtuAutoLogin
{
    public static class BackgroundRunner
    {
        internal static string InstanceScope = "";
        private static string MutexName { get { return @"Local\HTUAutoLogin.Worker." + LoginEngine.UserSuffix + InstanceScope; } }
        private static string StopName { get { return @"Local\HTUAutoLogin.Stop." + LoginEngine.UserSuffix + InstanceScope; } }
        public static bool IsRunning
        {
            get
            {
                try { using (Mutex mutex = Mutex.OpenExisting(MutexName)) return true; }
                catch (WaitHandleCannotBeOpenedException) { return false; }
            }
        }
        public static void Start()
        {
            if (IsRunning) return;
            using (var child = Process.Start(new ProcessStartInfo(System.Reflection.Assembly.GetExecutingAssembly().Location, "--background")
                { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden }))
            {
                var timer = Stopwatch.StartNew();
                while (!IsRunning && timer.Elapsed.TotalSeconds < 15)
                {
                    if (child.HasExited) throw new InvalidOperationException("后台启动失败，请检查账号配置和系统应用控制提示。");
                    Thread.Sleep(100);
                }
                if (!IsRunning)
                {
                    // Do not leave an unacknowledged worker that might start after the UI reports failure.
                    if (!child.HasExited) child.Kill();
                    throw new TimeoutException("后台未能及时启动，请稍后重试。");
                }
            }
        }
        public static void Stop()
        {
            if (!IsRunning) return;
            // Keep a signalled event alive even if the worker has not yet created its own handle.
            using (var stop = new EventWaitHandle(false, EventResetMode.ManualReset, StopName))
            {
                stop.Set();
                var timer = Stopwatch.StartNew();
                while (IsRunning && timer.Elapsed.TotalSeconds < 90) Thread.Sleep(200);
                if (IsRunning) throw new TimeoutException("后台仍在完成登录检查，请稍后再试。");
            }
        }
        public static void Run()
        {
            Run(new LoginEngine().RunOnce, delegate(WaitHandle stop, int milliseconds) { return stop.WaitOne(milliseconds); });
        }

        // Keep retry timing injectable so shutdown behavior can be tested without real logins or long sleeps.
        internal static void Run(Func<Settings, WaitHandle, LoginResult> authenticate, Func<WaitHandle, int, bool> wait)
        {
            bool created;
            using (var mutex = new Mutex(true, MutexName, out created))
            {
                if (!created) return;
                try
                {
                    using (var stop = new EventWaitHandle(false, EventResetMode.ManualReset, StopName))
                    {
                        Settings settings;
                        try { settings = Storage.Load(); }
                        catch { SetStatus("读取配置失败，请打开程序重新保存账号。"); return; }
                        if (settings == null) { SetStatus("尚未配置，请打开程序填写账号密码。"); return; }
                        SetStatus("自动登录已启动，等待网络就绪。");
                        // Brief startup acknowledgement window; network readiness is checked below.
                        if (wait(stop, 500)) { SetStatus("自动登录后台已停止。"); return; }
                        int submittedFailures = 0, waitFailures = 0, networkWaits = 0;
                        while (!stop.WaitOne(0))
                        {
                            LoginResult result = authenticate(settings, stop);
                            if (stop.WaitOne(0)) break;
                            if (result.State == LoginState.Online || result.State == LoginState.LoggedIn)
                            {
                                SetStatus(result.Message + " 本次自动登录已完成，程序自动退出。");
                                return;
                            }
                            SetStatus(result.Message);
                            int delay;
                            if (result.State == LoginState.Rejected)
                            {
                                submittedFailures++;
                                if (submittedFailures >= 3)
                                {
                                    SetStatus("连续 3 次提交后未确认联网，已退出本次自动登录。请核对账号后重试；下次登录 Windows 时会重新运行。");
                                    return;
                                }
                                delay = submittedFailures * 120000;
                            }
                            else
                            {
                                waitFailures++;
                                if (result.State == LoginState.Waiting)
                                {
                                    networkWaits++;
                                    delay = networkWaits <= 5 ? 2000 : networkWaits <= 10 ? 5000 : 15000;
                                }
                                else delay = Math.Min(300000, 30000 * (1 << Math.Min(waitFailures - 1, 4)));
                            }
                            if (wait(stop, delay)) break;
                        }
                        SetStatus("自动登录后台已停止。");
                    }
                }
                catch (Exception ex) { SetStatus("后台异常已停止（" + ex.GetType().Name + "），请重新打开设置。"); }
                finally { mutex.ReleaseMutex(); }
            }
        }
        private static string lastStatus;
        private static void SetStatus(string message)
        {
            try
            {
                Directory.CreateDirectory(Storage.DataDirectory);
                File.WriteAllText(Path.Combine(Storage.DataDirectory, "status.txt"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\r\n" + message, Encoding.UTF8);
                if (message != lastStatus) { Storage.Log(message); lastStatus = message; }
            }
            catch { }
        }
    }
}
