using System.IO.Compression;
using System.Security.Cryptography;

namespace GarminBox.Tests;

public class InstallerTests
{
    static byte[] Zip()
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(zip.CreateEntry("app/run_batch.py").Open()))
            writer.Write("print(1)");
        return memory.ToArray();
    }

    [Fact]
    public void WrongHashChangesNothing()
    {
        using var dir = new TempDir();
        string runtime = Path.Combine(dir.Root, "runtime");
        Assert.Throws<InvalidDataException>(() => Installer.ExtractRuntime(new MemoryStream(Zip()), new string('0', 64), runtime));
        Assert.False(Directory.Exists(runtime));
    }

    [Fact]
    public void ExtractsThenMarksCurrent()
    {
        using var dir = new TempDir();
        string runtime = Path.Combine(dir.Root, "runtime");
        byte[] zip = Zip();
        string sha = Convert.ToHexStringLower(SHA256.HashData(zip));
        Directory.CreateDirectory(runtime);
        File.WriteAllText(Path.Combine(runtime, "stale.txt"), "old");   // 이전 버전

        Installer.ExtractRuntime(new MemoryStream(zip), sha, runtime);

        Assert.True(File.Exists(Path.Combine(runtime, "app", "run_batch.py")));
        Assert.False(File.Exists(Path.Combine(runtime, "stale.txt")));
        Assert.True(Installer.IsCurrent(runtime, sha));
    }

    [Fact]
    public void RuntimeWithoutMarkerIsNotComplete()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(Path.Combine(dir.Root, "runtime", "python"));   // 강제 종료로 남은 부분 파일
        Assert.False(Installer.IsCurrent(Path.Combine(dir.Root, "runtime"), "abc"));
    }
}
