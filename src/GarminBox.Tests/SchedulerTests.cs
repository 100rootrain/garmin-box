namespace GarminBox.Tests;

public class SchedulerTests
{
    const string Exe = @"C:\Users\me\AppData\Local\garmin-box\GarminBox.exe";
    static readonly TimeOnly At = new(9, 10);
    static string Built() => Scheduler.BuildXml(Exe, At, @"PC\me", new DateOnly(2026, 10, 29));

    static string Task(string command, string arguments) =>
        $"""<Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task"><Actions><Exec><Command>{command}</Command><Arguments>{arguments}</Arguments></Exec></Actions></Task>""";

    [Fact]
    public void BuiltDefinitionPassesReadBackCheck() => Assert.Empty(Scheduler.Problems(Built(), Exe, At));

    [Fact]
    public void DefaultsWindowsMayOmitAreReadAsDefaults()
    {
        Assert.Empty(Scheduler.Problems(Built().Replace("<MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>", ""), Exe, At));
        Assert.Contains("DisallowStartIfOnBatteries=true",
            Scheduler.Problems(Built().Replace("<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>", ""), Exe, At));
    }

    [Theory]
    [InlineData("<ExecutionTimeLimit>PT1H</ExecutionTimeLimit>", "<ExecutionTimeLimit>PT72H</ExecutionTimeLimit>")]
    [InlineData("<StartWhenAvailable>true</StartWhenAvailable>", "")]
    [InlineData("<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>", "")]
    [InlineData("<LogonType>InteractiveToken</LogonType>", "<LogonType>Password</LogonType>")]
    public void UnsafeDefinitionsAreRejected(string from, string to) =>
        Assert.NotEmpty(Scheduler.Problems(Built().Replace(from, to), Exe, At));

    [Theory]
    [InlineData("wscript.exe", "\"C:\\x\\garmin-box\\run_garmin_batch_hidden.vbs\"", true)]
    [InlineData("powershell", "-ExecutionPolicy Bypass -File C:\\x\\run_garmin_batch.ps1", true)]
    [InlineData("notepad.exe", "", false)]
    public void LegacyBatchTasksAreFound(string command, string arguments, bool legacy) =>
        Assert.Equal(legacy, Scheduler.IsLegacyBatch(Task(command, arguments)));

    [Fact]
    public void MissingTaskIsNull()   // 실제 작업 스케줄러를 읽기만 한다
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        service.Connect();
        Assert.Null(Scheduler.FindTask(service.GetFolder(@"\"), $"garmin-box-test-{Guid.NewGuid():N}"));
    }

    [Fact]
    public void OnlyOurExecutableCountsAsOurs()
    {
        Assert.True(Scheduler.IsOurs(Task(Exe.ToUpperInvariant(), "--sync --scheduled"), Exe));
        Assert.False(Scheduler.IsOurs(Task(@"C:\other\tool.exe", ""), Exe));
    }
}
