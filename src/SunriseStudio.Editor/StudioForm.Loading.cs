using System.Numerics;
using ImGuiNET;

namespace SunriseStudio.Editor;

public sealed partial class StudioForm
{
    private static readonly Vector4 LogoColor = new(246 / 255f, 226 / 255f, 122 / 255f, 1);
    // The name's height in the game's face on the loading screen and in the title bar, before scaling. Its capitals'
    // centre and its baseline are these shares of that height below the line's top.
    private const float NameHeight = 30, BrandHeight = 20, NameCapCentre = .54f, NameBaseline = .86f;
    private bool loadingCaptured;

    /** Resource upload callbacks may present loading frames only outside an active ImGui frame. */
    private void ReportPreparation(string stage, int completed, int total)
    {
        presentationProgress = new(stage, completed, total);
        EditorReady = false;
        if (closed || renderer is null || gui is null || guiFrameActive || nativeDialog
            || clock.Elapsed.TotalSeconds - lastLoadingFrame < 1d / 30) return;
        lastLoadingFrame = clock.Elapsed.TotalSeconds;
        renderer.Resize(ClientSize.Width, ClientSize.Height);
        guiFrameActive = true;
        try
        {
            gui.NewFrame((float)Math.Clamp(lastLoadingFrame - lastFrame, .001, .1));
            lastFrame = lastLoadingFrame;
            renderer.BeginFrame(StudioTheme.Canvas);
            DrawLoading();
            gui.Render();
            CaptureLoadingFrame();
            renderer.EndFrame(false);
        }
        finally { guiFrameActive = false; }
    }

    /** Capture a counted loading stage without changing the worker's completion barrier. */
    private void CaptureLoadingFrame()
    {
        if (loadingCaptured || EditorReady || options.CaptureLoading is not { } path
            || (presentationProgress ?? workspace.LoadingProgress) is not { Fraction: >= .15f and <= .9f }) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        renderer!.CapturePng(path);
        loadingCaptured = true;
    }

