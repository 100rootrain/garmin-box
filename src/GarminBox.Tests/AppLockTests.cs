namespace GarminBox.Tests;

public class AppLockTests
{
    static string Name() => $@"Local\garmin-box-test-{Guid.NewGuid():N}";

    [Fact]
    public void SecondHolderIsRefused()
    {
        string name = Name();
        using var first = AppLock.TryAcquire(name);
        AppLock? second = null;
        var thread = new Thread(() => second = AppLock.TryAcquire(name));
        thread.Start(); thread.Join();
        Assert.NotNull(first);
        Assert.Null(second);
    }

    [Fact]
    public void SameThreadCannotAcquireTwiceUntilReleased()
    {
        string name = Name();
        using (var first = AppLock.TryAcquire(name))
        {
            Assert.NotNull(first);
            Assert.Null(AppLock.TryAcquire(name));   // mutex만으로는 같은 스레드 재진입이 성공한다
        }
        using var again = AppLock.TryAcquire(name);
        Assert.NotNull(again);
    }

    [Fact]
    public void AbandonedLockIsOwnedReportedAndReleased()
    {
        string name = Name();
        var thread = new Thread(() => AppLock.TryAcquire(name));   // 해제 없이 스레드 종료 = 비정상 종료한 이전 실행
        thread.Start(); thread.Join();
        using (var recovered = AppLock.TryAcquire(name))
        {
            Assert.NotNull(recovered);
            Assert.True(recovered.WasAbandoned);
        }
        using var again = AppLock.TryAcquire(name);
        Assert.False(again!.WasAbandoned);
    }
}
