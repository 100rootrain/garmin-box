namespace GarminBox.Tests;

public class PathsTests
{
    [Fact]
    public void NonAsciiInstallPathStopsBeforeAnything()
    {
        using var profile = new TempProfile();
        string korean = Path.Combine(profile.Dir.Root, "홍길동");
        Environment.SetEnvironmentVariable("LOCALAPPDATA", korean);
        string? problem = Paths.Preflight();
        Assert.StartsWith("현재 Python 번들은 한글 등 비 ASCII 설치 경로에서 인증서를 읽지 못합니다. 이 PC에서는 설정을 진행할 수 없습니다.", problem);
        Assert.Contains(Path.Combine(korean, "garmin-box"), problem);
        Assert.False(Directory.Exists(korean));   // 파일 배치 전에 멈춘다
    }

    [Fact]
    public void AsciiProfilePasses()
    {
        using var profile = new TempProfile();
        Assert.Null(Paths.Preflight());
    }
}
