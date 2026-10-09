using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Xml;

namespace HtuAutoLogin
{
    // This test assembly redirects storage to a workspace folder and uses only fictitious credentials.
    internal static class Tests
    {
        private static int passed;
        private static void Assert(bool condition, string name)
        {
            if (!condition) throw new Exception("FAIL: " + name);
            passed++; Console.WriteLine("PASS: " + name);
        }
        private sealed class LimitedReader : StringReader
        {
            private readonly int chunkSize, maxReads;
            public int ReadCalls, CharactersRead;
            public LimitedReader(string text, int chunkSize, int maxReads) : base(text)
            { this.chunkSize = chunkSize; this.maxReads = maxReads; }
            public override int Read(char[] buffer, int index, int count)
            {
                if (++ReadCalls > maxReads) throw new InvalidOperationException("Unnecessary response read.");
                int read = base.Read(buffer, index, Math.Min(count, chunkSize));
                CharactersRead += read;
                return read;
            }
        }
        // A deliberately delayed response proves that startup navigation and CDP setup overlap.
        // The server listens only on loopback and never receives real credentials.
        private sealed class StartupPortalFixture : IDisposable
        {
            private readonly TcpListener listener;
            private readonly Thread thread;
            private readonly ManualResetEvent stopped = new ManualResetEvent(false);
            internal readonly ManualResetEvent RequestReceived = new ManualResetEvent(false);
            internal readonly ManualResetEvent ReleaseResponse = new ManualResetEvent(false);
            internal readonly string Url;
            private Exception failure;

            internal StartupPortalFixture()
            {
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                Url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/fixture";
                thread = new Thread(Serve) { IsBackground = true };
                thread.Start();
            }

            private void Serve()
            {
                try
                {
                    while (!stopped.WaitOne(0))
                    {
                        if (!listener.Pending()) { stopped.WaitOne(10); continue; }
                        using (var client = listener.AcceptTcpClient())
                        {
                            client.ReceiveTimeout = 1000; client.SendTimeout = 1000;
                            try
                            {
                                using (var stream = client.GetStream())
                                using (var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true))
                                {
                                    string line = reader.ReadLine();
                                    if (line == null) continue;
                                    bool fixtureRequest = line.StartsWith("GET /fixture ", StringComparison.Ordinal);
                                    int headerLength = line.Length;
                                    while (!String.IsNullOrEmpty(line = reader.ReadLine()))
                                        if ((headerLength += line.Length) > 16384) throw new IOException();
                                    if (fixtureRequest)
                                    {
                                        RequestReceived.Set();
                                        if (WaitHandle.WaitAny(new WaitHandle[] { ReleaseResponse, stopped }) == 1) return;
                                    }
                                    byte[] body = Encoding.UTF8.GetBytes("<!doctype html><html><head><meta charset='utf-8'>" +
                                        "<title>河南师范大学校园网登录</title><link rel='icon' href='data:,'></head><body>" +
                                        "<form><input name='userName'><input name='password' type='password'>" +
                                        "<button id='checkButton' type='button'>登录</button></form></body></html>");
                                    byte[] headers = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\n" +
                                        "Connection: close\r\nContent-Length: " + body.Length + "\r\n\r\n");
                                    stream.Write(headers, 0, headers.Length);
                                    stream.Write(body, 0, body.Length);
                                }
                            }
                            catch (IOException) { } // Chromium may close a speculative connection without a request.
                        }
                    }
                }
                catch (Exception ex) { if (!stopped.WaitOne(0)) failure = ex; }
            }

