using UnityEngine;

namespace NuclearOptionCommander;

internal sealed class CommanderInputController
{
    private readonly CommanderOverlayUi overlayUi;
    private readonly CommanderSelectionService selectionService;
    private readonly CommanderSpawnService spawnService;
    private readonly CommanderMarkerService markerService;
    private readonly CommanderMoveService moveService;
    private readonly CommanderTacticalMapService tacticalMapService;
    private readonly CommanderSupplyHeliService supplyHeliService;
    private readonly CommanderMobileEmplacementService mobileEmplacementService;
    private readonly CommanderAirCommandService airCommandService;

    private bool isDragging;
    private Vector2 dragStart;
    private const float DragThreshold = 8f;

    internal static CommanderInputController? Instance { get; private set; }
    internal bool IsBoxSelecting => isDragging;
    internal Rect BoxSelectionScreenRect { get; private set; }

    internal CommanderInputController(
        CommanderOverlayUi overlayUi,
        CommanderSelectionService selectionService,
        CommanderSpawnService spawnService,
        CommanderMarkerService markerService,
        CommanderMoveService moveService,
        CommanderTacticalMapService tacticalMapService,
        CommanderSupplyHeliService supplyHeliService,
        CommanderMobileEmplacementService mobileEmplacementService,
        CommanderAirCommandService airCommandService)
    {
        this.overlayUi = overlayUi;
        this.selectionService = selectionService;
        this.spawnService = spawnService;
        this.markerService = markerService;
        this.moveService = moveService;
        this.tacticalMapService = tacticalMapService;
        this.supplyHeliService = supplyHeliService;
        this.mobileEmplacementService = mobileEmplacementService;
        this.airCommandService = airCommandService;
        Instance = this;
    }

    internal void Tick()
    {
        if (spawnService.IsMapInteractionActive())
        {
            return;
        }

        Vector2 mousePosition = Input.mousePosition;
        if (tacticalMapService.ContainsScreenPoint(mousePosition))
        {
            return;
        }

        DynamicMap? dynamicMap = SceneSingleton<DynamicMap>.i;
        if (tacticalMapService.IsOpen && dynamicMap != null && dynamicMap.IsCursorInMapRectangle())
        {
            return;
        }

        HandleControlGroups();
        HandleBoxSelection(mousePosition);

        if (Input.GetMouseButtonDown(1))
        {
            HandleSecondaryClick(mousePosition);
        }
    }

    private void HandleBoxSelection(Vector2 mousePosition)
    {
        if (Input.GetMouseButtonDown(0))
        {
            if (!overlayUi.ContainsScreenPoint(mousePosition))
            {
                dragStart = mousePosition;
                isDragging = false;
            }
        }

        if (Input.GetMouseButton(0))
        {
            if (!isDragging && Vector2.Distance(mousePosition, dragStart) > DragThreshold)
            {
                isDragging = true;
            }

            if (isDragging)
            {
                float xMin = Mathf.Min(dragStart.x, mousePosition.x);
                float xMax = Mathf.Max(dragStart.x, mousePosition.x);
                float yMin = Mathf.Min(dragStart.y, mousePosition.y);
                float yMax = Mathf.Max(dragStart.y, mousePosition.y);
                BoxSelectionScreenRect = new Rect(xMin, yMin, xMax - xMin, yMax - yMin);
            }
        }

        if (Input.GetMouseButtonUp(0))
        {
            if (isDragging)
            {
                selectionService.SelectUnitsInScreenRect(BoxSelectionScreenRect);
                isDragging = false;
            }
            else
            {
                HandlePrimaryClick(mousePosition);
            }
        }
    }

    private void HandleControlGroups()
    {
        bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
        for (int i = 0; i < 9; i++)
        {
            if (Input.GetKeyDown(KeyCode.Alpha1 + i))
            {
                if (ctrl)
                {
                    selectionService.AssignControlGroup(i);
                }
                else
                {
                    selectionService.RecallControlGroup(i);
                }
            }
        }
    }

    private void HandlePrimaryClick(Vector2 mousePosition)
    {
        if (overlayUi.ContainsScreenPoint(mousePosition))
        {
            return;
        }

        if (supplyHeliService.AwaitingTargetSelection)
        {
            supplyHeliService.TrySpawnAtWorldPoint(mousePosition);
            return;
        }

        if (airCommandService.AwaitingAreaSelection)
        {
            airCommandService.TrySetAreaFromWorld(mousePosition);
            return;
        }

        if (mobileEmplacementService.AwaitingDestination)
        {
            mobileEmplacementService.TrySetDestinationFromWorld(mousePosition);
            return;
        }

        if (spawnService.AwaitingRallyPointSelection)
        {
            spawnService.TrySetRallyPointFromWorld(mousePosition);
            return;
        }

        bool additive = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

        if (markerService.TryGetMarkerUnitAt(mousePosition, out Unit markerUnit))
        {
            selectionService.SelectUnit(markerUnit, additive);
            CommanderCameraFollowService.Instance?.CenterOnSelectionIfFollowing();
            return;
        }

        if (CommanderGameAccess.TryRaycastSelectableUnit(mousePosition, out Unit worldUnit))
        {
            selectionService.SelectUnit(worldUnit, additive);
            CommanderCameraFollowService.Instance?.CenterOnSelectionIfFollowing();
            return;
        }

        if (!additive)
        {
            selectionService.DeselectAll();
        }
    }

    private void HandleSecondaryClick(Vector2 mousePosition)
    {
        if (overlayUi.ContainsScreenPoint(mousePosition))
        {
            return;
        }

        moveService.TryIssueMoveOrder(mousePosition);
    }
}
