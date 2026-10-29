namespace GarminBox.Tests;

public class ProgramTests
{
    [Fact]
    public void ScriptExitWithoutStatusReplacesEarlierSuccess()
    {
        using var profile = new TempProfile();
        using var python = new LinkedPython();   // Python은 시작되지만 run_batch.py를 열지 못해 상태 없이 종료 코드 2
        File.WriteAllText(Paths.Status, """{"result":"success"}""");
        Assert.Equal(2, Program.Sync(scheduled: true));
        Assert.Equal("failed", (string?)ConfigFiles.ReadStatus()?["result"]);
    }
}
