using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace HtuAutoLogin
{
    public class Settings
    {
        public string PortalUrl = "http://1.1.1.1/";
        public string UserName = "";
        public string Password = "";
    }

    public static class Storage
    {
        public static string DataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HtuAutoLogin");
        private static readonly object LogLock = new object();
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("HtuAutoLogin.Config.v1");

        public static Settings Load()
        {
            string path = Path.Combine(DataDirectory, "settings.xml");
            if (!File.Exists(path)) return null;
            XmlDocument doc = new XmlDocument();
            doc.XmlResolver = null;
            XmlReaderSettings readerSettings = new XmlReaderSettings();
            readerSettings.DtdProcessing = DtdProcessing.Prohibit;
            readerSettings.XmlResolver = null;
            readerSettings.MaxCharactersInDocument = 65536;
            using (XmlReader reader = XmlReader.Create(path, readerSettings)) doc.Load(reader);
            XmlElement root = doc.DocumentElement;
            if (root == null || root.Name != "HtuAutoLogin" || root.GetAttribute("version") != "1")
                throw new InvalidDataException("配置格式不正确，请重新保存账号设置。");
            Settings settings = new Settings();
            settings.PortalUrl = ReadElement(root, "PortalUrl");
            settings.UserName = ReadElement(root, "UserName");
            byte[] encrypted = Convert.FromBase64String(ReadElement(root, "ProtectedPassword"));
            byte[] plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            try { settings.Password = Encoding.UTF8.GetString(plain); }
            finally { Array.Clear(plain, 0, plain.Length); }
            Validate(settings);
            return settings;
        }

        public static void Save(Settings settings)
        {
            Validate(settings);
            Directory.CreateDirectory(DataDirectory);
            byte[] plain = Encoding.UTF8.GetBytes(settings.Password);
            byte[] encrypted;
            try { encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser); }
            finally { Array.Clear(plain, 0, plain.Length); }
            XmlDocument doc = new XmlDocument();
            XmlElement root = doc.CreateElement("HtuAutoLogin");
            root.SetAttribute("version", "1");
            doc.AppendChild(root);
            AppendElement(doc, root, "PortalUrl", settings.PortalUrl.Trim());
            AppendElement(doc, root, "UserName", settings.UserName);
            AppendElement(doc, root, "ProtectedPassword", Convert.ToBase64String(encrypted));
            string path = Path.Combine(DataDirectory, "settings.xml");
            string temporary = Path.Combine(DataDirectory, "settings." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                XmlWriterSettings writerSettings = new XmlWriterSettings();
                writerSettings.Encoding = new UTF8Encoding(false);
                writerSettings.Indent = true;
                using (XmlWriter writer = XmlWriter.Create(temporary, writerSettings)) doc.Save(writer);
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        public static void Validate(Settings settings)
        {
            if (settings == null) throw new ArgumentException("请先填写校园网账号和密码。");
            Uri uri;
            if (String.IsNullOrWhiteSpace(settings.PortalUrl) ||
                !Uri.TryCreate(settings.PortalUrl.Trim(), UriKind.Absolute, out uri) ||
                uri.Scheme != Uri.UriSchemeHttp || !String.IsNullOrEmpty(uri.UserInfo) ||
                !String.IsNullOrEmpty(uri.Query) || !String.IsNullOrEmpty(uri.Fragment) ||
                !((uri.Host == "1.1.1.1" && uri.Port == 80) ||
                  (uri.Host == "10.101.2.194" && uri.Port == 6060)))
                throw new ArgumentException("认证地址仅支持 http://1.1.1.1/ 或 http://10.101.2.194:6060/ 下的页面，不可含查询参数。");
            if (String.IsNullOrWhiteSpace(settings.UserName))
                throw new ArgumentException("请填写校园网账号。");
            foreach (char c in settings.UserName)
                if (Char.IsWhiteSpace(c) || Char.IsControl(c))
                    throw new ArgumentException("校园网账号不能包含空格、换行或其他空白字符。");
            if (String.IsNullOrEmpty(settings.Password))
                throw new ArgumentException("请填写校园网密码。");
        }

        public static void Log(string message)
        {
            // Callers provide operational summaries only: no account, password, URL query or response body.
            lock (LogLock)
            {
                try
                {
                    Directory.CreateDirectory(DataDirectory);
                    string path = Path.Combine(DataDirectory, "log.txt");
                    if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024)
                    {
                        string previous = Path.Combine(DataDirectory, "log.previous.txt");
                        if (File.Exists(previous)) File.Delete(previous);
                        File.Move(path, previous);
                    }
                    string line = (message ?? "").Replace('\r', ' ').Replace('\n', ' ');
                    if (line.Length > 2000) line = line.Substring(0, 2000);
                    File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + line + Environment.NewLine,
                        new UTF8Encoding(false));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static string ReadElement(XmlElement root, string name)
        {
            XmlNode node = root.SelectSingleNode(name);
            if (node == null) throw new InvalidDataException("配置不完整，请重新保存账号设置。");
            return node.InnerText;
        }

        private static void AppendElement(XmlDocument doc, XmlElement root, string name, string value)
        {
            XmlElement element = doc.CreateElement(name);
            element.InnerText = value;
            root.AppendChild(element);
        }
    }

    internal static class StartupRegistration
    {
        private const string ShortcutName = "HTU-Campus-AutoLogin.lnk";
        private const string TaskNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        internal static string ResumeTaskName { get { return "HTU-Campus-AutoLogin-Resume-" + LoginEngine.UserSuffix; } }
        private static string ShortcutPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), ShortcutName); }
        }

        public static bool IsEnabled
        {
            get
            {
                try { return IsTaskEnabledXml(ReadTaskXml(ResumeTaskName)); }
                catch { return false; }
            }
        }

        public static void Enable()
        {
            string executable = Assembly.GetExecutingAssembly().Location;
            RegisterTask(ResumeTaskName, BuildResumeTaskXml(executable, "--background", LoginEngine.UserSuffix));
            if (!IsEnabled) throw new InvalidOperationException("登录/恢复触发未完整启用，保留原有启动入口，请稍后重试。");
            // Migrate only after confirming the unified task. Explorer's delayed shortcut caused a second run.
            if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
        }

        public static void Disable()
        {
            RemoveTask(ResumeTaskName);
            if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
        }

        internal static string BuildResumeTaskXml(string executable, string arguments, string sid)
        {
            var output = new StringBuilder();
            using (var writer = XmlWriter.Create(output, new XmlWriterSettings { OmitXmlDeclaration = true, Indent = true }))
            {
                writer.WriteStartElement("Task", TaskNamespace);
                writer.WriteAttributeString("version", "1.2");
                writer.WriteStartElement("RegistrationInfo");
                writer.WriteElementString("Description", "河南师范大学校园网自动登录：当前用户登录或睡眠/休眠恢复后运行，确认联网后退出。");
                writer.WriteEndElement();
                writer.WriteStartElement("Triggers");
                writer.WriteStartElement("LogonTrigger");
                writer.WriteElementString("Enabled", "true");
                writer.WriteElementString("UserId", sid);
                writer.WriteEndElement();
                writer.WriteStartElement("EventTrigger");
                writer.WriteElementString("Enabled", "true");
                writer.WriteElementString("Subscription", "<QueryList><Query Id=\"0\" Path=\"System\"><Select Path=\"System\">*[System[Provider[@Name='Microsoft-Windows-Power-Troubleshooter'] and EventID=1]]</Select></Query></QueryList>");
                writer.WriteEndElement();
                writer.WriteEndElement();
                writer.WriteStartElement("Principals");
                writer.WriteStartElement("Principal");
                writer.WriteAttributeString("id", "CurrentUser");
                writer.WriteElementString("UserId", sid);
                writer.WriteElementString("LogonType", "InteractiveToken");
                writer.WriteElementString("RunLevel", "LeastPrivilege");
                writer.WriteEndElement();
                writer.WriteEndElement();
                writer.WriteStartElement("Settings");
                writer.WriteElementString("MultipleInstancesPolicy", "IgnoreNew");
                writer.WriteElementString("DisallowStartIfOnBatteries", "false");
                writer.WriteElementString("StopIfGoingOnBatteries", "false");
                writer.WriteElementString("StartWhenAvailable", "true");
                writer.WriteElementString("RunOnlyIfNetworkAvailable", "false");
                writer.WriteElementString("AllowStartOnDemand", "true");
                writer.WriteElementString("Enabled", "true");
                writer.WriteElementString("WakeToRun", "false");
                writer.WriteElementString("ExecutionTimeLimit", "PT0S");
                writer.WriteEndElement();
                writer.WriteStartElement("Actions");
                writer.WriteAttributeString("Context", "CurrentUser");
                writer.WriteStartElement("Exec");
                writer.WriteElementString("Command", Path.GetFullPath(executable));
                writer.WriteElementString("Arguments", arguments);
                writer.WriteElementString("WorkingDirectory", Path.GetDirectoryName(Path.GetFullPath(executable)));
                writer.WriteEndElement();
                writer.WriteEndElement();
                writer.WriteEndElement();
            }
            return output.ToString();
        }

        internal static bool IsTaskEnabledXml(string xml)
        {
            var doc = new XmlDocument();
            doc.XmlResolver = null;
            doc.LoadXml(xml);
            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("t", TaskNamespace);
            XmlNode enabled = doc.SelectSingleNode("/t:Task/t:Settings/t:Enabled", ns);
            if (enabled != null && enabled.InnerText != "true") return false;
            foreach (string kind in new[] { "LogonTrigger", "EventTrigger" })
            {
                XmlNode trigger = doc.SelectSingleNode("/t:Task/t:Triggers/t:" + kind, ns);
                if (trigger == null) return false;
                XmlNode flag = trigger.SelectSingleNode("t:Enabled", ns);
                // Windows omits default-valued true flags in registered XML.
                if (flag != null && flag.InnerText != "true") return false;
            }
            return true;
        }

        // Task Scheduler uses the current interactive user token; no Windows password is stored.
        internal static void RegisterTask(string name, string xml)
        {
            WithTaskFolder(delegate(object folder)
            {
                object task = Call(folder, "RegisterTask", name, xml, 6, LoginEngine.UserSuffix, null, 3, null);
                Release(task);
                return 0;
            });
        }

        internal static string ReadTaskXml(string name)
        { return Convert.ToString(ReadTaskScalar(name, "Xml")); }

        internal static object ReadTaskScalar(string name, params string[] properties)
        {
            return WithTaskFolder(delegate(object folder)
            {
                var values = new List<object>();
                try
                {
                    object value = Call(folder, "GetTask", name);
                    values.Add(value);
                    foreach (string property in properties)
                    {
                        value = value.GetType().InvokeMember(property, BindingFlags.GetProperty, null, value, null);
                        values.Add(value);
                    }
                    return value;
                }
                finally { for (int i = values.Count - 1; i >= 0; i--) Release(values[i]); }
            });
        }

        internal static void StartTask(string name)
        {
            WithTaskFolder(delegate(object folder)
            {
                object task = Call(folder, "GetTask", name);
                try { Release(Call(task, "Run", (object)null)); }
                finally { Release(task); }
                return 0;
            });
        }

        internal static void RemoveTask(string name)
        {
            try { WithTaskFolder(delegate(object folder) { Call(folder, "DeleteTask", name, 0); return 0; }); }
            catch (FileNotFoundException) { }
            catch (COMException ex) { if (ex.ErrorCode != unchecked((int)0x80070002)) throw; }
        }

        private static T WithTaskFolder<T>(Func<object, T> action)
        {
            Type type = Type.GetTypeFromProgID("Schedule.Service");
            if (type == null) throw new InvalidOperationException("Windows 任务计划程序不可用，未能设置唤醒后自动登录。");
            object service = null, folder = null;
            try
            {
                service = Activator.CreateInstance(type);
                Call(service, "Connect", Type.Missing, Type.Missing, Type.Missing, Type.Missing);
                folder = Call(service, "GetFolder", "\\");
                return action(folder);
            }
            finally { Release(folder); Release(service); }
        }

        private static object Call(object target, string name, params object[] arguments)
        {
            try { return target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, arguments); }
            catch (TargetInvocationException ex) { if (ex.InnerException != null) throw ex.InnerException; throw; }
        }

        private static void Release(object value)
        {
            if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
        }

    }
}
