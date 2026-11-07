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
    // A millisecond label which rounds a nominal frame boundary denotes that
    // boundary. Other typed times retain their millisecond meaning. Keep this
    // conversion shared by export and previews; never export the display rounding.
    internal static double Boundary(double seconds,double? fps,double? duration=null)
    {
        if(!KnownFps(fps))return seconds;
        double frame=Math.Round(seconds*fps!.Value,MidpointRounding.AwayFromZero);
        double exact=frame/fps.Value;
        return Math.Abs(Milliseconds(exact)-seconds)<1e-9 && (duration is null || exact<=duration.Value)
            ?exact:seconds;
    }
    // Sampling must not reinterpret a boundary. Only the editor's explicit
    // frame-grid context may restore a rounded label through Boundary first.
    internal static double FrameSample(double boundary,bool isEnd,double? fps,double? duration=null)
    {
        if(!KnownFps(fps))return isEnd?Math.Max(0,boundary-.05):boundary;
        // The first nominal PTS at/after the start, or last strictly before the
        // exclusive end. Tolerance only absorbs floating-point frame arithmetic.
        double index=Math.Ceiling(boundary*fps!.Value-1e-7)-(isEnd?1:0);
        if(duration is >0)index=Math.Min(index,Math.Ceiling(duration.Value*fps.Value-1e-7)-1);
        return Math.Max(0,index)/fps.Value;
    }
    internal static double? CommonFrameRate(IEnumerable<MediaFormat> formats)
    {
        var video=formats.Where(format=>format.HasVideo).ToArray();
        if(video.Length==0 || video.Any(format=>!KnownFps(format.Fps)))return null;
        var values=video.Select(format=>format.Fps).Distinct().Take(2).ToArray();
        return values.Length==1?values[0]:null;
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
