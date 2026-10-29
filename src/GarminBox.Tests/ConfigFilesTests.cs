using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GarminBox.Tests;

public class ConfigFilesTests
{
    const string NoDb = @"Z:\no-such-dir\garmin_activities.db";   // 로컬 DB가 없는 PC

    /// 프로필 ACL이 넓은 PC를 흉내 낸다: 이 폴더에 새로 만드는 파일은 Everyone 읽기를 물려받는다.
    static void AllowEveryoneToRead(string directory)
    {
        var dir = new DirectoryInfo(directory);
        var security = dir.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        dir.SetAccessControl(security);
    }

    [Fact]
    public void SecretTempFileIsRestrictedBeforeAnyContent()
    {
        using var temp = new TempDir();
        AllowEveryoneToRead(temp.Root);
        string tempFile = ConfigFiles.CreateProtectedTemp(Path.Combine(temp.Root, ".garmin-box.env"));
        Assert.Equal(0, new FileInfo(tempFile).Length);
        Assert.True(SecretAcl.IsRestricted(new FileInfo(tempFile).GetAccessControl()));
    }

    [Fact]
    public void SecretFileAndBackupAreRestrictedUnderBroadParent()
    {
        using var temp = new TempDir();
        AllowEveryoneToRead(temp.Root);
        string path = Path.Combine(temp.Root, ".garmin-box.env");
        File.WriteAllText(path, "SUPA_DSN=old\n");   // 넓은 권한을 물려받은 기존 파일 → 백업이 된다
        ConfigFiles.AtomicWrite(path, "SUPA_DSN=new\n");
        // 사후 보호가 없으므로, 교체 전에 기존 대상을 보호하지 않으면 File.Replace가 넓은 DACL을 남겨 여기서 실패한다
        Assert.True(SecretAcl.IsRestricted(new FileInfo(path).GetAccessControl()));
        Assert.True(SecretAcl.IsRestricted(new FileInfo(path + ".bak").GetAccessControl()));
        string fresh = Path.Combine(temp.Root, "new.json");
        ConfigFiles.AtomicWrite(fresh, "{}");   // 기존 파일이 없으면 보호된 임시 파일을 그대로 옮긴다
        Assert.True(SecretAcl.IsRestricted(new FileInfo(fresh).GetAccessControl()));
    }

    [Fact]
    public void EnvKeepsEqualsUnicodeAndEmptyValues()
    {
        var values = new Dictionary<string, string> { ["SUPA_DSN"] = "host=db password='a=b' 한글", ["GIST_FILENAME"] = "", ["GITHUB_TOKEN"] = "ghp_x" };
        Assert.Equal(values, ConfigFiles.ParseEnv(ConfigFiles.FormatEnv(values)));
    }

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    [InlineData("a\0b")]
    public void EnvRejectsLineBreaksAndNul(string value) =>
        Assert.Throws<ArgumentException>(() => ConfigFiles.FormatEnv(new Dictionary<string, string> { ["X"] = value }));

    [Fact]
    public void GarminConfigKeepsUnknownSettingsAndCustomDataPath()
    {
        string existing = """{"directories":{"relative_to_home":false,"base_dir":"D:\\Health"},"custom":{"x":1},"credentials":{"user":"me@x.com","password":"old"}}""";
        var merged = JsonNode.Parse(ConfigFiles.MergeGarminConfig(existing, "{}", "me@x.com", null))!;
        Assert.Equal(@"D:\Health", (string?)merged["directories"]!["base_dir"]);
        Assert.Equal(1, (int)merged["custom"]!["x"]!);
        Assert.Equal("old", (string?)merged["credentials"]!["password"]);   // 빈 입력은 저장된 비밀번호를 지우지 않는다
    }

    [Fact]
    public void NewGarminConfigStartsFromBundledExample()
    {
        string example = """{"credentials":{"user":"","secure_password":false,"password":"","password_file":null},"data":{"download_latest_activities":25}}""";
        var merged = JsonNode.Parse(ConfigFiles.MergeGarminConfig(null, example, "me@x.com", "pw"))!;
        Assert.Equal("pw", (string?)merged["credentials"]!["password"]);
        Assert.Equal(25, (int)merged["data"]!["download_latest_activities"]!);
    }

    [Fact]
    public void CorruptGarminConfigIsNotOverwritten() =>
        Assert.ThrowsAny<JsonException>(() => ConfigFiles.MergeGarminConfig("{ broken", "{}", "a", "b"));

