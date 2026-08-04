using System;
using System.Collections.Generic;
using NuclearOption.Networking;
using UnityEngine;

namespace NuclearOptionCommander;

internal sealed class CommanderSamSiteAnalyzerService
{
    internal static CommanderSamSiteAnalyzerService? Instance { get; private set; }
    private const float RegionalSampleSpacing = 200f;
    private const float CandidateRegionSize = 4000f;
    private const int CandidateLimit = 2000;
    private const float LowAltitudeClearance = 100f;
    private const int CoverageDirectionCount = 64;
    private const float CoverageSampleSpacing = 1000f;
    private const float CoverageRange = 50000f;
    private const float ForwardCoverageRange = 5000f;
    private const float ForwardCoverageHalfAngle = 22.5f;
    private const float RequiredRoadDistance = 500f;
    private const float RadarHeight = 5f;
    private const int CoverageOverlayResolution = 192;
    private const int CoverageHorizonDirectionCount = 720;
    private const float CoverageHorizonSampleSpacing = 100f;
    private const int SuggestedSiteCount = 12;
    private const float CandidateSeparation = 500f;
    private const float StartupDelaySeconds = 8f;
    private const float SiteControlRadius = 300f;
    private const float LocalSnapRadius = 250f;
    private readonly List<SiteCandidate> candidates = new();
    private readonly List<SiteCandidate> suggestedSites = new();
    private readonly List<SiteLayoutMarker> siteLayout = new();
    private readonly List<GlobalPosition> friendlyAirbases = new();
    private readonly List<GlobalPosition> enemyAirbases = new();
    private const int StrategicWeightResolution = 256;
    private float[]? strategicWeights;
    private float[]? strategicRisks;
    private readonly CommanderStrategicHeightMap strategicHeightMap = new();
    private readonly CommanderLocalHeightMapBaker localHeightMapBaker = new();
    private readonly List<CommanderLocalHeightMapBaker.LocalHeightMap> localSiteMaps = new();

    private AnalyzerState state;
    private MapSettings? mapSettings;
    private string mapKey = string.Empty;
    private int regionColumns;
    private int regionRows;
    private int regionIndex;
    private int coverageCandidateIndex;
    private int coverageDirectionIndex;
    private float coverageDistance;
    private float coverageHighestTerrainSlope;
    private float coverageVisibleAreaWeight;
    private float coverageVisibleFrontWeight;
    private float coverageTotalFrontWeight;
    private float coverageTotalAreaWeight;
    private float coverageForwardVisibleWeight;
    private float coverageForwardTotalWeight;
    private Vector2 coverageEnemyDirection;
    private bool coverageEnemyDirectionReady;
    private Color32[]? coverageOverlayPixels;
    private float[]? coverageRequiredAltitudes;
    private byte[]? coverageOverlayAlpha;
    private float[]? coverageHorizonSlopes;
    private float[]? coverageHorizonProfile;
    private SiteCandidate coverageOverlayCandidate;
    private int coverageOverlayPixelIndex;
    private int coverageHorizonSampleIndex;
    private bool coverageOverlayBuilding;
    private Unit? coverageOverlaySource;
    private float coverageEmitterHeight;
    private float coverageTargetAltitude = LowAltitudeClearance;
    private bool uiVisible;
    private bool localRefinementActive;
    private int activeCandidateId = -1;
    private int activeRefinementGeneration;
    private SiteCandidate activeSite;
    private float nextNearbyRefreshAt;
    private float nextInfluenceRefreshAt;
    private bool showProposalMarkers;
    private Action<bool>? automaticSelectionCompleted;
    private bool limitRoadDistance;
    private float maximumCandidateRange;
    private float minimumAreaCoverage;
    private float minimumFrontShare;
    private float maximumRisk = 1f;
    private float minimumForwardCoverage;
    private FilterComparison rangeComparison = FilterComparison.Maximum;
    private FilterComparison areaComparison = FilterComparison.Minimum;
    private FilterComparison frontComparison = FilterComparison.Minimum;
    private FilterComparison riskComparison = FilterComparison.Maximum;
    private FilterComparison forwardComparison = FilterComparison.Minimum;
    private CandidateListMode candidateListMode;
    private CandidateSortMode candidateSortMode = CandidateSortMode.Rating;
    private string statusText = "Waiting for mission terrain.";
    private float analysisStartedAt;
    private float samplingStartedAt;
    private float coverageStartedAt;
    private float refinementStartedAt;

    internal IReadOnlyList<SiteCandidate> SuggestedSites => suggestedSites;
    internal AnalyzerState State => state;
    internal string StatusText => statusText;
    internal bool IsReady => state == AnalyzerState.Ready;
    internal IReadOnlyList<SiteLayoutMarker> SiteLayout => siteLayout;
    internal int ActiveSiteIndex => FindSuggestedSiteIndex(activeCandidateId);
    internal bool HasActiveSite => activeCandidateId >= 0;
    internal bool ActiveSiteReady => HasActiveSite && !localRefinementActive && siteLayout.Count > 0;
    internal bool ShowProposalMarkers => showProposalMarkers;
    internal bool LimitRoadDistance => limitRoadDistance;
    internal float MaximumCandidateRange => maximumCandidateRange;
    internal float MinimumAreaCoverage => minimumAreaCoverage;
    internal float MinimumFrontShare => minimumFrontShare;
    internal float MaximumRisk => maximumRisk;
    internal float MinimumForwardCoverage => minimumForwardCoverage;
    internal FilterComparison RangeComparison => rangeComparison;
    internal FilterComparison AreaComparison => areaComparison;
    internal FilterComparison FrontComparison => frontComparison;
    internal FilterComparison RiskComparison => riskComparison;
    internal FilterComparison ForwardComparison => forwardComparison;
    internal CandidateListMode ListMode => candidateListMode;
    internal CandidateSortMode SortMode => candidateSortMode;
    internal float MaxRoadDistance => RequiredRoadDistance;
    internal Texture2D? CoverageOverlayTexture { get; private set; }
    internal bool CoverageOverlayEnabled { get; private set; }
    internal bool CoverageOverlayBuilding => coverageOverlayBuilding;
    internal bool CoverageOverlayReady => CoverageOverlayEnabled
        && CoverageOverlayTexture != null
        && !coverageOverlayBuilding;
    internal float CoverageOverlayProgress => CoverageOverlayTexture == null
        ? 0f
        : coverageOverlayBuilding
            ? (float)(coverageHorizonSampleIndex + coverageOverlayPixelIndex)
                / (CoverageHorizonSampleCount + CoverageOverlayTexture.width * CoverageOverlayTexture.height)
            : 1f;
    internal GlobalPosition CoverageOverlayOrigin => coverageOverlayCandidate.Position;
    internal float CoverageTargetAltitude => coverageTargetAltitude;
    internal int ScanQueriesPerFrame => Mathf.Clamp(CommanderSettings.SamScanQueriesPerFrame, 16, 1024);

    internal static bool TryGetStrategicTerrainHeight(float globalX, float globalZ, out float height)
    {
        height = 0f;
        return Instance != null
            && Instance.strategicHeightMap.TryGetHeight(globalX, globalZ, out height);
    }

    internal static bool TryGetStrategicHeightMapSize(out Vector2 size)
    {
        size = Instance?.strategicHeightMap.MapSize ?? Vector2.zero;
        return Instance?.strategicHeightMap.IsReady == true && size.x > 0f && size.y > 0f;
    }

    internal static float EstimateStrategicTerrainNormalY(float globalX, float globalZ, float spacing = 25f)
    {
        return Instance?.strategicHeightMap.EstimateNormalY(globalX, globalZ, spacing) ?? 0f;
    }

    internal static bool TryEvaluateLogisticsRisk(
        GlobalPosition start,
        GlobalPosition destination,
        IReadOnlyList<GlobalPosition>? route,
        out float risk,
        out float routeLength)
    {
        risk = 0.5f;
        routeLength = 0f;
        if (Instance == null || !Instance.EnsureStrategicInfluenceReady())
        {
            return false;
        }

        float weightedRisk = 0f;
        float totalWeight = 0f;
        float maximumRisk = 0f;
        GlobalPosition previous = start;
        int pointCount = (route?.Count ?? 0) + 1;
        for (int pointIndex = 0; pointIndex < pointCount; pointIndex++)
        {
            GlobalPosition next = route != null && pointIndex < route.Count
                ? route[pointIndex]
                : destination;
            float segmentLength = Mathf.Sqrt(HorizontalSquareDistance(previous, next));
            routeLength += segmentLength;
            int samples = Mathf.Max(1, Mathf.CeilToInt(segmentLength / 2000f));
            for (int sample = 0; sample < samples; sample++)
            {
                float t = (sample + 0.5f) / samples;
                GlobalPosition position = new(
                    Mathf.Lerp((float)previous.x, (float)next.x, t),
                    Mathf.Lerp((float)previous.y, (float)next.y, t),
                    Mathf.Lerp((float)previous.z, (float)next.z, t));
                float sampleRisk = Instance.CalculateRisk(position);
                float weight = Mathf.Lerp(1.15f, 0.85f, t);
                weightedRisk += sampleRisk * weight;
                totalWeight += weight;
                maximumRisk = Mathf.Max(maximumRisk, sampleRisk);
            }
            previous = next;
        }

        float sourceRisk = Instance.CalculateRisk(start);
        float averageRisk = totalWeight > 0f ? weightedRisk / totalWeight : sourceRisk;
        risk = Mathf.Clamp01(sourceRisk * 0.4f + averageRisk * 0.4f + maximumRisk * 0.2f);
        return true;
    }

    internal CommanderSamSiteAnalyzerService()
    {
        Instance = this;
    }

    internal float Progress
    {
        get
        {
            return state switch
            {
                AnalyzerState.Sampling => regionColumns * regionRows == 0
                    ? 0f
                    : (float)regionIndex / (regionColumns * regionRows),
                AnalyzerState.Coverage => candidates.Count == 0
                    ? 0f
                    : (float)coverageCandidateIndex / candidates.Count,
                AnalyzerState.Refining => localRefinementActive ? 0.5f : 0f,
                AnalyzerState.Ready => 1f,
                _ => 0f
            };
        }
    }

    internal void Activate()
    {
    }

    internal void Deactivate()
    {
        uiVisible = false;
        ClearActiveSite();
        CoverageOverlayEnabled = false;
        ClearCoverageOverlay();
    }

    internal void ResetSession()
    {
        state = AnalyzerState.Waiting;
        mapSettings = null;
        mapKey = string.Empty;
        regionColumns = 0;
        regionRows = 0;
        regionIndex = 0;
        coverageCandidateIndex = 0;
        ResetCoverageAccumulator();
        coverageOverlayPixels = null;
        coverageOverlayPixelIndex = 0;
        coverageOverlayBuilding = false;
        activeCandidateId = -1;
        activeRefinementGeneration = 0;
        nextNearbyRefreshAt = 0f;
        nextInfluenceRefreshAt = 0f;
        showProposalMarkers = false;
        candidates.Clear();
        suggestedSites.Clear();
        siteLayout.Clear();
        strategicWeights = null;
        strategicRisks = null;
        strategicHeightMap.Reset();
        localHeightMapBaker.Reset();
        localSiteMaps.Clear();
        localRefinementActive = false;
        CoverageOverlayEnabled = false;
        ClearCoverageOverlay();
        statusText = "Waiting for mission terrain.";
    }

    internal void TickPersistent()
    {
        UpdateCoverageOverlayBatch();

        if (state == AnalyzerState.Waiting)
        {
            TryStart();
            return;
        }

        if (state == AnalyzerState.Sampling)
        {
            SampleTerrainBatch();
        }
        else if (state == AnalyzerState.Coverage)
        {
            EvaluateCoverageBatch();
        }

        if ((state == AnalyzerState.Ready || state == AnalyzerState.Refining)
            && (uiVisible || showProposalMarkers))
        {
            RefreshNearbySites();
        }
    }

    internal void TickActive()
    {
    }

