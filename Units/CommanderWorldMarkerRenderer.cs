using System.Collections.Generic;
using UnityEngine;

namespace NuclearOptionCommander;

internal sealed class CommanderWorldMarkerRenderer
{
    private readonly CommanderSelectionService selectionService;
    private readonly CommanderMoveService moveService;
    private readonly CommanderSpawnService spawnService;
    private readonly CommanderSupplyHeliService supplyHeliService;
    private readonly CommanderSamSiteAnalyzerService samSiteAnalyzerService;
    private readonly CommanderSamSiteService samSiteService;
    private readonly List<GlobalPosition> deliveryTargets = new();
    private readonly List<GlobalPosition> supplyRoute = new();
    private readonly List<CommanderSamSiteAnalyzerService.SiteLayoutMarker> samSiteLayout = new();
    private readonly List<CommanderSamSiteAnalyzerService.SiteCandidate> samSiteProposals = new();

    internal CommanderWorldMarkerRenderer(
        CommanderSelectionService selectionService,
        CommanderMoveService moveService,
        CommanderSpawnService spawnService,
        CommanderSupplyHeliService supplyHeliService,
        CommanderSamSiteAnalyzerService samSiteAnalyzerService,
        CommanderSamSiteService samSiteService)
    {
        this.selectionService = selectionService;
        this.moveService = moveService;
        this.spawnService = spawnService;
        this.supplyHeliService = supplyHeliService;
        this.samSiteAnalyzerService = samSiteAnalyzerService;
        this.samSiteService = samSiteService;
    }

    internal void Draw(bool supplyWindowVisible)
    {
        if (Event.current.type != EventType.Repaint)
        {
            return;
        }

        Camera? camera = SceneSingleton<CameraStateManager>.i?.mainCamera;
        if (camera == null)
        {
            return;
        }

        for (int i = 0; i < selectionService.SelectedUnits.Count; i++)
        {
            Unit unit = selectionService.SelectedUnits[i];
            if (moveService.TryGetPlayerDestination(unit, out GlobalPosition destination))
            {
                DrawLineToDestination(camera, unit.transform.position, destination, new Color(0.2f, 0.85f, 0.82f, 0.4f), CommanderSettings.LineThickness);
                DrawMarker(camera, destination, "MOVE", new Color(0.2f, 0.85f, 0.82f, 0.9f));
            }
        }

        if (spawnService.SelectedDepot != null && spawnService.TryGetSelectedRallyPoint(out GlobalPosition rallyPoint))
        {
            DrawMarker(camera, rallyPoint, "RALLY", new Color(0.95f, 0.78f, 0.22f, 0.9f));
        }

        if (supplyHeliService.AwaitingTargetSelection)
        {
            DrawCursorMarker("LZ", new Color(0.35f, 0.9f, 0.42f, 0.95f));
        }

        samSiteAnalyzerService.CopyProposalSites(samSiteProposals);
        for (int i = 0; i < samSiteProposals.Count; i++)
        {
            DrawLargeMarker(
                camera,
                samSiteProposals[i].Position,
                $"SAM SITE {i + 1}",
                new Color(0.1f, 0.82f, 1f, 0.95f));
        }

        samSiteAnalyzerService.CopyVisibleActiveLayout(samSiteLayout);
        for (int i = 0; i < samSiteLayout.Count; i++)
        {
            CommanderSamSiteAnalyzerService.SiteLayoutMarker marker = samSiteLayout[i];
            if (marker.Role == CommanderSamSiteAnalyzerService.SiteUnitRole.ControlTower)
            {
                continue;
            }
            DrawMarker(camera, marker.Position, GetSamLabel(marker.Role), GetSamColor(marker.Role));
        }

        samSiteService.CopyVisibleSupplyRoute(supplyRoute);
        for (int i = 0; i < supplyRoute.Count; i++)
        {
            string label = i == 0
                ? "AIRBASE"
                : i == supplyRoute.Count - 1 ? "SAM SITE" : $"ROUTE {i}";
            DrawMarker(camera, supplyRoute[i], label, new Color(0.2f, 0.78f, 1f, 0.92f));
        }
        if (!supplyWindowVisible)
        {
            return;
        }

        supplyHeliService.CopyActiveDeliveryTargets(deliveryTargets);
        for (int i = 0; i < deliveryTargets.Count; i++)
        {
            DrawMarker(camera, deliveryTargets[i], "LZ", new Color(0.35f, 0.9f, 0.42f, 0.9f));
        }
    }

    private static string GetSamLabel(CommanderSamSiteAnalyzerService.SiteUnitRole role)
    {
        return role switch
        {
            CommanderSamSiteAnalyzerService.SiteUnitRole.Radar => "RADAR",
            CommanderSamSiteAnalyzerService.SiteUnitRole.Platform => "PLATFORM",
            CommanderSamSiteAnalyzerService.SiteUnitRole.ControlTower => "SITE CORE",
            CommanderSamSiteAnalyzerService.SiteUnitRole.Gun23mm => "23MM",
            CommanderSamSiteAnalyzerService.SiteUnitRole.Irm => "IRM",
            CommanderSamSiteAnalyzerService.SiteUnitRole.StratoLauncher => "STRATOLANCE",
            CommanderSamSiteAnalyzerService.SiteUnitRole.Ammo => "AMMO",
            CommanderSamSiteAnalyzerService.SiteUnitRole.FireControl => "FIRE CTRL",
            _ => "SITE"
        };
    }

