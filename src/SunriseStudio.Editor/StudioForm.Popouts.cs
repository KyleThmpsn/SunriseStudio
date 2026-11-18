using System.Numerics;
using ImGuiNET;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/** Editors in their own windows. Each window has its own ImGui context; the main tick draws them after itself. */
public sealed partial class StudioForm
{
    private readonly List<StudioPopout> popouts = [];

    private bool IsPoppedOut(StudioPanelKind kind) => popouts.Any(popout => popout.Panel == kind);

    /** Reopens the saved windows; an editor the layout already shows stays docked. */
    private void RestorePopouts()
    {
        if (options.Smoke) { settings.Popouts = []; return; }
        foreach (var state in settings.Popouts.ToArray())
            if (layout.Find(state.Panel) is null && !IsPoppedOut(state.Panel))
            {
                // A window that cannot open docks its editor; the editor must stay reachable.
                try { OpenPopout(state.Panel, new Rectangle(state.X, state.Y, state.Width, state.Height)); }
                catch (Exception error) when (error is not OutOfMemoryException)
                { workspace.Error = error.Message; layout.Show(state.Panel); }
            }
        RememberPopouts();
    }

    /** The area closes and its editor moves to a new window; the last area stays, empty. */
    private void PopOut(StudioLayoutNode node)
    {
        var kind = node.Panel;
        if (kind == StudioPanelKind.Empty || IsPoppedOut(kind)) return;
        if (node == layout.Root) node.Panel = StudioPanelKind.Empty;
        else layout.Close(node.Id);
        maximizedPanel = null;
        layoutDirty = true;
        OpenPopout(kind, null);
    }

    private void OpenPopout(StudioPanelKind kind, Rectangle? bounds)
    {
        var popout = new StudioPopout(kind, PanelTitle(kind), studioIcon, bounds);
        popout.Show(this);
        try { popout.Attach(renderer!, gui!, scale); }
        catch { popout.Dispose(); throw; }
        popouts.Add(popout);
        RememberPopouts();
    }

    /** Puts the editor back into the main window. */
    private void PopIn(StudioPopout popout)
    {
        var kind = popout.Panel;
        ClosePopout(kind);
        // The preview returns to its document's model pane.
        if (kind != StudioPanelKind.Preview && layout.Find(kind) is null) layout.Show(kind);
        layoutDirty = true;
    }

    /** Closes the editor's window, if it has one, without docking it. */
    private void ClosePopout(StudioPanelKind kind)
    {
        if (popouts.FirstOrDefault(popout => popout.Panel == kind) is not { } popout) return;
        if (ReferenceEquals(fullScreen, popout.Chrome)) fullScreen = null;
        popouts.Remove(popout);
        popout.Dispose();
        RememberPopouts();
    }

    /**
     * Shows a popped-out editor's window; a minimized one is restored.
     * @param activate Also gives the window keyboard focus.
     * @return False when the editor is docked.
     */
    private bool ShowPopout(StudioPanelKind kind, bool activate = false)
    {
        if (popouts.FirstOrDefault(popout => popout.Panel == kind) is not { } popout) return false;
        if (popout.WindowState == FormWindowState.Minimized) popout.WindowState = FormWindowState.Normal;
        if (activate) popout.Activate();
        return true;
    }

