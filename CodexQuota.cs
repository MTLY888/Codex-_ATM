using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using Ellipse = System.Windows.Shapes.Ellipse;
using System.Windows.Threading;

namespace CodexQuotaDesktop
{
    public sealed class QuotaData
    {
        public int RemainingPercent;
        public int UsedPercent;
        public DateTime? ResetAt;
        public string Plan;
        public DateTime SyncedAt;
    }

    public enum CodexTaskState { Idle, Running, WaitingForConfirmation }

    public sealed class CodexTaskSnapshot
    {
        public CodexTaskState State;
        public long EventId;
        public DateTime EventAt;
    }

    public static class CodexTaskReader
    {
        private const string StatusSql =
            "with events as (" +
            "select id, ts, feedback_log_body from logs " +
            "where id > (select coalesce(max(id),0)-50000 from logs) and target='codex_app_server::outgoing_message' and (" +
            "feedback_log_body like 'app-server event: turn/started %' or " +
            "feedback_log_body like 'app-server event: turn/completed %' or " +
            "feedback_log_body like 'app-server event: item/started %' or " +
            "feedback_log_body like 'app-server event: thread/status/changed %' or " +
            "feedback_log_body like 'app-server event: serverRequest/resolved %' or " +
            "feedback_log_body like '%item/commandExecution/requestApproval%' or " +
            "feedback_log_body like '%item/fileChange/requestApproval%' or " +
            "feedback_log_body like '%item/permissions/requestApproval%' or " +
            "feedback_log_body like '%item/tool/requestUserInput%' or " +
            "feedback_log_body like '%mcpServer/elicitation/request%'))," +
            "markers as (" +
            "select current.id, current.ts, case " +
            "when current.feedback_log_body like '%item/commandExecution/requestApproval%' or " +
            "current.feedback_log_body like '%item/fileChange/requestApproval%' or " +
            "current.feedback_log_body like '%item/permissions/requestApproval%' or " +
            "current.feedback_log_body like '%item/tool/requestUserInput%' or " +
            "current.feedback_log_body like '%mcpServer/elicitation/request%' then 'waiting' " +
            "when current.feedback_log_body like 'app-server event: turn/completed %' then 'idle' " +
            "when current.feedback_log_body like 'app-server event: serverRequest/resolved %' then 'running' " +
            "when current.feedback_log_body like 'app-server event: thread/status/changed %' and " +
            "(select previous.feedback_log_body from events previous where previous.id < current.id order by previous.id desc limit 1) " +
            "like 'app-server event: item/started %' then 'waiting' " +
            "when current.feedback_log_body like 'app-server event: thread/status/changed %' then 'running' " +
            "when current.feedback_log_body like 'app-server event: turn/started %' then 'running' " +
            "else null end as state from events current) " +
            "select id, ts, state from markers where state is not null order by id desc limit 1;";
        private static CodexTaskSnapshot lastRemote;
        private static string lastRemoteKey;

        private sealed class RemoteConfig
        {
            public string Host;
            public string User;
            public int Port;