    private static Color GetSamColor(CommanderSamSiteAnalyzerService.SiteUnitRole role)
    {
        return role switch
        {
            CommanderSamSiteAnalyzerService.SiteUnitRole.Radar => new Color(1f, 0.78f, 0.12f, 0.95f),
            CommanderSamSiteAnalyzerService.SiteUnitRole.Platform => new Color(0.72f, 0.7f, 1f, 0.95f),
            CommanderSamSiteAnalyzerService.SiteUnitRole.ControlTower => new Color(0.6f, 0.78f, 1f, 0.95f),
            CommanderSamSiteAnalyzerService.SiteUnitRole.Gun23mm => new Color(1f, 0.46f, 0.12f, 0.95f),
            CommanderSamSiteAnalyzerService.SiteUnitRole.Irm => new Color(1f, 0.2f, 0.08f, 0.95f),
            CommanderSamSiteAnalyzerService.SiteUnitRole.StratoLauncher => new Color(0.95f, 0.12f, 0.12f, 0.95f),
            CommanderSamSiteAnalyzerService.SiteUnitRole.Ammo => new Color(0.35f, 1f, 0.35f, 0.95f),
            CommanderSamSiteAnalyzerService.SiteUnitRole.FireControl => new Color(0.1f, 0.9f, 1f, 0.95f),
            _ => Color.white
        };
    }

    private static void DrawCursorMarker(string label, Color color)
    {
        Vector2 guiPoint = CommanderUiScale.ScreenToGui(Input.mousePosition);
        GUIStyle style = CommanderUiTheme.Panel;
        float width = GetMarkerWidth(label, style, 60f);
        float height = GetMarkerHeight(label, style, width, 26f);
        Rect marker = new(guiPoint.x + 14f, guiPoint.y + 14f, width, height);
        Color previous = GUI.color;
        GUI.color = color;
        GUI.Box(marker, label, style);
        CommanderUiTheme.DrawFrame(marker, 1f);
        GUI.color = previous;
    }

    private static void DrawMarker(Camera camera, GlobalPosition position, string label, Color color)
    {
        Vector3 world = position.ToLocalPosition();
        Vector3 screen = camera.WorldToScreenPoint(world);
        if (screen.z <= 0f || screen.x < 0f || screen.x > Screen.width || screen.y < 0f || screen.y > Screen.height)
        {
            return;
        }

        Vector2 guiPoint = CommanderUiScale.ScreenToGui(screen);
        GUIStyle style = CommanderUiTheme.Panel;
        float width = GetMarkerWidth(label, style, 60f);
        float height = GetMarkerHeight(label, style, width, 26f);
        Rect marker = new(guiPoint.x - width * 0.5f, guiPoint.y - height * 0.5f, width, height);
        Color previous = GUI.color;
        GUI.color = color;
        GUI.Box(marker, label, style);
        CommanderUiTheme.DrawFrame(marker, 1f);
        GUI.color = previous;
    }
        
    private static void DrawLargeMarker(Camera camera, GlobalPosition position, string label, Color color)
    {
        Vector3 screen = camera.WorldToScreenPoint(position.ToLocalPosition());
        if (screen.z <= 0f || screen.x < 0f || screen.x > Screen.width || screen.y < 0f || screen.y > Screen.height)
        {
            return;
        }

        Vector2 guiPoint = CommanderUiScale.ScreenToGui(screen);
        GUIStyle style = CommanderUiTheme.PrimaryButton;
        float width = GetMarkerWidth(label, style, 116f);
        float height = GetMarkerHeight(label, style, width, 38f);
        Rect marker = new(guiPoint.x - width * 0.5f, guiPoint.y - height * 0.5f, width, height);
        Color previous = GUI.color;
        GUI.color = color;
        GUI.Box(marker, label, style);
        CommanderUiTheme.DrawFrame(marker, 2f);
        GUI.color = previous;
    }

    private static float GetMarkerWidth(string label, GUIStyle style, float minimumWidth)
    {
        return Mathf.Max(minimumWidth, style.CalcSize(new GUIContent(label)).x + 18f);
    }

    private static float GetMarkerHeight(string label, GUIStyle style, float width, float minimumHeight)
    {
        float contentWidth = Mathf.Max(1f, width - style.padding.horizontal);
        float calculatedHeight = style.CalcHeight(new GUIContent(label), contentWidth);
        return Mathf.Max(minimumHeight, calculatedHeight + 4f);
    }

    private static void DrawLineToDestination(Camera camera, Vector3 unitWorldPos, GlobalPosition destination, Color lineColor, float lineThickness)
    {
        Vector3 destWorld = destination.ToLocalPosition();
        Vector3 unitScreen = camera.WorldToScreenPoint(unitWorldPos);
        Vector3 destScreen = camera.WorldToScreenPoint(destWorld);

        if (unitScreen.z <= 0f && destScreen.z <= 0f)
        {
            return;
        }

        // Convert screen coords to GUI space (Y-flipped, scaled)
        Vector2 unitGui = CommanderUiScale.ScreenToGui(unitScreen);
        Vector2 destGui = CommanderUiScale.ScreenToGui(destScreen);

        Color previous = GUI.color;
        GUI.color = lineColor;
        DrawGuiLine(unitGui, destGui, lineThickness);
        GUI.color = previous;
    }

    private static void DrawGuiLine(Vector2 from, Vector2 to, float thickness)
    {
        CommanderUiTheme.Ensure();
        Vector2 delta = to - from;
        float length = delta.magnitude;
        if (length < 1f) return;

        float angle = Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;
        Matrix4x4 saved = GUI.matrix;
        Matrix4x4 rotation = Matrix4x4.TRS(
            new Vector3(from.x, from.y, 0f),
            Quaternion.Euler(0f, 0f, angle),
            Vector3.one);
        GUI.matrix = saved * rotation;
        GUI.DrawTexture(new Rect(0f, -thickness * 0.5f, length, thickness), CommanderUiTheme.BorderTexture);
        GUI.matrix = saved;
    }
}
