namespace GarminBox.Tests;

public class MainFormTests
{
    [Fact]
    public void StartFailureReplacesEarlierSuccess()   // 임시 프로필에는 Python이 없어 Process.Start가 실패한다
    {
        using var profile = new TempProfile();
        Directory.CreateDirectory(Paths.InstallDir);
        File.WriteAllText(Paths.Status, """{"result":"success"}""");
        RunResult? result = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new MainForm();   // 표시하지 않으므로 OnLoad(배치)는 돌지 않는다
                result = form.RunLocked("sync", [], TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            }
            catch (Exception e) { error = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        Assert.Null(error);
        Assert.Null(result);
        Assert.Equal("failed", (string?)ConfigFiles.ReadStatus()?["result"]);
    }
}