    // The preview shows the open document's model, and none is open at launch, so its window is not saved.
    private void RememberPopouts() => settings.Popouts = popouts.Where(popout => popout.Panel != StudioPanelKind.Preview).Select(popout =>
    {
        var bounds = popout.WindowState == FormWindowState.Normal ? popout.Bounds : popout.RestoreBounds;
        return new StudioPopoutState(popout.Panel, bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }).ToList();

    /** Draws each window with its own target and context, after the main window's frame. */
    private void DrawPopouts(float delta)
    {
        foreach (var popout in popouts.ToArray())
        {
            if (popout.PopInRequested) { PopIn(popout); continue; }
            var size = popout.ClientSize;
            if (!popout.IsAttached || popout.WindowState == FormWindowState.Minimized || size.Width <= 0 || size.Height <= 0)
                continue;
            renderer!.BeginWindowFrame(popout.Target, size.Width, size.Height, StudioTheme.Canvas);
            try
            {
                popout.Gui.NewFrame(delta);
                ShortcutInput.BeginFrame(popout.Gui.IsInputActive && EditorReady && !workspace.IsBusy);
                if (EditorReady && workspace.IsBusy)
                { ImGui.BeginDisabled(); DrawPopoutContent(popout, size); ImGui.EndDisabled(); }
                else if (EditorReady) { HandleShortcuts(); DrawPopoutContent(popout, size); HandleViewShortcuts(); }
                popout.Chrome.EndFrame();
                popout.Gui.Render();
            }
            finally { renderer.EndWindowFrame(popout.Target); }
        }
        // Code after the frame expects the main window's context.
        gui?.MakeCurrent();
    }

    // The window showing only the scene view over its whole screen, or null.
    private WindowChrome? fullScreen;

    /**
     * Shows the scene view alone over the whole screen of its window, or returns that window to its panels. The
     * Workspace's own window goes full screen when it has one, so a second screen can hold the map.
     */
    private void ToggleFullScreen()
    {
        if (fullScreen is { } shown) { fullScreen = null; shown.SetFullScreen(false); return; }
        if (workspace.World is null) return;
        fullScreen = popouts.FirstOrDefault(popout => popout.Panel == StudioPanelKind.Workspace)?.Chrome ?? chrome;
        fullScreen.SetFullScreen(true);
    }

    // The editor asked for at the left of the full-screen view, the browser or the object tree, and the one last
    // drawn there, which stays while it slides out; whether the inspector is asked for at the right; and how far each
    // side has slid in, from 0 hidden to 1 shown.
    private StudioPanelKind? leftDrawer;
    private StudioPanelKind leftShown = StudioPanelKind.Browser;
    private bool inspectorDrawer;
    private float leftSlide, inspectorSlide;
    // A drawer slides in or out in this many seconds, and takes at most this share of the window.
    private const float SlideSeconds = .15f, DrawerShare = .4f;

    /**
     * The scene view over the whole window. A map that closes ends full screen.
     * @param drawers Offers the browser, the object tree and the inspector as drawers, which the main window does.
     */
    private void DrawFullScreen(Vector2 size, bool drawers)
    {
        if (workspace.World is null || viewport is null) { BeginInvoke(ToggleFullScreen); return; }
        float left = 0, right = 0;
        if (drawers)
        {
            leftShown = leftDrawer ?? leftShown;
            float width = leftShown == StudioPanelKind.Browser ? settings.BrowserWidth : settings.SceneTreeWidth;
            left = DrawDrawer(leftShown, leftDrawer is not null, ref leftSlide, width, size, 0);
            right = DrawDrawer(StudioPanelKind.Inspector, inspectorDrawer, ref inspectorSlide, settings.InspectorWidth, size, 1);
            // A panel made later sits under the ones made before it, so the tabs are made before the view they sit on.
            (StudioPanelKind Kind, StudioIcon Icon)[] leftTabs =
                [(StudioPanelKind.Browser, StudioIcon.Folder), (StudioPanelKind.ObjectTree, StudioIcon.Tree)];
            for (int tab = 0; tab < leftTabs.Length; tab++)
            {
                var kind = leftTabs[tab].Kind;
                if (DrawDrawerTab(kind, leftTabs[tab].Icon, leftDrawer == kind, left, size, true, tab - 1))
                    leftDrawer = leftDrawer == kind ? null : kind;
            }
            if (DrawDrawerTab(StudioPanelKind.Inspector, StudioIcon.Edit, inspectorDrawer, size.X - right, size, false, 0))
                inspectorDrawer = !inspectorDrawer;
        }
        // The view reaches every edge. Its controls take the form the preview's have: a bar of their own when pinned,
        // else floating over the view while the pointer is there.
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        BeginPanel("full-screen", new(left, 0), new(size.X - left - right, size.Y), ImGuiWindowFlags.NoScrollbar);
        UpdateGizmo();
        bool onHover = viewport.ControlsOnHover;
        viewport.ControlsOnHover = true;
        viewport.DrawImGui(ImGui.GetContentRegionAvail());
        viewport.ControlsOnHover = onHover;
        ImGui.End();
        ImGui.PopStyleVar();
    }

    /**
     * One drawer, sliding toward shown or hidden: the editor at its side of the window, which the view makes room for.
     * An editor in its own window has no drawer.
     * @param side 0 for the left side, 1 for the right. @return The width the drawer takes this frame.
     */
    private float DrawDrawer(StudioPanelKind kind, bool shown, ref float slide, float width, Vector2 size, int side)
    {
        if (IsPoppedOut(kind)) return 0;
        float step = ImGui.GetIO().DeltaTime / SlideSeconds;
        slide = Math.Clamp(slide + (shown ? step : -step), 0, 1);
        if (slide <= 0) return 0;
        width = Math.Min(width * scale, size.X * DrawerShare);
        float taken = width * slide;
        DrawPanelContent(kind, 0, new(side == 0 ? taken - width : size.X - taken, 0), new(width, size.Y));
        return taken;
    }

    /**
     * A drawer's tab on its side's open edge, lit while its editor shows. Tabs on one side stack about the middle.
     * @param slot The tab's place in its stack: 0 starts at the middle, -1 sits above it. @return True when pressed.
     */
    private bool DrawDrawerTab(StudioPanelKind kind, StudioIcon icon, bool shown, float edge, Vector2 size, bool leftSide,
        int slot)
    {
        if (IsPoppedOut(kind)) return false;
        float tab = ImGui.GetFrameHeight();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.One);
        BeginPanel($"drawer-tab-{kind}", new(leftSide ? edge : edge - tab, size.Y / 2 + slot * tab), new(tab),
            ImGuiWindowFlags.NoScrollbar);
        bool pressed = ImGuiIcons.Button("slide", icon, (shown ? "Hide " : "Show ") + PanelTitle(kind), shown);
        ImGui.End();
        ImGui.PopStyleVar(2);
        return pressed;
    }

