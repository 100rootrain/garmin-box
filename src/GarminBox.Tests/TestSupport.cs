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
