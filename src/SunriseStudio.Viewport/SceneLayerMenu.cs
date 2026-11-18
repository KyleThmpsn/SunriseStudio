using ImGuiNET;
using SunriseStudio.Scene;
using SunriseStudio.Scene.Collision;

namespace SunriseStudio.Viewport;

/** Box and content switches per scene layer, shared by the world scene and the asset view. */
public static class SceneLayerMenu
{
    // Box is the placement marker; content is what the placement draws. Lights draw no content yet.
    private static readonly (SceneLayer Layer, bool Box, bool Content)[] LayerKinds =
    [
        (SceneLayer.Geometry, false, true), (SceneLayer.Terrain, false, true), (SceneLayer.Objects, true, true),
        (SceneLayer.Skybox, false, true), (SceneLayer.Distant, false, true), (SceneLayer.Volumes, true, true),
        (SceneLayer.Effects, true, true), (SceneLayer.Spawns, true, false), (SceneLayer.Squads, true, false),
        (SceneLayer.Lights, true, false), (SceneLayer.Transitions, true, false), (SceneLayer.Collision, true, true),
        (SceneLayer.NavMesh, true, true)
    ];

    /**
     * Lists every layer, or only the layers in present when it is given.
     * @param collision The viewport that draws collision; without it the Collision row is left out.
     */
    public static void Draw(SceneVisibility visibility, IReadOnlySet<SceneLayer>? present = null, AssetViewport? collision = null)
    {
        // Close rows on a striped table, each switch centred under its heading.
        CompactFrames.Push();
        if (ImGui.BeginTable("layer-choices", 3, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Kind");
            ImGui.TableSetupColumn("Box");
            ImGui.TableSetupColumn("Content");
            ImGui.TableHeadersRow();
            foreach (var (layer, box, content) in LayerKinds)
            {
                if (present is not null && !present.Contains(layer)) continue;
                if (layer == SceneLayer.Collision && collision is null) continue;
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(layer.ToString());
                if (layer == SceneLayer.Collision)
                {
                    NextSwitch();
                    bool boxes = visibility.ShowCollisionBoxes;
                    if (StudioControls.Checkbox("##box-Collision", ref boxes)) visibility.ShowCollisionBoxes = boxes;
                    NextSwitch();
                    bool shown = visibility.ShowCollisionContent;
                    if (StudioControls.Checkbox("##content-Collision", ref shown)) visibility.ShowCollisionContent = shown;
                    continue;
                }
                NextSwitch(box); if (box) Choice(visibility, layer, $"##box-{layer}", visibility.HiddenOverlayLayers);
                NextSwitch(content); if (content) Choice(visibility, layer, $"##content-{layer}", visibility.HiddenGeometryLayers);
            }
            ImGui.EndTable();
        }
        ImGui.Separator();
        bool inactiveParts = visibility.ShowInactiveParts;
        if (StudioControls.Checkbox("Inactive parts", ref inactiveParts)) visibility.ShowInactiveParts = inactiveParts;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Show parts that start hidden or have no material");
        if (collision is not null && (present is null || present.Contains(SceneLayer.Collision))) DrawCollisionOptions(collision);
        if (collision is { Scene.NavMeshes: [var navMesh, ..] } && (present is null || present.Contains(SceneLayer.NavMesh)))
        {
            // Each layer is baked for the agent radius its firing point layer stores.
            string radii = string.Concat(navMesh.FiringPoints.Layers.Select(layer => $"{layer.Agent[0]:0.##} m\0"));
            int agent = collision.NavMeshAgent;
            ImGui.Separator();
            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 6);
            if (StudioControls.Combo("Nav mesh radius", ref agent, radii)) collision.NavMeshAgent = agent;
        }
        CompactFrames.Pop();
    }

    /**
     * Moves to the next column and centres the checkbox that follows. A cell with no switch leaves the cursor alone,
     * since ImGui refuses a moved cursor that no item follows.
     */
    private static void NextSwitch(bool follows = true)
    {
        ImGui.TableNextColumn();
        if (follows)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0, (ImGui.GetColumnWidth() - StudioControls.BoxWidth) / 2));
    }

    /** Edge and solid switches, and what the collision layer holds or why it is empty. */
    private static void DrawCollisionOptions(AssetViewport viewport)
    {
        ImGui.Separator();
        bool edges = viewport.CollisionEdges, solid = viewport.CollisionSolid;
        if (StudioControls.Checkbox("Collision edges", ref edges)) viewport.CollisionEdges = edges;
        if (StudioControls.Checkbox("Solid collision", ref solid)) viewport.CollisionSolid = solid;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Opaque faces that hide what is behind them; triggers stay translucent");
        bool inactive = viewport.ShowInactiveCollision;
        if (StudioControls.Checkbox("Inactive bodies", ref inactive)) viewport.ShowInactiveCollision = inactive;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Show bodies that start disabled");
        foreach (var role in Enum.GetValues<CollisionRole>())
        {
            bool shown = !viewport.HiddenCollisionRoles.Contains(role);
            if (role != default) ImGui.SameLine();
            if (StudioControls.Checkbox($"{role}##collision-role", ref shown))
            { if (shown) viewport.HiddenCollisionRoles.Remove(role); else viewport.HiddenCollisionRoles.Add(role); }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(role switch
                {
                    CollisionRole.World => "Blocks movement",
                    CollisionRole.Hitbox => "Takes weapon hits",
                    CollisionRole.Trigger => "Detects entry",
                    _ => "Other shapes"
                });
        }
        if (viewport.CollisionStatus.Length > 0) ImGui.TextDisabled(viewport.CollisionStatus);
        if (viewport.Collision is not { } scene) return;
        ImGui.TextDisabled($"{scene.Placements.Count:N0} shapes placed / {scene.Pieces.Count:N0} distinct");
        ImGui.TextDisabled($"{viewport.VisibleCollisionCount:N0} drawn / {viewport.VisibleCollisionTriangles:N0} triangles");
        if (scene.Undecoded.Count > 0 && ImGui.IsItemHovered())
            ImGui.SetTooltip("No triangles: " + string.Join(", ", scene.Undecoded.Select(item => $"{item.Key} {item.Value}")));
    }

    /** The layers a scene holds, from its meshes and markers. */
    public static IReadOnlySet<SceneLayer> Present(SceneDocument scene)
    {
        var layers = new HashSet<SceneLayer>();
        foreach (var instance in scene.Instances) layers.Add(instance.Layer);
        foreach (var marker in scene.Markers) layers.Add(marker.Layer);
        if (scene.NavMeshes.Count > 0) layers.Add(SceneLayer.NavMesh);
        return layers;
    }

    private static void Choice(SceneVisibility visibility, SceneLayer layer, string label, HashSet<SceneLayer> hidden)
    {
        ImGui.PushID(label);
        bool visible = !hidden.Contains(layer) && !visibility.HiddenLayers.Contains(layer);
        if (StudioControls.Checkbox(label, ref visible))
        {
            visibility.HiddenLayers.Remove(layer);
            if (visible) hidden.Remove(layer); else hidden.Add(layer);
        }
        ImGui.PopID();
    }
}
