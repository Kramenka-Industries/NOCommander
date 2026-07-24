using UnityEngine;

namespace NuclearOptionCommander;

internal sealed class CommanderCameraFollowService
{
    private readonly CommanderSelectionService selectionService;
    private Unit? target;
    private Vector3 lastGlobalPosition;
    private Quaternion lastTargetRotation;
    private Vector3 povLocalPosition;
    private Quaternion povLocalRotation;
    private float spacePressedAt;
    private bool spaceHeld;
    private bool longSpaceTriggered;

    internal CommanderCameraFollowService(CommanderSelectionService selectionService)
    {
        this.selectionService = selectionService;
        Instance = this;
    }

    internal static CommanderCameraFollowService? Instance { get; private set; }
    internal bool Enabled { get; private set; }
    internal bool FollowRotation { get; private set; }
    internal bool PovMode { get; private set; }
    internal bool CanFollow => selectionService.FocusedSelection is Unit unit && !unit.disabled;
    internal Aircraft? FollowedAircraft => Enabled ? target as Aircraft : null;

    internal void Toggle()
    {
        if (Enabled)
        {
            Disable();
            return;
        }

        Unit? selected = selectionService.FocusedSelection;
        if (selected == null || selected.disabled)
        {
            return;
        }

        target = selected;
        lastGlobalPosition = selected.GlobalPosition().AsVector3();
        lastTargetRotation = selected.transform.rotation;
        Enabled = true;
    }

    internal void ToggleRotation()
    {
        if (!CanFollow)
        {
            return;
        }

        if (!Enabled)
        {
            Toggle();
        }

        FollowRotation = !FollowRotation;
        PovMode = false;
        Unit? selected = selectionService.FocusedSelection;
        if (selected != null)
        {
            lastTargetRotation = selected.transform.rotation;
        }
    }

    internal void TogglePov()
    {
        if (!CanFollow)
        {
            return;
        }

        if (!Enabled)
        {
            Toggle();
        }

        PovMode = !PovMode;
        FollowRotation = false;
        CapturePovOffset();
    }

    internal void CenterOnSelection()
    {
        Unit? selected = selectionService.FocusedSelection;
        CameraStateManager? cameraManager = SceneSingleton<CameraStateManager>.i;
        if (selected == null || selected.disabled || cameraManager == null)
        {
            return;
        }

        float length = selected.definition != null ? selected.definition.length : selected.maxRadius * 2f;
        float distance = Mathf.Max(20f, selected.maxRadius * 4f, length * 2f);
        Vector3 targetPosition = selected.transform.position + Vector3.up * Mathf.Max(1f, selected.maxRadius * 0.35f);
        Vector3 viewDirection = cameraManager.transform.forward;
        if (viewDirection.sqrMagnitude < 0.1f)
        {
            viewDirection = -selected.transform.forward;
        }

        cameraManager.transform.position = targetPosition - viewDirection.normalized * distance;
        cameraManager.transform.rotation = Quaternion.LookRotation(targetPosition - cameraManager.transform.position, Vector3.up);
        cameraManager.cameraVelocity = Vector3.zero;

        target = selected;
        lastGlobalPosition = selected.GlobalPosition().AsVector3();
        lastTargetRotation = selected.transform.rotation;
        if (PovMode)
        {
            CapturePovOffset();
        }
    }

    internal void CenterOnSelectionIfFollowing()
    {
        if (Enabled)
        {
            CenterOnSelection();
        }
    }

    internal void Tick()
    {
        HandleSpaceShortcut();
        if (!Enabled)
        {
            return;
        }

        Unit? selected = selectionService.FocusedSelection;
        if (selected == null || selected.disabled)
        {
            Disable();
            return;
        }

        Vector3 currentGlobalPosition = selected.GlobalPosition().AsVector3();
        if (!ReferenceEquals(selected, target))
        {
            target = selected;
            lastGlobalPosition = currentGlobalPosition;
            lastTargetRotation = selected.transform.rotation;
            CapturePovOffset();
            return;
        }

        CameraStateManager? cameraManager = SceneSingleton<CameraStateManager>.i;
        if (cameraManager != null)
        {
            if (PovMode)
            {
                cameraManager.transform.position = selected.transform.TransformPoint(povLocalPosition);
                cameraManager.transform.rotation = selected.transform.rotation * povLocalRotation;
            }
            else
            {
                cameraManager.transform.position += currentGlobalPosition - lastGlobalPosition;
                if (FollowRotation)
                {
                    Quaternion rotationDelta = selected.transform.rotation * Quaternion.Inverse(lastTargetRotation);
                    cameraManager.transform.rotation = rotationDelta * cameraManager.transform.rotation;
                }
            }
        }
        lastGlobalPosition = currentGlobalPosition;
        lastTargetRotation = selected.transform.rotation;
    }

    internal void Disable()
    {
        Enabled = false;
        FollowRotation = false;
        PovMode = false;
        target = null;
    }

    internal static void ApplyCommanderLatePose(CameraStateManager cameraManager)
    {
        CommanderCameraFollowService? service = Instance;
        Unit? selected = service?.selectionService.FocusedSelection;
        if (service == null || !service.Enabled || selected == null || selected.disabled)
        {
            return;
        }

        if (!service.PovMode)
        {
            return;
        }

        cameraManager.transform.position = selected.transform.TransformPoint(service.povLocalPosition);
        cameraManager.transform.rotation = selected.transform.rotation * service.povLocalRotation;
        cameraManager.cameraVelocity = Vector3.zero;
    }

    private void HandleSpaceShortcut()
    {
        var shortcut = CommanderSettings.CameraCenterFollow;
        if (shortcut.IsDown())
        {
            spaceHeld = true;
            longSpaceTriggered = false;
            spacePressedAt = Time.unscaledTime;
        }

        if (spaceHeld && !longSpaceTriggered && shortcut.IsPressed() && Time.unscaledTime - spacePressedAt >= 0.45f)
        {
            if (!Enabled)
            {
                Toggle();
            }
            CenterOnSelection();
            longSpaceTriggered = true;
        }

        if (spaceHeld && shortcut.IsUp())
        {
            if (!longSpaceTriggered)
            {
                CenterOnSelection();
            }
            spaceHeld = false;
        }
    }

    private void CapturePovOffset()
    {
        Unit? selected = selectionService.FocusedSelection;
        CameraStateManager? cameraManager = SceneSingleton<CameraStateManager>.i;
        if (selected == null || cameraManager == null)
        {
            return;
        }

        povLocalPosition = selected.transform.InverseTransformPoint(cameraManager.transform.position);
        povLocalRotation = Quaternion.Inverse(selected.transform.rotation) * cameraManager.transform.rotation;
    }
}