    internal void SetUiVisible(bool visible)
    {
        if (uiVisible == visible)
        {
            return;
        }

        uiVisible = visible;
        if (uiVisible)
        {
            RefreshNearbySites(force: true);
            return;
        }
        if (!uiVisible)
        {
            showProposalMarkers = false;
            if (automaticSelectionCompleted == null)
            {
                ClearActiveSite();
            }
            ClearCoverageOverlay();
        }
    }

    internal void RebuildAnalysis()
    {
        if (automaticSelectionCompleted != null)
        {
            statusText = "AI site refinement is in progress; rebuild is temporarily unavailable.";
            return;
        }
        ResetAnalysisData();
        if (mapSettings != null)
        {
            BeginSampling(mapSettings);
        }
    }

    internal void SetLimitRoadDistance(bool enabled)
    {
        if (limitRoadDistance == enabled)
        {
            return;
        }

        limitRoadDistance = enabled;
        RefreshFilteredSuggestions();
    }

    internal void SetCandidateFilters(
        float maximumRange,
        float minimumAreaLos,
        float minimumFront,
        float riskLimit,
        float minimumForward)
    {
        maximumRange = Mathf.Max(0f, maximumRange);
        minimumAreaLos = Mathf.Clamp01(minimumAreaLos);
        minimumFront = Mathf.Clamp01(minimumFront);
        riskLimit = Mathf.Clamp01(riskLimit);
        minimumForward = Mathf.Clamp01(minimumForward);
        if (Mathf.Approximately(maximumCandidateRange, maximumRange)
            && Mathf.Approximately(minimumAreaCoverage, minimumAreaLos)
            && Mathf.Approximately(minimumFrontShare, minimumFront)
            && Mathf.Approximately(maximumRisk, riskLimit)
            && Mathf.Approximately(minimumForwardCoverage, minimumForward))
        {
            return;
        }

        maximumCandidateRange = maximumRange;
        minimumAreaCoverage = minimumAreaLos;
        minimumFrontShare = minimumFront;
        maximumRisk = riskLimit;
        minimumForwardCoverage = minimumForward;
        RefreshFilteredSuggestions();
    }

    internal void SetFilterComparison(int filter, FilterComparison comparison)
    {
        switch (filter)
        {
            case 0:
                rangeComparison = comparison;
                break;
            case 1:
                areaComparison = comparison;
                break;
            case 2:
                frontComparison = comparison;
                break;
            case 3:
                riskComparison = comparison;
                break;
            case 4:
                forwardComparison = comparison;
                break;
            default:
                return;
        }
        RefreshFilteredSuggestions();
    }

    internal void ResetCandidateFilters()
    {
        rangeComparison = FilterComparison.Maximum;
        areaComparison = FilterComparison.Minimum;
        frontComparison = FilterComparison.Minimum;
        riskComparison = FilterComparison.Maximum;
        forwardComparison = FilterComparison.Minimum;
        SetCandidateFilters(0f, 0f, 0f, 1f, 0f);
    }

    internal void SetCandidateListMode(CandidateListMode mode)
    {
        if (candidateListMode == mode)
        {
            return;
        }
        candidateListMode = mode;
        RefreshNearbySites(force: true);
    }

    internal void SetCandidateSortMode(CandidateSortMode mode)
    {
        if (candidateSortMode == mode)
        {
            return;
        }
        candidateSortMode = mode;
        candidateListMode = CandidateListMode.Ranked;
        RefreshNearbySites(force: true);
    }

    internal void JumpToSite(int index)
    {
        if (automaticSelectionCompleted != null)
        {
            statusText = "AI site refinement is in progress; wait for its construction request to finish.";
            return;
        }
        if (index < 0 || index >= suggestedSites.Count)
        {
            return;
        }

        SiteCandidate candidate = suggestedSites[index];
        if (activeCandidateId == candidate.CandidateId)
        {
            ClearActiveSite();
            statusText = $"Hidden SAM-site proposal {index + 1}.";
            return;
        }

        CommanderTacticalMapService? mapService = CommanderTacticalMapService.Instance;
        if (mapService?.JumpCameraToPosition(candidate.Position) != true)
        {
            statusText = "Could not move the camera to the selected proposal.";
            return;
        }

        BeginActiveSiteRefinement(candidate);
        statusText = $"Camera moved to SAM-site proposal {index + 1}; baking detailed terrain.";
        mapService.Close();
    }

    internal bool BeginAutomaticSiteSelection(
        bool useLocalCandidatePass,
        Action<bool> completed)
    {
        if (state != AnalyzerState.Ready || candidates.Count == 0 || localRefinementActive)
        {
            statusText = "Automatic site selection is waiting for completed terrain analysis.";
            return false;
        }

        CameraStateManager? cameraManager = SceneSingleton<CameraStateManager>.i;
        GlobalPosition cameraPosition = cameraManager != null
            ? cameraManager.transform.position.ToGlobalPosition()
            : default;
        List<SiteCandidate> eligible = new(candidates.Count);
        for (int i = 0; i < candidates.Count; i++)
        {
            float distance = cameraManager == null
                ? 0f
                : Mathf.Sqrt(HorizontalSquareDistance(cameraPosition, candidates[i].Position));
            if (PassesActiveFilters(candidates[i]) && PassesRangeFilter(distance))
            {
                eligible.Add(candidates[i]);
            }
        }

        if (eligible.Count == 0)
        {
            statusText = "No SAM-site candidate satisfies the active AI thresholds.";
            return false;
        }

        SiteCandidate selected = eligible[UnityEngine.Random.Range(0, eligible.Count)];
        if (useLocalCandidatePass)
        {
            const float localRadiusSquared = 1000f * 1000f;
            List<SiteCandidate> nearby = new();
            for (int i = 0; i < eligible.Count; i++)
            {
                if (HorizontalSquareDistance(selected.Position, eligible[i].Position) <= localRadiusSquared)
                {
                    nearby.Add(eligible[i]);
                }
            }
            nearby.Sort(static (left, right) => right.Score.CompareTo(left.Score));
            int topCount = Mathf.Max(1, Mathf.CeilToInt(nearby.Count * 0.3f));
            selected = nearby[UnityEngine.Random.Range(0, topCount)];
        }

        automaticSelectionCompleted = completed;
        BeginActiveSiteRefinement(selected);
        if (!localRefinementActive)
        {
            CompleteAutomaticSelection(false);
            return false;
        }
        statusText = useLocalCandidatePass
            ? "AI selected a filtered candidate and is refining a random local top-30% site."
            : "AI selected a random filtered candidate and is refining its layout.";
        return true;
    }

    internal bool GenerateCoverageOverlay(Unit source, GlobalPosition position)
    {
        if (!strategicHeightMap.IsReady)
        {
            statusText = "Radar coverage is waiting for the strategic heightmap.";
            return false;
        }

        CoverageOverlayEnabled = true;
        GlobalPosition emitterPosition = ResolveRadarEmitterPosition(source, position);
        SiteCandidate candidate = new(
            emitterPosition,
            emitterPosition.y,
            0f,
            0f,
            0f,
            0f,
            0f);
        BeginCoverageOverlay(candidate, emitterPosition.y);
        coverageOverlaySource = source;
        statusText = "Generating radar coverage.";
        return true;
    }

    internal void SetCoverageTargetAltitude(float altitude)
    {
        float snapped = Mathf.Clamp(Mathf.Round(altitude / 25f) * 25f, 0f, 2000f);
        if (Mathf.Approximately(coverageTargetAltitude, snapped))
        {
            return;
        }

        coverageTargetAltitude = snapped;
        if (CoverageOverlayReady)
        {
            RecolorCoverageOverlay();
        }
    }

    private static GlobalPosition ResolveRadarEmitterPosition(Unit source, GlobalPosition fallback)
    {
        Radar[] radars = source.GetComponentsInChildren<Radar>(includeInactive: true);
        Transform? highestScanner = null;
        for (int i = 0; i < radars.Length; i++)
        {
            Transform scanner = radars[i].GetScanPoint();
            if (scanner != null && (highestScanner == null || scanner.position.y > highestScanner.position.y))
            {
                highestScanner = scanner;
            }
        }

        return highestScanner != null
            ? highestScanner.GlobalPosition()
            : new GlobalPosition(fallback.x, fallback.y + RadarHeight, fallback.z);
    }

    internal bool CoverageMatches(Unit source)
    {
        return CoverageOverlayEnabled
            && CoverageOverlayTexture != null
            && ReferenceEquals(coverageOverlaySource, source);
    }

    internal void RetainCoverageForSelection(IReadOnlyList<Unit> selectedUnits)
    {
        if (!CoverageOverlayEnabled || coverageOverlaySource == null)
        {
            return;
        }

        for (int i = 0; i < selectedUnits.Count; i++)
        {
            if (ReferenceEquals(selectedUnits[i], coverageOverlaySource))
            {
                return;
            }
        }

        CoverageOverlayEnabled = false;
        ClearCoverageOverlay();
    }

    internal void SetProposalMarkersVisible(bool visible)
    {
        showProposalMarkers = visible;
    }

    internal void CopyProposalSites(List<SiteCandidate> destination)
    {
        destination.Clear();
        if (!showProposalMarkers)
        {
            return;
        }
        destination.AddRange(suggestedSites);
    }

    internal void CopyActiveLayout(List<SiteLayoutMarker> destination)
    {
        destination.Clear();
        if (!HasActiveSite)
        {
            return;
        }

        for (int i = 0; i < siteLayout.Count; i++)
        {
            if (siteLayout[i].SiteIndex == activeCandidateId)
            {
                destination.Add(siteLayout[i]);
            }
        }
    }

    internal void CopyVisibleActiveLayout(List<SiteLayoutMarker> destination)
    {
        if (!uiVisible)
        {
            destination.Clear();
            return;
        }
        CopyActiveLayout(destination);
    }

    internal bool TryGetActiveEnemyDirection(out Vector2 direction)
    {
        if (HasActiveSite)
        {
            direction = FindEnemyFrontDirection(activeSite.Position);
            return true;
        }

        direction = Vector2.up;
        return false;
    }

    private void TryStart()
    {
        if (!MissionManager.IsRunning
            || Time.timeSinceLevelLoad < StartupDelaySeconds
            || NetworkSceneSingleton<LevelInfo>.i?.LoadedMapSettings == null)
        {
            return;
        }

        mapSettings = NetworkSceneSingleton<LevelInfo>.i.LoadedMapSettings;
        mapKey = BuildMapKey(mapSettings);
        analysisStartedAt = Time.realtimeSinceStartup;
        strategicHeightMap.TryStart(mapSettings);
        state = AnalyzerState.Sampling;
        statusText = "Baking strategic terrain heightmap.";
    }

    private void BeginSampling(MapSettings settings)
    {
        candidates.Clear();
        suggestedSites.Clear();
        regionColumns = Mathf.Max(1, Mathf.CeilToInt(settings.MapSize.x / CandidateRegionSize));
        regionRows = Mathf.Max(1, Mathf.CeilToInt(settings.MapSize.y / CandidateRegionSize));
        regionIndex = 0;
        coverageCandidateIndex = 0;
        analysisStartedAt = Time.realtimeSinceStartup;
        samplingStartedAt = analysisStartedAt;
        state = AnalyzerState.Sampling;
        statusText = $"Extracting terrain features from {regionColumns * regionRows} regions.";
        CommanderPlugin.Log.LogInfo(
            $"SAM regional analysis started: map={mapKey}, regions={regionColumns}x{regionRows}, "
            + $"regionSize={CandidateRegionSize:0}m, sampleSpacing={RegionalSampleSpacing:0}m.");
    }

