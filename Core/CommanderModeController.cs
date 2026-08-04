using UnityEngine;
using UnityEngine.SceneManagement;

namespace NuclearOptionCommander;

internal sealed class CommanderModeController : MonoBehaviour
{
    private CommanderCameraController? cameraController;
    private CommanderCursorController? cursorController;
    private CommanderSelectionService? selectionService;
    private CommanderFactionVehicleService? factionVehicleService;
    private CommanderCameraFollowService? cameraFollowService;
    private CommanderPovCrewUi? povCrewUi;
    private CommanderTacticalMapService? tacticalMapService;
    private CommanderRadarService? radarService;
    private CommanderMobileEmplacementService? mobileEmplacementService;
    private CommanderDirectPathService? directPathService;
    private CommanderRepairService? repairService;
    private CommanderSupplyHeliService? supplyHeliService;
    private CommanderAirCommandService? airCommandService;
    private CommanderNavalPurchaseService? navalPurchaseService;
    private CommanderSamSiteAnalyzerService? samSiteAnalyzerService;
    private CommanderSamSiteService? samSiteService;
    private CommanderSpawnService? spawnService;
    private CommanderMarkerService? markerService;
    private CommanderMoveService? moveService;
    private CommanderOverlayUi? overlayUi;
    private CommanderInputController? inputController;
    private CommanderPersistentOperations? persistentOperations;
    private float nextInactiveEntryProbeAt;
    private bool aircraftSelectionMenuPresent;

    internal bool IsActive { get; private set; }

    private void Awake()
    {
        CommanderUiScale.ApplyResolutionPreset();
        cameraController = new CommanderCameraController();
        cursorController = new CommanderCursorController();
        selectionService = new CommanderSelectionService();
        cameraFollowService = new CommanderCameraFollowService(selectionService);
        povCrewUi = new CommanderPovCrewUi(cameraFollowService);
        factionVehicleService = new CommanderFactionVehicleService();
        tacticalMapService = new CommanderTacticalMapService(cameraFollowService);
        radarService = new CommanderRadarService(selectionService);
        mobileEmplacementService = new CommanderMobileEmplacementService(selectionService);
        directPathService = new CommanderDirectPathService(selectionService);
        repairService = new CommanderRepairService();
        supplyHeliService = new CommanderSupplyHeliService();
        airCommandService = new CommanderAirCommandService(tacticalMapService);
        navalPurchaseService = new CommanderNavalPurchaseService(tacticalMapService);
        samSiteAnalyzerService = new CommanderSamSiteAnalyzerService();
        samSiteService = new CommanderSamSiteService(
            samSiteAnalyzerService,
            supplyHeliService);
        spawnService = new CommanderSpawnService(selectionService, factionVehicleService, tacticalMapService);
        persistentOperations = new CommanderPersistentOperations(
            spawnService,
            supplyHeliService,
            airCommandService,
            mobileEmplacementService,
            samSiteAnalyzerService,
            samSiteService);
        markerService = new CommanderMarkerService(selectionService);
        moveService = new CommanderMoveService(selectionService);
        overlayUi = new CommanderOverlayUi(
            selectionService,
            moveService,
            spawnService,
            radarService,
            mobileEmplacementService,
            repairService,
            directPathService,
            supplyHeliService,
            airCommandService,
            navalPurchaseService,
            samSiteAnalyzerService,
            samSiteService,
            UnlockAdvancedFeatures,
            () => Deactivate());
        inputController = new CommanderInputController(
            overlayUi,
            selectionService,
            spawnService,
            markerService,
            moveService,
            tacticalMapService,
            supplyHeliService,
            mobileEmplacementService,
            airCommandService);
        inputController.SetPovCrewUi(povCrewUi);
        SceneManager.activeSceneChanged += OnActiveSceneChanged;
    }

