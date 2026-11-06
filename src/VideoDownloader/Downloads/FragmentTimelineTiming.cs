namespace VideoDownloader.Native;

/// <summary>Millisecond-safe range editing; frame steps snap to the source's frame grid.</summary>
internal static class FragmentTimelineTiming
{
    internal const double MinimumRange=.001;
    internal const double UnknownFrameStep=.1;
    internal static bool KnownDuration(double? duration)=>duration is >0 && double.IsFinite(duration.Value) &&
        duration.Value>=MinimumRange && duration.Value<TimeSpan.MaxValue.TotalSeconds;
    internal static bool KnownFps(double? fps)=>fps is >0 and <=1000 && double.IsFinite(fps.Value);
    internal static double Milliseconds(double value)=>Math.Round(value,3,MidpointRounding.AwayFromZero);
    internal static double LastMillisecond(double duration)
    {
        double rounded=Milliseconds(duration);
        return rounded<=duration?rounded:Milliseconds(rounded-MinimumRange);
    }
    internal static double Step(double current,int direction,double? fps,double minimum,double maximum)
    {
        if(!double.IsFinite(current) || !double.IsFinite(minimum) || !double.IsFinite(maximum) || maximum<minimum)
            throw new ArgumentException("Invalid timeline bounds.");
        if(direction==0)return Math.Clamp(current,minimum,maximum);
        double target;
        if(KnownFps(fps))
        {
            double frame=Math.Round(current*fps!.Value,MidpointRounding.AwayFromZero);
            target=(frame+direction)/fps.Value;
        }
        else target=current+direction*UnknownFrameStep;
        return Math.Clamp(Milliseconds(target),minimum,maximum);
    }
}
