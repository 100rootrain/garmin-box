using System.IO.Compression;
using System.Security.Cryptography;

namespace GarminBox;

static class Installer
{
    const string Marker = "payload.sha256";

    /// 잠금을 잡은 상태에서 호출한다 (동기화 중인 코드를 덮어쓰지 않게). 런타임 표식이 맞으면 압축 해제를 건너뛴다.
    public static void Deploy()
    {
        var assembly = typeof(Installer).Assembly;
        using var hash = assembly.GetManifestResourceStream("payload.sha256")
            ?? throw new InvalidOperationException("이 빌드에는 Python 번들이 없습니다. build\\release.ps1로 만든 EXE를 쓰세요.");
        string expected = new StreamReader(hash).ReadToEnd().Trim();
        if (!IsCurrent(Paths.Runtime, expected))
        {
            using var zip = assembly.GetManifestResourceStream("payload.zip")!;
            ExtractRuntime(zip, expected, Paths.Runtime);
        }
        CopySelf();
        CreateShortcut();
    }

    public static bool IsCurrent(string runtime, string sha256)
    {
        string marker = Path.Combine(runtime, Marker);
        return File.Exists(marker) && File.ReadAllText(marker).Trim() == sha256;
    }

    public static void ExtractRuntime(Stream zip, string expectedSha256, string runtime)
    {
        string archive = runtime + ".zip.tmp", next = runtime + ".new", old = runtime + ".old";
        try
        {
            using (var file = File.Create(archive)) zip.CopyTo(file);
            string actual;
            using (var file = File.OpenRead(archive)) actual = Convert.ToHexStringLower(SHA256.HashData(file));
            if (actual != expectedSha256) throw new InvalidDataException("Python 번들 무결성 확인에 실패했습니다. 프로그램을 다시 내려받으세요.");
            if (Directory.Exists(next)) Directory.Delete(next, true);
            ZipFile.ExtractToDirectory(archive, next);
            if (Directory.Exists(old)) Directory.Delete(old, true);
            if (Directory.Exists(runtime)) Directory.Move(runtime, old);
            Directory.Move(next, runtime);
            File.WriteAllText(Path.Combine(runtime, Marker), expectedSha256);   // 표식은 마지막: 중간에 끊기면 다음 실행에서 다시 푼다
            if (Directory.Exists(old)) Directory.Delete(old, true);
        }
        finally
        {
            File.Delete(archive);
        }
    }

    /// 다운로드 폴더의 EXE를 옮겨도 예약 작업이 깨지지 않게 고정 경로로 복사한다.
    static void CopySelf()
    {
        string self = Path.GetFullPath(Environment.ProcessPath!);
        if (string.Equals(self, Paths.Exe, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            File.Copy(self, Paths.Exe + ".new", overwrite: true);
            File.Move(Paths.Exe + ".new", Paths.Exe, overwrite: true);
        }
        catch (IOException e)
        {
            throw new IOException($"설치된 garmin-box가 열려 있어 바꾸지 못했습니다. 열린 창을 닫고 다시 실행하세요. ({e.Message})", e);
        }
    }

    static void CreateShortcut()
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        dynamic link = shell.CreateShortcut(Paths.StartMenuLink);
        link.TargetPath = Paths.Exe;
        link.WorkingDirectory = Paths.InstallDir;
        link.Description = "garmin-box 설정";
        link.Save();
    }
}