    private void Update()
    {
        CommanderUiScale.RefreshResolutionPreset();
        if (CommanderFeatureGate.AdvancedFeaturesEnabled)
        {
            persistentOperations?.Tick();
        }
        if (!IsActive)
        {
            return;
        }

        if (CommanderShortcutInput.IsDown(CommanderSettings.ToggleUi))
        {
            overlayUi?.ToggleScreenshotUi();
        }

        if (IsPlayerInOperationalAircraft())
        {
            Deactivate(restorePreviousCamera: false);
            return;
        }

        cursorController?.Tick();
        selectionService?.Tick();
        cameraFollowService?.Tick();
        moveService?.Tick();
        markerService?.Tick();
        tacticalMapService?.Tick();
        if (CommanderFeatureGate.AdvancedFeaturesEnabled)
        {
            radarService?.Tick();
            mobileEmplacementService?.TickActive();
            supplyHeliService?.TickActive();
            airCommandService?.TickActive();
            navalPurchaseService?.TickActive();
            samSiteAnalyzerService?.TickActive();
            spawnService?.TickActive();
        }
        overlayUi?.Tick();
        inputController?.Tick();
    }

    private void FixedUpdate()
    {
        if (IsActive)
        {
            cameraFollowService?.FixedTick();
        }
    }

    private void OnGUI()
    {
        Matrix4x4 previousMatrix = CommanderUiScale.Begin();
        try
        {
            if (!IsActive)
            {
                if (ShouldShowCommanderEntry())
                {
                    overlayUi?.DrawInactiveLauncher(Activate);
                }
                return;
            }

            overlayUi?.Draw();
            if (overlayUi?.CommanderUiHidden != true)
            {
                povCrewUi?.Draw();
            }
            if (overlayUi?.ShowTacticalMapUi == true)
            {
                tacticalMapService?.DrawControls();
            }
            if (inputController != null && inputController.IsBoxSelecting)
            {
                DrawBoxSelection(inputController.BoxSelectionScreenRect);
            }
        }
        finally
        {
            CommanderUiScale.End(previousMatrix);
        }
    }

    private bool ShouldShowCommanderEntry()
    {
        if (IsPlayerInOperationalAircraft()
            || (GameManager.gameState != GameState.SinglePlayer && GameManager.gameState != GameState.Multiplayer))
        {
            return false;
        }

        if (DynamicMap.mapMaximized)
        {
            return true;
        }

        if (Time.unscaledTime >= nextInactiveEntryProbeAt)
        {
            nextInactiveEntryProbeAt = Time.unscaledTime + 0.75f;
            aircraftSelectionMenuPresent = UnityEngine.Object.FindObjectOfType<AircraftSelectionMenu>() != null;
        }
        return aircraftSelectionMenuPresent;
    }

    private void OnDisable()
    {
        Deactivate();
    }

    private void OnDestroy()
    {
        SceneManager.activeSceneChanged -= OnActiveSceneChanged;
        Deactivate(restorePreviousCamera: false);
    }

    private void OnApplicationQuit()
    {
        Deactivate();
    }

    internal void Toggle()
    {
        if (IsActive)
        {
            Deactivate();
            return;
        }

        Activate();
    }

    private void Activate()
    {
        if (IsActive)
        {
            return;
        }

        if (IsPlayerInOperationalAircraft())
        {
            CommanderPlugin.Log.LogWarning("Commander mode is only available while the player is outside an aircraft.");
            return;
        }

        AircraftSelectionMenu? aircraftSelectionMenu = UnityEngine.Object.FindObjectOfType<AircraftSelectionMenu>();
        if (aircraftSelectionMenu != null && aircraftSelectionMenu.gameObject.activeInHierarchy)
        {
            aircraftSelectionMenu.ReturnToMap();
        }

        if (cameraController == null || !cameraController.TryActivate())
        {
            CommanderPlugin.Log.LogWarning("Commander mode could not start because the free camera is not available yet.");
            return;
        }

        CommanderFeatureGate.RefreshMission();
        cursorController?.Activate();
        IsActive = true;
        selectionService?.Activate();
        markerService?.Activate();
        if (CommanderFeatureGate.AdvancedFeaturesEnabled)
        {
            ActivateAdvancedServices();
        }
        overlayUi?.Activate();
        if (CommanderFeatureGate.AdvancedFeaturesEnabled
            && overlayUi?.ShowTacticalMapUi == true)
        {
            tacticalMapService?.Open();
        }
        CommanderPlugin.Log.LogInfo(
            $"Commander mode enabled: mission={CommanderFeatureGate.MissionName}, features={(CommanderFeatureGate.AdvancedFeaturesEnabled ? "full" : "core")}.");
    }

