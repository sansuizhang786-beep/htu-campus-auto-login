using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace HtuAutoLogin
{
    public enum LoginState { Online, LoggedIn, Waiting, UnsupportedPortal, Rejected, Error }
    public class LoginResult
    {
        public LoginState State;
        public string Message;
        public LoginResult(LoginState state, string message) { State = state; Message = message; }
    }

    public static class PortalAutomation
    {
        public static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 4194304 };
        public static bool IsTrustedUrl(string value)
        {
            Uri uri;
            return Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Scheme == "http" &&
                String.IsNullOrEmpty(uri.UserInfo) &&
                ((uri.Host == "1.1.1.1" && uri.Port == 80) ||
                 (uri.Host == "10.101.2.194" && uri.Port == 6060));
        }

        public static string[] EntryUrls(string configured)
        {
            // The bare authentication server root can open self/selfLogin.do (an empty self-service page).
            // Enter through the gateway to preserve the redirect's client/session parameters.
            var entries = new List<string> { "http://1.1.1.1/", "http://www.edu.cn/" };
            if (IsTrustedUrl(configured) && !entries.Contains(configured)) entries.Add(configured);
            return entries.ToArray();
        }

        // This is an independent implementation based on the HTU project's published selectors.
        // It neither downloads nor executes third-party userscripts.
        private const string Guard = @"
const allowed = location.protocol === 'http:' &&
  ((location.hostname === '1.1.1.1' && (!location.port || location.port === '80')) ||
   (location.hostname === '10.101.2.194' && location.port === '6060'));
if (!allowed || window.top !== window.self) return {kind:'untrusted', origin:location.origin};
const user = document.querySelector('input[name=""userName""]');
const pass = document.querySelector('input[name=""password""]');
const button = document.getElementById('checkButton');
const visible = el => !!el && !!(el.offsetWidth || el.offsetHeight || el.getClientRects().length);
const text = document.body ? document.body.innerText : '';
if (!document.title.includes('河南师范大学'))
  return {kind:'waiting', origin:location.origin, reason:'school-title-missing'};
if (!user || !pass || !button)
  return {kind:'waiting', origin:location.origin, reason:'form-elements-missing'};
if (!visible(user) || !visible(pass) || !visible(button))
  return {kind:'waiting', origin:location.origin, reason:'form-elements-hidden'};
const captcha = Array.from(document.querySelectorAll('input')).some(el =>
  visible(el) && /captcha|verifycode|validatecode|验证码/i.test(el.name + ' ' + el.id + ' ' + el.placeholder));
if (captcha) return {kind:'captcha'};
if (button.disabled || user.disabled || pass.disabled) return {kind:'waiting', origin:location.origin};
";

        public static string ProbeScript()
        {
            return "(() => {const result = (() => {" + Guard + @"
return {kind:'ready',origin:location.origin};})();
result.loaded = document.readyState === 'complete';
result.formPresent = !!document.querySelector('input[name=""userName""], input[name=""password""], #checkButton');
return result;
})()";
        }

        public static string SubmitScript(Settings settings)
        {
            string credentials = Json.Serialize(new { user = settings.UserName, password = settings.Password });
            return "(() => {" + Guard + @"
const credentials = " + credentials + @";
// Check the destination again immediately before touching either input.
const form = pass.form;
if (form && form.action) {
  const action = new URL(form.action, location.href);
  if (action.origin !== location.origin) return {kind:'untrusted', origin:action.origin};
}
window.__htuBeforeLogin = text;
const setValue = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
setValue.call(user, credentials.user);
user.dispatchEvent(new Event('input', {bubbles:true}));
user.dispatchEvent(new Event('change', {bubbles:true}));
setValue.call(pass, credentials.password);
pass.dispatchEvent(new Event('input', {bubbles:true}));
pass.dispatchEvent(new Event('change', {bubbles:true}));
button.click();
return {kind:'submitted'};
})()";
        }

        public static string RejectionScript()
        {
            return @"(() => {
const text = document.body ? document.body.innerText : '';
const changed = typeof window.__htuBeforeLogin === 'string' && text !== window.__htuBeforeLogin;
return {rejected: changed && /密码错误|密码不正确|用户名或密码|账号不存在|账户不存在|认证失败|登录失败|认证被拒绝/.test(text)};
})()";
        }
    }

    internal sealed class CampusNetwork
    {
        public bool CampusWifi;
        public bool OtherWifi;
        public bool Wired;
        internal bool IsOtherWifi { get { return OtherWifi && !CampusWifi && !Wired; } }
        internal bool HasCampusCandidate { get { return CampusWifi || Wired; } }

        internal static bool IsCampusSsid(string ssid)
        {
            return Regex.IsMatch(ssid ?? "", @"^(HTU|HTU_5G|HTU_Student|Htu-AuteWiFi5G-\d+)$", RegexOptions.IgnoreCase);
        }

        internal static CampusNetwork Read()
        {
            var context = new CampusNetwork();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType != NetworkInterfaceType.Ethernet) continue;
                foreach (var gateway in nic.GetIPProperties().GatewayAddresses)
                    if (gateway.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !gateway.Address.Equals(IPAddress.Any))
                        context.Wired = true;
            }
            // No profile credentials are queried. Only the connected SSID is used to distinguish a hotspot.
            var output = new StringBuilder();
            using (var process = new Process())
            {
                process.StartInfo = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "netsh.exe"), "wlan show interfaces")
                    { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true };
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
                process.Start();
                process.BeginOutputReadLine();
                if (!process.WaitForExit(4000)) { process.Kill(); return context; }
                process.WaitForExit();
            }
            foreach (Match match in Regex.Matches(output.ToString(), @"^\s*SSID\s*:\s*(.+?)\s*$", RegexOptions.Multiline))
            {
                if (IsCampusSsid(match.Groups[1].Value.Trim())) context.CampusWifi = true;
                else context.OtherWifi = true;
            }
            return context;
        }
    }

    public sealed class LoginEngine
    {
        public static string UserSuffix { get { return WindowsIdentity.GetCurrent().User.Value; } }

        public LoginResult RunOnce(Settings settings)
        { return RunOnce(settings, null); }

        public LoginResult RunOnce(Settings settings, WaitHandle stop)
        {
            if (settings == null || String.IsNullOrWhiteSpace(settings.UserName) || String.IsNullOrEmpty(settings.Password))
                return new LoginResult(LoginState.Error, "请先在设置窗口保存校园网账号和密码。");
            if (!PortalAutomation.IsTrustedUrl(settings.PortalUrl))
                return new LoginResult(LoginState.UnsupportedPortal, "认证地址未获支持，请使用 http://1.1.1.1/。");

            using (var mutex = new Mutex(false, @"Local\HTUAutoLogin.Login." + UserSuffix))
            {
                bool locked = false;
                bool submissionAttempted = false;
                try
                {
                    try { locked = mutex.WaitOne(0); } catch (AbandonedMutexException) { locked = true; }
                    if (!locked) return new LoginResult(LoginState.Waiting, "已有一次登录检查正在进行。");
                    CheckStop(stop);
                    var elapsed = Stopwatch.StartNew();
                    var network = CampusNetwork.Read();
                    Storage.Log("阶段耗时：网络识别 " + elapsed.ElapsedMilliseconds + " 毫秒。");
                    if (network.IsOtherWifi)
                        return new LoginResult(LoginState.Waiting, "当前连接的是其他 Wi-Fi（可能是手机热点），等待连接校园网；不会将热点联网视为校园网认证成功。");
                    if (!System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable())
                        return new LoginResult(LoginState.Waiting, "等待网络连接，请先连接 HTU / HTU_5G 或校园网网线。");

                    bool sawGateway = false;
                    string[] entries = PortalAutomation.EntryUrls(settings.PortalUrl);
                    var browserStart = Stopwatch.StartNew();
                    // Begin gateway loading while Edge initializes its profile and private control connection.
                    using (var browser = new PortalBrowser(entries[0]))
                    {
                      Storage.Log("阶段耗时：临时浏览器启动 " + browserStart.ElapsedMilliseconds + " 毫秒。");
                      for (int entryIndex = 0; entryIndex < entries.Length; entryIndex++)
                      {
                        string entry = entries[entryIndex];
                        CheckStop(stop);
                        var entryStart = Stopwatch.StartNew();
                        // The first entry is already loading; do not reload it and discard redirect parameters.
                        try { if (entryIndex != 0) browser.Navigate(entry); }
                        catch (Exception ex)
                        {
                            if (!(ex is OperationCanceledException) && !(ex is InvalidOperationException) && !(ex is WebSocketException)) throw;
                            CheckStop(stop);
                            Storage.Log("认证入口响应失败，尝试下一入口（" + ex.GetType().Name + "）。");
                            continue;
                        }
                        Dictionary<string, object> probe = null;
                        var timer = Stopwatch.StartNew();
                        Stopwatch settled = null;
                        bool quickInternetChecked = false;
                        while (timer.Elapsed.TotalSeconds < 10)
                        {
                            CheckStop(stop);
                            try { probe = browser.Evaluate(PortalAutomation.ProbeScript()); }
                            catch (InvalidOperationException) { Thread.Sleep(200); continue; }
                            string kind = GetString(probe, "kind");
                            if (GetString(probe, "origin") == "http://1.1.1.1") sawGateway = true;
                            if (kind == "ready") break;
                            if (kind == "captcha") return new LoginResult(LoginState.UnsupportedPortal, "登录页需要验证码，请在浏览器中手动登录。");
                            bool gatewayWithoutForm = IsSettledGatewayWithoutForm(probe);
                            bool publicPage = IsLoadedPublicPage(probe);
                            if (gatewayWithoutForm || publicPage)
                            {
                                if (settled == null) settled = Stopwatch.StartNew();
                                if (settled.ElapsedMilliseconds >= 1000)
                                {
                                    if (publicPage) break;
                                    if (!quickInternetChecked && network.HasCampusCandidate)
                                    {
                                        quickInternetChecked = true;
                                        if (TestInternet(1000))
                                        {
                                            CheckStop(stop);
                                            var freshProbe = browser.Evaluate(PortalAutomation.ProbeScript());
                                            var freshNetwork = CampusNetwork.Read();
                                            if (IsSettledGatewayWithoutForm(freshProbe) &&
                                                CanSkipLogin(freshNetwork.HasCampusCandidate, sawGateway, false, true))
                                            {
                                                Storage.Log("阶段耗时：已联网快速检查完成，检查总计 " + elapsed.ElapsedMilliseconds + " 毫秒。");
                                                return new LoginResult(LoginState.Online, "当前连接校园网络，网关页面无认证表单，且普通 HTTPS 网站可访问；本次无需提交登录。");
                                            }
                                        }
                                    }
                                }
                            }
                            else settled = null;
                            // about:blank / chrome-error are transient navigation states, never credential targets.
                            // Public entry pages may take a moment to redirect. They never receive credentials.
                            Thread.Sleep(200);
                        }
                        if (probe == null || GetString(probe, "kind") != "ready")
                        {
                            Storage.Log("入口未提供可识别的认证表单：" + SafeOrigin(entry) + "；识别结果=" + GetString(probe, "kind") + "/" + GetString(probe, "reason") + "；页面来源=" + SafeOrigin(GetString(probe, "origin")));
                            continue;
                        }

                        // A transport failure after this point can still mean the server received a login.
                        Storage.Log("阶段耗时：控制连接就绪后等待表单 " + entryStart.ElapsedMilliseconds + " 毫秒；检查开始到表单就绪 " + elapsed.ElapsedMilliseconds + " 毫秒。");
                        CheckStop(stop);
                        if (CampusNetwork.Read().IsOtherWifi)
                            return new LoginResult(LoginState.Waiting, "认证前网络已切换，等待重新连接校园网；未填写账号密码。");
                        Storage.Log("已识别河师大原生认证表单，提交一次登录。");
                        submissionAttempted = true;
                        var submitted = browser.Evaluate(PortalAutomation.SubmitScript(settings));
                        if (GetString(submitted, "kind") != "submitted")
                            return new LoginResult(LoginState.UnsupportedPortal, "登录页发生变化或表单目标不可信，已停止填写。");
                        for (int attempt = 0; attempt < 3; attempt++)
                        {
                            if (stop == null) Thread.Sleep(2000);
                            else if (stop.WaitOne(2000)) throw new OperationCanceledException();
                            if (TestInternet())
                            {
                                var afterLogin = browser.Evaluate(PortalAutomation.ProbeScript());
                                if (GetString(afterLogin, "kind") != "ready" && !CampusNetwork.Read().IsOtherWifi)
                                    return new LoginResult(LoginState.LoggedIn, "已提交校园网认证、登录表单已离开，并确认普通 HTTPS 网站连通。");
                            }
                            try
                            {
                                var rejected = browser.Evaluate(PortalAutomation.RejectionScript());
                                if (rejected.ContainsKey("rejected") && Object.Equals(rejected["rejected"], true))
                                    return new LoginResult(LoginState.Rejected, "认证页面提示登录失败，请检查账号、密码或校园网账户状态。");
                            }
                            catch (InvalidOperationException) { }
                        }
                        return new LoginResult(LoginState.Rejected, "已提交一次登录，但未确认联网；请检查校园网账户或在浏览器手动验证。");
                      }
                    }
                    CheckStop(stop);
                    // A captive-portal exemption for Microsoft NCSI, or internet over a phone hotspot,
                    // must never be interpreted as successful campus authentication.
                    var currentNetwork = CampusNetwork.Read();
                    if (CanSkipLogin(currentNetwork.HasCampusCandidate, sawGateway, false, TestInternet()))
                        return new LoginResult(LoginState.Online, "当前连接校园网络，未发现待认证表单，且普通 HTTPS 网站可访问；本次无需提交登录。");
                    return new LoginResult(LoginState.Waiting, "尚未取得有效的校园网认证页面，继续等待连接或重新获取认证跳转；未判定为登录成功。");
                }
                catch (BrowserUnavailableException)
                {
                    return new LoginResult(LoginState.Error, "未找到 Microsoft Edge，请安装 Edge 后再试。");
                }
                catch (OperationCanceledException)
                {
                    return CancellationResult(stop, submissionAttempted);
                }
                catch (Exception ex)
                {
                    // Exception messages can contain the Runtime.evaluate payload (including credentials).
                    // Never place exception text, browser URLs, page text or CDP frames in logs/status.
                    Storage.Log("登录检查异常，类型：" + ex.GetType().Name);
                    if (submissionAttempted)
                        return new LoginResult(LoginState.Rejected, "提交登录后未能确认结果，已计入失败次数以避免重复认证。");
                    return new LoginResult(LoginState.Error, "登录检查未完成（" + ex.GetType().Name + "），稍后重试。");
                }
                finally { if (locked) mutex.ReleaseMutex(); }
            }
        }

        private static void CheckStop(WaitHandle stop)
        { if (stop != null && stop.WaitOne(0)) throw new OperationCanceledException(); }

        internal static LoginResult CancellationResult(WaitHandle stop, bool submissionAttempted)
        {
            if (stop != null && stop.WaitOne(0))
                return new LoginResult(LoginState.Waiting, "自动登录检查已停止。");
            // CDP's internal timeout also throws OperationCanceledException; it is not a user stop.
            if (submissionAttempted)
                return new LoginResult(LoginState.Rejected, "提交登录后等待响应超时，已计入失败次数以避免重复认证。");
            return new LoginResult(LoginState.Error, "认证页面响应超时，等待网络恢复后重试。");
        }

        public static bool TestInternet()
        { return TestInternet(3500); }

        private static bool TestInternet(int timeout)
        {
            return CheckProbe("https://www.qq.com/", timeout) || CheckProbe("https://www.baidu.com/", timeout);
        }

        internal static bool IsSettledGatewayWithoutForm(Dictionary<string, object> probe)
        {
            return GetString(probe, "origin") == "http://1.1.1.1" && GetString(probe, "kind") == "waiting" &&
                probe.ContainsKey("loaded") && Object.Equals(probe["loaded"], true) &&
                probe.ContainsKey("formPresent") && Object.Equals(probe["formPresent"], false);
        }

        internal static bool IsLoadedPublicPage(Dictionary<string, object> probe)
        {
            Uri origin;
            return GetString(probe, "kind") == "untrusted" && probe.ContainsKey("loaded") && Object.Equals(probe["loaded"], true) &&
                Uri.TryCreate(GetString(probe, "origin"), UriKind.Absolute, out origin) &&
                (origin.Scheme == "http" || origin.Scheme == "https") && !PortalAutomation.IsTrustedUrl(origin.AbsoluteUri);
        }

        internal static bool CanSkipLogin(bool campusNetwork, bool gatewaySeen, bool loginFormSeen, bool httpsReachable)
        {
            return campusNetwork && gatewaySeen && !loginFormSeen && httpsReachable;
        }

        internal static bool IsInternetResponse(string url, int status, string body)
        {
            if (status != 200) return false;
            string keyword = url == "https://www.qq.com/" ? "腾讯" : url == "https://www.baidu.com/" ? "百度" : null;
            return keyword != null && Regex.IsMatch(body ?? "", @"<title\b[^>]*>[^<]*" + keyword, RegexOptions.IgnoreCase);
        }

        internal static bool ReadInternetResponse(string url, int status, TextReader reader)
        {
            if (status != 200) return false;
            var body = new StringBuilder();
            char[] buffer = new char[1024];
            while (body.Length < 32768)
            {
                int count = reader.Read(buffer, 0, Math.Min(buffer.Length, 32768 - body.Length));
                if (count == 0) break;
                body.Append(buffer, 0, count);
                // A matching title is sufficient; do not wait for trailing HTML.
                if (IsInternetResponse(url, status, body.ToString())) return true;
            }
            return false;
        }

        private static bool CheckProbe(string url, int timeout)
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Proxy = null;
                request.AllowAutoRedirect = false;
                request.Timeout = timeout;
                request.ReadWriteTimeout = timeout;
                request.UserAgent = "HTUAutoLogin/1.0";
                request.CachePolicy = new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.NoCacheNoStore);
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    return ReadInternetResponse(url, (int)response.StatusCode, reader);
                }
            }
            catch { return false; }
        }

        internal static string GetString(Dictionary<string, object> value, string key)
        { return value != null && value.ContainsKey(key) ? Convert.ToString(value[key]) : ""; }

        private static string SafeOrigin(string value)
        {
            Uri uri;
            if (Uri.TryCreate(value, UriKind.Absolute, out uri) && (uri.Scheme == "http" || uri.Scheme == "https"))
                return uri.GetLeftPart(UriPartial.Authority);
            return "未知地址";
        }
    }

    internal class BrowserUnavailableException : Exception { }

    // A fresh, hidden Edge profile is used only for the portal. The user's regular browser is untouched.
    internal sealed class PortalBrowser : IDisposable
    {
        private Process process;
        private Process browserProcess;
        private ClientWebSocket socket;
        private ClientWebSocket browserSocket;
        private BrowserJob job;
        private string profile;
        private int sequence;

        internal PortalBrowser() : this("about:blank") { }

        internal PortalBrowser(string initialUrl)
        {
            string initialArgument = InitialNavigationArgument(initialUrl);
            string edge = FindEdge();
            if (edge == null) throw new BrowserUnavailableException();
            profile = Path.Combine(Storage.DataDirectory, "browser-session-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(profile);
            try
            {
                job = new BrowserJob();
                process = Process.Start(new ProcessStartInfo(edge,
                    "--headless=new --disable-gpu --no-first-run --no-default-browser-check --disable-extensions " +
                    "--disable-background-networking --disable-sync --no-proxy-server --remote-debugging-port=0 " +
                    "--remote-debugging-address=127.0.0.1 --user-data-dir=\"" + profile + "\" " + initialArgument)
                    { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
                job.Assign(process);
                string portFile = Path.Combine(profile, "DevToolsActivePort");
                string[] lines = null;
                var timer = Stopwatch.StartNew();
                while (timer.Elapsed.TotalSeconds < 15)
                {
                    // Edge's launcher may exit after handing off to a child in the same job.
                    // Readiness is determined by the private DevToolsActivePort file.
                    if (File.Exists(portFile))
                    {
                        try { lines = File.ReadAllLines(portFile); } catch (IOException) { }
                        int parsed;
                        if (lines != null && lines.Length > 0 && Int32.TryParse(lines[0], out parsed) && parsed > 0 && parsed < 65536) break;
                    }
                    Thread.Sleep(200);
                }
                if (lines == null || lines.Length == 0) throw new TimeoutException();
                var versionRequest = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + lines[0] + "/json/version");
                versionRequest.Proxy = null; versionRequest.Timeout = 3000; versionRequest.ReadWriteTimeout = 3000;
                string versionJson;
                using (var response = versionRequest.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream())) versionJson = reader.ReadToEnd();
                var version = PortalAutomation.Json.Deserialize<Dictionary<string, object>>(versionJson);
                browserSocket = ConnectSocket(LoginEngine.GetString(version, "webSocketDebuggerUrl"), lines[0]);
                // Edge can hand off to a broker-created process before the launcher's job assignment.
                // Obtain the browser PID from this private DevTools endpoint, never by process name.
                var processInfo = Command("SystemInfo.getProcessInfo", new { }, browserSocket);
                var items = processInfo["processInfo"] as System.Collections.IEnumerable;
                if (items != null)
                {
                    foreach (object item in items)
                    {
                        var info = item as Dictionary<string, object>;
                        if (LoginEngine.GetString(info, "type") != "browser") continue;
                        browserProcess = Process.GetProcessById(Convert.ToInt32(info["id"]));
                        job.Assign(browserProcess);
                        break;
                    }
                }
                if (browserProcess == null) throw new InvalidOperationException();
                var request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + lines[0] + "/json/list");
                request.Proxy = null; request.Timeout = 3000; request.ReadWriteTimeout = 3000;
                string json;
                using (var response = request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream())) json = reader.ReadToEnd();
                var tabs = PortalAutomation.Json.Deserialize<List<Dictionary<string, object>>>(json);
                string endpoint = null;
                foreach (var tab in tabs)
                    if (LoginEngine.GetString(tab, "type") == "page") { endpoint = LoginEngine.GetString(tab, "webSocketDebuggerUrl"); break; }
                socket = ConnectSocket(endpoint, lines[0]);
                Command("Page.enable", new { });
            }
            catch { Dispose(); throw; }
        }

        private static string InitialNavigationArgument(string value)
        {
            if (value == "about:blank") return "about:blank";
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || !String.IsNullOrEmpty(uri.UserInfo) ||
                !(PortalAutomation.IsTrustedUrl(value) || (uri.IsLoopback && uri.Scheme == "http")))
                throw new ArgumentException("临时浏览器起始页必须为已支持的认证入口。");
            // Canonical URI escaping prevents the starting URL from introducing command-line switches.
            return "\"" + uri.AbsoluteUri + "\"";
        }

        private static ClientWebSocket ConnectSocket(string endpoint, string port)
        {
            Uri uri;
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out uri) || uri.Scheme != "ws" ||
                !uri.IsLoopback || uri.Port.ToString() != port) throw new InvalidOperationException();
            var connection = new ClientWebSocket();
            connection.Options.Proxy = null;
            try
            {
                using (var timeout = new CancellationTokenSource(5000)) connection.ConnectAsync(uri, timeout.Token).GetAwaiter().GetResult();
                return connection;
            }
            catch { connection.Dispose(); throw; }
        }

        private static string FindEdge()
        {
            string[] roots = { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) };
            foreach (string root in roots)
            {
                string path = Path.Combine(root, @"Microsoft\Edge\Application\msedge.exe");
                if (File.Exists(path)) return path;
            }
            return null;
        }

        internal void Navigate(string url) { Command("Page.navigate", new { url = url }); }

        internal Dictionary<string, object> Evaluate(string expression)
        {
            var response = Command("Runtime.evaluate", new { expression = expression, returnByValue = true });
            if (response.ContainsKey("exceptionDetails")) throw new InvalidOperationException();
            var remote = response["result"] as Dictionary<string, object>;
            if (remote == null || !remote.ContainsKey("value")) throw new InvalidOperationException();
            var value = remote["value"] as Dictionary<string, object>;
            if (value == null) throw new InvalidOperationException();
            return value;
        }

        private Dictionary<string, object> Command(string method, object parameters)
        { return Command(method, parameters, socket); }

        private Dictionary<string, object> Command(string method, object parameters, ClientWebSocket connection)
        {
            int id = ++sequence;
            byte[] bytes = Encoding.UTF8.GetBytes(PortalAutomation.Json.Serialize(new { id = id, method = method, @params = parameters }));
            using (var timeout = new CancellationTokenSource(8000))
            {
                connection.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, timeout.Token).GetAwaiter().GetResult();
                Array.Clear(bytes, 0, bytes.Length);
                while (true)
                {
                    using (var data = new MemoryStream())
                    {
                        var buffer = new byte[8192];
                        WebSocketReceiveResult received;
                        do
                        {
                            received = connection.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token).GetAwaiter().GetResult();
                            if (received.MessageType == WebSocketMessageType.Close) throw new InvalidOperationException();
                            data.Write(buffer, 0, received.Count);
                            if (data.Length > 4194304) throw new InvalidOperationException();
                        } while (!received.EndOfMessage);
                        var message = PortalAutomation.Json.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(data.ToArray()));
                        // Native JS alerts can otherwise stall the portal indefinitely. Dismiss without reading or logging text.
                        if (LoginEngine.GetString(message, "method") == "Page.javascriptDialogOpening")
                        {
                            byte[] dismiss = Encoding.UTF8.GetBytes(PortalAutomation.Json.Serialize(new { id = ++sequence,
                                method = "Page.handleJavaScriptDialog", @params = new { accept = true } }));
                            connection.SendAsync(new ArraySegment<byte>(dismiss), WebSocketMessageType.Text, true, timeout.Token).GetAwaiter().GetResult();
                        }
                        if (!message.ContainsKey("id") || Convert.ToInt32(message["id"]) != id) continue;
                        if (message.ContainsKey("error")) throw new InvalidOperationException();
                        return (Dictionary<string, object>)message["result"];
                    }
                }
            }
        }

        public void Dispose()
        {
            if (browserSocket != null)
            {
                // Browser.close is sent to the browser endpoint, independent of page navigation or dialogs.
                try { if (browserSocket.State == WebSocketState.Open) Command("Browser.close", new { }, browserSocket); } catch { }
                browserSocket.Dispose(); browserSocket = null;
            }
            if (socket != null)
            {
                socket.Dispose(); socket = null;
            }
            if (job != null) { job.Dispose(); job = null; }
            if (browserProcess != null)
            {
                try { if (!browserProcess.WaitForExit(2500)) { browserProcess.Kill(); browserProcess.WaitForExit(2500); } } catch { }
                browserProcess.Dispose(); browserProcess = null;
            }
            if (process != null)
            {
                try { if (!process.WaitForExit(2500)) process.Kill(); } catch { }
                process.Dispose(); process = null;
            }
            if (profile != null)
            {
                // Only delete this instance's generated profile, after checking the absolute target.
                string root = Path.GetFullPath(Storage.DataDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string target = Path.GetFullPath(profile);
                if (target.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(target).StartsWith("browser-session-", StringComparison.Ordinal))
                {
                    for (int i = 0; i < 25; i++)
                    {
                        try { if (Directory.Exists(target)) Directory.Delete(target, true); break; }
                        catch (IOException) { Thread.Sleep(200); }
                        catch (UnauthorizedAccessException) { Thread.Sleep(200); }
                    }
                }
                profile = null;
            }
        }
    }

    // Closing the job terminates only the Edge process tree created by this app, including renderer processes.
    internal sealed class BrowserJob : IDisposable
    {
        private IntPtr handle;
        public BrowserJob()
        {
            handle = CreateJobObject(IntPtr.Zero, null);
            if (handle == IntPtr.Zero) throw new InvalidOperationException();
            var limits = new ExtendedLimits();
            limits.Basic.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            int size = Marshal.SizeOf(typeof(ExtendedLimits));
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(limits, ptr, false);
                if (!SetInformationJobObject(handle, 9, ptr, (uint)size)) { Dispose(); throw new InvalidOperationException(); }
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }
        public void Assign(Process process)
        {
            bool alreadyAssigned;
            if (IsProcessInJob(process.Handle, handle, out alreadyAssigned) && alreadyAssigned) return;
            if (!AssignProcessToJobObject(handle, process.Handle)) throw new InvalidOperationException();
        }
        public void Dispose() { if (handle != IntPtr.Zero) { CloseHandle(handle); handle = IntPtr.Zero; } }
        [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
        { public long ProcessTime, JobTime; public uint LimitFlags; public UIntPtr MinWorkingSet, MaxWorkingSet; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
        [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong A, B, C, D, E, F; }
        [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
        { public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);
        [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll")] private static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr value);
    }
}
