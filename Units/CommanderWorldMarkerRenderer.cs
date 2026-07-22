using System.Collections.Generic;
using UnityEngine;

namespace NuclearOptionCommander;

internal sealed class CommanderWorldMarkerRenderer
{
    private readonly CommanderSelectionService selectionService;
    private readonly CommanderMoveService moveService;
    private readonly CommanderSpawnService spawnService;
    private readonly CommanderSupplyHeliService supplyHeliService;
    private readonly List<GlobalPosition> deliveryTargets = new();

    internal CommanderWorldMarkerRenderer(
        CommanderSelectionService selectionService,
        CommanderMoveService moveService,
        CommanderSpawnService spawnService,
        CommanderSupplyHeliService supplyHeliService)
    {
        this.selectionService = selectionService;
        this.moveService = moveService;
        this.spawnService = spawnService;
        this.supplyHeliService = supplyHeliService;
    }

    internal void Draw(bool supplyWindowVisible)
    {
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

    private static void DrawMarker(Camera camera, GlobalPosition position, string label, Color color)
    {
        Vector3 world = position.ToLocalPosition();
        Vector3 screen = camera.WorldToScreenPoint(world);
        if (screen.z <= 0f || screen.x < 0f || screen.x > Screen.width || screen.y < 0f || screen.y > Screen.height)
        {
            return;
        }

        Vector2 guiPoint = CommanderUiScale.ScreenToGui(screen);
        Rect marker = new(guiPoint.x - 30f, guiPoint.y - 13f, 65f, 32f);
        Color previous = GUI.color;
        GUI.color = color;
        GUI.Box(marker, label, CommanderUiTheme.Panel);
        CommanderUiTheme.DrawFrame(marker, 1f);
        GUI.color = previous;
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