    private void SampleTerrainBatch()
    {
        if (!strategicHeightMap.IsReady)
        {
            if (mapSettings != null)
            {
                strategicHeightMap.TryStart(mapSettings);
            }
            statusText = "Baking strategic terrain heightmap.";
            return;
        }

        if (regionColumns == 0 && mapSettings != null)
        {
            BeginSampling(mapSettings);
        }

        int regionsPerFrame = Mathf.Clamp(ScanQueriesPerFrame / 16, 1, 16);
        int end = Mathf.Min(regionIndex + regionsPerFrame, regionColumns * regionRows);
        for (; regionIndex < end; regionIndex++)
        {
            ExtractRegionCandidates(regionIndex);
        }

        statusText = $"Extracting terrain regions: {regionIndex}/{regionColumns * regionRows}";
        if (regionIndex < regionColumns * regionRows)
        {
            return;
        }

        if (candidates.Count > CandidateLimit)
        {
            List<SiteCandidate> reduced = new(CandidateLimit);
            float stride = candidates.Count / (float)CandidateLimit;
            for (int i = 0; i < CandidateLimit; i++)
            {
                reduced.Add(candidates[Mathf.Min(
                    Mathf.FloorToInt(i * stride),
                    candidates.Count - 1)]);
            }
            candidates.Clear();
            candidates.AddRange(reduced);
        }
        if (candidates.Count == 0)
        {
            state = AnalyzerState.Failed;
            statusText = "Regional terrain analysis found no viable SAM-site seeds.";
            return;
        }
        coverageCandidateIndex = 0;
        ResetCoverageAccumulator();
        RefreshStrategicAnchors();
        state = AnalyzerState.Coverage;
        statusText = $"Checking strategic LOS for {candidates.Count} terrain candidates.";
        coverageStartedAt = Time.realtimeSinceStartup;
        CommanderPlugin.Log.LogInfo(
            $"SAM regional extraction complete: duration={coverageStartedAt - samplingStartedAt:0.000}s, "
            + $"candidates={candidates.Count}.");
    }

    private void ExtractRegionCandidates(int index)
    {
        if (mapSettings == null)
        {
            return;
        }

        int regionX = index % regionColumns;
        int regionY = index / regionColumns;
        float mapMinX = -mapSettings.MapSize.x * 0.5f;
        float mapMinZ = -mapSettings.MapSize.y * 0.5f;
        float minX = mapMinX + regionX * CandidateRegionSize;
        float minZ = mapMinZ + regionY * CandidateRegionSize;
        float maxX = Mathf.Min(minX + CandidateRegionSize, mapSettings.MapSize.x * 0.5f);
        float maxZ = Mathf.Min(minZ + CandidateRegionSize, mapSettings.MapSize.y * 0.5f);
        List<TerrainSeed> heightSeeds = new(24);

        for (float z = minZ + RegionalSampleSpacing * 0.5f; z < maxZ; z += RegionalSampleSpacing)
        {
            for (float x = minX + RegionalSampleSpacing * 0.5f; x < maxX; x += RegionalSampleSpacing)
            {
                if (!strategicHeightMap.TryGetHeightNearest(x, z, out float height) || height <= 1f)
                {
                    continue;
                }

                float farAverage = SampleAverageHeight(x, z, 1500f);
                float farProminence = height - farAverage;
                float normalY = strategicHeightMap.EstimateNormalY(x, z, 40f);
                float slopeDegrees = Mathf.Acos(Mathf.Clamp(normalY, -1f, 1f)) * Mathf.Rad2Deg;
                GlobalPosition position = new(x, height, z);
                InsertSeed(
                    heightSeeds,
                    new TerrainSeed(position, height, slopeDegrees, farProminence, height),
                    24);
            }
        }

        List<GlobalPosition> selected = new(3);
        AddFirstSeparatedSeed(heightSeeds, selected);
        AddFirstSeparatedSeed(heightSeeds, selected);
        AddFirstSeparatedSeed(heightSeeds, selected);
    }

    private float SampleAverageHeight(float x, float z, float radius)
    {
        float total = 0f;
        int count = 0;
        for (int i = 0; i < 4; i++)
        {
            float angle = i * Mathf.PI * 0.5f;
            if (strategicHeightMap.TryGetHeightNearest(
                x + Mathf.Cos(angle) * radius,
                z + Mathf.Sin(angle) * radius,
                out float height))
            {
                total += height;
                count++;
            }
        }
        return count == 0 ? 0f : total / count;
    }

    // Retained for later terrain-quality experiments; it does not affect current candidates or scores.
    private float CalculateRidgeStrength(float x, float z, float height, float radius)
    {
        float best = float.MinValue;
        for (int i = 0; i < 4; i++)
        {
            float angle = i * Mathf.PI * 0.25f;
            float dx = Mathf.Cos(angle) * radius;
            float dz = Mathf.Sin(angle) * radius;
            if (strategicHeightMap.TryGetHeightNearest(x + dx, z + dz, out float forward)
                && strategicHeightMap.TryGetHeightNearest(x - dx, z - dz, out float backward))
            {
                best = Mathf.Max(best, Mathf.Min(height - forward, height - backward));
            }
        }
        return best == float.MinValue ? 0f : best;
    }

