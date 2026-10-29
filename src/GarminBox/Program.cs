namespace GarminBox;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        bool scheduled = args.Contains("--scheduled");
        if (Paths.Preflight() is { } problem)
        {
            if (!scheduled) MessageBox.Show(problem, "garmin-box", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        if (args.Contains("--sync")) return Sync(scheduled);
        // 설정 창은 하나만: 창마다 확인한 토큰이 같은 확인용 파일을 덮어쓰지 않게 한다. 작업 잠금과 달리 창이 열린 동안 잡는다.
        using var window = AppLock.TryAcquire(@"Local\garmin-box-window");
        if (window is null)
        {
            MessageBox.Show("garmin-box 설정 창이 이미 열려 있습니다.", "garmin-box");
            return 1;
        }
        Application.Run(new MainForm());
        return 0;
    }

    /// 예약 작업·수동 런처 경로. MFA가 필요하면 기다리지 않고 재인증 필요로 끝난다.
    internal static int Sync(bool scheduled)
    {
        int code;
        using (var held = AppLock.TryAcquire())
        {
            if (held is null)   // 다른 작업 진행 중: 새로 시작하지 않는다
            {
                // 수동 런처(VBS)는 창 없이 실행되므로 알리지 않으면 아무 반응이 없어 보인다
                if (!scheduled) MessageBox.Show("다른 작업(동기화 또는 설정)이 진행 중입니다. 끝난 뒤 다시 실행하세요.", "garmin-box");
                return 1;
            }
            try
            {
                var before = File.GetLastWriteTimeUtc(Paths.Status);
                var result = PythonRunner.RunAsync(Paths.Python, Paths.RunBatch, ["sync", "--no-prompt"], new Dictionary<string, string>(),
                    _ => { }, _ => Task.FromResult<string?>(null), TimeSpan.FromMinutes(55), CancellationToken.None).GetAwaiter().GetResult();
                if (result.TimedOut) ConfigFiles.WriteFailureStatus("sync", "55분 안에 끝나지 않아 중단했습니다.");
                else ConfigFiles.RecordUnreportedFailure("sync", before, result.ExitCode);
                code = result.ExitCode;
            }
            catch (Exception e)
            {
                ConfigFiles.WriteFailureStatus("sync", $"동기화를 시작하지 못했습니다: {e.Message}");
                code = 1;
            }
        }   // 메시지 창이 잠금을 잡고 있지 않도록 먼저 해제
        if (!scheduled) MessageBox.Show((string?)ConfigFiles.ReadStatus()?["message"] ?? "결과를 읽지 못했습니다.", "garmin-box");
        return code;
    }
}
