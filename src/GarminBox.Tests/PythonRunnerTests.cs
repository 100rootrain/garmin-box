using System.Diagnostics;
using System.Text.Json;

namespace GarminBox.Tests;

public class PythonRunnerTests
{
    static string Python => Path.Combine(Repo.Root, "build", "out", "runtime", "python", "python.exe");   // 작업 1
    static string Fixture => Path.Combine(Repo.Root, "src", "GarminBox.Tests", "runner_fixture.py");
    static readonly Dictionary<string, string> NoEnv = new();

    [Fact]
    public async Task KoreanEventsAndMfaReplyRoundTripAfterStart()
    {
        var events = new List<JsonElement>();
        var result = await PythonRunner.RunAsync(Python, Fixture, ["echo"], NoEnv, events.Add,
            _ => Task.FromResult<string?>("123456"), TimeSpan.FromSeconds(30), default);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("한글 진행 메시지 ✓", events[0].GetProperty("message").GetString());
        Assert.Equal("123456", events[^1].GetProperty("code").GetString());
    }

    [Fact]
    public async Task TimeoutKillsPythonAndGrandchild()
    {
        int child = 0;
        var result = await PythonRunner.RunAsync(Python, Fixture, ["hang"], NoEnv,
            e => { if (e.GetProperty("event").GetString() == "child") child = e.GetProperty("pid").GetInt32(); },
            _ => Task.FromResult<string?>(null), TimeSpan.FromSeconds(5), default);
        Assert.True(result.TimedOut);
        Assert.NotEqual(0, child);
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(child));   // 손자도 Job과 함께 종료
    }

    [Fact]
    public async Task TimeoutDuringMfaPromptReturnsAndCancelsPrompt()
    {
        CancellationToken prompt = default;
        var run = PythonRunner.RunAsync(Python, Fixture, ["echo"], NoEnv, _ => { },
            token => { prompt = token; return new TaskCompletionSource<string?>().Task; },   // 사용자가 입력하지 않는 대화상자
            TimeSpan.FromSeconds(1), default);
        Assert.Same(run, await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(15))));   // 입력을 기다리며 멈추지 않는다
        Assert.True((await run).TimedOut);
        Assert.True(prompt.IsCancellationRequested);   // 화면은 이 토큰으로 대화상자를 닫는다
    }
}