    private void AddFirstSeparatedSeed(List<TerrainSeed> seeds, List<GlobalPosition> regionSelected)
    {
        for (int i = 0; i < seeds.Count; i++)
        {
            TerrainSeed seed = seeds[i];
            bool tooClose = false;
            for (int selectedIndex = 0; selectedIndex < regionSelected.Count; selectedIndex++)
            {
                if (HorizontalSquareDistance(seed.Position, regionSelected[selectedIndex]) < CandidateSeparation * CandidateSeparation)
                {
                    tooClose = true;
                    break;
                }
            }
            if (tooClose)
            {
                continue;
            }
            for (int candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
            {
                if (HorizontalSquareDistance(seed.Position, candidates[candidateIndex].Position) < CandidateSeparation * CandidateSeparation)
                {
                    tooClose = true;
                    break;
                }
            }
            if (tooClose)
            {
                continue;
            }

            regionSelected.Add(seed.Position);
            candidates.Add(new SiteCandidate(
                seed.Position,
                seed.Height,
                seed.SlopeDegrees,
                seed.Prominence,
                0f,
                0f,
                seed.Score));
            return;
        }
    }

    private static void InsertSeed(List<TerrainSeed> seeds, TerrainSeed seed, int limit)
    {
        int index = 0;
        while (index < seeds.Count && seeds[index].Score >= seed.Score)
        {
            index++;
        }
        seeds.Insert(index, seed);
        if (seeds.Count > limit)
        {
            seeds.RemoveAt(seeds.Count - 1);
        }
    }

    private void EvaluateCoverageBatch()
    {
        if (!strategicHeightMap.IsReady)
        {
            if (mapSettings != null)
            {
                strategicHeightMap.TryStart(mapSettings);
            }
            statusText = "Waiting for strategic terrain before refreshing front influence.";
            return;
        }
        if (!EnsureStrategicInfluenceReady())
        {
            statusText = "Waiting for friendly and enemy faction units before evaluating front coverage.";
            return;
        }

        int queryBudget = Mathf.Clamp(ScanQueriesPerFrame * 32, 2048, 16384);
        int queries = 0;
        while (queries < queryBudget && coverageCandidateIndex < candidates.Count)
        {
            if (coverageDirectionIndex >= CoverageDirectionCount)
            {
                SiteCandidate completed = candidates[coverageCandidateIndex];
                completed.Coverage = coverageTotalAreaWeight <= 0f
                    ? 0f
                    : coverageVisibleAreaWeight / coverageTotalAreaWeight;
                completed.StrategicCoverage = coverageTotalFrontWeight <= 0f
                    ? 0f
                    : coverageVisibleFrontWeight / coverageTotalFrontWeight;
                completed.ForwardCoverage = coverageForwardTotalWeight <= 0f
                    ? 0f
                    : coverageForwardVisibleWeight / coverageForwardTotalWeight;
                completed.Risk = CalculateRisk(completed.Position);
                float frontUtility = completed.StrategicCoverage
                    * Mathf.Lerp(0.35f, 1f, completed.Coverage);
                // Terrain metrics remain diagnostic inputs for seed generation, not final ranking.
                completed.Score = (
                    completed.Coverage * 0.35f
                    + frontUtility * 0.65f) * 1000f;
                candidates[coverageCandidateIndex++] = completed;
                ResetCoverageAccumulator();
                continue;
            }

            SiteCandidate candidate = candidates[coverageCandidateIndex];
            if (!coverageEnemyDirectionReady)
            {
                coverageEnemyDirection = FindEnemyDirectionFromAnchors(candidate.Position);
                coverageEnemyDirectionReady = true;
            }
            if (coverageDistance > CoverageRange)
            {
                coverageDirectionIndex++;
                coverageDistance = CoverageSampleSpacing;
                coverageHighestTerrainSlope = float.MinValue;
                continue;
            }

            float angle = coverageDirectionIndex * (Mathf.PI * 2f / CoverageDirectionCount);
            Vector2 sampleDirection = new(Mathf.Cos(angle), Mathf.Sin(angle));
            float x = candidate.Position.x + Mathf.Cos(angle) * coverageDistance;
            float z = candidate.Position.z + Mathf.Sin(angle) * coverageDistance;
            if (strategicHeightMap.TryGetHeightNearest(x, z, out float terrainHeight))
            {
                bool landSample = terrainHeight > Datum.SeaLevel.y + 1f;
                float terrainRelevance = landSample ? 1f : 0.2f;
                float areaWeight = coverageDistance / CoverageRange * terrainRelevance;
                float sourceHeight = candidate.Height + RadarHeight;
                float targetSlope = (terrainHeight + LowAltitudeClearance - sourceHeight) / coverageDistance;
                bool forwardSample = coverageDistance <= ForwardCoverageRange
                    && Vector2.Dot(sampleDirection, coverageEnemyDirection)
                        >= Mathf.Cos(ForwardCoverageHalfAngle * Mathf.Deg2Rad);
                float forwardWeight = coverageDistance / ForwardCoverageRange * terrainRelevance;
                if (forwardSample)
                {
                    coverageForwardTotalWeight += forwardWeight;
                }
                float strategicWeight = CalculateStrategicWeight(new GlobalPosition(x, terrainHeight, z))
                    * CalculateEngagementRangeValue(coverageDistance);
                coverageTotalAreaWeight += areaWeight;
                coverageTotalFrontWeight += areaWeight * strategicWeight;
                if (targetSlope >= coverageHighestTerrainSlope)
                {
                    coverageVisibleAreaWeight += areaWeight;
                    coverageVisibleFrontWeight += areaWeight * strategicWeight;
                    if (forwardSample)
                    {
                        coverageForwardVisibleWeight += forwardWeight;
                    }
                }

                float terrainSlope = (terrainHeight - sourceHeight) / coverageDistance;
                coverageHighestTerrainSlope = Mathf.Max(coverageHighestTerrainSlope, terrainSlope);
            }
            coverageDistance += CoverageSampleSpacing;
            queries++;
        }

        statusText = $"Evaluating 50 km terrain coverage: {Mathf.Min(coverageCandidateIndex + 1, candidates.Count)}/{candidates.Count}";
        if (coverageCandidateIndex >= candidates.Count)
        {
            FinishAnalysis();
        }
    }

    private void ResetCoverageAccumulator()
    {
        coverageDirectionIndex = 0;
        coverageDistance = CoverageSampleSpacing;
        coverageHighestTerrainSlope = float.MinValue;
        coverageVisibleAreaWeight = 0f;
        coverageVisibleFrontWeight = 0f;
        coverageTotalFrontWeight = 0f;
        coverageTotalAreaWeight = 0f;
        coverageForwardVisibleWeight = 0f;
        coverageForwardTotalWeight = 0f;
        coverageEnemyDirection = Vector2.up;
        coverageEnemyDirectionReady = false;
    }

    private static float CalculateEngagementRangeValue(float distance)
    {
        if (distance <= 20000f)
        {
            return 1f;
        }

        return Mathf.Lerp(1f, 0.2f, Mathf.InverseLerp(20000f, CoverageRange, distance));
    }

    private static int CoverageHorizonDistanceSteps => Mathf.CeilToInt(CoverageRange / CoverageHorizonSampleSpacing);
    private static int CoverageHorizonSampleCount => CoverageHorizonDirectionCount * CoverageHorizonDistanceSteps;

    private void BeginCoverageOverlay(SiteCandidate candidate, float? emitterHeight = null)
    {
        ClearCoverageOverlay();
        if (!strategicHeightMap.IsReady)
        {
            return;
        }

        Texture2D texture = new(CoverageOverlayResolution, CoverageOverlayResolution, TextureFormat.RGBA32, false)
        {
            name = "NOCommander_SamCoverage",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };
        CoverageOverlayTexture = texture;
        coverageOverlayPixels = new Color32[CoverageOverlayResolution * CoverageOverlayResolution];
        coverageRequiredAltitudes = new float[coverageOverlayPixels.Length];
        coverageOverlayAlpha = new byte[coverageOverlayPixels.Length];
        for (int i = 0; i < coverageRequiredAltitudes.Length; i++)
        {
            coverageRequiredAltitudes[i] = float.PositiveInfinity;
        }
        coverageHorizonSlopes = new float[CoverageHorizonDirectionCount];
        for (int i = 0; i < coverageHorizonSlopes.Length; i++)
        {
            coverageHorizonSlopes[i] = float.NegativeInfinity;
        }
        coverageHorizonProfile = new float[CoverageHorizonDirectionCount * (CoverageHorizonDistanceSteps + 1)];
        texture.SetPixels32(coverageOverlayPixels);
        texture.Apply(false, false);
        coverageOverlayCandidate = candidate;
        coverageEmitterHeight = emitterHeight ?? candidate.Height + RadarHeight;
        coverageOverlayPixelIndex = 0;
        coverageHorizonSampleIndex = 0;
        coverageOverlayBuilding = true;
    }

    private void UpdateCoverageOverlayBatch()
    {
        if (!CoverageOverlayEnabled
            || !coverageOverlayBuilding
            || CoverageOverlayTexture == null
            || coverageOverlayPixels == null
            || coverageRequiredAltitudes == null
            || coverageOverlayAlpha == null
            || coverageHorizonSlopes == null
            || coverageHorizonProfile == null)
        {
            return;
        }

        int baseBatchSize = Mathf.Clamp(ScanQueriesPerFrame, 64, 512);
        int horizonEnd = Mathf.Min(
            coverageHorizonSampleIndex + baseBatchSize * 24,
            CoverageHorizonSampleCount);
        for (; coverageHorizonSampleIndex < horizonEnd; coverageHorizonSampleIndex++)
        {
            int directionIndex = coverageHorizonSampleIndex / CoverageHorizonDistanceSteps;
            int distanceIndex = coverageHorizonSampleIndex % CoverageHorizonDistanceSteps + 1;
            float distance = distanceIndex * CoverageHorizonSampleSpacing;
            float angle = directionIndex * Mathf.PI * 2f / CoverageHorizonDirectionCount;
            float x = coverageOverlayCandidate.Position.x + Mathf.Cos(angle) * distance;
            float z = coverageOverlayCandidate.Position.z + Mathf.Sin(angle) * distance;
            if (strategicHeightMap.TryGetHeight(x, z, out float terrainHeight))
            {
                float slope = (terrainHeight - coverageEmitterHeight) / distance;
                coverageHorizonSlopes[directionIndex] = Mathf.Max(coverageHorizonSlopes[directionIndex], slope);
            }
            coverageHorizonProfile[
                directionIndex * (CoverageHorizonDistanceSteps + 1) + distanceIndex] =
                coverageHorizonSlopes[directionIndex];
        }

        if (coverageHorizonSampleIndex < CoverageHorizonSampleCount)
        {
            return;
        }

        int resolution = CoverageOverlayTexture.width;
        int end = Mathf.Min(
            coverageOverlayPixelIndex + baseBatchSize * 24,
            coverageOverlayPixels.Length);
        for (; coverageOverlayPixelIndex < end; coverageOverlayPixelIndex++)
        {
            int x = coverageOverlayPixelIndex % resolution;
            int y = coverageOverlayPixelIndex / resolution;
            float z = Mathf.Lerp(
                -strategicHeightMap.MapSize.y * 0.5f,
                strategicHeightMap.MapSize.y * 0.5f,
                y / (float)(resolution - 1));
            float globalX = Mathf.Lerp(
                -strategicHeightMap.MapSize.x * 0.5f,
                strategicHeightMap.MapSize.x * 0.5f,
                x / (float)(resolution - 1));
            if (!strategicHeightMap.TryGetHeight(globalX, z, out float terrainHeight))
            {
                continue;
            }
            GlobalPosition target = new(globalX, terrainHeight + LowAltitudeClearance, z);
            float distance = Mathf.Sqrt(HorizontalSquareDistance(coverageOverlayCandidate.Position, target));
            if (distance > CoverageRange)
            {
                continue;
            }

            float requiredAltitude = 0f;
            if (distance >= CoverageHorizonSampleSpacing)
            {
                float angle = Mathf.Atan2(
                    z - coverageOverlayCandidate.Position.z,
                    globalX - coverageOverlayCandidate.Position.x);
                if (angle < 0f)
                {
                    angle += Mathf.PI * 2f;
                }
                int directionIndex = Mathf.Clamp(
                    Mathf.FloorToInt(angle / (Mathf.PI * 2f) * CoverageHorizonDirectionCount),
                    0,
                    CoverageHorizonDirectionCount - 1);
                int distanceIndex = Mathf.Clamp(
                    Mathf.FloorToInt(distance / CoverageHorizonSampleSpacing),
                    1,
                    CoverageHorizonDistanceSteps);
                float horizonSlope = coverageHorizonProfile[
                    directionIndex * (CoverageHorizonDistanceSteps + 1) + distanceIndex];
                if (!float.IsNegativeInfinity(horizonSlope))
                {
                    requiredAltitude = Mathf.Max(
                        0f,
                        coverageEmitterHeight + horizonSlope * distance - terrainHeight);
                }
            }
            coverageRequiredAltitudes[coverageOverlayPixelIndex] = requiredAltitude;
            float weight = CalculateStrategicWeight(target) * CalculateEngagementRangeValue(distance);
            coverageOverlayAlpha[coverageOverlayPixelIndex] =
                (byte)Mathf.RoundToInt(Mathf.Lerp(38f, 112f, weight));
            coverageOverlayPixels[coverageOverlayPixelIndex] = requiredAltitude <= coverageTargetAltitude
                ? new Color32(34, 174, 230, coverageOverlayAlpha[coverageOverlayPixelIndex])
                : default;
        }

        if (coverageOverlayPixelIndex < coverageOverlayPixels.Length)
        {
            return;
        }

        CoverageOverlayTexture.SetPixels32(coverageOverlayPixels);
        CoverageOverlayTexture.Apply(false, false);
        coverageHorizonSlopes = null;
        coverageHorizonProfile = null;
        coverageOverlayBuilding = false;
    }

    private void RecolorCoverageOverlay()
    {
        if (CoverageOverlayTexture == null
            || coverageOverlayPixels == null
            || coverageRequiredAltitudes == null
            || coverageOverlayAlpha == null)
        {
            return;
        }

        for (int i = 0; i < coverageOverlayPixels.Length; i++)
        {
            coverageOverlayPixels[i] = coverageRequiredAltitudes[i] <= coverageTargetAltitude
                ? new Color32(34, 174, 230, coverageOverlayAlpha[i])
                : default;
        }
        CoverageOverlayTexture.SetPixels32(coverageOverlayPixels);
        CoverageOverlayTexture.Apply(false, false);
    }

    private void ClearCoverageOverlay()
    {
        if (CoverageOverlayTexture != null)
        {
            UnityEngine.Object.Destroy(CoverageOverlayTexture);
            CoverageOverlayTexture = null;
        }
        coverageOverlayPixels = null;
        coverageRequiredAltitudes = null;
        coverageOverlayAlpha = null;
        coverageHorizonSlopes = null;
        coverageHorizonProfile = null;
        coverageOverlayPixelIndex = 0;
        coverageHorizonSampleIndex = 0;
        coverageOverlayBuilding = false;
        coverageOverlaySource = null;
    }

    private float CalculateStrategicWeight(GlobalPosition position)
    {
        if (strategicWeights != null && mapSettings != null)
        {
            return SampleStrategicField(strategicWeights, position);
        }

        return CalculateStrategicWeightExact(position.x, position.z);
    }

    private float CalculateStrategicWeightExact(float x, float z)
    {
        if (friendlyAirbases.Count == 0 || enemyAirbases.Count == 0)
        {
            return 1f;
        }

        GlobalPosition position = new(x, 0f, z);
        float friendlyDistance = NearestDistance(position, friendlyAirbases);
        float enemyDistance = NearestDistance(position, enemyAirbases);
        const float frontWidth = 10000f;
        float depth = Mathf.Abs(enemyDistance - friendlyDistance);
        float front = Mathf.Exp(-(depth * depth) / (2f * frontWidth * frontWidth));
        return 0.02f + front * 0.98f;
    }

    private float CalculateRisk(GlobalPosition position)
    {
        if (strategicRisks != null && mapSettings != null)
        {
            return SampleStrategicField(strategicRisks, position);
        }

        float friendlyDistance = NearestDistance(position, friendlyAirbases);
        float enemyDistance = NearestDistance(position, enemyAirbases);
        if (friendlyDistance == float.MaxValue || enemyDistance == float.MaxValue)
        {
            return 0.5f;
        }

        float friendlyDepth = enemyDistance - friendlyDistance;
        return 1f - Mathf.InverseLerp(-15000f, 30000f, friendlyDepth);
    }

    private bool EnsureStrategicInfluenceReady()
    {
        if (strategicWeights != null && strategicRisks != null)
        {
            return true;
        }
        if (Time.unscaledTime < nextInfluenceRefreshAt)
        {
            return false;
        }
        nextInfluenceRefreshAt = Time.unscaledTime + 1f;
        return RefreshStrategicAnchors();
    }

    private bool RefreshStrategicAnchors()
    {
        friendlyAirbases.Clear();
        enemyAirbases.Clear();
        strategicWeights = null;
        strategicRisks = null;
        FactionHQ? localHq = CommanderGameAccess.GetLocalHq();
        if (localHq == null)
        {
            return false;
        }
        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (hq == null)
            {
                continue;
            }
            FactionMode mode = ReferenceEquals(hq, localHq)
                ? FactionMode.Friendly
                : DynamicMap.GetFactionMode(hq);
            if (mode == FactionMode.NoFaction && !ReferenceEquals(hq, localHq))
            {
                mode = FactionMode.Enemy;
            }
            List<GlobalPosition>? destination = mode == FactionMode.Friendly
                ? friendlyAirbases
                : mode == FactionMode.Enemy ? enemyAirbases : null;
            if (destination == null)
            {
                continue;
            }

            foreach (Airbase airbase in hq!.GetAirbases())
            {
                if (airbase?.center != null)
                {
                    destination.Add(airbase.center.GlobalPosition());
                }
            }
        }
        return BuildStrategicWeightMap(localHq);
    }

    private bool BuildStrategicWeightMap(FactionHQ localHq)
    {
        if (mapSettings == null)
        {
            return false;
        }

        int cellCount = StrategicWeightResolution * StrategicWeightResolution;
        float[] friendlyInfluence = new float[cellCount];
        float[] enemyInfluence = new float[cellCount];
        int friendlyUnits = 0;
        int enemyUnits = 0;

        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (hq == null)
            {
                continue;
            }
            FactionMode mode = ReferenceEquals(hq, localHq)
                ? FactionMode.Friendly
                : DynamicMap.GetFactionMode(hq);
            if (mode == FactionMode.NoFaction && !ReferenceEquals(hq, localHq))
            {
                mode = FactionMode.Enemy;
            }
            float[]? field = mode == FactionMode.Friendly
                ? friendlyInfluence
                : mode == FactionMode.Enemy ? enemyInfluence : null;
            if (field == null)
            {
                continue;
            }

            foreach (PersistentID unitId in hq.factionUnits)
            {
                if (!unitId.TryGetUnit(out Unit unit) || !IsStrategicInfluenceUnit(unit))
                {
                    continue;
                }
                GetUnitInfluence(unit, out float weight, out float radius);
                AddInfluence(field, unit.GlobalPosition(), weight, radius);
                if (mode == FactionMode.Friendly)
                {
                    friendlyUnits++;
                }
                else
                {
                    enemyUnits++;
                }
            }
        }

