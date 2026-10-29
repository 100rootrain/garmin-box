namespace GarminBox;

/// 고정 경로. USERPROFILE·LOCALAPPDATA 기준이라 자식 Python(Path.home())과 같은 위치를 본다.
static class Paths
{
    static string Env(string name) => Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"{name} 환경 변수가 없습니다.");
    public static string Home => Env("USERPROFILE");
    public static string InstallDir => Path.Combine(Env("LOCALAPPDATA"), "garmin-box");
    public static string Exe => Path.Combine(InstallDir, "GarminBox.exe");
    public static string Runtime => Path.Combine(InstallDir, "runtime");
    public static string Python => Path.Combine(Runtime, "python", "python.exe");
    public static string RunBatch => Path.Combine(Runtime, "app", "run_batch.py");
    public static string CaBundle => Path.Combine(Runtime, "python", "Lib", "site-packages", "certifi", "cacert.pem");
    public static string GarminExample => Path.Combine(Runtime, "python", "Lib", "site-packages", "garmindb", "GarminConnectConfig.json.example");
    public static string Settings => Path.Combine(InstallDir, "settings.json");
    public static string Status => Path.Combine(InstallDir, "status.json");
    public static string GarminDbDir => Path.Combine(Home, ".GarminDb");
    public static string GarminConfig => Path.Combine(GarminDbDir, "GarminConnectConfig.json");
    public static string GarminToken => Path.Combine(GarminDbDir, "garmin_tokens.json");         // GarminDB 운영 토큰
    public static string CheckToken => Path.Combine(GarminDbDir, "garmin_tokens.check.json");    // 연결 확인용 (run_batch.py와 같은 이름)
    public static string EnvFile => Path.Combine(Home, ".garmin-box.env");
    // Create: 폴더가 없을 때 빈 문자열을 받으면 바로가기가 현재 폴더(다운로드 폴더 등)에 생긴다
    public static string StartMenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs, Environment.SpecialFolderOption.Create), "garmin-box.lnk");

    /// 첫 실행·재실행 모두 정보 입력 전에 통과해야 한다. 문제가 없으면 null.
    public static string? Preflight()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10) || !Environment.Is64BitOperatingSystem)
            return "Windows 10/11 64비트에서만 실행할 수 있습니다.";
        // 현재 Python 번들은 비 ASCII 경로의 CA 파일을 libcurl이 읽지 못한다 (실행 검증 기록). 우회하지 않고 멈춘다.
        if (new[] { InstallDir, CaBundle }.FirstOrDefault(p => p.Any(c => c > 0x7F)) is { } bad)
            return $"현재 Python 번들은 한글 등 비 ASCII 설치 경로에서 인증서를 읽지 못합니다. 이 PC에서는 설정을 진행할 수 없습니다.\n경로: {bad}";
        try
        {
            Directory.CreateDirectory(InstallDir);
            string probe = Path.Combine(InstallDir, ".write-check");
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return $"설치 폴더에 쓸 수 없습니다. Windows 정책이나 권한을 확인하세요.\n경로: {InstallDir}\n{e.Message}";
        }
        return null;
    }
}
