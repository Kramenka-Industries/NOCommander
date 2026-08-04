namespace NuclearOptionCommander;

internal sealed class CommanderPersistentOperations
{
    private readonly CommanderSpawnService spawnService;
    private readonly CommanderSupplyHeliService supplyHeliService;
    private readonly CommanderAirCommandService airCommandService;
    private readonly CommanderMobileEmplacementService mobileEmplacementService;
    private readonly CommanderSamSiteAnalyzerService samSiteAnalyzerService;
    private readonly CommanderSamSiteService samSiteService;

    internal CommanderPersistentOperations(
        CommanderSpawnService spawnService,
        CommanderSupplyHeliService supplyHeliService,
        CommanderAirCommandService airCommandService,
        CommanderMobileEmplacementService mobileEmplacementService,
        CommanderSamSiteAnalyzerService samSiteAnalyzerService,
        CommanderSamSiteService samSiteService)
    {
        this.spawnService = spawnService;
        this.supplyHeliService = supplyHeliService;
        this.airCommandService = airCommandService;
        this.mobileEmplacementService = mobileEmplacementService;
        this.samSiteAnalyzerService = samSiteAnalyzerService;
        this.samSiteService = samSiteService;
    }

    internal void Tick()
    {
        spawnService.TickPersistent();
        supplyHeliService.TickPersistent();
        airCommandService.TickPersistent();
        mobileEmplacementService.TickPersistent();
        samSiteAnalyzerService.TickPersistent();
        samSiteService.TickPersistent();
    }
}
