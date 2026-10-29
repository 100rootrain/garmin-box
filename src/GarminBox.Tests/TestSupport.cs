[assembly: CollectionBehavior(DisableTestParallelization = true)]   // TempProfile이 프로세스 환경 변수를 바꾼다

namespace GarminBox.Tests;

sealed class TempDir : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("garmin-box-test-").FullName;
    public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } }
}

/// USERPROFILE·LOCALAPPDATA를 임시 폴더로 바꾼다 (Paths와 run_batch.py가 같은 변수를 기준으로 한다).
sealed class TempProfile : IDisposable
{
    readonly string? home = Environment.GetEnvironmentVariable("USERPROFILE");
    readonly string? local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
    public TempDir Dir { get; } = new();

    public TempProfile()
    {
        Environment.SetEnvironmentVariable("USERPROFILE", Dir.Root);
        Environment.SetEnvironmentVariable("LOCALAPPDATA", Path.Combine(Dir.Root, "Local"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("USERPROFILE", home);
        Environment.SetEnvironmentVariable("LOCALAPPDATA", local);
        Dir.Dispose();
    }
}

static class Repo
{
    public static string Root { get; } = Find();
    static string Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "GarminBox.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("GarminBox.slnx");
    }
}

/// 임시 프로필의 Paths.Python 자리에 번들 Python(작업 1)을 junction으로 잇는다(126MB 복사 대신). run_batch.py는 없다.
sealed class LinkedPython : IDisposable
{
    readonly string link = Path.GetDirectoryName(Paths.Python)!;

    public LinkedPython()
    {
        Directory.CreateDirectory(Paths.Runtime);
        var mklink = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (string arg in new[] { "/c", "mklink", "/J", link, Path.Combine(Repo.Root, "build", "out", "runtime", "python") })
            mklink.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(mklink)!;
        process.WaitForExit();
        if (!File.Exists(Paths.Python)) throw new FileNotFoundException("번들 Python 연결 실패", Paths.Python);
    }

    public void Dispose() => Directory.Delete(link);   // 비재귀 삭제는 연결만 지운다: 프로필 정리가 번들 런타임 안으로 들어가지 않게
}
