namespace VideoDownloader.Native;

internal sealed record MediaWaveformRequest(MediaClipRequest ClipRequest,bool IsEnd);

internal readonly record struct WaveformBin(double Peak,double Rms);

/// <summary>Immutable, time-aligned envelope of decoded local audio.</summary>
internal sealed class AudioWaveform
{
    internal const int MaximumBins=320;
    internal double StartSeconds {get;}
    internal double EndSeconds {get;}
    internal double DurationSeconds=>EndSeconds-StartSeconds;
    internal double DecodedEndSeconds {get;}
    internal IReadOnlyList<WaveformBin> Bins {get;}
    internal AudioWaveform(double startSeconds,double endSeconds,IEnumerable<WaveformBin> bins,double? decodedEndSeconds=null)
    {
        if(!double.IsFinite(startSeconds) || !double.IsFinite(endSeconds) || startSeconds<0 ||
            endSeconds<=startSeconds || endSeconds-startSeconds>MediaClipService.MaxPreviewSeconds+.001)
            throw new ArgumentException("Некорректное окно звуковой волны.");
        var copy=bins.Take(MaximumBins+1).ToArray();
        if(copy.Length is 0 or >MaximumBins || copy.Any(bin=>!double.IsFinite(bin.Peak) || !double.IsFinite(bin.Rms) ||
            bin.Peak is <0 or >1 || bin.Rms is <0 or >1 || bin.Rms>bin.Peak+1e-9))
            throw new ArgumentException("Некорректные значения звуковой волны.");
        double decoded=decodedEndSeconds??endSeconds;
        if(!double.IsFinite(decoded) || decoded<startSeconds || decoded>endSeconds)
            throw new ArgumentException("Некорректная длительность аудиодорожки.");
        StartSeconds=startSeconds;EndSeconds=endSeconds;DecodedEndSeconds=decoded;
        Bins=Array.AsReadOnly(copy);
    }
}

internal sealed record MediaWaveformResult(string Url,double RequestedSeconds,AudioWaveform? Waveform,string Error)
{
    internal bool Success=>Waveform is not null;
}