    private void UnlockAdvancedFeatures()
    {
        if (CommanderFeatureGate.AdvancedFeaturesEnabled)
        {
            return;
        }

        CommanderFeatureGate.UnlockAdvancedFeatures();
        if (IsActive)
        {
            ActivateAdvancedServices();
            if (overlayUi?.ShowTacticalMapUi == true)
            {
                tacticalMapService?.Open();
            }
        }
        CommanderPlugin.Log.LogWarning(
            $"Advanced Commander features manually unlocked for mission '{CommanderFeatureGate.MissionName}'.");
    }

    private void ActivateAdvancedServices()
    {
        radarService?.Activate();
        mobileEmplacementService?.Activate();
        supplyHeliService?.Activate();
        airCommandService?.Activate();
        navalPurchaseService?.Activate();
        samSiteAnalyzerService?.Activate();
        spawnService?.Activate();
    }

    private void Deactivate(bool restorePreviousCamera = true)
    {
        if (!IsActive)
        {
            return;
        }

        IsActive = false;
        selectionService?.Deactivate();
        cameraFollowService?.Disable();
        markerService?.Deactivate();
        radarService?.Deactivate();
        mobileEmplacementService?.Deactivate();
        directPathService?.Deactivate();
        supplyHeliService?.Deactivate();
        airCommandService?.Deactivate();
        navalPurchaseService?.Deactivate();
        samSiteAnalyzerService?.Deactivate();
        spawnService?.Deactivate();
        overlayUi?.Deactivate();
        tacticalMapService?.Close();
        cursorController?.Deactivate();
        cameraController?.Deactivate(restorePreviousCamera);
        CommanderPlugin.Log.LogInfo("Commander mode disabled.");
    }

    private static bool IsPlayerInOperationalAircraft()
    {
        return GameManager.GetLocalAircraft(out Aircraft aircraft)
            && aircraft != null
            && !aircraft.disabled;
    }

    private void OnActiveSceneChanged(Scene previousScene, Scene newScene)
    {
        Deactivate(restorePreviousCamera: false);
        CommanderFeatureGate.ResetSession();
        selectionService?.ResetSession();
        cameraFollowService?.Disable();
        tacticalMapService?.ResetSession();
        radarService?.ResetSession();
        mobileEmplacementService?.ResetSession();
        directPathService?.ResetSession();
        repairService?.ResetSession();
        supplyHeliService?.ResetSession();
        airCommandService?.ResetSession();
        navalPurchaseService?.ResetSession();
        samSiteService?.ResetSession();
        samSiteAnalyzerService?.ResetSession();
        aircraftSelectionMenuPresent = false;
        nextInactiveEntryProbeAt = 0f;
        factionVehicleService?.ResetSession();
        spawnService?.ResetSession();
    }

    private static void DrawBoxSelection(Rect screenRect)
    {
        CommanderUiTheme.Ensure();
        // Convert screen rect (Y=0 bottom) to GUI rect (Y=0 top, scaled)
        float xMin = screenRect.xMin;
        float xMax = screenRect.xMax;
        float yMin = screenRect.yMin;
        float yMax = screenRect.yMax;
        Vector2 topLeft = CommanderUiScale.ScreenToGui(new Vector2(xMin, yMax));
        Vector2 bottomRight = CommanderUiScale.ScreenToGui(new Vector2(xMax, yMin));
        Rect guiRect = new(topLeft.x, topLeft.y, bottomRight.x - topLeft.x, bottomRight.y - topLeft.y);

        Color previous = GUI.color;
        GUI.color = new Color(0.2f, 0.85f, 0.82f, 0.15f);
        GUI.DrawTexture(guiRect, CommanderUiTheme.BorderTexture);
        GUI.color = new Color(0.2f, 0.85f, 0.82f, 0.8f);
        CommanderUiTheme.DrawFrame(guiRect, 1f);
        GUI.color = previous;
    }

}
