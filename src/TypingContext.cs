using System;

namespace Switchcraft;

/// <summary>Only language labels are retained, never previous text.</summary>
internal sealed class TypingContext
{
    private string language = "", layout = "", expectedLayout = "";
    private int evidence, gap;
    private long lastObservation;
    public string Language => evidence >= 3 && Environment.TickCount64 - lastObservation < 15000 ? language : "";
    public void Before(string token)
    {
        if (token.IndexOfAny(new[] { '«', '»', '“', '”', '"', '(', ')', '[', ']' }) >= 0) Reset();
    }
    public void After(string token)
    {
        if (token.IndexOfAny(new[] { '.', '!', '?', ':', ';', '…', '»', '”', '"' }) >= 0) Reset();
    }
    public void Observe(string next)
    {
        if (Environment.TickCount64 - lastObservation > 15000) { language = ""; evidence = 0; }
        lastObservation = Environment.TickCount64;
        if (next is not ("uk" or "ru")) { if (++gap > 6) { language = ""; evidence = 0; } return; }
        gap = 0; evidence = next == language ? Math.Min(evidence + 1, 3) : 1; language = next;
    }
    public bool ObserveLayout(string next)
    {
        bool manual = false;
        if (layout.Length > 0 && layout != next)
        {
            if (next != expectedLayout) { manual = true; Reset(); }
            expectedLayout = "";
        }
        layout = next;
        return manual;
    }
    public void ExpectLayout(string next) => expectedLayout = next;
    public void Reset() { language = layout = expectedLayout = ""; evidence = gap = 0; }
}
