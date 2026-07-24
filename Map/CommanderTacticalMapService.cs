using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NuclearOptionCommander;

internal sealed class CommanderTacticalMapService
{
    private const float TacticalMapSize = 675f;
    private const float TacticalMapMargin = 12f;
    private const float HeaderHeight = 30f;

    private static readonly MethodInfo? JumpCameraToMethod = AccessTools.Method(typeof(DynamicMap), "JumpCameraTo");

    private readonly CommanderCameraFollowService cameraFollowService;
    private readonly CommanderMapClickTracker cameraJumpTracker = new();
    private readonly Vector3[] mapCorners = new Vector3[4];
    private DynamicMap? activeMap;
    private bool tacticalOpen;
    private bool positionInitialized;
    private bool dragging;
    private bool helpVisible;
    private Vector2 dragOffset;
    private Rect mapWindowRect;
    private GameObject? hiddenVirtualMfd;
    private bool virtualMfdWasActive;
    private bool restoreTacticalAfterFullscreen;
    private int suppressExtraUiFrame = -1;

    internal static CommanderTacticalMapService? Instance { get; private set; }
    internal static bool AllowCommanderMapJump { get; private set; }
    internal bool IsOpen => tacticalOpen && DynamicMap.mapMaximized;
    internal bool IsFullscreenOpen => !tacticalOpen && DynamicMap.mapMaximized;
    internal bool SuppressMapFollow { get; set; }
    internal bool SuppressExtraUiThisFrame => suppressExtraUiFrame == Time.frameCount;

    internal bool ContainsScreenPoint(Vector2 screenPoint)
    {
        Vector2 guiPoint = CommanderUiScale.ScreenToGui(screenPoint);
        if (IsOpen)
        {
            return mapWindowRect.Contains(guiPoint);
        }

        return !DynamicMap.mapMaximized
            && new Rect(CommanderUiScale.Width - 86f, TacticalMapMargin, 74f, 38f).Contains(guiPoint);
    }

    internal CommanderTacticalMapService(CommanderCameraFollowService cameraFollowService)
    {
        this.cameraFollowService = cameraFollowService;
        Instance = this;
    }

    internal void Toggle()
    {
        if (IsOpen)
        {
            Close();
        }
        else
        {
            Open();
        }
    }

    internal bool HandleMapKey()
    {
        if (IsOpen)
        {
            bool opened = OpenFullscreen();
            restoreTacticalAfterFullscreen = opened;
            return opened;
        }

        if (restoreTacticalAfterFullscreen && IsFullscreenOpen)
        {
            RestoreTacticalMap();
            return true;
        }

        return false;
    }

    internal bool Open()
    {
        DynamicMap? dynamicMap = SceneSingleton<DynamicMap>.i;
        if (dynamicMap == null)
        {
            return false;
        }

        DynamicMap.AllowedToOpen = true;
        if (!DynamicMap.mapMaximized)
        {
            dynamicMap.Maximize();
        }

        activeMap = dynamicMap;
        tacticalOpen = true;
        helpVisible = false;
        cameraJumpTracker.Reset();
        EnsureInitialPosition();
        ApplyLayout();
        HideFullMapPanels();
        return true;
    }

    internal bool OpenFullscreen()
    {
        if (IsOpen)
        {
            Close();
        }

        DynamicMap? dynamicMap = SceneSingleton<DynamicMap>.i;
        if (dynamicMap == null)
        {
            return false;
        }

        RestoreVirtualMfd();
        RestoreScale(dynamicMap);
        DynamicMap.AllowedToOpen = true;
        if (!DynamicMap.mapMaximized)
        {
            dynamicMap.Maximize();
        }

        activeMap = dynamicMap;
        tacticalOpen = false;
        return DynamicMap.mapMaximized;
    }

    internal void Close()
    {
        DynamicMap? dynamicMap = activeMap ?? SceneSingleton<DynamicMap>.i;
        RestoreScale(dynamicMap);
        RestoreVirtualMfd();
        if (dynamicMap != null && DynamicMap.mapMaximized)
        {
            dynamicMap.Minimize();
        }

        tacticalOpen = false;
        dragging = false;
        helpVisible = false;
        SuppressMapFollow = false;
        activeMap = null;
        cameraJumpTracker.Reset();
        restoreTacticalAfterFullscreen = false;
    }

    internal void CloseFullscreen()
    {
        DynamicMap? dynamicMap = activeMap ?? SceneSingleton<DynamicMap>.i;
        RestoreScale(dynamicMap);
        RestoreVirtualMfd();
        if (dynamicMap != null && DynamicMap.mapMaximized)
        {
            dynamicMap.Minimize();
        }

        tacticalOpen = false;
        SuppressMapFollow = false;
        activeMap = null;
    }

