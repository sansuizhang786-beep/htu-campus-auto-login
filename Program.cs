using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

namespace HtuAutoLogin
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--install-startup")
            {
                try { StartupRegistration.Enable(); Storage.Log("已启用 Windows 登录和睡眠恢复后的自动登录触发；此次只安装触发，不提交认证。"); return 0; }
                catch (Exception ex) { Storage.Log("安装自动登录触发失败：" + ex.GetType().Name); return 1; }
            }
            if (args.Length == 1 && args[0] == "--background")
            {
                try { BackgroundRunner.Run(); return 0; }
                catch (Exception ex) { Storage.Log("后台退出：" + ex.GetType().Name); return 1; }
            }
            if (args.Length == 1 && args[0] == "--once")
            {
                try
                {
                    Settings settings = Storage.Load();
                    if (settings == null) return 2;
                    LoginResult result = new LoginEngine().RunOnce(settings);
                    Storage.Log("单次运行：" + result.State.ToString());
                    return result.State == LoginState.Online || result.State == LoginState.LoggedIn ? 0 : 1;
                }
                catch (Exception ex) { Storage.Log("单次运行失败：" + ex.GetType().Name); return 1; }
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length != 0)
            {
                MessageBox.Show("请直接双击程序配置账号。支持的后台参数为 --background、--once 和 --install-startup。", "校园网自动登录");
                return 2;
            }
            string sid = WindowsIdentity.GetCurrent().User.Value;
            bool created;
            using (Mutex mutex = new Mutex(true, @"Local\HtuAutoLogin.Settings." + sid, out created))
            {
                if (!created)
                {
                    MessageBox.Show("设置窗口已经打开，请在任务栏找到“校园网自动登录”。", "校园网自动登录");
                    return 0;
                }
                try { Application.Run(new SettingsForm()); }
                finally { mutex.ReleaseMutex(); }
            }
            return 0;
        }
    }

    public sealed class SettingsForm : Form
    {
        private readonly TextBox userName = new TextBox();
        private readonly TextBox password = new TextBox();
        private readonly ComboBox portalUrl = new ComboBox();
        private readonly TextBox status = new TextBox();
        private readonly Label running = new Label();
        private readonly Button save = new Button();
        private readonly Button test = new Button();
        private readonly Button start = new Button();
        private readonly Button stop = new Button();
        private readonly Button disable = new Button();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private bool busy;
        private string lastWorkerStatus;

        public SettingsForm()
        {
            Text = "河南师范大学 · 校园网自动登录";
            Font = new Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(690, 590);
            MinimumSize = new Size(650, 590);
            StartPosition = FormStartPosition.CenterScreen;
            Icon = SystemIcons.Application;
            BackColor = Color.FromArgb(248, 250, 252);

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.Padding = new Padding(24, 18, 24, 18);
            layout.ColumnCount = 2;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowCount = 12;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
            Controls.Add(layout);

            Label title = MakeLabel("校园网自动登录", 17, Color.FromArgb(18, 51, 88));
            title.Font = new Font(Font.FontFamily, 17F, FontStyle.Bold);
            AddWide(layout, title, 0);
            Label help = MakeLabel("请先在 Windows 连接 HTU_Student 等校园网络，首次填写账号后保存启用。\n程序获取认证跳转并登录，确认成功后退出；不会自行切换 Wi-Fi。", 9, Color.FromArgb(70, 82, 98));
            AddWide(layout, help, 1);
            AddField(layout, "认证地址", portalUrl, 2);
            portalUrl.DropDownStyle = ComboBoxStyle.DropDown;
            portalUrl.Items.Add("http://1.1.1.1/");
            portalUrl.Text = "http://1.1.1.1/";
            AddField(layout, "校园网账号", userName, 3);
            AddField(layout, "校园网密码", password, 4);
            password.UseSystemPasswordChar = true;
            CheckBox showPassword = new CheckBox();
            showPassword.Text = "显示密码（密码在本机按当前 Windows 用户加密保存）";
            showPassword.AutoSize = true;
            showPassword.Dock = DockStyle.Fill;
            showPassword.CheckedChanged += delegate { password.UseSystemPasswordChar = !showPassword.Checked; };
            layout.Controls.Add(showPassword, 1, 5);

            FlowLayoutPanel actions = MakeActions();
            ConfigureButton(save, "保存并启用自动登录", 198);
            ConfigureButton(test, "测试登录", 112);
            save.BackColor = Color.FromArgb(32, 100, 176);
            save.ForeColor = Color.White;
            save.FlatStyle = FlatStyle.Flat;
            actions.Controls.Add(save);
            actions.Controls.Add(test);
            AddWide(layout, actions, 6);
            FlowLayoutPanel controlActions = MakeActions();
            ConfigureButton(start, "立即自动登录", 126);
            ConfigureButton(stop, "停止本次登录", 126);
            ConfigureButton(disable, "关闭自启动并停止", 162);
            controlActions.Controls.Add(start);
            controlActions.Controls.Add(stop);
            controlActions.Controls.Add(disable);
            AddWide(layout, controlActions, 7);
            running.Dock = DockStyle.Fill;
            running.TextAlign = ContentAlignment.MiddleLeft;
            running.ForeColor = Color.FromArgb(54, 75, 97);
            AddWide(layout, running, 8);
            status.Multiline = true;
            status.ReadOnly = true;
            status.ScrollBars = ScrollBars.Vertical;
            status.BackColor = Color.White;
            status.BorderStyle = BorderStyle.FixedSingle;
            status.Dock = DockStyle.Fill;
            status.Text = "尚未配置。请填写校园网账号和密码。";
            AddWide(layout, status, 9);
            LinkLabel openFolder = new LinkLabel();
            openFolder.Text = "打开配置与日志文件夹";
            openFolder.AutoSize = true;
            openFolder.Dock = DockStyle.Fill;
            openFolder.TextAlign = ContentAlignment.MiddleLeft;
            openFolder.LinkClicked += delegate
            {
                try
                {
                    Directory.CreateDirectory(Storage.DataDirectory);
                    Process.Start(new ProcessStartInfo("explorer.exe", "\"" + Storage.DataDirectory + "\"") { UseShellExecute = true });
                }
                catch (Exception ex) { ShowError("打开文件夹失败", ex); }
            };
            AddWide(layout, openFolder, 10);
            Label footnote = MakeLabel("登录 Windows 或睡眠恢复后自动运行；确认联网后自行退出，不常驻。\n请将程序保留在当前位置；移动后需重新保存启用。", 8, Color.FromArgb(89, 102, 117));
            AddWide(layout, footnote, 11);

            save.Click += SaveAndEnable;
            test.Click += TestLogin;
            start.Click += delegate { StartSaved(); };
            stop.Click += delegate
            {
                RunOperation(BackgroundRunner.Stop, "正在停止本次自动登录，请稍候。", "本次自动登录已停止。下次登录 Windows 时仍会自动启动。", "停止失败");
            };
            disable.Click += delegate
            {
                RunOperation(delegate
                {
                    StartupRegistration.Disable();
                    BackgroundRunner.Stop();
                }, "正在关闭自启动并停止本次自动登录，请稍候。", "已关闭当前 Windows 用户的开机自启动并停止本次自动登录。账号配置仍保存在本机。", "关闭自启动失败");
            };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (busy && e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    ShowStatus("正在完成当前操作，请稍候再关闭窗口。");
                }
            };
            FormClosed += delegate { timer.Stop(); timer.Dispose(); };
            Shown += delegate { LoadSettings(); RefreshStatus(); timer.Start(); };
            timer.Interval = 2000;
            timer.Tick += delegate { RefreshStatus(); };
        }

        private void LoadSettings()
        {
            try
            {
                Settings settings = Storage.Load();
                if (settings == null) return;
                portalUrl.Text = settings.PortalUrl;
                userName.Text = settings.UserName;
                password.Text = settings.Password;
                ShowStatus("已读取本机保存的账号配置。密码框中的圆点代表已保存的密码，可直接保留或修改。");
            }
            catch (Exception ex)
            {
                Storage.Log("读取配置失败：" + ex.GetType().Name);
                ShowStatus("无法读取已有配置。密码只能由保存它的 Windows 用户解密；请重新填写账号和密码后保存。");
            }
        }

        private Settings ReadForm()
        {
            Settings settings = new Settings();
            settings.PortalUrl = portalUrl.Text.Trim();
            settings.UserName = userName.Text;
            settings.Password = password.Text;
            Storage.Validate(settings);
            return settings;
        }

        private void SaveAndEnable(object sender, EventArgs args)
        {
            Settings settings;
            try
            {
                settings = ReadForm();
            }
            catch (Exception ex) { ShowError("请检查账号设置", ex); return; }
            RunOperation(delegate
            {
                Storage.Save(settings);
                StartupRegistration.Enable();
                BackgroundRunner.Stop();
                BackgroundRunner.Start();
            }, "正在保存账号设置并启动自动登录，请稍候。", "已保存配置并启用自动登录。本次自动登录已启动，确认联网成功后进程自动退出。\r\n以后每次登录 Windows 或从睡眠恢复时会静默运行；设置窗口可手动关闭。", "保存或启用自动登录失败");
        }

        private void StartSaved()
        {
            RunOperation(delegate
            {
                Settings settings = Storage.Load();
                if (settings == null) throw new InvalidOperationException("请先点击“保存并启用自动登录”保存账号配置。");
                BackgroundRunner.Start();
            }, "正在启动自动登录，请稍候。", "已使用上次保存的账号配置启动自动登录，确认联网成功后进程自动退出。表单修改需要保存后才会生效。", "启动失败");
        }

        private void RunOperation(Action operation, string pending, string completed, string failure)
        {
            SetBusy(true);
            ShowStatus(pending);
            BackgroundWorker worker = new BackgroundWorker();
            worker.DoWork += delegate { operation(); };
            worker.RunWorkerCompleted += delegate(object sender, RunWorkerCompletedEventArgs e)
            {
                SetBusy(false);
                if (e.Error != null) ShowError(failure, e.Error);
                else ShowStatus(completed);
                RefreshStatus();
                worker.Dispose();
            };
            worker.RunWorkerAsync();
        }

        private void TestLogin(object sender, EventArgs args)
        {
            Settings settings;
            try { settings = ReadForm(); }
            catch (Exception ex) { ShowError("请检查账号设置", ex); return; }
            SetBusy(true);
            ShowStatus("正在使用表单中的账号测试登录，请稍候（通常在一分钟内完成）。");
            BackgroundWorker worker = new BackgroundWorker();
            worker.DoWork += delegate(object workerSender, DoWorkEventArgs e)
            {
                e.Result = new LoginEngine().RunOnce(settings);
            };
            worker.RunWorkerCompleted += delegate(object workerSender, RunWorkerCompletedEventArgs e)
            {
                SetBusy(false);
                if (e.Error != null)
                {
                    Storage.Log("测试登录异常：" + e.Error.GetType().Name);
                    ShowStatus("测试未完成，网络或认证服务暂时不可用。请确认已连接校园网后重试。");
                }
                else
                {
                    LoginResult result = (LoginResult)e.Result;
                    ShowStatus("测试结果：" + StateName(result.State) + "\r\n" + result.Message);
                }
                worker.Dispose();
            };
            worker.RunWorkerAsync();
        }

        private void SetBusy(bool value)
        {
            busy = value;
            save.Enabled = test.Enabled = start.Enabled = stop.Enabled = disable.Enabled = !value;
        }

        private void RefreshStatus()
        {
            try
            {
                running.Text = "自动登录进程：" + (BackgroundRunner.IsRunning ? "运行中" : "未运行") +
                    "    |    登录/唤醒自动运行：" + (StartupRegistration.IsEnabled ? "已启用" : "未完整启用");
                string path = Path.Combine(Storage.DataDirectory, "status.txt");
                if (!busy && File.Exists(path))
                {
                    string contents;
                    using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (StreamReader reader = new StreamReader(stream))
                    {
                        char[] buffer = new char[8192];
                        int length = reader.Read(buffer, 0, buffer.Length);
                        contents = new string(buffer, 0, length).Trim();
                    }
                    if (!String.IsNullOrEmpty(contents) && contents != lastWorkerStatus)
                    {
                        lastWorkerStatus = contents;
                        status.Text = contents;
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private void ShowStatus(string message) { status.Text = message; }

        private void ShowError(string action, Exception error)
        {
            Storage.Log(action + "：" + error.GetType().Name);
            string message = error is ArgumentException || error is InvalidOperationException || error is TimeoutException ? error.Message :
                "请检查配置文件夹和程序位置是否可写，或稍后重试。";
            ShowStatus(action + "：" + message);
        }

        private static string StateName(LoginState state)
        {
            switch (state)
            {
                case LoginState.Online: return "已连接互联网";
                case LoginState.LoggedIn: return "认证成功";
                case LoginState.Waiting: return "等待校园网络";
                case LoginState.UnsupportedPortal: return "认证页面暂不支持";
                case LoginState.Rejected: return "认证未完成";
                default: return "暂时未能登录";
            }
        }

        private static Label MakeLabel(string text, float size, Color color)
        {
            Label label = new Label();
            label.Text = text;
            label.Font = new Font("Microsoft YaHei UI", size);
            label.ForeColor = color;
            label.Dock = DockStyle.Fill;
            label.TextAlign = ContentAlignment.MiddleLeft;
            return label;
        }

        private static void AddField(TableLayoutPanel layout, string label, Control control, int row)
        {
            layout.Controls.Add(MakeLabel(label, 9, Color.FromArgb(50, 65, 82)), 0, row);
            control.Dock = DockStyle.Top;
            control.Margin = new Padding(3, 8, 3, 3);
            layout.Controls.Add(control, 1, row);
        }

        private static void AddWide(TableLayoutPanel layout, Control control, int row)
        {
            layout.Controls.Add(control, 0, row);
            layout.SetColumnSpan(control, 2);
        }

        private static FlowLayoutPanel MakeActions()
        {
            FlowLayoutPanel panel = new FlowLayoutPanel();
            panel.Dock = DockStyle.Fill;
            panel.WrapContents = false;
            panel.Margin = Padding.Empty;
            return panel;
        }

        private static void ConfigureButton(Button button, string text, int width)
        {
            button.Text = text;
            button.Size = new Size(width, 34);
            button.Margin = new Padding(0, 4, 12, 4);
            button.UseVisualStyleBackColor = true;
        }
    }
}
