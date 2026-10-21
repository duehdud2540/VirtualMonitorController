namespace VirtualMonitor;

internal static class DirectXFrameStatisticsDiagnostics
{
    // 가짜 시간과 출력 결과로 FPS 분류, 평균 시간, 초기화와 0초 구간을 검증합니다.
    internal static void Run()
    {
        double now = 0;
        var stats = new DirectXFrameStatistics(() => now);
        for (int i = 0; i < 40; i++) { stats.Receive(); stats.Submit(0, true, 2); }
        for (int i = 0; i < 20; i++) stats.Submit(0, false, 1);
        stats.Receive(); stats.Submit(1, true, 3);
        stats.Receive(); stats.Submit(unchecked((int)0x887A0005), true, 3);
        now = 2;
        var first = stats.Sample();
        Require(first.ReceivedFps == 21 && first.SubmittedFps == 20 && first.RedrawFps == 10,
            "New submissions must exclude redraws, occlusion and failures.");
        Require(Math.Abs(first.AverageSubmitMilliseconds - 106.0 / 62) < 0.0001, "Submit timing average.");
        now = 3;
        Require(stats.Sample() == new DirectXFrameStatistics.Snapshot(0, 0, 0, 0), "Idle interval must show zero.");
        stats.Receive(); stats.Submit(0, true, 1);
        now = 100;
        stats.Reset(); // Pause/resume or monitor switch discards the previous measurement window.
        now = 101;
        stats.Receive(); stats.Submit(0, true, 1);
        Require(stats.Sample().SubmittedFps == 1, "Reset must exclude paused time and old frames.");
        Require(stats.Sample().SubmittedFps == 0, "Zero duration must stay finite.");
        Console.WriteLine("DirectX frame statistics: all checks passed.");
    }

    // 조건이 거짓이면 설명을 담은 진단 실패 예외를 발생시킵니다.
    private static void Require(bool passed, string message)
    {
        if (!passed) throw new InvalidOperationException(message);
    }
}