    /**
     * Editor controls stay absent until the scene and its graphics resources are ready. One square box holds the
     * logo and the name in white capitals with the share done opposite, the stage and the count in small muted
     * spaced capitals under a ruled divider, and a bottom edge that fills as the work is done. Ticks mark the box's
     * corners and thin rings orbit the silhouette, as the game frames its own panels.
     */
    private void DrawLoading()
    {
        // The window is the darker canvas, so the box stands off it.
        ImGui.PushStyleColor(ImGuiCol.WindowBg, StudioTheme.Canvas);
        BeginPanel("Loading", Vector2.Zero, new(ClientSize.Width, ClientSize.Height), ImGuiWindowFlags.NoScrollbar);
        ImGui.PopStyleColor();
        // Without a menu bar the top strip is the caption, so the window can move and close while loading.
        chrome.Scale = scale;
        chrome.CaptionHeight = 32 * scale;
        chrome.DrawButtons(32 * scale);
        // Text is set in the game's faces once an installation supplies them, and in the editor's until then.
        var faces = gui?.GameFonts;
        var nameFace = faces?.Figure ?? gui?.TitleFont ?? ImGui.GetFont();
        var textFace = faces?.Text ?? ImGui.GetFont();
        float nameHeight = faces is null ? nameFace.FontSize : NameHeight * scale, line = textFace.FontSize;
        float logo = 44 * scale, gap = 14 * scale, rule = 4 * scale, strip = 152 * scale, pad = 28 * scale;
        float width = Math.Min(640 * scale, ClientSize.Width - 64 * scale);
        float top = MathF.Round((ClientSize.Height - strip) * .45f), left = (ClientSize.Width - width) / 2;
        bool working = !setup && workspace.Error.Length == 0;
        var progress = presentationProgress ?? (workspace.IsBusy ? workspace.LoadingProgress
            : new StudioLoadProgress(viewport?.Animation?.IsLoading == true || assetViewport?.Animation?.IsLoading == true
                ? "Loading animation" : browserPreparing ? "Preparing asset browser" : "Preparing editor"));
        var draw = ImGui.GetWindowDrawList();
        uint muted = ImGui.GetColorU32(ImGuiCol.TextDisabled), white = ImGui.GetColorU32(ImGuiCol.Text);
        uint hairline = ImGui.GetColorU32(new Vector4(1, 1, 1, .16f));

        // The box lightens a little toward its top, with a large silhouette of the logo turning slowly behind its
        // content, centred on the right edge so half of it shows.
        var boxStart = new Vector2(left, top);
        var boxEnd = new Vector2(left + width, top + strip);
        uint lower = ImGui.GetColorU32(StudioTheme.Panel);
        uint upper = ImGui.GetColorU32(Vector4.Lerp(StudioTheme.Panel, Vector4.One, .05f));
        draw.AddRectFilledMultiColor(boxStart, boxEnd, upper, upper, lower, lower);
        draw.PushClipRect(boxStart, boxEnd, true);
        float halo = strip * 2.2f;
        DrawSunSpinner(new(boxEnd.X - halo * .5f, top + (strip - halo) / 2), halo, working,
            ImGui.GetColorU32(new Vector4(1, 1, 1, .05f)), -60);
        // Rings share the silhouette's centre, drawn as the game draws its dials. The inner ring passes left of the
        // share done and carries graduated ticks that turn slowly. A dashed ring turns back between the two, and the
        // outer ring carries two broad bands and a diamond at each quarter.
        var hub = new Vector2(boxEnd.X, top + strip / 2);
        float orbit = strip * .62f, outer = strip * .9f, between = (orbit + outer) / 2;
        // An hour's wrap keeps the angle small without a visible jump on the rings that turn at other rates.
        float turn = working ? (float)(clock.Elapsed.TotalSeconds % 3600 * .2) : 0;
        uint ring = ImGui.GetColorU32(new Vector4(1, 1, 1, .08f)), minor = ImGui.GetColorU32(new Vector4(1, 1, 1, .16f));
        uint major = ImGui.GetColorU32(new Vector4(1, 1, 1, .34f));
        Vector2 On(float radius, float angle) => hub + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
        void Arc(float radius, float from, float span, float weight, uint color)
        {
            draw.PathArcTo(hub, radius, from, from + span, 32);
            draw.PathStroke(color, ImDrawFlags.None, weight * scale);
        }
        draw.AddCircle(hub, orbit, ring, 96);
        draw.AddCircle(hub, outer, ring, 96);
        // Every sixth tick is longer and brighter.
        const int Ticks = 72, Long = 6, Dashes = 30, Quarters = 4;
        for (int mark = 0; mark < Ticks; mark++)
        {
            float angle = turn + mark * MathF.Tau / Ticks;
            bool lead = mark % Long == 0;
            draw.AddLine(On(orbit, angle), On(orbit + (lead ? 8 : 4) * scale, angle), lead ? major : minor);
        }
        float slot = MathF.Tau / Dashes;
        for (int dash = 0; dash < Dashes; dash++) Arc(between, dash * slot - turn * .6f, slot * .55f, 1, minor);
        for (int quarter = 0; quarter < Quarters; quarter++)
        {
            float angle = turn * .5f + quarter * MathF.Tau / Quarters;
            if (quarter % 2 == 0) Arc(outer, angle + .35f, .9f, 5, ring);
            var point = On(outer, angle);
            float half = 4 * scale;
            draw.AddQuadFilled(point - new Vector2(half, 0), point - new Vector2(0, half), point + new Vector2(half, 0),
                point + new Vector2(0, half), major);
        }
        draw.PopClipRect();
        draw.AddRect(boxStart, boxEnd, hairline);
        // Slightly brighter ticks mark the corners.
        float tick = 10 * scale;
        uint corner = ImGui.GetColorU32(new Vector4(1, 1, 1, .3f));
        // The outline's right side is drawn on the pixel inside the box's end.
        foreach (float x in (ReadOnlySpan<float>)[left, boxEnd.X - 1])
        {
            float inward = x == left ? tick : -tick;
            draw.AddLine(new(x, top), new(x + inward, top), corner);
            draw.AddLine(new(x, top), new(x, top + tick), corner);
            draw.AddLine(new(x, boxEnd.Y - rule), new(x, boxEnd.Y - rule - tick), corner);
        }
        // The bottom edge is the progress line, across the whole box.
        DrawEdgeProgress(new(left, top + strip - rule), new(width, rule), working ? progress.Fraction : 0, hairline);

        // Content hangs on two inset edges: the logo and the name over the stage at the left, the share done over the
        // count at the right, with a ruled divider between the two rows.
        float inner = left + pad, right = boxEnd.X - pad, row = 11 * scale;
        float block = logo + row * 2 + line, blockTop = top + (strip - rule - block) / 2;
        float Width(ImFontPtr face, float height, string text) => face.CalcTextSizeA(height, float.MaxValue, 0, text).X;
        // Small capitals are spaced apart, a glyph at a time.
        float spacing = line * .14f;
        float Spaced(string text)
        {
            float total = 0;
            foreach (char letter in text) total += Width(textFace, line, letter.ToString()) + spacing;
            return MathF.Max(0, total - spacing);
        }
        void DrawSpaced(Vector2 at, string text)
        {
            foreach (char letter in text)
            {
                string glyph = letter.ToString();
                draw.AddText(textFace, line, at, muted, glyph);
                at.X += Width(textFace, line, glyph) + spacing;
            }
        }
        DrawSunSpinner(new(inner, blockTop), logo, working);
        // The name's capitals centre on the logo; a face's line starts above them, so the line is raised by its slack.
        float nameTop = blockTop + logo / 2 - nameHeight * NameCapCentre, nameLeft = inner + logo + gap;
        const string Name = "SUNRISE STUDIO";
        draw.AddText(nameFace, nameHeight, new(nameLeft, nameTop), white, Name);
        // The version follows the name on its baseline, after a double stroke.
        DrawSpaced(new(nameLeft + Width(nameFace, nameHeight, Name) + gap * .75f, nameTop + (nameHeight - line) * NameBaseline),
            "// v" + StudioVersion.Text);
        string share = working && progress.Fraction is { } done ? $"{(int)(done * 100)}%" : "";
        draw.AddText(nameFace, nameHeight, new(right - Width(nameFace, nameHeight, share), nameTop), white, share);
        float ruleTop = MathF.Round(blockTop + logo + row);
        draw.AddLine(new(inner, ruleTop), new(right, ruleTop), hairline);
        string stage = setup ? "Sunrise location" : workspace.Error.Length != 0 ? "Unable to load" : progress.Stage;
        string count = !working ? "" : progress.Total > 0 ? $"{progress.Completed:N0} / {progress.Total:N0}"
            : progress.Completed > 0 ? $"{progress.Completed:N0}" : "";
        float stageTop = blockTop + logo + row * 2;
        DrawSpaced(new(inner, stageTop), stage.ToUpperInvariant());
        DrawSpaced(new(right - Spaced(count), stageTop), count);
        if (workspace.Error.Length != 0)
        {
            // The reason and the way out sit under the box.
            ImGui.SetCursorScreenPos(new(left, top + strip + gap));
            ImGui.BeginGroup();
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
            ImGui.TextWrapped(workspace.Error);
            ImGui.PopTextWrapPos();
            if (!workspace.IsBusy && ImGui.Button("Choose Sunrise location")) setup = true;
            ImGui.EndGroup();
        }
        ImGui.End();
        if (setup || settingsDialog) DrawDialogs();
    }