        for (int i = 0; i < friendlyAirbases.Count; i++)
        {
            AddInfluence(friendlyInfluence, friendlyAirbases[i], 7f, 18000f);
        }
        for (int i = 0; i < enemyAirbases.Count; i++)
        {
            AddInfluence(enemyInfluence, enemyAirbases[i], 7f, 18000f);
        }

        bool hasFriendlySource = friendlyUnits > 0 || friendlyAirbases.Count > 0;
        bool hasEnemySource = enemyUnits > 0 || enemyAirbases.Count > 0;
        if (!hasFriendlySource || !hasEnemySource)
        {
            CommanderPlugin.Log.LogInfo(
                $"SAM influence field waiting: friendlyUnits={friendlyUnits}, enemyUnits={enemyUnits}, "
                + $"friendlyAirbases={friendlyAirbases.Count}, enemyAirbases={enemyAirbases.Count}.");
            return false;
        }

        strategicWeights = new float[cellCount];
        strategicRisks = new float[cellCount];
        for (int i = 0; i < cellCount; i++)
        {
            float friendly = friendlyInfluence[i];
            float enemy = enemyInfluence[i];
            float total = friendly + enemy;
            if (total <= 0.001f)
            {
                strategicWeights[i] = 0f;
                strategicRisks[i] = 0.5f;
                continue;
            }

            float balance = 1f - Mathf.Abs(friendly - enemy) / total;
            float presence = 1f - Mathf.Exp(-total * 0.35f);
            float enemyShare = enemy / total;
            float contestedRelevance = balance * presence;
            float enemyTerritoryRelevance = enemyShare * presence * 0.75f;
            strategicWeights[i] = Mathf.Max(contestedRelevance, enemyTerritoryRelevance);
            strategicRisks[i] = enemyShare;
        }