    /**
     * What full screen adds to the view's tool row: the bubble and layer menus, then the map and the bubble the eye is
     * in at the row's far end.
     */
    private void DrawFullScreenBar(Action<float> next, float right)
    {
        if (workspace.World is not { } world) return;
        next(BubbleMenuWidth);
        DrawBubbleMenu(world);
        next(LayerMenuWidth);
        DrawLayerMenu();
        string place = WorldName(world.MissionTag ?? world.Map.Tag);
        if (BubbleAt(world, viewport!.Camera.Position) is { Length: > 0 } bubble) place += Dot + bubble;
        float width = ImGui.CalcTextSize(place).X;
        next(width);
        var at = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(at with { X = MathF.Max(at.X, right - width) });
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(place);
    }

    // Each bubble's bounds, from the positions of its markers, for the scene they were read from.
    private SceneDocument? boundedScene;
    private readonly Dictionary<uint, (Vector3 Min, Vector3 Max)> bubbleBounds = [];

    /** The name of the bubble whose markers' bounds hold the eye, else of the nearest; null when no bubble has markers. */
    private string? BubbleAt(WorldDocument world, Vector3 eye)
    {
        if (!ReferenceEquals(boundedScene, world.Scene))
        {
            boundedScene = world.Scene;
            bubbleBounds.Clear();
            foreach (var marker in world.Scene.Markers)
            {
                uint bubble = world.CanonicalBubble(marker.BubbleHash);
                bubbleBounds[bubble] = bubbleBounds.TryGetValue(bubble, out var box)
                    ? (Vector3.Min(box.Min, marker.Position), Vector3.Max(box.Max, marker.Position))
                    : (marker.Position, marker.Position);
            }
        }
        if (bubbleBounds.Count == 0) return null;
        // The distance to a box is zero inside it.
        uint nearest = bubbleBounds.MinBy(pair => Vector3.Distance(eye, Vector3.Clamp(eye, pair.Value.Min, pair.Value.Max))).Key;
        string name = world.Scene.Bubbles.FirstOrDefault(bubble => world.CanonicalBubble(bubble.TagHash) == nearest)?.Name ?? "";
        return BubbleTitle(nearest, name);
    }

    private void DrawPopoutContent(StudioPopout popout, Size client)
    {
        var size = new Vector2(client.Width, client.Height);
        if (ReferenceEquals(fullScreen, popout.Chrome)) { DrawFullScreen(size, drawers: false); return; }
        float header = ImGui.GetFrameHeight() + 8 * scale;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(4 * scale));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.One);
        BeginPanel("popout-header", Vector2.Zero, new(size.X, header), ImGuiWindowFlags.NoScrollbar);
        // The header is the caption wherever no control sits; its close button docks the editor.
        popout.Chrome.Scale = scale;
        popout.Chrome.CaptionHeight = header;
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(PanelTitle(popout.Panel));
        float titleEnd = ImGui.GetItemRectMax().X - ImGui.GetWindowPos().X + ImGui.GetStyle().ItemSpacing.X;
        // Closing Preview returns it to the document's model pane.
        if (popout.Panel != StudioPanelKind.Preview)
        {
            string label = "Pop in";
            float width = ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2;
            float buttons = 3 * 46 * scale;
            ImGui.SameLine(MathF.Max(titleEnd, size.X - buttons - width - 8 * scale));
            if (ImGui.Button(label)) popout.RequestPopIn();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Put this editor back into the main window");
        }
        popout.Chrome.DrawButtons(header, popout.RequestPopIn);
        ImGui.End();
        ImGui.PopStyleVar(2);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.One);
        DrawPanelContent(popout.Panel, 0, new(0, header), new(size.X, MathF.Max(1, size.Y - header)));
        ImGui.PopStyleVar();
    }
}
