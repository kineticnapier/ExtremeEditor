using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExtremeEditor.Core;

namespace ExtremeEditor.Wpf.Native;

internal static class FlashTimelineBuilder
{
    internal static NativeFlashEvent[] Build(LevelDocument level, TimingMap timingMap)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(timingMap);
        VfxTimeline timeline = VfxTimelineBuilder.Build(
            level, timingMap,
            static action => string.Equals(action.EventType, "Flash", StringComparison.Ordinal));
        return timeline.Occurrences.Where(static o => o.Active).Select(ToNative).ToArray();
    }

    private static NativeFlashEvent ToNative(VfxOccurrence o)
    {
        JsonObject? p = o.SourceEvent.PropertyOverrides;
        return new NativeFlashEvent
        {
            StartTime = o.StartTime,
            DurationSeconds = Math.Max(0.0, o.DurationSeconds ?? 0.0),
            StartColor = ReadColor(p, "startColor", 0xffffffu),
            EndColor = ReadColor(p, "endColor", 0xffffffu),
            StartOpacity = ReadPercent(p, "startOpacity", 100f),
            EndOpacity = ReadPercent(p, "endOpacity", 0f),
            Plane = string.Equals(ReadString(p, "plane"), "Foreground", StringComparison.OrdinalIgnoreCase)
                ? NativeFlashEvent.PlaneForeground : NativeFlashEvent.PlaneBackground,
            Ease = ResolveEase(o.Ease),
            SourceIndex = o.SourceIndex
        };
    }

    private static string? ReadString(JsonObject? p,string name)
    {
        if(p?[name] is not JsonValue v) return null;
        if(v.TryGetValue(out string? s)) return s;
        return v.TryGetValue(out JsonElement e) && e.ValueKind==JsonValueKind.String ? e.GetString() : null;
    }

    private static float ReadPercent(JsonObject? p,string name,float fallback)
    {
        if(p?[name] is not JsonValue v) return fallback/100f;
        if(v.TryGetValue(out double n)) return (float)Math.Clamp(n/100.0,0.0,1.0);
        if(v.TryGetValue(out string? s) && double.TryParse(s,NumberStyles.Float,CultureInfo.InvariantCulture,out n))
            return (float)Math.Clamp(n/100.0,0.0,1.0);
        return fallback/100f;
    }

    private static uint ReadColor(JsonObject? p,string name,uint fallback)
    {
        string? s=ReadString(p,name);
        if(string.IsNullOrWhiteSpace(s)) return fallback;
        string hex=s.Trim().TrimStart('#');
        if(hex.Length>=6) hex=hex[..6];
        return uint.TryParse(hex,NumberStyles.HexNumber,CultureInfo.InvariantCulture,out uint v) ? v&0xffffffu : fallback;
    }

    private static uint ResolveEase(string? e)=>e switch
    {
        "InSine"=>NativeCameraEvent.EaseInSine,"OutSine"=>NativeCameraEvent.EaseOutSine,"InOutSine"=>NativeCameraEvent.EaseInOutSine,
        "InQuad"=>NativeCameraEvent.EaseInQuad,"OutQuad"=>NativeCameraEvent.EaseOutQuad,"InOutQuad"=>NativeCameraEvent.EaseInOutQuad,
        "InCubic"=>NativeCameraEvent.EaseInCubic,"OutCubic"=>NativeCameraEvent.EaseOutCubic,"InOutCubic"=>NativeCameraEvent.EaseInOutCubic,
        "InQuart"=>NativeCameraEvent.EaseInQuart,"OutQuart"=>NativeCameraEvent.EaseOutQuart,"InOutQuart"=>NativeCameraEvent.EaseInOutQuart,
        "InQuint"=>NativeCameraEvent.EaseInQuint,"OutQuint"=>NativeCameraEvent.EaseOutQuint,"InOutQuint"=>NativeCameraEvent.EaseInOutQuint,
        "InExpo"=>NativeCameraEvent.EaseInExpo,"OutExpo"=>NativeCameraEvent.EaseOutExpo,"InOutExpo"=>NativeCameraEvent.EaseInOutExpo,
        "InCirc"=>NativeCameraEvent.EaseInCirc,"OutCirc"=>NativeCameraEvent.EaseOutCirc,"InOutCirc"=>NativeCameraEvent.EaseInOutCirc,
        "InBack"=>NativeCameraEvent.EaseInBack,"OutBack"=>NativeCameraEvent.EaseOutBack,"InOutBack"=>NativeCameraEvent.EaseInOutBack,
        "InElastic"=>NativeCameraEvent.EaseInElastic,"OutElastic"=>NativeCameraEvent.EaseOutElastic,"InOutElastic"=>NativeCameraEvent.EaseInOutElastic,
        "InBounce"=>NativeCameraEvent.EaseInBounce,"OutBounce"=>NativeCameraEvent.EaseOutBounce,"InOutBounce"=>NativeCameraEvent.EaseInOutBounce,
        _=>NativeCameraEvent.EaseLinear
    };
}

internal static class FlashStateEvaluator
{
    internal static NativeFlashState Evaluate(IReadOnlyList<NativeFlashEvent> events,double chartTime)
    {
        uint bg=0,fg=0; float bga=0,fga=0;
        foreach(NativeFlashEvent e in events)
        {
            if(e.StartTime>chartTime) break;
            double t=e.DurationSeconds<=1e-9?1.0:Math.Clamp((chartTime-e.StartTime)/e.DurationSeconds,0.0,1.0);
            uint color=LerpColor(e.StartColor,e.EndColor,t);
            float opacity=(float)Math.Clamp(e.StartOpacity+(e.EndOpacity-e.StartOpacity)*t,0.0,1.0);
            if(e.Plane==NativeFlashEvent.PlaneForeground){fg=color;fga=opacity;} else {bg=color;bga=opacity;}
        }
        return new NativeFlashState(bg,bga,fg,fga);
    }

    private static uint LerpColor(uint a,uint b,double t)
    {
        static int C(uint x,int shift)=>(int)((x>>shift)&255u);
        int r=(int)Math.Round(C(a,16)+(C(b,16)-C(a,16))*t,MidpointRounding.AwayFromZero);
        int g=(int)Math.Round(C(a,8)+(C(b,8)-C(a,8))*t,MidpointRounding.AwayFromZero);
        int bl=(int)Math.Round(C(a,0)+(C(b,0)-C(a,0))*t,MidpointRounding.AwayFromZero);
        return ((uint)Math.Clamp(r,0,255)<<16)|((uint)Math.Clamp(g,0,255)<<8)|(uint)Math.Clamp(bl,0,255);
    }
}