        CommanderPlugin.Log.LogInfo(
            $"SAM influence field ready: resolution={StrategicWeightResolution}x{StrategicWeightResolution}, "
            + $"friendlyUnits={friendlyUnits}, enemyUnits={enemyUnits}, "
            + $"friendlyAirbases={friendlyAirbases.Count}, enemyAirbases={enemyAirbases.Count}.");
        return true;
    }

    private static bool IsStrategicInfluenceUnit(Unit unit)
    {
        return unit != null
            && !unit.disabled
            && unit.unitState != Unit.UnitState.Destroyed
            && unit.unitState != Unit.UnitState.Returned
            && unit is not Aircraft
            && unit is not Missile
            && unit is not PilotDismounted;
    }

    private static void GetUnitInfluence(Unit unit, out float weight, out float radius)
    {
        if (unit is Ship)
        {
            weight = 1.8f;
            radius = 10000f;
            return;
        }
        if (unit is GroundVehicle)
        {
            weight = 1f;
            radius = 7000f;
            return;
        }

        bool armed = unit.weaponStations != null && unit.weaponStations.Count > 0;
        float captureValue = Mathf.Max(unit.CaptureStrength, unit.CaptureDefense);
        weight = armed ? 0.9f : captureValue > 0f ? 1.2f : 0.35f;
        radius = captureValue > 0f ? 9000f : 5000f;
    }

    private void AddInfluence(float[] field, GlobalPosition position, float weight, float radius)
    {
        if (mapSettings == null || radius <= 0f || weight <= 0f)
        {
            return;
        }

        float cellWidth = mapSettings.MapSize.x / (StrategicWeightResolution - 1);
        float cellHeight = mapSettings.MapSize.y / (StrategicWeightResolution - 1);
        float centerX = (position.x / mapSettings.MapSize.x + 0.5f) * (StrategicWeightResolution - 1);
        float centerY = (position.z / mapSettings.MapSize.y + 0.5f) * (StrategicWeightResolution - 1);
        int radiusX = Mathf.CeilToInt(radius / cellWidth);
        int radiusY = Mathf.CeilToInt(radius / cellHeight);
        int minX = Mathf.Max(0, Mathf.FloorToInt(centerX) - radiusX);
        int maxX = Mathf.Min(StrategicWeightResolution - 1, Mathf.CeilToInt(centerX) + radiusX);
        int minY = Mathf.Max(0, Mathf.FloorToInt(centerY) - radiusY);
        int maxY = Mathf.Min(StrategicWeightResolution - 1, Mathf.CeilToInt(centerY) + radiusY);
        float sigma = radius / 3f;
        float inverseTwoSigmaSquared = 1f / (2f * sigma * sigma);
        for (int y = minY; y <= maxY; y++)
        {
            float worldZ = (y / (float)(StrategicWeightResolution - 1) - 0.5f) * mapSettings.MapSize.y;
            float dz = worldZ - position.z;
            for (int x = minX; x <= maxX; x++)
            {
                float worldX = (x / (float)(StrategicWeightResolution - 1) - 0.5f) * mapSettings.MapSize.x;
                float dx = worldX - position.x;
                float distanceSquared = dx * dx + dz * dz;
                if (distanceSquared > radius * radius)
                {
                    continue;
                }
                field[y * StrategicWeightResolution + x] +=
                    weight * Mathf.Exp(-distanceSquared * inverseTwoSigmaSquared);
            }
        }
    }

    private float SampleStrategicField(float[] field, GlobalPosition position)
    {
        if (mapSettings == null)
        {
            return 0f;
        }
        float u = Mathf.Clamp01(position.x / mapSettings.MapSize.x + 0.5f) * (StrategicWeightResolution - 1);
        float v = Mathf.Clamp01(position.z / mapSettings.MapSize.y + 0.5f) * (StrategicWeightResolution - 1);
        int x0 = Mathf.FloorToInt(u);
        int y0 = Mathf.FloorToInt(v);
        int x1 = Mathf.Min(x0 + 1, StrategicWeightResolution - 1);
        int y1 = Mathf.Min(y0 + 1, StrategicWeightResolution - 1);
        float bottom = Mathf.Lerp(field[y0 * StrategicWeightResolution + x0], field[y0 * StrategicWeightResolution + x1], u - x0);
        float top = Mathf.Lerp(field[y1 * StrategicWeightResolution + x0], field[y1 * StrategicWeightResolution + x1], u - x0);
        return Mathf.Lerp(bottom, top, v - y0);
    }

    private static float NearestDistance(GlobalPosition position, List<GlobalPosition> points)
    {
        float nearestSquared = float.MaxValue;
        for (int i = 0; i < points.Count; i++)
        {
            nearestSquared = Mathf.Min(nearestSquared, HorizontalSquareDistance(position, points[i]));
        }
        return nearestSquared == float.MaxValue ? float.MaxValue : Mathf.Sqrt(nearestSquared);
    }

    private Vector2 FindEnemyDirectionFromAnchors(GlobalPosition position)
    {
        if (enemyAirbases.Count == 0)
        {
            return Vector2.up;
        }

        List<(float distance, GlobalPosition position)> nearest = new(enemyAirbases.Count);
        for (int i = 0; i < enemyAirbases.Count; i++)
        {
            nearest.Add((HorizontalSquareDistance(position, enemyAirbases[i]), enemyAirbases[i]));
        }
        nearest.Sort(static (left, right) => left.distance.CompareTo(right.distance));

        Vector2 average = Vector2.zero;
        int count = Mathf.Min(3, nearest.Count);
        for (int i = 0; i < count; i++)
        {
            average += new Vector2(
                nearest[i].position.x - position.x,
                nearest[i].position.z - position.z).normalized;
        }
        return average.sqrMagnitude > 0.01f ? average.normalized : Vector2.up;
    }

    private void FinishAnalysis()
    {
        candidates.Sort(static (left, right) => right.Score.CompareTo(left.Score));
        AssignCandidateIds();
        state = AnalyzerState.Ready;
        float now = Time.realtimeSinceStartup;
        CommanderPlugin.Log.LogInfo(
            $"SAM LOS evaluation complete: duration={now - coverageStartedAt:0.000}s, "
            + $"candidates={candidates.Count}, directions={CoverageDirectionCount}, "
            + $"range={CoverageRange / 1000f:0}km.");
        RefreshNearbySites(force: true);
    }

    private void RefreshNearbySites(bool force = false)
    {
        if (!force && Time.unscaledTime < nextNearbyRefreshAt)
        {
            return;
        }
        nextNearbyRefreshAt = Time.unscaledTime + 1f;

        CameraStateManager? cameraManager = SceneSingleton<CameraStateManager>.i;
        if (cameraManager == null || candidates.Count == 0)
        {
            return;
        }

        GlobalPosition cameraPosition = cameraManager.transform.position.ToGlobalPosition();
        List<NearbyCandidate> pool = new(candidates.Count);
        for (int i = 0; i < candidates.Count; i++)
        {
            float distanceSquared = HorizontalSquareDistance(cameraPosition, candidates[i].Position);
            if (!PassesActiveFilters(candidates[i])
                || !PassesRangeFilter(Mathf.Sqrt(distanceSquared)))
            {
                continue;
            }
            pool.Add(new NearbyCandidate(
                candidates[i],
                distanceSquared));
        }
        if (candidateListMode == CandidateListMode.Nearby)
        {
            pool.Sort(static (left, right) => left.DistanceSquared.CompareTo(right.DistanceSquared));
        }
        else
        {
            pool.Sort(CompareRankedCandidates);
        }

        suggestedSites.Clear();
        for (int i = 0; i < pool.Count && i < SuggestedSiteCount; i++)
        {
            SiteCandidate candidate = pool[i].Candidate;
            if (candidate.CandidateId == activeCandidateId && ActiveSiteReady)
            {
                candidate = activeSite;
            }
            suggestedSites.Add(candidate);
        }
    }

    private int CompareRankedCandidates(NearbyCandidate left, NearbyCandidate right)
    {
        float leftValue;
        float rightValue;
        bool ascending = false;
        switch (candidateSortMode)
        {
            case CandidateSortMode.AreaLos:
                leftValue = left.Candidate.Coverage;
                rightValue = right.Candidate.Coverage;
                break;
            case CandidateSortMode.FrontEnemy:
                leftValue = left.Candidate.StrategicCoverage;
                rightValue = right.Candidate.StrategicCoverage;
                break;
            case CandidateSortMode.Risk:
                leftValue = left.Candidate.Risk;
                rightValue = right.Candidate.Risk;
                ascending = true;
                break;
            case CandidateSortMode.Forward5Km:
                leftValue = left.Candidate.ForwardCoverage;
                rightValue = right.Candidate.ForwardCoverage;
                break;
            case CandidateSortMode.Height:
                leftValue = left.Candidate.Height;
                rightValue = right.Candidate.Height;
                break;
            default:
                leftValue = left.Candidate.Score;
                rightValue = right.Candidate.Score;
                break;
        }

        int comparison = ascending
            ? leftValue.CompareTo(rightValue)
            : rightValue.CompareTo(leftValue);
        return comparison != 0
            ? comparison
            : left.DistanceSquared.CompareTo(right.DistanceSquared);
    }

    private void AssignCandidateIds()
    {
        for (int i = 0; i < candidates.Count; i++)
        {
            SiteCandidate candidate = candidates[i];
            candidate.CandidateId = i;
            candidates[i] = candidate;
        }
    }

    private int FindSuggestedSiteIndex(int candidateId)
    {
        if (candidateId < 0)
        {
            return -1;
        }
        for (int i = 0; i < suggestedSites.Count; i++)
        {
            if (suggestedSites[i].CandidateId == candidateId)
            {
                return i;
            }
        }
        return -1;
    }

    private float DistanceToMapEdge(GlobalPosition position, Vector3 direction)
    {
        if (mapSettings == null)
        {
            return 0f;
        }

        float halfWidth = mapSettings.MapSize.x * 0.5f;
        float halfHeight = mapSettings.MapSize.y * 0.5f;
        float xDistance = Mathf.Abs(direction.x) < 0.0001f
            ? float.MaxValue
            : (direction.x > 0f ? halfWidth - position.x : -halfWidth - position.x) / direction.x;
        float zDistance = Mathf.Abs(direction.z) < 0.0001f
            ? float.MaxValue
            : (direction.z > 0f ? halfHeight - position.z : -halfHeight - position.z) / direction.z;
        return Mathf.Max(0f, Mathf.Min(xDistance, zDistance));
    }

    private void RefreshFilteredSuggestions()
    {
        if (candidates.Count == 0)
        {
            return;
        }

        RefreshNearbySites(force: true);
    }

    private bool PassesActiveFilters(SiteCandidate candidate)
    {
        if (!PassesPercentFilter(candidate.Coverage, minimumAreaCoverage, areaComparison)
            || !PassesPercentFilter(candidate.StrategicCoverage, minimumFrontShare, frontComparison)
            || !PassesPercentFilter(candidate.Risk, maximumRisk, riskComparison)
            || !PassesPercentFilter(candidate.ForwardCoverage, minimumForwardCoverage, forwardComparison))
        {
            return false;
        }
        if (limitRoadDistance && !IsWithinRoadDistance(candidate.Position, MaxRoadDistance))
        {
            return false;
        }

        return true;
    }

    private bool PassesRangeFilter(float distance)
    {
        if (maximumCandidateRange <= 0f)
        {
            return true;
        }
        return rangeComparison == FilterComparison.Minimum
            ? distance >= maximumCandidateRange
            : distance <= maximumCandidateRange;
    }

    private static bool PassesPercentFilter(
        float value,
        float threshold,
        FilterComparison comparison)
    {
        if (comparison == FilterComparison.Minimum)
        {
            return threshold <= 0f || value >= threshold;
        }
        return threshold >= 0.999f || value <= threshold;
    }

    private static bool IsWithinRoadDistance(GlobalPosition position, float maxDistance)
    {
        LevelInfo? level = NetworkSceneSingleton<LevelInfo>.i;
        return level?.roadNetwork != null
            && level.roadNetwork.TryGetNearestPoint(position, out GlobalPosition nearestRoad, out _)
            && HorizontalSquareDistance(position, nearestRoad) <= maxDistance * maxDistance;
    }

    private static float HorizontalSquareDistance(GlobalPosition left, GlobalPosition right)
    {
        float x = left.x - right.x;
        float z = left.z - right.z;
        return x * x + z * z;
    }

    private void ClearActiveSite()
    {
        activeRefinementGeneration++;
        activeCandidateId = -1;
        siteLayout.Clear();
        localSiteMaps.Clear();
        localRefinementActive = false;
        localHeightMapBaker.Reset();
        if (state == AnalyzerState.Refining)
        {
            state = AnalyzerState.Ready;
        }
        ClearCoverageOverlay();
        CompleteAutomaticSelection(false);
    }

    private void BeginActiveSiteRefinement(SiteCandidate candidate)
    {
        siteLayout.Clear();
        localSiteMaps.Clear();
        localHeightMapBaker.Reset();
        ClearCoverageOverlay();
        activeCandidateId = candidate.CandidateId;
        activeSite = candidate;
        refinementStartedAt = Time.realtimeSinceStartup;
        state = AnalyzerState.Refining;
        localRefinementActive = true;
        int requestedCandidateId = candidate.CandidateId;
        int requestedGeneration = ++activeRefinementGeneration;
        GlobalPosition strategicPeak = FindStrategicPeak(candidate.Position, LocalSnapRadius);
        statusText = "Baking detailed 600 m terrain for the selected site.";
        if (!localHeightMapBaker.TryBake(
            strategicPeak,
            600f,
            localMap => CompleteActiveSiteRefinement(
                requestedCandidateId,
                requestedGeneration,
                localMap)))
        {
            state = AnalyzerState.Ready;
            localRefinementActive = false;
            statusText = "Could not bake detailed terrain for the selected SAM site.";
            CompleteAutomaticSelection(false);
        }
    }

    private GlobalPosition FindStrategicPeak(GlobalPosition center, float radius)
    {
        float spacingX = strategicHeightMap.ResolutionX > 0
            ? strategicHeightMap.MapSize.x / strategicHeightMap.ResolutionX
            : 20f;
        float spacingZ = strategicHeightMap.ResolutionY > 0
            ? strategicHeightMap.MapSize.y / strategicHeightMap.ResolutionY
            : 20f;
        float spacing = Mathf.Max(5f, Mathf.Min(spacingX, spacingZ));
        GlobalPosition best = center;
        float bestHeight = center.y;
        for (float z = -radius; z <= radius; z += spacing)
        {
            for (float x = -radius; x <= radius; x += spacing)
            {
                if (x * x + z * z > radius * radius
                    || !strategicHeightMap.TryGetHeightNearest(
                        center.x + x,
                        center.z + z,
                        out float height)
                    || height <= bestHeight)
                {
                    continue;
                }
                bestHeight = height;
                best = new GlobalPosition(center.x + x, height, center.z + z);
            }
        }
        return best;
    }

    private void CompleteActiveSiteRefinement(
        int requestedCandidateId,
        int requestedGeneration,
        CommanderLocalHeightMapBaker.LocalHeightMap? localMap)
    {
        if (!localRefinementActive
            || requestedCandidateId != activeCandidateId
            || requestedGeneration != activeRefinementGeneration)
        {
            return;
        }
        if (localMap == null)
        {
            state = AnalyzerState.Ready;
            localRefinementActive = false;
            statusText = "Detailed terrain readback for the selected SAM site failed.";
            CompleteAutomaticSelection(false);
            return;
        }

        SiteCandidate site = activeSite;
        List<LocalRadarSeed> radarSeeds = new(24);
        const float spacing = 4f;
        for (float z = -LocalSnapRadius; z <= LocalSnapRadius; z += spacing)
        {
            for (float x = -LocalSnapRadius; x <= LocalSnapRadius; x += spacing)
            {
                float distance = Mathf.Sqrt(x * x + z * z);
                if (distance > LocalSnapRadius)
                {
                    continue;
                }
                float sampleX = localMap.Center.x + x;
                float sampleZ = localMap.Center.z + z;
                if (!localMap.TryGetHeight(sampleX, sampleZ, out float height))
                {
                    continue;
                }
                float normalY = localMap.EstimateNormalY(sampleX, sampleZ, 2f);
                if (normalY < 0.65f)
                {
                    continue;
                }
                float terrainScore = (height - site.Height) * 10f
                    + normalY * 30f
                    - distance * 0.03f;
                InsertLocalRadarSeed(
                    radarSeeds,
                    new LocalRadarSeed(
                        new GlobalPosition(sampleX, height, sampleZ),
                        height,
                        normalY,
                        terrainScore),
                    24);
            }
        }

        LocalRadarSeed best = new(
            site.Position,
            site.Height,
            Mathf.Cos(site.SlopeDegrees * Mathf.Deg2Rad),
            float.MinValue);
        float bestScore = float.MinValue;
        for (int i = 0; i < radarSeeds.Count; i++)
        {
            LocalRadarSeed seed = radarSeeds[i];
            float score = seed.TerrainScore + CalculateLocalRadarOpenness(localMap, seed) * 80f;
            if (score > bestScore)
            {
                best = seed;
                bestScore = score;
            }
        }

        site.Position = best.Position;
        site.Height = best.Height;
        site.SlopeDegrees = Mathf.Acos(Mathf.Clamp(best.NormalY, -1f, 1f)) * Mathf.Rad2Deg;
        site.Risk = CalculateRisk(site.Position);
        activeSite = site;
        localSiteMaps.Add(localMap);
        statusText = "Baking precise 50 m radar terrain around the local peak.";
        if (localHeightMapBaker.TryBake(
            site.Position,
            50f,
            peakMap => CompleteRadarPeakRefinement(
                requestedCandidateId,
                requestedGeneration,
                peakMap)))
        {
            return;
        }

        FinalizeActiveSiteRefinement(site);
    }

    private void CompleteRadarPeakRefinement(
        int requestedCandidateId,
        int requestedGeneration,
        CommanderLocalHeightMapBaker.LocalHeightMap? peakMap)
    {
        if (!localRefinementActive
            || requestedCandidateId != activeCandidateId
            || requestedGeneration != activeRefinementGeneration)
        {
            return;
        }

        SiteCandidate site = activeSite;
        if (peakMap != null)
        {
            float spacing = Mathf.Max(0.25f, peakMap.MetersPerPixel * 2f);
            float half = peakMap.Size * 0.5f - spacing;
            float bestHeight = float.MinValue;
            GlobalPosition bestPosition = site.Position;
            float bestNormalY = Mathf.Cos(site.SlopeDegrees * Mathf.Deg2Rad);
            for (float z = -half; z <= half; z += spacing)
            {
                for (float x = -half; x <= half; x += spacing)
                {
                    float sampleX = peakMap.Center.x + x;
                    float sampleZ = peakMap.Center.z + z;
                    if (!peakMap.TryGetHeight(sampleX, sampleZ, out float height)
                        || height <= bestHeight)
                    {
                        continue;
                    }
                    float normalY = peakMap.EstimateNormalY(sampleX, sampleZ, spacing);
                    if (normalY < 0.55f)
                    {
                        continue;
                    }
                    bestHeight = height;
                    bestPosition = new GlobalPosition(sampleX, height, sampleZ);
                    bestNormalY = normalY;
                }
            }

            if (TryGetTerrainSurface(bestPosition.x, bestPosition.z, out GlobalPosition terrainPosition))
            {
                bestPosition = terrainPosition;
                bestHeight = terrainPosition.y;
            }
            site.Position = bestPosition;
            site.Height = bestHeight;
            site.SlopeDegrees = Mathf.Acos(Mathf.Clamp(bestNormalY, -1f, 1f)) * Mathf.Rad2Deg;
            site.Risk = CalculateRisk(site.Position);
        }

        FinalizeActiveSiteRefinement(site);
    }

    private void FinalizeActiveSiteRefinement(SiteCandidate site)
    {
        activeSite = site;
        BuildSiteLayouts();
        localSiteMaps.Clear();
        localRefinementActive = false;
        state = AnalyzerState.Ready;
        if (CoverageOverlayEnabled)
        {
            BeginCoverageOverlay(activeSite);
        }
        CommanderTacticalMapService.Instance?.JumpCameraToPosition(activeSite.Position);
        statusText = "Selected SAM site refined and ready.";
        CommanderPlugin.Log.LogInfo(
            $"SAM selected-site refinement complete: duration={Time.realtimeSinceStartup - refinementStartedAt:0.000}s, "
            + $"candidate={activeCandidateId}, layoutWindow=600m, radarWindow=50m, resolution=1024.");
        CompleteAutomaticSelection(true);
    }

    private void CompleteAutomaticSelection(bool success)
    {
        Action<bool>? completed = automaticSelectionCompleted;
        automaticSelectionCompleted = null;
        completed?.Invoke(success);
    }

    private static bool TryGetTerrainSurface(float globalX, float globalZ, out GlobalPosition position)
    {
        position = default;
        if (GameAssets.i == null)
        {
            return false;
        }

        Vector3 local = new GlobalPosition(globalX, 0f, globalZ).ToLocalPosition();
        Vector3 origin = new(local.x, Datum.LocalSeaY + 10000f, local.z);
        RaycastHit[] hits = Physics.RaycastAll(
            origin,
            Vector3.down,
            20000f,
            PhysicsLayers.StaticsMask,
            QueryTriggerInteraction.Ignore);
        float highestTerrainY = float.MinValue;
        Vector3 terrainPoint = default;
        for (int i = 0; i < hits.Length; i++)
        {
            RaycastHit hit = hits[i];
            if (hit.collider != null
                && hit.collider.sharedMaterial == GameAssets.i.terrainMaterial
                && hit.point.y > highestTerrainY)
            {
                highestTerrainY = hit.point.y;
                terrainPoint = hit.point;
            }
        }
        if (highestTerrainY <= float.MinValue)
        {
            return false;
        }

        position = terrainPoint.ToGlobalPosition();
        return true;
    }

    private static void InsertLocalRadarSeed(
        List<LocalRadarSeed> seeds,
        LocalRadarSeed seed,
        int limit)
    {
        int index = 0;
        while (index < seeds.Count && seeds[index].TerrainScore >= seed.TerrainScore)
        {
            index++;
        }
        seeds.Insert(index, seed);
        if (seeds.Count > limit)
        {
            seeds.RemoveAt(seeds.Count - 1);
        }
    }

    private static float CalculateLocalRadarOpenness(
        CommanderLocalHeightMapBaker.LocalHeightMap map,
        LocalRadarSeed seed)
    {
        const int directionCount = 12;
        const float step = 25f;
        const float range = 250f;
        int visible = 0;
        int samples = 0;
        float sourceHeight = seed.Height + RadarHeight;
        for (int directionIndex = 0; directionIndex < directionCount; directionIndex++)
        {
            float angle = directionIndex * (Mathf.PI * 2f / directionCount);
            float highestTerrainSlope = float.MinValue;
            for (float distance = step; distance <= range; distance += step)
            {
                float x = seed.Position.x + Mathf.Cos(angle) * distance;
                float z = seed.Position.z + Mathf.Sin(angle) * distance;
                if (!map.TryGetHeight(x, z, out float terrainHeight))
                {
                    continue;
                }

                float targetSlope = (terrainHeight + RadarHeight - sourceHeight) / distance;
                if (targetSlope >= highestTerrainSlope)
                {
                    visible++;
                }
                highestTerrainSlope = Mathf.Max(
                    highestTerrainSlope,
                    (terrainHeight - sourceHeight) / distance);
                samples++;
            }
        }
        return samples == 0 ? 0f : visible / (float)samples;
    }

    private void BuildSiteLayouts()
    {
        siteLayout.Clear();
        if (!HasActiveSite)
        {
            return;
        }

            int siteIndex = activeCandidateId;
            SiteCandidate site = activeSite;
            List<GlobalPosition> occupied = new() { site.Position };
            siteLayout.Add(new SiteLayoutMarker(siteIndex, SiteUnitRole.Radar, site.Position));

            Vector2 enemyDirection = FindEnemyFrontDirection(site.Position);
            Vector2 rearDirection = -enemyDirection;
            int seed = Mathf.Abs(
                Mathf.RoundToInt(site.Position.x * 0.17f + site.Position.z * 0.31f));
            GlobalPosition platform = FindLayoutPosition(
                site,
                Rotate(rearDirection, -55f * Mathf.Deg2Rad),
                240f,
                35f,
                minimumNormalY: 0.78f,
                forwardVisibilityPreference: -1,
                enemyDirection,
                occupied);
            occupied.Add(platform);
            siteLayout.Add(new SiteLayoutMarker(siteIndex, SiteUnitRole.Platform, platform));
            float towerAngle = ((seed * 37) % 6001 / 100f - 30f) * Mathf.Deg2Rad;
            Vector2 towerOffset = Rotate(enemyDirection, towerAngle) * 40f;
            GlobalPosition controlTowerGround = SnapLayoutPointToTerrain(
                platform.x + towerOffset.x,
                platform.z + towerOffset.y,
                platform);
            occupied.Add(controlTowerGround);
            siteLayout.Add(new SiteLayoutMarker(
                siteIndex,
                SiteUnitRole.ControlTower,
                new GlobalPosition(
                    controlTowerGround.x,
                    controlTowerGround.y - 20f,
                    controlTowerGround.z)));

            int gunCount = 2 + seed % 2;
            int irmCount = 2 + seed / 2 % 3;
            for (int gunIndex = 0; gunIndex < gunCount; gunIndex++)
            {
                float arc = gunIndex == 0
                    ? 0f
                    : gunCount == 2
                        ? 0f
                        : gunIndex == 1 ? -55f : 55f;
                float radius = gunIndex == 0 ? 55f : 145f;
                GlobalPosition gun = FindLayoutPosition(
                    site,
                    Rotate(enemyDirection, arc * Mathf.Deg2Rad),
                    radius,
                    gunIndex == 0 ? 45f : 20f,
                    minimumNormalY: 0.88f,
                    forwardVisibilityPreference: 1,
                    enemyDirection,
                    occupied);
                occupied.Add(gun);
                siteLayout.Add(new SiteLayoutMarker(siteIndex, SiteUnitRole.Gun23mm, gun));
            }

            for (int irmIndex = 0; irmIndex < irmCount; irmIndex++)
            {
                float spread = irmCount <= 2
                    ? (irmIndex == 0 ? -32f : 32f)
                    : Mathf.Lerp(-65f, 65f, irmIndex / (float)(irmCount - 1));
                GlobalPosition irm = FindLayoutPosition(
                    site,
                    Rotate(enemyDirection, spread * Mathf.Deg2Rad),
                    105f + irmIndex % 2 * 35f,
                    24f,
                    minimumNormalY: 0.88f,
                    forwardVisibilityPreference: 1,
                    enemyDirection,
                    occupied);
                occupied.Add(irm);
                siteLayout.Add(new SiteLayoutMarker(siteIndex, SiteUnitRole.Irm, irm));
            }

            GlobalPosition batteryAnchor = FindBatteryAnchor(
                site,
                rearDirection,
                enemyDirection,
                platform,
                occupied);

            const int launcherCount = 3;
            for (int launcherIndex = 0; launcherIndex < launcherCount; launcherIndex++)
            {
                float angle = launcherIndex * (Mathf.PI * 2f / launcherCount);
                GlobalPosition launcher = FindLayoutPositionAroundAnchor(
                    site,
                    batteryAnchor,
                    new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)),
                    58f,
                    18f,
                    minimumNormalY: 0.9f,
                    forwardVisibilityPreference: -1,
                    enemyDirection,
                    occupied,
                    keepAwayFrom: platform,
                    keepAwayDistance: 100f);
                occupied.Add(launcher);
                siteLayout.Add(new SiteLayoutMarker(siteIndex, SiteUnitRole.StratoLauncher, launcher));
            }

            Vector2 batteryCross = new(-rearDirection.y, rearDirection.x);
            GlobalPosition ammo = FindLayoutPositionAroundAnchor(
                site,
                batteryAnchor,
                batteryCross,
                28f,
                20f,
                minimumNormalY: 0.94f,
                forwardVisibilityPreference: -1,
                enemyDirection,
                occupied);
            occupied.Add(ammo);
            siteLayout.Add(new SiteLayoutMarker(siteIndex, SiteUnitRole.Ammo, ammo));

            GlobalPosition fireControl = FindLayoutPositionAroundAnchor(
                site,
                batteryAnchor,
                -batteryCross,
                28f,
                20f,
                minimumNormalY: 0.94f,
                forwardVisibilityPreference: -1,
                enemyDirection,
                occupied);
            siteLayout.Add(new SiteLayoutMarker(
                siteIndex,
                SiteUnitRole.FireControl,
                fireControl));
    }

    private GlobalPosition FindLayoutPosition(
        SiteCandidate site,
        Vector2 preferredDirection,
        float preferredRadius,
        float angleSpreadDegrees,
        float minimumNormalY,
        int forwardVisibilityPreference,
        Vector2 enemyDirection,
        List<GlobalPosition> occupied)
    {
        GlobalPosition best = default;
        float bestScore = float.MinValue;
        float[] radiusOffsets = { -35f, 0f, 35f };
        float[] angleOffsets = { -angleSpreadDegrees, 0f, angleSpreadDegrees };
        for (int radiusIndex = 0; radiusIndex < radiusOffsets.Length; radiusIndex++)
        {
            float radius = Mathf.Clamp(
                preferredRadius + radiusOffsets[radiusIndex],
                40f,
                SiteControlRadius - 15f);
            for (int angleIndex = 0; angleIndex < angleOffsets.Length; angleIndex++)
            {
                Vector2 direction = Rotate(
                    preferredDirection,
                    angleOffsets[angleIndex] * Mathf.Deg2Rad);
                if (!TrySampleLayoutGround(
                    site.Position,
                    direction * radius,
                    out GlobalPosition position,
                    out float normalY))
                {
                    continue;
                }

                bool blockedByLayout = false;
                for (int occupiedIndex = 0; occupiedIndex < occupied.Count; occupiedIndex++)
                {
                    if (HorizontalSquareDistance(position, occupied[occupiedIndex]) < 35f * 35f)
                    {
                        blockedByLayout = true;
                        break;
                    }
                }
                if (blockedByLayout)
                {
                    continue;
                }

                float visibleDistance = forwardVisibilityPreference == 0
                    ? 0f
                    : GetForwardVisibility(position, enemyDirection, 12000f);
                float score =
                    normalY * 600f
                    + visibleDistance * 0.03f * forwardVisibilityPreference
                    - Mathf.Abs(radius - preferredRadius) * 0.5f;
                if (normalY < minimumNormalY)
                {
                    score -= (minimumNormalY - normalY) * 2000f;
                }

                if (score > bestScore)
                {
                    best = position;
                    bestScore = score;
                }
            }
        }

        if (bestScore > float.MinValue)
        {
            return best;
        }

        Vector2 fallback = preferredDirection.normalized * preferredRadius;
        return SnapLayoutPointToTerrain(
            site.Position.x + fallback.x,
            site.Position.z + fallback.y,
            site.Position);
    }

    private GlobalPosition FindBatteryAnchor(
        SiteCandidate site,
        Vector2 rearDirection,
        Vector2 enemyDirection,
        GlobalPosition platform,
        List<GlobalPosition> occupied)
    {
        GlobalPosition best = default;
        float bestScore = float.MinValue;
        float bestHeightRange = float.MaxValue;
        float bestMinimumNormalY = 0f;
        float[] radii = { 105f, 125f, 145f, 165f, 185f, 205f, 225f };
        for (int radiusIndex = 0; radiusIndex < radii.Length; radiusIndex++)
        {
            for (float angle = -110f; angle <= 110f; angle += 10f)
            {
                Vector2 direction = Rotate(rearDirection, angle * Mathf.Deg2Rad);
                if (!TrySampleLayoutGround(
                    site.Position,
                    direction * radii[radiusIndex],
                    out GlobalPosition position,
                    out float normalY)
                    || normalY < 0.75f
                    || HorizontalSquareDistance(position, platform) < 110f * 110f)
                {
                    continue;
                }

                bool blockedByLayout = false;
                for (int occupiedIndex = 0; occupiedIndex < occupied.Count; occupiedIndex++)
                {
                    if (HorizontalSquareDistance(position, occupied[occupiedIndex]) < 55f * 55f)
                    {
                        blockedByLayout = true;
                        break;
                    }
                }
                if (blockedByLayout)
                {
                    continue;
                }

                if (!TryEvaluateBatteryFootprint(
                    position,
                    72f,
                    out float heightRange,
                    out float minimumNormalY,
                    out float averageNormalY))
                {
                    continue;
                }

                float forwardVisibility = GetForwardVisibility(position, enemyDirection, 12000f);
                float rearPreference = Mathf.Max(0f, Vector2.Dot(direction.normalized, rearDirection));
                float score =
                    - heightRange * 1800f
                    + minimumNormalY * 1200f
                    + averageNormalY * 800f
                    + rearPreference * 350f
                    - forwardVisibility * 0.015f
                    - Mathf.Abs(radii[radiusIndex] - 175f) * 0.5f;
                if (score > bestScore)
                {
                    best = position;
                    bestScore = score;
                    bestHeightRange = heightRange;
                    bestMinimumNormalY = minimumNormalY;
                }
            }
        }

        if (bestScore > float.MinValue)
        {
            CommanderPlugin.Log.LogInfo(
                $"SAM battery terrain selected: heightRange={bestHeightRange:0.00}m, "
                + $"minimumNormalY={bestMinimumNormalY:0.000}, distance={CommanderGameAccess.HorizontalDistance(site.Position.ToLocalPosition(), best.ToLocalPosition()):0}m.");
            return best;
        }

        return FindLayoutPosition(
            site,
            rearDirection,
            190f,
            70f,
            minimumNormalY: 0.9f,
            forwardVisibilityPreference: -1,
            enemyDirection,
            occupied);
    }

    private bool TryEvaluateBatteryFootprint(
        GlobalPosition center,
        float radius,
        out float heightRange,
        out float minimumNormalY,
        out float averageNormalY)
    {
        const float spacing = 12f;
        float minimumHeight = float.MaxValue;
        float maximumHeight = float.MinValue;
        float normalTotal = 0f;
        int heightSamples = 0;
        int normalSamples = 0;
        minimumNormalY = 1f;

        for (float z = -radius; z <= radius; z += spacing)
        {
            for (float x = -radius; x <= radius; x += spacing)
            {
                if (x * x + z * z > radius * radius
                    || !TryGetDetailedHeight(center.x + x, center.z + z, out float height)
                    || height <= 1f)
                {
                    continue;
                }

                minimumHeight = Mathf.Min(minimumHeight, height);
                maximumHeight = Mathf.Max(maximumHeight, height);
                heightSamples++;

                if (((Mathf.RoundToInt(x / spacing) + Mathf.RoundToInt(z / spacing)) & 1) != 0)
                {
                    continue;
                }

                float normalY = EstimateDetailedNormalY(center.x + x, center.z + z);
                minimumNormalY = Mathf.Min(minimumNormalY, normalY);
                normalTotal += normalY;
                normalSamples++;
            }
        }

        if (heightSamples < 80 || normalSamples == 0)
        {
            heightRange = float.MaxValue;
            minimumNormalY = 0f;
            averageNormalY = 0f;
            return false;
        }

        heightRange = maximumHeight - minimumHeight;
        averageNormalY = normalTotal / normalSamples;
        return true;
    }

    private GlobalPosition FindLayoutPositionAroundAnchor(
        SiteCandidate site,
        GlobalPosition anchor,
        Vector2 preferredDirection,
        float preferredRadius,
        float angleSpreadDegrees,
        float minimumNormalY,
        int forwardVisibilityPreference,
        Vector2 enemyDirection,
        List<GlobalPosition> occupied,
        GlobalPosition? keepAwayFrom = null,
        float keepAwayDistance = 0f)
    {
        GlobalPosition best = default;
        float bestScore = float.MinValue;
        float[] radiusOffsets = { -12f, 0f, 12f };
        float[] angleOffsets = { -angleSpreadDegrees, 0f, angleSpreadDegrees };
        for (int radiusIndex = 0; radiusIndex < radiusOffsets.Length; radiusIndex++)
        {
            float radius = Mathf.Max(12f, preferredRadius + radiusOffsets[radiusIndex]);
            for (int angleIndex = 0; angleIndex < angleOffsets.Length; angleIndex++)
            {
                Vector2 direction = Rotate(
                    preferredDirection,
                    angleOffsets[angleIndex] * Mathf.Deg2Rad);
                if (!TrySampleLayoutGround(
                    anchor,
                    direction * radius,
                    out GlobalPosition position,
                    out float normalY)
                    || HorizontalSquareDistance(position, site.Position)
                    > (SiteControlRadius - 10f) * (SiteControlRadius - 10f))
                {
                    continue;
                }
                if (keepAwayFrom.HasValue
                    && HorizontalSquareDistance(position, keepAwayFrom.Value)
                        < keepAwayDistance * keepAwayDistance)
                {
                    continue;
                }

                bool blockedByLayout = false;
                for (int occupiedIndex = 0; occupiedIndex < occupied.Count; occupiedIndex++)
                {
                    if (HorizontalSquareDistance(position, occupied[occupiedIndex]) < 24f * 24f)
                    {
                        blockedByLayout = true;
                        break;
                    }
                }
                if (blockedByLayout)
                {
                    continue;
                }

                float visibleDistance = forwardVisibilityPreference == 0
                    ? 0f
                    : GetForwardVisibility(position, enemyDirection, 12000f);
                float score =
                    normalY * 700f
                    + visibleDistance * 0.03f * forwardVisibilityPreference;
                if (normalY < minimumNormalY)
                {
                    score -= (minimumNormalY - normalY) * 2500f;
                }

                if (score > bestScore)
                {
                    best = position;
                    bestScore = score;
                }
            }
        }

        if (bestScore > float.MinValue)
        {
            return best;
        }

        Vector2 fallback = preferredDirection.normalized * preferredRadius;
        return SnapLayoutPointToTerrain(
            anchor.x + fallback.x,
            anchor.z + fallback.y,
            anchor);
    }

    private bool TrySampleLayoutGround(
        GlobalPosition center,
        Vector2 offset,
        out GlobalPosition position,
        out float normalY)
    {
        GlobalPosition requestedPosition = new(
            center.x + offset.x,
            center.y,
            center.z + offset.y);
        if (TryGetDetailedHeight(requestedPosition.x, requestedPosition.z, out float height)
            && height > 1f)
        {
            position = new GlobalPosition(requestedPosition.x, height, requestedPosition.z);
            normalY = EstimateDetailedNormalY(requestedPosition.x, requestedPosition.z);
            return true;
        }

        position = default;
        normalY = 0f;
        return false;
    }

    private float GetForwardVisibility(
        GlobalPosition position,
        Vector2 direction,
        float maximumDistance)
    {
        float sourceHeight = position.y + 3f;
        for (float distance = 20f; distance <= maximumDistance; distance += 20f)
        {
            float x = position.x + direction.x * distance;
            float z = position.z + direction.y * distance;
            if (!TryGetDetailedHeight(x, z, out float height))
            {
                return distance;
            }
            if (height > sourceHeight)
            {
                return distance;
            }
        }
        return maximumDistance;
    }

    private static Vector2 FindEnemyFrontDirection(GlobalPosition position)
    {
        List<(float distance, GlobalPosition position)> enemyAirbases = new();
        foreach (FactionHQ hq in FactionRegistry.GetAllHQs())
        {
            if (hq == null || DynamicMap.GetFactionMode(hq) != FactionMode.Enemy)
            {
                continue;
            }

            foreach (Airbase airbase in hq.GetAirbases())
            {
                if (airbase?.center == null)
                {
                    continue;
                }

                GlobalPosition airbasePosition = airbase.center.GlobalPosition();
                enemyAirbases.Add((
                    HorizontalSquareDistance(position, airbasePosition),
                    airbasePosition));
            }
        }

        enemyAirbases.Sort(static (left, right) => left.distance.CompareTo(right.distance));
        int count = Mathf.Min(3, enemyAirbases.Count);
        if (count == 0)
        {
            return Vector2.up;
        }

        Vector2 averageDirection = Vector2.zero;
        for (int i = 0; i < count; i++)
        {
            GlobalPosition airbasePosition = enemyAirbases[i].position;
            averageDirection += new Vector2(
                airbasePosition.x - position.x,
                airbasePosition.z - position.z).normalized;
        }

        return averageDirection.sqrMagnitude > 0.01f
            ? averageDirection.normalized
            : Vector2.up;
    }

    private GlobalPosition SnapLayoutPointToTerrain(
        float globalX,
        float globalZ,
        GlobalPosition fallback)
    {
        if (TryGetDetailedHeight(globalX, globalZ, out float height))
        {
            return new GlobalPosition(globalX, height, globalZ);
        }

        return fallback;
    }

    private bool TryGetDetailedHeight(float x, float z, out float height)
    {
        for (int i = 0; i < localSiteMaps.Count; i++)
        {
            if (localSiteMaps[i].TryGetHeight(x, z, out height))
            {
                return true;
            }
        }
        return strategicHeightMap.TryGetHeight(x, z, out height);
    }

    private float EstimateDetailedNormalY(float x, float z)
    {
        for (int i = 0; i < localSiteMaps.Count; i++)
        {
            if (localSiteMaps[i].Contains(x, z))
            {
                return localSiteMaps[i].EstimateNormalY(x, z, 2f);
            }
        }
        return strategicHeightMap.EstimateNormalY(x, z, 20f);
    }

    private static Vector2 Rotate(Vector2 value, float angle)
    {
        float sin = Mathf.Sin(angle);
        float cos = Mathf.Cos(angle);
        return new Vector2(
            value.x * cos - value.y * sin,
            value.x * sin + value.y * cos);
    }

    private static string BuildMapKey(MapSettings settings)
    {
        int prefix = settings.NetworkMap != null ? settings.NetworkMap.MapPrefix : 0;
        return string.Join(
            "|",
            Application.version,
            settings.name,
            prefix,
            settings.MapSize.x.ToString("R"),
            settings.MapSize.y.ToString("R"),
            settings.GridSizeX,
            settings.GridSizeY,
            settings.OffsetX,
            settings.OffsetY,
            RegionalSampleSpacing.ToString("R"),
            CandidateRegionSize.ToString("R"));
    }

    private void ResetAnalysisData()
    {
        state = AnalyzerState.Waiting;
        candidates.Clear();
        suggestedSites.Clear();
        siteLayout.Clear();
        strategicWeights = null;
        strategicRisks = null;
        regionColumns = 0;
        regionRows = 0;
        regionIndex = 0;
        coverageCandidateIndex = 0;
        ResetCoverageAccumulator();
        activeCandidateId = -1;
        activeRefinementGeneration++;
        nextNearbyRefreshAt = 0f;
        nextInfluenceRefreshAt = 0f;
        localHeightMapBaker.Reset();
        localSiteMaps.Clear();
        localRefinementActive = false;
        ClearCoverageOverlay();
    }

    internal enum AnalyzerState
    {
        Waiting,
        Sampling,
        Coverage,
        Refining,
        Ready,
        Failed
    }

    internal enum CandidateListMode
    {
        Nearby,
        Ranked
    }

    internal enum CandidateSortMode
    {
        Rating,
        AreaLos,
        FrontEnemy,
        Risk,
        Forward5Km,
        Height
    }

    internal enum FilterComparison
    {
        Minimum,
        Maximum
    }

    private readonly struct TerrainSeed
    {
        internal TerrainSeed(
            GlobalPosition position,
            float height,
            float slopeDegrees,
            float prominence,
            float score)
        {
            Position = position;
            Height = height;
            SlopeDegrees = slopeDegrees;
            Prominence = prominence;
            Score = score;
        }

        internal GlobalPosition Position { get; }
        internal float Height { get; }
        internal float SlopeDegrees { get; }
        internal float Prominence { get; }
        internal float Score { get; }
    }

    private readonly struct LocalRadarSeed
    {
        internal LocalRadarSeed(
            GlobalPosition position,
            float height,
            float normalY,
            float terrainScore)
        {
            Position = position;
            Height = height;
            NormalY = normalY;
            TerrainScore = terrainScore;
        }

        internal GlobalPosition Position { get; }
        internal float Height { get; }
        internal float NormalY { get; }
        internal float TerrainScore { get; }
    }

    internal struct SiteCandidate
    {
        internal SiteCandidate(
            GlobalPosition position,
            float height,
            float slopeDegrees,
            float prominence,
            float coverage,
            float risk,
            float score)
        {
            CandidateId = -1;
            Position = position;
            Height = height;
            SlopeDegrees = slopeDegrees;
            Prominence = prominence;
            Coverage = coverage;
            StrategicCoverage = 0f;
            ForwardCoverage = 0f;
            Risk = risk;
            BaseScore = score - coverage * 1000f;
            Score = score;
        }

        internal int CandidateId;
        internal GlobalPosition Position;
        internal float Height;
        internal float SlopeDegrees;
        internal float Prominence;
        internal float Coverage;
        internal float StrategicCoverage;
        internal float ForwardCoverage;
        internal float Risk;
        internal float BaseScore;
        internal float Score;
    }

    internal readonly struct SiteLayoutMarker
    {
        internal SiteLayoutMarker(int siteIndex, SiteUnitRole role, GlobalPosition position)
        {
            SiteIndex = siteIndex;
            Role = role;
            Position = position;
        }

        internal int SiteIndex { get; }
        internal SiteUnitRole Role { get; }
        internal GlobalPosition Position { get; }
    }

    internal enum SiteUnitRole
    {
        Radar,
        Platform,
        ControlTower,
        Gun23mm,
        Irm,
        StratoLauncher,
        Ammo,
        FireControl
    }

    private readonly struct NearbyCandidate
    {
        internal NearbyCandidate(SiteCandidate candidate, float distanceSquared)
        {
            Candidate = candidate;
            DistanceSquared = distanceSquared;
        }

        internal SiteCandidate Candidate { get; }
        internal float DistanceSquared { get; }
    }
}