            public static RemoteConfig Load()
            {
                try
                {
                    string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "remote.ini");
                    if (!File.Exists(path)) return null;
                    RemoteConfig config = new RemoteConfig { Port = 22 };
                    foreach (string rawLine in File.ReadAllLines(path))
                    {
                        string line = rawLine.Trim();
                        if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                        int equals = line.IndexOf('=');
                        if (equals <= 0) continue;
                        string key = line.Substring(0, equals).Trim().ToLowerInvariant();
                        string value = line.Substring(equals + 1).Trim();
                        if (key == "host") config.Host = value;
                        else if (key == "user") config.User = value;
                        else if (key == "port")
                        {
                            int port;
                            if (Int32.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out port)) config.Port = port;
                        }
                    }
                    if (!IsSafeSshValue(config.Host) || !IsSafeSshValue(config.User) || config.Port < 1 || config.Port > 65535) return null;
                    return config;
                }
                catch { return null; }
            }
        }

        private static bool IsSafeSshValue(string value)
        {
            if (String.IsNullOrWhiteSpace(value) || value.StartsWith("-", StringComparison.Ordinal)) return false;
            foreach (char c in value)
            {
                if (Char.IsWhiteSpace(c) || c == '"' || c == '\'') return false;
            }
            return true;
        }

        private static string FindSqlite()
        {
            string bundled = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sqlite3.exe");
            return File.Exists(bundled) ? bundled : "sqlite3.exe";
        }

        public static CodexTaskSnapshot Read()
        {
            CodexTaskSnapshot local = ReadLocal();
            RemoteConfig config = RemoteConfig.Load();
            string remoteKey = config == null ? null : config.User + "@" + config.Host + ":" + config.Port.ToString(CultureInfo.InvariantCulture);
            if (!String.Equals(remoteKey, lastRemoteKey, StringComparison.OrdinalIgnoreCase))
            {
                lastRemote = null;
                lastRemoteKey = remoteKey;
            }

            CodexTaskSnapshot remote = ReadRemote(config);
            if (remote != null)
            {
                lastRemote = remote;
            }
            else if (config != null)
            {
                // SSH 读取失败只代表状态未知，不能把它误判为任务已经结束。
                remote = lastRemote;
            }
            return Merge(local, remote);
        }

        private static CodexTaskSnapshot ReadLocal()
        {
            try
            {
                string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string database = Path.Combine(profile, ".codex", "logs_2.sqlite");
                if (!File.Exists(database)) return null;

                ProcessStartInfo info = new ProcessStartInfo();
                info.FileName = FindSqlite();
                info.Arguments = "-readonly \"" + database.Replace("\"", "\"\"") + "\" \"" + StatusSql.Replace("\"", "\"\"") + "\"";
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.RedirectStandardOutput = true;
                info.RedirectStandardError = true;

                using (Process process = Process.Start(info))
                {
                    Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
                    Task<string> errorTask = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(2500))
                    {
                        try { process.Kill(); } catch { }
                        return null;
                    }
                    return ParseSnapshot(outputTask.Result);
                }
            }
            catch { return null; }
        }

        private static CodexTaskSnapshot ReadRemote(RemoteConfig config)
        {
            if (config == null) return null;
            try
            {
                string encodedSql = Convert.ToBase64String(Encoding.UTF8.GetBytes(StatusSql));
                string script =
                    "import base64,os,sqlite3\n" +
                    "p=os.path.expanduser('~/.codex/logs_2.sqlite')\n" +
                    "q=base64.b64decode('" + encodedSql + "').decode('utf-8')\n" +
                    "try:\n" +
                    " c=sqlite3.connect('file:'+p+'?mode=ro',uri=True,timeout=2)\n" +
                    " r=c.execute(q).fetchone()\n" +
                    " print('|'.join(str(v) for v in r) if r else '0|0|idle')\n" +
                    "except Exception:\n" +
                    " print('ERROR')\n";

                ProcessStartInfo info = new ProcessStartInfo();
                info.FileName = "ssh.exe";
                info.Arguments = "-o BatchMode=yes -o ConnectTimeout=4 -o ConnectionAttempts=1 -p " + config.Port.ToString(CultureInfo.InvariantCulture) + " " + config.User + "@" + config.Host + " python3 -";
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.RedirectStandardInput = true;
                info.RedirectStandardOutput = true;
                info.RedirectStandardError = true;

                using (Process process = Process.Start(info))
                {
                    Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
                    Task<string> errorTask = process.StandardError.ReadToEndAsync();
                    process.StandardInput.Write(script);
                    process.StandardInput.Close();
                    if (!process.WaitForExit(7000))
                    {
                        try { process.Kill(); } catch { }
                        return null;
                    }
                    return ParseSnapshot(outputTask.Result);
                }
            }
            catch { return null; }
        }

        private static CodexTaskSnapshot ParseSnapshot(string output)
        {
            if (String.IsNullOrWhiteSpace(output)) return null;
            string[] lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int index = lines.Length - 1; index >= 0; index--)
            {
                string[] parts = lines[index].Trim().Split('|');
                long id, unix;
                if (parts.Length != 3 ||
                    !Int64.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out id) ||
                    !Int64.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out unix)) continue;
                CodexTaskState state = parts[2] == "waiting" ? CodexTaskState.WaitingForConfirmation : (parts[2] == "idle" ? CodexTaskState.Idle : CodexTaskState.Running);
                DateTime eventAt = DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime;
                if (state == CodexTaskState.Running && DateTime.Now - eventAt > TimeSpan.FromHours(6)) state = CodexTaskState.Idle;
                return new CodexTaskSnapshot { State = state, EventId = id, EventAt = eventAt };
            }
            return null;
        }

        private static CodexTaskSnapshot Merge(CodexTaskSnapshot local, CodexTaskSnapshot remote)
        {
            CodexTaskState state;
            if ((local != null && local.State == CodexTaskState.WaitingForConfirmation) || (remote != null && remote.State == CodexTaskState.WaitingForConfirmation))
                state = CodexTaskState.WaitingForConfirmation;
            else if ((local != null && local.State == CodexTaskState.Running) || (remote != null && remote.State == CodexTaskState.Running))
                state = CodexTaskState.Running;
            else
                state = CodexTaskState.Idle;
            DateTime eventAt = DateTime.MinValue;
            if (local != null && local.EventAt > eventAt) eventAt = local.EventAt;
            if (remote != null && remote.EventAt > eventAt) eventAt = remote.EventAt;
            return new CodexTaskSnapshot { State = state, EventAt = eventAt };
        }
    }
    public static class CodexReader
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        private static string FindCodex()
        {
            string local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (!String.IsNullOrEmpty(local))
            {
                string installed = Path.Combine(local, "Programs", "OpenAI", "Codex", "bin", "codex.exe");
                if (File.Exists(installed)) return installed;
            }
            return "codex.exe";
        }

        private static void Send(Process process, object value)
        {
            process.StandardInput.WriteLine(Json.Serialize(value));
            process.StandardInput.Flush();
        }

        private static IDictionary<string, object> AsDictionary(object value)
        {
            return value as IDictionary<string, object>;
        }

        private static object Value(IDictionary<string, object> source, string key)
        {
            object result;
            return source != null && source.TryGetValue(key, out result) ? result : null;
        }

        private static IDictionary<string, object> Child(IDictionary<string, object> source, string key)
        {
            return AsDictionary(Value(source, key));
        }

        private static string ErrorMessage(IDictionary<string, object> response)
        {
            IDictionary<string, object> error = Child(response, "error");
            object message = Value(error, "message");
            return message == null ? null : Convert.ToString(message, CultureInfo.InvariantCulture);
        }

        public static QuotaData Read()
        {
            ProcessStartInfo info = new ProcessStartInfo();
            info.FileName = FindCodex();
            info.Arguments = "app-server --stdio";
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardInput = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;

            using (Process process = new Process())
            {
                process.StartInfo = info;
                try
                {
                    if (!process.Start()) throw new InvalidOperationException("无法启动 Codex CLI");
                    Send(process, new
                    {
                        id = 1,
                        method = "initialize",
                        @params = new
                        {
                            clientInfo = new { name = "codex_quota_desktop", title = "Codex Quota", version = "3.0.0" },
                            capabilities = new { experimentalApi = true }
                        }
                    });

                    IDictionary<string, object> account = null;
                    IDictionary<string, object> limits = null;
                    bool initialized = false;
                    DateTime deadline = DateTime.Now.AddSeconds(20);
                    Task<string> readTask = process.StandardOutput.ReadLineAsync();

                    while (DateTime.Now < deadline)
                    {
                        if (!readTask.Wait(250))
                        {
                            if (process.HasExited) throw new InvalidOperationException("Codex 服务意外退出");
                            continue;
                        }
                        string line = readTask.Result;
                        if (line == null)
                        {
                            string detail = process.StandardError.ReadToEnd();
                            throw new InvalidOperationException(String.IsNullOrWhiteSpace(detail) ? "Codex 没有返回数据" : detail.Trim());
                        }
                        readTask = process.StandardOutput.ReadLineAsync();
                        IDictionary<string, object> message;
                        try { message = AsDictionary(Json.DeserializeObject(line)); }
                        catch { continue; }
                        if (message == null) continue;

                        object idValue = Value(message, "id");
                        int id = idValue == null ? -1 : Convert.ToInt32(idValue, CultureInfo.InvariantCulture);
                        if (id == 1 && !initialized)
                        {
                            string error = ErrorMessage(message);
                            if (error != null) throw new InvalidOperationException(error);
                            initialized = true;
                            Send(process, new { method = "initialized", @params = new { } });
                            Send(process, new { id = 2, method = "account/read", @params = new { refreshToken = true } });
                            Send(process, new { id = 3, method = "account/rateLimits/read", @params = (object)null });
                            continue;
                        }
                        if (id == 2) account = message;
                        if (id == 3) limits = message;
                        if (account != null && limits != null) break;
                    }

                    if (account == null || limits == null) throw new TimeoutException("读取额度超时，请确认 Codex 已登录");
                    string accountError = ErrorMessage(account);
                    string limitsError = ErrorMessage(limits);
                    if (accountError != null) throw new InvalidOperationException(accountError);
                    if (limitsError != null) throw new InvalidOperationException(limitsError);

                    IDictionary<string, object> result = Child(limits, "result");
                    IDictionary<string, object> bucket = Child(result, "rateLimits");
                    IDictionary<string, object> byId = Child(result, "rateLimitsByLimitId");
                    if (byId != null)
                    {
                        object codex;
                        if (byId.TryGetValue("codex", out codex) && AsDictionary(codex) != null) bucket = AsDictionary(codex);
                    }
                    if (bucket == null) throw new InvalidOperationException("Codex 暂未返回额度信息");

                    IDictionary<string, object> primary = Child(bucket, "primary");
                    IDictionary<string, object> secondary = Child(bucket, "secondary");
                    IDictionary<string, object> weekly = primary;
                    long primaryMinutes = primary == null || Value(primary, "windowDurationMins") == null ? 0 : Convert.ToInt64(Value(primary, "windowDurationMins"), CultureInfo.InvariantCulture);
                    long secondaryMinutes = secondary == null || Value(secondary, "windowDurationMins") == null ? 0 : Convert.ToInt64(Value(secondary, "windowDurationMins"), CultureInfo.InvariantCulture);
                    if (secondaryMinutes > primaryMinutes) weekly = secondary;
                    if (weekly == null) throw new InvalidOperationException("Codex 暂未返回额度周期");

                    int used = Convert.ToInt32(Value(weekly, "usedPercent"), CultureInfo.InvariantCulture);
                    DateTime? reset = null;
                    object resetValue = Value(weekly, "resetsAt");
                    if (resetValue != null)
                    {
                        reset = DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(resetValue, CultureInfo.InvariantCulture)).ToLocalTime().DateTime;
                    }
                    object planValue = Value(bucket, "planType");
                    string plan = planValue == null ? "unknown" : Convert.ToString(planValue, CultureInfo.InvariantCulture);
                    return new QuotaData
                    {
                        RemainingPercent = Math.Max(0, 100 - used),
                        UsedPercent = used,
                        ResetAt = reset,
                        Plan = plan,
                        SyncedAt = DateTime.Now
                    };
                }
                finally
                {
                    try { if (!process.HasExited) process.Kill(); } catch { }
                }
            }
        }
    }

    public sealed class MainWindowController
    {
        private readonly Window window;
        private readonly Window mascotWindow;
        private readonly Border windowSurface;
        private readonly TextBlock statusText, planText, remainingText, usedText, resetText, countdownText, lastSyncText, expiryDisplay;
        private readonly Ellipse statusDot, energyLamp;
        private readonly ProgressBar progress;
        private readonly Button refreshButton, saveExpiryButton;
        private readonly DatePicker expiryPicker;
        private readonly DispatcherTimer timer, taskTimer;
        private readonly BackgroundWorker worker, taskWorker;
        private DateTime? resetAt;
        private int secondsSinceRefresh = 60;
        private readonly string settingsPath;
        private enum DockEdge { None, Left, Right, Top, Bottom }
        private DockEdge dockEdge = DockEdge.None;
        private bool edgeHidden;
        private bool internalMove;
        private bool isDragging;
        private bool dockForcedTopmost;
        private bool mascotArmed;
        private bool previousTopmost;
        private const double MascotWidth = 92;
        private const double MascotHeight = 118;

        private const string Xaml = @"
<Window xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"" xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml""
 Title=""Codex 额度"" Width=""640"" Height=""520"" MinWidth=""600"" MinHeight=""490"" WindowStartupLocation=""CenterScreen""
 WindowStyle=""None"" AllowsTransparency=""True"" Background=""Transparent"" ResizeMode=""CanResizeWithGrip"" FontFamily=""Segoe UI, Microsoft YaHei"">
 <Window.Resources>
  <DropShadowEffect x:Key=""WindowShadow"" BlurRadius=""28"" ShadowDepth=""7"" Opacity=""0.22"" Color=""#000000""/>
  <DropShadowEffect x:Key=""CardShadow"" BlurRadius=""12"" ShadowDepth=""2"" Opacity=""0.06"" Color=""#000000""/>
  <Style x:Key=""RoundButton"" TargetType=""Button""><Setter Property=""Background"" Value=""#007AFF""/><Setter Property=""Foreground"" Value=""White""/><Setter Property=""BorderThickness"" Value=""0""/><Setter Property=""Padding"" Value=""15,8""/><Setter Property=""FontSize"" Value=""12""/><Setter Property=""FontWeight"" Value=""SemiBold""/><Setter Property=""Cursor"" Value=""Hand""/><Setter Property=""Template""><Setter.Value><ControlTemplate TargetType=""Button""><Border x:Name=""B"" Background=""{TemplateBinding Background}"" CornerRadius=""7"" Padding=""{TemplateBinding Padding}""><ContentPresenter HorizontalAlignment=""Center"" VerticalAlignment=""Center""/></Border><ControlTemplate.Triggers><Trigger Property=""IsMouseOver"" Value=""True""><Setter TargetName=""B"" Property=""Opacity"" Value=""0.82""/></Trigger><Trigger Property=""IsEnabled"" Value=""False""><Setter TargetName=""B"" Property=""Opacity"" Value=""0.45""/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
  <Style x:Key=""ToolbarButton"" TargetType=""Button"" BasedOn=""{StaticResource RoundButton}""><Setter Property=""Background"" Value=""#E7E7EC""/><Setter Property=""Foreground"" Value=""#3A3A3C""/><Setter Property=""Padding"" Value=""12,7""/></Style>
  <Style x:Key=""TrafficButton"" TargetType=""Button""><Setter Property=""Width"" Value=""13""/><Setter Property=""Height"" Value=""13""/><Setter Property=""BorderThickness"" Value=""0""/><Setter Property=""Cursor"" Value=""Hand""/><Setter Property=""Template""><Setter.Value><ControlTemplate TargetType=""Button""><Ellipse Fill=""{TemplateBinding Background}"" Stroke=""#22000000"" StrokeThickness=""0.7""/></ControlTemplate></Setter.Value></Setter></Style>
  <Style x:Key=""SectionLabel"" TargetType=""TextBlock""><Setter Property=""Foreground"" Value=""#71717A""/><Setter Property=""FontSize"" Value=""11""/><Setter Property=""FontWeight"" Value=""SemiBold""/></Style>
  <Style x:Key=""Card"" TargetType=""Border""><Setter Property=""Background"" Value=""#FFFFFF""/><Setter Property=""BorderBrush"" Value=""#E5E5EA""/><Setter Property=""BorderThickness"" Value=""1""/><Setter Property=""CornerRadius"" Value=""14""/><Setter Property=""Effect"" Value=""{StaticResource CardShadow}""/></Style>
  <Style TargetType=""DatePicker""><Setter Property=""Height"" Value=""32""/><Setter Property=""Background"" Value=""#F2F2F7""/><Setter Property=""BorderBrush"" Value=""#D9D9DE""/><Setter Property=""BorderThickness"" Value=""1""/><Setter Property=""FontSize"" Value=""12""/></Style>
 </Window.Resources>
 <Border x:Name=""WindowSurface"" Margin=""14"" Background=""#F5F5F7"" BorderBrush=""#D6D6DB"" BorderThickness=""1"" CornerRadius=""18"" Effect=""{StaticResource WindowShadow}"">
  <Grid>
   <Grid.RowDefinitions><RowDefinition Height=""52""/><RowDefinition Height=""*""/><RowDefinition Height=""47""/></Grid.RowDefinitions>
   <Grid x:Name=""TitleBar"" Background=""Transparent"">
    <StackPanel Orientation=""Horizontal"" Margin=""18,0,0,0"" VerticalAlignment=""Center""><Button x:Name=""CloseButton"" Style=""{StaticResource TrafficButton}"" Background=""#FF5F57"" ToolTip=""关闭""/><Button x:Name=""MinButton"" Style=""{StaticResource TrafficButton}"" Background=""#FEBC2E"" Margin=""8,0,0,0"" ToolTip=""最小化""/><Button x:Name=""MaxButton"" Style=""{StaticResource TrafficButton}"" Background=""#28C840"" Margin=""8,0,0,0"" ToolTip=""最大化""/></StackPanel>
    <TextBlock Text=""Codex 额度"" Foreground=""#3A3A3C"" FontSize=""13"" FontWeight=""SemiBold"" HorizontalAlignment=""Center"" VerticalAlignment=""Center""/>
    <Button x:Name=""RefreshButton"" Content=""刷新"" Style=""{StaticResource ToolbarButton}"" HorizontalAlignment=""Right"" VerticalAlignment=""Center"" Margin=""0,0,15,0""/>
   </Grid>
   <Grid Grid.Row=""1"" Margin=""24,4,24,10"">
    <Grid.RowDefinitions><RowDefinition Height=""218""/><RowDefinition Height=""14""/><RowDefinition Height=""*""/></Grid.RowDefinitions>
    <Border Style=""{StaticResource Card}"" Padding=""26,23""><Grid><Grid.RowDefinitions><RowDefinition Height=""Auto""/><RowDefinition Height=""*""/><RowDefinition Height=""Auto""/></Grid.RowDefinitions>
      <Grid><TextBlock Text=""本周剩余额度"" Style=""{StaticResource SectionLabel}""/><Border HorizontalAlignment=""Right"" Background=""#F2F2F7"" CornerRadius=""7"" Padding=""9,4""><TextBlock x:Name=""PlanText"" Text=""同步中"" Foreground=""#636366"" FontSize=""10"" FontWeight=""SemiBold""/></Border></Grid>
      <Grid Grid.Row=""1""><StackPanel VerticalAlignment=""Center""><StackPanel Orientation=""Horizontal""><TextBlock x:Name=""RemainingText"" Text=""—"" Foreground=""#1D1D1F"" FontSize=""76"" FontWeight=""SemiBold""/><TextBlock Text=""%"" Foreground=""#8E8E93"" FontSize=""22"" Margin=""6,40,0,0""/></StackPanel><TextBlock x:Name=""UsedText"" Text=""正在读取 Codex 数据"" Foreground=""#8E8E93"" FontSize=""12"" Margin=""2,1,0,0""/></StackPanel><Border HorizontalAlignment=""Right"" VerticalAlignment=""Center"" Background=""#EEF6FF"" CornerRadius=""22"" Width=""104"" Height=""104""><StackPanel VerticalAlignment=""Center""><TextBlock Text=""WEEKLY"" Foreground=""#007AFF"" FontSize=""9"" FontWeight=""Bold"" HorizontalAlignment=""Center""/><TextBlock Text=""LIMIT"" Foreground=""#007AFF"" FontSize=""16"" FontWeight=""SemiBold"" HorizontalAlignment=""Center"" Margin=""0,2,0,0""/></StackPanel></Border></Grid>
      <ProgressBar x:Name=""QuotaProgress"" Grid.Row=""2"" Height=""7"" Minimum=""0"" Maximum=""100"" Value=""0"" Foreground=""#007AFF"" Background=""#E9E9ED"" BorderThickness=""0""/>
    </Grid></Border>
    <Grid Grid.Row=""2""><Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width=""14""/><ColumnDefinition/></Grid.ColumnDefinitions>
      <Border Grid.Column=""0"" Style=""{StaticResource Card}"" Padding=""20""><StackPanel><TextBlock Text=""额度更新时间"" Style=""{StaticResource SectionLabel}""/><TextBlock x:Name=""ResetText"" Text=""—"" Foreground=""#1D1D1F"" FontSize=""19"" FontWeight=""SemiBold"" Margin=""0,23,0,0""/><TextBlock x:Name=""CountdownText"" Text=""等待同步"" Foreground=""#8E8E93"" FontSize=""11"" Margin=""0,8,0,0""/></StackPanel></Border>
      <Border Grid.Column=""2"" Style=""{StaticResource Card}"" Padding=""20""><Grid><Grid.RowDefinitions><RowDefinition Height=""Auto""/><RowDefinition Height=""Auto""/><RowDefinition Height=""Auto""/></Grid.RowDefinitions><Grid><TextBlock Text=""订阅到期时间"" Style=""{StaticResource SectionLabel}""/><TextBlock x:Name=""ExpiryDisplay"" Text=""未设置"" Foreground=""#8E8E93"" FontSize=""11"" HorizontalAlignment=""Right""/></Grid><DatePicker x:Name=""ExpiryPicker"" Grid.Row=""1"" Margin=""0,17,0,0""/><Button x:Name=""SaveExpiryButton"" Grid.Row=""2"" Content=""保存日期"" Style=""{StaticResource RoundButton}"" HorizontalAlignment=""Left"" Margin=""0,10,0,0""/></Grid></Border>
    </Grid>
   </Grid>
   <Grid Grid.Row=""2"" Margin=""24,0""><StackPanel Orientation=""Horizontal"" VerticalAlignment=""Center""><Ellipse x:Name=""StatusDot"" Width=""7"" Height=""7"" Fill=""#34C759""/><TextBlock x:Name=""StatusText"" Text=""正在连接"" Foreground=""#636366"" FontSize=""11"" Margin=""7,0,0,0""/><TextBlock Text="" · 每 60 秒自动更新"" Foreground=""#AEAEB2"" FontSize=""11""/></StackPanel><TextBlock x:Name=""LastSyncText"" Text=""尚未同步"" Foreground=""#8E8E93"" FontSize=""10"" HorizontalAlignment=""Right"" VerticalAlignment=""Center""/></Grid>
  </Grid>
 </Border>
</Window>";

        private const string MascotXaml = @"
<Window xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"" xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml""
 Width=""92"" Height=""118"" WindowStyle=""None"" AllowsTransparency=""True"" Background=""Transparent"" ResizeMode=""NoResize"" ShowInTaskbar=""False"" Topmost=""True"">
 <Window.Resources><DropShadowEffect x:Key=""MascotShadow"" BlurRadius=""11"" ShadowDepth=""3"" Opacity=""0.32"" Color=""#000000""/></Window.Resources>
 <Grid ToolTip=""??????????"">
  <Ellipse Width=""67"" Height=""13"" Fill=""#42000000"" HorizontalAlignment=""Center"" VerticalAlignment=""Bottom"" Margin=""0,0,0,2""/>
  <Canvas Width=""82"" Height=""108"" HorizontalAlignment=""Center"" VerticalAlignment=""Top"" Effect=""{StaticResource MascotShadow}"">
   <Path Fill=""#E53935"" Data=""M41,1 L31,19 L51,19 Z""/>
   <Ellipse Canvas.Left=""18"" Canvas.Top=""10"" Width=""46"" Height=""43"" Fill=""#DDE4EA"" Stroke=""#8A98A5"" StrokeThickness=""1.4""/>
   <Path Fill=""#E53935"" Data=""M18,29 C24,14 34,10 41,10 L35,52 C25,49 19,42 18,29 Z""/>
   <Path Fill=""#E53935"" Data=""M64,29 C58,14 48,10 41,10 L47,52 C57,49 63,42 64,29 Z""/>
   <Polygon Points=""25,29 37,25 34,35 24,35"" Fill=""#FFF59D"" Stroke=""#F9A825"" StrokeThickness=""1""/>
   <Polygon Points=""57,29 45,25 48,35 58,35"" Fill=""#FFF59D"" Stroke=""#F9A825"" StrokeThickness=""1""/>
   <Path Fill=""#E53935"" Data=""M22,53 C10,61 7,78 11,96 L27,92 L30,67 Z""/>
   <Path Fill=""#E53935"" Data=""M60,53 C72,61 75,78 71,96 L55,92 L52,67 Z""/>
   <Path Fill=""#DDE4EA"" Stroke=""#8A98A5"" StrokeThickness=""1.2"" Data=""M29,48 C35,44 47,44 53,48 L62,91 C56,101 26,101 20,91 Z""/>
   <Path Fill=""#E53935"" Data=""M29,50 L41,63 L53,50 L58,91 C49,98 33,98 24,91 Z""/>
   <Path Fill=""#DDE4EA"" Data=""M23,55 L41,68 L59,55 L55,70 L41,77 L27,70 Z""/>
   <Ellipse x:Name=""EnergyLamp"" Canvas.Left=""35"" Canvas.Top=""60"" Width=""12"" Height=""12"" Fill=""#FFD60A"" Stroke=""White"" StrokeThickness=""2""/>
   <Path Fill=""#DDE4EA"" Stroke=""#8A98A5"" StrokeThickness=""1"" Data=""M23,89 L37,89 L35,107 L19,107 Z""/>
   <Path Fill=""#DDE4EA"" Stroke=""#8A98A5"" StrokeThickness=""1"" Data=""M45,89 L59,89 L63,107 L47,107 Z""/>
  </Canvas>
 </Grid>
</Window>";

        public MainWindowController()
        {
            window = (Window)XamlReader.Parse(Xaml);
            mascotWindow = (Window)XamlReader.Parse(MascotXaml);
            windowSurface = Find<Border>("WindowSurface");
            statusText = Find<TextBlock>("StatusText"); planText = Find<TextBlock>("PlanText"); remainingText = Find<TextBlock>("RemainingText"); usedText = Find<TextBlock>("UsedText");
            resetText = Find<TextBlock>("ResetText"); countdownText = Find<TextBlock>("CountdownText"); lastSyncText = Find<TextBlock>("LastSyncText"); expiryDisplay = Find<TextBlock>("ExpiryDisplay");
            statusDot = Find<Ellipse>("StatusDot"); progress = Find<ProgressBar>("QuotaProgress"); refreshButton = Find<Button>("RefreshButton"); saveExpiryButton = Find<Button>("SaveExpiryButton"); expiryPicker = Find<DatePicker>("ExpiryPicker");
            energyLamp = mascotWindow.FindName("EnergyLamp") as Ellipse;
            settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.ini");

            Find<Button>("CloseButton").Click += delegate { window.Close(); };
            Find<Button>("MinButton").Click += delegate { window.WindowState = WindowState.Minimized; };
            Find<Button>("MaxButton").Click += delegate { ToggleMaximize(); };
            Find<Grid>("TitleBar").MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                if (e.ClickCount == 2) { ToggleMaximize(); return; }
                if (edgeHidden) RevealFromEdge();
                isDragging = true;
                try { window.DragMove(); }
                finally { isDragging = false; }
                EvaluateDockEdge();
                if (dockEdge != DockEdge.None) HideToEdge();
            };
            refreshButton.Click += delegate { Refresh(); };
            saveExpiryButton.Click += SaveExpiry;
            mascotWindow.MouseEnter += delegate { if (mascotArmed) RevealFromEdge(); };
            mascotWindow.MouseLeave += delegate { mascotArmed = true; };
            window.MouseLeave += delegate
            {
                if (!edgeHidden && !isDragging && window.WindowState != WindowState.Minimized)
                {
                    if (dockEdge == DockEdge.None) dockEdge = DockEdge.Right;
                    HideToEdge();
                }
            };

            worker = new BackgroundWorker();
            worker.DoWork += delegate(object sender, DoWorkEventArgs e) { e.Result = CodexReader.Read(); };
            worker.RunWorkerCompleted += WorkerCompleted;
            taskWorker = new BackgroundWorker();
            taskWorker.DoWork += delegate(object sender, DoWorkEventArgs e) { e.Result = CodexTaskReader.Read(); };
            taskWorker.RunWorkerCompleted += TaskWorkerCompleted;
            timer = new DispatcherTimer(); timer.Interval = TimeSpan.FromSeconds(1); timer.Tick += TimerTick;
            taskTimer = new DispatcherTimer(); taskTimer.Interval = TimeSpan.FromSeconds(2); taskTimer.Tick += delegate { RefreshTaskState(); };
            window.LocationChanged += delegate
            {
                if (!internalMove && !edgeHidden && !isDragging && window.WindowState == WindowState.Normal)
                { EvaluateDockEdge(); if (dockEdge != DockEdge.None) HideToEdge(); }
            };
            LoadSettings();
            window.Loaded += delegate { timer.Start(); taskTimer.Start(); Refresh(); RefreshTaskState(); };
            window.Closed += delegate
            {
                timer.Stop();
                taskTimer.Stop();
                try { mascotWindow.Close(); } catch { }
            };
        }

        private T Find<T>(string name) where T : class { return window.FindName(name) as T; }
        private static Brush BrushFrom(string value) { return (Brush)new BrushConverter().ConvertFromString(value); }

        private void ToggleMaximize()
        {
            if (edgeHidden) RevealFromEdge();
            dockEdge = DockEdge.None;
            window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            windowSurface.CornerRadius = window.WindowState == WindowState.Maximized ? new CornerRadius(0) : new CornerRadius(18);
            windowSurface.Margin = window.WindowState == WindowState.Maximized ? new Thickness(0) : new Thickness(14);
        }

        private void EvaluateDockEdge()
        {
            if (window.WindowState != WindowState.Normal) { dockEdge = DockEdge.None; RestoreDockTopmost(); return; }
            Rect work = SystemParameters.WorkArea;
            double width = Math.Max(1, window.ActualWidth);
            double height = Math.Max(1, window.ActualHeight);
            double left = Math.Max(0, work.Left - window.Left) / width;
            double right = Math.Max(0, window.Left + width - work.Right) / width;
            double top = Math.Max(0, work.Top - window.Top) / height;
            double bottom = Math.Max(0, window.Top + height - work.Bottom) / height;
            double nearest = Math.Max(Math.Max(left, right), Math.Max(top, bottom));
            if (nearest < 0.5) { dockEdge = DockEdge.None; RestoreDockTopmost(); return; }
            if (nearest == left) dockEdge = DockEdge.Left;
            else if (nearest == right) dockEdge = DockEdge.Right;
            else if (nearest == top) dockEdge = DockEdge.Top;
            else dockEdge = DockEdge.Bottom;
        }

        private void HideToEdge()
        {
            if (dockEdge == DockEdge.None || edgeHidden || window.WindowState == WindowState.Minimized) return;
            Rect work = SystemParameters.WorkArea;
            if (!dockForcedTopmost) { previousTopmost = window.Topmost; window.Topmost = true; dockForcedTopmost = true; }
            internalMove = true;
            try
            {
                edgeHidden = true;
                PositionMascot(work);
                mascotArmed = false;
                window.Hide();
                mascotWindow.Show();
                mascotWindow.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate { if (!mascotWindow.IsMouseOver) mascotArmed = true; }));
            }
            finally { internalMove = false; }
        }

        private void PositionMascot(Rect work)
        {
            double centerX = window.Left + window.ActualWidth / 2;
            double centerY = window.Top + window.ActualHeight / 2;
            if (dockEdge == DockEdge.Left)
            {
                mascotWindow.Left = work.Left + 4;
                mascotWindow.Top = Clamp(centerY - MascotHeight / 2, work.Top + 4, work.Bottom - MascotHeight - 4);
            }
            else if (dockEdge == DockEdge.Right)
            {
                mascotWindow.Left = work.Right - MascotWidth - 4;
                mascotWindow.Top = Clamp(centerY - MascotHeight / 2, work.Top + 4, work.Bottom - MascotHeight - 4);
            }
            else if (dockEdge == DockEdge.Top)
            {
                mascotWindow.Left = Clamp(centerX - MascotWidth / 2, work.Left + 4, work.Right - MascotWidth - 4);
                mascotWindow.Top = work.Top + 4;
            }
            else
            {
                mascotWindow.Left = Clamp(centerX - MascotWidth / 2, work.Left + 4, work.Right - MascotWidth - 4);
                mascotWindow.Top = work.Bottom - MascotHeight - 4;
            }
        }

        private static double Clamp(double value, double minimum, double maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        private void RestoreDockTopmost()
        {
            if (!dockForcedTopmost) return;
            window.Topmost = previousTopmost;
            dockForcedTopmost = false;
        }

        private void RevealFromEdge()
        {
            if (!edgeHidden || dockEdge == DockEdge.None) return;
            Rect work = SystemParameters.WorkArea;
            internalMove = true;
            try
            {
                mascotWindow.Hide();
                if (dockEdge == DockEdge.Left) window.Left = work.Left;
                else if (dockEdge == DockEdge.Right) window.Left = work.Right - window.ActualWidth;
                else if (dockEdge == DockEdge.Top) window.Top = work.Top;
                else if (dockEdge == DockEdge.Bottom) window.Top = work.Bottom - window.ActualHeight;
                edgeHidden = false;
                window.Show();
                window.Activate();
                RestoreDockTopmost();
            }
            finally { internalMove = false; }
        }

        private void RefreshTaskState()
        {
            if (!taskWorker.IsBusy) taskWorker.RunWorkerAsync();
        }

        private void TaskWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            CodexTaskSnapshot snapshot = e.Error == null ? e.Result as CodexTaskSnapshot : null;
            CodexTaskState state = snapshot == null ? CodexTaskState.Idle : snapshot.State;
            if (state == CodexTaskState.Running)
            {
                energyLamp.Fill = BrushFrom("#FF3B30");
                mascotWindow.ToolTip = "Codex \u6b63\u5728\u8fd0\u884c";
            }
            else if (state == CodexTaskState.WaitingForConfirmation)
            {
                energyLamp.Fill = BrushFrom("#34C759");
                mascotWindow.ToolTip = "Codex \u5df2\u6682\u505c\uff0c\u7b49\u5f85\u7528\u6237\u786e\u8ba4";
            }
            else
            {
                energyLamp.Fill = BrushFrom("#FFD60A");
                mascotWindow.ToolTip = "Codex \u4efb\u52a1\u5df2\u7ed3\u675f\uff0c\u65e0\u9700\u7528\u6237\u786e\u8ba4";
            }
        }

        private void Refresh()
        {
            if (worker.IsBusy) return;
            refreshButton.IsEnabled = false; statusText.Text = "正在同步"; statusDot.Fill = BrushFrom("#FFCC00"); worker.RunWorkerAsync();
        }

        private void WorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            refreshButton.IsEnabled = true;
            if (e.Error != null)
            {
                statusText.Text = "连接异常"; statusDot.Fill = BrushFrom("#FF3B30"); usedText.Text = CleanError(e.Error.Message); lastSyncText.Text = "刷新失败"; secondsSinceRefresh = 0; return;
            }
            QuotaData data = (QuotaData)e.Result;
            remainingText.Text = data.RemainingPercent.ToString(CultureInfo.InvariantCulture);
            usedText.Text = String.Format(CultureInfo.CurrentCulture, "本周期已使用 {0}%", data.UsedPercent);
            progress.Value = data.RemainingPercent; planText.Text = PlanName(data.Plan); resetAt = data.ResetAt;
            resetText.Text = data.ResetAt.HasValue ? data.ResetAt.Value.ToString("M月d日  HH:mm", CultureInfo.CurrentCulture) : "暂未提供";
            lastSyncText.Text = "更新于 " + data.SyncedAt.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
            statusText.Text = "已连接"; statusDot.Fill = BrushFrom("#34C759"); secondsSinceRefresh = 0; UpdateCountdown();
        }

        private static string CleanError(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return "读取失败";
            string[] lines = value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return lines.Length == 0 ? "读取失败" : lines[lines.Length - 1];
        }

        private static string PlanName(string value)
        {
            switch ((value ?? "unknown").ToLowerInvariant()) { case "free": return "FREE"; case "go": return "GO"; case "plus": return "PLUS"; case "pro": return "PRO"; case "team": return "TEAM"; case "business": return "BUSINESS"; case "enterprise": return "ENTERPRISE"; case "edu": return "EDU"; default: return "CODEX"; }
        }

        private void TimerTick(object sender, EventArgs e)
        {
            secondsSinceRefresh++; UpdateCountdown(); if (secondsSinceRefresh >= 60) Refresh();
        }

        private void UpdateCountdown()
        {
            if (!resetAt.HasValue) { countdownText.Text = "等待同步"; return; }
            TimeSpan span = resetAt.Value - DateTime.Now;
            if (span.TotalSeconds <= 0) { countdownText.Text = "额度周期正在更新"; return; }
            if (span.TotalDays >= 1) countdownText.Text = String.Format(CultureInfo.CurrentCulture, "{0} 天 {1} 小时后更新", Math.Floor(span.TotalDays), span.Hours);
            else if (span.TotalHours >= 1) countdownText.Text = String.Format(CultureInfo.CurrentCulture, "{0} 小时 {1} 分后更新", Math.Floor(span.TotalHours), span.Minutes);
            else countdownText.Text = String.Format(CultureInfo.CurrentCulture, "{0} 分钟后更新", Math.Max(0, span.Minutes));
        }

        private void LoadSettings()
        {
            try
            {
                if (!File.Exists(settingsPath)) return;
                DateTime date;
                if (DateTime.TryParseExact(File.ReadAllText(settingsPath).Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
                { expiryPicker.SelectedDate = date; expiryDisplay.Text = date.ToString("yyyy年M月d日", CultureInfo.CurrentCulture); }
            }
            catch { }
        }

        private void SaveExpiry(object sender, RoutedEventArgs e)
        {
            if (!expiryPicker.SelectedDate.HasValue) { expiryDisplay.Text = "请选择日期"; return; }
            DateTime date = expiryPicker.SelectedDate.Value;
            File.WriteAllText(settingsPath, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), new UTF8Encoding(false));
            expiryDisplay.Text = date.ToString("yyyy年M月d日", CultureInfo.CurrentCulture); saveExpiryButton.Content = "已保存";
            DispatcherTimer once = new DispatcherTimer(); once.Interval = TimeSpan.FromSeconds(1.2); once.Tick += delegate { saveExpiryButton.Content = "保存日期"; once.Stop(); }; once.Start();
        }

        public int Run()
        {
            window.Show();
            window.UpdateLayout();
            dockEdge = DockEdge.Right;
            HideToEdge();
            return Application.Current.Run();
        }
    }

    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--self-test")
            {
                try { QuotaData data = CodexReader.Read(); return data.RemainingPercent >= 0 ? 0 : 2; }
                catch { return 1; }
            }
            try
            {
                Application app = new Application();
                app.ShutdownMode = ShutdownMode.OnMainWindowClose;
                MainWindowController controller = new MainWindowController();
                return controller.Run();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Codex 额度", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }
    }
}
