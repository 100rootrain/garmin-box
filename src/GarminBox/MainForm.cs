using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GarminBox;

/// 입력 → 연결 확인 → 설정 시작(저장·첫 동기화·예약 등록), 재실행 시 지금 동기화·최근 결과.
/// 잠금은 UI 스레드에서 잡고 await 뒤 같은 UI 스레드에서 해제한다(ConfigureAwait(false)를 쓰지 않는다).
sealed class MainForm : Form
{
    const string SavedHint = "저장됨 (바꿀 때만 입력)";
    const string DefaultGistFile = "garmin-stats";

    readonly TextBox email = new(), password = Secret(), dsn = Secret(), token = Secret(), gist = new();
    readonly ComboBox gistFile = new() { DropDownStyle = ComboBoxStyle.DropDown };
    readonly DateTimePicker runAt = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Width = 80 };
    readonly Button checkButton = new() { Text = "연결 확인", AutoSize = true }, setupButton = new() { Text = "설정 시작", AutoSize = true },
        syncButton = new() { Text = "지금 동기화", AutoSize = true }, cancelButton = new() { Text = "취소", AutoSize = true },
        scheduleButton = new() { Text = "예약 다시 등록", AutoSize = true, Visible = false };
    readonly Label checkLabel = Wrapped(), gistLabel = Wrapped(), statusLabel = Wrapped(width: 720), recentLabel = Wrapped(width: 720);
    readonly LinkLabel gistLink = new() { AutoSize = true };
    readonly TextBox progress = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };

    string? savedEmail;
    JsonElement? check;          // 마지막으로 성공한 연결 확인 결과. 입력이 바뀌면 버린다.
    JsonElement? checkEvent, doneEvent;
    string? failedMessage;
    int inputVersion;
    bool loading, busy, deployPending, deployFailed, closeAfterRun;
    CancellationTokenSource? running;

    static TextBox Secret() => new() { UseSystemPasswordChar = true };

    public MainForm()
    {
        Text = "garmin-box 설정";
        Font = new Font("Malgun Gothic", 9F);
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(760, 780);
        MinimumSize = new Size(640, 560);

        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12), AutoScroll = true };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Row(string label, Control control)
        {
            table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, table.RowCount);
            control.Dock = DockStyle.Fill;
            table.Controls.Add(control, 1, table.RowCount++);
        }
        void Wide(Control control)
        {
            table.Controls.Add(control, 0, table.RowCount);
            table.SetColumnSpan(control, 2);
            table.RowCount++;
        }

        Row("Garmin 이메일", email);
        Row("Garmin 비밀번호", password);
        Row("Supabase 연결 문자열", dsn);
        Row("", Flow(Link("연결 문자열 찾기 (Supabase 안내)", "https://supabase.com/docs/guides/database/connecting-to-postgres")));
        Row("GitHub 토큰", token);
        Row("", Flow(Link("gist 권한 토큰 만들기", "https://github.com/settings/tokens/new?scopes=gist&description=garmin-box"),
            Link("새 gist 만들기", "https://gist.github.com/")));
        Row("Gist URL 또는 ID", gist);
        Row("갱신할 gist 파일", gistFile);
        Row("매일 실행 시각", Flow(runAt, Wrapped("로그인되어 있을 때만 실행합니다. PC가 꺼져 있었다면 로그인 후 실행합니다. Windows 비밀번호는 받지 않습니다.", 460)));
        Row("", Flow(checkButton));
        Row("", checkLabel);
        Row("", Flow(gistLabel, gistLink));
        Row("", Flow(setupButton, Wrapped("설정 파일 저장 → Supabase 테이블 생성 → 운동 기록 업로드 → 선택한 gist 파일 갱신 → 매일 자동 실행 등록", 460)));
        Row("", Flow(syncButton, cancelButton, scheduleButton));
        Wide(statusLabel);
        Wide(recentLabel);
        table.RowStyles.Clear();
        for (int i = 0; i < table.RowCount; i++) table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        progress.MinimumSize = new Size(0, 160);
        Wide(progress);
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(table);

        foreach (var input in new Control[] { email, password, dsn, token, gist }) input.TextChanged += OnInputChanged;
        gistFile.TextChanged += (_, _) => ShowGistTarget();   // 연결값이 아니라 확인 결과를 버리지 않는다(목록은 확인 뒤에 채워진다)
        gistLink.LinkClicked += (_, _) => Open(gistLink.Text);
        checkButton.Click += OnCheck;
        setupButton.Click += OnSetup;
        syncButton.Click += OnSync;
        cancelButton.Click += (_, _) => running?.Cancel();
        scheduleButton.Click += (_, _) => RegisterSchedule(null);
        UpdateButtons();
    }

    static FlowLayoutPanel Flow(params Control[] controls)
    {
        var flow = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = Padding.Empty };
        flow.Controls.AddRange(controls);
        return flow;
    }

    static Label Wrapped(string text = "", int width = 560) => new() { Text = text, AutoSize = true, MaximumSize = new Size(width, 0), Anchor = AnchorStyles.Left };

    static LinkLabel Link(string text, string url)
    {
        var link = new LinkLabel { Text = text, AutoSize = true };
        link.LinkClicked += (_, _) => Open(url);
        return link;
    }

    static void Open(string url)
    {
        if (url.StartsWith("https://")) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        LoadSaved();
        ShowRecent();
        using var held = AppLock.TryAcquire();
        if (held is null)
        {
            // 동기화 중인 런타임을 덮어쓰지 않는다. 다음 작업이 잠금을 잡은 직후 배치한다(RunLocked).
            deployPending = true;
            ShowInfo("동기화가 진행 중입니다. 끝난 뒤 작업을 시작하면 프로그램 파일을 먼저 준비합니다.");
            return;
        }
        SetBusy(true);
        try
        {
            Append("프로그램 파일을 준비합니다.");
            await Task.Run(Installer.Deploy);
            Append("프로그램 파일 준비 완료");
        }
        catch (Exception ex)
        {
            deployFailed = true;
            ShowFailure($"프로그램 파일을 준비하지 못했습니다: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
            if (closeAfterRun) BeginInvoke(Close);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (busy)
        {
            // 작업을 끝내고(Job 종료·자식 종료 확인, 배치 완료) 잠금을 푼 뒤 닫는다
            e.Cancel = true;
            closeAfterRun = true;
            running?.Cancel();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        DeleteCheckToken();   // 저장하지 않은 확인 결과는 버린다
        base.OnFormClosed(e);
    }

    /// 저장된 설정을 화면에 채운다. 비밀값은 보여 주지 않고 빈 칸이면 기존 값을 쓴다.
    void LoadSaved()
    {
        loading = true;
        try
        {
            var settings = ConfigFiles.LoadSettings();
            runAt.Value = DateTime.Today + (TimeOnly.TryParseExact(settings.ScheduleTime, "HH:mm", out var at) ? at : new TimeOnly(9, 10)).ToTimeSpan();
            JsonNode? garmin = null;
            try { garmin = File.Exists(Paths.GarminConfig) ? JsonNode.Parse(File.ReadAllText(Paths.GarminConfig)) : null; }
            catch (Exception ex) when (ex is IOException or JsonException) { ShowFailure($"Garmin 설정 파일을 읽지 못했습니다: {ex.Message}"); }
            savedEmail = (string?)garmin?["credentials"]?["user"];
            email.Text = savedEmail ?? "";
            var env = File.Exists(Paths.EnvFile) ? ConfigFiles.ParseEnv(File.ReadAllText(Paths.EnvFile)) : new Dictionary<string, string>();
            SetSecret(password, !string.IsNullOrEmpty((string?)garmin?["credentials"]?["password"]));
            SetSecret(dsn, !string.IsNullOrEmpty(env.GetValueOrDefault("SUPA_DSN")));
            SetSecret(token, !string.IsNullOrEmpty(env.GetValueOrDefault("GITHUB_TOKEN")));
            gist.Text = env.GetValueOrDefault("GIST_ID", "");
            gistFile.Text = env.GetValueOrDefault("GIST_FILENAME") is { Length: > 0 } file ? file : DefaultGistFile;
        }
        finally { loading = false; }
        UpdateButtons();
    }

    static void SetSecret(TextBox box, bool saved)
    {
        box.Text = "";
        box.PlaceholderText = saved ? SavedHint : "";
    }

    void ShowRecent()
    {
        var settings = ConfigFiles.LoadSettings();
        var status = ConfigFiles.ReadStatus();
        var lines = new List<string>();
        // 첫 동기화 실패·취소 뒤에는 열지 않는다: 그때는 설정 시작이나 지금 동기화가 먼저다
        bool retrySchedule = ConfigFiles.SyncedSinceSaved() && !ScheduleRegistered();
        scheduleButton.Visible = retrySchedule;
        if (!settings.ConfigComplete)
            lines.Add("설정이 완료되지 않았습니다. 정보를 입력하고 연결 확인 → 설정 시작을 누르세요.");
        else if (retrySchedule)
            lines.Add("매일 자동 실행이 등록되어 있지 않습니다. '예약 다시 등록'을 누르세요.");
        if (status is null)
            lines.Add("최근 동기화 결과: 없음");
        else
        {
            string result = (string?)status["result"] switch { "success" => "성공", "reauth" => "Garmin 재인증 필요", _ => "실패" };
            string count = status["activities"] is { } n ? $" · {n}건" : "";
            lines.Add($"최근 동기화 결과({status["finished_at"]}, {status["mode"]}): {result}{count} — {status["message"]}");
            if (status["warnings"] is JsonArray { Count: > 0 } warnings)
                lines.AddRange(warnings.Select(w => $"경고: {w}"));
            if ((string?)status["result"] == "reauth")
                lines.Add("Garmin 재인증 필요: 비밀번호 칸을 비운 채 연결 확인을 눌러 다시 로그인하세요.");
        }
        recentLabel.Text = string.Join("\n", lines);
    }

    static bool ScheduleRegistered()
    {
        try { return Scheduler.IsRegistered(Paths.Exe); }
        catch (Exception) { return false; }   // 읽지 못하면 버튼을 보여 주고, 누르면 실제 오류가 나온다
    }

    void OnInputChanged(object? sender, EventArgs e)
    {
        if (loading) return;
        inputVersion++;
        if (check is not null) ShowInfo("입력이 바뀌어 연결 확인 결과를 버렸습니다. 다시 연결 확인을 누르세요.");
        DiscardCheck();
    }

    void DiscardCheck()
    {
        check = null;
        checkLabel.Text = gistLabel.Text = gistLink.Text = "";
        DeleteCheckToken();
        UpdateButtons();
    }

    static void DeleteCheckToken()
    {
        try { if (File.Exists(Paths.CheckToken)) File.Delete(Paths.CheckToken); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // 다음 연결 확인이 먼저 지운다
    }

    async void OnCheck(object? sender, EventArgs e)
    {
        string mail = email.Text.Trim();
        if (mail.Length == 0) { ShowFailure("Garmin 이메일을 입력하세요."); return; }
        if (!string.Equals(mail, savedEmail, StringComparison.OrdinalIgnoreCase) && password.Text.Length == 0)
        {
            ShowFailure("Garmin 이메일을 새로 입력하거나 바꿀 때는 비밀번호도 입력하세요.");
            return;
        }
        DiscardCheck();
        int version = inputVersion;
        // 입력한 값만 자식 환경으로 넘긴다. 빈 칸은 run_batch.py가 저장된 .env 값을 쓴다.
        var env = new Dictionary<string, string> { ["GARMIN_EMAIL"] = mail };
        if (password.Text.Length > 0) env["GARMIN_PASSWORD"] = password.Text;
        if (dsn.Text.Trim() is { Length: > 0 } d) env["SUPA_DSN"] = d;
        if (token.Text.Trim() is { Length: > 0 } t) env["GITHUB_TOKEN"] = t;
        if (gist.Text.Trim() is { Length: > 0 } g) env["GIST_ID"] = g;
        ShowInfo("연결을 확인하는 중입니다.");
        var result = await RunLocked("check", env, TimeSpan.FromMinutes(10), () =>
        {
            Directory.CreateDirectory(Paths.GarminDbDir);
            SecretAcl.ProtectTree(Paths.GarminDbDir);   // 확인 중 받는 토큰도 보호
        });
        if (result is null || result.TimedOut || result.Cancelled) { DeleteCheckToken(); return; }
        if (version != inputVersion)
        {
            DiscardCheck();
            ShowInfo("확인하는 동안 입력이 바뀌어 결과를 버렸습니다. 다시 연결 확인을 누르세요.");
            return;
        }
        if (checkEvent is { } found) ShowCheck(found);
        else { DeleteCheckToken(); ShowFailure(failedMessage ?? $"연결 확인을 마치지 못했습니다(종료 코드 {result.ExitCode})."); }
    }

    void ShowCheck(JsonElement found)
    {
        var lines = new List<string>();
        bool allOk = true;
        foreach (var (key, label) in new[] { ("garmin", "Garmin"), ("postgres", "Supabase"), ("github", "GitHub") })
        {
            var item = found.GetProperty(key);
            if (item.GetProperty("ok").GetBoolean())
                lines.Add($"✓ {label}: 연결됨" + (key == "garmin" && item.TryGetProperty("name", out var name) && name.GetString() is { } who ? $" ({who})" : ""));
            else
            {
                allOk = false;
                lines.Add($"✗ {label}: {item.GetProperty("message").GetString()}");
            }
        }
        var github = found.GetProperty("github");
        if (github.GetProperty("ok").GetBoolean())
        {
            var files = github.GetProperty("files").EnumerateArray().Select(f => f.GetString()!).ToList();
            if (!files.Contains(DefaultGistFile)) files.Add(DefaultGistFile);
            string current = gistFile.Text.Trim().Length > 0 ? gistFile.Text.Trim() : DefaultGistFile;
            gistFile.Items.Clear();
            gistFile.Items.AddRange([.. files]);
            gistFile.Text = current;
            gistLink.Text = github.GetProperty("url").GetString() ?? "";
        }
        checkLabel.Text = string.Join("\n", lines);
        if (allOk)
        {
            check = found;
            ShowGistTarget();
            ShowSuccess("연결 확인 완료. 갱신할 gist 파일을 확인한 뒤 설정 시작을 누르세요.");
        }
        else
        {
            DeleteCheckToken();
            ShowFailure("실패한 항목의 입력을 고친 뒤 다시 연결 확인을 누르세요.");
        }
        UpdateButtons();
    }

    void ShowGistTarget()
    {
        if (check is not { } found) return;
        var github = found.GetProperty("github");
        string scope = github.GetProperty("public").GetBoolean() ? "공개" : "비공개";
        string file = gistFile.Text.Trim().Length > 0 ? gistFile.Text.Trim() : DefaultGistFile;
        gistLabel.Text = $"{github.GetProperty("login").GetString()}의 {scope} gist · 갱신할 파일: {file}";
    }

    async void OnSetup(object? sender, EventArgs e)
    {
        if (check is not { } found) return;
        bool saved = false;
        ShowInfo("설정을 저장하고 첫 동기화를 시작합니다. 기록이 많으면 오래 걸릴 수 있습니다.");
        var result = await RunLocked("setup", new Dictionary<string, string>(), TimeSpan.FromHours(6), () =>
        {
            SaveInputs(found);
            saved = true;
        });
        // 저장이 끝났으면 화면을 저장된 값으로 다시 채운다(비밀값은 가림). 저장 전에 실패했으면 입력을 그대로 둬 다시 시도하게 한다.
        if (saved) LoadSaved();
        ShowRecent();
        if (result is null || result.TimedOut || result.Cancelled) return;
        if (result.ExitCode != 0) { ShowRunFailure(result); return; }   // 확인 결과를 남겨 같은 입력으로 다시 시도할 수 있다
        DiscardCheck();
        if (doneEvent is { } done && done.TryGetProperty("gist_url", out var url)) gistLink.Text = url.GetString() ?? "";
        RegisterSchedule(doneEvent?.GetProperty("activities").GetInt32());
    }

    void SaveInputs(JsonElement check)
    {
        string? existing = File.Exists(Paths.GarminConfig) ? File.ReadAllText(Paths.GarminConfig) : null;
        string garmin = ConfigFiles.MergeGarminConfig(existing, File.ReadAllText(Paths.GarminExample), email.Text.Trim(),
            password.Text.Length > 0 ? password.Text : null);
        var env = File.Exists(Paths.EnvFile) ? ConfigFiles.ParseEnv(File.ReadAllText(Paths.EnvFile)) : new Dictionary<string, string>();
        if (dsn.Text.Trim().Length > 0) env["SUPA_DSN"] = dsn.Text.Trim();
        if (token.Text.Trim().Length > 0) env["GITHUB_TOKEN"] = token.Text.Trim();
        env["GIST_ID"] = check.GetProperty("github").GetProperty("gist_id").GetString()!;
        env["GIST_FILENAME"] = gistFile.Text.Trim();
        ConfigFiles.SaveAll(ConfigFiles.LoadSettings() with { ScheduleTime = runAt.Value.ToString("HH:mm") }, garmin, ConfigFiles.FormatEnv(env),
            check.GetProperty("garmin").GetProperty("db_path").GetString()!);
    }

    /// 첫 동기화 성공 뒤 예약만 등록한다. 실패하면 gist 갱신을 다시 하지 않고 등록만 재시도할 수 있다.
    void RegisterSchedule(int? count)
    {
        var at = TimeOnly.TryParseExact(ConfigFiles.LoadSettings().ScheduleTime, "HH:mm", out var saved) ? saved : TimeOnly.FromDateTime(runAt.Value);
        string synced = count is { } n ? $"첫 동기화 {n}건" : "동기화 성공";
        try
        {
            var outcome = Scheduler.Register(Paths.Exe, at);
            if (outcome.Registered)
                ShowSuccess($"설정 완료 · {synced} · 다음 실행 {outcome.NextRun:yyyy-MM-dd HH:mm} (로그인되어 있을 때만)");
            else
                ShowFailure($"동기화 성공 / 자동 실행 설정 실패: {outcome.Message}");
        }
        catch (Exception e)
        {
            ShowFailure($"동기화 성공 / 자동 실행 설정 실패: {e.Message}");
        }
        ShowRecent();   // 재시도 버튼 표시도 여기서 정한다
        UpdateButtons();
    }

    async void OnSync(object? sender, EventArgs e)
    {
        ShowInfo("동기화하는 중입니다.");
        var result = await RunLocked("sync", new Dictionary<string, string>(), TimeSpan.FromMinutes(55));
        ShowRecent();
        if (result is null || result.TimedOut || result.Cancelled) return;
        if (result.ExitCode != 0) ShowRunFailure(result);
        else ShowSuccess($"동기화 완료 · {doneEvent?.GetProperty("activities").GetInt32()}건");
    }

    void ShowRunFailure(RunResult result)
    {
        if (result.ExitCode == 4) ShowFailure("Garmin 재인증 필요: 비밀번호 칸을 비운 채 연결 확인을 눌러 다시 로그인하세요.");
        else ShowFailure(failedMessage ?? $"작업이 실패했습니다(종료 코드 {result.ExitCode}). 다시 시도하세요.");
    }

    /// 잠금을 잡고 run_batch.py를 실행한다. 설정 완료 표식은 run_batch.py가 매번, 런타임 표식은 Deploy가(시작 때, 또는
    /// 미뤘으면 여기서) 확인하므로 abandoned 잠금은 경고만 남긴다.
    internal async Task<RunResult?> RunLocked(string mode, Dictionary<string, string> env, TimeSpan timeout, Action? prepare = null)
    {
        using var held = AppLock.TryAcquire();
        if (held is null) { ShowFailure("다른 작업(동기화 또는 설정)이 진행 중입니다. 끝난 뒤 다시 시도하세요."); return null; }
        if (held.WasAbandoned) Append("이전 작업이 비정상 종료됐습니다. 설정 상태를 다시 확인하고 진행합니다.");
        checkEvent = doneEvent = null;
        failedMessage = null;
        running = new CancellationTokenSource();
        SetBusy(true);
        try
        {
            if (deployPending) { await Task.Run(Installer.Deploy); deployPending = false; }   // 시작 때 잠금이 바빠 미룬 배치
            prepare?.Invoke();
            var before = File.GetLastWriteTimeUtc(Paths.Status);
            var result = await PythonRunner.RunAsync(Paths.Python, Paths.RunBatch, [mode], env, OnEvent, AskMfa, timeout, running.Token);
            if (result.TimedOut || result.Cancelled)
            {
                string why = result.Cancelled ? "취소했습니다." : "제한 시간 안에 끝나지 않아 중단했습니다.";
                if (mode != "check") ConfigFiles.WriteFailureStatus(mode, why);
                ShowFailure(why);
            }
            else if (mode != "check") ConfigFiles.RecordUnreportedFailure(mode, before, result.ExitCode);
            return result;
        }
        catch (Exception e)
        {
            ShowFailure(e.Message);
            // 시작하지 못한 실행도 최근 결과로 남긴다(Program.Sync와 같은 규칙). 안 남기면 이전 성공이 최근 결과로 보인다.
            if (mode != "check")
                try { ConfigFiles.WriteFailureStatus(mode, $"시작하지 못했습니다: {e.Message}"); }
                catch (Exception) { }   // 상태 파일도 못 쓰면 화면의 오류가 전부다(async void 호출자로 던지지 않는다)
            return null;
        }
        finally
        {
            running.Dispose();
            running = null;
            SetBusy(false);
            if (closeAfterRun) BeginInvoke(Close);
        }
    }

    void OnEvent(JsonElement ev)
    {
        string? Field(string name) => ev.TryGetProperty(name, out var value) ? value.ToString() : null;
        switch (Field("event"))
        {
            case "progress": Append(Field("message")); break;
            case "text": Append(Field("line")); break;
            case "warning": Append($"경고: {Field("message")}"); break;
            case "mfa_required": Append("Garmin 인증 코드를 입력하세요."); break;
            case "check_result": checkEvent = ev; break;
            case "done": doneEvent = ev; break;
            case "failed":
                failedMessage = Field("kind") == "reauth"
                    ? "Garmin 재인증 필요: 비밀번호 칸을 비운 채 연결 확인을 눌러 다시 로그인하세요."
                    : Field("message");
                Append($"실패: {failedMessage}");
                break;
        }
    }

    /// 모달이 아닌 입력 창: 열려 있는 동안에도 본 화면의 취소를 누를 수 있다. 취소하면 빈 줄,
    /// 시간 초과·취소로 토큰이 취소되면 창을 닫는다(RunAsync는 토큰으로 이미 기다림을 끝냈다).
    Task<string?> AskMfa(CancellationToken cancel)
    {
        var answer = new TaskCompletionSource<string?>();
        string? entered = null;
        var code = new TextBox { Width = 160 };
        var ok = new Button { Text = "확인", AutoSize = true };
        var no = new Button { Text = "취소", AutoSize = true };
        var dialog = new Form
        {
            Text = "Garmin 인증 코드", Font = Font, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false,
            ShowInTaskbar = false, StartPosition = FormStartPosition.CenterParent, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            AcceptButton = ok, CancelButton = no, Padding = new Padding(12),
        };
        var flow = Flow(Wrapped("Garmin이 보낸 일회용 인증 코드를 입력하세요.", 320), code, ok, no);
        flow.FlowDirection = FlowDirection.TopDown;
        dialog.Controls.Add(flow);
        ok.Click += (_, _) => { entered = code.Text.Trim(); dialog.Close(); };
        no.Click += (_, _) => dialog.Close();
        var registration = cancel.Register(() => BeginInvoke(() => { if (!dialog.IsDisposed) dialog.Close(); }));
        dialog.FormClosed += (_, _) =>
        {
            registration.Dispose();
            answer.TrySetResult(entered ?? "");
        };
        dialog.Show(this);
        code.Focus();
        return answer.Task;
    }

    void SetBusy(bool value)
    {
        busy = value;
        UpdateButtons();
    }

    void UpdateButtons()
    {
        bool idle = !busy && !deployFailed;
        checkButton.Enabled = idle;
        setupButton.Enabled = idle && check is not null;
        syncButton.Enabled = idle && ConfigFiles.LoadSettings().ConfigComplete;
        scheduleButton.Enabled = idle;
        cancelButton.Enabled = busy && running is not null;
    }

    void Append(string? line)
    {
        if (string.IsNullOrEmpty(line)) return;
        progress.AppendText($"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}");
    }

    void ShowInfo(string message) => SetStatus(message, SystemColors.ControlText);
    void ShowSuccess(string message) => SetStatus(message, Color.DarkGreen);
    void ShowFailure(string message) => SetStatus(message, Color.Firebrick);

    void SetStatus(string message, Color color)
    {
        statusLabel.ForeColor = color;
        statusLabel.Text = message;
        Append(message);
    }
}
