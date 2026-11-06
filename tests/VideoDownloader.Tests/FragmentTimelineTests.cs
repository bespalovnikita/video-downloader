using VideoDownloader.Native;

internal static class FragmentTimelineTests
{
    internal static void Run()
    {
        static void Require(bool condition,string message)
        {if(!condition)throw new InvalidOperationException("Fragment timeline: "+message);}
        Require(FragmentTimelineTiming.LastMillisecond(12.34567)==12.345 &&
            FragmentTimelineTiming.LastMillisecond(1.001)==1.001,
            "A fractional duration must use the last legal millisecond without losing an exact millisecond.");
        double time=0;
        for(int index=0;index<30;index++)time=FragmentTimelineTiming.Step(time,1,29.97,0,10);
        Require(time==1.001,"Fractional FPS steps must stay on the frame grid instead of accumulating rounded-step drift.");
        double next=FragmentTimelineTiming.Step(time,1,29.97,0,10);
        Require(next==1.034 && FragmentTimelineTiming.Step(next,-1,29.97,0,10)==1.001,
            "A rounded fractional-frame timestamp must step forward and back to the same source frame.");
        Require(FragmentTimelineTiming.Step(1,1,25,0,10)==1.04 && FragmentTimelineTiming.Step(1,-1,25,0,10)==.96,
            "Known FPS must move one frame in either direction.");
        Require(FragmentTimelineTiming.Step(1,1,null,0,10)==1.1 &&
            FragmentTimelineTiming.Step(1,-10,double.NaN,0,10)==0,
            "Unknown FPS uses an explicit tenth-second step and clamps at zero.");
        Require(FragmentTimelineTiming.Step(7.99,1,25,0,7.999)==7.999 &&
            FragmentTimelineTiming.Step(2,-1,25,2,8)==2,
            "Stepping must never cross a neighbouring range boundary.");
        Require(!FragmentTimelineTiming.KnownDuration(double.NaN) && !FragmentTimelineTiming.KnownDuration(.0005) &&
            !FragmentTimelineTiming.KnownFps(2000),"Unrepresentable duration/FPS cannot pretend to provide millisecond frame precision.");
        bool refused=false;
        try{FragmentTimelineTiming.Step(1,1,25,3,2);}catch(ArgumentException){refused=true;}
        Require(refused,"Invalid bounds must fail before producing a timestamp.");
        Console.WriteLine("PASS: timeline frame grid, fractional FPS, millisecond duration and boundary-safe fallback steps");
    }
}
