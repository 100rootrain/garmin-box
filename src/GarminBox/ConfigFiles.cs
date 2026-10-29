using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GarminBox;

/// settings.json: 비밀값 없는 앱 상태. config_complete가 true일 때만 run_batch.py가 setup/sync를 실행한다.
sealed record AppSettings(string ScheduleTime = "09:10", bool ConfigComplete = false);

static class ConfigFiles
{
    static readonly UTF8Encoding Utf8 = new(false);
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };

    /// KEY=VALUE, 첫 '='만 구분자. run_batch.py·기존 run_garmin_batch.ps1과 같은 규칙.
    public static Dictionary<string, string> ParseEnv(string text)
    {
        var values = new Dictionary<string, string>();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            int i = line.IndexOf('=');
            if (line.StartsWith('#') || i < 1) continue;
            values[line[..i].Trim()] = line[(i + 1)..];
        }
        return values;
    }

    public static string FormatEnv(IReadOnlyDictionary<string, string> values)
    {
        foreach (var (key, value) in values)
            if ((key + value).IndexOfAny(['\r', '\n', '\0']) >= 0)
                throw new ArgumentException($"{key} 값에는 줄바꿈이나 NUL 문자를 넣을 수 없습니다.");
        return string.Concat(values.Select(pair => $"{pair.Key}={pair.Value}\n"));
    }

    /// 기존 파일이 있으면 알 수 없는 설정·사용자 지정 데이터 경로를 보존하고, 없으면 번들 GarminDB 예제에서 시작한다.
    /// 깨진 기존 파일은 JsonException으로 거부한다(기본값으로 덮어쓰지 않는다).
    public static string MergeGarminConfig(string? existing, string example, string email, string? password)
    {
        var root = JsonNode.Parse(existing ?? example) as JsonObject ?? throw new JsonException("Garmin 설정이 JSON 객체가 아닙니다.");
        if (root["credentials"] is not JsonObject credentials) root["credentials"] = credentials = new JsonObject();
        credentials["user"] = email;
        if (password is not null)
        {
            credentials["password"] = password;
            credentials["secure_password"] = false;
            credentials["password_file"] = null;
        }
        return root.ToJsonString(Json);
    }

    public static AppSettings LoadSettings()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Paths.Settings), Json) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException) { return new(); }   // 없거나 깨졌으면 설정 미완료
    }

    public static void SaveSettings(AppSettings settings) => AtomicWrite(Paths.Settings, JsonSerializer.Serialize(settings, Json));

    /// 내용을 쓰기 전에 접근을 제한·확인한 빈 임시 파일을 만든다 (프로필 ACL이 넓어도 평문이 노출되는 구간이 없게).
    internal static string CreateProtectedTemp(string path)
    {
        string temp = path + ".tmp";
        File.WriteAllBytes(temp, []);
        SecretAcl.Protect(temp);   // 제한하지 못하면 예외: 내용을 쓰지 않는다
        return temp;
    }

    /// 보호된 임시 파일에 완성한 뒤 교체하고 직전 파일을 .bak 하나로 보관한다. 실패하면 임시 파일을 지운다.
    /// File.Replace는 기존 대상의 DACL을 새 내용에 그대로 남기므로(실행 확인), 기존 대상·백업을 먼저 보호한다.
    /// 그러면 교체 결과와 백업, Move한 임시 파일 모두 제한된 ACL을 가져 사후 보호가 필요 없다.
    public static void AtomicWrite(string path, string content)
    {
        string backup = path + ".bak";
        foreach (string existing in new[] { path, backup })
            if (File.Exists(existing)) SecretAcl.Protect(existing);   // 제한하지 못하면 예외: 새 내용을 쓰지 않는다
        string? temp = null;
        try
        {
            temp = CreateProtectedTemp(path);
            File.WriteAllText(temp, content, Utf8);   // 기존 파일을 덮어쓰므로 위에서 정한 ACL이 유지된다
            if (File.Exists(path)) File.Replace(temp, path, backup);
            else File.Move(temp, path);
        }
        finally
        {
            if (temp is not null) File.Delete(temp);   // 성공했으면 이미 없다
        }
    }

    static string? UserOf(string json) => (string?)JsonNode.Parse(json)?["credentials"]?["user"];

    /// 잠금을 잡은 상태에서 호출한다. 두 파일 저장과 권한 확인이 모두 끝나야 설정 완료로 표시한다.
    /// 파일 간 자동 롤백은 없다: 중간에 실패하면 미완료 상태로 남아 동기화가 막히고, 사용자가 다시 저장한다.
    /// dbPath는 연결 확인 결과의 garmin.db_path. 확인 뒤 수동 동기화가 DB를 처음 만들었을 수 있어 여기서 다시 막는다
    /// (run_batch.refuse_account_change와 같은 규칙).
    public static void SaveAll(AppSettings settings, string garminJson, string envText, string dbPath)
    {
        string? saved = File.Exists(Paths.GarminConfig) ? UserOf(File.ReadAllText(Paths.GarminConfig)) : null;
        bool sameAccount = string.Equals(saved, UserOf(garminJson), StringComparison.OrdinalIgnoreCase);
        if (!sameAccount && !string.IsNullOrEmpty(saved) && File.Exists(dbPath))
        {
            File.Delete(Paths.CheckToken);   // 거부한 계정의 확인 토큰이 다음 같은 계정 저장에서 운영 토큰이 되지 않게
            throw new InvalidOperationException("이 PC에는 다른 Garmin 계정의 운동 DB가 있어 계정을 바꿀 수 없습니다.");
        }
        // 계정을 바꾸면 새 이메일을 디스크에 쓰기 전에 이전 계정 토큰부터 지운다. 중간에 실패한 뒤 재시도할 때는 저장된
        // 이메일이 이미 새 계정이라 같은 계정으로 보이므로, 그때 남은 토큰을 새 계정 것으로 오인하지 않게 한다.
        // 확인 토큰이 없으면(로그인은 됐지만 토큰 저장 실패) 다음 동기화는 저장한 새 자격증명으로 로그인한다.
        if (!sameAccount) File.Delete(Paths.GarminToken);
        SaveSettings(settings with { ConfigComplete = false });
        Directory.CreateDirectory(Paths.GarminDbDir);
        SecretAcl.ProtectTree(Paths.GarminDbDir);   // 기존 설정·토큰·백업의 과도한 권한도 여기서 정리
        AtomicWrite(Paths.GarminConfig, garminJson);
        AtomicWrite(Paths.EnvFile, envText);
        // 연결 확인에서 받은 토큰은 설정이 저장된 뒤에만 운영 토큰이 된다. 저장하지 않으면 기존 계정 토큰이 그대로다.
        if (File.Exists(Paths.CheckToken)) File.Move(Paths.CheckToken, Paths.GarminToken, overwrite: true);
        SaveSettings(settings with { ConfigComplete = true });
    }

    public static JsonNode? ReadStatus()
    {
        try { return JsonNode.Parse(File.ReadAllText(Paths.Status)); }
        catch (Exception e) when (e is IOException or JsonException) { return null; }
    }

    /// ConfigComplete는 설정 저장 완료일 뿐이다. 저장 뒤의 실행(setup·sync)이 성공으로 상태를 남겨야 현재 설정으로 동기화된 것이다.
    public static bool SyncedSinceSaved() => LoadSettings().ConfigComplete && (string?)ReadStatus()?["result"] == "success"
        && File.GetLastWriteTimeUtc(Paths.Status) > File.GetLastWriteTimeUtc(Paths.Settings);

    /// run_batch.py는 main에 들어간 뒤의 실패를 모두 상태로 남긴다. 그 전에 끝난 오류 종료(스크립트 열기·import 실패 등)는
    /// 상태 파일이 실행 전 그대로이므로 기본 실패를 남긴다. Python이 남긴 reauth·실패 원인은 덮어쓰지 않는다.
    public static void RecordUnreportedFailure(string mode, DateTime statusBefore, int exitCode)
    {
        if (exitCode != 0 && File.GetLastWriteTimeUtc(Paths.Status) == statusBefore)
            WriteFailureStatus(mode, $"결과를 남기지 못하고 종료됐습니다(종료 코드 {exitCode}). 다시 시도하세요.");
    }

    /// Python이 상태를 쓰지 못한 경우(시간 초과로 종료, 시작 실패)에만 쓴다.
    public static void WriteFailureStatus(string mode, string message) => AtomicWrite(Paths.Status, new JsonObject
    {
        ["finished_at"] = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"),
        ["mode"] = mode, ["result"] = "failed", ["message"] = message, ["warnings"] = new JsonArray(),
    }.ToJsonString(Json));
}
