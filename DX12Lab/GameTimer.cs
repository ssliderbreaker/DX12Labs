using System.Diagnostics;

namespace DX12Lab;

public class GameTimer
{
    private readonly Stopwatch _stopwatch = new();
    private double _prevTime;

    public double TotalTime { get; private set; }
    public double DeltaTime { get; private set; }

    public void Start()
    {
        _stopwatch.Start();
        _prevTime = _stopwatch.Elapsed.TotalSeconds;
    }

    public void Tick()
    {
        double currentTime = _stopwatch.Elapsed.TotalSeconds;
        DeltaTime = currentTime - _prevTime;
        _prevTime = currentTime;
        TotalTime = currentTime;
    }
}