namespace SwtorRecap.Engine;

/// <summary>One-times replay follows recorded forward time; clock adjustments never create a negative wait.</summary>
public sealed class ReplayTimeline
{
    private TimeOnly? _previous;
    private TimeSpan _elapsed;
    public TimeSpan At(TimeOnly time)
    {
        if(_previous is { } previous)
        {
            var delta=time.ToTimeSpan()-previous.ToTimeSpan();
            if(previous.Hour>=23&&time.Hour<=1&&delta<-TimeSpan.FromHours(20))delta+=TimeSpan.FromDays(1);
            if(delta>TimeSpan.Zero)_elapsed+=delta;
        }
        _previous=time;return _elapsed;
    }
}