            public void Dispose()
            {
                stopped.Set(); ReleaseResponse.Set(); listener.Stop();
                if (!thread.Join(6000)) throw new InvalidOperationException("Local fixture server did not stop.");
                stopped.Dispose(); RequestReceived.Dispose(); ReleaseResponse.Dispose();
                if (failure != null) throw new InvalidOperationException("Local fixture server failed.", failure);
            }
        }
        private static void VerifyInitialNavigation(Settings settings)
        {
            bool refused = false;
            try { using (var browser = new PortalBrowser("http://evil.example/")) { } }
            catch (ArgumentException) { refused = true; }
            Assert(refused, "untrusted initial URL refused before browser or network launch");
            using (var fixture = new StartupPortalFixture())
            using (var browser = new PortalBrowser(fixture.Url))
            {
                Assert(fixture.RequestReceived.WaitOne(5000), "browser starts initial page request without a separate navigation command");
                var pending = browser.Evaluate("({formPresent:!!document.querySelector('[name=password]')})");
                Assert(!fixture.ReleaseResponse.WaitOne(0) && Object.Equals(pending["formPresent"], false),
                    "browser control is usable while the initial page response is still pending");
                fixture.ReleaseResponse.Set();
                var timer = System.Diagnostics.Stopwatch.StartNew();
                Dictionary<string, object> probe = null;
                while (timer.ElapsedMilliseconds < 5000)
                {
                    try { probe = browser.Evaluate(PortalAutomation.ProbeScript()); }
                    catch (InvalidOperationException) { Thread.Sleep(50); continue; }
                    if (Object.Equals(probe["loaded"], true) && Object.Equals(probe["formPresent"], true)) break;
                    Thread.Sleep(50);
                }
                Assert(probe != null && Object.Equals(probe["loaded"], true) && Object.Equals(probe["formPresent"], true),
                    "initial page completes without being reloaded after control setup");
                Assert(LoginEngine.GetString(probe, "kind") == "untrusted" &&
                    LoginEngine.GetString(browser.Evaluate(PortalAutomation.SubmitScript(settings)), "kind") == "untrusted" &&
                    LoginEngine.GetString(browser.Evaluate("({value:document.querySelector('[name=password]').value})"), "value") == "",
                    "startup navigation preserves origin guard and leaves local fixture password empty");
            }
            Assert(Directory.GetDirectories(Storage.DataDirectory, "browser-session-*").Length == 0,
                "browser started with pending navigation still removes its private profile on exit");
        }
        private static string Scoped(string expression, string host, string port)
        {
            string origin = "http://" + host + (port == "80" ? "" : ":" + port);
            return "(() => { const location = " + PortalAutomation.Json.Serialize(new { protocol = "http:", hostname = host, port = port, origin = origin }) + "; return " + expression + "; })()";
        }
        private static void Fixture(PortalBrowser browser)
        {
            browser.Evaluate(@"(() => {
document.title = '河南师范大学校园网登录';
document.body.innerHTML = '<form action=""http://10.101.2.194:6060/login""><input name=""userName""><input type=""password"" name=""password""><button id=""checkButton"" type=""button"">登录</button></form>';
window.testEvents = [];
for (const input of document.querySelectorAll('input')) {
  for (const name of ['input','change']) input.addEventListener(name, () => testEvents.push(input.name + ':' + name));
}
document.getElementById('checkButton').onclick = () => {
  window.testClicks = (window.testClicks || 0) + 1;
};
window.testClicks = 0;
return {ok:true};
})()");
        }
        private static void VerifyStartupExit(LoginState[] outcomes, int[] expectedWaits, string finalStatus, string label)
        {
            if (BackgroundRunner.IsRunning) throw new InvalidOperationException("An existing worker must not be interrupted by tests.");
            int attempts = 0;
            var waits = new List<int>();
            BackgroundRunner.Run(
                delegate(Settings config, System.Threading.WaitHandle stop)
                {
                    if (attempts >= outcomes.Length)
                    {
                        attempts++;
                        throw new InvalidOperationException("Unexpected login attempt after completion.");
                    }
                    return new LoginResult(outcomes[attempts++], "fixture");
                },
                delegate(System.Threading.WaitHandle stop, int milliseconds) { waits.Add(milliseconds); return false; });
            bool timingMatches = waits.Count == expectedWaits.Length;
            for (int i = 0; timingMatches && i < waits.Count; i++) timingMatches = waits[i] == expectedWaits[i];
            Assert(attempts == outcomes.Length && timingMatches, label + ": returns without another login or wait");
            Assert(!BackgroundRunner.IsRunning && File.ReadAllText(Path.Combine(Storage.DataDirectory, "status.txt")).Contains(finalStatus),
                label + ": releases worker and preserves completion status");
        }
        private static void VerifyResumeTask()
        {
            string taskName = "HTU-Campus-AutoLogin-Resume-Test-" + Guid.NewGuid().ToString("N");
            string markerName = "startup-fixture-" + Guid.NewGuid().ToString("N") + ".txt";
            string marker = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, markerName);
            string executable = System.Reflection.Assembly.GetExecutingAssembly().Location;
            string arguments = "--startup-fixture " + markerName;
            bool registered = false;
            try
            {
                StartupRegistration.RegisterTask(taskName, StartupRegistration.BuildResumeTaskXml(executable, arguments, LoginEngine.UserSuffix));
                registered = true;
                var doc = new XmlDocument();
                doc.XmlResolver = null;
                doc.LoadXml(StartupRegistration.ReadTaskXml(taskName));
                var ns = new XmlNamespaceManager(doc.NameTable);
                ns.AddNamespace("t", "http://schemas.microsoft.com/windows/2004/02/mit/task");
                Func<string, string> value = delegate(string xpath) { XmlNode node = doc.SelectSingleNode(xpath, ns); return node == null ? "" : node.InnerText; };
                string subscription = value("/t:Task/t:Triggers/t:EventTrigger/t:Subscription");
                Assert(subscription.Contains("Microsoft-Windows-Power-Troubleshooter") && subscription.Contains("EventID=1"),
                    "Windows accepts and preserves the wake event subscription");
                string logonUser = value("/t:Task/t:Triggers/t:LogonTrigger/t:UserId");
                string logonSid = logonUser.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase) ? logonUser :
                    ((System.Security.Principal.SecurityIdentifier)new System.Security.Principal.NTAccount(logonUser).Translate(typeof(System.Security.Principal.SecurityIdentifier))).Value;
                Assert(logonSid == LoginEngine.UserSuffix && Convert.ToInt32(StartupRegistration.ReadTaskScalar(taskName, "Definition", "Triggers", "Count")) == 2,
                    "logon and resume share one task and logon is scoped to current user");
                Assert(StartupRegistration.IsTaskEnabledXml(doc.OuterXml), "unified startup status handles Windows omitted default flags");
                var incomplete = (XmlDocument)doc.CloneNode(true);
                XmlNode logonNode = incomplete.SelectSingleNode("/t:Task/t:Triggers/t:LogonTrigger", ns);
                logonNode.ParentNode.RemoveChild(logonNode);
                Assert(!StartupRegistration.IsTaskEnabledXml(incomplete.OuterXml), "legacy resume-only task is not reported as fully migrated");
                string registeredUser = value("/t:Task/t:Principals/t:Principal/t:UserId");
                string registeredSid = registeredUser.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase) ? registeredUser :
                    ((System.Security.Principal.SecurityIdentifier)new System.Security.Principal.NTAccount(registeredUser).Translate(typeof(System.Security.Principal.SecurityIdentifier))).Value;
                int runLevel = Convert.ToInt32(StartupRegistration.ReadTaskScalar(taskName, "Definition", "Principal", "RunLevel"));
                Assert(registeredSid == LoginEngine.UserSuffix &&
                    value("/t:Task/t:Principals/t:Principal/t:LogonType") == "InteractiveToken" &&
                    runLevel == 0,
                    "wake task uses current user and least privilege (user match=" + (registeredSid == LoginEngine.UserSuffix) +
                    ", logon=" + value("/t:Task/t:Principals/t:Principal/t:LogonType") +
                    ", run level=" + runLevel + ")");
                Assert(value("/t:Task/t:Actions/t:Exec/t:Command") == executable && value("/t:Task/t:Actions/t:Exec/t:Arguments") == arguments,
                    "Windows preserves Unicode executable path and separate task arguments");
                Assert(!Convert.ToBoolean(StartupRegistration.ReadTaskScalar(taskName, "Definition", "Settings", "DisallowStartIfOnBatteries")) &&
                    !Convert.ToBoolean(StartupRegistration.ReadTaskScalar(taskName, "Definition", "Settings", "StopIfGoingOnBatteries")) &&
                    !Convert.ToBoolean(StartupRegistration.ReadTaskScalar(taskName, "Definition", "Settings", "RunOnlyIfNetworkAvailable")) &&
                    Convert.ToInt32(StartupRegistration.ReadTaskScalar(taskName, "Definition", "Settings", "MultipleInstances")) == 2,
                    "wake task can wait for campus network on battery without overlapping instances");
                StartupRegistration.StartTask(taskName);
                var timer = System.Diagnostics.Stopwatch.StartNew();
                while (!File.Exists(marker) && timer.ElapsedMilliseconds < 10000) System.Threading.Thread.Sleep(100);
                Assert(File.Exists(marker) && File.ReadAllText(marker) == "fixture started",
                    "Task Scheduler launches harmless fixture that exits without network access");
            }
            finally
            {
                if (registered) StartupRegistration.RemoveTask(taskName);
                if (File.Exists(marker)) File.Delete(marker);
            }
            bool removed = false;
            try { StartupRegistration.ReadTaskXml(taskName); }
            catch (FileNotFoundException) { removed = true; }
            catch (System.Runtime.InteropServices.COMException ex) { removed = ex.ErrorCode == unchecked((int)0x80070002); }
            Assert(removed, "temporary scheduled test task removed after verification");
        }
        public static int Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--startup-fixture")
            {
                if (Path.GetFileName(args[1]) != args[1] || !args[1].StartsWith("startup-fixture-", StringComparison.Ordinal) || !args[1].EndsWith(".txt")) return 2;
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, args[1]), "fixture started");
                return 0;
            }
            try
            {
                if (args.Length == 1 && args[0] == "--task-tests")
                {
                    VerifyResumeTask();
                    Console.WriteLine("All " + passed + " task tests passed.");
                    return 0;
                }
                Storage.DataDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data-" + Guid.NewGuid().ToString("N"));
                BackgroundRunner.InstanceScope = ".Test." + Guid.NewGuid().ToString("N");
                Assert(!Array.Exists(AppDomain.CurrentDomain.GetAssemblies(), a => a.GetName().Name == "System.Windows.Forms"), "background fixture starts without loading WinForms");
                Assert(Program.Main(new[] { "--once" }) == 2 &&
                    !Array.Exists(AppDomain.CurrentDomain.GetAssemblies(), a => a.GetName().Name == "System.Windows.Forms"),
                    "background entry handles missing fictitious settings without loading GUI or using network");
                Assert(PortalAutomation.IsTrustedUrl("http://1.1.1.1/"), "user-confirmed entry accepted");
                Assert(PortalAutomation.IsTrustedUrl("http://10.101.2.194:6060/path?dynamic=123"), "HTU dynamic portal URL accepted");
                Assert(!PortalAutomation.IsTrustedUrl("http://10.101.2.194:80/"), "unverified port rejected");
                Assert(!PortalAutomation.IsTrustedUrl("http://1.1.1.1.attacker.example/"), "host suffix rejected");
                Assert(!PortalAutomation.IsTrustedUrl("http://user:password@1.1.1.1/"), "URL credentials rejected");
                Assert(!PortalAutomation.IsTrustedUrl("https://1.1.1.1/"), "public Cloudflare HTTPS origin rejected");
                Assert(!PortalAutomation.IsTrustedUrl("javascript:alert(1)"), "non-HTTP scheme rejected");
                string[] entries = PortalAutomation.EntryUrls("http://10.101.2.194:6060/");
                Assert(entries[0] == "http://1.1.1.1/" && entries[1] == "http://www.edu.cn/" && entries[2] == "http://10.101.2.194:6060/", "bare authentication root does not bypass gateway redirect discovery");
                Assert(!CampusNetwork.IsCampusSsid("OPPO phone hotspot") && !CampusNetwork.IsCampusSsid("Home WiFi"), "hotspot and home Wi-Fi are not campus connections");
                Assert(CampusNetwork.IsCampusSsid("HTU_5G") && CampusNetwork.IsCampusSsid("HTU_Student"), "known HTU campus SSIDs recognized");
                Assert(!LoginEngine.IsInternetResponse("http://www.msftconnecttest.com/connecttest.txt", 200, "Microsoft Connect Test"), "Microsoft captive-portal exemption is not proof of authenticated internet");
                Assert(LoginEngine.IsInternetResponse("https://www.qq.com/", 200, "<html><title>腾讯网</title></html>"), "ordinary HTTPS page requires expected content");
                Assert(!LoginEngine.IsInternetResponse("https://www.qq.com/", 200, "") && !LoginEngine.IsInternetResponse("https://www.qq.com/", 302, "<title>腾讯网</title>"), "empty response and redirect never count as online");
                using (var reader = new LimitedReader("<title>腾讯网</title>", 1024, 1))
                    Assert(LoginEngine.ReadInternetResponse("https://www.qq.com/", 200, reader), "HTTPS check finishes before waiting for trailing content");
                using (var reader = new LimitedReader("<html><title>百度一下</title>", 3, 20))
                    Assert(LoginEngine.ReadInternetResponse("https://www.baidu.com/", 200, reader), "HTTPS title split across response chunks recognized");
                using (var reader = new StringReader("<html><title>校园认证</title><p>腾讯 百度</p></html>"))
                    Assert(!LoginEngine.ReadInternetResponse("https://www.qq.com/", 200, reader), "streamed portal content never passes expected title validation");
                using (var reader = new LimitedReader(new string('x', 32768) + "<title>腾讯网</title>", 1024, 32))
                    Assert(!LoginEngine.ReadInternetResponse("https://www.qq.com/", 200, reader) && reader.CharactersRead == 32768, "HTTPS check keeps bounded response size");
                using (var reader = new LimitedReader("<title>腾讯网</title>", 1024, 0))
                    Assert(!LoginEngine.ReadInternetResponse("https://www.qq.com/", 302, reader) && reader.ReadCalls == 0, "redirect response rejected without reading its body");
                Assert(!LoginEngine.CanSkipLogin(false, true, false, true), "switching to a hotspot cannot finish a campus login task");
                Assert(!LoginEngine.CanSkipLogin(true, false, false, true), "reachable internet without a responding campus gateway does not finish login");
                Assert(!LoginEngine.CanSkipLogin(true, true, true, true), "a visible login form takes priority over external internet access");
                Assert(LoginEngine.CanSkipLogin(true, true, false, true), "already-online exit requires campus network, gateway and HTTPS confirmation");
                var gatewayProbe = new Dictionary<string, object> { {"kind", "waiting"}, {"origin", "http://1.1.1.1"}, {"loaded", true}, {"formPresent", false} };
                Assert(LoginEngine.IsSettledGatewayWithoutForm(gatewayProbe), "fully loaded empty gateway permits a quick HTTPS check");
                gatewayProbe["loaded"] = false;
                Assert(!LoginEngine.IsSettledGatewayWithoutForm(gatewayProbe), "loading gateway cannot skip waiting for authentication form");
                gatewayProbe["loaded"] = true; gatewayProbe["formPresent"] = true;
                Assert(!LoginEngine.IsSettledGatewayWithoutForm(gatewayProbe), "partial authentication form prevents quick already-online exit");
                gatewayProbe["formPresent"] = false; gatewayProbe["origin"] = "http://evil.example";
                Assert(!LoginEngine.IsSettledGatewayWithoutForm(gatewayProbe) && !LoginEngine.IsSettledGatewayWithoutForm(null), "untrusted or missing gateway is never quick online evidence");
                var publicProbe = new Dictionary<string, object> { {"kind", "untrusted"}, {"origin", "https://www.edu.cn"}, {"loaded", true} };
                Assert(LoginEngine.IsLoadedPublicPage(publicProbe), "completed ordinary web page need not consume full portal wait");
                publicProbe["origin"] = "null";
                Assert(!LoginEngine.IsLoadedPublicPage(publicProbe), "about:blank and browser error states retain navigation readiness wait");
                Assert(new LoginEngine().RunOnce(null).State == LoginState.Error, "missing configuration handled without network");
                var settings = new Settings { PortalUrl = "http://1.1.1.1/", UserName = "20260001", Password = "密 &+='\"\\$%雪" };
                Storage.Save(settings);
                Settings restored = Storage.Load();
                Assert(restored.Password == settings.Password && restored.UserName == settings.UserName, "Windows DPAPI save/load round trip");
                string saved = File.ReadAllText(Path.Combine(Storage.DataDirectory, "settings.xml"));
                Assert(!saved.Contains(settings.Password) && !saved.Contains("<Password>"), "saved configuration contains no plaintext password");
                Storage.Save(settings);
                Assert(Storage.Load().Password == settings.Password, "atomic replacement of existing configuration");
                using (var alreadyStopped = new System.Threading.ManualResetEvent(true))
                    Assert(new LoginEngine().RunOnce(settings, alreadyStopped).State == LoginState.Waiting, "cancelled check returns before network or credential submission");
                using (var notStopped = new System.Threading.ManualResetEvent(false))
                {
                    Assert(LoginEngine.CancellationResult(notStopped, true).State == LoginState.Rejected, "post-submission CDP timeout consumes authentication failure budget");
                    Assert(LoginEngine.CancellationResult(notStopped, false).State == LoginState.Error, "pre-submission CDP timeout remains a network failure");
                }
                using (var browser = new PortalBrowser())
                {
                    Fixture(browser);
                    Assert(LoginEngine.GetString(browser.Evaluate(PortalAutomation.ProbeScript()), "kind") == "untrusted", "actual about:blank never receives credentials");
                    var formProbe = browser.Evaluate(Scoped(PortalAutomation.ProbeScript(), "1.1.1.1", "80"));
                    Assert(Object.Equals(formProbe["formPresent"], true) && !LoginEngine.IsSettledGatewayWithoutForm(formProbe), "real DOM login form retains priority over quick internet checks");
                    Assert(LoginEngine.GetString(browser.Evaluate(Scoped(PortalAutomation.ProbeScript(), "10.101.2.194", "6060")), "kind") == "ready", "real Chromium recognizes HTU form");
                    Assert(LoginEngine.GetString(browser.Evaluate(Scoped(PortalAutomation.SubmitScript(settings), "10.101.2.194", "6060")), "kind") == "submitted", "trusted form submitted");
                    var values = browser.Evaluate("({user:document.querySelector('[name=userName]').value,password:document.querySelector('[name=password]').value,clicks:window.testClicks,events:window.testEvents.length})");
                    Assert(LoginEngine.GetString(values, "user") == settings.UserName && LoginEngine.GetString(values, "password") == settings.Password, "special characters preserved without JavaScript injection");
                    Assert(Convert.ToInt32(values["clicks"]) == 1 && Convert.ToInt32(values["events"]) == 4, "exactly one click and both input/change events");

                    Fixture(browser);
                    Assert(LoginEngine.GetString(browser.Evaluate(Scoped(PortalAutomation.SubmitScript(settings), "evil.example", "80")), "kind") == "untrusted", "redirected hostile origin blocked");
                    Assert(LoginEngine.GetString(browser.Evaluate("({value:document.querySelector('[name=password]').value})"), "value") == "", "hostile page password field stays empty");
                    browser.Evaluate("(() => {document.querySelector('form').action='http://evil.example/'; return {ok:true};})()");
                    Assert(LoginEngine.GetString(browser.Evaluate(Scoped(PortalAutomation.SubmitScript(settings), "10.101.2.194", "6060")), "kind") == "untrusted", "external form destination blocked");
                    Assert(LoginEngine.GetString(browser.Evaluate("({value:document.querySelector('[name=password]').value})"), "value") == "", "external form receives no password");

                    Fixture(browser);
                    browser.Evaluate("(() => {document.getElementById('checkButton').style.display='none'; return {ok:true};})()");
                    Assert(LoginEngine.GetString(browser.Evaluate(Scoped(PortalAutomation.ProbeScript(), "10.101.2.194", "6060")), "kind") == "waiting", "hidden form never submitted");
                    Fixture(browser);
                    browser.Evaluate("(() => {document.querySelector('form').insertAdjacentHTML('beforeend','<input name=verifycode>'); return {ok:true};})()");
                    Assert(LoginEngine.GetString(browser.Evaluate(Scoped(PortalAutomation.SubmitScript(settings), "10.101.2.194", "6060")), "kind") == "captcha", "visible captcha blocks automatic submission");
                    Fixture(browser);
                    browser.Evaluate("(() => {document.title='Other site'; return {ok:true};})()");
                    Assert(LoginEngine.GetString(browser.Evaluate(Scoped(PortalAutomation.ProbeScript(), "10.101.2.194", "6060")), "kind") == "waiting", "unrecognized title rejected");
                    var partialProbe = browser.Evaluate(Scoped(PortalAutomation.ProbeScript(), "1.1.1.1", "80"));
                    Assert(!LoginEngine.IsSettledGatewayWithoutForm(partialProbe), "gateway with unexpected title and form elements cannot exit through fast path");
                    Fixture(browser);
                    browser.Evaluate(Scoped(PortalAutomation.SubmitScript(settings), "10.101.2.194", "6060"));
                    browser.Evaluate("(() => {document.body.insertAdjacentHTML('beforeend','<p>密码错误</p>'); return {ok:true};})()");
                    Assert(Object.Equals(browser.Evaluate(PortalAutomation.RejectionScript())["rejected"], true), "new visible failure detected");
                }
                Assert(Directory.GetDirectories(Storage.DataDirectory, "browser-session-*").Length == 0, "isolated browser profile removed after process exit");
                VerifyInitialNavigation(settings);
                if (!BackgroundRunner.IsRunning)
                {
                    int authenticationCalls = 0;
                    var thread = new System.Threading.Thread(delegate()
                    {
                        BackgroundRunner.Run(
                            delegate(Settings config, System.Threading.WaitHandle stop)
                            {
                                System.Threading.Interlocked.Increment(ref authenticationCalls);
                                return new LoginResult(LoginState.LoggedIn, "fixture");
                            },
                            delegate(System.Threading.WaitHandle stop, int milliseconds) { return stop.WaitOne(milliseconds); });
                    });
                    thread.Start();
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    while (!BackgroundRunner.IsRunning && timer.ElapsedMilliseconds < 2000) System.Threading.Thread.Sleep(1);
                    Assert(BackgroundRunner.IsRunning, "background instance mutex registered");
                    BackgroundRunner.Stop();
                    Assert(thread.Join(3000) && !BackgroundRunner.IsRunning && authenticationCalls == 0, "immediate stop cancels startup wait without authenticating");
                }
                VerifyStartupExit(new[] { LoginState.Online }, new[] { 500 }, "程序自动退出", "already online at startup");
                VerifyStartupExit(new[] { LoginState.LoggedIn }, new[] { 500 }, "程序自动退出", "new successful login");
                VerifyStartupExit(new[] { LoginState.Waiting, LoginState.LoggedIn }, new[] { 500, 2000 }, "程序自动退出", "network becomes ready");
                var waitingOutcomes = new LoginState[12];
                for (int i = 0; i < 11; i++) waitingOutcomes[i] = LoginState.Waiting;
                waitingOutcomes[11] = LoginState.LoggedIn;
                VerifyStartupExit(waitingOutcomes, new[] { 500, 2000, 2000, 2000, 2000, 2000, 5000, 5000, 5000, 5000, 5000, 15000 },
                    "程序自动退出", "slow network starts with short retries then backs off");
                VerifyStartupExit(new[] { LoginState.Error, LoginState.Error, LoginState.Error, LoginState.Error, LoginState.Error, LoginState.Error, LoginState.LoggedIn },
                    new[] { 500, 30000, 60000, 120000, 240000, 300000, 300000 }, "程序自动退出", "execution errors preserve capped backoff");
                VerifyStartupExit(new[] { LoginState.Rejected, LoginState.Waiting, LoginState.Rejected, LoginState.Rejected }, new[] { 500, 120000, 2000, 240000 },
                    "已退出本次自动登录", "network waiting never resets failed submission budget");
                VerifyStartupExit(new[] { LoginState.Rejected, LoginState.Rejected, LoginState.Rejected }, new[] { 500, 120000, 240000 },
                    "已退出本次自动登录", "three failed submissions");
                VerifyResumeTask();
                Console.WriteLine("All " + passed + " tests passed.");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.ToString()); return 1; }
        }
    }
}