    [Fact]
    public void AtomicWriteKeepsOneBackup()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Root, "a.json");
        foreach (var text in new[] { "1", "2", "3" }) ConfigFiles.AtomicWrite(path, text);
        Assert.Equal("3", File.ReadAllText(path));
        Assert.Equal("2", File.ReadAllText(path + ".bak"));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void FailedSaveLeavesSyncBlocked()
    {
        using var profile = new TempProfile();
        Directory.CreateDirectory(Paths.InstallDir);
        Directory.CreateDirectory(Paths.EnvFile);   // 같은 이름의 폴더 → .env 저장 실패
        Directory.CreateDirectory(Paths.GarminDbDir);
        File.WriteAllText(Paths.GarminToken, "A");
        File.WriteAllText(Paths.CheckToken, "B");
        Assert.ThrowsAny<Exception>(() => ConfigFiles.SaveAll(new AppSettings(), "{}", "SUPA_DSN=secret\n", NoDb));
        Assert.False(ConfigFiles.LoadSettings().ConfigComplete);
        Assert.False(File.Exists(Paths.EnvFile + ".tmp"));             // 평문 비밀 임시 파일이 남지 않는다
        Assert.Equal("A", File.ReadAllText(Paths.GarminToken));        // 저장이 끝나지 않으면 운영 토큰도 그대로
    }

    [Fact]
    public void SuccessfulSaveMarksCompleteAndPromotesCheckedToken()
    {
        using var profile = new TempProfile();
        Directory.CreateDirectory(Paths.InstallDir);
        Directory.CreateDirectory(Paths.GarminDbDir);
        File.WriteAllText(Paths.GarminToken, "old");
        File.WriteAllText(Paths.CheckToken, "checked");
        ConfigFiles.SaveAll(new AppSettings("07:30"), "{}", "SUPA_DSN=x\n", NoDb);
        Assert.Equal(new AppSettings("07:30", true), ConfigFiles.LoadSettings());
        Assert.Contains("\"config_complete\": true", File.ReadAllText(Paths.Settings));   // run_batch.py가 읽는 이름
        Assert.Equal("checked", File.ReadAllText(Paths.GarminToken));
        Assert.False(File.Exists(Paths.CheckToken));
    }

    [Fact]
    public void ScheduleRetryNeedsSuccessAfterSave()
    {
        using var profile = new TempProfile();
        Directory.CreateDirectory(Paths.InstallDir);
        void Status(string result, int minutesAfterSave)
        {
            File.WriteAllText(Paths.Status, $$"""{"result":"{{result}}"}""");
            File.SetLastWriteTimeUtc(Paths.Status, File.GetLastWriteTimeUtc(Paths.Settings).AddMinutes(minutesAfterSave));
        }
        ConfigFiles.SaveAll(new AppSettings(), "{}", "SUPA_DSN=x\n", NoDb);
        Assert.False(ConfigFiles.SyncedSinceSaved());   // 저장만 끝났다
        Status("failed", 1);
        Assert.False(ConfigFiles.SyncedSinceSaved());   // 첫 동기화 실패·취소
        Status("success", 1);
        Assert.True(ConfigFiles.SyncedSinceSaved());
        Status("success", -1);
        Assert.False(ConfigFiles.SyncedSinceSaved());   // 이전 설정의 성공. 다시 저장한 뒤 실행이 상태를 남기지 못했다
    }

    [Fact]
    public void UnreportedFailureKeepsStatusPythonWrote()
    {
        using var profile = new TempProfile();
        Directory.CreateDirectory(Paths.InstallDir);
        File.WriteAllText(Paths.Status, """{"result":"success"}""");
        var before = File.GetLastWriteTimeUtc(Paths.Status);
        ConfigFiles.RecordUnreportedFailure("sync", before, 0);
        Assert.Equal("success", (string?)ConfigFiles.ReadStatus()?["result"]);   // 정상 종료는 그대로
        File.WriteAllText(Paths.Status, """{"result":"reauth"}""");
        File.SetLastWriteTimeUtc(Paths.Status, before.AddSeconds(1));
        ConfigFiles.RecordUnreportedFailure("sync", before, 4);
        Assert.Equal("reauth", (string?)ConfigFiles.ReadStatus()?["result"]);   // 이번 실행의 Python이 남긴 원인
    }

    [Fact]
    public void AccountChangeIsRefusedAtSaveWhenDbAppearedAfterCheck()
    {
        using var profile = new TempProfile();
        Directory.CreateDirectory(Paths.InstallDir);
        Directory.CreateDirectory(Paths.GarminDbDir);
        File.WriteAllText(Paths.GarminConfig, """{"credentials":{"user":"a@x.com"}}""");
        File.WriteAllText(Paths.GarminToken, "A");
        File.WriteAllText(Paths.CheckToken, "B");                 // DB가 없을 때 B로 확인했다
        string db = Path.Combine(profile.Dir.Root, "garmin_activities.db");
        File.WriteAllBytes(db, []);                               // 그 사이 A 설정의 수동 동기화가 DB를 만들었다
        Assert.Throws<InvalidOperationException>(() =>
            ConfigFiles.SaveAll(new AppSettings(), """{"credentials":{"user":"b@x.com"}}""", "SUPA_DSN=x\n", db));
        Assert.Equal("A", File.ReadAllText(Paths.GarminToken));   // 다음 동기화도 A로 로그인한다
        Assert.Contains("a@x.com", File.ReadAllText(Paths.GarminConfig));
        Assert.False(File.Exists(Paths.CheckToken));              // 거부한 B의 확인 토큰은 남기지 않는다
        ConfigFiles.SaveAll(new AppSettings(), """{"credentials":{"user":"A@X.com"}}""", "SUPA_DSN=x\n", db);   // 같은 계정은 통과
        Assert.Equal("A", File.ReadAllText(Paths.GarminToken));   // B 토큰이 A 운영 토큰으로 승격되지 않았다
    }

    [Fact]
    public void AccountChangeWithoutCheckedTokenDropsOldToken()
    {
        using var profile = new TempProfile();
        Directory.CreateDirectory(Paths.InstallDir);
        Directory.CreateDirectory(Paths.GarminDbDir);
        File.WriteAllText(Paths.GarminConfig, """{"credentials":{"user":"a@x.com"}}""");
        File.WriteAllText(Paths.GarminToken, "A");   // 로컬 DB 없음, B 확인은 로그인만 되고 확인 토큰 저장은 실패했다
        ConfigFiles.SaveAll(new AppSettings(), """{"credentials":{"user":"b@x.com"}}""", "SUPA_DSN=x\n", NoDb);
        Assert.False(File.Exists(Paths.GarminToken));   // 다음 동기화는 A 토큰이 아니라 B 자격증명으로 로그인한다
        File.WriteAllText(Paths.GarminToken, "B");
        ConfigFiles.SaveAll(new AppSettings(), """{"credentials":{"user":"B@x.com"}}""", "SUPA_DSN=x\n", NoDb);
        Assert.Equal("B", File.ReadAllText(Paths.GarminToken));   // 같은 계정이면 기존 토큰을 그대로 쓴다
    }

    [Fact]
    public void RetryAfterPartialAccountChangeDoesNotReuseOldToken()
    {
        using var profile = new TempProfile();
        Directory.CreateDirectory(Paths.InstallDir);
        Directory.CreateDirectory(Paths.GarminDbDir);
        File.WriteAllText(Paths.GarminConfig, """{"credentials":{"user":"a@x.com"}}""");
        File.WriteAllText(Paths.GarminToken, "A");
        File.WriteAllText(Paths.CheckToken, "B");
        Directory.CreateDirectory(Paths.EnvFile);   // B 설정을 쓴 뒤 .env 저장 실패
        string b = """{"credentials":{"user":"b@x.com"}}""";
        Assert.ThrowsAny<Exception>(() => ConfigFiles.SaveAll(new AppSettings(), b, "SUPA_DSN=x\n", NoDb));
        Assert.Contains("b@x.com", File.ReadAllText(Paths.GarminConfig));   // 디스크에는 이미 B
        Assert.False(File.Exists(Paths.GarminToken));                       // A 토큰은 B를 쓰기 전에 지웠다
        File.Delete(Paths.CheckToken);              // 창을 닫아 확인 결과를 버렸다
        Directory.Delete(Paths.EnvFile);
        // 다시 열면 저장된 이메일 B·빈 비밀번호로 재확인하고(캐시 토큰이 없어 B 자격증명 로그인) 저장을 재시도한다
        ConfigFiles.SaveAll(new AppSettings(), b, "SUPA_DSN=x\n", NoDb);
        Assert.True(ConfigFiles.LoadSettings().ConfigComplete);
        Assert.False(File.Exists(Paths.GarminToken));   // 다음 로그인도 A 토큰이 아니라 B 자격증명
    }
}