    internal void Tick()
    {
        if (restoreTacticalAfterFullscreen && IsFullscreenOpen && CommanderGameInput.CancelDown)
        {
            suppressExtraUiFrame = Time.frameCount;
            RestoreTacticalMap();
            return;
        }

        if (restoreTacticalAfterFullscreen && !DynamicMap.mapMaximized)
        {
            restoreTacticalAfterFullscreen = false;
        }

        if (!tacticalOpen)
        {
            return;
        }

        if (!DynamicMap.mapMaximized || activeMap == null)
        {
            tacticalOpen = false;
            dragging = false;
            SuppressMapFollow = false;
            activeMap = null;
            cameraJumpTracker.Reset();
            return;
        }

        mapWindowRect.width = TacticalMapSize;
        mapWindowRect.height = TacticalMapSize + HeaderHeight;
        mapWindowRect = CommanderUiTheme.ClampWindow(mapWindowRect, TacticalMapMargin);
        ApplyLayout();
        HideFullMapPanels();

        if (!SuppressMapFollow && cameraJumpTracker.Tick(activeMap, out GlobalPosition position))
        {
            JumpCameraTo(position);
        }
    }

    internal void DrawControls()
    {
        CommanderUiTheme.Ensure();
        if (!IsOpen)
        {
            if (!DynamicMap.mapMaximized)
            {
                Rect launcher = new(CommanderUiScale.Width - 86f, TacticalMapMargin, 74f, 38f);
                if (GUI.Button(launcher, "MAP  <", CommanderUiTheme.PrimaryButton))
                {
                    Open();
                }
            }
            return;
        }

        Rect header = new(mapWindowRect.x, mapWindowRect.y, mapWindowRect.width, HeaderHeight);
        GUI.Box(header, string.Empty, CommanderUiTheme.Panel);
        GUI.Label(new Rect(header.x + 10f, header.y + 3f, 82f, 24f), "MAP", CommanderUiTheme.Header);
        Rect cameraGroup = new(header.x + 96f, header.y + 2f, 514f, 26f);
        CommanderUiTheme.DrawFrame(cameraGroup, 1f);
        GUI.Label(new Rect(cameraGroup.x + 4f, cameraGroup.y + 1f, 38f, 24f), "CAM", CommanderUiTheme.MutedLabel);
        bool oldEnabled = GUI.enabled;
        GUI.enabled = oldEnabled && cameraFollowService.CanFollow;
        if (GUI.Button(new Rect(cameraGroup.x + 42f, cameraGroup.y + 2f, 104f, 22f),
            cameraFollowService.Enabled ? "FOLLOW POS" : "FOLLOW",
            cameraFollowService.Enabled ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            cameraFollowService.Toggle();
        }
        if (GUI.Button(new Rect(cameraGroup.x + 150f, cameraGroup.y + 2f, 96f, 22f), "CENTER", CommanderUiTheme.Button))
        {
            cameraFollowService.CenterOnSelection();
        }
        if (GUI.Button(new Rect(cameraGroup.x + 250f, cameraGroup.y + 2f, 126f, 22f),
            cameraFollowService.FollowRotation ? "FOLLOW ROT ON" : "FOLLOW ROT",
            cameraFollowService.FollowRotation ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            cameraFollowService.ToggleRotation();
        }
        if (GUI.Button(new Rect(cameraGroup.x + 380f, cameraGroup.y + 2f, 128f, 22f),
            cameraFollowService.PovMode ? "POV ON" : "POV",
            cameraFollowService.PovMode ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            cameraFollowService.TogglePov();
        }
        GUI.enabled = oldEnabled;
        if (GUI.Button(new Rect(header.xMax - 60f, header.y + 3f, 26f, 24f), "?", CommanderUiTheme.HelpButton))
        {
            helpVisible = !helpVisible;
        }
        if (GUI.Button(new Rect(header.xMax - 30f, header.y + 3f, 26f, 24f), "X", CommanderUiTheme.DangerButton))
        {
            Close();
            return;
        }

        Rect mapRect = GetMapGuiRect();
        CommanderUiTheme.DrawFrame(new Rect(mapRect.x - 2f, mapRect.y - 2f, mapRect.width + 4f, mapRect.height + 4f));
        if (helpVisible)
        {
            CommanderUiTheme.DrawHelpOverlay(
                new Rect(mapRect.x + 10f, mapRect.y + 10f, mapRect.width - 20f, 82f),
                "LMB selects map icons or moves the free camera when terrain is empty; RMB issues Basegame orders. FOLLOW tracks position, CENTER jumps once, FOLLOW ROT tracks orientation, and POV uses a fixed local view. M opens the fullscreen map and restores this map when closed.");
        }

        HandleDrag(new Rect(header.x, header.y, header.width - 66f, header.height));
    }

    internal void ResetSession()
    {
        RestoreScale(activeMap);
        RestoreVirtualMfd();
        tacticalOpen = false;
        dragging = false;
        helpVisible = false;
        SuppressMapFollow = false;
        activeMap = null;
        cameraJumpTracker.Reset();
        restoreTacticalAfterFullscreen = false;
        suppressExtraUiFrame = -1;
    }

    internal void ResetLayoutPosition()
    {
        positionInitialized = false;
        if (IsOpen)
        {
            EnsureInitialPosition();
            ApplyLayout();
        }
    }

    private void RestoreTacticalMap()
    {
        restoreTacticalAfterFullscreen = false;
        CloseFullscreen();
        Open();
    }


    private void EnsureInitialPosition()
    {
        if (positionInitialized)
        {
            return;
        }

        mapWindowRect = new Rect(
            CommanderUiScale.Width - TacticalMapMargin - TacticalMapSize,
            TacticalMapMargin,
            TacticalMapSize,
            TacticalMapSize + HeaderHeight);
        positionInitialized = true;
    }

    private Rect GetMapGuiRect()
    {
        return new Rect(mapWindowRect.x, mapWindowRect.y + HeaderHeight, TacticalMapSize, TacticalMapSize);
    }

    private void ApplyLayout()
    {
        if (activeMap == null)
        {
            return;
        }

        Rect mapRect = GetMapGuiRect();
        RectTransform rectTransform = activeMap.GetComponent<RectTransform>();
        rectTransform.localScale = Vector3.one;
        RectTransform measuredTransform = activeMap.mapBackground != null
            ? activeMap.mapBackground.rectTransform
            : rectTransform;
        measuredTransform.GetWorldCorners(mapCorners);
        Vector2 bottomLeft = RectTransformUtility.WorldToScreenPoint(null, mapCorners[0]);
        Vector2 bottomRight = RectTransformUtility.WorldToScreenPoint(null, mapCorners[3]);
        float sourcePixelSize = Mathf.Max(Vector2.Distance(bottomLeft, bottomRight), 1f);
        float scale = TacticalMapSize * CommanderUiScale.Scale / sourcePixelSize;
        rectTransform.localScale = Vector3.one * scale;
        Vector2 screenCenter = CommanderUiScale.GuiToScreen(mapRect.center);
        rectTransform.position = new Vector3(
            screenCenter.x,
            screenCenter.y,
            rectTransform.position.z);
    }

    private void HandleDrag(Rect dragRect)
    {
        Event current = Event.current;
        if (current.type == EventType.MouseDown && current.button == 0 && dragRect.Contains(current.mousePosition))
        {
            dragging = true;
            dragOffset = current.mousePosition - mapWindowRect.position;
            current.Use();
        }
        else if (current.type == EventType.MouseDrag && dragging)
        {
            mapWindowRect.position = current.mousePosition - dragOffset;
            mapWindowRect = CommanderUiTheme.ClampWindow(mapWindowRect, TacticalMapMargin);
            ApplyLayout();
            current.Use();
        }
        else if (current.type == EventType.MouseUp && current.button == 0)
        {
            dragging = false;
        }
    }

    private void JumpCameraTo(GlobalPosition position)
    {
        if (activeMap == null || JumpCameraToMethod == null)
        {
            return;
        }

        AllowCommanderMapJump = true;
        try
        {
            JumpCameraToMethod.Invoke(activeMap, new object[] { position });
        }
        finally
        {
            AllowCommanderMapJump = false;
        }
    }

    private static void RestoreScale(DynamicMap? dynamicMap)
    {
        if (dynamicMap != null)
        {
            dynamicMap.GetComponent<RectTransform>().localScale = Vector3.one;
        }
    }

    private static void HideFullMapPanels()
    {
        GameplayUI? gameplayUi = SceneSingleton<GameplayUI>.i;
        gameplayUi?.HideSelectAirbase();
        gameplayUi?.HideSpectatorPanel();
        Instance?.HideVirtualMfd();
    }

    private void HideVirtualMfd()
    {
        GameObject? virtualMfd = SceneSingleton<MapOptions>.i?.screen?.virtualMFD?.gameObject;
        if (virtualMfd == null)
        {
            return;
        }

        if (hiddenVirtualMfd != virtualMfd)
        {
            RestoreVirtualMfd();
            hiddenVirtualMfd = virtualMfd;
            virtualMfdWasActive = virtualMfd.activeSelf;
        }
        virtualMfd.SetActive(false);
    }

    private void RestoreVirtualMfd()
    {
        if (hiddenVirtualMfd != null)
        {
            hiddenVirtualMfd.SetActive(virtualMfdWasActive);
        }
        hiddenVirtualMfd = null;
        virtualMfdWasActive = false;
    }
}