    /** An unknown total draws a moving segment instead of a fill. */
    private void DrawProgressBar(StudioLoadProgress progress, float width, float height)
    {
        var bar = ImGui.GetCursorScreenPos();
        if (height < ImGui.GetTextLineHeight()) bar.Y += (ImGui.GetTextLineHeight() - height) / 2;
        var size = new Vector2(width, height);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(bar, bar + size, ImGui.GetColorU32(ImGuiCol.FrameBg), 3 * scale);
        if (progress.Fraction is { } fraction)
            draw.AddRectFilled(bar, bar + new Vector2(size.X * fraction, size.Y), ImGui.GetColorU32(LogoColor), 3 * scale);
        else
        {
            float offset = (float)(clock.Elapsed.TotalSeconds % 1.6 / 1.6) * (width + width * .22f) - width * .22f;
            draw.PushClipRect(bar, bar + size, true);
            draw.AddRectFilled(bar + new Vector2(offset, 0), bar + new Vector2(offset + width * .22f, size.Y),
                ImGui.GetColorU32(LogoColor), 3 * scale);
            draw.PopClipRect();
        }
        ImGui.Dummy(new Vector2(width, MathF.Max(height, ImGui.GetTextLineHeight())));
    }

    /**
     * A line that fills from the left in the text's colour.
     * @param fraction The share done, or null to sweep a segment for an unknown total. @param track The unfilled colour.
     */
    private void DrawEdgeProgress(Vector2 at, Vector2 size, float? fraction, uint track)
    {
        var draw = ImGui.GetWindowDrawList();
        uint fill = ImGui.GetColorU32(ImGuiCol.Text);
        draw.AddRectFilled(at, at + size, track);
        if (fraction is { } done)
        {
            float reach = size.X * Math.Clamp(done, 0, 1);
            draw.AddRectFilled(at, at + size with { X = reach }, fill);
            // A taller head marks where the fill has reached.
            if (done is > 0 and < 1)
                draw.AddRectFilled(new(at.X + reach - scale, at.Y - size.Y), new(at.X + reach + scale, at.Y + size.Y), fill);
            return;
        }
        const float Share = .22f, Period = 1.6f;
        float offset = (float)(clock.Elapsed.TotalSeconds % Period / Period) * size.X * (1 + Share) - size.X * Share;
        draw.PushClipRect(at, at + size, true);
        draw.AddRectFilled(at with { X = at.X + offset }, new(at.X + offset + size.X * Share, at.Y + size.Y), fill);
        draw.PopClipRect();
    }

    /**
     * Rotation transforms only the displayed quad; the supplied artwork remains unchanged.
     * @param period Seconds a turn takes; a negative period turns the other way.
     */
    private void DrawSunSpinner(Vector2 origin, float size, bool spin, uint tint = uint.MaxValue, double period = 6)
    {
        if (loadingLogo is null) return;
        var center = origin + new Vector2(size / 2);
        float angle = spin ? (float)(clock.Elapsed.TotalSeconds % period / period * Math.Tau) : 0;
        float sine = MathF.Sin(angle), cosine = MathF.Cos(angle);
        Vector2 Corner(float x, float y) => center + new Vector2(x * cosine - y * sine, x * sine + y * cosine) * (size / 2);
        ImGui.GetWindowDrawList().AddImageQuad(loadingLogo.ImGuiId,
            Corner(-1, -1), Corner(1, -1), Corner(1, 1), Corner(-1, 1),
            Vector2.Zero, Vector2.UnitX, Vector2.One, Vector2.UnitY, tint);
    }
}
